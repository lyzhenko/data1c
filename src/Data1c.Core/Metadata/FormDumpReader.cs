using System.Text;
using System.Xml;
using Data1c.Core.Dump;

namespace Data1c.Core.Metadata;

/// <summary>Результат разбора описания формы.</summary>
/// <param name="Form">Разобранная форма. При битом XML — то, что успело прочитаться.</param>
/// <param name="Warnings">Замечания разбора: битый XML, неожиданный корневой узел.</param>
public sealed record FormReadResult(FormModel Form, IReadOnlyList<string> Warnings);

/// <summary>
/// Потоковый разбор описания формы 1С (<c>&lt;Объект&gt;/Forms/&lt;Имя&gt;/Ext/Form.xml</c>):
/// реквизиты, элементы с привязкой по <c>DataPath</c>, команды и обработчики событий.
/// </summary>
/// <remarks>
/// Разбор устойчив к незнакомым узлам (они пропускаются) и к битому XML: вместо исключения
/// возвращается предупреждение и всё, что успело прочитаться. Формы в реальной выгрузке достигают
/// мегабайта (медиана 15 КБ, максимум под 2 МБ), поэтому документ читается через <see cref="XmlReader"/>
/// без загрузки в память, а незнакомые узлы обходятся пропуском поддерева.
/// <para>
/// Поддержанные узлы: корень <c>Form</c>; <c>Attributes/Attribute</c> с <c>Name</c>, <c>Type</c>
/// (включая <c>v8:Type</c> и <c>v8:TypeSet</c>) и <c>MainAttribute</c>; <c>ChildItems</c> с
/// типизированными элементами (<c>InputField</c>, <c>Table</c>, <c>UsualGroup</c>, <c>Button</c>,
/// <c>Column</c> и любыми другими) и их <c>DataPath</c>, <c>CommandName</c>, <c>ChildItems</c>,
/// <c>AutoCommandBar</c>; <c>Commands</c> и <c>FormCommands</c> с <c>Name</c>, <c>Action</c>,
/// <c>CommandName</c>; <c>Events</c> и <c>FormEvents</c> с текстом узла <c>Event</c> как именем
/// процедуры-обработчика.
/// </para>
/// </remarks>
public sealed class FormDumpReader
{
    private static readonly XmlReaderSettings ReaderSettings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        CloseInput = false,
        CheckCharacters = false,
    };

    /// <summary>
    /// Читает описание формы из потока. Исключений не бросает: ошибки чтения попадают
    /// в <see cref="FormReadResult.Warnings"/>, а разобранная часть модели остаётся пригодной.
    /// </summary>
    /// <param name="stream">Поток файла формы.</param>
    /// <param name="sourcePath">Путь файла внутри выгрузки: попадает в модель и в предупреждения.</param>
    /// <param name="formName">Имя формы, если оно уже известно из свойств объекта метаданных.</param>
    public FormReadResult Read(Stream stream, string sourcePath, string? formName = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var state = new FormState(
            formName is { Length: > 0 } ? formName : NameFromPath(sourcePath),
            sourcePath);
        var warnings = new List<string>();

        try
        {
            using var reader = XmlReader.Create(stream, ReaderSettings);
            ReadForm(reader, state, warnings);
        }
        catch (XmlException exception)
        {
            warnings.Add($"Форма «{sourcePath}» разобрана частично: {exception.Message}");
        }
        catch (IOException exception)
        {
            warnings.Add($"Форма «{sourcePath}» не прочитана: {exception.Message}");
        }

        return new FormReadResult(state.Build(), warnings);
    }

    /// <summary>Имя формы по пути файла: «…/Forms/ФормаЭлемента/Ext/Form.xml» → «ФормаЭлемента».</summary>
    public static string NameFromPath(string sourcePath)
    {
        var normalized = DumpPath.Normalize(sourcePath);
        var name = DumpPath.GetFileName(DumpPath.GetDirectory(DumpPath.GetDirectory(normalized)));
        return name.Length > 0 ? name : DumpPath.GetFileNameWithoutExtension(normalized);
    }

    /// <summary>Разбирает корневой узел и его прямых детей — разделы описания формы.</summary>
    private static void ReadForm(XmlReader reader, FormState state, List<string> warnings)
    {
        if (!MoveToRoot(reader))
        {
            warnings.Add($"Форма «{state.SourcePath}» пуста: корневой узел не найден.");
            return;
        }

        state.Kind = KindFromNamespace(reader.NamespaceURI);
        if (!string.Equals(reader.LocalName, "Form", StringComparison.Ordinal))
        {
            warnings.Add($"Файл «{state.SourcePath}» не похож на описание формы: корневой узел «{reader.LocalName}».");
        }

        if (reader.IsEmptyElement)
        {
            return;
        }

        var depth = reader.Depth;
        var closed = false;
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth)
            {
                closed = true;
                break;
            }

            if (reader.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            switch (reader.LocalName)
            {
                case "Attributes":
                    ReadAttributes(reader, state);
                    break;
                case "Commands" or "FormCommands":
                    ReadCommands(reader, state);
                    break;
                case "ChildItems" or "Items" or "Elements":
                    ReadElementContainer(reader, state);
                    break;
                case "AutoCommandBar":
                    ReadCommandBar(reader, state);
                    break;
                case "Events" or "FormEvents":
                    ReadEvents(reader, state, element: null);
                    break;
                case "Name":
                    var name = ReadLeafText(reader);
                    if (name.Length > 0)
                    {
                        state.Name = name;
                    }

                    break;
                default:
                    SkipCurrent(reader);
                    break;
            }
        }

        // Ридер может дойти до конца файла молча (обрыв внутри узла): тогда о неполноте
        // разбора сообщает эта проверка, а не XmlException.
        if (!closed)
        {
            warnings.Add($"Форма «{state.SourcePath}» обрывается: корневой узел не закрыт.");
        }
    }

    /// <summary>Определяет вид формы по пространству имён корневого узла.</summary>
    private static FormKind KindFromNamespace(string? namespaceUri) => namespaceUri switch
    {
        null or "" => FormKind.Unknown,
        var value when value.Contains("logform", StringComparison.OrdinalIgnoreCase) => FormKind.Managed,
        var value when value.Contains("data/ui/form", StringComparison.OrdinalIgnoreCase) => FormKind.Ordinary,
        _ => FormKind.Unknown,
    };

    // --- Реквизиты формы ---------------------------------------------------------------------

    private static void ReadAttributes(XmlReader reader, FormState state)
    {
        if (reader.IsEmptyElement)
        {
            return;
        }

        var depth = reader.Depth;
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth)
            {
                break;
            }

            if (reader.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            if (!string.Equals(reader.LocalName, "Attribute", StringComparison.Ordinal))
            {
                SkipCurrent(reader);
                continue;
            }

            ReadAttribute(reader, state);
        }
    }

    private static void ReadAttribute(XmlReader reader, FormState state)
    {
        string? attributeName = reader.GetAttribute("name");
        string? childName = null;
        var isMain = false;
        var types = new List<string>();

        if (!reader.IsEmptyElement)
        {
            var depth = reader.Depth;
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth)
                {
                    break;
                }

                if (reader.NodeType != XmlNodeType.Element)
                {
                    continue;
                }

                switch (reader.LocalName)
                {
                    case "Name":
                        childName = ReadLeafText(reader);
                        break;
                    case "MainAttribute":
                        isMain = IsTrue(ReadLeafText(reader));
                        break;
                    case "Type":
                        ReadTypes(reader, types);
                        break;
                    default:
                        SkipCurrent(reader);
                        break;
                }
            }
        }

        var name = FirstNonEmpty(attributeName, childName);
        if (name is not null)
        {
            state.Attributes.Add(new FormAttribute(name, types, isMain));
        }
    }

    /// <summary>
    /// Собирает типы значения внутри узла <c>Type</c>: тип записан либо текстом узла
    /// (<c>&lt;v8:Type&gt;</c>, <c>&lt;v8:TypeSet&gt;</c>), либо вложен в контейнер.
    /// </summary>
    private static void ReadTypes(XmlReader reader, List<string> types)
    {
        if (reader.IsEmptyElement)
        {
            return;
        }

        var isTypeNode = reader.LocalName is "Type" or "TypeSet";
        var depth = reader.Depth;
        var text = new StringBuilder();

        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth)
            {
                break;
            }

            switch (reader.NodeType)
            {
                case XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace:
                    if (reader.Depth == depth + 1)
                    {
                        text.Append(reader.Value);
                    }

                    break;

                case XmlNodeType.Element:
                    // Внутри Type лежат и сами типы, и уточнения (длина строки, разрядность числа):
                    // уточнения не являются типами и по имени узла отсеиваются.
                    ReadTypes(reader, types);
                    break;
            }
        }

        if (isTypeNode)
        {
            AddType(types, text.ToString());
        }
    }

    /// <summary>Добавляет тип, приводя ссылку на объект метаданных к каноническому виду.</summary>
    private static void AddType(List<string> types, string value)
    {
        var text = value.Trim();
        if (text.Length == 0)
        {
            return;
        }

        if (MdRefParser.TryParseObjectId(text, out var id))
        {
            text = id;
        }

        if (!types.Contains(text, StringComparer.Ordinal))
        {
            types.Add(text);
        }
    }

    // --- Команды формы -----------------------------------------------------------------------

    private static void ReadCommands(XmlReader reader, FormState state)
    {
        if (reader.IsEmptyElement)
        {
            return;
        }

        var depth = reader.Depth;
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth)
            {
                break;
            }

            if (reader.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            if (!string.Equals(reader.LocalName, "Command", StringComparison.Ordinal))
            {
                SkipCurrent(reader);
                continue;
            }

            ReadCommand(reader, state);
        }
    }

    private static void ReadCommand(XmlReader reader, FormState state)
    {
        string? commandName = reader.GetAttribute("name");
        string? childName = null;
        string? action = null;
        string? objectCommand = null;

        if (!reader.IsEmptyElement)
        {
            var depth = reader.Depth;
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth)
                {
                    break;
                }

                if (reader.NodeType != XmlNodeType.Element)
                {
                    continue;
                }

                switch (reader.LocalName)
                {
                    case "Name":
                        childName = ReadLeafText(reader);
                        break;
                    case "Action":
                        action = ReadLeafText(reader);
                        break;
                    case "CommandName":
                        objectCommand = ReadLeafText(reader);
                        break;
                    default:
                        SkipCurrent(reader);
                        break;
                }
            }
        }

        var name = FirstNonEmpty(commandName, childName);
        if (name is not null)
        {
            state.Commands.Add(new FormCommand(name, EmptyToNull(action), EmptyToNull(objectCommand)));
        }
    }

    // --- Элементы формы ----------------------------------------------------------------------

    /// <summary>Читает контейнер элементов: его прямые дети — элементы формы.</summary>
    private static void ReadElementContainer(XmlReader reader, FormState state)
    {
        if (reader.IsEmptyElement)
        {
            return;
        }

        var depth = reader.Depth;
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth)
            {
                break;
            }

            if (reader.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            ReadElement(reader, state);
        }
    }

    /// <summary>
    /// Читает элемент формы: вид — имя узла, имя — атрибут <c>name</c>. Вложенные элементы
    /// лежат в <c>ChildItems</c>, кнопки командной панели — в <c>AutoCommandBar</c>.
    /// </summary>
    private static void ReadElement(XmlReader reader, FormState state)
    {
        var kind = reader.LocalName;
        var name = reader.GetAttribute("name");
        if (string.IsNullOrEmpty(name))
        {
            SkipCurrent(reader);
            return;
        }

        var index = state.Elements.Count;
        state.Elements.Add(new FormElement(name, kind));
        if (reader.IsEmptyElement)
        {
            return;
        }

        var depth = reader.Depth;
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth)
            {
                break;
            }

            if (reader.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            switch (reader.LocalName)
            {
                case "DataPath":
                    state.Elements[index] = state.Elements[index] with { DataPath = EmptyToNull(ReadLeafText(reader)) };
                    break;
                case "CommandName":
                    state.Elements[index] = state.Elements[index] with { CommandName = EmptyToNull(ReadLeafText(reader)) };
                    break;
                case "Events" or "FormEvents":
                    ReadEvents(reader, state, name);
                    break;
                case "ChildItems" or "Items" or "Elements":
                    ReadElementContainer(reader, state);
                    break;
                case "AutoCommandBar":
                    ReadCommandBar(reader, state);
                    break;
                default:
                    // ContextMenu, ExtendedTooltip, Title, CommandSet и прочие служебные узлы
                    // элементами формы не являются и пропускаются целиком.
                    SkipCurrent(reader);
                    break;
            }
        }
    }

    /// <summary>Читает командную панель: интересны только её кнопки из <c>ChildItems</c>.</summary>
    private static void ReadCommandBar(XmlReader reader, FormState state)
    {
        if (reader.IsEmptyElement)
        {
            return;
        }

        var depth = reader.Depth;
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth)
            {
                break;
            }

            if (reader.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            if (reader.LocalName is "ChildItems" or "Items")
            {
                ReadElementContainer(reader, state);
            }
            else
            {
                SkipCurrent(reader);
            }
        }
    }

    // --- Обработчики событий -----------------------------------------------------------------

    /// <summary>
    /// Читает обработчики событий: имя события — атрибут, имя процедуры — текст узла
    /// (<c>&lt;Event name="OnOpen"&gt;ПриОткрытии&lt;/Event&gt;</c>).
    /// </summary>
    private static void ReadEvents(XmlReader reader, FormState state, string? element)
    {
        if (reader.IsEmptyElement)
        {
            return;
        }

        var depth = reader.Depth;
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth)
            {
                break;
            }

            if (reader.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            if (reader.LocalName is not ("Event" or "Handler"))
            {
                SkipCurrent(reader);
                continue;
            }

            var eventName = FirstNonEmpty(reader.GetAttribute("name"), reader.GetAttribute("event"));
            var procedure = ReadEventProcedure(reader);
            if (procedure.Length == 0)
            {
                continue;
            }

            state.Handlers.Add(new FormEventHandler(eventName ?? procedure, element, procedure));
        }
    }

    /// <summary>
    /// Имя процедуры-обработчика: текст узла события, а если его нет — вложенный узел
    /// <c>Handler</c> (встречается в части версий формата).
    /// </summary>
    private static string ReadEventProcedure(XmlReader reader)
    {
        if (reader.IsEmptyElement)
        {
            return (reader.GetAttribute("handler") ?? string.Empty).Trim();
        }

        var depth = reader.Depth;
        var text = new StringBuilder();
        string? nested = null;

        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth)
            {
                break;
            }

            switch (reader.NodeType)
            {
                case XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace:
                    if (reader.Depth == depth + 1)
                    {
                        text.Append(reader.Value);
                    }

                    break;

                case XmlNodeType.Element when reader.LocalName is "Handler" or "Procedure" or "Method":
                    nested = ReadLeafText(reader);
                    break;

                case XmlNodeType.Element:
                    SkipCurrent(reader);
                    break;
            }
        }

        var direct = text.ToString().Trim();
        return direct.Length > 0 ? direct : (nested ?? string.Empty).Trim();
    }

    // --- Общие операции чтения ---------------------------------------------------------------

    private static bool MoveToRoot(XmlReader reader)
    {
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Читает текстовое значение узла (только непосредственный текст) и оставляет ридер на его конце.</summary>
    private static string ReadLeafText(XmlReader reader)
    {
        if (reader.IsEmptyElement)
        {
            return string.Empty;
        }

        var depth = reader.Depth;
        var text = new StringBuilder();
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth)
            {
                break;
            }

            if (reader.Depth == depth + 1 &&
                reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace)
            {
                text.Append(reader.Value);
            }
        }

        return text.ToString().Trim();
    }

    /// <summary>Пропускает узел целиком, оставляя ридер на его конечном теге (или на самом узле, если он пуст).</summary>
    private static void SkipCurrent(XmlReader reader)
    {
        if (reader.IsEmptyElement)
        {
            return;
        }

        var depth = reader.Depth;
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth)
            {
                return;
            }
        }
    }

    private static bool IsTrue(string value) =>
        string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) || value == "1";

    private static string? FirstNonEmpty(string? first, string? second) =>
        !string.IsNullOrWhiteSpace(first) ? first : string.IsNullOrWhiteSpace(second) ? null : second;

    private static string? EmptyToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>Накопитель разбора: ридер отдаёт данные по частям, модель собирается в конце.</summary>
    private sealed class FormState(string name, string sourcePath)
    {
        internal string Name { get; set; } = name;

        internal string SourcePath { get; } = sourcePath;

        internal FormKind Kind { get; set; }

        internal List<FormAttribute> Attributes { get; } = [];

        internal List<FormElement> Elements { get; } = [];

        internal List<FormCommand> Commands { get; } = [];

        internal List<FormEventHandler> Handlers { get; } = [];

        internal FormModel Build()
        {
            var elements = new List<FormElement>(Elements.Count);
            foreach (var element in Elements)
            {
                elements.Add(element with { Attribute = FormModel.ResolveAttribute(Attributes, element.DataPath) });
            }

            return new FormModel(Name, Kind, SourcePath, Attributes, elements, Commands, Handlers);
        }
    }
}
