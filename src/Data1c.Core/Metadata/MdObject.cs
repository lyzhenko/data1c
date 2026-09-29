using Data1c.Core.Bsl;

namespace Data1c.Core.Metadata;

/// <summary>Смысл перекрёстной ссылки, найденной в XML.</summary>
public enum MdReferenceKind
{
    /// <summary>Ссылка на другой объект по имени (например, содержимое подсистемы).</summary>
    Content,

    /// <summary>Тип значения: <c>cfg:CatalogRef.Товары</c>.</summary>
    Type,

    /// <summary>Ссылка на поле/реквизит: <c>Catalog.Товары.StandardAttribute.Code</c>.</summary>
    Field,

    /// <summary>Форма по умолчанию или вспомогательная форма объекта.</summary>
    Form,

    /// <summary>Макет объекта.</summary>
    Template,

    /// <summary>Команда объекта.</summary>
    Command,

    /// <summary>Право роли на объект.</summary>
    RoleRight,

    /// <summary>Источник подписки на событие.</summary>
    EventSource,

    Other,
}

/// <summary>Перекрёстная ссылка из объекта метаданных на другой объект.</summary>
/// <param name="TargetId">Канонический идентификатор цели: «Catalog.Товары».</param>
/// <param name="TargetKind">Вид объекта-цели.</param>
/// <param name="TargetName">Имя объекта-цели.</param>
/// <param name="Kind">Смысл ссылки.</param>
/// <param name="Detail">Дополнительная часть ссылки (имя реквизита, формы и т. п.).</param>
/// <param name="Raw">Исходный текст ссылки в XML.</param>
public sealed record MdReference(
    string TargetId,
    MdKind TargetKind,
    string TargetName,
    MdReferenceKind Kind,
    string? Detail = null,
    string? Raw = null);

/// <summary>Файл модуля, привязанный к объекту метаданных.</summary>
/// <param name="RelativePath">Путь файла внутри выгрузки.</param>
/// <param name="Kind">Вид модуля (модуль объекта, менеджера, формы и т. д.).</param>
public sealed record MdModuleFile(string RelativePath, BslModuleKind Kind);

/// <summary>
/// Объект метаданных 1С: объект конфигурации либо вложенный объект (реквизит, табличная часть, форма, макет, команда).
/// </summary>
public sealed class MdObject
{
    private readonly List<MdObject> _children = [];
    private readonly List<MdReference> _references = [];
    private readonly HashSet<(string TargetId, MdReferenceKind Kind, string? Detail)> _referenceKeys = [];
    private readonly List<MdModuleFile> _modules = [];
    private readonly Dictionary<string, string?> _properties = new(StringComparer.Ordinal);
    private string _name;

    public MdObject(MdKind kind, string name)
    {
        Kind = kind;
        _name = name ?? string.Empty;
    }

    /// <summary>Вид объекта.</summary>
    public MdKind Kind { get; }

    /// <summary>Имя объекта так, как оно записано в конфигурации.</summary>
    public string Name
    {
        get => _name;
        internal set => _name = value ?? string.Empty;
    }

    /// <summary>
    /// Иерархический идентификатор: «Catalog.Товары» для объекта верхнего уровня,
    /// «Catalog.Товары/Attribute.Артикул» для вложенного.
    /// </summary>
    public string Id => Kind == MdKind.Configuration
        ? "Configuration"
        : Parent is null || Parent.Kind == MdKind.Configuration
            ? MdNaming.CreateId(Kind, Name)
            : $"{Parent.Id}/{MdNaming.CreateId(Kind, Name)}";

    /// <summary>Каноническое имя вида «Catalog.Товары» (для вложенных — только вид и имя).</summary>
    public string CanonicalName => MdNaming.CreateId(Kind, Name);

    public Guid? Uuid { get; internal set; }

    public string? Synonym { get; internal set; }

    public string? Comment { get; internal set; }

    public MdObject? Parent { get; internal set; }

    /// <summary>Путь XML-файла, из которого прочитан объект (для вложенных — файл родителя).</summary>
    public string? SourcePath { get; internal set; }

    /// <summary>Вложенный объект, который в файле был указан только по имени (например, &lt;Form&gt;Имя&lt;/Form&gt;).</summary>
    public bool IsNameOnlyReference { get; internal set; }

    /// <summary>Признак того, что объект прочитан как корень отдельного XML-файла выгрузки.</summary>
    public bool IsFileRoot { get; internal set; }

    /// <summary>
    /// Каталог выгрузки, соответствующий объекту: «Catalogs/Товары», «Catalogs/Товары/Forms/ФормаЭлемента».
    /// Используется для привязки файлов модулей.
    /// </summary>
    public string? Directory { get; internal set; }

    public IReadOnlyList<MdObject> Children => _children;

    public IReadOnlyList<MdReference> References => _references;

    public IReadOnlyList<MdModuleFile> Modules => _modules;

    /// <summary>Плоские скалярные свойства из секции Properties (без вложенных структур).</summary>
    public IReadOnlyDictionary<string, string?> Properties => _properties;

    public bool HasChildren => _children.Count > 0;

    /// <summary>Добавляет ссылку на другой объект, если такой ещё нет (учитываются цель, вид и уточнение).</summary>
    public bool AddReference(MdReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (!_referenceKeys.Add((reference.TargetId, reference.Kind, reference.Detail)))
        {
            return false;
        }

        _references.Add(reference);
        return true;
    }

    internal List<MdObject> ChildrenMutable => _children;

    internal List<MdReference> ReferencesMutable => _references;

    internal List<MdModuleFile> ModulesMutable => _modules;

    internal Dictionary<string, string?> PropertiesMutable => _properties;

    /// <summary>Дочерние объекты и объекты глубже, в порядке обхода в ширину.</summary>
    public IEnumerable<MdObject> Descendants()
    {
        var queue = new Queue<MdObject>(_children);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            yield return current;
            foreach (var child in current._children)
            {
                queue.Enqueue(child);
            }
        }
    }

    /// <summary>Все вложенные объекты указанного вида.</summary>
    public IEnumerable<MdObject> DescendantsOfKind(MdKind kind) =>
        Descendants().Where(o => o.Kind == kind);

    /// <summary>Прямой дочерний объект по виду и имени.</summary>
    public MdObject? FindChild(MdKind kind, string name) =>
        _children.FirstOrDefault(c => c.Kind == kind && string.Equals(c.Name, name, StringComparison.Ordinal));

    public string? GetProperty(string name) =>
        _properties.TryGetValue(name, out var value) ? value : null;

    /// <summary>Признак того, что объект верхнего уровня: его родитель — сама конфигурация.</summary>
    public bool IsTopLevel => Parent is null || Parent.Kind == MdKind.Configuration;

    public override string ToString() => Id;
}

/// <summary>Разобранная модель метаданных конфигурации.</summary>
public sealed class MdObjectModel
{
    private readonly Dictionary<string, MdObject> _byId;
    private readonly List<MdObject> _objects;

    public MdObjectModel(MdObject configuration, IEnumerable<MdObject> objects)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        Configuration = configuration;
        _objects = [.. objects];
        _byId = new Dictionary<string, MdObject>(StringComparer.Ordinal);
        foreach (var obj in _objects)
        {
            _byId.TryAdd(obj.Id, obj);
        }
    }

    /// <summary>Корневой объект конфигурации.</summary>
    public MdObject Configuration { get; }

    /// <summary>Все объекты, включая корень конфигурации и вложенные.</summary>
    public IReadOnlyList<MdObject> Objects => _objects;

    /// <summary>Объекты верхнего уровня (без самой конфигурации).</summary>
    public IEnumerable<MdObject> TopLevel => Configuration.Children;

    public int Count => _objects.Count;

    public bool TryGet(string id, out MdObject obj) => _byId.TryGetValue(id, out obj!);

    public MdObject? Find(string id) => _byId.TryGetValue(id, out var obj) ? obj : null;

    public IEnumerable<MdObject> OfKind(MdKind kind) => _objects.Where(o => o.Kind == kind && o.Parent?.Kind != MdKind.Configuration);

    public IEnumerable<MdObject> TopLevelOfKind(MdKind kind) => Configuration.Children.Where(o => o.Kind == kind);
}
