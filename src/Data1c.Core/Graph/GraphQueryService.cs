namespace Data1c.Core.Graph;

/// <summary>Результат поиска узла по имени.</summary>
/// <param name="Id">Идентификатор узла.</param>
/// <param name="Kind">Тип узла.</param>
/// <param name="Name">Отображаемое имя.</param>
/// <param name="MetadataKind">Вид объекта метаданных для узлов метаданных.</param>
/// <param name="SourcePath">Путь файла выгрузки.</param>
/// <param name="Synonym">Синоним объекта метаданных, если есть.</param>
/// <param name="IncomingCount">Число входящих связей.</param>
/// <param name="OutgoingCount">Число исходящих связей.</param>
public sealed record GraphSearchHit(
    string Id,
    GraphNodeKind Kind,
    string Name,
    string? MetadataKind,
    string? SourcePath,
    string? Synonym,
    int IncomingCount,
    int OutgoingCount);

/// <summary>Запрос окружения узла для интерактивного просмотра.</summary>
public sealed record GraphNeighborhoodRequest
{
    /// <summary>Узел, вокруг которого строится окружение.</summary>
    public required string NodeId { get; init; }

    /// <summary>Радиус обхода в шагах связей.</summary>
    public int Depth { get; init; } = 1;

    /// <summary>Ограничение числа узлов в ответе.</summary>
    public int MaxNodes { get; init; } = 300;

    /// <summary>Учитывать входящие связи.</summary>
    public bool Incoming { get; init; } = true;

    /// <summary>Учитывать исходящие связи.</summary>
    public bool Outgoing { get; init; } = true;

    /// <summary>Какие типы связей учитывать. null — все.</summary>
    public IReadOnlyCollection<GraphEdgeKind>? EdgeKinds { get; init; }

    /// <summary>Какие типы узлов включать. null — все.</summary>
    public IReadOnlyCollection<GraphNodeKind>? NodeKinds { get; init; }
}

/// <summary>Подграф: узлы и связи между ними.</summary>
/// <param name="Nodes">Узлы подграфа.</param>
/// <param name="Edges">Связи, оба конца которых попали в подграф.</param>
/// <param name="Truncated">Обход остановлен из-за ограничения <see cref="GraphNeighborhoodRequest.MaxNodes"/>.</param>
public sealed record GraphNeighborhood(IReadOnlyList<GraphNode> Nodes, IReadOnlyList<GraphEdge> Edges, bool Truncated);

/// <summary>Карточка узла: сам узел и его связи.</summary>
public sealed record GraphNodeDetails(GraphNode Node, IReadOnlyList<GraphEdge> Incoming, IReadOnlyList<GraphEdge> Outgoing);

/// <summary>
/// Запросы к готовому графу для интерактивного просмотра: поиск узлов, окружение узла, карточка узла.
/// Граф при этом не копируется, поэтому сервис можно держать в памяти всё время работы просмотрщика.
/// </summary>
public sealed class GraphQueryService : IGraphQuery
{
    private readonly DependencyGraph _graph;

    public GraphQueryService(DependencyGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        _graph = graph;
    }

    public DependencyGraph Graph => _graph;

    public GraphStatistics Statistics => _graph.Statistics;

    /// <summary>Узел по идентификатору без загрузки связей.</summary>
    public GraphNode? FindNode(string? id) => string.IsNullOrWhiteSpace(id) ? null : _graph.FindNode(id.Trim());

    /// <summary>
    /// Ищет узлы по идентификатору, имени, синониму, виду объекта или пути файла.
    /// Ранжирование: точное совпадение, затем совпадение с начала строки, затем вхождение.
    /// </summary>
    public IReadOnlyList<GraphSearchHit> Search(string? query, int limit = 30)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        var text = query.Trim();
        var hits = new List<(int Score, int Degree, GraphNode Node)>();

        foreach (var node in _graph.Nodes)
        {
            var score = Rank(node, text);
            if (score < 0)
            {
                continue;
            }

            hits.Add((score, _graph.Incoming(node.Id).Count + _graph.Outgoing(node.Id).Count, node));
        }

        return hits
            .OrderBy(static h => h.Score)
            .ThenByDescending(static h => h.Degree)
            .ThenBy(static h => h.Node.Id, StringComparer.Ordinal)
            .Take(Math.Max(1, limit))
            .Select(h => new GraphSearchHit(
                h.Node.Id,
                h.Node.Kind,
                h.Node.Name,
                h.Node.MetadataKind,
                h.Node.SourcePath,
                GetSynonym(h.Node),
                _graph.Incoming(h.Node.Id).Count,
                _graph.Outgoing(h.Node.Id).Count))
            .ToList();
    }

    /// <summary>
    /// Находит узлы по ссылке из командной строки: точный идентификатор, затем поиск.
    /// </summary>
    public IReadOnlyList<GraphNode> Resolve(string? reference, int limit = 10)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return [];
        }

        var text = reference.Trim();
        var exact = new List<GraphNode>();
        if (_graph.TryGetNode(text, out var node))
        {
            exact.Add(node);
        }

        foreach (var hit in Search(text, limit))
        {
            if (exact.All(n => !string.Equals(n.Id, hit.Id, StringComparison.Ordinal)))
            {
                exact.Add(_graph.FindNode(hit.Id)!);
            }
        }

        return exact;
    }

    /// <summary>Строит окружение узла в ширину с учётом направления и типов связей.</summary>
    public GraphNeighborhood GetNeighborhood(GraphNeighborhoodRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var maxNodes = Math.Max(1, request.MaxNodes);

        // Центральный узел показывается всегда, даже если его тип исключён фильтром:
        // пользователь явно выбрал именно его.
        if (!_graph.TryGetNode(request.NodeId, out var start))
        {
            return new GraphNeighborhood([], [], false);
        }

        var nodes = new List<GraphNode> { start };
        var nodeIds = new HashSet<string>(StringComparer.Ordinal) { start.Id };
        var edges = new List<GraphEdge>();
        var edgeKeys = new HashSet<(string, string, GraphEdgeKind, int)>();
        var queue = new Queue<(GraphNode Node, int Depth)>();
        queue.Enqueue((start, 0));
        var truncated = false;

        while (queue.Count > 0)
        {
            var (current, depth) = queue.Dequeue();
            if (depth >= request.Depth)
            {
                continue;
            }

            foreach (var (edge, neighbourId) in Neighbours(current, request))
            {
                if (nodes.Count >= maxNodes && !nodeIds.Contains(neighbourId))
                {
                    truncated = true;
                    continue;
                }

                if (!_graph.TryGetNode(neighbourId, out var neighbour))
                {
                    continue;
                }

                var key = (edge.SourceId, edge.TargetId, edge.Kind, edge.Line ?? -1);
                if (edgeKeys.Add(key))
                {
                    edges.Add(edge);
                }

                if (nodeIds.Add(neighbour.Id))
                {
                    nodes.Add(neighbour);
                    queue.Enqueue((neighbour, depth + 1));
                }
            }
        }

        // Связи, у которых один конец не попал в подграф, отбрасываем: иначе просмотрщик
        // нарисует висящие рёбра.
        var visible = edges.Where(e => nodeIds.Contains(e.SourceId) && nodeIds.Contains(e.TargetId)).ToList();
        return new GraphNeighborhood(nodes, visible, truncated);
    }

    /// <summary>Возвращает узел вместе с входящими и исходящими связями.</summary>
    public GraphNodeDetails? GetNode(string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || !_graph.TryGetNode(id, out var node))
        {
            return null;
        }

        return new GraphNodeDetails(node, _graph.Incoming(node.Id), _graph.Outgoing(node.Id));
    }

    private IEnumerable<(GraphEdge Edge, string NeighbourId)> Neighbours(GraphNode node, GraphNeighborhoodRequest request)
    {
        if (request.Outgoing)
        {
            foreach (var edge in _graph.Outgoing(node.Id))
            {
                if (IsAllowed(edge, request.EdgeKinds) && IsAllowed(edge.TargetId, request.NodeKinds))
                {
                    yield return (edge, edge.TargetId);
                }
            }
        }

        if (request.Incoming)
        {
            foreach (var edge in _graph.Incoming(node.Id))
            {
                if (IsAllowed(edge, request.EdgeKinds) && IsAllowed(edge.SourceId, request.NodeKinds))
                {
                    yield return (edge, edge.SourceId);
                }
            }
        }
    }

    private bool IsAllowed(string nodeId, IReadOnlyCollection<GraphNodeKind>? kinds)
    {
        if (kinds is null || kinds.Count == 0)
        {
            return true;
        }

        return _graph.TryGetNode(nodeId, out var node) && IsAllowed(node, kinds);
    }

    private static bool IsAllowed(GraphNode node, IReadOnlyCollection<GraphNodeKind>? kinds) =>
        kinds is null || kinds.Count == 0 || kinds.Contains(node.Kind);

    private static bool IsAllowed(GraphEdge edge, IReadOnlyCollection<GraphEdgeKind>? kinds) =>
        kinds is null || kinds.Count == 0 || kinds.Contains(edge.Kind);

    private static string? GetSynonym(GraphNode node) =>
        node.Tags is not null && node.Tags.TryGetValue("synonym", out var synonym) ? synonym : null;

    private static int Rank(GraphNode node, string query)
    {
        if (string.Equals(node.Id, query, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (string.Equals(node.Name, query, StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        var synonym = GetSynonym(node);
        if (synonym is not null && string.Equals(synonym, query, StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }

        if (node.Id.StartsWith(query, StringComparison.OrdinalIgnoreCase))
        {
            return 3;
        }

        if (node.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
        {
            return 4;
        }

        if (node.Id.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            return 5;
        }

        if (node.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            return 6;
        }

        if (node.MetadataKind is not null && node.MetadataKind.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            return 7;
        }

        if (synonym is not null && synonym.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            return 8;
        }

        if (node.SourcePath is not null && node.SourcePath.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            return 9;
        }

        return -1;
    }
}
