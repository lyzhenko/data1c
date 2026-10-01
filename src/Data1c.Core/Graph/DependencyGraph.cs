using Data1c.Core.Metadata;

namespace Data1c.Core.Graph;

/// <summary>Тип узла графа зависимостей.</summary>
public enum GraphNodeKind
{
    Configuration,
    MetadataObject,
    Module,
    Routine,

    /// <summary>Узел-заглушка для цели, которой нет в разобранной выгрузке.</summary>
    External,

    /// <summary>
    /// Метод или свойство платформы 1С: подтверждённые справкой установленной версии либо выведенные
    /// по известному типу переменной (Э2-4: «Таблица.Свернуть» при типе «ТаблицаЗначений»).
    /// </summary>
    Platform,
}

/// <summary>Тип связи между узлами.</summary>
public enum GraphEdgeKind
{
    /// <summary>Вложенность: конфигурация → объект → модуль → процедура.</summary>
    Contains,

    /// <summary>Ссылка метаданных: тип реквизита, содержимое подсистемы, право роли и т. п.</summary>
    References,

    /// <summary>Объявление процедуры или функции в модуле.</summary>
    Defines,

    /// <summary>Вызов процедуры или функции.</summary>
    Calls,

    /// <summary>Обращение из кода к объекту метаданных.</summary>
    UsesMetadata,
}

/// <summary>Узел графа.</summary>
/// <param name="Id">Устойчивый идентификатор узла.</param>
/// <param name="Kind">Тип узла.</param>
/// <param name="Name">Отображаемое имя.</param>
/// <param name="SourcePath">Путь файла выгрузки, если узел связан с файлом.</param>
/// <param name="MetadataKind">Вид объекта метаданных для узлов метаданных.</param>
/// <param name="IsExternal">Узел отсутствует в разобранной выгрузке.</param>
/// <param name="Tags">Дополнительные сведения (синоним, вид модуля, признак экспорта и т. п.).</param>
public sealed record GraphNode(
    string Id,
    GraphNodeKind Kind,
    string Name,
    string? SourcePath = null,
    string? MetadataKind = null,
    bool IsExternal = false,
    IReadOnlyDictionary<string, string?>? Tags = null);

/// <summary>Связь между узлами графа.</summary>
/// <param name="SourceId">Идентификатор узла-источника.</param>
/// <param name="TargetId">Идентификатор узла-цели.</param>
/// <param name="Kind">Тип связи.</param>
/// <param name="Line">Строка в файле модуля для связей из кода.</param>
/// <param name="Detail">Уточнение (например, имя реквизита или признак неразрешённого вызова).</param>
/// <param name="Context">
/// Контекст обращения для таблицы <c>metadata_refs</c>: <see cref="MetadataRefContexts.Code"/> —
/// обращение из кода, <see cref="MetadataRefContexts.Query"/> — из текста запроса.
/// <see langword="null"/> означает <see cref="MetadataRefContexts.Code"/>.
/// </param>
public sealed record GraphEdge(
    string SourceId,
    string TargetId,
    GraphEdgeKind Kind,
    int? Line = null,
    string? Detail = null,
    string? Context = null);

/// <summary>Контексты обращений к метаданным в таблице <c>metadata_refs</c>.</summary>
public static class MetadataRefContexts
{
    /// <summary>Обращение из кода BSL: «Справочники.Товары».</summary>
    public const string Code = "code";

    /// <summary>Обращение из текста запроса: «ИЗ Справочник.Товары».</summary>
    public const string Query = "query";

    /// <summary>Права роли на объект: в <c>detail</c> лежит сжатый перечень прав («Read=true;Insert=false»),
    /// признак ограничения доступа к данным — метка «RLS».</summary>
    public const string Right = "right";

    /// <summary>
    /// Контекст перекрёстной ссылки объекта метаданных по смыслу ссылки в XML.
    /// Тот же набор значений, что пишет индекс в <c>metadata_refs.context</c> для ссылок.
    /// </summary>
    /// <param name="kind">Смысл ссылки, найденной в XML.</param>
    public static string FromReference(MdReferenceKind kind) => kind switch
    {
        MdReferenceKind.Type => "type",
        MdReferenceKind.Content => "content",
        MdReferenceKind.Field => "field",
        MdReferenceKind.Form => "form",
        MdReferenceKind.Template => "template",
        MdReferenceKind.Command => "command",
        MdReferenceKind.RoleRight => Right,
        MdReferenceKind.EventSource => "event",
        _ => "other",
    };

    /// <summary>
    /// Вид связи графа, к которому относится контекст обращения: код и тексты запросов — это
    /// <see cref="GraphEdgeKind.UsesMetadata"/>, остальные контексты — ссылки метаданных
    /// (<see cref="GraphEdgeKind.References"/>).
    /// </summary>
    /// <param name="context">Контекст обращения из <c>metadata_refs</c>.</param>
    public static string KindOf(string context) =>
        context is Code or Query ? nameof(GraphEdgeKind.UsesMetadata) : nameof(GraphEdgeKind.References);

    /// <summary>Подпись контекста для ответа агенту: «в коде», «в запросах», «в типах» и так далее.</summary>
    /// <param name="context">Контекст обращения из <c>metadata_refs</c>.</param>
    public static string Label(string context) => context switch
    {
        Code => "в коде",
        Query => "в запросах",
        "type" => "в типах",
        "content" => "в составе объекта",
        "field" => "в полях",
        "form" => "в формах",
        "template" => "в макетах",
        "command" => "в командах",
        Right => "в правах ролей",
        "event" => "в подписках на события",
        "other" => "прочее",
        _ => context,
    };
}

/// <summary>Сводная статистика графа.</summary>
public sealed record GraphStatistics(
    int NodeCount,
    int EdgeCount,
    int ExternalNodeCount,
    int UnresolvedEdgeCount,
    int MetadataObjectCount,
    int ModuleCount,
    int RoutineCount,
    IReadOnlyDictionary<string, int> NodesByKind,
    IReadOnlyDictionary<string, int> EdgesByKind);

/// <summary>Настройки построения графа.</summary>
public sealed record DependencyGraphOptions
{
    /// <summary>Включать связи вложенности.</summary>
    public bool IncludeContainment { get; init; } = true;

    /// <summary>Включать ссылки метаданных (типы, содержимое подсистем, права ролей).</summary>
    public bool IncludeMetadataReferences { get; init; } = true;

    /// <summary>Включать узлы модулей.</summary>
    public bool IncludeModules { get; init; } = true;

    /// <summary>Включать узлы процедур и функций.</summary>
    public bool IncludeRoutines { get; init; } = true;

    /// <summary>Включать связи вызовов.</summary>
    public bool IncludeCalls { get; init; } = true;

    /// <summary>Включать обращения к метаданным из кода.</summary>
    public bool IncludeMetadataAccess { get; init; } = true;

    /// <summary>Включать обращения к метаданным из текстов запросов.</summary>
    public bool IncludeQueryReferences { get; init; } = true;

    /// <summary>Создавать узлы-заглушки для целей, которых нет в выгрузке.</summary>
    public bool IncludeExternalNodes { get; init; } = true;

    /// <summary>Включать вложенные объекты (реквизиты, табличные части). По умолчанию они не попадают в граф.</summary>
    public bool IncludeNestedObjects { get; init; }
}

/// <summary>Граф зависимостей выгрузки: объекты метаданных, модули, процедуры и связи между ними.</summary>
public sealed class DependencyGraph
{
    private readonly Dictionary<string, GraphNode> _nodes;
    private readonly Dictionary<string, List<GraphEdge>> _outgoing = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<GraphEdge>> _incoming = new(StringComparer.Ordinal);

    public DependencyGraph(IReadOnlyList<GraphNode> nodes, IReadOnlyList<GraphEdge> edges)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(edges);

        Nodes = nodes;
        Edges = edges;
        _nodes = new Dictionary<string, GraphNode>(nodes.Count, StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            _nodes.TryAdd(node.Id, node);
        }

        foreach (var edge in edges)
        {
            GetOrAdd(_outgoing, edge.SourceId).Add(edge);
            GetOrAdd(_incoming, edge.TargetId).Add(edge);
        }

        Statistics = BuildStatistics(nodes, edges);
    }

    public IReadOnlyList<GraphNode> Nodes { get; }

    public IReadOnlyList<GraphEdge> Edges { get; }

    public GraphStatistics Statistics { get; }

    public bool TryGetNode(string id, out GraphNode node) => _nodes.TryGetValue(id, out node!);

    public GraphNode? FindNode(string id) => _nodes.TryGetValue(id, out var node) ? node : null;

    /// <summary>Связи, выходящие из узла.</summary>
    public IReadOnlyList<GraphEdge> Outgoing(string nodeId) =>
        _outgoing.TryGetValue(nodeId, out var list) ? list : [];

    /// <summary>Связи, входящие в узел.</summary>
    public IReadOnlyList<GraphEdge> Incoming(string nodeId) =>
        _incoming.TryGetValue(nodeId, out var list) ? list : [];

    /// <summary>Идентификаторы узлов, которые вызывает указанный узел.</summary>
    public IEnumerable<string> Callees(string nodeId) =>
        Outgoing(nodeId).Where(static e => e.Kind == GraphEdgeKind.Calls).Select(static e => e.TargetId).Distinct(StringComparer.Ordinal);

    /// <summary>Идентификаторы узлов, которые вызывают указанный узел.</summary>
    public IEnumerable<string> Callers(string nodeId) =>
        Incoming(nodeId).Where(static e => e.Kind == GraphEdgeKind.Calls).Select(static e => e.SourceId).Distinct(StringComparer.Ordinal);

    private static List<GraphEdge> GetOrAdd(Dictionary<string, List<GraphEdge>> map, string key)
    {
        if (!map.TryGetValue(key, out var list))
        {
            list = [];
            map[key] = list;
        }

        return list;
    }

    private static GraphStatistics BuildStatistics(IReadOnlyList<GraphNode> nodes, IReadOnlyList<GraphEdge> edges)
    {
        var nodesByKind = nodes
            .GroupBy(static n => n.Kind.ToString())
            .ToDictionary(static g => g.Key, static g => g.Count(), StringComparer.Ordinal);

        var edgesByKind = edges
            .GroupBy(static e => e.Kind.ToString())
            .ToDictionary(static g => g.Key, static g => g.Count(), StringComparer.Ordinal);

        var externalNodes = nodes.Count(static n => n.Kind == GraphNodeKind.External);
        var externalIds = new HashSet<string>(
            nodes.Where(static n => n.Kind == GraphNodeKind.External).Select(static n => n.Id),
            StringComparer.Ordinal);
        var unresolvedEdges = edges.Count(e => externalIds.Contains(e.TargetId));

        return new GraphStatistics(
            nodes.Count,
            edges.Count,
            externalNodes,
            unresolvedEdges,
            nodes.Count(static n => n.Kind == GraphNodeKind.MetadataObject),
            nodes.Count(static n => n.Kind == GraphNodeKind.Module),
            nodes.Count(static n => n.Kind == GraphNodeKind.Routine),
            nodesByKind,
            edgesByKind);
    }
}
