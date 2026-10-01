using System.Collections.Frozen;
using Data1c.Core.Metadata;

namespace Data1c.Core.Analysis;

/// <summary>
/// Вид точки входа: код, который вызывает платформа, а не другой код конфигурации.
/// </summary>
/// <remarks>
/// Агенту такие места нужны отдельно от графа вызовов: правка процедуры-обработчика меняет
/// поведение объекта, хотя явных вызовов этой процедуры в выгрузке может не быть вовсе.
/// </remarks>
public enum EntryPointKind
{
    /// <summary>Подписка на событие («ПодпискиНаСобытия»): платформа вызывает обработчик при событии объекта.</summary>
    Subscription,

    /// <summary>Регламентное задание («РегламентныеЗадания»): платформа вызывает обработчик по расписанию.</summary>
    Job,

    /// <summary>Обработчик события формы: процедуру модуля формы вызывает платформа при событии формы или элемента.</summary>
    Form,
}

/// <summary>Что именно нужно из точек входа.</summary>
/// <param name="Metadata">
/// Идентификатор объекта метаданных, который «реагирует» на изменения: объекта-источника подписки,
/// общего модуля-обработчика, самой формы или объекта-владельца формы («Catalog.Товары»).
/// <see langword="null"/> — без фильтра.
/// </param>
/// <param name="Kind">Оставить только этот вид точек входа. <see langword="null"/> — все виды.</param>
/// <param name="Limit">Предел числа точек входа каждого вида: источники отдают не больше этого числа.</param>
public sealed record EntryPointFilter(string? Metadata = null, EntryPointKind? Kind = null, int Limit = 50);

/// <summary>
/// Сырые сведения индекса об одной точке входа. Собирает их <see cref="EntryPoints"/>, а готовит
/// источник: индекс (<c>IndexEntryPoints</c>) или разбор в памяти (<see cref="AnalysisEntryPoints"/>).
/// </summary>
/// <param name="Kind">Вид точки входа.</param>
/// <param name="Id">Идентификатор точки входа: подписки, задания или формы с событием и элементом.</param>
/// <param name="Name">Имя подписки, задания или формы.</param>
/// <param name="ObjectId">Идентификатор объекта метаданных, к которому относится точка входа.</param>
/// <param name="ObjectKind">Вид объекта метаданных: EventSubscription, ScheduledJob, Form.</param>
/// <param name="RelatedIds">
/// Объекты метаданных, с которыми связана точка входа: она сама, источники подписки, модуль-обработчик
/// и объект-владелец формы. По этому списку работает фильтр <see cref="EntryPointFilter.Metadata"/>.
/// </param>
/// <param name="Event">Событие: подписки («BeforeWrite») или обработчика формы («OnOpen»).</param>
/// <param name="Element">Элемент формы, у которого объявлено событие, если обработчик не формы целиком.</param>
/// <param name="Sources">Объекты-источники подписки: чьи события слушает обработчик.</param>
/// <param name="HandlerOwnerId">Объект метаданных, которому принадлежит модуль-обработчик (обычно общий модуль).</param>
/// <param name="ModulePath">Путь модуля-обработчика внутри выгрузки.</param>
/// <param name="Procedure">Имя процедуры-обработчика в модуле.</param>
/// <param name="Line">Строка процедуры-обработчика в модуле.</param>
/// <param name="Resolved">Процедура-обработчик найдена в модуле: только тогда существует узел процедуры.</param>
/// <param name="Properties">Свойства объекта из индекса: событие подписки, использование и расписание задания.</param>
/// <param name="SourcePath">Путь XML-файла объекта или описания формы.</param>
public sealed record EntryPointFact(
    EntryPointKind Kind,
    string Id,
    string Name,
    string? ObjectId,
    string? ObjectKind,
    IReadOnlyList<string>? RelatedIds = null,
    string? Event = null,
    string? Element = null,
    IReadOnlyList<string>? Sources = null,
    string? HandlerOwnerId = null,
    string? ModulePath = null,
    string? Procedure = null,
    int? Line = null,
    bool Resolved = false,
    IReadOnlyDictionary<string, string>? Properties = null,
    string? SourcePath = null);

/// <summary>Готовая точка входа: то, что видит агент, и то, по чему он открывает код.</summary>
/// <param name="Kind">Вид точки входа.</param>
/// <param name="Id">Идентификатор точки входа.</param>
/// <param name="Name">Имя подписки, задания или формы.</param>
/// <param name="ObjectId">Объект метаданных, к которому относится точка входа.</param>
/// <param name="ObjectKind">Вид объекта метаданных.</param>
/// <param name="Event">Событие так, как оно записано в выгрузке.</param>
/// <param name="EventName">Русское имя события, если оно известно; иначе <see langword="null"/>.</param>
/// <param name="Element">Элемент формы, у которого объявлено событие.</param>
/// <param name="Sources">Объекты-источники подписки.</param>
/// <param name="ModulePath">Путь модуля-обработчика.</param>
/// <param name="Procedure">Имя процедуры-обработчика.</param>
/// <param name="RoutineId">Узел процедуры «routine:module:путь#Имя»: его принимает инструмент code. Null, если процедура не найдена.</param>
/// <param name="Line">Строка процедуры в модуле.</param>
/// <param name="Resolved">Процедура-обработчик найдена в модуле.</param>
/// <param name="Properties">Свойства объекта из индекса.</param>
/// <param name="SourcePath">Путь XML-файла объекта или описания формы.</param>
public sealed record EntryPointInfo(
    EntryPointKind Kind,
    string Id,
    string Name,
    string? ObjectId,
    string? ObjectKind,
    string? Event,
    string? EventName,
    string? Element,
    IReadOnlyList<string> Sources,
    string? ModulePath,
    string? Procedure,
    string? RoutineId,
    int? Line,
    bool Resolved,
    IReadOnlyDictionary<string, string> Properties,
    string? SourcePath);

/// <summary>Собранные точки входа вместе со счётчиками по видам.</summary>
/// <param name="Items">Точки входа в порядке видов: подписки, задания, обработчики форм.</param>
/// <param name="CountsByKind">Сколько точек входа каждого вида попало в ответ (нули для отсутствующих видов).</param>
/// <param name="Truncated">Источник нашёл больше точек входа, чем поместилось в предел.</param>
public sealed record EntryPointReport(
    IReadOnlyList<EntryPointInfo> Items,
    IReadOnlyDictionary<EntryPointKind, int> CountsByKind,
    bool Truncated);

/// <summary>
/// Источник точек входа: отдаёт сырые сведения о подписках, регламентных заданиях и обработчиках
/// форм. Реализации — индекс (<c>IndexEntryPoints</c>) и разбор в памяти (<see cref="AnalysisEntryPoints"/>).
/// </summary>
public interface IEntryPointFacts
{
    /// <summary>
    /// Читает точки входа по фильтру: не больше <c>limit + 1</c> каждого вида. Лишняя строка нужна
    /// <see cref="EntryPoints.Collect"/>, чтобы отличить «подходящих ровно по пределу» от «их больше».
    /// </summary>
    IReadOnlyList<EntryPointFact> Read(EntryPointFilter filter);
}

/// <summary>
/// Единый список точек входа конфигурации (Э3-2): подписки на события, регламентные задания
/// и обработчики событий форм.
/// </summary>
/// <remarks>
/// Компонент не ходит в базу сам: сведения приносит <see cref="IEntryPointFacts"/>, а здесь они
/// приводятся к одному виду — дополняются узлом процедуры и русским именем события, упорядочиваются
/// и получают счётчики по видам. Поэтому один и тот же ответ получается и из индекса, и из разбора
/// в памяти.
/// <para>
/// Схема индекса для этого не менялась: подписки и задания берутся из <c>metadata_objects</c>
/// (вид объекта и свойства), источник подписки и обработчик — из <c>metadata_refs</c>
/// (ссылка на объект и на общий модуль с именем процедуры в уточнении), обработчики форм —
/// из <c>form_items</c> с видом <c>Handler</c>.
/// </para>
/// </remarks>
public static class EntryPoints
{
    /// <summary>Предел числа точек входа по умолчанию.</summary>
    public const int DefaultLimit = 50;

    /// <summary>Наибольший предел выдачи: точки входа читает модель-агент, длинный список ей вреден.</summary>
    public const int MaxLimit = 200;

    private static readonly IReadOnlyDictionary<string, string> EventNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        // В XML выгрузки имя события записано по-английски, а в Конфигураторе и в коде — по-русски.
        ["BeforeWrite"] = "ПередЗаписью",
        ["AfterWrite"] = "ПослеЗаписи",
        ["OnCopy"] = "ПриКопировании",
        ["BeforeDelete"] = "ПередУдалением",
        ["Filling"] = "ОбработкаЗаполнения",
        ["FillCheck"] = "ОбработкаПроверкиЗаполнения",
        ["Posting"] = "ОбработкаПроведения",
        ["UndoPosting"] = "ОбработкаУдаленияПроведения",
        ["OnSetNewCode"] = "ПриУстановкеНовогоКода",
        ["OnSetNewNumber"] = "ПриУстановкеНовогоНомера",
        ["OnOpen"] = "ПриОткрытии",
        ["OnClose"] = "ПриЗакрытии",
        ["OnCreateAtServer"] = "ПриСозданииНаСервере",
        ["OnChange"] = "ПриИзменении",
        ["ChoiceProcessing"] = "ОбработкаВыбора",
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Собирает ответ из сведений источника: дополняет их узлом процедуры и русским именем события,
    /// упорядочивает по видам и именам, ограничивает пределом и считает счётчики по видам.
    /// </summary>
    /// <param name="facts">Источник сведений о точках входа.</param>
    /// <param name="filter">Фильтр: объект метаданных, вид точек входа и предел выдачи.</param>
    public static EntryPointReport Collect(IEntryPointFacts facts, EntryPointFilter? filter = null)
    {
        ArgumentNullException.ThrowIfNull(facts);
        filter ??= new EntryPointFilter();
        var limit = Math.Clamp(filter.Limit, 1, MaxLimit);

        // Источник читает на одну точку входа больше предела: так видно, что подходящих больше,
        // чем помещается в ответ, — иначе «ровно предел» и «обрезано» неотличимы.
        var raw = facts.Read(filter with { Limit = limit + 1 });

        var items = raw
            .Select(ToInfo)
            .OrderBy(static item => item.Kind)
            .ThenBy(static item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static item => item.Id, StringComparer.Ordinal)
            .Take(limit)
            .ToList();

        var counts = new Dictionary<EntryPointKind, int>(3);
        foreach (var kind in Enum.GetValues<EntryPointKind>())
        {
            counts[kind] = 0;
        }

        foreach (var item in items)
        {
            counts[item.Kind]++;
        }

        // Источники ограничивают каждый вид пределом, поэтому выход за предел означает,
        // что подходящих точек входа больше, чем показано.
        return new EntryPointReport(items, counts, raw.Count > limit);
    }

    /// <summary>Относится ли точка входа к указанному объекту метаданных: сам объект, источник подписки, модуль-обработчик, форма.</summary>
    public static bool Matches(EntryPointFact fact, string? metadata)
    {
        ArgumentNullException.ThrowIfNull(fact);
        if (string.IsNullOrWhiteSpace(metadata))
        {
            return true;
        }

        if (string.Equals(fact.ObjectId, metadata, StringComparison.Ordinal))
        {
            return true;
        }

        return fact.RelatedIds is { Count: > 0 } related && related.Contains(metadata, StringComparer.Ordinal);
    }

    /// <summary>Узел процедуры в том же виде, в каком его принимает инструмент code: «routine:module:путь#Имя».</summary>
    public static string RoutineId(string modulePath, string procedure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modulePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(procedure);
        return $"routine:module:{modulePath}#{procedure}";
    }

    /// <summary>Имя вида точки входа для ответа инструмента: subscription, job, form.</summary>
    public static string KindName(EntryPointKind kind) => kind switch
    {
        EntryPointKind.Subscription => "subscription",
        EntryPointKind.Job => "job",
        _ => "form",
    };

    /// <summary>Разбирает имя вида точки входа: subscription, job, form (без учёта регистра).</summary>
    public static bool TryParseKind(string? value, out EntryPointKind kind)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "subscription":
                kind = EntryPointKind.Subscription;
                return true;
            case "job":
                kind = EntryPointKind.Job;
                return true;
            case "form":
                kind = EntryPointKind.Form;
                return true;
            default:
                kind = EntryPointKind.Subscription;
                return false;
        }
    }

    /// <summary>
    /// Разбирает строку обработчика «CommonModule.Модуль.Процедура» на объект метаданных и имя
    /// процедуры: так обработчик записан и в свойстве подписки, и в свойстве регламентного задания.
    /// </summary>
    /// <param name="text">Текст обработчика из выгрузки.</param>
    /// <param name="moduleId">Идентификатор модуля-владельца: «CommonModule.Модуль».</param>
    /// <param name="procedure">Имя процедуры-обработчика, если оно указано.</param>
    public static bool TrySplitHandler(string? text, out string? moduleId, out string? procedure)
    {
        moduleId = null;
        procedure = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        if (!MdRefParser.TryParse(text, out var kind, out var name, out var rest) || kind.IsUnknown)
        {
            return false;
        }

        moduleId = MdNaming.CreateId(kind, name);
        procedure = string.IsNullOrWhiteSpace(rest) ? null : rest;
        return true;
    }

    /// <summary>Русское имя события по имени из XML: «BeforeWrite» → «ПередЗаписью». Null, если имя незнакомо.</summary>
    public static string? RussianEventName(string? name) =>
        name is not null && EventNames.TryGetValue(name, out var russian) ? russian : null;

    /// <summary>Пополняет точку входа тем, что не зависит от источника: узлом процедуры и русским именем события.</summary>
    private static EntryPointInfo ToInfo(EntryPointFact fact)
    {
        var sources = fact.Sources ?? [];
        var properties = fact.Properties ?? EmptyProperties;
        var routineId = fact.Resolved && fact.ModulePath is { Length: > 0 } path && fact.Procedure is { Length: > 0 } procedure
            ? RoutineId(path, procedure)
            : null;

        return new EntryPointInfo(
            fact.Kind,
            fact.Id,
            fact.Name,
            fact.ObjectId,
            fact.ObjectKind,
            fact.Event,
            RussianEventName(fact.Event),
            fact.Element,
            sources,
            fact.ModulePath,
            fact.Procedure,
            routineId,
            fact.Line,
            fact.Resolved,
            properties,
            fact.SourcePath);
    }

    private static readonly IReadOnlyDictionary<string, string> EmptyProperties =
        new Dictionary<string, string>(StringComparer.Ordinal);
}
