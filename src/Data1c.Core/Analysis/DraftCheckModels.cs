namespace Data1c.Core.Analysis;

/// <summary>Важность замечания к черновику модуля.</summary>
public enum DraftProblemSeverity
{
    /// <summary>Ошибка: так код работать не будет (1С не скомпилирует модуль).</summary>
    Error,

    /// <summary>Предупреждение: код работоспособен, но почти наверняка содержит оплошность.</summary>
    Warning,

    /// <summary>Заметка: на работу кода не влияет, но полезно знать.</summary>
    Info,
}

/// <summary>Код замечания к черновику модуля.</summary>
public enum DraftProblemKind
{
    /// <summary>Локальный вызов, которого нет ни в модуле, ни среди процедур конфигурации, ни в платформе.</summary>
    UnknownProcedure,

    /// <summary>У общего модуля конфигурации нет метода с таким именем.</summary>
    UnknownCommonModuleMethod,

    /// <summary>Число аргументов вызова не совпадает с числом параметров вызываемого.</summary>
    WrongArgumentCount,

    /// <summary>Метод общего модуля вызван из другого модуля, но объявлен без ключевого слова «Экспорт».</summary>
    MethodNotExported,

    /// <summary>Обращение к объекту метаданных, которого нет в конфигурации.</summary>
    UnknownMetadataObject,

    /// <summary>Объявленная в «Перем» переменная нигде не используется.</summary>
    UnusedVariable,

    /// <summary>Параметр процедуры или функции нигде не используется.</summary>
    UnusedParameter,

    /// <summary>У функции нет ни одного оператора «Возврат».</summary>
    FunctionWithoutReturn,

    /// <summary>После оператора «Возврат» идёт недостижимый код.</summary>
    CodeAfterReturn,

    /// <summary>Процедура вызвана как функция: её результат использован в выражении.</summary>
    ProcedureUsedAsFunction,

    /// <summary>Результат функции не используется: вызов стоит отдельным оператором.</summary>
    FunctionUsedAsProcedure,
}

/// <summary>Замечание к черновику модуля. Позиция достаточна, чтобы найти место в Конфигураторе.</summary>
/// <param name="Line">Номер строки черновика (1-based).</param>
/// <param name="Severity">Важность замечания.</param>
/// <param name="Kind">Код замечания.</param>
/// <param name="Message">Текст замечания для агента.</param>
/// <param name="Hint">Подсказка: похожие имена, как исправить. Может отсутствовать.</param>
public sealed record DraftProblem(
    int Line,
    DraftProblemSeverity Severity,
    DraftProblemKind Kind,
    string Message,
    string? Hint = null);

/// <summary>Процедура или функция, известная конфигурации: из SQLite-индекса или из разбора в памяти.</summary>
/// <param name="Name">Имя процедуры или функции.</param>
/// <param name="Kind">Вид: «Procedure» или «Function».</param>
/// <param name="IsExport">Объявлена с ключевым словом «Экспорт».</param>
/// <param name="ModulePath">Путь модуля, в котором она объявлена.</param>
/// <param name="OwnerId">Идентификатор объекта-владельца: «CommonModule.ОбщегоНазначения».</param>
/// <param name="Parameters">Имена параметров в порядке объявления.</param>
/// <param name="RequiredCount">
/// Сколько параметров обязательно передать: у остальных есть значение по умолчанию
/// («Режим = Неопределено»), и их можно не передавать.
/// </param>
/// <param name="StartLine">Строка заголовка в модуле-объявлении.</param>
/// <param name="EndLine">Строка завершения в модуле-объявлении.</param>
public sealed record DraftSymbol(
    string Name,
    string Kind,
    bool IsExport,
    string ModulePath,
    string? OwnerId,
    IReadOnlyList<string> Parameters,
    int RequiredCount,
    int StartLine,
    int EndLine)
{
    /// <summary>Это функция, а не процедура.</summary>
    public bool IsFunction => string.Equals(Kind, "Function", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Объект метаданных конфигурации: то, что нужно проверке обращений вида «Справочники.Товары».</summary>
/// <param name="Id">Канонический идентификатор: «Catalog.Товары».</param>
/// <param name="Kind">Вид объекта: «Catalog», «Document».</param>
/// <param name="Name">Имя объекта.</param>
/// <param name="Synonym">Синоним, если есть.</param>
public sealed record DraftMetadataObject(string Id, string Kind, string Name, string? Synonym);

/// <summary>
/// Факты о конфигурации, нужные проверке черновика. Реализации: поверх SQLite-индекса
/// (<c>IndexDraftContext</c> в Data1c.Store) и поверх готового разбора в памяти
/// (<see cref="AnalysisDraftContext"/>). Проверка не знает, откуда пришли данные.
/// </summary>
public interface IDraftContext
{
    /// <summary>
    /// Процедуры и функции по имени. Точный режим нужен для разрешения вызова, обычный —
    /// для подсказки похожих имён.
    /// </summary>
    /// <param name="name">Имя процедуры или функции.</param>
    /// <param name="limit">Предел числа результатов.</param>
    /// <param name="exact">Только точное совпадение имени.</param>
    IReadOnlyList<DraftSymbol> FindSymbols(string name, int limit = 20, bool exact = false);

    /// <summary>Процедуры и функции одного объекта-владельца: нужны для подсказки методов общего модуля.</summary>
    /// <param name="ownerId">Идентификатор владельца: «CommonModule.ОбщегоНазначения».</param>
    /// <param name="limit">Предел числа результатов.</param>
    IReadOnlyList<DraftSymbol> FindSymbolsByOwner(string ownerId, int limit = 50);

    /// <summary>Поиск объектов метаданных по имени и синониму — для подсказки похожих.</summary>
    /// <param name="query">Имя или его часть.</param>
    /// <param name="limit">Предел числа результатов.</param>
    IReadOnlyList<DraftMetadataObject> FindMetadataObjects(string query, int limit = 20);

    /// <summary>Объект метаданных по каноническому идентификатору: «Catalog.Товары».</summary>
    /// <param name="id">Канонический идентификатор объекта.</param>
    DraftMetadataObject? GetMetadataObject(string id);

    /// <summary>
    /// Есть ли в конфигурации хотя бы один объект такого вида («Catalog», «Document»). Нужно, чтобы
    /// не выдавать замечания, когда секция выгрузки вообще не разбиралась.
    /// </summary>
    /// <param name="kind">Вид объекта метаданных.</param>
    bool HasMetadataKind(string kind);

    /// <summary>
    /// Зарегистрирована ли процедура модуля обработчиком события или команды формы: такую процедуру
    /// вызывает платформа, и часть её параметров может не использоваться в коде.
    /// </summary>
    /// <param name="modulePath">Путь модуля внутри выгрузки; для черновика без места — его подпись.</param>
    /// <param name="routineName">Имя процедуры или функции модуля.</param>
    bool IsEventHandler(string modulePath, string routineName);
}

/// <summary>Результат проверки черновика: замечания и оговорки о том, что проверить не удалось.</summary>
/// <param name="Problems">Замечания по возрастанию строки.</param>
/// <param name="Notes">Оговорки: например, что справка платформы или индекс недоступны.</param>
public sealed record DraftCheckResult(IReadOnlyList<DraftProblem> Problems, IReadOnlyList<string> Notes)
{
    /// <summary>Сколько замечаний такой важности.</summary>
    /// <param name="severity">Важность.</param>
    public int Count(DraftProblemSeverity severity) => Problems.Count(problem => problem.Severity == severity);

    /// <summary>Замечаний нет.</summary>
    public bool IsClean => Problems.Count == 0;
}
