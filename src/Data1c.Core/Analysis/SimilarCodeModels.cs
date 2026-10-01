namespace Data1c.Core.Analysis;

/// <summary>Вид признака, по которому сравниваются реализации кода.</summary>
/// <remarks>
/// Порядок перечисления — от самого сильного признака к самому слабому: так же заданы веса
/// в <see cref="SimilarCodeOptions"/>.
/// </remarks>
public enum SimilarCodeSignal
{
    /// <summary>Вызов метода платформы: узел <c>platform:Тип.Метод</c>.</summary>
    PlatformCall,

    /// <summary>Обращение к объекту метаданных — из кода или из текста запроса.</summary>
    MetadataReference,

    /// <summary>
    /// Вызов процедуры или функции конфигурации: связь Calls на узел <c>routine:…#Имя</c>.
    /// Сравнивается идентификатор цели, а не текст вызова: «ОбщийМодуль.Метод» и «Метод» внутри
    /// этого модуля ведут в один узел.
    /// </summary>
    RoutineCall,

    /// <summary>Близость имени, шапки комментария и параметров: термы (terms_fts и BM25).</summary>
    Terms,

    /// <summary>
    /// Вызов, цель которого не разрешилась (<c>call:…</c>): сравнивается только имя метода без
    /// квалификатора — слабый сигнал, он не доказывает, что вызван тот же самый метод.
    /// </summary>
    UnresolvedCall,

    /// <summary>Штраф за разницу в объёме кода: строки и операторы.</summary>
    Size,
}

/// <summary>Признак кода: значение и уточнение, откуда оно взято.</summary>
/// <param name="Value">
/// Сам признак: «Запрос.Выполнить», «Catalog.Товары», у вызова процедуры — идентификатор цели
/// <c>routine:module:…#Имя</c>, у неразрешённого вызова — имя метода строчными без квалификатора.
/// </param>
/// <param name="Detail">
/// Уточнение: для обращения к метаданным — контекст <c>code</c> или <c>query</c>, для вызова —
/// текст вызова, как он записан в коде (нужен только для объяснения в ответе).
/// </param>
public sealed record SimilarCodeFeature(string Value, string? Detail = null);

/// <summary>
/// Разрешение вызова в узел процедуры конфигурации. Признак вызова процедуры — это идентификатор
/// цели, поэтому текст вызова черновика нужно разрешить так же, как это делает индекс: по
/// квалификатору («ОбщийМодуль.Метод») или по модулю черновика для вызова без квалификатора.
/// </summary>
/// <param name="qualifier">Квалификатор вызова («ОбщийМодуль»); null — вызов без квалификатора.</param>
/// <param name="method">Имя вызываемого метода.</param>
/// <returns>Идентификатор узла <c>routine:module:…#Имя</c> или null, если цель не разрешилась.</returns>
public delegate string? RoutineCallResolver(string? qualifier, string method);

/// <summary>
/// Признаки фрагмента кода: что в нём вызывается, к чему обращается и как он назван.
/// Собираются из черновика (<see cref="SimilarCode.Describe"/>) и сравниваются с признаками кандидатов.
/// </summary>
/// <param name="Name">Имя процедуры (пусто, если в тексте процедуры нет).</param>
/// <param name="PlatformCalls">Вызовы методов платформы.</param>
/// <param name="MetadataReferences">Обращения к объектам метаданных.</param>
/// <param name="RoutineCalls">
/// Вызовы процедур и функций конфигурации с разрешённой целью: значение — идентификатор узла
/// <c>routine:module:…#Имя</c>, уточнение — текст вызова.
/// </param>
/// <param name="UnresolvedCalls">
/// Вызовы, цель которых разрешить не удалось: значение — имя метода строчными без квалификатора.
/// Идут в слабый сигнал и в выборке кандидатов не участвуют.
/// </param>
/// <param name="Terms">Термы имени, шапки комментария и параметров: идут в запрос к terms_fts.</param>
/// <param name="Lines">Число строк фрагмента.</param>
/// <param name="Statements">Оценка числа операторов: вызовы и обращения.</param>
public sealed record SimilarCodeFeatures(
    string Name,
    IReadOnlyList<SimilarCodeFeature> PlatformCalls,
    IReadOnlyList<SimilarCodeFeature> MetadataReferences,
    IReadOnlyList<SimilarCodeFeature> RoutineCalls,
    IReadOnlyList<SimilarCodeFeature> UnresolvedCalls,
    IReadOnlyList<string> Terms,
    int Lines,
    int Statements)
{
    /// <summary>Пустые признаки: у фрагмента не нашлось ни вызовов, ни обращений, ни термов.</summary>
    public static SimilarCodeFeatures Empty { get; } = new(string.Empty, [], [], [], [], [], 0, 0);

    /// <summary>Сколько всего признаков-вызовов и обращений (без термов).</summary>
    public int SignalCount =>
        PlatformCalls.Count + MetadataReferences.Count + RoutineCalls.Count + UnresolvedCalls.Count;
}

/// <summary>
/// Процедура-кандидат: всё, что нужно для сравнения с черновиком. Собирается источником данных
/// (<see cref="ISimilarCodeSource"/>) — из SQLite-индекса или из готового разбора в памяти.
/// </summary>
/// <param name="Id">Идентификатор узла процедуры: <c>routine:module:…#Имя</c>.</param>
/// <param name="Name">Имя процедуры.</param>
/// <param name="ModulePath">Путь модуля внутри выгрузки.</param>
/// <param name="OwnerId">Объект-владелец модуля, если он известен.</param>
/// <param name="StartLine">Первая строка процедуры в модуле.</param>
/// <param name="EndLine">Последняя строка процедуры в модуле.</param>
/// <param name="Statements">Оценка числа операторов: вызовы и обращения внутри процедуры.</param>
/// <param name="PlatformCalls">Вызовы методов платформы.</param>
/// <param name="MetadataReferences">Обращения к объектам метаданных.</param>
/// <param name="RoutineCalls">
/// Вызовы процедур конфигурации: значение — идентификатор разрешённой цели <c>routine:…#Имя</c>,
/// уточнение — текст вызова, как он записан у кандидата.
/// </param>
/// <param name="UnresolvedCalls">Вызовы с неразрешённой целью (<c>call:…</c>): имя метода строчными.</param>
/// <param name="TermScore">Оценка близости термов по BM25, приведённая к отрезку 0…1.</param>
/// <param name="CommentHead">Шапка комментария над процедурой, если она читалась при сборке индекса.</param>
/// <param name="Parameters">Параметры процедуры одной строкой, если они есть.</param>
public sealed record SimilarCodeProfile(
    string Id,
    string Name,
    string ModulePath,
    string? OwnerId,
    int StartLine,
    int EndLine,
    int Statements,
    IReadOnlyList<SimilarCodeFeature> PlatformCalls,
    IReadOnlyList<SimilarCodeFeature> MetadataReferences,
    IReadOnlyList<SimilarCodeFeature> RoutineCalls,
    IReadOnlyList<SimilarCodeFeature> UnresolvedCalls,
    double TermScore,
    string? CommentHead = null,
    string? Parameters = null)
{
    /// <summary>Число строк процедуры.</summary>
    public int Lines => EndLine - StartLine + 1;
}

/// <summary>Что вернул источник данных: кандидаты, веса признаков черновика и пояснения.</summary>
/// <param name="Profiles">Ограниченный набор процедур-кандидатов.</param>
/// <param name="Weights">
/// Веса значимых признаков черновика по видам сигналов: чем реже признак встречается в конфигурации,
/// тем он весомее. Признака нет в словаре — он не различает реализации и в оценке не участвует.
/// </param>
/// <param name="Notes">Пояснения к выборке: что пропущено и почему.</param>
/// <param name="Recognized">
/// Признаки черновика, которые действительно есть в конфигурации: вызов метода платформы нашёлся
/// среди узлов <c>platform:…</c>, обращение — среди объектов метаданных. Остальные признаки черновика
/// (например, вызов процедуры своего же модуля) в конфигурации ни на что не похожи.
/// </param>
public sealed record SimilarCodeCandidates(
    IReadOnlyList<SimilarCodeProfile> Profiles,
    IReadOnlyDictionary<SimilarCodeSignal, IReadOnlyDictionary<string, double>> Weights,
    IReadOnlyList<string> Notes,
    IReadOnlyDictionary<SimilarCodeSignal, IReadOnlyList<string>>? Recognized = null);

/// <summary>
/// Источник процедур-кандидатов для поиска похожего кода. Реализации: SQLite-индекс
/// (<c>IndexSimilarCodeSource</c> в Data1c.Store) и готовый разбор в памяти.
/// </summary>
/// <remarks>
/// Кандидаты выбираются только по признакам, у которых есть идентификатор цели в графе
/// (вызовы платформы, обращения к метаданным, вызовы процедур) и по термам имени. Вызовы
/// с неразрешённой целью (<c>call:…</c>) кандидатов не выбирают: их цель — текст вызова,
/// одного имени для выборки мало (столько же «похожих» нашлось бы на любой одноимённый метод).
/// Они приходят слабым сигналом <see cref="SimilarCodeSignal.UnresolvedCall"/> и лишь
/// добавляют немного к оценке уже найденных процедур.
/// </remarks>
public interface ISimilarCodeSource
{
    /// <summary>
    /// Возвращает ограниченный набор кандидатов: только те процедуры, у которых есть общий
    /// с черновиком признак. Полный просмотр всех процедур запрещён — на большой выгрузке
    /// это сотни тысяч строк.
    /// </summary>
    /// <param name="features">Признаки черновика.</param>
    /// <param name="candidateLimit">Предел числа кандидатов.</param>
    /// <param name="excludeId">Процедура, которую нужно исключить из выдачи (сама себя).</param>
    SimilarCodeCandidates FindCandidates(SimilarCodeFeatures features, int candidateLimit, string? excludeId);
}

/// <summary>Один совпавший признак: что именно совпало у кандидата с черновиком.</summary>
/// <param name="Signal">Вид сигнала.</param>
/// <param name="Value">Значение признака.</param>
/// <param name="Detail">Уточнение: для обращения к метаданным — <c>code</c> или <c>query</c>.</param>
public sealed record SimilarCodeMatch(SimilarCodeSignal Signal, string Value, string? Detail = null);

/// <summary>Найденная похожая реализация: оценка, совпавшие признаки и причина попадания в список.</summary>
/// <param name="Id">Идентификатор процедуры: его принимает инструмент <c>code</c>.</param>
/// <param name="Name">Имя процедуры.</param>
/// <param name="ModulePath">Путь модуля внутри выгрузки.</param>
/// <param name="OwnerId">Объект-владелец модуля, если он известен.</param>
/// <param name="StartLine">Первая строка процедуры.</param>
/// <param name="EndLine">Последняя строка процедуры.</param>
/// <param name="Lines">Число строк процедуры.</param>
/// <param name="Statements">Оценка числа операторов.</param>
/// <param name="Score">Итоговая оценка похожести: 0…1, больше — похожее.</param>
/// <param name="Matches">Совпавшие признаки.</param>
/// <param name="Reason">Почему кандидат попал в список: человекочитаемое объяснение.</param>
public sealed record SimilarCodeCandidate(
    string Id,
    string Name,
    string ModulePath,
    string? OwnerId,
    int StartLine,
    int EndLine,
    int Lines,
    int Statements,
    double Score,
    IReadOnlyList<SimilarCodeMatch> Matches,
    string Reason);

/// <summary>Результат поиска похожего кода.</summary>
/// <param name="Candidates">Кандидаты по убыванию оценки.</param>
/// <param name="Draft">Признаки, собранные из черновика: видно, что вообще искалось.</param>
/// <param name="Considered">Сколько кандидатов рассмотрел источник данных.</param>
/// <param name="Notes">Пояснения: что пропущено, чего не хватило, почему ответ пуст.</param>
/// <param name="Recognized">Признаки черновика, которые нашлись в конфигурации.</param>
public sealed record SimilarCodeResult(
    IReadOnlyList<SimilarCodeCandidate> Candidates,
    SimilarCodeFeatures Draft,
    int Considered,
    IReadOnlyList<string> Notes,
    IReadOnlyDictionary<SimilarCodeSignal, IReadOnlyList<string>>? Recognized = null);

/// <summary>
/// Веса поиска похожего кода. Значения подобраны по замеру на выгрузке 2,9 ГБ (570 тысяч узлов,
/// 2,6 млн связей) и отражают надёжность признака.
/// </summary>
/// <remarks>
/// <para>Порядок весов — по убыванию надёжности признака:</para>
/// <list type="number">
/// <item><description>
/// вызовы методов платформы (0.40): набор вызовов платформы — это и есть «как сделано»,
/// он почти не зависит от предметной области и от имён; ради него и заводится узел platform:;
/// </description></item>
/// <item><description>
/// обращения к метаданным (0.28): совпадение объектов из кода и, главное, из текстов запросов
/// говорит «про то же самое», даже когда текст написан другими словами;
/// </description></item>
/// <item><description>
/// вызовы процедур конфигурации (0.18): общие вспомогательные процедуры — признак полезный,
/// но шумный: «ОбщегоНазначения.ЗначениеРеквизитаОбъекта» вызывают почти везде;
/// </description></item>
/// <item><description>
/// близость имени и термов (0.14): имя и комментарий — самое слабое доказательство «той же
/// реализации», но единственное, что остаётся у короткого фрагмента без вызовов;
/// </description></item>
/// <item><description>
/// вызовы с неразрешённой целью (0.08): совпало только имя метода без квалификатора — это
/// не доказывает, что вызван тот же метод (одноимённые методы есть у разных модулей и объектов),
/// поэтому сигнал слабее всех остальных и не может перевесить ни платформенные вызовы, ни
/// обращения к метаданным.
/// </description></item>
/// </list>
/// <para>
/// Веса отдельных признаков внутри категории считает источник данных: чем реже признак встречается
/// в конфигурации, тем он весомее (idf = log(всего процедур / процедур с признаком)).
/// Частые вызовы («Структура.Вставить», «Массив.Добавить») вес не получают вовсе: они не
/// различают реализации, и любой черновик нашёл бы по ним тысячи «похожих».
/// </para>
/// <para>
/// Исключение — вызовы с неразрешённой целью: частоты по конфигурации для них не считаются
/// (выборку кандидатов они не делают, см. <see cref="ISimilarCodeSource"/>), все они получают
/// единичный вес внутри своей категории, а сила сигнала ограничена его общим весом 0.08.
/// </para>
/// </remarks>
public sealed record SimilarCodeOptions
{
    /// <summary>Вес совпадения вызовов методов платформы.</summary>
    public double PlatformWeight { get; init; } = 0.40;

    /// <summary>Вес совпадения обращений к объектам метаданных.</summary>
    public double MetadataWeight { get; init; } = 0.28;

    /// <summary>Вес совпадения вызовов процедур конфигурации.</summary>
    public double RoutineWeight { get; init; } = 0.18;

    /// <summary>Вес близости имени, комментария и параметров.</summary>
    public double TermsWeight { get; init; } = 0.14;

    /// <summary>
    /// Вес совпадения вызовов с неразрешённой целью (<c>call:…</c>): сравнивается только имя метода
    /// без квалификатора. Самый слабый сигнал: он добавляет немного к оценке уже найденных процедур
    /// и не может перевесить ни платформенные вызовы, ни обращения к метаданным.
    /// </summary>
    public double UnresolvedCallWeight { get; init; } = 0.08;

    /// <summary>
    /// Наибольший штраф за разницу в объёме (число строк и операторов). Штраф слабый:
    /// он лишь разводит равные по признакам реализации, а «гигантов» отсекает поправка
    /// на лишние признаки (см. <see cref="SimilarCode"/>).
    /// </summary>
    public double SizePenalty { get; init; } = 0.10;

    /// <summary>Ниже этой оценки кандидат в ответ не попадает.</summary>
    public double MinScore { get; init; } = 0.05;

    /// <summary>Сколько кандидатов запрашивать у источника данных.</summary>
    public int CandidateLimit { get; init; } = 600;

    /// <summary>Сколько кандидатов возвращать по умолчанию.</summary>
    public int DefaultLimit { get; init; } = 10;

    /// <summary>Наибольшее число кандидатов в ответе.</summary>
    public int MaxLimit { get; init; } = 50;
}
