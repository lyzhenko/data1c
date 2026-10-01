using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Data1c.Core.Analysis;
using Data1c.Core.Bsl;
using Data1c.Core.Dump;
using Data1c.Core.Graph;
using Data1c.Core.Metadata;
using Microsoft.Data.Sqlite;

namespace Data1c.Store;

/// <summary>Записывает результат разбора выгрузки в индекс.</summary>
/// <remarks>
/// Запись идёт одной транзакцией с <c>synchronous=OFF</c> и подготовленными командами: на полной
/// выгрузке это миллионы строк, и построчная вставка с обычной синхронизацией заняла бы минуты.
/// </remarks>
public sealed class IndexWriter
{
    private readonly SqliteIndex _index;

    public IndexWriter(SqliteIndex index)
    {
        ArgumentNullException.ThrowIfNull(index);
        _index = index;
    }

    /// <summary>Читать комментарии над процедурами для смыслового поиска (нужно чтение файлов модулей).</summary>
    public bool IncludeComments { get; init; } = true;

    /// <summary>Полностью перезаписывает индекс данными разбора.</summary>
    public IndexWriteResult Write(IDumpSource source, AnalysisResult result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(result);
        var stopwatch = Stopwatch.StartNew();

        return _index.WithLock(() =>
        {
            _index.Execute("PRAGMA synchronous=OFF");
            try
            {
                using var transaction = _index.Connection.BeginTransaction();
                var connection = transaction.Connection!;
                var counters = new Counters();

                Clear(connection);
                WriteFiles(source, connection, counters, cancellationToken);
                WriteNodes(result.Graph.Nodes, connection, counters, cancellationToken);
                WriteEdges(result.Graph.Edges, connection, counters, cancellationToken);
                WriteSymbols(source, result.Modules, connection, counters, cancellationToken);
                WriteMetadata(result, connection, counters, cancellationToken);

                _index.SetMeta("dump_path", result.SourceName);
                _index.SetMeta("indexed_at", DateTimeOffset.UtcNow.ToString("O"));
                _index.SetMeta("schema_version", IndexSchema.Version.ToString(System.Globalization.CultureInfo.InvariantCulture));
                _index.SetMeta("nodes", counters.Nodes.ToString(System.Globalization.CultureInfo.InvariantCulture));
                _index.SetMeta("edges", counters.Edges.ToString(System.Globalization.CultureInfo.InvariantCulture));

                WriteCounters(connection, counters, cancellationToken);
                transaction.Commit();
                stopwatch.Stop();

                return new IndexWriteResult(
                    counters.Nodes,
                    counters.Edges,
                    counters.Symbols,
                    counters.Calls,
                    counters.MetadataObjects,
                    counters.MetadataItems,
                    counters.MetadataRefs,
                    counters.Files,
                    stopwatch.Elapsed);
            }
            finally
            {
                _index.Execute("PRAGMA synchronous=NORMAL");
            }
        });
    }

    /// <summary>
    /// Частичная переиндексация: строки изменённых модулей удаляются, затем записываются заново
    /// из разбора только этих модулей. Остальной индекс не переписывается, поэтому правка одного
    /// модуля стоит секунды, а не минуты.
    /// </summary>
    /// <remarks>
    /// Частичный разбор не видит процедуры других модулей, поэтому после записи вызовы связываются
    /// по имени с настоящими узлами процедур, а вызовы к только что появившимся процедурам
    /// перенаправляются с внешних заглушек на них.
    /// </remarks>
    public IndexWriteResult WriteModules(
        IDumpSource source,
        AnalysisResult result,
        IndexScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(scope);
        var stopwatch = Stopwatch.StartNew();

        return _index.WithLock(() =>
        {
            _index.Execute("PRAGMA synchronous=OFF");
            try
            {
                using var transaction = _index.Connection.BeginTransaction();
                var connection = transaction.Connection!;
                var counters = new Counters();

                var scopePaths = new HashSet<string>(scope.ModuleFiles, StringComparer.OrdinalIgnoreCase);
                var scopeNodes = result.Graph.Nodes
                    .Where(node => node.SourcePath is not null && scopePaths.Contains(node.SourcePath))
                    .ToList();
                var scopeIds = new HashSet<string>(scopeNodes.Select(static node => node.Id), StringComparer.Ordinal);
                var scopeEdges = result.Graph.Edges
                    .Where(edge => scopeIds.Contains(edge.SourceId)
                        || (edge.Kind == GraphEdgeKind.Contains && scopeIds.Contains(edge.TargetId)))
                    .ToList();

                // Цели связей, которых нет среди узлов области: внешние заглушки вызовов и методы
                // платформы. Их нужно добавить в индекс, иначе связь будет вести в никуда.
                var referenced = new HashSet<string>(
                    scopeEdges.Where(edge => !scopeIds.Contains(edge.TargetId)).Select(static edge => edge.TargetId),
                    StringComparer.Ordinal);
                var present = referenced.Count == 0
                    ? []
                    : ReadIds(connection, count => $"SELECT id FROM nodes WHERE id IN ({Placeholders(count)})", [.. referenced]);
                var presentSet = new HashSet<string>(present, StringComparer.Ordinal);
                var extraNodes = result.Graph.Nodes
                    .Where(node => referenced.Contains(node.Id) && !presentSet.Contains(node.Id))
                    .ToList();

                var nodesToWrite = new List<GraphNode>(scopeNodes.Count + extraNodes.Count);
                nodesToWrite.AddRange(scopeNodes);
                nodesToWrite.AddRange(extraNodes);

                // Процедуры, которых в индексе ещё не было: только для них нужно искать вызовы
                // из других модулей, а это дорогой проход по внешним вызовам.
                var routineNames = scopeNodes
                    .Where(static node => node.Kind == GraphNodeKind.Routine)
                    .Select(static node => node.Name.ToLowerInvariant())
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
                var knownNames = routineNames.Count == 0
                    ? []
                    : ReadIds(
                        connection,
                        count => $"SELECT name_lower FROM nodes WHERE kind = 'Routine' AND name_lower IN ({Placeholders(count)})",
                        routineNames);
                var known = new HashSet<string>(knownNames, StringComparer.Ordinal);
                var addedRoutines = routineNames.Where(name => !known.Contains(name)).ToList();

                DeleteScope(connection, scope, scopePaths, new HashSet<string>(nodesToWrite.Select(static node => node.Id), StringComparer.Ordinal));
                WriteNodes(nodesToWrite, connection, counters, cancellationToken);
                WriteEdges(scopeEdges, connection, counters, cancellationToken);
                WriteSymbols(source, result.Modules, connection, counters, cancellationToken);
                TouchFiles(source, connection, scope, counters, cancellationToken);
                RepairCalls(connection, scopeIds, addedRoutines, cancellationToken);
                RecountCounters(connection);

                _index.SetMeta("indexed_at", DateTimeOffset.UtcNow.ToString("O"));
                transaction.Commit();
                stopwatch.Stop();

                return new IndexWriteResult(
                    counters.Nodes,
                    counters.Edges,
                    counters.Symbols,
                    counters.Calls,
                    0,
                    0,
                    0,
                    counters.Files,
                    stopwatch.Elapsed);
            }
            finally
            {
                _index.Execute("PRAGMA synchronous=NORMAL");
            }
        });
    }

    /// <summary>
    /// Убирает из индекса всё, что относится к указанным файлам. Входящие связи удаляются только
    /// у исчезнувших узлов: у процедуры, которая осталась на месте, идентификатор не меняется,
    /// и вызовы из других модулей должны сохраниться.
    /// </summary>
    private static void DeleteScope(
        SqliteConnection connection,
        IndexScope scope,
        HashSet<string> scopePaths,
        HashSet<string> keepIds)
    {
        var paths = new List<string>(scopePaths);
        if (scope.RemovedFiles is { Count: > 0 } removed)
        {
            paths.AddRange(removed);
        }

        if (paths.Count == 0)
        {
            return;
        }

        var oldNodeIds = ReadIds(connection, count => $"SELECT id FROM nodes WHERE source_path IN ({Placeholders(count)})", paths);
        var symbolIds = ReadIds(connection, count => $"SELECT id FROM symbols WHERE module_path IN ({Placeholders(count)})", paths);
        var goneNodeIds = oldNodeIds.Where(id => !keepIds.Contains(id)).ToList();

        ExecuteIds(connection, count => $"DELETE FROM edges WHERE source_id IN ({Placeholders(count)})", oldNodeIds);
        ExecuteIds(connection, count => $"DELETE FROM edges WHERE kind = 'Contains' AND target_id IN ({Placeholders(count)})", oldNodeIds);
        ExecuteIds(connection, count => $"DELETE FROM edges WHERE target_id IN ({Placeholders(count)})", goneNodeIds);
        ExecuteIds(connection, count => $"DELETE FROM metadata_refs WHERE source_id IN ({Placeholders(count)})", oldNodeIds);
        ExecuteIds(connection, count => $"DELETE FROM nodes_fts WHERE node_id IN ({Placeholders(count)})", oldNodeIds);
        ExecuteIds(connection, count => $"DELETE FROM nodes WHERE id IN ({Placeholders(count)})", oldNodeIds);
        ExecuteIds(connection, count => $"DELETE FROM terms_fts WHERE symbol_id IN ({Placeholders(count)})", symbolIds);
        ExecuteIds(connection, count => $"DELETE FROM symbols WHERE module_path IN ({Placeholders(count)})", paths);
        ExecuteIds(connection, count => $"DELETE FROM files WHERE path IN ({Placeholders(count)})", [.. scope.RemovedFiles ?? []]);
    }

    /// <summary>Обновляет строки файлов, которые перезаписаны, и убирает исчезнувшие.</summary>
    private static void TouchFiles(
        IDumpSource source,
        SqliteConnection connection,
        IndexScope scope,
        Counters counters,
        CancellationToken cancellationToken)
    {
        if (scope.ModuleFiles.Count == 0)
        {
            return;
        }

        // Сначала спрашиваем файлы по одному: обход всей выгрузки ради десятка файлов
        // стоит секунды на большой конфигурации.
        var changed = new List<DumpFile>(scope.ModuleFiles.Count);
        var missing = new List<string>();
        foreach (var path in scope.ModuleFiles)
        {
            var file = source.FindFile(path);
            if (file is null)
            {
                missing.Add(path);
            }
            else
            {
                changed.Add(file);
            }
        }

        if (missing.Count > 0)
        {
            var wanted = new HashSet<string>(missing, StringComparer.OrdinalIgnoreCase);
            foreach (var file in source.EnumerateFiles(cancellationToken))
            {
                if (wanted.Contains(file.RelativePath))
                {
                    changed.Add(file);
                }
            }
        }

        using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR REPLACE INTO files (path, size, mtime) VALUES (@path, @size, @mtime)";
        var pathParameter = command.Parameters.Add("@path", SqliteType.Text);
        var size = command.Parameters.Add("@size", SqliteType.Integer);
        var mtime = command.Parameters.Add("@mtime", SqliteType.Integer);

        foreach (var file in changed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            pathParameter.Value = file.RelativePath;
            size.Value = file.Size;
            mtime.Value = file.LastWriteTimeUtc.ToUnixTimeMilliseconds();
            command.ExecuteNonQuery();
            counters.Files++;
        }
    }

    /// <summary>
    /// Связывает вызовы изменённых модулей с настоящими узлами процедур: частичный разбор знает
    /// только свои модули и на остальные цели ставит внешние заглушки. Затем заглушки на процедуры
    /// из изменённых модулей заменяются настоящими узлами — этого ждут вызовы из других модулей.
    /// </summary>
    private static void RepairCalls(
        SqliteConnection connection,
        HashSet<string> scopeIds,
        IReadOnlyList<string> addedRoutineNames,
        CancellationToken cancellationToken)
    {
        if (scopeIds.Count == 0)
        {
            return;
        }

        // Имена сопоставляются в C#: встроенная функция lower() в SQLite знает только латиницу,
        // поэтому на кириллице регистронезависимое сравнение в SQL молча не находит ничего.
        var pending = new List<(long RowId, string Method, string Target)>();

        using (var outgoing = connection.CreateCommand())
        {
            outgoing.CommandText =
                $"""
                 SELECT rowid, detail, target_id FROM edges
                 WHERE kind = 'Calls' AND detail IS NOT NULL
                   AND source_id IN ({Placeholders(scopeIds.Count)})
                 """;
            var index = 0;
            foreach (var id in scopeIds)
            {
                outgoing.Parameters.AddWithValue("@" + index++.ToString(CultureInfo.InvariantCulture), id);
            }

            using var reader = outgoing.ExecuteReader();
            while (reader.Read())
            {
                pending.Add((reader.GetInt64(0), MethodOf(reader.GetString(1)), reader.GetString(2)));
            }
        }

        // Вызовы из других модулей к процедурам, которых раньше не было: их цели — внешние заглушки.
        if (addedRoutineNames.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var newRoutines = new HashSet<string>(addedRoutineNames, StringComparer.Ordinal);
            using var incoming = connection.CreateCommand();
            incoming.CommandText =
                "SELECT rowid, detail, target_id FROM edges WHERE kind = 'Calls' AND detail IS NOT NULL AND target_id LIKE 'call:%'";
            using var reader = incoming.ExecuteReader();
            while (reader.Read())
            {
                var method = MethodOf(reader.GetString(1));
                if (newRoutines.Contains(method.ToLowerInvariant()))
                {
                    pending.Add((reader.GetInt64(0), method, reader.GetString(2)));
                }
            }
        }

        if (pending.Count == 0)
        {
            return;
        }

        var targets = ResolveRoutineIds(connection, [.. pending.Select(static item => item.Method).Distinct(StringComparer.Ordinal)]);
        using var update = connection.CreateCommand();
        update.CommandText = "UPDATE edges SET target_id = @target WHERE rowid = @row";
        var target = update.Parameters.Add("@target", SqliteType.Text);
        var row = update.Parameters.Add("@row", SqliteType.Integer);
        var repaired = 0;
        foreach (var (rowId, method, current) in pending)
        {
            if (!targets.TryGetValue(method.ToLowerInvariant(), out var id)
                || string.Equals(id, current, StringComparison.Ordinal))
            {
                continue;
            }

            target.Value = id;
            row.Value = rowId;
            update.ExecuteNonQuery();
            repaired++;
        }

        if (repaired > 0)
        {
            using var note = connection.CreateCommand();
            note.CommandText = "INSERT OR REPLACE INTO meta (key, value) VALUES ('repaired_calls', @value)";
            note.Parameters.AddWithValue("@value", repaired.ToString(CultureInfo.InvariantCulture));
            note.ExecuteNonQuery();
        }
    }

    /// <summary>Имя метода из текста вызова: у «Модуль.Метод» берётся часть после последней точки.</summary>
    private static string MethodOf(string callee)
    {
        var separator = callee.LastIndexOf('.');
        return separator < 0 ? callee : callee[(separator + 1)..];
    }

    /// <summary>Узлы процедур по именам: первое совпадение по имени, как и при полной сборке.</summary>
    private static Dictionary<string, string> ResolveRoutineIds(SqliteConnection connection, IReadOnlyList<string> methods)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var lowered = methods.Select(static method => method.ToLowerInvariant()).Distinct(StringComparer.Ordinal).ToList();

        foreach (var chunk in Chunk(lowered, 400))
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                $"SELECT name_lower, id FROM nodes WHERE kind = 'Routine' AND name_lower IN ({Placeholders(chunk.Count)}) ORDER BY length(id), id";
            for (var index = 0; index < chunk.Count; index++)
            {
                command.Parameters.AddWithValue("@" + index.ToString(CultureInfo.InvariantCulture), chunk[index]);
            }

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                result.TryAdd(reader.GetString(0), reader.GetString(1));
            }
        }

        return result;
    }

    /// <summary>Пересчитывает счётчики в meta: частичная переиндексация меняет их неочевидно.</summary>
    private static void RecountCounters(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT OR REPLACE INTO meta (key, value) VALUES
                ('cnt_nodes', (SELECT COUNT(*) FROM nodes)),
                ('cnt_edges', (SELECT COUNT(*) FROM edges)),
                ('cnt_symbols', (SELECT COUNT(*) FROM symbols)),
                ('cnt_calls', (SELECT COUNT(*) FROM edges WHERE kind = 'Calls')),
                ('cnt_metadata_objects', (SELECT COUNT(*) FROM metadata_objects)),
                ('cnt_metadata_items', (SELECT COUNT(*) FROM metadata_items)),
                ('cnt_metadata_refs', (SELECT COUNT(*) FROM metadata_refs)),
                ('cnt_files', (SELECT COUNT(*) FROM files)),
                ('nodes', (SELECT COUNT(*) FROM nodes)),
                ('edges', (SELECT COUNT(*) FROM edges));
            """;
        command.ExecuteNonQuery();
    }

    private static List<string> ReadIds(SqliteConnection connection, Func<int, string> sql, IReadOnlyList<string> values)
    {
        var result = new List<string>();
        foreach (var chunk in Chunk(values, 400))
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql(chunk.Count);
            for (var index = 0; index < chunk.Count; index++)
            {
                command.Parameters.AddWithValue("@" + index.ToString(CultureInfo.InvariantCulture), chunk[index]);
            }

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                result.Add(reader.GetString(0));
            }
        }

        return result;
    }

    private static void ExecuteIds(SqliteConnection connection, Func<int, string> sql, IReadOnlyList<string> values)
    {
        foreach (var chunk in Chunk(values, 400))
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql(chunk.Count);
            for (var index = 0; index < chunk.Count; index++)
            {
                command.Parameters.AddWithValue("@" + index.ToString(CultureInfo.InvariantCulture), chunk[index]);
            }

            command.ExecuteNonQuery();
        }
    }

    private static string Placeholders(int count) =>
        string.Join(", ", Enumerable.Range(0, count).Select(static index => "@" + index.ToString(CultureInfo.InvariantCulture)));

    private static IEnumerable<List<string>> Chunk(IReadOnlyList<string> values, int size)
    {
        for (var offset = 0; offset < values.Count; offset += size)
        {
            yield return [.. values.Skip(offset).Take(size)];
        }
    }

    /// <summary>
    /// Счётчики пишутся в meta при сборке: инструмент status иначе считает миллионы строк живьём,
    /// а это полсекунды на каждый вызов.
    /// </summary>
    private static void WriteCounters(SqliteConnection connection, Counters counters, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR REPLACE INTO meta (key, value) VALUES (@key, @value)";
        var key = command.Parameters.Add("@key", SqliteType.Text);
        var value = command.Parameters.Add("@value", SqliteType.Text);

        foreach (var (name, count) in new (string Key, int Count)[]
        {
            ("cnt_nodes", counters.Nodes),
            ("cnt_edges", counters.Edges),
            ("cnt_symbols", counters.Symbols),
            ("cnt_calls", counters.Calls),
            ("cnt_metadata_objects", counters.MetadataObjects),
            ("cnt_metadata_items", counters.MetadataItems),
            ("cnt_metadata_refs", counters.MetadataRefs),
            ("cnt_files", counters.Files),
        })
        {
            cancellationToken.ThrowIfCancellationRequested();
            key.Value = name;
            value.Value = count.ToString(System.Globalization.CultureInfo.InvariantCulture);
            command.ExecuteNonQuery();
        }
    }

    private static void Clear(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            DELETE FROM nodes_fts;
            DELETE FROM terms_fts;
            DELETE FROM edges;
            DELETE FROM symbols;
            DELETE FROM metadata_refs;
            DELETE FROM metadata_items;
            DELETE FROM metadata_objects;
            DELETE FROM nodes;
            DELETE FROM files;
            DELETE FROM sqlite_sequence;
            """;
        command.ExecuteNonQuery();
    }

    private void WriteFiles(IDumpSource source, SqliteConnection connection, Counters counters, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR REPLACE INTO files (path, size, mtime) VALUES (@path, @size, @mtime)";
        var path = command.Parameters.Add("@path", SqliteType.Text);
        var size = command.Parameters.Add("@size", SqliteType.Integer);
        var mtime = command.Parameters.Add("@mtime", SqliteType.Integer);

        foreach (var file in source.EnumerateFiles())
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Файлы самого индекса лежат в каталоге выгрузки, но её файлами не являются:
            // иначе индекс считался бы устаревшим сразу после сборки.
            if (DumpState.IsServicePath(file.RelativePath))
            {
                continue;
            }

            path.Value = file.RelativePath;
            size.Value = file.Size;
            mtime.Value = file.LastWriteTimeUtc.ToUnixTimeMilliseconds();
            command.ExecuteNonQuery();
            counters.Files++;
        }
    }

    private void WriteNodes(IReadOnlyList<GraphNode> graphNodes, SqliteConnection connection, Counters counters, CancellationToken cancellationToken)
    {
        using var nodes = connection.CreateCommand();
        nodes.CommandText =
            """
            INSERT OR REPLACE INTO nodes
                (id, kind, name, name_lower, source_path, metadata_kind, is_external, platform_title, platform_version)
            VALUES (@id, @kind, @name, @nameLower, @sourcePath, @metadataKind, @isExternal, @platformTitle, @platformVersion)
            """;
        var id = nodes.Parameters.Add("@id", SqliteType.Text);
        var kind = nodes.Parameters.Add("@kind", SqliteType.Text);
        var name = nodes.Parameters.Add("@name", SqliteType.Text);
        var nameLower = nodes.Parameters.Add("@nameLower", SqliteType.Text);
        var sourcePath = nodes.Parameters.Add("@sourcePath", SqliteType.Text);
        var metadataKind = nodes.Parameters.Add("@metadataKind", SqliteType.Text);
        var isExternal = nodes.Parameters.Add("@isExternal", SqliteType.Integer);
        var platformTitle = nodes.Parameters.Add("@platformTitle", SqliteType.Text);
        var platformVersion = nodes.Parameters.Add("@platformVersion", SqliteType.Text);

        // Префиксный поиск по именам узлов ведёт таблица nodes_fts: без неё поиск вырождается
        // в просмотр всех узлов по LIKE.
        using var fts = connection.CreateCommand();
        fts.CommandText = "INSERT INTO nodes_fts (node_id, name) VALUES (@id, @name)";
        var ftsId = fts.Parameters.Add("@id", SqliteType.Text);
        var ftsName = fts.Parameters.Add("@name", SqliteType.Text);

        foreach (var node in graphNodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            id.Value = node.Id;
            kind.Value = node.Kind.ToString();
            name.Value = node.Name;
            nameLower.Value = node.Name.ToLowerInvariant();
            sourcePath.Value = (object?)node.SourcePath ?? DBNull.Value;
            metadataKind.Value = (object?)node.MetadataKind ?? DBNull.Value;
            isExternal.Value = node.IsExternal ? 1 : 0;
            platformTitle.Value = (object?)Tag(node, "platformTitle") ?? DBNull.Value;
            platformVersion.Value = (object?)Tag(node, "platformVersion") ?? DBNull.Value;
            nodes.ExecuteNonQuery();

            ftsId.Value = node.Id;
            ftsName.Value = node.Name;
            fts.ExecuteNonQuery();
            counters.Nodes++;
        }
    }

    private void WriteEdges(IReadOnlyList<GraphEdge> graphEdges, SqliteConnection connection, Counters counters, CancellationToken cancellationToken)
    {
        using var edges = connection.CreateCommand();
        edges.CommandText = "INSERT INTO edges (source_id, target_id, kind, line, detail) VALUES (@source, @target, @kind, @line, @detail)";
        var source = edges.Parameters.Add("@source", SqliteType.Text);
        var target = edges.Parameters.Add("@target", SqliteType.Text);
        var kind = edges.Parameters.Add("@kind", SqliteType.Text);
        var line = edges.Parameters.Add("@line", SqliteType.Integer);
        var detail = edges.Parameters.Add("@detail", SqliteType.Text);

        foreach (var edge in graphEdges)
        {
            cancellationToken.ThrowIfCancellationRequested();
            source.Value = edge.SourceId;
            target.Value = edge.TargetId;
            kind.Value = edge.Kind.ToString();
            line.Value = (object?)edge.Line ?? DBNull.Value;
            detail.Value = (object?)edge.Detail ?? DBNull.Value;
            edges.ExecuteNonQuery();
            counters.Edges++;

            // Отдельная таблица вызовов не нужна: вызов — это связь вида Calls,
            // а имя цели лежит в detail. Так индекс не дублирует 2,1 млн строк.
            if (edge.Kind == GraphEdgeKind.Calls)
            {
                counters.Calls++;
            }
        }
    }

    private void WriteSymbols(
        IDumpSource source,
        IReadOnlyList<BslModuleInfo> modules,
        SqliteConnection connection,
        Counters counters,
        CancellationToken cancellationToken)
    {
        using var insert = connection.CreateCommand();
        insert.CommandText =
            """
            INSERT INTO symbols
                (node_id, module_path, owner_id, name, name_lower, kind, is_export, start_line, end_line,
                 region, parameters, directives, comment_head)
            VALUES (@nodeId, @module, @owner, @name, @nameLower, @kind, @isExport, @start, @end,
                    @region, @parameters, @directives, @comment)
            """;
        var nodeId = insert.Parameters.Add("@nodeId", SqliteType.Text);
        var module = insert.Parameters.Add("@module", SqliteType.Text);
        var owner = insert.Parameters.Add("@owner", SqliteType.Text);
        var name = insert.Parameters.Add("@name", SqliteType.Text);
        var nameLower = insert.Parameters.Add("@nameLower", SqliteType.Text);
        var kind = insert.Parameters.Add("@kind", SqliteType.Text);
        var isExport = insert.Parameters.Add("@isExport", SqliteType.Integer);
        var start = insert.Parameters.Add("@start", SqliteType.Integer);
        var end = insert.Parameters.Add("@end", SqliteType.Integer);
        var region = insert.Parameters.Add("@region", SqliteType.Text);
        var parameters = insert.Parameters.Add("@parameters", SqliteType.Text);
        var directives = insert.Parameters.Add("@directives", SqliteType.Text);
        var comment = insert.Parameters.Add("@comment", SqliteType.Text);

        using var termsCommand = connection.CreateCommand();
        termsCommand.CommandText =
            "INSERT INTO terms_fts (symbol_id, name_tokens, comment_head, param_tokens) VALUES (@id, @name, @comment, @params)";
        var termsId = termsCommand.Parameters.Add("@id", SqliteType.Text);
        var termsName = termsCommand.Parameters.Add("@name", SqliteType.Text);
        var termsComment = termsCommand.Parameters.Add("@comment", SqliteType.Text);
        var termsParams = termsCommand.Parameters.Add("@params", SqliteType.Text);

        // Идентификатор символа нужен для связи с таблицей термов: берём фактический rowid,
        // а не порядковый номер, чтобы связь не разъехалась при любом изменении порядка вставки.
        using var lastId = connection.CreateCommand();
        lastId.CommandText = "SELECT last_insert_rowid()";

        foreach (var moduleInfo in modules)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var lines = IncludeComments ? ReadLines(source, moduleInfo.Path) : null;

            foreach (var routine in moduleInfo.Routines)
            {
                var identifier = $"routine:module:{moduleInfo.Path}#{routine.Name}";
                var commentHead = lines is null ? null : CommentHead(lines, routine.StartLine);

                nodeId.Value = identifier;
                module.Value = moduleInfo.Path;
                owner.Value = (object?)moduleInfo.OwnerId ?? DBNull.Value;
                name.Value = routine.Name;
                nameLower.Value = routine.Name.ToLowerInvariant();
                kind.Value = routine.Kind.ToString();
                isExport.Value = routine.IsExport ? 1 : 0;
                start.Value = routine.StartLine;
                end.Value = routine.EndLine;
                region.Value = (object?)routine.Region ?? DBNull.Value;
                parameters.Value = routine.Parameters.Count == 0 ? DBNull.Value : string.Join(", ", routine.Parameters);
                directives.Value = routine.Directives.Count == 0 ? DBNull.Value : string.Join(", ", routine.Directives);
                comment.Value = (object?)commentHead ?? DBNull.Value;
                insert.ExecuteNonQuery();
                var symbolId = Convert.ToInt64(lastId.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);

                termsId.Value = symbolId.ToString(System.Globalization.CultureInfo.InvariantCulture);
                termsName.Value = Tokenize(routine.Name);
                termsComment.Value = commentHead ?? string.Empty;
                termsParams.Value = routine.Parameters.Count == 0 ? string.Empty : Tokenize(string.Join(' ', routine.Parameters));
                termsCommand.ExecuteNonQuery();

                counters.Symbols++;
            }
        }
    }

    private void WriteMetadata(AnalysisResult result, SqliteConnection connection, Counters counters, CancellationToken cancellationToken)
    {
        using var objects = connection.CreateCommand();
        objects.CommandText =
            """
            INSERT OR REPLACE INTO metadata_objects
                (id, kind, name, name_lower, synonym, uuid, source_path, comment, is_top_level, parent_id, properties)
            VALUES (@id, @kind, @name, @nameLower, @synonym, @uuid, @path, @comment, @top, @parent, @properties)
            """;
        var objectId = objects.Parameters.Add("@id", SqliteType.Text);
        var objectKind = objects.Parameters.Add("@kind", SqliteType.Text);
        var objectName = objects.Parameters.Add("@name", SqliteType.Text);
        var objectNameLower = objects.Parameters.Add("@nameLower", SqliteType.Text);
        var synonym = objects.Parameters.Add("@synonym", SqliteType.Text);
        var uuid = objects.Parameters.Add("@uuid", SqliteType.Text);
        var objectPath = objects.Parameters.Add("@path", SqliteType.Text);
        var objectComment = objects.Parameters.Add("@comment", SqliteType.Text);
        var objectTopLevel = objects.Parameters.Add("@top", SqliteType.Integer);
        var objectParent = objects.Parameters.Add("@parent", SqliteType.Text);
        var objectProperties = objects.Parameters.Add("@properties", SqliteType.Text);

        using var items = connection.CreateCommand();
        items.CommandText =
            "INSERT INTO metadata_items (object_id, kind, name, name_lower, type_info, parent_id, synonym, comment) "
            + "VALUES (@object, @kind, @name, @nameLower, @type, @parent, @synonym, @comment)";
        var itemObject = items.Parameters.Add("@object", SqliteType.Text);
        var itemKind = items.Parameters.Add("@kind", SqliteType.Text);
        var itemName = items.Parameters.Add("@name", SqliteType.Text);
        var itemNameLower = items.Parameters.Add("@nameLower", SqliteType.Text);
        var itemType = items.Parameters.Add("@type", SqliteType.Text);
        var itemParent = items.Parameters.Add("@parent", SqliteType.Text);
        var itemSynonym = items.Parameters.Add("@synonym", SqliteType.Text);
        var itemComment = items.Parameters.Add("@comment", SqliteType.Text);

        using var refs = connection.CreateCommand();
        refs.CommandText = "INSERT INTO metadata_refs (source_id, target_id, context, line, detail) VALUES (@source, @target, @context, NULL, @detail)";
        var refSource = refs.Parameters.Add("@source", SqliteType.Text);
        var refTarget = refs.Parameters.Add("@target", SqliteType.Text);
        var refContext = refs.Parameters.Add("@context", SqliteType.Text);
        var refDetail = refs.Parameters.Add("@detail", SqliteType.Text);

        foreach (var obj in result.Metadata.Objects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (obj.Kind == MdKind.Configuration)
            {
                continue;
            }

            objectId.Value = obj.Id;
            objectKind.Value = obj.Kind.Name;
            objectName.Value = obj.Name;
            objectNameLower.Value = obj.Name.ToLowerInvariant();
            synonym.Value = (object?)obj.Synonym ?? DBNull.Value;
            uuid.Value = obj.Uuid is { } value ? value.ToString() : DBNull.Value;
            // Путь файла объекта, как его показывает карточка: по нему агент читает и правит XML.
            objectPath.Value = (object?)obj.SourcePath ?? DBNull.Value;
            objectComment.Value = (object?)obj.Comment ?? DBNull.Value;
            objectTopLevel.Value = obj.IsTopLevel ? 1 : 0;
            objectParent.Value = obj.Parent is null || obj.Parent.Kind == MdKind.Configuration
                ? DBNull.Value
                : obj.Parent.Id;
            objectProperties.Value = (object?)Properties(obj) ?? DBNull.Value;
            objects.ExecuteNonQuery();
            counters.MetadataObjects++;

            // Ссылки объекта: типы реквизитов, содержимое подсистем, права ролей, формы и макеты.
            foreach (var reference in obj.References)
            {
                refSource.Value = obj.Id;
                refTarget.Value = reference.TargetId;
                refContext.Value = ResourceContext(reference.Kind);
                refDetail.Value = (object?)reference.Detail ?? DBNull.Value;
                refs.ExecuteNonQuery();
                counters.MetadataRefs++;
            }

            foreach (var child in obj.Children)
            {
                itemObject.Value = obj.Id;
                itemKind.Value = child.Kind.Name;
                itemName.Value = child.Name;
                itemNameLower.Value = child.Name.ToLowerInvariant();
                itemType.Value = (object?)TypeInfo(obj, child) ?? DBNull.Value;
                itemParent.Value = child.Parent is null || child.Parent.Kind == MdKind.Configuration ? DBNull.Value : child.Parent.Id;
                itemSynonym.Value = (object?)child.Synonym ?? DBNull.Value;
                itemComment.Value = (object?)child.Comment ?? DBNull.Value;
                items.ExecuteNonQuery();
                counters.MetadataItems++;
            }
        }

        // Обращения к метаданным из кода: отдельный контекст, чтобы отличать их от типов и прав.
        using var codeRefs = connection.CreateCommand();
        codeRefs.CommandText =
            "INSERT INTO metadata_refs (source_id, target_id, context, line, detail) VALUES (@source, @target, 'code', @line, @detail)";
        var codeSource = codeRefs.Parameters.Add("@source", SqliteType.Text);
        var codeTarget = codeRefs.Parameters.Add("@target", SqliteType.Text);
        var codeLine = codeRefs.Parameters.Add("@line", SqliteType.Integer);
        var codeDetail = codeRefs.Parameters.Add("@detail", SqliteType.Text);

        foreach (var edge in result.Graph.Edges)
        {
            if (edge.Kind != GraphEdgeKind.UsesMetadata)
            {
                continue;
            }

            codeSource.Value = edge.SourceId;
            codeTarget.Value = edge.TargetId;
            codeLine.Value = (object?)edge.Line ?? DBNull.Value;
            codeDetail.Value = (object?)edge.Detail ?? DBNull.Value;
            codeRefs.ExecuteNonQuery();
            counters.MetadataRefs++;
        }
    }

    private static string? Tag(GraphNode node, string key) =>
        node.Tags is not null && node.Tags.TryGetValue(key, out var value) ? value : null;

    private static string ResourceContext(MdReferenceKind kind) => kind switch
    {
        MdReferenceKind.Type => "type",
        MdReferenceKind.Content => "content",
        MdReferenceKind.Field => "field",
        MdReferenceKind.Form => "form",
        MdReferenceKind.Template => "template",
        MdReferenceKind.Command => "command",
        MdReferenceKind.RoleRight => "rights",
        MdReferenceKind.EventSource => "event",
        _ => "other",
    };

    /// <summary>Свойства объекта метаданных одним JSON-объектом: их читает карточка объекта.</summary>
    private static string? Properties(MdObject obj)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in obj.Properties)
        {
            if (!string.IsNullOrWhiteSpace(property.Value) && values.Count < 40)
            {
                values[property.Key] = property.Value;
            }
        }

        return values.Count == 0 ? null : JsonSerializer.Serialize(values);
    }

    /// <summary>
    /// Тип реквизита: сначала собственные ссылки вложенного объекта (в XML тип описан внутри самого
    /// реквизита), затем — ссылки владельца, отобранные по имени реквизита.
    /// </summary>
    private static string? TypeInfo(MdObject owner, MdObject child)
    {
        var types = new List<string>();
        Collect(child.References, static _ => true);
        Collect(
            owner.References,
            reference => string.IsNullOrEmpty(reference.Detail)
                || reference.Detail!.Contains(child.Name, StringComparison.OrdinalIgnoreCase));

        return types.Count == 0 ? null : string.Join(", ", types);

        void Collect(IReadOnlyList<MdReference> references, Func<MdReference, bool> matches)
        {
            foreach (var reference in references)
            {
                if (types.Count >= 8)
                {
                    return;
                }

                if (reference.Kind == MdReferenceKind.Type
                    && matches(reference)
                    && !types.Contains(reference.TargetId, StringComparer.Ordinal))
                {
                    types.Add(reference.TargetId);
                }
            }
        }
    }

    private static string[]? ReadLines(IDumpSource source, string path)
    {
        try
        {
            using var stream = source.OpenRead(new DumpFile(path, 0, default));
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var text = reader.ReadToEnd();
            return text.Split('\n');
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or KeyNotFoundException)
        {
            return null;
        }
    }

    /// <summary>До трёх строк комментария непосредственно над процедурой.</summary>
    private static string? CommentHead(string[] lines, int startLine)
    {
        var collected = new List<string>(3);
        for (var index = startLine - 2; index >= 0 && collected.Count < 3; index--)
        {
            if (index >= lines.Length)
            {
                continue;
            }

            var line = lines[index].Trim();
            if (line.Length == 0 || line.StartsWith('&'))
            {
                continue;
            }

            if (!line.StartsWith("//", StringComparison.Ordinal))
            {
                break;
            }

            collected.Add(line.TrimStart('/').Trim());
        }

        if (collected.Count == 0)
        {
            return null;
        }

        collected.Reverse();
        return string.Join(' ', collected);
    }

    /// <summary>Разбивает идентификатор на слова: «ЗначениеРеквизитаОбъекта» → «значение реквизит объекта».</summary>
    internal static string Tokenize(string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(identifier.Length + 8);
        for (var index = 0; index < identifier.Length; index++)
        {
            var current = identifier[index];
            if (current is '_' or '.' or '(' or ')' or ',')
            {
                builder.Append(' ');
                continue;
            }

            if (index > 0 && char.IsUpper(current) && !char.IsUpper(identifier[index - 1]))
            {
                builder.Append(' ');
            }

            builder.Append(char.ToLowerInvariant(current));
        }

        return builder.ToString();
    }

    private static string LastSegment(string id)
    {
        var separator = id.LastIndexOfAny(['.', '/', '#', ':']);
        return separator < 0 || separator == id.Length - 1 ? id : id[(separator + 1)..];
    }

    private sealed class Counters
    {
        internal int Nodes;
        internal int Edges;
        internal int Symbols;
        internal int Calls;
        internal int MetadataObjects;
        internal int MetadataItems;
        internal int MetadataRefs;
        internal int Files;
    }
}
