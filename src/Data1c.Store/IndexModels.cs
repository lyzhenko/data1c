namespace Data1c.Store;

/// <summary>Строка таблицы узлов.</summary>
public sealed record NodeRow(
    string Id,
    string Kind,
    string Name,
    string? SourcePath = null,
    string? MetadataKind = null,
    bool IsExternal = false,
    string? PlatformTitle = null,
    string? PlatformVersion = null);

/// <summary>Строка таблицы связей.</summary>
public sealed record EdgeRow(string SourceId, string TargetId, string Kind, int? Line = null, string? Detail = null);

/// <summary>Процедура или функция в индексе.</summary>
public sealed record SymbolRow(
    long Id,
    string NodeId,
    string ModulePath,
    string? OwnerId,
    string Name,
    string Kind,
    bool IsExport,
    int StartLine,
    int EndLine,
    string? Region = null,
    string? Parameters = null,
    string? CommentHead = null);

/// <summary>Объект метаданных целиком: свойства, признак верхнего уровня и владелец.</summary>
public sealed record MetadataObjectRow(
    string Id,
    string Kind,
    string Name,
    string? Synonym = null,
    string? Uuid = null,
    string? SourcePath = null,
    string? Comment = null,
    bool IsTopLevel = false,
    string? ParentId = null,
    string? Properties = null);

/// <summary>Вложенный объект метаданных: реквизит, табличная часть, форма, макет, команда.</summary>
public sealed record MetadataItemRow(
    string ObjectId,
    string Kind,
    string Name,
    string? TypeInfo = null,
    string? ParentId = null,
    string? Synonym = null,
    string? Comment = null);

/// <summary>Страница состава объекта: показанные дети и их общее число.</summary>
public sealed record MetadataChildrenPage(IReadOnlyList<MetadataItemRow> Items, int Total);

/// <summary>
/// Обработчик события формы из индекса: форма, элемент, событие и назначенная процедура модуля формы.
/// Нужен точкам входа (Э3-2): платформа вызывает эти процедуры сама, явных вызовов у них нет.
/// </summary>
/// <param name="FormId">Идентификатор формы: «Catalog.Товары/Form.ФормаЭлемента».</param>
/// <param name="FormName">Имя формы.</param>
/// <param name="ObjectId">Объект-владелец формы: «Catalog.Товары». У общей формы владельца нет.</param>
/// <param name="SourcePath">Путь файла описания формы (<c>Ext/Form.xml</c>).</param>
/// <param name="Event">Имя события так, как оно записано в XML («OnOpen», «OnChange»).</param>
/// <param name="Element">Элемент формы, у которого объявлено событие; null — событие самой формы.</param>
/// <param name="Procedure">Имя процедуры модуля формы.</param>
/// <param name="Line">Строка процедуры в модуле формы, если она найдена.</param>
/// <param name="Resolved">Процедура найдена в модуле формы.</param>
public sealed record FormHandlerRow(
    string FormId,
    string FormName,
    string? ObjectId,
    string? SourcePath,
    string Event,
    string? Element,
    string Procedure,
    int? Line,
    bool Resolved);

/// <summary>Виды строк состава формы в индексе (<c>form_items.kind</c>).</summary>
internal static class FormItemKinds
{
    /// <summary>Реквизит формы: имя, типы значения и признак основного реквизита.</summary>
    internal const string Attribute = "Attribute";

    /// <summary>Элемент формы: имя, вид (InputField, Table, …), путь данных и команда.</summary>
    internal const string Element = "Element";

    /// <summary>Команда формы: имя, процедура действия и ссылка на команду объекта.</summary>
    internal const string Command = "Command";

    /// <summary>Обработчик события: событие, элемент, процедура модуля формы и её строка.</summary>
    internal const string Handler = "Handler";
}

/// <summary>Обращение к объекту метаданных: из кода, из текста запроса, тип реквизита, право роли.</summary>
public sealed record MetadataRefRow(
    string SourceId,
    string TargetId,
    string Context,
    int? Line = null,
    string? Detail = null);

/// <summary>Результат обхода связей в базе.</summary>
public sealed record ReachRow(string Id, int Depth);

/// <summary>Сводка по индексу.</summary>
public sealed record IndexStatistics(
    long Nodes,
    long Edges,
    long Symbols,
    long Calls,
    long MetadataObjects,
    long MetadataItems,
    long MetadataRefs,
    long Files,
    long PlatformNodes,
    long ExternalNodes,
    string? DumpPath,
    DateTimeOffset? IndexedAt,
    long Forms = 0)
{
    public override string ToString() =>
        $"узлов {Nodes:N0}, связей {Edges:N0}, символов {Symbols:N0}, вызовов {Calls:N0}, " +
        $"объектов метаданных {MetadataObjects:N0}, реквизитов {MetadataItems:N0}, обращений {MetadataRefs:N0}, " +
        $"форм {Forms:N0}";
}

/// <summary>Итог записи индекса.</summary>
public sealed record IndexWriteResult(
    int Nodes,
    int Edges,
    int Symbols,
    int Calls,
    int MetadataObjects,
    int MetadataItems,
    int MetadataRefs,
    int Files,
    int Forms,
    TimeSpan Duration);
