using System.Globalization;
using Data1c.Core.Analysis;
using Data1c.Core.Graph;
using Microsoft.Data.Sqlite;

namespace Data1c.Store;

/// <summary>
/// Кандидаты для поиска похожего кода (Э2-5) из SQLite-индекса выгрузки: по признакам черновика
/// выбираются процедуры с теми же вызовами платформы, обращениями к метаданным и вызовами процедур,
/// а близость термов считается по <c>terms_fts</c> (BM25).
/// </summary>
/// <remarks>
/// <para>Полного просмотра процедур нет. Признак, встречающийся более чем у <see cref="SelectiveShare"/>
/// процедур, не различает реализации и в выборку не попадает: «Структура.Вставить» вызывают
/// 236 тысяч раз, и кандидаты по нему — это случайная выборка из конфигурации. Поэтому выборка
/// начинается с самых редких признаков черновика, а частые пропускаются с пояснением.</para>
/// <para>Вес признака — обратная частота: <c>log(всего процедур / процедур с признаком)</c>.
/// Он считается здесь, потому что только здесь известны частоты по индексу.</para>
/// <para>Запросы идут по существующим индексам схемы: <c>idx_edges_target</c> — вызовы платформы,
/// <c>idx_refs_target</c> — обращения к метаданным, <c>idx_edges_calls_detail</c> — вызовы процедур,
/// <c>idx_symbols_node</c> и <c>idx_edges_source</c> — признаки выбранных кандидатов.
/// Схема индекса при этом не меняется.</para>
/// </remarks>
public sealed class IndexSimilarCodeSource : ISimilarCodeSource
{
    /// <summary>Доля процедур, после которой признак считается частым и не различает реализации.</summary>
    private const int SelectiveShare = 50;

    /// <summary>Наименьший предел частоты: на маленьком индексе значимыми остаются все признаки.</summary>
    private const int SelectiveFloor = 32;

    /// <summary>Сколько признаков одной категории проверяется: остальные отбрасываются.</summary>
    private const int MaxFeaturesPerCategory = 24;

    /// <summary>Сколько термов уходит в запрос к terms_fts: длинные термы точнее.</summary>
    private const int MaxTermsTokens = 8;

    /// <summary>Сколько кандидатов можно запросить у источника.</summary>
    private const int MaxCandidates = 5000;

    /// <summary>Сколько идентификаторов помещается в один запрос: предел числа параметров SQLite.</summary>
    private const int ChunkSize = 400;

    /// <summary>Сколько идентификаторов символов может вернуть поиск по термам.</summary>
    private const int TermsRowLimit = 2000;

    private static readonly string PlatformSql =
        """
        SELECT source_id FROM edges
        WHERE kind = 'Calls' AND target_id = @value AND source_id LIKE 'routine:%'
        LIMIT @cap
        """;

    private static readonly string MetadataSql =
        $"""
         SELECT source_id FROM metadata_refs
         WHERE target_id = @value AND context IN ('{MetadataRefContexts.Code}', '{MetadataRefContexts.Query}')
           AND source_id LIKE 'routine:%'
         LIMIT @cap
         """;

    private static readonly string RoutineSql =
        """
        SELECT source_id FROM edges
        WHERE kind = 'Calls' AND detail = @value AND source_id LIKE 'routine:%'
        LIMIT @cap
        """;

    private static readonly string TermsSql =
        """
        SELECT s.node_id, bm25(terms_fts)
        FROM terms_fts
        JOIN symbols s ON s.id = CAST(terms_fts.symbol_id AS INTEGER)
        WHERE terms_fts MATCH @match
        ORDER BY bm25(terms_fts)
        LIMIT @limit
        """;

    private readonly SqliteIndex _index;

    /// <summary>Создаёт источник поверх соединения с индексом.</summary>
    /// <param name="index">Открытый индекс выгрузки: чтение идёт под его замком.</param>
    public IndexSimilarCodeSource(SqliteIndex index)
    {
        ArgumentNullException.ThrowIfNull(index);
        _index = index;
    }

    /// <inheritdoc/>
    public SimilarCodeCandidates FindCandidates(SimilarCodeFeatures features, int candidateLimit, string? excludeId)
    {
        ArgumentNullException.ThrowIfNull(features);
        var bounded = Math.Clamp(candidateLimit, 1, MaxCandidates);
        return _index.WithLock(() => Find(features, bounded, excludeId));
    }

    private SimilarCodeCandidates Find(SimilarCodeFeatures features, int limit, string? excludeId)
    {
        var total = SymbolCount();

        // Признак, встречающийся чаще, чем у каждой пятидесятой процедуры, не различает реализации.
        var cap = Math.Max(SelectiveFloor, (int)Math.Min(int.MaxValue, total / SelectiveShare));

        var notes = new List<string>();
        var weights = new Dictionary<SimilarCodeSignal, IReadOnlyDictionary<string, double>>();
        var recognized = new Dictionary<SimilarCodeSignal, IReadOnlyList<string>>();
        var probes = new List<Probe>();

        foreach (var signal in new[] { SimilarCodeSignal.PlatformCall, SimilarCodeSignal.MetadataReference, SimilarCodeSignal.RoutineCall })
        {
            var (sql, prefix, sourceFeatures) = signal switch
            {
                SimilarCodeSignal.PlatformCall => (PlatformSql, "platform:", features.PlatformCalls),
                SimilarCodeSignal.MetadataReference => (MetadataSql, (string?)null, features.MetadataReferences),
                _ => (RoutineSql, (string?)null, features.RoutineCalls),
            };

            var (probe, skipped, found) = Check(sql, prefix, sourceFeatures, cap, total);
            recognized[signal] = found;
            if (probe is not null)
            {
                probes.Add(probe);
                weights[signal] = probe.Weights;
            }

            if (skipped.Count > 0)
            {
                notes.Add($"Признаки, встречающиеся более чем у {100.0 / SelectiveShare:0.#}% процедур, "
                    + $"не учитывались как неразличающие: {string.Join(", ", skipped.Take(5))}"
                    + (skipped.Count > 5 ? " и другие" : string.Empty) + ".");
            }
        }

        var terms = ReadTerms(features, limit);
        var candidates = Collect(probes, terms, limit, excludeId);
        var profiles = ReadProfiles(candidates, terms.Scores);

        if (probes.Count == 0)
        {
            notes.Add("Редких признаков в черновике нет — кандидаты набраны только по смыслу имени, "
                + "комментария и параметров (terms_fts). Добавьте в код характерные вызовы или обращения к метаданным.");
        }

        if (candidates.Count >= limit)
        {
            notes.Add($"Кандидатов оказалось больше предела ({limit}): рассмотрены самые редкие признаки.");
        }

        return new SimilarCodeCandidates(profiles, weights, notes, recognized);
    }

    /// <summary>
    /// Проверяет признаки одной категории: возвращает частоты значимых признаков и процедуры,
    /// которые их используют. Признак, у которого строк не меньше предела, считается частым.
    /// </summary>
    private (Probe? Probe, List<string> Skipped, IReadOnlyList<string> Recognized) Check(
        string sql,
        string? prefix,
        IReadOnlyList<SimilarCodeFeature> features,
        int cap,
        long total)
    {
        var weights = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var sources = new HashSet<string>(StringComparer.Ordinal);
        var skipped = new List<string>();
        var recognized = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var feature in features)
        {
            if (seen.Count >= MaxFeaturesPerCategory)
            {
                break;
            }

            if (!seen.Add(feature.Value))
            {
                continue;
            }

            using var command = _index.CreateCommand(sql);
            command.Parameters.AddWithValue("@value", prefix is null ? feature.Value : prefix + feature.Value);
            command.Parameters.AddWithValue("@cap", cap);

            using var reader = command.ExecuteReader();
            var rows = new List<string>(Math.Min(cap, 64));
            while (rows.Count < cap && reader.Read())
            {
                rows.Add(reader.GetString(0));
            }

            if (rows.Count >= cap)
            {
                // Признак частый, но он есть в конфигурации: для отчёта о черновике это важно.
                recognized.Add(feature.Value);
                skipped.Add(feature.Value);
                continue;
            }

            var distinct = new HashSet<string>(rows, StringComparer.Ordinal);
            if (distinct.Count == 0)
            {
                continue;
            }

            recognized.Add(feature.Value);
            weights[feature.Value] = Math.Log(total / (double)distinct.Count);
            sources.UnionWith(distinct);
        }

        return weights.Count == 0
            ? (null, skipped, recognized)
            : (new Probe(weights, sources), skipped, recognized);
    }

    /// <summary>
    /// Поиск по термам имени, шапки комментария и параметров: BM25 по <c>terms_fts</c>.
    /// Оценки приводятся к отрезку 0…1 по лучшему совпадению выборки.
    /// </summary>
    private Terms ReadTerms(SimilarCodeFeatures features, int limit)
    {
        var tokens = features.Terms
            .Where(static token => token.Length >= 3)
            .Where(static token => token.All(char.IsLetterOrDigit))
            .Distinct(StringComparer.Ordinal)
            .Take(MaxTermsTokens)
            .ToList();

        if (tokens.Count == 0)
        {
            return new Terms([], new Dictionary<string, double>(StringComparer.Ordinal));
        }

        using var command = _index.CreateCommand(TermsSql);
        command.Parameters.AddWithValue("@match", string.Join(" OR ", tokens.Select(static token => token + "*")));
        command.Parameters.AddWithValue("@limit", Math.Clamp(limit * 2, 32, TermsRowLimit));

        using var reader = command.ExecuteReader();
        var found = new List<(string NodeId, double Rank)>();
        while (reader.Read())
        {
            var rank = -reader.GetDouble(1);
            if (rank > 0 && !reader.IsDBNull(0))
            {
                // Чем меньше bm25 (то есть чем больше его модуль), тем ближе термы.
                found.Add((reader.GetString(0), rank));
            }
        }

        if (found.Count == 0)
        {
            return new Terms([], new Dictionary<string, double>(StringComparer.Ordinal));
        }

        var best = found.Max(static item => item.Rank);
        var scores = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (nodeId, rank) in found)
        {
            scores[nodeId] = Math.Min(1, rank / best);
        }

        return new Terms([.. found.Select(static item => item.NodeId)], scores);
    }

    /// <summary>
    /// Набор кандидатов: сначала процедуры с самыми редкими признаками, затем совпадения по термам.
    /// Предел числа кандидатов ограничивает работу: дальше читаются признаки только этих процедур.
    /// </summary>
    private static List<string> Collect(IReadOnlyList<Probe> probes, Terms terms, int limit, string? excludeId)
    {
        var candidates = new List<string>(Math.Min(limit, 256));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (excludeId is not null)
        {
            seen.Add(excludeId);
        }

        // Самые редкие признаки первыми: они и различают реализации.
        foreach (var probe in probes.OrderBy(static probe => probe.Sources.Count))
        {
            foreach (var source in probe.Sources)
            {
                if (candidates.Count >= limit)
                {
                    break;
                }

                if (seen.Add(source))
                {
                    candidates.Add(source);
                }
            }

            if (candidates.Count >= limit)
            {
                break;
            }
        }

        foreach (var nodeId in terms.NodeIds)
        {
            if (candidates.Count >= limit)
            {
                break;
            }

            if (seen.Add(nodeId))
            {
                candidates.Add(nodeId);
            }
        }

        return candidates;
    }

    /// <summary>
    /// Признаки выбранных процедур: строки символов, вызовы и обращения к метаданным. Читаются
    /// порциями по <see cref="ChunkSize"/> идентификаторов, чтобы не упереться в предел параметров.
    /// </summary>
    private List<SimilarCodeProfile> ReadProfiles(IReadOnlyList<string> ids, IReadOnlyDictionary<string, double> terms)
    {
        var symbols = new List<SymbolRow>();
        var platforms = new Dictionary<string, List<SimilarCodeFeature>>(StringComparer.Ordinal);
        var routines = new Dictionary<string, List<SimilarCodeFeature>>(StringComparer.Ordinal);
        var references = new Dictionary<string, List<SimilarCodeFeature>>(StringComparer.Ordinal);
        var statements = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var chunk in Chunk(ids, ChunkSize))
        {
            var placeholders = string.Join(", ", chunk.Select(static (_, index) => "@c" + index.ToString(CultureInfo.InvariantCulture)));

            using (var command = _index.CreateCommand(
                $"""
                 SELECT id, node_id, module_path, owner_id, name, kind, is_export, start_line, end_line,
                        region, parameters, comment_head
                 FROM symbols WHERE node_id IN ({placeholders})
                 """))
            {
                AddParameters(command, chunk);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    symbols.Add(new SymbolRow(
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
            }

            using (var command = _index.CreateCommand(
                $"SELECT source_id, target_id, detail FROM edges WHERE kind = 'Calls' AND source_id IN ({placeholders})"))
            {
                AddParameters(command, chunk);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var source = reader.GetString(0);
                    var target = reader.GetString(1);
                    statements[source] = statements.GetValueOrDefault(source) + 1;

                    if (target.StartsWith("platform:", StringComparison.Ordinal))
                    {
                        Add(platforms, source, new SimilarCodeFeature(target["platform:".Length..]));
                    }
                    else if (target.StartsWith("routine:", StringComparison.Ordinal))
                    {
                        // Значение признака — текст вызова: он же лежит в detail и по нему идёт выборка.
                        var detail = reader.IsDBNull(2) ? target : reader.GetString(2);
                        Add(routines, source, new SimilarCodeFeature(detail));
                    }
                }
            }

            using (var command = _index.CreateCommand(
                $"""
                 SELECT source_id, target_id, MAX(context) FROM metadata_refs
                 WHERE context IN ('{MetadataRefContexts.Code}', '{MetadataRefContexts.Query}')
                   AND source_id IN ({placeholders})
                 GROUP BY source_id, target_id
                 """))
            {
                AddParameters(command, chunk);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var source = reader.GetString(0);
                    statements[source] = statements.GetValueOrDefault(source) + 1;
                    Add(references, source, new SimilarCodeFeature(reader.GetString(1), reader.GetString(2)));
                }
            }
        }

        var profiles = new List<SimilarCodeProfile>(symbols.Count);
        foreach (var symbol in symbols)
        {
            profiles.Add(new SimilarCodeProfile(
                symbol.NodeId,
                symbol.Name,
                symbol.ModulePath,
                symbol.OwnerId,
                symbol.StartLine,
                symbol.EndLine,
                statements.GetValueOrDefault(symbol.NodeId),
                Distinct(platforms, symbol.NodeId),
                Distinct(references, symbol.NodeId),
                Distinct(routines, symbol.NodeId),
                terms.TryGetValue(symbol.NodeId, out var score) ? score : 0,
                symbol.CommentHead,
                symbol.Parameters));
        }

        return profiles;
    }

    /// <summary>Признаки процедуры без повторов: сравниваются наборы, а не число вызовов.</summary>
    private static IReadOnlyList<SimilarCodeFeature> Distinct(
        Dictionary<string, List<SimilarCodeFeature>> bySource,
        string sourceId)
    {
        if (!bySource.TryGetValue(sourceId, out var features))
        {
            return [];
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<SimilarCodeFeature>(features.Count);
        foreach (var feature in features)
        {
            if (seen.Add(feature.Value))
            {
                result.Add(feature);
            }
        }

        return result;
    }

    private static void Add(Dictionary<string, List<SimilarCodeFeature>> bySource, string sourceId, SimilarCodeFeature feature)
    {
        if (!bySource.TryGetValue(sourceId, out var list))
        {
            list = [];
            bySource[sourceId] = list;
        }

        list.Add(feature);
    }

    private static void AddParameters(SqliteCommand command, IReadOnlyList<string> values)
    {
        for (var index = 0; index < values.Count; index++)
        {
            command.Parameters.AddWithValue("@c" + index.ToString(CultureInfo.InvariantCulture), values[index]);
        }
    }

    private static IEnumerable<IReadOnlyList<string>> Chunk(IReadOnlyList<string> values, int size)
    {
        for (var offset = 0; offset < values.Count; offset += size)
        {
            var take = Math.Min(size, values.Count - offset);
            var chunk = new string[take];
            for (var index = 0; index < take; index++)
            {
                chunk[index] = values[offset + index];
            }

            yield return chunk;
        }
    }

    /// <summary>Сколько процедур в индексе: нужно для веса признаков (обратная частота).</summary>
    private long SymbolCount()
    {
        var value = _index.GetMeta("cnt_symbols");
        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed
            : _index.QueryScalar("SELECT COUNT(*) FROM symbols");
    }

    /// <summary>Признаки одной категории: веса значений и процедуры, у которых они есть.</summary>
    private sealed record Probe(
        IReadOnlyDictionary<string, double> Weights,
        IReadOnlySet<string> Sources);

    /// <summary>Совпадения по термам: процедуры по убыванию BM25 и приведённые оценки.</summary>
    private sealed record Terms(
        IReadOnlyList<string> NodeIds,
        IReadOnlyDictionary<string, double> Scores);
}
