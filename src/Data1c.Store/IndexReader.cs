using System.Globalization;
using System.Text;
using Data1c.Core.Graph;
using Microsoft.Data.Sqlite;

namespace Data1c.Store;

/// <summary>Запросы к индексу: поиск, карточка узла, соседи, вызовы, обращения к метаданным.</summary>
/// <remarks>
/// Все запросы ограничены лимитами: индекс отвечает модели-агенту, и многотысячные ответы ей вредны.
/// Многошаговые обходы выполняются рекурсивным SQL, без материализации графа в памяти.
/// </remarks>
public sealed class IndexReader
{
    private readonly SqliteIndex _index;

    public IndexReader(SqliteIndex index)
    {
        ArgumentNullException.ThrowIfNull(index);
        _index = index;
    }

    /// <summary>Сколько узлов каждого вида: нужно для статистики графа.</summary>
    public IReadOnlyDictionary<string, int> CountNodesByKind() => CountByKind("nodes");

    /// <summary>Сколько связей каждого вида.</summary>
    public IReadOnlyDictionary<string, int> CountEdgesByKind() => CountByKind("edges");

    private IReadOnlyDictionary<string, int> CountByKind(string table) => _index.WithLock(() =>
    {
        using var command = _index.CreateCommand($"SELECT kind, COUNT(*) FROM {table} GROUP BY kind");
        using var reader = command.ExecuteReader();
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        while (reader.Read())
        {
            result[reader.GetString(0)] = reader.GetInt32(1);
        }

        return (IReadOnlyDictionary<string, int>)result;
    });

    /// <summary>Синонимы объектов метаданных по идентификаторам: нужны выдаче поиска.</summary>
    public IReadOnlyDictionary<string, string?> GetSynonyms(IReadOnlyList<string> ids)
    {
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (ids.Count == 0)
        {
            return result;
        }

        return _index.WithLock(() =>
        {
            foreach (var chunk in Chunk([.. ids], 400))
            {
                using var command = _index.CreateCommand(
                    $"SELECT id, synonym FROM metadata_objects WHERE id IN ({Placeholders(chunk.Count, "@s")})");
                AddIdParameters(command, chunk, "@s");
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    result[reader.GetString(0)] = reader.IsDBNull(1) ? null : reader.GetString(1);
                }
            }

            return (IReadOnlyDictionary<string, string?>)result;
        });
    }

    /// <summary>
    /// Поиск по вложенным объектам: реквизиты, табличные части, формы, команды, макеты.
    /// В графе их нет — они лежат в таблице состава объектов.
    /// </summary>
    public IReadOnlyList<MetadataItemRow> SearchMetadataItems(string query, int limit = 20, IReadOnlyCollection<string>? kinds = null)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        var lower = query.Trim().ToLowerInvariant();
        return _index.WithLock(() =>
        {
            var sql = new StringBuilder("SELECT object_id, kind, name, type_info, parent_id FROM metadata_items WHERE ");
            if (kinds is { Count: > 0 })
            {
                sql.Append("kind IN (").Append(Placeholders(kinds.Count, "@k")).Append(") AND ");
            }

            sql.Append(@" (name_lower = @lower OR name_lower LIKE @prefix ESCAPE '\' OR name_lower LIKE @like ESCAPE '\')")
                .Append(@" ORDER BY CASE WHEN name_lower = @lower THEN 0 WHEN name_lower LIKE @prefix ESCAPE '\' THEN 1 ELSE 2 END, length(name), name LIMIT @limit");

            using var command = _index.CreateCommand(sql.ToString());
            AddKindParameters(command, kinds, "@k");
            command.Parameters.AddWithValue("@lower", lower);
            command.Parameters.AddWithValue("@prefix", EscapeLike(lower) + "%");
            command.Parameters.AddWithValue("@like", "%" + EscapeLike(lower) + "%");
            command.Parameters.AddWithValue("@limit", Math.Clamp(limit, 1, 200));

            using var reader = command.ExecuteReader();
            var result = new List<MetadataItemRow>();
            while (reader.Read())
            {
                result.Add(new MetadataItemRow(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4)));
            }

            return (IReadOnlyList<MetadataItemRow>)result;
        });
    }

    /// <summary>Сколько связей ведёт в отсутствующие узлы: то же, что «неразрешённые» в графе.</summary>
    public long CountUnresolvedEdges() => _index.WithLock(() => _index.QueryScalar(
        "SELECT COUNT(*) FROM edges e JOIN nodes n ON n.id = e.target_id WHERE n.is_external = 1"));

    /// <summary>Число входящих связей для набора узлов (нужно карточкам поиска).</summary>
    public IReadOnlyDictionary<string, int> CountIncoming(IReadOnlyList<string> ids) => CountPerNode(ids, "target_id");

    /// <summary>Число исходящих связей для набора узлов.</summary>
    public IReadOnlyDictionary<string, int> CountOutgoing(IReadOnlyList<string> ids) => CountPerNode(ids, "source_id");

    private IReadOnlyDictionary<string, int> CountPerNode(IReadOnlyList<string> ids, string column)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        if (ids.Count == 0)
        {
            return result;
        }

        return _index.WithLock(() =>
        {
            foreach (var chunk in Chunk([.. ids], 400))
            {
                using var command = _index.CreateCommand(
                    $"SELECT {column}, COUNT(*) FROM edges WHERE {column} IN ({Placeholders(chunk.Count, "@c")}) GROUP BY {column}");
                AddIdParameters(command, chunk, "@c");
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    result[reader.GetString(0)] = reader.GetInt32(1);
                }
            }

            return (IReadOnlyDictionary<string, int>)result;
        });
    }

    /// <summary>Сводка по индексу.</summary>
    public IndexStatistics GetStatistics() => _index.WithLock(() =>
    {
        var dumpPath = _index.GetMeta("dump_path");
        DateTimeOffset? indexedAt = DateTimeOffset.TryParse(_index.GetMeta("indexed_at"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;

        return new IndexStatistics(
            Counter("cnt_nodes", "nodes"),
            Counter("cnt_edges", "edges"),
            Counter("cnt_symbols", "symbols"),
            Counter("cnt_calls", "SELECT COUNT(*) FROM edges WHERE kind = 'Calls'"),
            Counter("cnt_metadata_objects", "metadata_objects"),
            Counter("cnt_metadata_items", "metadata_items"),
            Counter("cnt_metadata_refs", "metadata_refs"),
            Counter("cnt_files", "files"),
            Scalar("SELECT COUNT(*) FROM nodes WHERE kind = 'Platform'"),
            Scalar("SELECT COUNT(*) FROM nodes WHERE is_external = 1"),
            dumpPath,
            indexedAt);
    });

    /// <summary>Счётчик из meta: считается при сборке индекса. Для старых индексов — подсчёт строк.</summary>
    private long Counter(string key, string tableOrQuery) =>
        long.TryParse(_index.GetMeta(key), CultureInfo.InvariantCulture, out var value)
            ? value
            : Scalar(tableOrQuery.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
                ? tableOrQuery
                : $"SELECT COUNT(*) FROM {tableOrQuery}");

    /// <summary>Состояние файлов из индекса: сравнение с текущей выгрузкой показывает, что изменилось.</summary>
    public IReadOnlyDictionary<string, (long Size, long Mtime)> ReadFileStates() => _index.WithLock(() =>
    {
        using var command = _index.CreateCommand("SELECT path, size, mtime FROM files");
        using var reader = command.ExecuteReader();
        var result = new Dictionary<string, (long, long)>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
        {
            result[reader.GetString(0)] = (reader.GetInt64(1), reader.GetInt64(2));
        }

        return (IReadOnlyDictionary<string, (long Size, long Mtime)>)result;
    });

    /// <summary>
    /// Поиск узлов по идентификатору и имени. Сначала идёт быстрый префиксный поиск через FTS5
    /// (имя обычно набирают с начала), и только если он ничего не дал — медленный просмотр по подстроке.
    /// </summary>
    public IReadOnlyList<NodeRow> Search(string query, int limit = 20, IReadOnlyCollection<string>? kinds = null)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        var text = query.Trim();
        var bounded = Math.Clamp(limit, 1, 500);
        var hits = SearchByPrefix(text, bounded, kinds);
        return hits.Count > 0 ? hits : SearchBySubstring(text, bounded, kinds);
    }

    /// <summary>Быстрый путь: префиксное совпадение по индексу FTS5 и точные совпадения.</summary>
    private IReadOnlyList<NodeRow> SearchByPrefix(string text, int limit, IReadOnlyCollection<string>? kinds)
    {
        var lower = text.ToLowerInvariant();
        return _index.WithLock(() =>
        {
            var sql = new StringBuilder(
                """
                SELECT id, kind, name, source_path, metadata_kind, is_external, platform_title, platform_version
                FROM nodes WHERE
                """);
            AppendKindFilter(sql, kinds);
            // Никаких LIKE: они не используют индекс и превращают запрос в скан 564 тысяч строк.
            sql.Append(" (id = @raw OR name_lower = @lower")
                .Append(" OR id IN (SELECT node_id FROM nodes_fts WHERE nodes_fts MATCH @fts))")
                .Append(" ORDER BY CASE WHEN id = @raw THEN 0 WHEN name_lower = @lower THEN 1 ELSE 2 END, length(name), name LIMIT @limit");

            using var command = _index.CreateCommand(sql.ToString());
            AddKindParameters(command, kinds);
            command.Parameters.AddWithValue("@raw", text);
            command.Parameters.AddWithValue("@lower", lower);
            command.Parameters.AddWithValue("@fts", EscapeFtsPrefix(text));
            command.Parameters.AddWithValue("@limit", limit);
            return ReadNodes(command);
        });
    }

    /// <summary>Медленный путь: поиск по подстроке в имени.</summary>
    private IReadOnlyList<NodeRow> SearchBySubstring(string text, int limit, IReadOnlyCollection<string>? kinds)
    {
        var lower = text.ToLowerInvariant();
        return _index.WithLock(() =>
        {
            var sql = new StringBuilder(
                """
                SELECT id, kind, name, source_path, metadata_kind, is_external, platform_title, platform_version
                FROM nodes WHERE
                """);
            AppendKindFilter(sql, kinds);
            sql.Append(" (name_lower LIKE @like ESCAPE '\\' OR id = @raw)")
                .Append(" ORDER BY CASE WHEN id = @raw THEN 0 WHEN name_lower = @lower THEN 1 ELSE 2 END, length(name), name LIMIT @limit");

            using var command = _index.CreateCommand(sql.ToString());
            AddKindParameters(command, kinds);
            command.Parameters.AddWithValue("@raw", text);
            command.Parameters.AddWithValue("@lower", lower);
            command.Parameters.AddWithValue("@like", "%" + EscapeLike(lower) + "%");
            command.Parameters.AddWithValue("@limit", limit);
            return ReadNodes(command);
        });
    }

    private static void AppendKindFilter(StringBuilder sql, IReadOnlyCollection<string>? kinds)
    {
        if (kinds is { Count: > 0 })
        {
            sql.Append(" kind IN (").Append(string.Join(", ", kinds.Select(static (_, i) => "@k" + i.ToString(CultureInfo.InvariantCulture)))).Append(") AND ");
        }
    }

    /// <summary>Узел по идентификатору.</summary>
    public NodeRow? GetNode(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        return _index.WithLock(() =>
        {
            using var command = _index.CreateCommand(
                "SELECT id, kind, name, source_path, metadata_kind, is_external, platform_title, platform_version FROM nodes WHERE id = @id");
            command.Parameters.AddWithValue("@id", id.Trim());
            using var reader = command.ExecuteReader();
            return reader.Read() ? MapNode(reader) : null;
        });
    }

    /// <summary>Связи, выходящие из узла.</summary>
    public IReadOnlyList<EdgeRow> Outgoing(string id, string? kind = null, int limit = 200) =>
        ReadEdges("source_id", id, kind, limit);

    /// <summary>Связи, входящие в узел.</summary>
    public IReadOnlyList<EdgeRow> Incoming(string id, string? kind = null, int limit = 200) =>
        ReadEdges("target_id", id, kind, limit);

    /// <summary>Кто вызывает узел (по связям вызовов).</summary>
    public IReadOnlyList<string> Callers(string id, int limit = 200) => _index.WithLock(() =>
    {
        using var command = _index.CreateCommand(
            """
            SELECT DISTINCT source_id FROM edges
            WHERE kind = 'Calls' AND target_id = @id
            ORDER BY source_id
            LIMIT @limit
            """);
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@limit", Math.Clamp(limit, 1, 1000));
        using var reader = command.ExecuteReader();
        var result = new List<string>();
        while (reader.Read())
        {
            result.Add(reader.GetString(0));
        }

        return result;
    });

    /// <summary>Кого вызывает узел.</summary>
    public IReadOnlyList<string> Callees(string id, int limit = 200) => _index.WithLock(() =>
    {
        using var command = _index.CreateCommand(
            """
            SELECT DISTINCT target_id FROM edges
            WHERE kind = 'Calls' AND source_id = @id
            ORDER BY target_id
            LIMIT @limit
            """);
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@limit", Math.Clamp(limit, 1, 1000));
        using var reader = command.ExecuteReader();
        var result = new List<string>();
        while (reader.Read())
        {
            result.Add(reader.GetString(0));
        }

        return result;
    });

    /// <summary>Кто вызывает процедуру, найденную по имени цели (по тексту вызова).</summary>
    public IReadOnlyList<EdgeRow> CallersByName(string calleeName, int limit = 200)
    {
        if (string.IsNullOrWhiteSpace(calleeName))
        {
            return [];
        }

        return _index.WithLock(() =>
        {
            using var command = _index.CreateCommand(
                """
                SELECT source_id, target_id, kind, line, detail FROM edges
                WHERE kind = 'Calls' AND detail = @name
                ORDER BY source_id, line LIMIT @limit
                """);
            command.Parameters.AddWithValue("@name", calleeName.Trim());
            command.Parameters.AddWithValue("@limit", Math.Clamp(limit, 1, 1000));
            using var reader = command.ExecuteReader();
            var result = new List<EdgeRow>();
            while (reader.Read())
            {
                result.Add(new EdgeRow(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetInt32(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4)));
            }

            return result;
        });
    }

    /// <summary>Кто использует объект метаданных: из кода, из текстов запросов, как тип реквизита.</summary>
    public IReadOnlyList<MetadataRefRow> Usages(string targetId, int limit = 200, string? context = null) => _index.WithLock(() =>
    {
        using var command = _index.CreateCommand(
            context is null
                ? "SELECT source_id, target_id, context, line, detail FROM metadata_refs WHERE target_id = @id ORDER BY context, source_id LIMIT @limit"
                : "SELECT source_id, target_id, context, line, detail FROM metadata_refs WHERE target_id = @id AND context = @context ORDER BY source_id LIMIT @limit");
        command.Parameters.AddWithValue("@id", targetId);
        command.Parameters.AddWithValue("@limit", Math.Clamp(limit, 1, 1000));
        if (context is not null)
        {
            command.Parameters.AddWithValue("@context", context);
        }

        using var reader = command.ExecuteReader();
        var result = new List<MetadataRefRow>();
        while (reader.Read())
        {
            result.Add(new MetadataRefRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetInt32(3),
                reader.IsDBNull(4) ? null : reader.GetString(4)));
        }

        return result;
    });

    /// <summary>Обращения, которые делает узел: какие объекты метаданных он затрагивает.</summary>
    public IReadOnlyList<MetadataRefRow> ReferencesOf(string sourceId, int limit = 200) => _index.WithLock(() =>
    {
        using var command = _index.CreateCommand(
            "SELECT source_id, target_id, context, line, detail FROM metadata_refs WHERE source_id = @id ORDER BY target_id LIMIT @limit");
        command.Parameters.AddWithValue("@id", sourceId);
        command.Parameters.AddWithValue("@limit", Math.Clamp(limit, 1, 1000));
        using var reader = command.ExecuteReader();
        var result = new List<MetadataRefRow>();
        while (reader.Read())
        {
            result.Add(new MetadataRefRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetInt32(3),
                reader.IsDBNull(4) ? null : reader.GetString(4)));
        }

        return result;
    });

    /// <summary>
    /// «Умный» поиск символа: точное имя, затем префикс, затем смысловые термы (BM25), и только
    /// в конце — просмотр по подстроке. Ступени идут по возрастанию цены.
    /// </summary>
    public IReadOnlyList<SymbolRow> SmartSearch(string query, int limit = 20)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        var text = query.Trim();
        var lower = text.ToLowerInvariant();
        var bounded = Math.Clamp(limit, 1, 200);

        var hits = QuerySymbols("s.name_lower = @lower", lower, null, bounded);
        if (hits.Count > 0)
        {
            return hits;
        }

        hits = QuerySymbols(@"s.name_lower LIKE @prefix ESCAPE '\'", lower, EscapeLike(lower) + "%", bounded);
        if (hits.Count > 0)
        {
            return hits;
        }

        hits = SearchSymbolsByTerms(text, bounded);
        return hits.Count > 0
            ? hits
            : QuerySymbols(@"s.name_lower LIKE @like ESCAPE '\'", lower, "%" + EscapeLike(lower) + "%", bounded);
    }

    /// <summary>Поиск по смысловым термам: имя по частям, шапка комментария, имена параметров.</summary>
    public IReadOnlyList<SymbolRow> SearchSymbolsByTerms(string query, int limit = 20)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        var match = EscapeFtsPrefix(query);
        if (match.Length == 0)
        {
            return [];
        }

        return _index.WithLock(() =>
        {
            using var command = _index.CreateCommand(
                """
                SELECT s.id, s.node_id, s.module_path, s.owner_id, s.name, s.kind, s.is_export, s.start_line,
                       s.end_line, s.region, s.parameters, s.comment_head
                FROM terms_fts t
                JOIN symbols s ON s.id = CAST(t.symbol_id AS INTEGER)
                WHERE terms_fts MATCH @match
                ORDER BY bm25(terms_fts), length(s.name)
                LIMIT @limit
                """);
            command.Parameters.AddWithValue("@match", match);
            command.Parameters.AddWithValue("@limit", Math.Clamp(limit, 1, 200));

            using var reader = command.ExecuteReader();
            var result = new List<SymbolRow>();
            while (reader.Read())
            {
                result.Add(new SymbolRow(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.GetString(4),
                    reader.GetString(5),
                    reader.GetInt32(6) != 0,
                    reader.GetInt32(7),
                    reader.GetInt32(8),
                    reader.IsDBNull(9) ? null : reader.GetString(9),
                    reader.IsDBNull(10) ? null : reader.GetString(10),
                    reader.IsDBNull(11) ? null : reader.GetString(11)));
            }

            return (IReadOnlyList<SymbolRow>)result;
        });
    }

    /// <summary>Поиск процедур и функций по имени (точный, по префиксу и по подстроке).</summary>
    public IReadOnlyList<SymbolRow> FindSymbols(string name, int limit = 20, bool exact = false)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return [];
        }

        var text = name.Trim();
        var lower = text.ToLowerInvariant();
        var bounded = Math.Clamp(limit, 1, 200);

        // Три ступени вместо одного LIKE с OR: точное совпадение и префикс идут по индексу,
        // и только если ничего не нашлось — просмотр по подстроке.
        if (exact)
        {
            return QuerySymbols("s.name_lower = @lower", lower, null, bounded);
        }

        var hits = QuerySymbols("s.name_lower = @lower", lower, null, bounded);
        if (hits.Count > 0)
        {
            return hits;
        }

        hits = QuerySymbols(@"s.name_lower LIKE @prefix ESCAPE '\'", lower, EscapeLike(lower) + "%", bounded);
        return hits.Count > 0
            ? hits
            : QuerySymbols(@"s.name_lower LIKE @like ESCAPE '\'", lower, "%" + EscapeLike(lower) + "%", bounded);
    }

    private IReadOnlyList<SymbolRow> QuerySymbols(string condition, string lower, string? pattern, int limit)
    {
        return _index.WithLock(() =>
        {
            var sql =
                $"""
                 SELECT s.id, s.node_id, s.module_path, s.owner_id, s.name, s.kind, s.is_export, s.start_line,
                        s.end_line, s.region, s.parameters, s.comment_head
                 FROM symbols s
                 WHERE {condition}
                 ORDER BY length(s.name), s.name LIMIT @limit
                 """;

            using var command = _index.CreateCommand(sql);
            command.Parameters.AddWithValue("@lower", lower);
            command.Parameters.AddWithValue("@limit", limit);
            if (pattern is not null)
            {
                command.Parameters.AddWithValue(condition.Contains("@prefix", StringComparison.Ordinal) ? "@prefix" : "@like", pattern);
            }

            using var reader = command.ExecuteReader();
            var result = new List<SymbolRow>();
            while (reader.Read())
            {
                result.Add(new SymbolRow(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.GetString(4),
                    reader.GetString(5),
                    reader.GetInt32(6) != 0,
                    reader.GetInt32(7),
                    reader.GetInt32(8),
                    reader.IsDBNull(9) ? null : reader.GetString(9),
                    reader.IsDBNull(10) ? null : reader.GetString(10),
                    reader.IsDBNull(11) ? null : reader.GetString(11)));
            }

            return (IReadOnlyList<SymbolRow>)result;
        });
    }

    /// <summary>Процедура, накрывающая указанную строку модуля.</summary>
    public SymbolRow? FindSymbolAt(string modulePath, int line)
    {
        var symbols = FindSymbolsInModule(modulePath);
        return symbols.FirstOrDefault(s => s.StartLine <= line && s.EndLine >= line);
    }

    /// <summary>Все процедуры модуля по порядку строк.</summary>
    public IReadOnlyList<SymbolRow> FindSymbolsInModule(string modulePath) => _index.WithLock(() =>
    {
        using var command = _index.CreateCommand(
            """
            SELECT id, node_id, module_path, owner_id, name, kind, is_export, start_line, end_line, region, parameters, comment_head
            FROM symbols WHERE module_path = @path ORDER BY start_line
            """);
        command.Parameters.AddWithValue("@path", modulePath);
        using var reader = command.ExecuteReader();
        var result = new List<SymbolRow>();
        while (reader.Read())
        {
            result.Add(new SymbolRow(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetInt32(6) != 0,
                reader.GetInt32(7),
                reader.GetInt32(8),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.IsDBNull(10) ? null : reader.GetString(10),
                reader.IsDBNull(11) ? null : reader.GetString(11)));
        }

        return result;
    });

    /// <summary>Состав объекта метаданных: реквизиты, табличные части, формы, макеты, команды.</summary>
    public IReadOnlyList<MetadataItemRow> GetMetadataItems(string objectId, int limit = 500) => _index.WithLock(() =>
    {
        using var command = _index.CreateCommand(
            "SELECT object_id, kind, name, type_info, parent_id FROM metadata_items WHERE object_id = @id ORDER BY kind, name LIMIT @limit");
        command.Parameters.AddWithValue("@id", objectId);
        command.Parameters.AddWithValue("@limit", Math.Clamp(limit, 1, 5000));
        using var reader = command.ExecuteReader();
        var result = new List<MetadataItemRow>();
        while (reader.Read())
        {
            result.Add(new MetadataItemRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4)));
        }

        return result;
    });

    /// <summary>Объект метаданных по идентификатору: свойства, признак верхнего уровня, владелец.</summary>
    public MetadataObjectRow? GetMetadataObject(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        return _index.WithLock(() =>
        {
            using var command = _index.CreateCommand(
                """
                SELECT id, kind, name, synonym, uuid, source_path, comment, is_top_level, parent_id, properties
                FROM metadata_objects WHERE id = @id
                """);
            command.Parameters.AddWithValue("@id", id.Trim());
            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                return (MetadataObjectRow?)null;
            }

            return new MetadataObjectRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.GetInt32(7) != 0,
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9));
        });
    }

    /// <summary>Дети объекта метаданных вместе с их общим числом: состав дерева для карточки.</summary>
    public MetadataChildrenPage MetadataChildren(string parentId, int limit = 500) => _index.WithLock(() =>
    {
        var items = new List<MetadataItemRow>();
        using (var command = _index.CreateCommand(
            """
            SELECT object_id, kind, name, type_info, parent_id, synonym, comment
            FROM metadata_items WHERE parent_id = @id ORDER BY kind, name LIMIT @limit
            """))
        {
            command.Parameters.AddWithValue("@id", parentId);
            command.Parameters.AddWithValue("@limit", Math.Clamp(limit, 1, 5000));
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                items.Add(new MetadataItemRow(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6)));
            }
        }

        using var count = _index.CreateCommand("SELECT COUNT(*) FROM metadata_items WHERE parent_id = @id");
        count.Parameters.AddWithValue("@id", parentId);
        var total = Convert.ToInt32(count.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        return new MetadataChildrenPage(items, total);
    });

    /// <summary>Пути модулей объекта: связи Contains ведут от объекта к его модулям.</summary>
    public IReadOnlyList<string> ModulePaths(string ownerId, int limit = 50) => _index.WithLock(() =>
    {
        using var command = _index.CreateCommand(
            """
            SELECT DISTINCT n.source_path FROM edges e JOIN nodes n ON n.id = e.target_id
            WHERE e.source_id = @id AND e.kind = 'Contains' AND n.kind = 'Module' AND n.source_path IS NOT NULL
            ORDER BY n.source_path LIMIT @limit
            """);
        command.Parameters.AddWithValue("@id", ownerId);
        command.Parameters.AddWithValue("@limit", Math.Clamp(limit, 1, 500));
        using var reader = command.ExecuteReader();
        var result = new List<string>();
        while (reader.Read())
        {
            if (!reader.IsDBNull(0))
            {
                result.Add(reader.GetString(0));
            }
        }

        return (IReadOnlyList<string>)result;
    });

    /// <summary>
    /// Пути файлов выгрузки из индекса: поиску по тексту не нужен обход каталога.
    /// Сначала модули BSL — совпадения в коде нужнее, чем в XML.
    /// </summary>
    public IReadOnlyList<string> FilePaths(
        IReadOnlyCollection<string>? extensions,
        IReadOnlyList<string>? prefixes,
        int limit = 200_000) => _index.WithLock(() =>
    {
        var sql = new StringBuilder("SELECT path FROM files WHERE 1 = 1");
        if (extensions is { Count: > 0 })
        {
            sql.Append(" AND (").Append(LikeList(extensions.Count, "@ext")).Append(')');
        }

        if (prefixes is { Count: > 0 })
        {
            sql.Append(" AND (").Append(LikeList(prefixes.Count, "@pre")).Append(')');
        }

        sql.Append(" ORDER BY (path LIKE '%.bsl') DESC, path LIMIT @limit");

        using var command = _index.CreateCommand(sql.ToString());
        if (extensions is { Count: > 0 })
        {
            var index = 0;
            foreach (var extension in extensions)
            {
                command.Parameters.AddWithValue($"@ext{index++}", "%" + EscapeLike(extension));
            }
        }

        if (prefixes is { Count: > 0 })
        {
            var index = 0;
            foreach (var prefix in prefixes)
            {
                command.Parameters.AddWithValue($"@pre{index++}", EscapeLike(prefix) + "%");
            }
        }

        command.Parameters.AddWithValue("@limit", Math.Clamp(limit, 1, 1_000_000));

        using var reader = command.ExecuteReader();
        var result = new List<string>();
        while (reader.Read())
        {
            result.Add(reader.GetString(0));
        }

        return (IReadOnlyList<string>)result;
    });

    /// <summary>Владелец файла выгрузки: для модулей — объект, которому принадлежит файл (связь Contains),
    /// для XML — объект метаданных с этим путём. Нужно поиску по тексту, чтобы не ждать разбор.
    /// </summary>
    public IReadOnlyDictionary<string, string> OwnersOf(IReadOnlyList<string> paths)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (paths.Count == 0)
        {
            return result;
        }

        return _index.WithLock(() =>
        {
            foreach (var chunk in Chunk([.. paths], 400))
            {
                using (var modules = _index.CreateCommand(
                    $"""
                    SELECT n.source_path, e.source_id FROM edges e JOIN nodes n ON n.id = e.target_id
                    WHERE e.kind = 'Contains' AND n.kind = 'Module' AND n.source_path IN ({Placeholders(chunk.Count, "@p")})
                    """))
                {
                    AddIdParameters(modules, chunk, "@p");
                    using var reader = modules.ExecuteReader();
                    while (reader.Read())
                    {
                        if (!reader.IsDBNull(0))
                        {
                            result[reader.GetString(0)] = TopLevel(reader.GetString(1));
                        }
                    }
                }

                using var objects = _index.CreateCommand(
                    $"SELECT source_path, id FROM metadata_objects WHERE source_path IN ({Placeholders(chunk.Count, "@p")})");
                AddIdParameters(objects, chunk, "@p");
                using var objectReader = objects.ExecuteReader();
                while (objectReader.Read())
                {
                    if (!objectReader.IsDBNull(0))
                    {
                        result[objectReader.GetString(0)] = TopLevel(objectReader.GetString(1));
                    }
                }
            }

            return (IReadOnlyDictionary<string, string>)result;
        });
    }

    /// <summary>
    /// Каталоги объектов верхнего уровня: файлы без собственного объекта (макеты, схемы, формы)
    /// получают владельца по каталогу, в котором лежат.
    /// </summary>
    public IReadOnlyList<(string Directory, string Owner)> TopLevelObjectDirectories() => _index.WithLock(() =>
    {
        using var command = _index.CreateCommand(
            "SELECT id, source_path FROM metadata_objects WHERE is_top_level = 1 AND source_path IS NOT NULL");
        using var reader = command.ExecuteReader();
        var result = new List<(string, string)>();
        while (reader.Read())
        {
            var path = reader.GetString(1);
            var separator = path.LastIndexOf('.');
            var directory = separator > 0 ? path[..separator] : path;
            if (directory.Length > 0)
            {
                result.Add((directory, reader.GetString(0)));
            }
        }

        return (IReadOnlyList<(string, string)>)result;
    });

    /// <summary>Идентификатор объекта верхнего уровня: «Document.Заказ/Form.Форма» → «Document.Заказ».</summary>
    private static string TopLevel(string id)
    {
        var separator = id.IndexOf('/');
        return separator < 0 ? id : id[..separator];
    }

    /// <summary>Поиск объектов метаданных по имени и синониму.</summary>
    public IReadOnlyList<(string Id, string Kind, string Name, string? Synonym)> FindMetadataObjects(string query, int limit = 20)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        var lower = query.Trim().ToLowerInvariant();
        return _index.WithLock(() =>
        {
            using var command = _index.CreateCommand(
                """
                SELECT id, kind, name, synonym FROM metadata_objects
                WHERE name_lower LIKE @like OR lower(COALESCE(synonym, '')) LIKE @like
                ORDER BY CASE WHEN name_lower = @lower THEN 0 WHEN name_lower LIKE @prefix THEN 1 ELSE 2 END, length(name), name
                LIMIT @limit
                """);
            command.Parameters.AddWithValue("@like", "%" + EscapeLike(lower) + "%");
            command.Parameters.AddWithValue("@prefix", EscapeLike(lower) + "%");
            command.Parameters.AddWithValue("@lower", lower);
            command.Parameters.AddWithValue("@limit", Math.Clamp(limit, 1, 200));
            using var reader = command.ExecuteReader();
            var result = new List<(string, string, string, string?)>();
            while (reader.Read())
            {
                result.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3)));
            }

            return (IReadOnlyList<(string, string, string, string?)>)result;
        });
    }

    /// <summary>Обход связей на заданную глубину: рекурсивный запрос вместо обхода графа в памяти.</summary>
    public IReadOnlyList<ReachRow> Reach(string startId, int depth = 2, int maxNodes = 200, string? direction = null, IReadOnlyCollection<string>? kinds = null)
    {
        if (string.IsNullOrWhiteSpace(startId))
        {
            return [];
        }

        var boundedDepth = Math.Clamp(depth, 1, 4);
        var boundedNodes = Math.Clamp(maxNodes, 1, 2000);

        return _index.WithLock(() =>
        {
            var (walk, kindPrefix) = BuildWalk(direction, kinds);
            using var command = _index.CreateCommand(
                $"""
                 WITH RECURSIVE walk(id, depth) AS (
                     SELECT @start, 0
                     UNION
                     {walk}
                 )
                 SELECT id, MIN(depth) AS depth FROM walk GROUP BY id ORDER BY depth, id LIMIT @max
                 """);
            command.Parameters.AddWithValue("@start", startId.Trim());
            command.Parameters.AddWithValue("@depth", boundedDepth);
            command.Parameters.AddWithValue("@max", boundedNodes);
            AddKindParameters(command, kinds, kindPrefix);

            using var reader = command.ExecuteReader();
            var result = new List<ReachRow>();
            while (reader.Read())
            {
                result.Add(new ReachRow(reader.GetString(0), reader.GetInt32(1)));
            }

            return (IReadOnlyList<ReachRow>)result;
        });
    }

    /// <summary>
    /// Рекурсивная часть обхода связей. Временные таблицы намеренно не используются: индекс
    /// открывается и только на чтение, а создание временной таблицы в этом режиме запрещено.
    /// </summary>
    private static (string Walk, string KindPrefix) BuildWalk(string? direction, IReadOnlyCollection<string>? kinds)
    {
        var kindFilter = kinds is { Count: > 0 }
            ? " AND e.kind IN (" + string.Join(", ", kinds.Select(static (_, i) => "@e" + i.ToString(CultureInfo.InvariantCulture))) + ")"
            : string.Empty;

        var walk = direction switch
        {
            "out" => "SELECT e.target_id, w.depth + 1 FROM edges e JOIN walk w ON e.source_id = w.id WHERE w.depth < @depth" + kindFilter,
            "in" => "SELECT e.source_id, w.depth + 1 FROM edges e JOIN walk w ON e.target_id = w.id WHERE w.depth < @depth" + kindFilter,
            _ => "SELECT CASE WHEN e.source_id = w.id THEN e.target_id ELSE e.source_id END, w.depth + 1 FROM edges e JOIN walk w ON (e.source_id = w.id OR e.target_id = w.id) WHERE w.depth < @depth" + kindFilter,
        };

        return (walk, "@e");
    }

    private static void AddKindParameters(SqliteCommand command, IReadOnlyCollection<string>? kinds, string prefix)
    {
        if (kinds is not { Count: > 0 })
        {
            return;
        }

        var index = 0;
        foreach (var kind in kinds)
        {
            command.Parameters.AddWithValue(prefix + index.ToString(CultureInfo.InvariantCulture), kind);
            index++;
        }
    }

    /// <summary>Узлы и связи внутри окружения указанного узла.</summary>
    public (IReadOnlyList<NodeRow> Nodes, IReadOnlyList<EdgeRow> Edges) Neighborhood(
        string startId,
        int depth = 1,
        int maxNodes = 60,
        string? direction = null,
        IReadOnlyCollection<string>? nodeKinds = null,
        IReadOnlyCollection<string>? edgeKinds = null)
    {
        var reached = Reach(startId, depth, maxNodes, direction, edgeKinds);
        if (reached.Count == 0)
        {
            return ([], []);
        }

        // Обход уже ограничен лимитом, дальше работаем по списку идентификаторов порциями:
        // повторять рекурсивный запрос в соединении нельзя — на «хабе» это десятки секунд.
        var ids = reached.Select(static r => r.Id).ToList();
        return _index.WithLock(() =>
        {
            var nodes = new List<NodeRow>();
            foreach (var chunk in Chunk(ids, 400))
            {
                var sql = new StringBuilder(
                    "SELECT id, kind, name, source_path, metadata_kind, is_external, platform_title, platform_version FROM nodes WHERE id IN (");
                sql.Append(Placeholders(chunk.Count, "@i")).Append(')');
                AppendNodeKindFilter(sql, nodeKinds, "kind");
                sql.Append(" ORDER BY kind, name");

                using var command = _index.CreateCommand(sql.ToString());
                AddIdParameters(command, chunk, "@i");
                AddKindParameters(command, nodeKinds, "@n");
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    nodes.Add(MapNode(reader));
                }
            }

            var keep = new HashSet<string>(nodes.Select(static n => n.Id), StringComparer.Ordinal);
            var edges = new List<EdgeRow>();
            foreach (var chunk in Chunk(ids, 400))
            {
                var sql = new StringBuilder("SELECT source_id, target_id, kind, line, detail FROM edges WHERE source_id IN (");
                sql.Append(Placeholders(chunk.Count, "@s")).Append(')');
                if (edgeKinds is { Count: > 0 })
                {
                    sql.Append(" AND kind IN (").Append(Placeholders(edgeKinds.Count, "@e")).Append(')');
                }

                using var command = _index.CreateCommand(sql.ToString());
                AddIdParameters(command, chunk, "@s");
                AddKindParameters(command, edgeKinds, "@e");
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var source = reader.GetString(0);
                    var target = reader.GetString(1);
                    if (keep.Contains(source) && keep.Contains(target))
                    {
                        edges.Add(new EdgeRow(source, target, reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetInt32(3), reader.IsDBNull(4) ? null : reader.GetString(4)));
                    }
                }
            }

            return ((IReadOnlyList<NodeRow>)nodes, (IReadOnlyList<EdgeRow>)edges);
        });
    }

    private static IEnumerable<List<string>> Chunk(List<string> items, int size)
    {
        for (var offset = 0; offset < items.Count; offset += size)
        {
            yield return items.GetRange(offset, Math.Min(size, items.Count - offset));
        }
    }

    private static string Placeholders(int count, string prefix) =>
        string.Join(", ", Enumerable.Range(0, count).Select(i => prefix + i.ToString(CultureInfo.InvariantCulture)));

    /// <summary>Условие LIKE по каждому значению: <c>path LIKE @ext0 OR path LIKE @ext1</c>.</summary>
    private static string LikeList(int count, string prefix) =>
        string.Join(" OR ", Enumerable.Range(0, count).Select(i => $"path LIKE {prefix}{i.ToString(CultureInfo.InvariantCulture)}"));

    private static void AddIdParameters(SqliteCommand command, List<string> ids, string prefix)
    {
        for (var index = 0; index < ids.Count; index++)
        {
            command.Parameters.AddWithValue(prefix + index.ToString(CultureInfo.InvariantCulture), ids[index]);
        }
    }

    private static void AppendNodeKindFilter(StringBuilder sql, IReadOnlyCollection<string>? kinds, string column)
    {
        if (kinds is { Count: > 0 })
        {
            sql.Append(" AND ").Append(column).Append(" IN (").Append(string.Join(", ", kinds.Select(static (_, i) => "@n" + i.ToString(CultureInfo.InvariantCulture)))).Append(')');
        }
    }

    private IReadOnlyList<EdgeRow> ReadEdges(string column, string id, string? kind, int limit)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return [];
        }

        return _index.WithLock(() =>
        {
            var sql = $"SELECT source_id, target_id, kind, line, detail FROM edges WHERE {column} = @id";
            if (kind is not null)
            {
                sql += " AND kind = @kind";
            }

            sql += " ORDER BY kind, line LIMIT @limit";

            using var command = _index.CreateCommand(sql);
            command.Parameters.AddWithValue("@id", id.Trim());
            command.Parameters.AddWithValue("@limit", Math.Clamp(limit, 1, 2000));
            if (kind is not null)
            {
                command.Parameters.AddWithValue("@kind", kind);
            }

            using var reader = command.ExecuteReader();
            var result = new List<EdgeRow>();
            while (reader.Read())
            {
                result.Add(new EdgeRow(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetInt32(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4)));
            }

            return result;
        });
    }

    private static string BuildSearchSql(IReadOnlyCollection<string>? kinds)
    {
        var sql = new StringBuilder(
            """
            SELECT id, kind, name, source_path, metadata_kind, is_external, platform_title, platform_version
            FROM nodes WHERE
            """);

        if (kinds is { Count: > 0 })
        {
            sql.Append(" kind IN (").Append(string.Join(", ", kinds.Select(static (_, i) => "@k" + i.ToString(CultureInfo.InvariantCulture)))).Append(") AND ");
        }

        // Поиск по подстроке идёт по name_lower: индекса для LIKE '%…%' нет, но 564 тысячи строк
        // просматриваются за десятки миллисекунд, а триграммный FTS5 на таком объёме занимал гигабайты.
        sql.Append(@"(name_lower LIKE @like ESCAPE '\' OR id = @raw OR name_lower = @lower");
        sql.Append(@" ) ORDER BY CASE WHEN id = @raw THEN 0 WHEN name_lower = @lower THEN 1 WHEN name_lower LIKE @prefix ESCAPE '\' THEN 2 ELSE 3 END, length(name), name LIMIT @limit");
        return sql.ToString();
    }

    /// <summary>Добавляет параметры фильтра по видам узлов (плейсхолдеры @k0, @k1, …).</summary>
    private static void AddKindParameters(SqliteCommand command, IReadOnlyCollection<string>? kinds)
    {
        if (kinds is not { Count: > 0 })
        {
            return;
        }

        var index = 0;
        foreach (var kind in kinds)
        {
            command.Parameters.AddWithValue("@k" + index.ToString(CultureInfo.InvariantCulture), kind);
            index++;
        }
    }

    private IReadOnlyList<NodeRow> ReadNodes(SqliteCommand command)
    {
        using var reader = command.ExecuteReader();
        var result = new List<NodeRow>();
        while (reader.Read())
        {
            result.Add(MapNode(reader));
        }

        return result;
    }

    private static NodeRow MapNode(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.IsDBNull(3) ? null : reader.GetString(3),
        reader.IsDBNull(4) ? null : reader.GetString(4),
        reader.GetInt32(5) != 0,
        reader.IsDBNull(6) ? null : reader.GetString(6),
        reader.IsDBNull(7) ? null : reader.GetString(7));

    private long Count(string table) => _index.QueryScalar("SELECT COUNT(*) FROM " + table);

    private long Scalar(string sql) => _index.QueryScalar(sql);

    private static string EscapeLike(string value) => value.Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);

    /// <summary>FTS5: запрос оборачивается в кавычки, внутренние кавычки удваиваются.</summary>
    internal static string EscapeFts(string value) => "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    /// <summary>
    /// Префиксный запрос FTS5: текст режется на слова, каждое ищется с усечением.
    /// Кавычки здесь не годятся — «"слово"*» не является префиксным запросом.
    /// </summary>
    internal static string EscapeFtsPrefix(string value)
    {
        var tokens = value
            .Split([' ', '.', '/', '\\', '(', ')', ',', ':', '#', '*', '"', '\''], StringSplitOptions.RemoveEmptyEntries)
            .Select(static token => token.Trim())
            .Where(static token => token.Length > 0)
            .Select(static token => token + "*")
            .ToList();

        return tokens.Count == 0 ? "\"\"" : string.Join(' ', tokens);
    }
}
