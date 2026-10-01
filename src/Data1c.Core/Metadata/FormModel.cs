namespace Data1c.Core.Metadata;

/// <summary>Вид формы конфигурации 1С.</summary>
public enum FormKind
{
    /// <summary>
    /// Вид не определён: ни свойства объекта метаданных, ни пространство имён файла его не задают.
    /// </summary>
    Unknown,

    /// <summary>Управляемая форма (пространство имён logform).</summary>
    Managed,

    /// <summary>Обычная форма (пространство имён data/ui/form).</summary>
    Ordinary,
}

/// <summary>Реквизит формы.</summary>
/// <param name="Name">Имя реквизита, как оно записано в форме.</param>
/// <param name="Types">
/// Типы значения: идентификаторы объектов метаданных («Catalog.Товары») или имена типов платформы («xs:string»).
/// </param>
/// <param name="IsMain">Реквизит основной: с ним форма связывается с объектом конфигурации.</param>
public sealed record FormAttribute(string Name, IReadOnlyList<string> Types, bool IsMain = false)
{
    /// <summary>Типы одной строкой — так их показывает карточка объекта.</summary>
    public string? TypeText => Types.Count == 0 ? null : string.Join(", ", Types);

    public override string ToString() => IsMain ? Name + " (основной)" : Name;
}

/// <summary>Элемент формы.</summary>
/// <param name="Name">Имя элемента (атрибут <c>name</c>).</param>
/// <param name="Kind">Вид элемента — имя узла XML: InputField, Table, UsualGroup, Button, Column.</param>
/// <param name="DataPath">Привязка к данным: содержимое узла <c>DataPath</c>.</param>
/// <param name="Attribute">Реквизит формы, к которому ведёт <see cref="DataPath"/> (первый сегмент пути), если он найден среди реквизитов.</param>
/// <param name="CommandName">Команда, назначенная элементу (у кнопок командных панелей), если есть.</param>
public sealed record FormElement(
    string Name,
    string Kind,
    string? DataPath = null,
    string? Attribute = null,
    string? CommandName = null)
{
    public override string ToString() => $"{Kind}.{Name}";
}

/// <summary>Команда формы.</summary>
/// <param name="Name">Имя команды.</param>
/// <param name="Handler">Процедура-обработчик действия команды (узел <c>Action</c>).</param>
/// <param name="CommandName">Ссылка на команду объекта, если команда формы её использует.</param>
public sealed record FormCommand(string Name, string? Handler = null, string? CommandName = null)
{
    public override string ToString() => Handler is null ? Name : $"{Name} → {Handler}";
}

/// <summary>Обработчик события формы или её элемента.</summary>
/// <param name="Event">Имя события так, как оно записано в XML («OnOpen», «OnCreateAtServer», «OnChange»).</param>
/// <param name="Element">Имя элемента, у которого объявлено событие; null — событие самой формы.</param>
/// <param name="Procedure">Имя процедуры модуля формы, назначенной обработчиком.</param>
/// <param name="Line">Строка процедуры в модуле формы, если процедура найдена.</param>
/// <param name="Resolved">Процедура найдена в модуле формы.</param>
public sealed record FormEventHandler(
    string Event,
    string? Element,
    string Procedure,
    int? Line = null,
    bool Resolved = false)
{
    public override string ToString() => Element is null ? $"{Event} → {Procedure}" : $"{Element}.{Event} → {Procedure}";
}

/// <summary>
/// Разобранное описание формы (<c>Ext/Form.xml</c>): реквизиты, элементы, команды и обработчики событий.
/// </summary>
/// <param name="Name">Имя формы: из свойств объекта метаданных, а если их нет — из пути файла.</param>
/// <param name="Kind">Вид формы.</param>
/// <param name="SourcePath">Путь файла описания формы внутри выгрузки.</param>
/// <param name="Attributes">Реквизиты формы.</param>
/// <param name="Elements">Элементы формы в порядке обхода дерева.</param>
/// <param name="Commands">Команды формы.</param>
/// <param name="Handlers">Обработчики событий формы и её элементов.</param>
public sealed record FormModel(
    string Name,
    FormKind Kind,
    string? SourcePath,
    IReadOnlyList<FormAttribute> Attributes,
    IReadOnlyList<FormElement> Elements,
    IReadOnlyList<FormCommand> Commands,
    IReadOnlyList<FormEventHandler> Handlers)
{
    /// <summary>Пустая модель: описание формы ещё не прочитано или файла нет.</summary>
    public static FormModel Empty(string name, string? sourcePath = null) =>
        new(name, FormKind.Unknown, sourcePath, [], [], [], []);

    /// <summary>В форме нет ни одного распознанного узла.</summary>
    public bool IsEmpty =>
        Attributes.Count == 0 && Elements.Count == 0 && Commands.Count == 0 && Handlers.Count == 0;

    /// <summary>Реквизит формы по имени.</summary>
    public FormAttribute? FindAttribute(string? name) =>
        string.IsNullOrEmpty(name)
            ? null
            : Attributes.FirstOrDefault(attribute => string.Equals(attribute.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Элементы формы указанного вида («InputField», «Table», …).</summary>
    public IEnumerable<FormElement> ElementsOfKind(string kind) =>
        Elements.Where(element => string.Equals(element.Kind, kind, StringComparison.OrdinalIgnoreCase));

    /// <summary>Обработчики, процедуры которых найдены в модуле формы.</summary>
    public IEnumerable<FormEventHandler> ResolvedHandlers => Handlers.Where(static handler => handler.Resolved);

    /// <summary>Копия формы с другим списком обработчиков: нужна, когда модуль формы разобран позже файла.</summary>
    public FormModel WithHandlers(IReadOnlyList<FormEventHandler> handlers) => this with { Handlers = handlers };

    /// <summary>
    /// Реквизит формы, к которому ведёт путь данных элемента: берётся первый сегмент <c>DataPath</c>
    /// («Объект.Артикул» → «Объект»). Пути через элементы («Items.Таблица.CurrentData.Поле») реквизита
    /// формы не называют и остаются без привязки.
    /// </summary>
    public static string? ResolveAttribute(IReadOnlyList<FormAttribute> attributes, string? dataPath)
    {
        if (string.IsNullOrEmpty(dataPath))
        {
            return null;
        }

        var separator = dataPath.IndexOf('.');
        var head = (separator < 0 ? dataPath : dataPath[..separator]).Trim();
        if (head.Length == 0)
        {
            return null;
        }

        foreach (var attribute in attributes)
        {
            if (string.Equals(attribute.Name, head, StringComparison.OrdinalIgnoreCase))
            {
                return attribute.Name;
            }
        }

        return null;
    }
}
