using System.Text;
using Data1c.Core.Bsl;
using Data1c.Core.Graph;
using Data1c.Core.Metadata;

namespace Data1c.Core.Analysis;

/// <summary>
/// Поиск похожих реализаций в конфигурации (этап Э2-5): по черновику кода или по существующей
/// процедуре находит процедуры, устроенные так же. Нужен, чтобы переиспользовать готовый код,
/// а не писать заново.
/// </summary>
/// <remarks>
/// <para>Похожесть считается по признакам, а не по тексту: совпадение вызовов методов платформы,
/// обращений к объектам метаданных (включая тексты запросов), вызовов процедур конфигурации и
/// близость имени с термами. Поэтому «похоже по смыслу» отличается от «похоже по буквам»:
/// две процедуры, читающие один справочник и вызывающие один и тот же метод платформы, похожи,
/// даже если написаны разными словами.</para>
/// <para>Вызов процедуры конфигурации сравнивается по идентификатору разрешённой цели, а не по
/// тексту вызова: «Справочники.Товары.НайтиПоНаименованию» и «Товары.НайтиПоНаименованию» ведут
/// в один узел, и квалификация на совпадение не влияет. Вызовы, цель которых разрешить не удалось
/// (узлы <c>call:…</c>), дают отдельный слабый сигнал: у них сравнивается только имя метода
/// без квалификатора.</para>
/// <para>Внутри категории признаки взвешиваются обратной частотой: вес признака считает источник
/// данных (<see cref="ISimilarCodeSource"/>), а частые признаки («Структура.Вставить») веса не
/// получают вовсе — иначе любой черновик находил бы тысячи «похожих» процедур.</para>
/// <para>Похожесть категории — это доля значимых признаков черновика, покрытая кандидатом, с мягкой
/// поправкой на лишние признаки кандидата: процедура, вызывающая всё подряд, не должна выигрывать
/// только за счёт объёма. Объём (строки и операторы) даёт слабый штраф к итоговой оценке.</para>
/// </remarks>
public sealed class SimilarCode
{
    private readonly ISimilarCodeSource _source;
    private readonly SimilarCodeOptions _options;

    /// <summary>Создаёт поиск поверх источника кандидатов.</summary>
    /// <param name="source">Источник процедур-кандидатов: SQLite-индекс или разбор в памяти.</param>
    /// <param name="options">Веса и пределы; по умолчанию — значения, подобранные по замеру на выгрузке 2,9 ГБ.</param>
    public SimilarCode(ISimilarCodeSource source, SimilarCodeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        _source = source;
        _options = options ?? new SimilarCodeOptions();
    }

    /// <summary>
    /// Ищет похожие реализации по тексту черновика. Вызовы процедур конфигурации при этом остаются
    /// слабым сигналом: разрешить их в узлы-цели можно только по индексу
    /// (<see cref="Describe(string, string?, RoutineCallResolver?)"/> с разрешителем источника).
    /// </summary>
    /// <param name="text">Текст черновика или процедуры.</param>
    /// <param name="limit">Сколько кандидатов вернуть (1…<see cref="SimilarCodeOptions.MaxLimit"/>).</param>
    /// <param name="excludeId">Процедура, которую нужно исключить из выдачи (обычно сам черновик).</param>
    public SimilarCodeResult Find(string text, int limit = 10, string? excludeId = null) =>
        Find(Describe(text), limit, excludeId);

    /// <summary>Ищет похожие реализации по готовым признакам.</summary>
    /// <param name="features">Признаки, собранные из черновика.</param>
    /// <param name="limit">Сколько кандидатов вернуть.</param>
    /// <param name="excludeId">Процедура, которую нужно исключить из выдачи.</param>
    public SimilarCodeResult Find(SimilarCodeFeatures features, int limit = 10, string? excludeId = null)
    {
        ArgumentNullException.ThrowIfNull(features);
        var bounded = Math.Clamp(limit, 1, _options.MaxLimit);
        var found = _source.FindCandidates(features, _options.CandidateLimit, excludeId);

        var scored = new List<SimilarCodeCandidate>();
        foreach (var profile in found.Profiles)
        {
            if (excludeId is not null && string.Equals(profile.Id, excludeId, StringComparison.Ordinal))
            {
                continue;
            }

            if (Score(features, profile, found.Weights) is { } candidate)
            {
                scored.Add(candidate);
            }
        }

        var ordered = scored
            .OrderByDescending(static candidate => candidate.Score)
            .ThenBy(static candidate => candidate.Lines)
            .ThenBy(static candidate => candidate.Name, StringComparer.Ordinal)
            .Take(bounded)
            .ToList();

        var notes = new List<string>(found.Notes);
        if (ordered.Count == 0)
        {
            notes.Add(found.Profiles.Count == 0
                ? "Общих признаков с процедурами конфигурации не нашлось: совпадений по вызовам, "
                    + "обращениям к метаданным и термам имени нет."
                : $"Из {found.Profiles.Count} рассмотренных кандидатов ни один не набрал оценку {_options.MinScore:0.00}.");
        }

        if (ordered.Any(static candidate =>
            candidate.Matches.Any(static match => match.Signal == SimilarCodeSignal.UnresolvedCall)))
        {
            notes.Add("Часть совпадений — по вызовам с неразрешённой целью (call:…): сравнивалось только "
                + "имя метода без квалификатора. Это слабый сигнал: одноимённые методы есть у разных "
                + "модулей и объектов, самостоятельно он кандидатов не находит.");
        }

        return new SimilarCodeResult(ordered, features, found.Profiles.Count, notes, found.Recognized);
    }

    /// <summary>
    /// Разбирает фрагмент кода и собирает признаки для сравнения. Вызовы методов платформы
    /// определяются по выведенному типу переменной, как и при сборке графа: «Таблица.Свернуть»
    /// при типе «ТаблицаЗначений» — это <c>platform:ТаблицаЗначений.Свернуть</c>.
    /// </summary>
    /// <param name="text">Текст черновика или процедуры.</param>
    /// <param name="path">Путь модуля для сообщений разбора; по умолчанию — «черновик.bsl».</param>
    /// <param name="resolve">
    /// Разрешение вызова в узел процедуры конфигурации. Обычно его даёт индекс
    /// (<c>IndexReader.SimilarCodeSource</c>): признак вызова процедуры — это идентификатор цели,
    /// поэтому квалификация («ОбщийМодуль.Метод» и «Метод» внутри того же модуля) на совпадение
    /// не влияет. Без разрешителя вызовы процедур конфигурации остаются слабым сигналом.
    /// </param>
    public static SimilarCodeFeatures Describe(
        string text,
        string? path = null,
        RoutineCallResolver? resolve = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        var module = new BslModuleParser().Parse(
            new BslModuleSource(string.IsNullOrWhiteSpace(path) ? DraftCheck.DefaultModulePath : path, text));

        var calls = module.Calls
            .Concat(module.Routines.SelectMany(static routine => routine.Calls))
            .ToList();

        var platform = new List<SimilarCodeFeature>();
        var routines = new List<SimilarCodeFeature>();
        var unresolved = new List<SimilarCodeFeature>();
        var resolved = new Dictionary<(string? Qualifier, string Method), string?>();
        foreach (var call in calls)
        {
            if (module.Types is not null && module.Types.TryGetPlatformCallee(call, out var platformCallee))
            {
                // Тип переменной известен: это метод платформы, а не процедура конфигурации.
                platform.Add(new SimilarCodeFeature(platformCallee));
                continue;
            }

            // Вызов без квалификатора может быть и глобальной функцией платформы, и процедурой
            // этого же модуля: в графе это решает узел-цель, поэтому признак идёт в обе категории.
            platform.Add(new SimilarCodeFeature(call.Qualifier is null ? call.Method : call.Callee));

            // Признак вызова процедуры конфигурации — идентификатор разрешённой цели, а не текст
            // вызова: «ОбщийМодуль.Метод» и «Метод» внутри этого модуля ведут в один узел.
            var key = (call.Qualifier, call.Method);
            if (!resolved.TryGetValue(key, out var target))
            {
                target = resolve?.Invoke(call.Qualifier, call.Method);
                resolved[key] = target;
            }

            if (target is { Length: > 0 })
            {
                routines.Add(new SimilarCodeFeature(target, call.Callee));
            }
            else
            {
                // Цель не разрешилась: остаётся слабый сигнал — имя метода без квалификатора.
                unresolved.Add(new SimilarCodeFeature(CallName(call.Callee), call.Callee));
            }
        }

        var metadata = new List<SimilarCodeFeature>();
        foreach (var access in All(module, static routine => routine.MetadataAccesses, static module => module.MetadataAccesses))
        {
            if (!access.Kind.IsUnknown)
            {
                metadata.Add(new SimilarCodeFeature(
                    MdNaming.CreateId(access.Kind, access.ObjectName),
                    MetadataRefContexts.Code));
            }
        }

        foreach (var reference in All(module, static routine => routine.QueryReferences, static module => module.QueryReferences))
        {
            if (!reference.Kind.IsUnknown)
            {
                // Обращение из текста запроса: именно оно отличает «похоже по смыслу» от «похоже по буквам».
                metadata.Add(new SimilarCodeFeature(
                    MdNaming.CreateId(reference.Kind, reference.ObjectName),
                    MetadataRefContexts.Query));
            }
        }

        var main = module.Routines
            .OrderByDescending(Weight)
            .ThenBy(static routine => routine.StartLine)
            .FirstOrDefault();

        return new SimilarCodeFeatures(
            main?.Name ?? string.Empty,
            Distinct(platform),
            Distinct(metadata),
            Distinct(routines),
            Distinct(unresolved),
            Terms(main, text),
            main?.LineCount ?? module.LineCount,
            calls.Count + metadata.Count);
    }

    /// <summary>
    /// Имя метода вызова без квалификатора, строчными: значение признака для вызова, цель которого
    /// не разрешилась. «Справочники.Товары.НайтиПоНаименованию» и «Товары.НайтиПоНаименованию» дают
    /// одно и то же значение — «найтипонаименованию».
    /// </summary>
    /// <param name="callee">Текст вызова, как он записан в коде.</param>
    public static string CallName(string callee)
    {
        ArgumentNullException.ThrowIfNull(callee);
        var separator = callee.LastIndexOf('.');
        var method = separator < 0 ? callee : callee[(separator + 1)..];
        return method.Trim().ToLowerInvariant();
    }

    /// <summary>Оценка одной реализации: доля покрытых признаков, термы и штраф за объём.</summary>
    private SimilarCodeCandidate? Score(
        SimilarCodeFeatures draft,
        SimilarCodeProfile profile,
        IReadOnlyDictionary<SimilarCodeSignal, IReadOnlyDictionary<string, double>> weights)
    {
        var matches = new List<SimilarCodeMatch>();
        var categories = new List<(SimilarCodeSignal Signal, double Similarity, string Title)>
        {
            (SimilarCodeSignal.PlatformCall, Category(draft.PlatformCalls, profile.PlatformCalls, SimilarCodeSignal.PlatformCall, weights, matches), "вызовы платформы"),
            (SimilarCodeSignal.MetadataReference, Category(draft.MetadataReferences, profile.MetadataReferences, SimilarCodeSignal.MetadataReference, weights, matches), "обращения к метаданным"),
            (SimilarCodeSignal.RoutineCall, Category(draft.RoutineCalls, profile.RoutineCalls, SimilarCodeSignal.RoutineCall, weights, matches), "вызовы процедур"),
            (SimilarCodeSignal.UnresolvedCall, Category(draft.UnresolvedCalls, profile.UnresolvedCalls, SimilarCodeSignal.UnresolvedCall, weights, matches), "неразрешённые вызовы, только имя метода (слабый сигнал)"),
        };

        var terms = TermsSimilarity(draft, profile, out var sharedTerms);
        if (terms > 0)
        {
            matches.Add(new SimilarCodeMatch(
                SimilarCodeSignal.Terms,
                sharedTerms,
                profile.TermScore > 0 ? "terms_fts (BM25)" : "имя, комментарий и параметры"));
        }

        var baseScore =
            _options.PlatformWeight * categories[0].Similarity
            + _options.MetadataWeight * categories[1].Similarity
            + _options.RoutineWeight * categories[2].Similarity
            + _options.UnresolvedCallWeight * categories[3].Similarity
            + _options.TermsWeight * terms;

        if (matches.Count == 0 || baseScore <= 0)
        {
            return null;
        }

        var size = SizeFactor(draft, profile);
        // Сумма весов сигналов чуть больше единицы (слабый сигнал добавлен сверх прежних четырёх),
        // поэтому оценка ограничивается единицей: «похожесть 1.00» — это совпадение всех сигналов.
        var score = Math.Min(1, baseScore * size.Factor);
        if (score < _options.MinScore)
        {
            return null;
        }

        return new SimilarCodeCandidate(
            profile.Id,
            profile.Name,
            profile.ModulePath,
            profile.OwnerId,
            profile.StartLine,
            profile.EndLine,
            profile.Lines,
            profile.Statements,
            score,
            matches,
            Reason(categories, terms, size.Detail, matches));
    }

    /// <summary>
    /// Похожесть одной категории признаков: доля значимых признаков черновика, покрытая кандидатом,
    /// умноженная на мягкую поправку за лишние признаки кандидата.
    /// </summary>
    private static double Category(
        IReadOnlyList<SimilarCodeFeature> draft,
        IReadOnlyList<SimilarCodeFeature> candidate,
        SimilarCodeSignal signal,
        IReadOnlyDictionary<SimilarCodeSignal, IReadOnlyDictionary<string, double>> weights,
        List<SimilarCodeMatch> matches)
    {
        if (draft.Count == 0 || candidate.Count == 0 || !weights.TryGetValue(signal, out var byValue))
        {
            return 0;
        }

        var candidateValues = new Dictionary<string, SimilarCodeFeature>(StringComparer.OrdinalIgnoreCase);
        foreach (var feature in candidate)
        {
            candidateValues.TryAdd(feature.Value, feature);
        }

        var total = 0.0;
        var hit = 0.0;
        var shared = 0;
        foreach (var feature in draft)
        {
            if (!byValue.TryGetValue(feature.Value, out var weight) || weight <= 0)
            {
                continue;
            }

            total += weight;
            if (candidateValues.TryGetValue(feature.Value, out var other))
            {
                hit += weight;
                shared++;
                matches.Add(new SimilarCodeMatch(signal, feature.Value, other.Detail ?? feature.Detail));
            }
        }

        if (total <= 0 || shared == 0)
        {
            return 0;
        }

        // Лишние признаки кандидата гасят оценку мягко (корень доли): процедура, которая вызывает
        // всё подряд, не должна побеждать только потому, что среди её вызовов нашлись нужные.
        var focus = Math.Min(1, Math.Sqrt(shared / (double)candidateValues.Count));
        return hit / total * focus;
    }

    /// <summary>Близость имени, комментария и параметров: лучшее из BM25 и совпадения термов.</summary>
    private static double TermsSimilarity(SimilarCodeFeatures draft, SimilarCodeProfile profile, out string shared)
    {
        var candidateTerms = SimilarCodeTokens.Words(profile.Name, profile.CommentHead, profile.Parameters);
        var draftTerms = new HashSet<string>(draft.Terms, StringComparer.Ordinal);
        var common = candidateTerms.Where(draftTerms.Contains).ToList();
        shared = string.Join(", ", common.Take(6));

        // BM25 из terms_fts считает источник данных: он учитывает редкость термов сам.
        var dice = draftTerms.Count == 0 || candidateTerms.Count == 0
            ? 0
            : 2.0 * common.Count / (draftTerms.Count + candidateTerms.Count);

        if (shared.Length == 0 && Math.Max(profile.TermScore, dice) > 0)
        {
            shared = string.Join(", ", candidateTerms.Take(4));
        }

        return Math.Min(1, Math.Max(profile.TermScore, dice));
    }

    /// <summary>Штраф за разницу в объёме: строки и операторы, не больше <see cref="SimilarCodeOptions.SizePenalty"/>.</summary>
    private (double Factor, string Detail) SizeFactor(SimilarCodeFeatures draft, SimilarCodeProfile profile)
    {
        var lines = Distance(draft.Lines, profile.Lines);
        var statements = Distance(draft.Statements, profile.Statements);
        var distance = 0.7 * lines + 0.3 * statements;
        var factor = 1 - _options.SizePenalty * distance;
        return (factor, $"объём: {profile.Lines} строк и {profile.Statements} операторов против "
            + $"{draft.Lines} и {draft.Statements} (штраф {(_options.SizePenalty * distance * 100):0}%)");
    }

    /// <summary>Относительная разница двух величин: 0 — равны, 1 — вдвое и больше.</summary>
    private static double Distance(int left, int right) =>
        left <= 0 || right <= 0 ? 0 : Math.Abs(left - right) / (double)Math.Max(left, right);

    /// <summary>Объяснение для агента: что совпало, насколько и почему кандидат в списке.</summary>
    private static string Reason(
        IReadOnlyList<(SimilarCodeSignal Signal, double Similarity, string Title)> categories,
        double terms,
        string sizeDetail,
        IReadOnlyList<SimilarCodeMatch> matches)
    {
        var parts = new List<string>(5);
        foreach (var (signal, similarity, title) in categories)
        {
            if (similarity <= 0)
            {
                continue;
            }

            var values = matches
                .Where(match => match.Signal == signal)
                .Select(Describe)
                .Take(4);
            parts.Add($"{title}: {string.Join(", ", values)} (совпадение {similarity:0.00})");
        }

        if (terms > 0)
        {
            var shared = matches.FirstOrDefault(match => match.Signal == SimilarCodeSignal.Terms)?.Value;
            parts.Add($"близость имени и термов: {shared} (совпадение {terms:0.00})");
        }

        parts.Add(sizeDetail);
        return string.Join("; ", parts);
    }

    /// <summary>
    /// Как показать совпавший признак: у вызова процедуры — текст вызова (идентификатор цели
    /// сравнивается, но агенту полезнее текст), у обращения к метаданным — откуда оно взято.
    /// </summary>
    private static string Describe(SimilarCodeMatch match) => match.Signal switch
    {
        SimilarCodeSignal.RoutineCall or SimilarCodeSignal.UnresolvedCall =>
            string.IsNullOrEmpty(match.Detail) ? match.Value : match.Detail,
        _ => match.Detail switch
        {
            null or "" => match.Value,
            MetadataRefContexts.Query => $"{match.Value} (из запроса)",
            MetadataRefContexts.Code => $"{match.Value} (из кода)",
            _ => $"{match.Value} ({match.Detail})",
        },
    };

    /// <summary>Термы черновика: имя процедуры по частям, шапка комментария и имена параметров.</summary>
    private static IReadOnlyList<string> Terms(BslRoutine? routine, string text)
    {
        var tokens = new List<string>();
        if (routine is null)
        {
            // Процедуры в тексте нет (фрагмент тела): остаются строки комментария.
            var comments = text
                .Split('\n')
                .Select(static line => line.Trim())
                .Where(static line => line.StartsWith("//", StringComparison.Ordinal))
                .Select(static line => line.TrimStart('/').Trim());
            SimilarCodeTokens.Add(tokens, string.Join(' ', comments));
        }
        else
        {
            SimilarCodeTokens.Add(tokens, routine.Name);
            SimilarCodeTokens.Add(tokens, CommentHead(text, routine.StartLine));
            foreach (var parameter in routine.Parameters)
            {
                SimilarCodeTokens.Add(tokens, parameter);
            }
        }

        return
        [
            .. tokens
                .Distinct(StringComparer.Ordinal)
                .OrderByDescending(static token => token.Length)
                .ThenBy(static token => token, StringComparer.Ordinal)
                .Take(12)
        ];
    }

    /// <summary>До трёх строк комментария непосредственно над процедурой — так же, как их читает индекс.</summary>
    private static string? CommentHead(string text, int startLine)
    {
        var lines = text.Split('\n');
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

    /// <summary>Сколько признаков у процедуры: по нему выбирается главная процедура модуля.</summary>
    private static int Weight(BslRoutine routine) =>
        routine.Calls.Count + routine.MetadataAccesses.Count + routine.QueryReferences.Count;

    /// <summary>Признаки процедур модуля и признаки самого модуля (код вне процедур) одним списком.</summary>
    private static IEnumerable<T> All<T>(
        BslModuleInfo module,
        Func<BslRoutine, IReadOnlyList<T>> fromRoutine,
        Func<BslModuleInfo, IReadOnlyList<T>> fromModule) =>
        module.Routines.SelectMany(fromRoutine).Concat(fromModule(module));

    /// <summary>Убирает повторы признаков, сохраняя порядок: у каждой категории свой набор значений.</summary>
    private static IReadOnlyList<SimilarCodeFeature> Distinct(IReadOnlyList<SimilarCodeFeature> features)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<SimilarCodeFeature>(features.Count);
        foreach (var feature in features)
        {
            if (string.IsNullOrWhiteSpace(feature.Value) || !seen.Add(feature.Value))
            {
                continue;
            }

            result.Add(feature);
        }

        return result;
    }
}

/// <summary>
/// Термы имён и комментариев: деление идентификатора на слова повторяет то, как их складывает
/// индекс в <c>terms_fts</c> («ЗначениеРеквизитаОбъекта» → «значение реквизит объекта»).
/// </summary>
internal static class SimilarCodeTokens
{
    /// <summary>Наименьшая длина терма, попадающего в поиск: короткие слова («на», «по») не различают код.</summary>
    private const int MinLength = 3;

    /// <summary>Слова из нескольких строк текста: без повторов, только пригодные для поиска.</summary>
    internal static HashSet<string> Words(params string?[] parts)
    {
        var tokens = new List<string>();
        foreach (var part in parts)
        {
            Add(tokens, part);
        }

        return [.. tokens];
    }

    /// <summary>Добавляет слова строки в список: строчные, без повторов и служебных символов.</summary>
    internal static void Add(List<string> tokens, string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        foreach (var token in Split(text))
        {
            if (token.Length >= MinLength)
            {
                tokens.Add(token);
            }
        }
    }

    /// <summary>Делит текст на строчные слова: по разделителям и по границе «строчная → заглавная».</summary>
    internal static IReadOnlyList<string> Split(string text)
    {
        var builder = new StringBuilder(text.Length + 8);
        for (var index = 0; index < text.Length; index++)
        {
            var current = text[index];
            if (char.IsLetterOrDigit(current))
            {
                if (index > 0 && char.IsUpper(current) && !char.IsUpper(text[index - 1]))
                {
                    builder.Append(' ');
                }

                builder.Append(char.ToLowerInvariant(current));
                continue;
            }

            builder.Append(' ');
        }

        return [.. builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries)];
    }
}
