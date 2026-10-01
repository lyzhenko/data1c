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
/// <param name="Recognition">Объяснение распознавания: по каким словам и методам узнан приём.</param>
public sealed record ConventionAnswer(
    string Intent,
    string Question,
    string? PlatformMethod,
    IReadOnlyList<ConventionModuleUsage> Modules,
    IReadOnlyList<ConventionRoutine> Routines,
    IReadOnlyList<ConventionCallSite> CallSites,
    string Hint,
    string? Note = null,
    ConventionRecognition? Recognition = null);

/// <summary>Ступень, по которой приём распознан в тексте запроса.</summary>
public enum ConventionMatchStep
{
    /// <summary>Приём не распознан: процедуры подбираются прямо по словам запроса.</summary>
    None,

    /// <summary>Приём узнан по имени метода платформы, встретившемуся в тексте запроса.</summary>
    PlatformMethod,

    /// <summary>Приём узнан по ключевым словам приёма и словам его имени.</summary>
    Keywords,
}

/// <summary>
/// Объяснение распознавания: каким приёмом признан запрос и по каким именно словам и методам.
/// Нужно агенту, чтобы понять ответ и переформулировать запрос, если приём узнан неверно.
/// </summary>
/// <param name="IntentId">Идентификатор приёма каталога; null, если приём не распознан или задан метод платформы.</param>
/// <param name="Title">Название приёма так, как его видит агент.</param>
/// <param name="Step">Ступень распознавания: метод платформы, ключевые слова или ничего.</param>
/// <param name="PlatformMethod">Метод платформы, найденный в тексте запроса.</param>
/// <param name="Words">Слова запроса, совпавшие с признаками приёма.</param>
/// <param name="Keywords">Ключевые слова приёма, совпавшие со словами запроса.</param>
/// <param name="NameTokens">Слова имени приёма, совпавшие со словами запроса.</param>
/// <param name="Score">Вес совпадения: чем больше, тем увереннее распознан приём.</param>
/// <param name="Summary">Готовая фраза для агента: по каким словам и методам распознан приём.</param>
public sealed record ConventionRecognition(
    string? IntentId,
    string Title,
    ConventionMatchStep Step,
    string? PlatformMethod,
    IReadOnlyList<string> Words,
    IReadOnlyList<string> Keywords,
    IReadOnlyList<string> NameTokens,
    int Score,
    string Summary);

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
/// <para>Приём распознаётся по каталогу из двадцати типовых задач. Признак — имя метода платформы,
/// встретившееся в запросе, и слова запроса: ключевые слова приёма и слова его имени. Каждое совпавшее
/// слово приносит вес: три-четыре за редкое слово и вдвое меньше за общее, которое встречается у
/// нескольких приёмов. Редкое имя метода платформы весомее всего, общее («Записать», «Получить») —
/// только подтверждает уже найденные слова.</para>
/// <para>Приём принимается, если совпало два слова запроса, либо одно редкое слово, либо в тексте есть
/// метод платформы. Из принятых берётся приём с наибольшим весом, а ничьи разрешаются по числу совпавших
/// слов, затем по числу редких, затем по порядку в каталоге: один и тот же запрос всегда даёт один и
/// тот же приём. Если ни один приём не принят, приём не выдумывается: процедуры ищутся по словам запроса,
/// и в ответе сказано, почему приём не распознан (см. <see cref="ConventionRecognition"/>).</para>
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

    /// <summary>
    /// Вес слова при распознавании приёма: редкое слово весомее общего. Общее слово весит два,
    /// редкое — четыре: два общих слова запроса перевешивают одно редкое, иначе «получить реквизит
    /// объекта» проигрывало бы приёму «записать объект» из-за одного слова «объект».
    /// </summary>
    private const int StrongTermWeight = 4;

    private const int WeakTermWeight = 2;

    /// <summary>Надбавка за метод платформы в тексте: редкое имя точнее общего слова.</summary>
    private const int PlatformMethodWeight = 5;

    private const int CommonMethodWeight = 2;

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

    /// <summary>
    /// Каталог типовых приёмов конфигурации 1С: двадцать задач, которые в конфигурациях решают чаще всего.
    /// </summary>
    /// <remarks>
    /// <para>У приёма три набора признаков. <c>PlatformMethods</c> — имена методов платформы: они ищутся
    /// в тексте запроса, и по ним же в индексе набираются процедуры, которые так делают. <c>Keywords</c> —
    /// слова задачи («записать», «реквизит», «наименование»). <c>NameTokens</c> — слова, с которых обычно
    /// начинается имя процедуры или её шапка комментария («ЗаписатьНаборЗаписей», «ПроверитьЗаполнение»):
    /// по ним процедура получает вес при подборе.</para>
    /// <para>Списки намеренно короткие: каждый метод платформы — это отдельный запрос к индексу
    /// на 2,6 млн связей, поэтому у приёма не больше четырёх методов. Порядок приёмов значим:
    /// при равном весе распознавания выигрывает объявленный раньше.</para>
    /// </remarks>
    private static readonly ConventionIntent[] Catalog =
    [
        new(
            "записать объект",
            "Запись объекта",
            ["записать", "сохранить", "запись", "создать", "объект", "поместить"],
            ["Записать", "ЗаписатьИЗакрыть", "СоздатьЭлемент", "СоздатьДокумент"],
            ["Catalog", "Document", "ExchangePlan"],
            ["Записать", "Запись", "Сохранить", "Создать", "Поместить", "ЗаписатьОбъект"]),

        new(
            "провести документ",
            "Проведение документа",
            ["провести", "документ", "регистратор", "движения", "перепровести"],
            ["Провести", "Записать", "РежимЗаписиДокумента"],
            ["Document", "DocumentJournal"],
            ["Провести", "Проведение", "ПровестиДокумент", "ОбработкаПроведения", "Перепровести", "Регистратор"]),

        new(
            "получить реквизит объекта",
            "Чтение реквизита объекта",
            ["получить", "прочитать", "реквизит", "свойство", "значение", "объект"],
            ["ЗначениеРеквизитаОбъекта", "ЗначениеРеквизитаОбъектов"],
            ["Catalog", "Document", "InformationRegister", "ExchangePlan"],
            ["Реквизит", "Свойство", "Значение", "ЗначениеРеквизита", "ПолучитьРеквизит", "ПрочитатьРеквизит"]),

        new(
            "установить реквизит объекта",
            "Заполнение реквизитов объекта",
            ["установить", "заполнить", "присвоить", "реквизит", "свойство", "значение", "изменить", "объект"],
            ["ЗаполнитьЗначенияСвойств"],
            ["Catalog", "Document"],
            ["Установить", "Заполнить", "Присвоить", "УстановитьРеквизит", "УстановитьСвойство", "ЗаполнитьРеквизиты"]),

        new(
            "найти по наименованию",
            "Поиск объекта по наименованию",
            ["найти", "поиск", "наименование", "название", "ссылка"],
            ["НайтиПоНаименованию", "НайтиПоРеквизиту"],
            ["Catalog", "Document", "ExchangePlan"],
            ["Найти", "Поиск", "Наименование", "Название", "НайтиПоНаименованию", "Ссылка"]),

        new(
            "найти по коду или артикулу",
            "Поиск объекта по коду, номеру или артикулу",
            ["найти", "поиск", "код", "артикул", "номер", "штрихкод", "серия"],
            ["НайтиПоКоду", "НайтиПоНомеру", "НайтиПоРеквизиту"],
            ["Catalog", "Document"],
            ["Найти", "НайтиПоКоду", "НайтиПоАртикулу", "НайтиПоНомеру", "НайтиПоШтрихкоду", "Код", "Артикул", "Номер"]),

        new(
            "прочитать данные запросом",
            "Чтение данных запросом",
            ["запрос", "выборка", "выбрать", "данные", "таблица", "параметр", "прочитать", "загрузить"],
            ["Выполнить", "ВыполнитьПакет", "Выбрать", "УстановитьПараметр"],
            ["Catalog", "Document", "InformationRegister", "AccumulationRegister", "AccountingRegister"],
            ["Запрос", "Выборка", "Выбрать", "Прочитать", "Загрузить", "Данные", "ПрочитатьДанные", "ТекстЗапроса"]),

        new(
            "записать набор записей регистра",
            "Запись набора записей регистра",
            ["набор", "записей", "регистр", "движения", "сведения", "остатки", "обороты", "записать"],
            ["СоздатьНаборЗаписей", "Записать"],
            ["InformationRegister", "AccumulationRegister", "AccountingRegister"],
            ["ЗаписатьНабор", "НаборЗаписей", "ЗаписатьРегистр", "ЗаписатьДвижения", "Движения", "Регистр", "СоздатьНаборЗаписей"]),

        new(
            "вывести сообщение пользователю",
            "Сообщение пользователю",
            ["сообщить", "сообщение", "пользователь", "показать", "предупреждение", "ошибка", "вопрос", "вывести", "оповестить"],
            ["Сообщить", "СообщениеПользователю", "ПоказатьПредупреждение", "ПоказатьВопрос"],
            [],
            ["Сообщить", "Сообщение", "Предупреждение", "Вопрос", "Пользователю", "Оповестить", "ВывестиСообщение"]),

        new(
            "выполнить на сервере или в фоне",
            "Выполнение на сервере, в фоне и на клиенте",
            ["сервер", "фон", "фоновое", "задание", "клиент", "асинхронно", "выполнить", "запустить"],
            ["ВыполнитьВФоне", "ЗапуститьФоновоеЗадание", "ВыполнитьНаСервере", "Выполнить"],
            [],
            ["Фоновое", "Фон", "Задание", "Сервер", "Клиент", "Асинхронно", "Выполнить", "Запустить", "ВыполнитьВФоне"]),

        new(
            "добавить в коллекцию",
            "Добавление элемента в коллекцию",
            ["добавить", "вставить", "коллекция", "массив", "структура", "соответствие", "список", "таблица", "значений"],
            ["Добавить", "Вставить", "ДобавитьЗначение", "ДобавитьСтроку"],
            [],
            ["Добавить", "Вставить", "ДобавитьСтроку", "ДобавитьЗначение", "Заполнить", "Массив", "Структура", "Соответствие", "Список"]),

        new(
            "сформировать печатную форму",
            "Формирование печатной формы",
            ["печатная", "печать", "форма", "сформировать", "макет", "область", "табличный", "документ", "вывести"],
            ["ПолучитьМакет", "ПолучитьОбласть", "Вывести", "ТабличныйДокумент"],
            [],
            ["ПечатнаяФорма", "Печать", "Сформировать", "Вывести", "Макет", "ТабличныйДокумент", "ПолучитьОбласть"]),

        new(
            "сохранить или прочитать файл",
            "Работа с файлами",
            ["файл", "каталог", "папка", "сохранить", "прочитать", "поместить", "выгрузить", "двоичные"],
            ["НачатьПомещениеФайла", "ПоместитьФайл", "ПолучитьФайл", "НайтиФайлы"],
            [],
            ["Файл", "СохранитьФайл", "ПрочитатьФайл", "ЗаписатьФайл", "ВыгрузитьФайл", "ПоместитьФайл", "ПолучитьФайл", "Каталог"]),

        new(
            "сериализовать в XML или JSON",
            "Сериализация в XML и JSON",
            ["сериализовать", "десериализовать", "сериализация", "xml", "json", "обмен"],
            ["ЗаписатьXML", "ПрочитатьXML", "ЗаписатьJSON", "ПрочитатьJSON"],
            [],
            ["XML", "JSON", "ЗаписатьXML", "ПрочитатьXML", "ЗаписатьJSON", "ПрочитатьJSON", "Сериализовать", "Десериализовать"]),

        new(
            "получить представление объекта",
            "Представление объекта",
            ["представление", "получить", "отобразить", "показать", "описание"],
            ["Строка"],
            ["Catalog", "Document"],
            ["Представление", "ПредставлениеОбъекта", "ПолучитьПредставление", "СтрокаПредставления", "ОписаниеОбъекта", "Наименование"]),

        new(
            "проверить заполнение реквизитов",
            "Проверка заполнения реквизитов",
            ["проверить", "проверка", "заполнение", "заполнено", "обязательный", "реквизит"],
            ["ЗначениеЗаполнено", "ПроверитьЗаполнение"],
            ["Catalog", "Document"],
            ["ПроверитьЗаполнение", "ЗначениеЗаполнено", "ПроверитьРеквизиты", "ПроверитьЗаполненность", "ПроверкаЗаполнения"]),

        new(
            "получить константу или настройку",
            "Чтение константы или настройки",
            ["константа", "настройка", "параметр", "получить", "прочитать", "значение", "хранилище"],
            ["Получить"],
            ["Constant"],
            ["Константа", "ПолучитьКонстанту", "Настройка", "ПолучитьНастройку", "ЗагрузитьНастройку", "СохранитьНастройку", "ХранилищеНастроек"]),

        new(
            "замерить производительность",
            "Замер производительности",
            ["замерить", "замер", "производительность", "время", "быстродействие", "оптимизация", "скорость"],
            ["ЗамерПроизводительности", "ТекущаяУниверсальнаяДатаВМиллисекундах"],
            [],
            ["Замер", "ЗамерВремени", "ЗамерПроизводительности", "Производительность", "Быстродействие", "Таймер"]),

        new(
            "записать в журнал регистрации",
            "Запись в журнал регистрации",
            ["журнал", "регистрации", "записать", "событие", "логирование", "ошибка"],
            ["ЗаписьЖурналаРегистрации"],
            [],
            ["ЗаписьЖурналаРегистрации", "ЗаписатьВЖурнал", "Журнал", "Регистрации", "Логирование", "ЗаписьСобытия"]),

        new(
            "выполнить в транзакции",
            "Выполнение в транзакции",
            ["транзакция", "выполнить", "начать", "зафиксировать", "отменить", "блокировка", "целостность"],
            ["НачатьТранзакцию", "ЗафиксироватьТранзакцию", "ОтменитьТранзакцию"],
            [],
            ["Транзакция", "НачатьТранзакцию", "ЗафиксироватьТранзакцию", "ОтменитьТранзакцию", "ВыполнитьВТранзакции", "Блокировка"]),
    ];

    /// <summary>
    /// Общие признаки приёмов: слова, которые встречаются у двух и более приёмов. Такое слово весит меньше —
    /// «объект» есть и у записи, и у чтения реквизита, а «наименование» — у поиска и у представления.
    /// Список выводится из каталога, чтобы не расходиться с ним при правках.
    /// </summary>
    private static readonly HashSet<string> WeakTerms = CollectWeakTerms();

    /// <summary>
    /// Методы платформы, которые встречаются в задачах слишком часто, чтобы быть признаком приёма:
    /// «Записать» есть и у записи объекта, и у записи набора записей. Они дают меньшую надбавку
    /// за совпадение, но по-прежнему участвуют в подборе процедур.
    /// </summary>
    private static readonly HashSet<string> CommonMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "Записать", "ЗаписатьИЗакрыть", "Получить", "Прочитать", "Загрузить", "Сохранить",
        "Выбрать", "Выполнить", "Добавить", "Вставить", "Показать", "Вывести", "Строка",
    };


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

        var recognition = new ConventionRecognition(
            null,
            $"вызов метода платформы «{method}»",
            ConventionMatchStep.PlatformMethod,
            method,
            [],
            [],
            [],
            0,
            $"Задан точный метод платформы «{method}»: приём не подбирался, показаны процедуры, которые его вызывают.");

        return new ConventionAnswer(
            $"вызов метода платформы «{method}»",
            intent ?? method,
            method,
            modules,
            routines,
            sites,
            Hint(routines.Count, method: method),
            note,
            recognition);
    }

    /// <summary>Ответ на свободный текст намерения: распознанный приём, рейтинг и примеры.</summary>
    private static ConventionAnswer IntentAnswer(IConventionQuery query, string text, int limit)
    {
        var tokens = Tokenize(text);
        var match = Recognize(text, tokens);
        var intent = match?.Intent;
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
        var recognition = match is null ? NoRecognition(tokens) : RecognitionOf(match);

        return new ConventionAnswer(
            title,
            text,
            null,
            modules,
            top,
            CallSitesOf(top),
            Hint(top.Count),
            note,
            recognition);
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
    /// Совпадение запроса с приёмом каталога: слова, метод платформы и вес совпадения.
    /// </summary>
    /// <param name="Intent">Приём каталога.</param>
    /// <param name="Index">Место приёма в каталоге: им разрешаются ничьи.</param>
    /// <param name="Score">Вес совпадения: редкие признаки весомее общих.</param>
    /// <param name="Words">Слова запроса, совпавшие с признаками приёма.</param>
    /// <param name="Keywords">Совпавшие ключевые слова приёма.</param>
    /// <param name="NameTokens">Совпавшие слова имени приёма.</param>
    /// <param name="PlatformMethod">Метод платформы, найденный в тексте запроса.</param>
    /// <param name="Specific">Слова запроса, совпавшие с редкими (не общими) признаками.</param>
    private sealed record IntentMatch(
        ConventionIntent Intent,
        int Index,
        int Score,
        IReadOnlyList<string> Words,
        IReadOnlyList<string> Keywords,
        IReadOnlyList<string> NameTokens,
        string? PlatformMethod,
        IReadOnlyList<string> Specific);

    /// <summary>
    /// Распознаёт приём по тексту намерения: имя метода платформы, ключевые слова, слова имени приёма.
    /// </summary>
    /// <remarks>
    /// <para>Признаков два. Метод платформы, встретившийся в тексте, — самый точный: запрос
    /// «ЗначениеРеквизитаОбъекта» однозначно ведёт к чтению реквизита, а «провести документ» — к проведению,
    /// потому что редкое имя метода весит больше общего слова. Второй признак — слова: каждое совпавшее
    /// слово приносит вес четыре, если оно есть только у этого приёма, и два, если оно встречается
    /// у нескольких приёмов («объект», «значение», «документ»). Поэтому два общих слова перевешивают
    /// одно редкое: «получить реквизит объекта» — это чтение реквизита, а не запись объекта.</para>
    /// <para>Приём принимается, если совпало два слова запроса, либо одно редкое слово, либо в тексте
    /// есть метод платформы с подтверждением. Это отсекает одиночные общие слова: «телепортировать
    /// документ» приёмом не признаётся, потому что «документ» есть у нескольких приёмов и ни один
    /// не подтверждён вторым словом.</para>
    /// <para>Из принятых берётся приём с наибольшим весом; ничьи разрешаются по числу совпавших слов,
    /// затем по числу редких слов, затем по порядку в каталоге — ответ не зависит от случая.</para>
    /// </remarks>
    private static IntentMatch? Recognize(string text, IReadOnlyList<string> tokens)
    {
        if (text.Length == 0 || tokens.Count == 0)
        {
            return null;
        }

        IntentMatch? best = null;
        for (var index = 0; index < Catalog.Length; index++)
        {
            var match = MatchOf(Catalog[index], index, text, tokens);
            if (match is not null && (best is null || Better(match, best)))
            {
                best = match;
            }
        }

        return best;
    }

    /// <summary>Собирает совпадение одного приёма с запросом; null — если признаков не хватило.</summary>
    private static IntentMatch? MatchOf(ConventionIntent intent, int index, string text, IReadOnlyList<string> tokens)
    {
        var keywords = new List<string>();
        var names = new List<string>();
        var words = new List<string>();
        var specific = new List<string>();
        var score = 0;

        foreach (var token in tokens)
        {
            var matched = false;
            var strong = false;
            foreach (var keyword in intent.Keywords)
            {
                if (!Matches(token, keyword))
                {
                    continue;
                }

                matched = true;
                strong |= IsStrong(keyword, token);
                if (!keywords.Contains(keyword, StringComparer.Ordinal))
                {
                    keywords.Add(keyword);
                }
            }

            foreach (var name in intent.NameTokens)
            {
                if (!Matches(token, name))
                {
                    continue;
                }

                matched = true;
                strong |= IsStrong(name, token);
                if (!names.Contains(name, StringComparer.Ordinal))
                {
                    names.Add(name);
                }
            }

            if (!matched)
            {
                continue;
            }

            words.Add(token);
            score += strong ? StrongTermWeight : WeakTermWeight;
            if (strong)
            {
                specific.Add(token);
            }
        }

        var method = PlatformMethodInText(intent, text);
        if (method is not null)
        {
            score += CommonMethods.Contains(method) ? CommonMethodWeight : PlatformMethodWeight;
        }

        // Метод платформы принимается и сам по себе, но общий метод («Записать», «Получить») —
        // только с подтверждением словом: иначе «выполнить» тянуло бы приём выполнения запроса.
        var accepted = words.Count >= 2
            || (words.Count == 1 && specific.Count == 1)
            || (method is not null && (!CommonMethods.Contains(method) || words.Count > 0));

        return accepted
            ? new IntentMatch(intent, index, score, words, keywords, names, method, specific)
            : null;
    }

    /// <summary>Какой из двух приёмов ближе к запросу: вес, число слов, число редких слов, порядок в каталоге.</summary>
    private static bool Better(IntentMatch candidate, IntentMatch current) =>
        candidate.Score != current.Score ? candidate.Score > current.Score
        : candidate.Words.Count != current.Words.Count ? candidate.Words.Count > current.Words.Count
        : candidate.Specific.Count != current.Specific.Count ? candidate.Specific.Count > current.Specific.Count
        : candidate.Index < current.Index;

    /// <summary>Метод платформы приёма, встретившийся в тексте запроса: берётся самый длинный, то есть самый точный.</summary>
    private static string? PlatformMethodInText(ConventionIntent intent, string text)
    {
        string? found = null;
        foreach (var method in intent.PlatformMethods)
        {
            if (text.Contains(method, StringComparison.OrdinalIgnoreCase) &&
                (found is null || method.Length > found.Length))
            {
                found = method;
            }
        }

        return found;
    }

    /// <summary>Общее ли слово: такие слова встречаются у двух и более приёмов и весят меньше.</summary>
    private static bool IsWeak(string term) => WeakTerms.Contains(term.ToLowerInvariant());

    /// <summary>
    /// Сильный ли признак для этого слова запроса. Однословный признак силён, если он есть только
    /// у одного приёма. Составное имя («ЗаполнитьРеквизиты») сильное, только когда слово запроса
    /// почти совпадает с ним целиком: иначе короткое «записать» доказывало бы приём «ЗаписатьФайл»,
    /// а точный запрос «ПолучитьРеквизит» остался бы без веса.
    /// </summary>
    private static bool IsStrong(string term, string token) =>
        IsSingleWord(term)
            ? !IsWeak(term)
            : token.Length * 4 >= term.Length * 3;

    /// <summary>Объяснение распознанного приёма: слова запроса, совпавшие признаки и вес.</summary>
    private static ConventionRecognition RecognitionOf(IntentMatch match) => new(
        match.Intent.Id,
        match.Intent.Title,
        match.PlatformMethod is null ? ConventionMatchStep.Keywords : ConventionMatchStep.PlatformMethod,
        match.PlatformMethod,
        match.Words,
        match.Keywords,
        match.NameTokens,
        match.Score,
        match.PlatformMethod is null
            ? $"Приём «{match.Intent.Title}» распознан по словам {Quote(match.Words)}; вес совпадения {match.Score}."
            : match.Words.Count == 0
                ? $"Приём «{match.Intent.Title}» распознан по методу платформы «{match.PlatformMethod}»; вес совпадения {match.Score}."
                : $"Приём «{match.Intent.Title}» распознан по методу платформы «{match.PlatformMethod}» и словам {Quote(match.Words)}; вес совпадения {match.Score}.");

    /// <summary>Объяснение нераспознанного запроса: приём не выдуман, процедуры ищутся по словам запроса.</summary>
    private static ConventionRecognition NoRecognition(IReadOnlyList<string> tokens) => new(
        null,
        "поиск по словам запроса",
        ConventionMatchStep.None,
        null,
        tokens,
        [],
        [],
        0,
        tokens.Count == 0
            ? "Приём не распознан: в запросе нет слов для подбора. Назовите приём словами конфигурации или укажите метод платформы."
            : $"Приём не распознан: слова {Quote(tokens)} не совпали с признаками каталога, процедуры ищутся по этим словам.");

    /// <summary>Слова в кавычках через запятую: «записать», «объект».</summary>
    private static string Quote(IReadOnlyList<string> words) => "«" + string.Join("», «", words) + "»";

    /// <summary>
    /// Общие признаки каталога: слово, которое совпадает с признаком другого приёма, перестаёт быть
    /// отличительным. Список выводится из каталога, поэтому не может с ним разойтись.
    /// </summary>
    /// <remarks>
    /// Составные имена («ЗаписатьНаборЗаписей») в сравнении не участвуют: иначе любое слово внутри
    /// такого имени — «записать», «набор» — выглядело бы общим для всех приёмов сразу, и вес переставал
    /// бы что-либо значить. Общими считаются только целые однословные признаки, которые действительно
    /// встречаются у двух и более приёмов («объект», «значение», «найти»).
    /// </remarks>
    private static HashSet<string> CollectWeakTerms()
    {
        var terms = new List<(int Intent, string Term)>();
        for (var index = 0; index < Catalog.Length; index++)
        {
            foreach (var term in AllTermsOf(Catalog[index]).Where(IsSingleWord))
            {
                terms.Add((index, term));
            }
        }

        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (intent, term) in terms)
        {
            if (result.Contains(term.ToLowerInvariant()))
            {
                continue;
            }

            if (terms.Any(other => other.Intent != intent && Matches(term, other.Term)))
            {
                result.Add(term.ToLowerInvariant());
            }
        }

        return result;
    }

    /// <summary>Однословный ли признак: у составного имени («ПолучитьРеквизит») основа не отличительна.</summary>
    private static bool IsSingleWord(string term) => term.Count(char.IsUpper) <= 1;

    /// <summary>Все признаки приёма: ключевые слова, слова имени и имена методов платформы.</summary>
    private static IEnumerable<string> AllTermsOf(ConventionIntent intent) =>
        intent.Keywords.Concat(intent.NameTokens).Concat(intent.PlatformMethods);

    /// <summary>Слова запроса, по которым ищутся кандидаты: без стоп-слов и имён методов платформы.</summary>
    /// <remarks>
    /// Слово убирается только при точном совпадении с именем метода платформы: «записать» из запроса
    /// «записать объект» — это метод «Записать», а «получить» из «получить настройку» остаётся словом
    /// поиска, иначе процедура «ПолучитьНастройкуПрограммы» не нашлась бы по имени.
    /// </remarks>
    private static List<string> TermsOf(IReadOnlyList<string> tokens, ConventionIntent? intent, bool excludePlatformMethods)
    {
        var result = new List<string>(tokens.Count);
        foreach (var token in tokens)
        {
            if (excludePlatformMethods && Catalog.Any(item => item.PlatformMethods.Any(method =>
                method.Equals(token, StringComparison.OrdinalIgnoreCase))))
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
    /// Совпадают ли слово запроса и слово приёма. Сравниваются основы: падежи и части речи в русском
    /// тексте расходятся, а основа остаётся общей («сообщение» и «сообщить», «реквизит» и «реквизита»).
    /// </summary>
    /// <remarks>
    /// Общей считается основа не короче пяти букв, а для коротких слов — все буквы, кроме последней,
    /// но не меньше трёх: «файл» и «файлы» совпадают, «форма» и «форму» совпадают, а «код» и «когда» — нет.
    /// </remarks>
    private static bool Matches(string token, string term)
    {
        var needed = Math.Max(3, Math.Min(Math.Min(token.Length, term.Length) - 1, MinStem));
        var common = 0;
        while (common < token.Length && common < term.Length && common < MinStem &&
            char.ToLowerInvariant(token[common]) == char.ToLowerInvariant(term[common]))
        {
            common++;
        }

        return common >= needed;
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
                + "Уточните намерение словами конфигурации (например, «записать объект») или укажите метод платформы. "
                + "Известные приёмы: " + string.Join(", ", Catalog.Select(static item => item.Id)) + ".");
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
