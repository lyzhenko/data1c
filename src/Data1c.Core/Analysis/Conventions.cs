using Data1c.Core.Graph;

namespace Data1c.Core.Analysis;

/// <summary>
/// Процедура конфигурации вместе с числом обращений к ней: строка рейтинга «кто чаще используется».
/// </summary>
/// <param name="RoutineId">Идентификатор узла процедуры (<c>routine:module:…#Имя</c>).</param>
/// <param name="OwnerId">Идентификатор объекта-владельца (общий модуль, документ, форма).</param>
/// <param name="BackingId">Узел-носитель: сама процедура, а если узел процедуры не найден — её модуль.</param>
/// <param name="Name">Имя процедуры.</param>
/// <param name="ModulePath">Путь модуля внутри выгрузки.</param>
/// <param name="Uses">Сколько вызовов ведёт в эту процедуру.</param>
public sealed record ConventionModuleUsage(
    string RoutineId,
    string? OwnerId,
    string BackingId,
    string Name,
    string ModulePath,
    int Uses);

/// <summary>Сведения о символе процедуры: нужны для пояснения «почему подходит».</summary>
/// <param name="Name">Имя процедуры.</param>
/// <param name="RoutineId">Узел процедуры в графе: <c>routine:module:…#Имя</c>.</param>
/// <param name="ModulePath">Путь модуля.</param>
/// <param name="Kind">Вид символа: Procedure или Function.</param>
/// <param name="IsExport">Процедура экспортная (её вообще можно вызывать из другого модуля).</param>
/// <param name="StartLine">Номер первой строки.</param>
/// <param name="EndLine">Номер последней строки.</param>
/// <param name="Parameters">Параметры через запятую, если были разобраны.</param>
/// <param name="CommentHead">Шапка комментария над процедурой, если комментарии индексировались.</param>
/// <param name="OwnerId">Объект-владелец модуля: общий модуль, документ, форма.</param>
public sealed record ConventionSymbol(
    string Name,
    string RoutineId,
    string ModulePath,
    string Kind,
    bool IsExport,
    int StartLine,
    int EndLine,
    string? Parameters = null,
    string? CommentHead = null,
    string? OwnerId = null);

/// <summary>Строка ответа «так уже делают»: процедура, пример вызова и пояснение.</summary>
/// <param name="RoutineId">Идентификатор узла процедуры: его принимает инструмент <c>code</c>.</param>
/// <param name="Name">Имя процедуры.</param>
/// <param name="OwnerId">Объект-владелец (общий модуль, документ, форма).</param>
/// <param name="ModulePath">Путь модуля внутри выгрузки.</param>
/// <param name="Uses">Сколько вызовов ведёт в эту процедуру.</param>
/// <param name="Callers">Сколько разных мест её вызывают.</param>
/// <param name="Symbol">Символ процедуры: строки, параметры, шапка комментария.</param>
/// <param name="Score">Вес совпадения с намерением: чем больше, тем ближе приём к запросу.</param>
/// <param name="CallMethod">Метод платформы, которым процедура делает приём (для запроса по методу).</param>
/// <param name="ExampleModule">Модуль примера вызова: откуда видно, как приём применяют.</param>
/// <param name="ExampleLine">Строка примера вызова.</param>
/// <param name="ExampleDetail">Текст вызова, как он записан в коде.</param>
/// <param name="Reasons">Короткие пояснения: почему процедура подходит под намерение.</param>
public sealed record ConventionRoutine(
    string RoutineId,
    string Name,
    string? OwnerId,
    string ModulePath,
    int Uses,
    int Callers,
    ConventionSymbol? Symbol,
    int Score,
    string? CallMethod = null,
    string? ExampleModule = null,
    int? ExampleLine = null,
    string? ExampleDetail = null,
    IReadOnlyList<string>? Reasons = null);

/// <summary>
/// Ответ на вопрос «в конфигурации это уже делают так»: рейтинг модулей и процедур,
/// примеры вызовов и подсказка, куда смотреть дальше.
/// </summary>
/// <param name="Intent">Распознанное намерение агента (название приёма).</param>
/// <param name="Question">Исходный запрос: текст намерения или имя метода платформы.</param>
/// <param name="PlatformMethod">Метод платформы, по которому шёл поиск, если он был задан.</param>
/// <param name="Modules">Рейтинг модулей по числу вызовов.</param>
/// <param name="Routines">Процедуры, которые делают то же самое, с примерами вызова.</param>
/// <param name="CallSites">Найденные вызовы метода платформы с позициями в коде.</param>
/// <param name="Hint">Подсказка агенту: как открыть код найденных процедур.</param>
/// <param name="Note">Оговорка: если приём распознан неуверенно, здесь сказано об этом.</param>
public sealed record ConventionAnswer(
    string Intent,
    string Question,
    string? PlatformMethod,
    IReadOnlyList<ConventionModuleUsage> Modules,
    IReadOnlyList<ConventionRoutine> Routines,
    IReadOnlyList<ConventionCallSite> CallSites,
    string Hint,
    string? Note = null);

/// <summary>Найденное место вызова метода платформы: где именно и в какой процедуре.</summary>
/// <param name="RoutineId">Идентификатор процедуры-вызывающего.</param>
/// <param name="ModulePath">Модуль, в котором стоит вызов.</param>
/// <param name="Line">Номер строки.</param>
/// <param name="Detail">Текст вызова, как он записан в коде.</param>
public sealed record ConventionCallSite(string RoutineId, string ModulePath, int? Line, string? Detail);

/// <summary>
/// Сведения о процедуре для разбора приёмов: символ, число входящих вызовов и пример вызывающего.
/// Читаются одним запросом на весь набор кандидатов, иначе на 2,6 млн связей ответ стоил бы минуты.
/// </summary>
/// <param name="RoutineId">Узел процедуры.</param>
/// <param name="Symbol">Символ процедуры, если он есть в индексе.</param>
/// <param name="Uses">Сколько вызовов ведёт в процедуру.</param>
/// <param name="Callers">Сколько разных мест её вызывают.</param>
/// <param name="ExampleModule">Модуль первого примера вызова.</param>
/// <param name="ExampleLine">Строка первого примера вызова.</param>
/// <param name="ExampleDetail">Текст первого примера вызова.</param>
/// <param name="MetadataKinds">Виды объектов метаданных, к которым она обращается.</param>
/// <param name="PlatformHits">Сколько раз она сама вызывает метод платформы, если он задан.</param>
public sealed record ConventionEvidence(
    string RoutineId,
    ConventionSymbol? Symbol,
    int Uses,
    int Callers,
    string? ExampleModule,
    int? ExampleLine,
    string? ExampleDetail,
    IReadOnlyList<string> MetadataKinds,
    int PlatformHits = 0);

/// <summary>
/// Типовой приём конфигурации: что делает агент, какими методами платформы это обычно пишут,
/// к каким объектам метаданных обращаются и какими словами это называют в коде.
/// </summary>
/// <param name="Id">Короткое имя приёма для ответа (<c>записать объект</c>).</param>
/// <param name="Title">Название приёма так, как его увидит агент.</param>
/// <param name="Keywords">Слова запроса, по которым приём узнаётся.</param>
/// <param name="PlatformMethods">Методы платформы, которыми приём выполняется.</param>
/// <param name="MetadataKinds">Виды объектов метаданных, к которым приём обращается.</param>
/// <param name="NameTokens">Слова имени процедуры и шапки комментария, выдающие приём.</param>
public sealed record ConventionIntent(
    string Id,
    string Title,
    IReadOnlyList<string> Keywords,
    IReadOnlyList<string> PlatformMethods,
    IReadOnlyList<string> MetadataKinds,
    IReadOnlyList<string> NameTokens);

/// <summary>
/// Источник данных о конфигурации для разбора конвенций: граф в памяти или SQLite-индекс.
/// </summary>
/// <remarks>
/// Интерфейс намеренно маленький и оперирует только строками: разбор приёмов живёт в
/// <see cref="Conventions"/>, а SQL и обход графа — в реализациях. Благодаря этому один и тот же
/// ответ получается и на индексе, и на разборе в память.
/// </remarks>
public interface IConventionQuery
{
    /// <summary>Процедуры по убыванию числа вызовов: основа рейтинга общих модулей.</summary>
    IReadOnlyList<ConventionModuleUsage> RankRoutines(int limit);

    /// <summary>Сведения о символах процедур по их узлам.</summary>
    IReadOnlyList<ConventionSymbol> SymbolsOf(IReadOnlyCollection<string> routineIds);

    /// <summary>Кто вызывает эту процедуру: модуль и строка вызова.</summary>
    IReadOnlyList<ConventionCallSite> CallerSites(string routineId, int limit);

    /// <summary>Кто вызывает метод платформы: процедуры с числом вызовов и примерами мест.</summary>
    IReadOnlyList<ConventionRoutine> PlatformMatches(string platformMethod, int limit);

    /// <summary>К каким объектам метаданных обращается процедура.</summary>
    IReadOnlyList<string> MetadataKindsOf(string routineId);

    /// <summary>Процедуры, у которых имя начинается с одного из указанных слов.</summary>
    IReadOnlyList<string> RoutinesByNamePrefix(IReadOnlyCollection<string> namePrefixes, int limit);

    /// <summary>Процедуры, найденные по словам имени, шапки комментария и параметров.</summary>
    IReadOnlyList<string> RoutinesByTerms(IReadOnlyCollection<string> terms, int limit);

    /// <summary>Объекты-владельцы модулей: «CommonModules/…/Module.bsl» → «CommonModule.Имя».</summary>
    IReadOnlyDictionary<string, string> ModuleOwners(IReadOnlyCollection<string> modulePaths);

    /// <summary>
    /// Сведения о процедурах одним запросом: символ, число вызовов, пример вызывающего, обращения
    /// к метаданным и число собственных вызовов метода платформы (если он задан).
    /// </summary>
    /// <param name="routineIds">Процедуры-кандидаты.</param>
    /// <param name="platformMethod">Метод платформы, вызовы которого считаются у самих процедур.</param>
    IReadOnlyList<ConventionEvidence> EvidenceOf(IReadOnlyCollection<string> routineIds, string? platformMethod);
}

/// <summary>
/// «В конфигурации это уже делают так»: рейтинг общих модулей и процедур по числу вызовов и
/// подбор типовых приёмов под намерение агента.
/// </summary>
/// <remarks>
/// <para>Разбор намерения идёт в три ступени. Сначала текст сравнивается с именами методов
/// платформы: «ЗначениеРеквизитаОбъекта» — это приём «прочитать реквизит». Затем — по ключевым
/// словам приёма. Если ничего не совпало, берётся приём с наибольшим числом общих слов, а при
/// полном отсутствии совпадений запрос ищется по словам напрямую.</para>
/// <para>Подбор процедур опирается на три признака: вызовы методов платформы, обращения к объектам
/// метаданных и слова имени либо шапки комментария. Признаки складываются в вес, и по нему
/// процедуры упорядочиваются; при равном весе выше стоит та, которую вызывают чаще.</para>
/// </remarks>
public static class Conventions
{
    /// <summary>Веса признаков совпадения: метод платформы весомее обращения и имени.</summary>
    private const int PlatformWeight = 4;

    private const int MetadataWeight = 3;
    private const int NameWeight = 3;
    private const int CommentWeight = 2;

    /// <summary>Предел числа процедур, которые просматриваются после набора кандидатов.</summary>
    private const int MaxCandidates = 2000;

    /// <summary>Сколько процедур берётся в рейтинг модулей: по ним считается «где это делают чаще».</summary>
    private const int RatingDepth = 300;

    /// <summary>Длина общей основы слова, по которой слова запроса и приёма считаются совпавшими.</summary>
    private const int MinStem = 5;

    /// <summary>Слова запроса, ничего не говорящие о приёме: по ним кандидаты не набираются.</summary>
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "как", "что", "где", "когда", "мне", "нам", "это", "этот", "для", "или", "если", "чтобы",
        "надо", "нужно", "хочу", "можно", "ли", "же", "бы", "не", "на", "из", "по", "в", "во",
    };

    /// <summary>Каталог типовых приёмов конфигурации 1С.</summary>
    private static readonly ConventionIntent[] Catalog =
    [
        new(
            "записать объект",
            "Запись и проведение объекта",
            ["записать", "сохранить", "провести", "запись", "объект", "создать", "поместить"],
            ["Записать", "ЗаписатьИЗакрыть", "ЗаписатьНастройки", "Провести", "ЗаписатьОбъект"],
            ["Document", "Catalog"],
            ["Записать", "Запись", "Сохранить", "Провести", "Проведение", "Создать"]),

        new(
            "получить реквизит объекта",
            "Чтение реквизита объекта",
            ["получить", "прочитать", "реквизит", "значение", "свойство", "объект"],
            ["ЗначениеРеквизитаОбъекта", "ПолучитьРеквизит", "ПолучитьСвойство", "РеквизитОбъекта"],
            ["Catalog", "Document", "InformationRegister"],
            ["Реквизит", "Свойство", "Значение", "ПолучитьРеквизит", "ПрочитатьРеквизит"]),

        new(
            "найти по наименованию",
            "Поиск объекта по наименованию, коду или номеру",
            ["найти", "наименование", "наименованию", "код", "номер", "поиск", "ссылка"],
            ["НайтиПоНаименованию", "НайтиПоКоду", "НайтиПоНомеру", "НайтиПоРеквизиту", "НайтиСсылку"],
            ["Catalog", "Document"],
            ["Найти", "Поиск", "Наименование", "Код", "Номер", "Ссылка"]),

        new(
            "прочитать данные запросом",
            "Чтение данных запросом",
            ["запрос", "запросом", "выборка", "прочитать", "выбрать", "данные", "таблица", "параметр"],
            ["Запрос", "Выполнить", "ВыполнитьПакет", "УстановитьПараметр", "Выбрать", "Прочитать"],
            ["Catalog", "Document", "InformationRegister", "AccumulationRegister", "AccountingRegister"],
            ["Запрос", "Выборка", "Выбрать", "Прочитать", "Загрузить", "Данные"]),

        new(
            "добавить в коллекцию",
            "Добавление элемента в коллекцию или структуру",
            ["добавить", "вставить", "коллекция", "массив", "структура", "список", "таблица", "значений"],
            ["Вставить", "Добавить", "ДобавитьЗначение", "ДобавитьСтроку"],
            [],
            ["Добавить", "Вставить", "Заполнить", "Массив", "Структура", "Список"]),

        new(
            "вывести сообщение пользователю",
            "Сообщение пользователю",
            ["сообщить", "сообщение", "пользователь", "показать", "предупреждение", "ошибка", "вопрос"],
            ["Сообщить", "ПоказатьПредупреждение", "ПоказатьВопрос", "Вопрос", "Предупреждение", "СообщениеПользователю"],
            [],
            ["Сообщить", "Сообщение", "Предупреждение", "Вопрос", "Пользователю"]),

        new(
            "выполнить на сервере или в фоне",
            "Выполнение на сервере, в фоне и на клиенте",
            ["сервер", "сервере", "фон", "фоне", "фоновое", "задание", "клиент", "асинхронно", "выполнить"],
            ["ЗапуститьФоновоеЗадание", "ВыполнитьВФоне", "ВыполнитьНаСервере", "ЗапуститьЗадание"],
            [],
            ["Фоновое", "Фон", "Задание", "Сервер", "Сервере", "Клиент", "Асинхронно", "Выполнить"]),

        new(
            "установить реквизит объекта",
            "Заполнение реквизитов объекта",
            ["установить", "заполнить", "присвоить", "реквизит", "значение", "объект"],
            ["УстановитьРеквизит", "ЗаполнитьЗначенияСвойств", "ЗаполнитьСвойства", "УстановитьСвойство"],
            ["Catalog", "Document"],
            ["Установить", "Заполнить", "Присвоить", "Реквизит", "Свойство"]),
    ];

    /// <summary>Приёмы, доступные для подсказок и проверок.</summary>
    public static IReadOnlyList<ConventionIntent> Intents => Catalog;

    /// <summary>Ищет процедуры и модули, которые уже делают то, что задумал агент.</summary>
    /// <param name="query">Доступ к индексу или к разбору в памяти.</param>
    /// <param name="intent">Свободный текст намерения; вместо него можно задать метод платформы.</param>
    /// <param name="platformMethod">Точное имя метода платформы: «Записать», «НайтиПоНаименованию».</param>
    /// <param name="limit">Сколько процедур и модулей вернуть.</param>
    public static ConventionAnswer Suggest(
        IConventionQuery query,
        string? intent = null,
        string? platformMethod = null,
        int limit = 10)
    {
        ArgumentNullException.ThrowIfNull(query);
        var bounded = Math.Clamp(limit, 1, 100);
        var method = Trim(platformMethod);
        var text = Trim(intent);

        return method is not null
            ? PlatformAnswer(query, method, text, bounded)
            : IntentAnswer(query, text ?? string.Empty, bounded);
    }

    /// <summary>Ответ на точный метод платформы: кто его вызывает, как и как часто.</summary>
    private static ConventionAnswer PlatformAnswer(IConventionQuery query, string method, string? intent, int limit)
    {
        var found = query.PlatformMatches(method, limit);
        var routines = found.Count == 0
            ? []
            : BuildRoutines(query, [.. found.Select(static item => item.RoutineId)], bound: MaxCandidates)
                .OrderByDescending(static item => item.Uses)
                .ThenBy(static item => item.Name, StringComparer.Ordinal)
                .Take(limit)
                .ToList();

        var sites = new List<ConventionCallSite>();
        foreach (var routine in routines)
        {
            sites.AddRange(query.CallerSites(routine.RoutineId, limit));
        }

        var modules = ModuleRating(query, routines, bound: limit);
        var note = found.Count > routines.Count
            ? $"Всего вызовов «{method}» найдено в {found.Count} процедурах, показаны первые {routines.Count}."
            : null;

        return new ConventionAnswer(
            $"вызов метода платформы «{method}»",
            intent ?? method,
            method,
            modules,
            routines,
            sites,
            Hint(routines.Count, method: method),
            note);
    }

    /// <summary>Ответ на свободный текст намерения: распознанный приём, рейтинг и примеры.</summary>
    private static ConventionAnswer IntentAnswer(IConventionQuery query, string text, int limit)
    {
        var tokens = Tokenize(text);
        var intent = MatchIntent(text, tokens);
        var terms = TermsOf(tokens, intent, excludePlatformMethods: intent is not null);
        var platformMethods = intent?.PlatformMethods ?? [];

        var candidates = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var truncated = false;

        // Порядок набора кандидатов — по убыванию точности признака: метод платформы, имя процедуры,
        // слова шапки и параметров. На реальной выгрузке вызовы «Записать» есть в тысячах модулей,
        // поэтому каждая ступень ограничена и молча уступает место следующей.
        foreach (var method in platformMethods)
        {
            if (candidates.Count >= MaxCandidates)
            {
                truncated = true;
                break;
            }

            Add(candidates, seen, query.PlatformMatches(method, limit: 120).Select(static item => item.RoutineId));
        }

        if (candidates.Count < MaxCandidates && terms.Count > 0)
        {
            var stems = Stems(terms);
            Add(candidates, seen, query.RoutinesByNamePrefix(stems, limit: 120));
            Add(candidates, seen, query.RoutinesByTerms(terms, limit: 120));
        }

        if (candidates.Count > MaxCandidates)
        {
            truncated = true;
            candidates.RemoveRange(MaxCandidates, candidates.Count - MaxCandidates);
        }

        var scored = candidates.Count == 0 ? [] : Score(query, candidates, platformMethods, intent, tokens);
        var top = scored
            .OrderByDescending(static item => item.Score)
            .ThenByDescending(static item => item.Uses)
            .ThenBy(static item => item.Name, StringComparer.Ordinal)
            .Take(limit)
            .ToList();

        var modules = ModuleRating(query, top, bound: limit);
        var note = BuildNote(intent, tokens, truncated);
        var title = intent?.Title ?? (tokens.Count == 0 ? "запрос без слов" : "поиск по словам запроса");

        return new ConventionAnswer(
            title,
            text,
            null,
            modules,
            top,
            CallSitesOf(top),
            Hint(top.Count),
            note);
    }

    /// <summary>Считает вес совпадения для каждой процедуры-кандидата.</summary>
    private static List<ConventionRoutine> Score(
        IConventionQuery query,
        IReadOnlyList<string> candidates,
        IReadOnlyList<string> platformMethods,
        ConventionIntent? intent,
        IReadOnlyList<string> tokens)
    {
        var methods = new HashSet<string>(platformMethods, StringComparer.OrdinalIgnoreCase);
        var tokensForName = intent?.NameTokens ?? tokens;
        var result = new List<ConventionRoutine>(candidates.Count);

        // Вызовы методов платформы и сведения о процедурах читаются один раз на весь набор:
        // на реальной выгрузке «Записать» вызывают в сотнях модулей, а связей 2,6 млн.
        var callers = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var method in methods)
        {
            foreach (var found in query.PlatformMatches(method, limit: 400))
            {
                if (!callers.TryGetValue(found.RoutineId, out var list))
                {
                    list = [];
                    callers[found.RoutineId] = list;
                }

                list.Add(method);
            }
        }

        var evidence = query.EvidenceOf(candidates, null).ToDictionary(static item => item.RoutineId, StringComparer.Ordinal);

        foreach (var routineId in candidates)
        {
            evidence.TryGetValue(routineId, out var found);
            var symbol = found?.Symbol;
            var name = NameOf(routineId, symbol);
            var score = 0;
            var reasons = new List<string>(3);
            List<string> matchedMethods = [];

            if (callers.TryGetValue(routineId, out var methodsOfRoutine) && methodsOfRoutine.Count > 0)
            {
                matchedMethods = methodsOfRoutine;
                score += PlatformWeight * matchedMethods.Count;
                reasons.Add("вызывает " + string.Join(", ", matchedMethods.Select(static item => "«" + item + "»")));
            }

            if (intent is { MetadataKinds.Count: > 0 } && found is { MetadataKinds.Count: > 0 })
            {
                var matchedKinds = found.MetadataKinds
                    .Where(kind => intent.MetadataKinds.Any(target => StartsWith(kind, target)))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (matchedKinds.Count > 0)
                {
                    score += MetadataWeight;
                    reasons.Add("обращается к " + string.Join(", ", matchedKinds));
                }
            }

            var matchedName = tokensForName.Where(token => StartsWith(name, token)).ToList();
            if (matchedName.Count > 0)
            {
                score += NameWeight * matchedName.Count;
                reasons.Add("имя: " + string.Join(", ", matchedName));
            }

            var matchedComment = symbol?.CommentHead is { Length: > 0 } comment
                ? tokens.Where(token => comment.Contains(token, StringComparison.OrdinalIgnoreCase)).ToList()
                : [];
            if (matchedComment.Count > 0)
            {
                score += CommentWeight;
                if (matchedName.Count == 0)
                {
                    reasons.Add("комментарий: " + string.Join(", ", matchedComment.Take(3)));
                }
            }

            var module = symbol?.ModulePath ?? ModuleOf(routineId);
            result.Add(new ConventionRoutine(
                routineId,
                name,
                symbol?.OwnerId,
                module,
                found?.Uses ?? 0,
                found?.Callers ?? 0,
                symbol,
                score,
                matchedMethods.Count > 0 ? matchedMethods[0] : null,
                found?.ExampleModule ?? module,
                found?.ExampleLine ?? symbol?.StartLine,
                found?.ExampleDetail,
                reasons.Count == 0 ? null : reasons));
        }

        return result;
    }

    /// <summary>Дочитывает сведения о уже найденных процедурах одним запросом на весь набор.</summary>
    private static List<ConventionRoutine> BuildRoutines(IConventionQuery query, IReadOnlyList<string> routineIds, int bound)
    {
        var selected = routineIds.Take(bound).ToList();
        var result = new List<ConventionRoutine>(selected.Count);
        foreach (var found in query.EvidenceOf(selected, null))
        {
            var symbol = found.Symbol;
            var module = symbol?.ModulePath ?? ModuleOf(found.RoutineId);
            result.Add(new ConventionRoutine(
                found.RoutineId,
                NameOf(found.RoutineId, symbol),
                symbol?.OwnerId,
                module,
                found.Uses,
                found.Callers,
                symbol,
                0,
                null,
                found.ExampleModule ?? module,
                found.ExampleLine ?? symbol?.StartLine,
                found.ExampleDetail));
        }

        return result;
    }

    /// <summary>Имя процедуры: из символа, а если его нет — из идентификатора узла.</summary>
    private static string NameOf(string routineId, ConventionSymbol? symbol)
    {
        if (symbol is not null && symbol.Name.Length > 0)
        {
            return symbol.Name;
        }

        var separator = routineId.LastIndexOf('#');
        return separator < 0 || separator == routineId.Length - 1 ? routineId : routineId[(separator + 1)..];
    }

    /// <summary>Модуль из идентификатора узла процедуры: <c>routine:module:путь#Имя</c>.</summary>
    private static string ModuleOf(string routineId)
    {
        const string prefix = "routine:module:";
        if (!routineId.StartsWith(prefix, StringComparison.Ordinal))
        {
            return routineId;
        }

        var text = routineId[prefix.Length..];
        var separator = text.LastIndexOf('#');
        return separator < 0 ? text : text[..separator];
    }

    /// <summary>Узел процедуры по пути модуля и имени: единый формат идентификатора в графе и индексе.</summary>
    private static string RoutineId(string modulePath, string name) => $"routine:module:{modulePath}#{name}";

    /// <summary>
    /// Рейтинг модулей по числу вызовов. Считается по рейтингу процедур из источника, а процедуры,
    /// показанные агенту, входят в свои модули даже если модуль не попал в общий рейтинг: иначе
    /// список модулей и список процедур выглядели бы несогласованно.
    /// </summary>
    private static List<ConventionModuleUsage> ModuleRating(
        IConventionQuery query,
        IReadOnlyList<ConventionRoutine> routines,
        int bound)
    {
        var totals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in query.RankRoutines(limit: RatingDepth))
        {
            if (item.ModulePath.Length == 0)
            {
                continue;
            }

            totals[item.ModulePath] = (totals.TryGetValue(item.ModulePath, out var current) ? current : 0) + item.Uses;
        }

        foreach (var routine in routines)
        {
            if (routine.ModulePath.Length == 0)
            {
                continue;
            }

            totals[routine.ModulePath] = (totals.TryGetValue(routine.ModulePath, out var current) ? current : 0) + routine.Uses;
        }

        // Владельцы дочитываются только по показанным модулям: имя модуля в ответе важнее,
        // чем идентификатор владельца, но агент ищет модуль именно по имени.
        var owners = query.ModuleOwners([.. totals.Keys]);
        return
        [
            .. totals
                .Where(static pair => pair.Value > 0)
                .OrderByDescending(static pair => pair.Value)
                .ThenBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Take(Math.Clamp(bound, 1, 50))
                .Select(pair => new ConventionModuleUsage(
                    RoutineId(pair.Key, string.Empty),
                    owners.TryGetValue(pair.Key, out var owner) ? owner : null,
                    "module:" + pair.Key,
                    ModuleName(pair.Key),
                    pair.Key,
                    pair.Value))
        ];
    }

    /// <summary>Имя общего модуля из пути модуля выгрузки.</summary>
    /// <remarks>
    /// У модуля объекта путь выглядит как «Catalogs/Товары/Ext/ObjectModule.bsl», у общего модуля —
    /// «CommonModules/ОбщегоНазначения/Ext/Module.bsl». Имя — сегмент перед «Ext»; если его нет,
    /// берётся имя файла без расширения.
    /// </remarks>
    private static string ModuleName(string modulePath)
    {
        var segments = modulePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var index = segments.Length - 1; index > 0; index--)
        {
            if (string.Equals(segments[index], "Ext", StringComparison.OrdinalIgnoreCase))
            {
                return segments[index - 1];
            }
        }

        if (segments.Length == 0)
        {
            return modulePath;
        }

        var last = segments[^1];
        var dot = last.LastIndexOf('.');
        return dot > 0 ? last[..dot] : last;
    }

    /// <summary>Места вызовов методов платформы найденными процедурами.</summary>
    private static List<ConventionCallSite> CallSitesOf(IReadOnlyList<ConventionRoutine> routines)
    {
        var sites = new List<ConventionCallSite>();
        foreach (var routine in routines)
        {
            if (routine.CallMethod is null || routine.ExampleLine is null)
            {
                continue;
            }

            sites.Add(new ConventionCallSite(
                routine.RoutineId,
                routine.ExampleModule ?? routine.ModulePath,
                routine.ExampleLine,
                routine.ExampleDetail));
        }

        return sites;
    }

    /// <summary>
    /// Распознаёт приём по тексту намерения: имя метода, ключевые слова, слова имени приёма.
    /// </summary>
    /// <remarks>
    /// Порог намеренно низкий: агент пишет «получить реквизит», а приём называется «ПолучитьРеквизит».
    /// Совпадение ищется по началу слова, потому что падежи и части речи в русском тексте расходятся
    /// («сообщение» и «сообщить», «реквизит» и «реквизита»). Ошибка в сторону лишнего приёма дешевле
    /// пропуска: рейтинг процедур всё равно выстроен по весу совпадения.
    /// </remarks>
    private static ConventionIntent? MatchIntent(string text, IReadOnlyList<string> tokens)
    {
        if (text.Length == 0)
        {
            return null;
        }

        // Имя метода платформы в запросе — самый точный признак приёма.
        foreach (var intent in Catalog)
        {
            foreach (var method in intent.PlatformMethods)
            {
                if (text.Contains(method, StringComparison.OrdinalIgnoreCase))
                {
                    return intent;
                }
            }
        }

        ConventionIntent? best = null;
        var bestScore = 0;
        foreach (var intent in Catalog)
        {
            var keywordHits = intent.Keywords.Count(keyword => tokens.Any(token => Matches(token, keyword)));
            var tokenHits = intent.NameTokens.Count(term => tokens.Any(token => Matches(token, term)));

            // Имя приёма учитывается с половинным весом: слова имени шире ключевых.
            var score = keywordHits * 2 + tokenHits;
            if (score > bestScore && (keywordHits >= 2 || (keywordHits >= 1 && tokenHits >= 1)))
            {
                bestScore = score;
                best = intent;
            }
        }

        return best;
    }

    /// <summary>Слова запроса, по которым ищутся кандидаты: без стоп-слов и имён методов платформы.</summary>
    private static List<string> TermsOf(IReadOnlyList<string> tokens, ConventionIntent? intent, bool excludePlatformMethods)
    {
        var result = new List<string>(tokens.Count);
        foreach (var token in tokens)
        {
            if (excludePlatformMethods && Catalog.Any(item => item.PlatformMethods.Any(method =>
                method.StartsWith(token, StringComparison.OrdinalIgnoreCase))))
            {
                continue;
            }

            result.Add(token);
        }

        return result;
    }

    /// <summary>Основы слов для префиксного поиска: «записать» и «записа» ведут к «Записать…».</summary>
    private static List<string> Stems(IReadOnlyList<string> tokens)
    {
        var result = new List<string>(tokens.Count);
        foreach (var token in tokens)
        {
            result.Add(token);
            if (token.Length >= 6)
            {
                result.Add(token[..^2]);
            }
        }

        return result;
    }

    /// <summary>Разбирает текст запроса на слова: нижний регистр, без стоп-слов, от трёх букв.</summary>
    private static List<string> Tokenize(string text)
    {
        var result = new List<string>();
        foreach (var raw in text.Split(
            [' ', ',', '.', ';', ':', '!', '?', '(', ')', '"', '\'', '/', '\\', '\n', '\r', '\t', '-'],
            StringSplitOptions.RemoveEmptyEntries))
        {
            var token = raw.ToLowerInvariant();
            if (token.Length < 3 || StopWords.Contains(token))
            {
                continue;
            }

            result.Add(token);
        }

        return result;
    }

    /// <summary>
    /// Совпадают ли слово запроса и слово приёма. Сравниваются начала слов: падежи и части речи
    /// в русском тексте расходятся, а основа остаётся общей («сообщение» и «сообщить»).
    /// </summary>
    private static bool Matches(string token, string term)
    {
        var length = Math.Min(Math.Min(token.Length, term.Length), MinStem);
        return length >= MinStem && token.AsSpan(0, length).Equals(term.AsSpan(0, length), StringComparison.OrdinalIgnoreCase);
    }

    private static bool StartsWith(string value, string prefix) =>
        value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>Собирает кандидатов, пропуская уже известные и не выходя за предел.</summary>
    private static void Add(List<string> candidates, HashSet<string> seen, IEnumerable<string> ids)
    {
        foreach (var id in ids)
        {
            if (candidates.Count >= MaxCandidates)
            {
                return;
            }

            if (seen.Add(id))
            {
                candidates.Add(id);
            }
        }
    }

    /// <summary>Оговорка к ответу: неуверенное распознавание приёма и обрезка кандидатов.</summary>
    private static string? BuildNote(ConventionIntent? intent, IReadOnlyList<string> tokens, bool truncated)
    {
        var parts = new List<string>(2);
        if (intent is null)
        {
            parts.Add("Приём не распознан: показаны процедуры, чьё имя или комментарий совпало со словами запроса. "
                + "Уточните намерение словами конфигурации (например, «записать объект») или укажите метод платформы.");
        }

        if (truncated)
        {
            parts.Add("Кандидатов было больше предела разбора: часть вызовов не просмотрена, сузьте запрос.");
        }

        return parts.Count == 0 ? null : string.Join(' ', parts);
    }

    /// <summary>Подсказка агенту: как открыть найденный код.</summary>
    private static string Hint(int found, string? method = null) => found == 0
        ? "Ничего не найдено. На реальной конфигурации приём мог не встречаться: проверьте имя метода "
            + "инструментом platform или переформулируйте намерение (search, grep)."
        : method is null
            ? "Откройте код найденных процедур инструментом code по их id — это и есть принятый в конфигурации способ."
            : $"Посмотрите, как этот метод применяют: code по id найденных процедур, а описание метода «{method}» — инструментом platform.";

    private static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>
/// Разбор конвенций по разбору выгрузки в памяти: тот же ответ, что и по индексу, но без SQLite.
/// </summary>
/// <remarks>
/// Режим без индекса нужен там, где индекс ещё не собран или сервер запущен с <c>--no-index</c>.
/// Источник обходит связи графа напрямую, поэтому часть сведений беднее индексной: шапка
/// комментария не читается (её читает только сборка индекса), а модуль вызова известен по узлу.
/// </remarks>
public sealed class AnalysisConventionQuery : IConventionQuery
{
    private readonly AnalysisResult _result;
    private readonly Dictionary<string, string> _moduleOfRoutine = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _ownerOfModule = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _incoming = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<(string Module, int? Line, string? Detail)>> _callerSites = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<(string Module, int? Line, string? Detail)>> _outgoing = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<string>> _metadataKinds = new(StringComparer.Ordinal);
    private readonly List<(string RoutineId, string Module, string Name)> _routines = [];
    private readonly HashSet<string> _commonModules = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Строит источник по готовому разбору выгрузки.</summary>
    public AnalysisConventionQuery(AnalysisResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        _result = result;

        // Владелец модуля и виды обращений к метаданным читаются из графа; путь модуля берётся
        // из разбора — он совпадает с путём в узле и не зависит от порядка связей.
        var metadata = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var edge in result.Graph.Edges)
        {
            switch (edge.Kind)
            {
                case GraphEdgeKind.Contains when edge.TargetId.StartsWith("module:", StringComparison.Ordinal):
                    _ownerOfModule.TryAdd(edge.TargetId, edge.SourceId);
                    break;

                case GraphEdgeKind.UsesMetadata:
                    var kind = KindOfId(edge.TargetId);
                    if (kind is not null)
                    {
                        if (!metadata.TryGetValue(edge.SourceId, out var kinds))
                        {
                            kinds = [];
                            metadata[edge.SourceId] = kinds;
                        }

                        kinds.Add(kind);
                    }

                    break;

                default:
                    break;
            }
        }

        var callers = new Dictionary<string, List<(string Module, int? Line, string? Detail)>>(StringComparer.Ordinal);
        foreach (var module in result.Modules)
        {
            var moduleId = "module:" + module.Path;
            if (module.OwnerId is { Length: > 0 } owner)
            {
                _ownerOfModule.TryAdd(moduleId, owner);
                var name = owner.StartsWith("CommonModule.", StringComparison.Ordinal) ? owner["CommonModule.".Length..] : null;
                if (name is not null)
                {
                    _commonModules.Add(name);
                }
            }

            foreach (var routine in module.Routines)
            {
                var routineId = $"routine:module:{module.Path}#{routine.Name}";
                _moduleOfRoutine.TryAdd(routineId, module.Path);
                _routines.Add((routineId, module.Path, routine.Name));

                var outgoing = new List<(string Module, int? Line, string? Detail)>(routine.Calls.Count);
                foreach (var call in routine.Calls)
                {
                    var target = call.IsLocal ? $"routine:{moduleId}#{call.Method}" : "call:" + call.Callee;
                    _incoming[target] = (_incoming.TryGetValue(target, out var count) ? count : 0) + 1;
                    if (!callers.TryGetValue(target, out var list))
                    {
                        list = [];
                        callers[target] = list;
                    }

                    list.Add((module.Path, call.Line, call.Callee));
                    outgoing.Add((module.Path, call.Line, call.Callee));
                }

                if (outgoing.Count > 0)
                {
                    _outgoing[routineId] = outgoing;
                }
            }
        }

        foreach (var (id, sites) in callers)
        {
            _callerSites[id] = sites;
        }

        foreach (var (id, kinds) in metadata)
        {
            _metadataKinds[id] = kinds;
        }
    }

    /// <summary>Процедуры по убыванию числа вызовов.</summary>
    public IReadOnlyList<ConventionModuleUsage> RankRoutines(int limit) =>
    [
        .. _routines
            .Select(routine => new ConventionModuleUsage(
                routine.RoutineId,
                OwnerOf(routine.Module),
                routine.RoutineId,
                routine.Name,
                routine.Module,
                _incoming.TryGetValue(routine.RoutineId, out var count) ? count : 0))
            .Where(static item => item.Uses > 0)
            .OrderByDescending(static item => item.Uses)
            .ThenBy(static item => item.Name, StringComparer.Ordinal)
            .Take(limit)
    ];

    /// <summary>Сведения о символах процедур: строки, параметры и экспортность из разбора.</summary>
    public IReadOnlyList<ConventionSymbol> SymbolsOf(IReadOnlyCollection<string> routineIds)
    {
        var wanted = new HashSet<string>(routineIds, StringComparer.Ordinal);
        var result = new List<ConventionSymbol>();
        foreach (var module in _result.Modules)
        {
            foreach (var routine in module.Routines)
            {
                var id = $"routine:module:{module.Path}#{routine.Name}";
                if (!wanted.Contains(id))
                {
                    continue;
                }

                result.Add(new ConventionSymbol(
                    routine.Name,
                    id,
                    module.Path,
                    routine.Kind.ToString(),
                    routine.IsExport,
                    routine.StartLine,
                    routine.EndLine,
                    routine.Parameters.Count == 0 ? null : string.Join(", ", routine.Parameters),
                    null,
                    module.OwnerId));
            }
        }

        return result;
    }

    /// <summary>Кто вызывает процедуру: модуль и строка вызова из графа.</summary>
    public IReadOnlyList<ConventionCallSite> CallerSites(string routineId, int limit) =>
        !_callerSites.TryGetValue(routineId, out var sites)
            ? []
            : [.. sites.Take(limit).Select(site => new ConventionCallSite(routineId, site.Module, site.Line, site.Detail))];

    /// <summary>Кто вызывает метод платформы: процедуры с числом вызовов.</summary>
    /// <remarks>
    /// Узлы <c>platform:Метод</c> и <c>call:Метод</c> — это методы платформы. Внешняя цель
    /// с квалификатором общего модуля конфигурации («РаботаСДанными.ЗагрузитьДанные») методом
    /// платформы не считается: это вызов процедуры конфигурации с похожим именем.
    /// </remarks>
    public IReadOnlyList<ConventionRoutine> PlatformMatches(string platformMethod, int limit)
    {
        var method = platformMethod.Trim();
        var found = new List<ConventionRoutine>();
        foreach (var (targetId, count) in _incoming)
        {
            if (!IsPlatformCall(targetId, method, _commonModules) || count == 0)
            {
                continue;
            }

            var module = ModuleOf(targetId);
            var name = NameOf(targetId);
            found.Add(new ConventionRoutine(
                targetId,
                name,
                OwnerOf(module),
                module,
                count,
                count,
                null,
                0,
                method));
        }

        return
        [
            .. found
                .OrderByDescending(static item => item.Uses)
                .ThenBy(static item => item.Name, StringComparer.Ordinal)
                .Take(limit)
        ];
    }

    /// <summary>К каким объектам метаданных обращается процедура.</summary>
    public IReadOnlyList<string> MetadataKindsOf(string routineId) =>
        _metadataKinds.TryGetValue(routineId, out var kinds) ? kinds : [];

    /// <summary>Процедуры, имя которых начинается с указанных слов.</summary>
    public IReadOnlyList<string> RoutinesByNamePrefix(IReadOnlyCollection<string> namePrefixes, int limit)
    {
        var found = new List<string>();
        foreach (var routine in _routines)
        {
            if (namePrefixes.Any(prefix => routine.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            {
                found.Add(routine.RoutineId);
            }
        }

        return [.. found.Take(limit)];
    }

    /// <summary>Процедуры, найденные по словам имени и параметров.</summary>
    public IReadOnlyList<string> RoutinesByTerms(IReadOnlyCollection<string> terms, int limit)
    {
        var found = new List<string>();
        foreach (var module in _result.Modules)
        {
            foreach (var routine in module.Routines)
            {
                var haystack = routine.Name + " " + string.Join(' ', routine.Parameters);
                if (terms.Any(term => haystack.Contains(term, StringComparison.OrdinalIgnoreCase)))
                {
                    found.Add($"routine:module:{module.Path}#{routine.Name}");
                }
            }
        }

        return [.. found.Take(limit)];
    }

    /// <summary>Объекты-владельцы модулей из разбора: связь «объект → модуль» уже разобрана.</summary>
    public IReadOnlyDictionary<string, string> ModuleOwners(IReadOnlyCollection<string> modulePaths)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var module in _result.Modules)
        {
            if (module.OwnerId is { Length: > 0 } owner && modulePaths.Contains(module.Path))
            {
                result[module.Path] = owner;
            }
        }

        return result;
    }

    /// <summary>Сведения о процедурах: символы, счётчики, примеры вызова и обращения к метаданным.</summary>
    /// <remarks>
    /// Всё уже собрано при построении источника, поэтому ответ не зависит от числа процедур.
    /// </remarks>
    public IReadOnlyList<ConventionEvidence> EvidenceOf(IReadOnlyCollection<string> routineIds, string? platformMethod)
    {
        var method = platformMethod?.Trim() ?? string.Empty;
        var result = new List<ConventionEvidence>(routineIds.Count);
        foreach (var id in routineIds)
        {
            var symbol = SymbolsOf([id]).FirstOrDefault();
            var sites = _callerSites.TryGetValue(id, out var found) ? found : [];
            var kinds = _metadataKinds.TryGetValue(id, out var metadata) ? metadata : [];
            var own = method.Length == 0 || !_outgoing.TryGetValue(id, out var outgoing)
                ? 0
                : outgoing.Count(call => MatchesMethod(call.Detail, method));
            result.Add(new ConventionEvidence(
                id,
                symbol,
                _incoming.TryGetValue(id, out var uses) ? uses : 0,
                sites.Select(static site => site.Module).Distinct(StringComparer.Ordinal).Count(),
                sites.Count > 0 ? sites[0].Module : symbol?.ModulePath ?? ModuleOf(id),
                sites.Count > 0 ? sites[0].Line : symbol?.StartLine,
                sites.Count > 0 ? sites[0].Detail : null,
                kinds,
                own));
        }

        return result;
    }

    /// <summary>Вызывает ли текст вызова метод платформы: «Объект.Записать» — да, «Модуль.ЗаписатьИтог» — нет.</summary>
    private static bool MatchesMethod(string? detail, string method)
    {
        if (string.IsNullOrEmpty(detail))
        {
            return false;
        }

        var separator = detail.LastIndexOf('.');
        var name = separator < 0 ? detail : detail[(separator + 1)..];
        return string.Equals(name, method, StringComparison.OrdinalIgnoreCase);
    }

    private static string? KindOfId(string id)
    {
        var separator = id.IndexOf('.');
        return separator > 0 ? id[..separator] : null;
    }

    private static bool IsPlatformCall(string targetId, string method, HashSet<string> commonModules)
    {
        if (targetId.StartsWith("platform:", StringComparison.Ordinal))
        {
            var callee = targetId["platform:".Length..];
            var separator = callee.LastIndexOf('.');
            var name = separator < 0 ? callee : callee[(separator + 1)..];
            return string.Equals(name, method, StringComparison.OrdinalIgnoreCase);
        }

        if (!targetId.StartsWith("call:", StringComparison.Ordinal))
        {
            return false;
        }

        var external = targetId["call:".Length..];
        var dot = external.LastIndexOf('.');
        if (dot < 0 || !string.Equals(external[(dot + 1)..], method, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !commonModules.Contains(external[..dot]);
    }

    private static string NameOf(string routineId)
    {
        var separator = routineId.LastIndexOf('#');
        return separator < 0 || separator == routineId.Length - 1 ? routineId : routineId[(separator + 1)..];
    }

    private string ModuleOf(string routineId) =>
        _moduleOfRoutine.TryGetValue(routineId, out var module) ? module : routineId;

    private string? OwnerOf(string modulePath) =>
        _ownerOfModule.TryGetValue("module:" + modulePath, out var owner) ? owner : null;
}
