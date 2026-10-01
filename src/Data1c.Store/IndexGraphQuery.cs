using Data1c.Core.Graph;

namespace Data1c.Store;

/// <summary>
/// Запросы к графу, прочитанному из SQLite-индекса. Реализует тот же контракт, что и
/// <see cref="GraphQueryService"/> поверх графа в памяти, поэтому MCP-сервер и просмотрщик
/// не знают, откуда взялись данные.
/// </summary>
public sealed class IndexGraphQuery : IGraphQuery
{
    private readonly IndexReader _reader;

    public IndexGraphQuery(IndexReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        _reader = reader;
    }

    /// <summary>Статистика графа: считается по таблицам индекса.</summary>
    public GraphStatistics Statistics
    {
        get
        {
            var summary = _reader.GetStatistics();
            var nodesByKind = _reader.CountNodesByKind();
            var edgesByKind = _reader.CountEdgesByKind();

            return new GraphStatistics(
                (int)summary.Nodes,
                (int)summary.Edges,
                (int)summary.ExternalNodes,
                (int)_reader.CountUnresolvedEdges(),
                nodesByKind.GetValueOrDefault("MetadataObject"),
                nodesByKind.GetValueOrDefault("Module"),
                nodesByKind.GetValueOrDefault("Routine"),
                nodesByKind,
                edgesByKind);
        }
    }

    public IReadOnlyList<GraphSearchHit> Search(string? query, int limit = 30)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        var rows = _reader.Search(query, Math.Clamp(limit, 1, 200));
        if (rows.Count == 0)
        {
            return [];
        }

        var ids = rows.Select(static row => row.Id).ToList();
        var incoming = _reader.CountIncoming(ids);
        var outgoing = _reader.CountOutgoing(ids);

        return
        [
            .. rows.Select(row => new GraphSearchHit(
                row.Id,
                ParseNodeKind(row.Kind),
                row.Name,
                row.MetadataKind,
                row.SourcePath,
                Synonym: null,
                incoming.GetValueOrDefault(row.Id),
                outgoing.GetValueOrDefault(row.Id)))
        ];
    }

    public IReadOnlyList<GraphNode> Resolve(string? reference, int limit = 10)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return [];
        }

        var text = reference.Trim();
        var exact = _reader.GetNode(text);
        if (exact is not null)
        {
            return [Map(exact)];
        }

        return [.. _reader.Search(text, Math.Clamp(limit, 1, 100)).Select(Map)];
    }

    public GraphNeighborhood GetNeighborhood(GraphNeighborhoodRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var direction = request switch
        {
            { Incoming: true, Outgoing: false } => "in",
            { Incoming: false, Outgoing: true } => "out",
            _ => "all",
        };

        var edgeKinds = request.EdgeKinds?.Select(static kind => kind.ToString()).ToList();
        var nodeKinds = request.NodeKinds?.Select(static kind => kind.ToString()).ToList();

        var (nodes, edges) = _reader.Neighborhood(request.NodeId, request.Depth, request.MaxNodes, direction, nodeKinds, edgeKinds);
        var reached = _reader.Reach(request.NodeId, request.Depth, request.MaxNodes, direction, edgeKinds);

        return new GraphNeighborhood(
            [.. nodes.Select(Map)],
            MapEdges(edges),
            reached.Count >= request.MaxNodes);
    }

    /// <summary>Узел по идентификатору без загрузки связей.</summary>
    public GraphNode? FindNode(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        var row = _reader.GetNode(id.Trim());
        return row is null ? null : Map(row);
    }

    public GraphNodeDetails? GetNode(string? id)
    {        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        var row = _reader.GetNode(id.Trim());
        if (row is null)
        {
            return null;
        }

        return new GraphNodeDetails(
            Map(row),
            MapEdges(_reader.Incoming(row.Id, kind: null, limit: 200)),
            MapEdges(_reader.Outgoing(row.Id, kind: null, limit: 200)));
    }

    /// <summary>Связи неизвестного вида отбрасываются: индекс пишется тем же перечислением, поэтому это защита, а не норма.</summary>
    private static IReadOnlyList<GraphEdge> MapEdges(IEnumerable<EdgeRow> rows) =>
    [
        .. rows
            .Select(static row => Enum.TryParse<GraphEdgeKind>(row.Kind, ignoreCase: false, out var kind)
                ? new GraphEdge(row.SourceId, row.TargetId, kind, row.Line, row.Detail)
                : null)
            .OfType<GraphEdge>()
    ];

    private static GraphNode Map(NodeRow row) => new(
        row.Id,
        ParseNodeKind(row.Kind),
        row.Name,
        row.SourcePath,
        row.MetadataKind,
        row.IsExternal,
        row.PlatformVersion is null && row.PlatformTitle is null
            ? null
            : new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["platformTitle"] = row.PlatformTitle,
                ["platformVersion"] = row.PlatformVersion,
            });

    private static GraphNodeKind ParseNodeKind(string value) =>
        Enum.TryParse<GraphNodeKind>(value, ignoreCase: false, out var kind) ? kind : GraphNodeKind.External;
}
