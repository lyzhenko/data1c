using System.Text;
using System.Xml;
using Data1c.Core.Dump;

namespace Data1c.Core.Metadata;

/// <summary>Право роли на объект: имя права (Read, Insert, Update, Delete, View, InteractiveInsert, …) и его значение.</summary>
/// <param name="Name">Имя права так, как оно записано в файле прав.</param>
/// <param name="Value">Значение права: <see langword="true"/> — право выдано, <see langword="false"/> — снято.</param>
public sealed record RoleRightEntry(string Name, bool Value);

/// <summary>
/// Объект в файле прав роли: имя из файла, разрешённый идентификатор объекта метаданных,
/// список прав и признак ограничения доступа к данным на уровне записей (RLS).
/// </summary>
/// <param name="Name">Имя объекта в файле прав: «Catalog.Товары», «Subsystem.Продажи.Subsystem.Розница», «Configuration».</param>
/// <param name="ObjectId">Идентификатор объекта метаданных; пусто, если имя не удалось разобрать.</param>
/// <param name="Kind">Вид объекта метаданных.</param>
/// <param name="Rights">Права на объект в порядке файла.</param>
/// <param name="HasRestriction">У объекта есть условие ограничения доступа к данным (RLS).</param>
/// <param name="Condition">Текст условия RLS; заполняется только при разборе с условиями.</param>
public sealed record RoleRightsObject(
    string Name,
    string ObjectId,
    MdKind Kind,
    IReadOnlyList<RoleRightEntry> Rights,
    bool HasRestriction,
    string? Condition = null)
{
    /// <summary>Имя объекта разрешено в идентификатор объекта метаданных.</summary>
    public bool IsResolved => ObjectId.Length > 0;

    /// <summary>Сколько прав выдано.</summary>
    public int GrantedCount => Rights.Count(static right => right.Value);

    /// <summary>Сколько прав снято.</summary>
    public int DeniedCount => Rights.Count(static right => !right.Value);
}

/// <summary>Права одной роли, разобранные из <c>Roles/&lt;Имя&gt;/Ext/Rights.xml</c>.</summary>
/// <param name="Role">Имя роли (каталог файла прав).</param>
/// <param name="SourcePath">Путь файла прав внутри выгрузки.</param>
/// <param name="SetForNewObjects">Права распространяются на новые объекты.</param>
/// <param name="SetForAttributesByDefault">Права по умолчанию распространяются на реквизиты.</param>
/// <param name="IndependentRightsOfChildObjects">Права подчинённых объектов заданы независимо.</param>
/// <param name="Objects">Объекты с правами в порядке файла.</param>
public sealed record RoleRights(
    string Role,
    string SourcePath,
    bool SetForNewObjects,
    bool SetForAttributesByDefault,
    bool IndependentRightsOfChildObjects,
    IReadOnlyList<RoleRightsObject> Objects)
{
    /// <summary>Идентификатор роли в модели метаданных, графе и индексе: «Role.Менеджер».</summary>
    public string RoleId => MdNaming.CreateId(MdKind.Role, Role);

    /// <summary>Сколько прав выдано во всех объектах роли.</summary>
    public int GrantedCount => Objects.Sum(static obj => obj.GrantedCount);

    /// <summary>Сколько прав снято во всех объектах роли.</summary>
    public int DeniedCount => Objects.Sum(static obj => obj.DeniedCount);

    /// <summary>У скольких объектов роли включено ограничение RLS.</summary>
    public int RestrictionCount => Objects.Count(static obj => obj.HasRestriction);
}

/// <summary>Результат разбора файла прав.</summary>
/// <param name="Rights">Права роли: то, что успело разобраться при битом XML.</param>
/// <param name="Warnings">Замечания разбора: битый XML, неожиданный корневой узел, право или объект без имени.</param>
public sealed record RightsReadResult(RoleRights Rights, IReadOnlyList<string> Warnings);

/// <summary>
/// Сжатая запись прав для строки <c>metadata_refs</c>: «Read=true;Insert=false», а признак
/// ограничения доступа к данным — метка <see cref="RlsMark"/> в конце. Сам текст условия RLS
/// метку не заменяет: он пишется в отдельную колонку <c>metadata_refs.condition</c> (схема v9),
/// поэтому признак ограничения по <c>detail</c> виден и там, где текста нет.
/// </summary>
public static class RightsDetail
{
    /// <summary>Метка ограничения доступа к данным на уровне записей (RLS).</summary>
    public const string RlsMark = "RLS";

    /// <summary>Собирает <c>detail</c> строки прав: права в порядке файла и метка RLS в конце.</summary>
    /// <param name="rights">Права роли на объект.</param>
    /// <param name="hasRestriction">У объекта есть условие RLS.</param>
    public static string Format(IReadOnlyList<RoleRightEntry> rights, bool hasRestriction)
    {
        ArgumentNullException.ThrowIfNull(rights);
        var text = string.Join(';', rights.Select(static right => $"{right.Name}={(right.Value ? "true" : "false")}"));
        return hasRestriction ? text.Length == 0 ? RlsMark : text + ";" + RlsMark : text;
    }

    /// <summary>
    /// Разбирает <c>detail</c> строки прав обратно в записи: «Read=true;Insert=false;RLS» →
    /// «Read» = true, «Insert» = false. Метка <see cref="RlsMark"/> без «=» пропускается: признак
    /// ограничения инструмент читает из <c>detail</c>, а текст условия — из <c>metadata_refs.condition</c>.
    /// </summary>
    /// <param name="detail">Сжатая запись прав из строки индекса.</param>
    public static IReadOnlyList<RoleRightEntry> Parse(string detail)
    {
        var entries = new List<RoleRightEntry>();
        if (string.IsNullOrWhiteSpace(detail))
        {
            return entries;
        }

        foreach (var part in detail.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var name = part[..separator].Trim();
            if (name.Length == 0)
            {
                continue;
            }

            var value = part[(separator + 1)..].Trim();
            entries.Add(new RoleRightEntry(name, string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)));
        }

        return entries;
    }
}

/// <summary>
/// Потоковый разбор прав роли 1С (<c>Roles/&lt;Имя&gt;/Ext/Rights.xml</c>): признаки роли,
/// затем повторяющиеся <c>object</c> с именем объекта, правами (<c>right/name</c>, <c>right/value</c>)
/// и признаком условия RLS (<c>restrictionByCondition</c>).
/// </summary>
/// <remarks>
/// Разбор устойчив к незнакомым узлам (они пропускаются поддеревом) и к битому XML: вместо исключения
/// возвращается предупреждение и всё, что успело прочитаться. Файл читается через <see cref="XmlReader"/>
/// без загрузки документа в память: в реальной выгрузке права занимают до 337 КБ и содержат тысячи объектов,
/// а всего таких файлов больше тысячи.
/// <para>
/// При <c>includeConditions = false</c> отмечается только признак <see cref="RoleRightsObject.HasRestriction"/>;
/// так собираются сводки, которым текст не нужен. С <c>includeConditions = true</c> текст попадает в
/// <see cref="RoleRightsObject.Condition"/>: так его пишут в индекс (схема v9) и читают из файла роли
/// как запасной путь, когда в индексе условия нет.
/// </para>
/// </remarks>
public sealed class RightsDumpReader
{
    /// <summary>Больше этого числа замечаний разбор не копит: битый файл иначе забьёт ответ.</summary>
    private const int MaxWarnings = 50;

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
    /// Читает права роли из потока. Исключений не бросает: ошибки чтения попадают в
    /// <see cref="RightsReadResult.Warnings"/>, а разобранная часть модели остаётся пригодной.
    /// </summary>
    /// <param name="stream">Поток файла прав.</param>
    /// <param name="sourcePath">Путь файла внутри выгрузки: попадает в модель и в предупреждения.</param>
    /// <param name="roleName">Имя роли, если оно уже известно; иначе берётся из пути файла.</param>
    /// <param name="includeConditions">Читать текст условий RLS (по умолчанию только признак ограничения).</param>
    public RightsReadResult Read(Stream stream, string sourcePath, string? roleName = null, bool includeConditions = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var state = new RightsState(
            roleName is { Length: > 0 } ? roleName : RoleNameFromPath(sourcePath),
            DumpPath.Normalize(sourcePath),
            includeConditions);

        try
        {
            using var reader = XmlReader.Create(DumpTextReader.CreateTextReader(stream), ReaderSettings);
            if (!MoveToRoot(reader))
            {
                AddWarning(state.Warnings, $"Файл прав «{state.SourcePath}» пуст: корневой узел не найден.");
            }
            else
            {
                if (!string.Equals(reader.LocalName, "Rights", StringComparison.Ordinal))
                {
                    AddWarning(state.Warnings, $"Файл «{state.SourcePath}» не похож на файл прав: корневой узел «{reader.LocalName}».");
                }

                ReadRights(reader, state);
            }
        }
        catch (XmlException exception)
        {
            AddWarning(state.Warnings, $"Права роли «{state.Role}» разобраны частично: {exception.Message}");
        }
        catch (IOException exception)
        {
            AddWarning(state.Warnings, $"Права роли «{state.Role}» не прочитаны: {exception.Message}");
        }

        return new RightsReadResult(state.Build(), state.Warnings);
    }

    /// <summary>Файл лежит в разделе прав ролей: «Roles/&lt;Имя&gt;/Ext/Rights.xml».</summary>
    public static bool IsRightsFile(string relativePath)
    {
        var segments = DumpPath.Segments(relativePath);
        return segments.Length >= 4
            && string.Equals(segments[0], "Roles", StringComparison.OrdinalIgnoreCase)
            && string.Equals(segments[^2], "Ext", StringComparison.OrdinalIgnoreCase)
            && string.Equals(segments[^1], "Rights.xml", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Имя роли по пути файла прав: «Roles/Менеджер/Ext/Rights.xml» → «Менеджер».</summary>
    public static string RoleNameFromPath(string sourcePath)
    {
        var segments = DumpPath.Segments(sourcePath);
        if (segments.Length >= 3
            && string.Equals(segments[^1], "Rights.xml", StringComparison.OrdinalIgnoreCase)
            && string.Equals(segments[^2], "Ext", StringComparison.OrdinalIgnoreCase))
        {
            return segments[^3];
        }

        var directory = DumpPath.GetFileName(DumpPath.GetDirectory(sourcePath));
        return directory.Length > 0 ? directory : DumpPath.GetFileNameWithoutExtension(sourcePath);
    }

    // --- Разбор ------------------------------------------------------------------------------

    /// <summary>Читает корневой узел: признаки роли и повторяющиеся объекты с правами.</summary>
    private static void ReadRights(XmlReader reader, RightsState state)
    {
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
                case "setForNewObjects":
                    state.SetForNewObjects = IsTrue(ReadLeafText(reader));
                    break;
                case "setForAttributesByDefault":
                    state.SetForAttributesByDefault = IsTrue(ReadLeafText(reader));
                    break;
                case "independentRightsOfChildObjects":
                    state.IndependentRightsOfChildObjects = IsTrue(ReadLeafText(reader));
                    break;
                case "object":
                    ReadObject(reader, state);
                    break;
                default:
                    SkipCurrent(reader);
                    break;
            }
        }

        // Ридер может дойти до конца файла молча (обрыв внутри узла): о неполноте сообщает эта проверка.
        if (!closed)
        {
            AddWarning(state.Warnings, $"Файл прав «{state.SourcePath}» обрывается: корневой узел не закрыт.");
        }
    }

    /// <summary>Читает объект прав: имя, права и признак условия RLS.</summary>
    private static void ReadObject(XmlReader reader, RightsState state)
    {
        var obj = new ObjectState();
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
                    case "name":
                        var name = ReadLeafText(reader);
                        obj.Name = string.IsNullOrWhiteSpace(obj.Name) ? name : obj.Name;
                        break;
                    case "right":
                        ReadRight(reader, obj, state);
                        break;
                    case "restrictionByCondition":
                        ReadRestriction(reader, obj, state);
                        break;
                    default:
                        SkipCurrent(reader);
                        break;
                }
            }
        }

        var objectName = obj.Name?.Trim() ?? string.Empty;
        if (objectName.Length == 0)
        {
            AddWarning(state.Warnings, $"В файле прав «{state.SourcePath}» пропущен объект без имени.");
            return;
        }

        RightsTargetResolver.TryResolve(objectName, out var objectId, out var kind);
        state.Objects.Add(new RoleRightsObject(objectName, objectId, kind, obj.Rights, obj.HasRestriction, obj.Condition));
    }

    /// <summary>Читает право: имя, значение и (если встретилось) условие ограничения внутри самого права.</summary>
    private static void ReadRight(XmlReader reader, ObjectState obj, RightsState state)
    {
        string? name = null;
        var value = false;
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
                    case "name":
                        var text = ReadLeafText(reader);
                        name = string.IsNullOrWhiteSpace(name) ? text : name;
                        break;
                    case "value":
                        value = IsTrue(ReadLeafText(reader));
                        break;
                    case "restrictionByCondition":
                        ReadRestriction(reader, obj, state);
                        break;
                    default:
                        SkipCurrent(reader);
                        break;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            AddWarning(state.Warnings, $"В файле прав «{state.SourcePath}» пропущено право без имени.");
            return;
        }

        obj.Rights.Add(new RoleRightEntry(name.Trim(), value));
    }

    /// <summary>
    /// Читает условие ограничения доступа к данным: отмечает признак RLS, а текст условия
    /// сохраняет только по запросу (<c>includeConditions</c>). Пустая заглушка
    /// (<c>xsi:nil="true"</c>) ограничением не считается.
    /// </summary>
    private static void ReadRestriction(XmlReader reader, ObjectState obj, RightsState state)
    {
        var nil = string.Equals(reader.GetAttribute("nil", "http://www.w3.org/2001/XMLSchema-instance"), "true", StringComparison.OrdinalIgnoreCase);
        if (!nil)
        {
            obj.HasRestriction = true;
        }

        if (!state.IncludeConditions || nil)
        {
            SkipCurrent(reader);
            return;
        }

        var text = ReadSubtreeText(reader);
        if (text.Length > 0 && string.IsNullOrEmpty(obj.Condition))
        {
            obj.Condition = text;
        }
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

    /// <summary>
    /// Читает весь текст поддерева, включая вложенные узлы: условие RLS записано либо текстом
    /// самого <c>restrictionByCondition</c>, либо внутри вложенного <c>condition</c>.
    /// </summary>
    private static string ReadSubtreeText(XmlReader reader)
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

            if (reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.SignificantWhitespace)
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

    private static void AddWarning(List<string> warnings, string text)
    {
        if (warnings.Count < MaxWarnings)
        {
            warnings.Add(text);
        }
    }

    /// <summary>Накопитель разбора: ридер отдаёт данные по частям, модель собирается в конце.</summary>
    private sealed class RightsState(string role, string sourcePath, bool includeConditions)
    {
        internal string Role { get; } = role;

        internal string SourcePath { get; } = sourcePath;

        internal bool IncludeConditions { get; } = includeConditions;

        internal bool SetForNewObjects { get; set; }

        internal bool SetForAttributesByDefault { get; set; }

        internal bool IndependentRightsOfChildObjects { get; set; }

        internal List<RoleRightsObject> Objects { get; } = [];

        internal List<string> Warnings { get; } = [];

        internal RoleRights Build() => new(
            Role,
            SourcePath,
            SetForNewObjects,
            SetForAttributesByDefault,
            IndependentRightsOfChildObjects,
            Objects);
    }

    /// <summary>Накопитель одного объекта прав: имя, права и признак условия RLS.</summary>
    private sealed class ObjectState
    {
        internal string? Name { get; set; }

        internal List<RoleRightEntry> Rights { get; } = [];

        internal bool HasRestriction { get; set; }

        internal string? Condition { get; set; }
    }
}

/// <summary>
/// Разрешение имён объектов из файла прав в идентификаторы объектов метаданных:
/// <c>Catalog.Товары</c> → <c>Catalog.Товары</c>, <c>Configuration</c> → <c>Configuration</c>,
/// <c>Subsystem.Продажи.Subsystem.Розница</c> → <c>Subsystem.Продажи/Subsystem.Розница</c>.
/// </summary>
/// <remarks>
/// Вложенные подсистемы записаны в правах цепочкой «Subsystem.Имя.Subsystem.Имя» и разбираются
/// в идентификатор с разделителем «/» — так же, как в модели метаданных. Остальные вложенные
/// объекты (реквизиты, табличные части) в правах не встречаются, поэтому остаток имени после
/// первого объекта добавляется одним сегментом.
/// </remarks>
public static class RightsTargetResolver
{
    /// <summary>Пытается разрешить имя объекта из файла прав в идентификатор объекта метаданных.</summary>
    /// <param name="name">Имя так, как оно записано в файле прав.</param>
    /// <param name="objectId">Канонический идентификатор; пусто, если имя не разобрано.</param>
    /// <param name="kind">Вид объекта метаданных.</param>
    public static bool TryResolve(string name, out string objectId, out MdKind kind)
    {
        objectId = string.Empty;
        kind = MdKind.Unknown;
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var text = name.Trim();

        // Права на конфигурацию целиком записаны без вида: <name>Configuration</name>.
        if (string.Equals(text, MdKind.Configuration.Name, StringComparison.OrdinalIgnoreCase))
        {
            objectId = MdKind.Configuration.Name;
            kind = MdKind.Configuration;
            return true;
        }

        if (!MdRefParser.TryParse(text, out kind, out var objectName, out var rest))
        {
            kind = MdKind.Unknown;
            return false;
        }

        var id = new StringBuilder(MdNaming.CreateId(kind, objectName));
        while (!string.IsNullOrEmpty(rest))
        {
            if (MdRefParser.TryParse(rest, out var nestedKind, out var nestedName, out var nestedRest))
            {
                id.Append('/').Append(MdNaming.CreateId(nestedKind, nestedName));
                rest = nestedRest;
                continue;
            }

            id.Append('/').Append(rest.Trim('/'));
            break;
        }

        objectId = id.ToString();
        return true;
    }
}
