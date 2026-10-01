using System.Text.Json;
using Data1c.Core.Graph;
using Data1c.Core.Metadata;

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
        var synonyms = _reader.GetSynonyms(ids);

        return
        [
            .. rows.Select(row => new GraphSearchHit(
                row.Id,
                ParseNodeKind(row.Kind),
                row.Name,
                row.MetadataKind,
                row.SourcePath,
                synonyms.GetValueOrDefault(row.Id),
                incoming.GetValueOrDefault(row.Id),
                outgoing.GetValueOrDefault(row.Id)))
        ];
    }

    /// <summary>Поиск вложенных объектов: реквизиты, табличные части, формы, команды, макеты.</summary>
    public IReadOnlyList<GraphNestedHit> SearchNested(string? query, int limit = 20, IReadOnlyCollection<string>? metadataKinds = null)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        return
        [
            .. _reader
                .SearchMetadataItems(query, Math.Clamp(limit, 1, 200), metadataKinds)
                .Select(static row => new GraphNestedHit(
                    $"{row.ObjectId}/{row.Kind}.{row.Name}",
                    row.Kind,
                    row.Name,
                    Synonym: null,
                    ObjectId: TopLevelId(row.ObjectId),
                    ParentId: row.ParentId,
                    Types: row.TypeInfo is null
                        ? []
                        : [.. row.TypeInfo.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)]))
        ];
    }

    /// <summary>Идентификатор объекта верхнего уровня: «Catalog.Товары/TabularSection.Строки» → «Catalog.Товары».</summary>
    private static string TopLevelId(string id)
    {
        var separator = id.IndexOf('/');
        return separator < 0 ? id : id[..separator];
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

    /// <summary>Карточка объекта метаданных из таблиц индекса.</summary>
    public MetadataCard? GetMetadata(string? id, int depth = 3, int maxChildren = 200)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        var row = _reader.GetMetadataObject(id.Trim());
        return row is null ? null : Card(row, Math.Clamp(depth, 1, 4), Math.Clamp(maxChildren, 1, 500));
    }

    /// <summary>Обращения к объекту метаданных из таблицы <c>metadata_refs</c>.</summary>
    public MetadataUsageSummary GetMetadataUsages(string? id, int limit = 20) =>
        string.IsNullOrWhiteSpace(id) ? MetadataUsageSummary.Empty : _reader.GetMetadataUsages(id.Trim(), limit);

    private MetadataCard Card(MetadataObjectRow row, int depth, int maxChildren)
    {
        var references = _reader.ReferencesOf(row.Id, limit: 400);
        var page = _reader.MetadataChildren(row.Id, maxChildren);

        var children = new List<MetadataCard>();
        if (depth > 1)
        {
            children.AddRange(page.Items.Select(item => Item(row.Id, item, depth - 1, maxChildren)));
        }

        // Описание формы читается только для самой формы: у остальных объектов его нет,
        // а лишний запрос на каждый узел дерева карточке ни к чему.
        var form = IsFormKind(row.Kind) ? _reader.FormDetails(row.Id) : null;

        return new MetadataCard(
            row.Id,
            row.Kind,
            row.Name,
            row.Synonym,
            row.Comment,
            row.Uuid,
            row.IsTopLevel,
            row.ParentId,
            row.SourcePath,
            [.. references
                .Where(static reference => reference.Context == "type")
                .Select(static reference => reference.TargetId)
                .Distinct(StringComparer.Ordinal)
                .Take(10)],
            ParseProperties(row.Properties),
            [.. references
                .Where(static reference => reference.Context != "type")
                .Take(60)
                .Select(static reference => new MetadataReferenceInfo(reference.Context, reference.TargetId, reference.Detail))],
            _reader.ModulePaths(row.Id),
            children,
            Math.Max(0, page.Total - children.Count),
            form);
    }

    /// <summary>
    /// Вложенный объект собирается из записи состава: вид, имя, синоним, комментарий и типы,
    /// а его собственные дети — из следующих уровней таблицы состава.
    /// </summary>
    private MetadataCard Item(string ownerId, MetadataItemRow item, int depth, int maxChildren)
    {
        var id = $"{ownerId}/{item.Kind}.{item.Name}";
        var children = new List<MetadataCard>();
        var hidden = 0;
        if (depth > 1)
        {
            var page = _reader.MetadataChildren(id, maxChildren);
            children.AddRange(page.Items.Select(child => Item(id, child, depth - 1, maxChildren)));
            hidden = Math.Max(0, page.Total - children.Count);
        }

        return new MetadataCard(
            id,
            item.Kind,
            item.Name,
            item.Synonym,
            item.Comment,
            Uuid: null,
            IsTopLevel: false,
            ParentId: ownerId,
            SourcePath: null,
            ParseTypes(item.TypeInfo),
            new Dictionary<string, string>(StringComparer.Ordinal),
            [],
            [],
            children,
            hidden);
    }

    private static IReadOnlyList<string> ParseTypes(string? typeInfo) =>
        typeInfo is null
            ? []
            : [.. typeInfo.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)];

    /// <summary>Объект метаданных — форма: только у неё есть описание реквизитов, элементов и обработчиков.</summary>
    private static bool IsFormKind(string kind) =>
        string.Equals(kind, MdKind.Form.Name, StringComparison.Ordinal)
        || string.Equals(kind, MdKind.CommonForm.Name, StringComparison.Ordinal);

    private static IReadOnlyDictionary<string, string> ParseProperties(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
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
