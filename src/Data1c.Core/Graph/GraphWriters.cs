using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Data1c.Core.Graph;

/// <summary>Экспорт графа в JSON.</summary>
public static class GraphJsonWriter
{
    private static readonly JsonWriterOptions Compact = new()
    {
        Indented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonWriterOptions Pretty = new()
    {
        Indented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static void Write(Stream stream, DependencyGraph graph, bool indented = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(graph);

        using var writer = new Utf8JsonWriter(stream, indented ? Pretty : Compact);
        writer.WriteStartObject();

        writer.WritePropertyName("nodes");
        writer.WriteStartArray();
        foreach (var node in graph.Nodes)
        {
            writer.WriteStartObject();
            writer.WriteString("id", node.Id);
            writer.WriteString("kind", node.Kind.ToString());
            writer.WriteString("name", node.Name);
            WriteOptional(writer, "source", node.SourcePath);
            WriteOptional(writer, "mdKind", node.MetadataKind);
            if (node.IsExternal)
            {
                writer.WriteBoolean("external", true);
            }

            if (node.Tags is { Count: > 0 })
            {
                writer.WritePropertyName("tags");
                writer.WriteStartObject();
                foreach (var (key, value) in node.Tags)
                {
                    writer.WriteString(key, value);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();

        writer.WritePropertyName("edges");
        writer.WriteStartArray();
        foreach (var edge in graph.Edges)
        {
            writer.WriteStartObject();
            writer.WriteString("from", edge.SourceId);
            writer.WriteString("to", edge.TargetId);
            writer.WriteString("kind", edge.Kind.ToString());
            if (edge.Line is { } line)
            {
                writer.WriteNumber("line", line);
            }

            WriteOptional(writer, "detail", edge.Detail);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();

        WriteStatistics(writer, graph.Statistics);
        writer.WriteEndObject();
        writer.Flush();
    }

    public static string ToJson(DependencyGraph graph, bool indented = false)
    {
        using var stream = new MemoryStream();
        Write(stream, graph, indented);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteStatistics(Utf8JsonWriter writer, GraphStatistics statistics)
    {
        writer.WritePropertyName("statistics");
        writer.WriteStartObject();
        writer.WriteNumber("nodes", statistics.NodeCount);
        writer.WriteNumber("edges", statistics.EdgeCount);
        writer.WriteNumber("externalNodes", statistics.ExternalNodeCount);
        writer.WriteNumber("unresolvedEdges", statistics.UnresolvedEdgeCount);
        writer.WriteNumber("metadataObjects", statistics.MetadataObjectCount);
        writer.WriteNumber("modules", statistics.ModuleCount);
        writer.WriteNumber("routines", statistics.RoutineCount);

        writer.WritePropertyName("nodesByKind");
        writer.WriteStartObject();
        foreach (var (key, value) in statistics.NodesByKind)
        {
            writer.WriteNumber(key, value);
        }

        writer.WriteEndObject();

        writer.WritePropertyName("edgesByKind");
        writer.WriteStartObject();
        foreach (var (key, value) in statistics.EdgesByKind)
        {
            writer.WriteNumber(key, value);
        }

        writer.WriteEndObject();

        writer.WriteEndObject();
    }

    private static void WriteOptional(Utf8JsonWriter writer, string name, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            writer.WriteString(name, value);
        }
    }
}

/// <summary>Настройки экспорта графа в формат Graphviz DOT.</summary>
public sealed record GraphDotOptions
{
    /// <summary>Какие связи включать. null — все.</summary>
    public IReadOnlyCollection<GraphEdgeKind>? EdgeKinds { get; init; }

    /// <summary>Какие типы узлов включать. null — все.</summary>
    public IReadOnlyCollection<GraphNodeKind>? NodeKinds { get; init; }

    /// <summary>Ограничить граф окружением указанного узла (он и его соседи).</summary>
    public string? RootId { get; init; }

    /// <summary>Максимальное число узлов в выводе.</summary>
    public int MaxNodes { get; init; } = 500;

    /// <summary>Включать узлы-заглушки отсутствующих объектов.</summary>
    public bool IncludeExternal { get; init; } = true;

    public string GraphName { get; init; } = "data1c";
}

/// <summary>Экспорт графа в Graphviz DOT.</summary>
public static class GraphDotWriter
{
    public static void Write(TextWriter writer, DependencyGraph graph, GraphDotOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(graph);
        options ??= new GraphDotOptions();

        var selection = Select(graph, options);

        writer.WriteLine($"digraph {Sanitize(options.GraphName)} {{");
        writer.WriteLine("  rankdir=LR;");
        writer.WriteLine("  node [shape=box, fontname=\"Segoe UI\", fontsize=10];");
        writer.WriteLine("  edge [fontname=\"Segoe UI\", fontsize=8];");

        foreach (var node in selection.Nodes)
        {
            var shape = node.Kind switch
            {
                GraphNodeKind.Routine => "ellipse",
                GraphNodeKind.Configuration => "doubleoctagon",
                _ => "box",
            };

            var attributes = new List<string>
            {
                $"label=\"{Escape(ShortLabel(node))}\"",
                $"tooltip=\"{Escape(node.Id)}\"",
                $"color=\"{ColorOf(node.Kind)}\"",
                $"shape={shape}",
            };

            if (node.IsExternal)
            {
                attributes.Add("style=dashed");
            }

            writer.WriteLine($"  \"{Escape(node.Id)}\" [{string.Join(", ", attributes)}];");
        }

        foreach (var edge in selection.Edges)
        {
            var attributes = new List<string> { $"color=\"{ColorOf(edge.Kind)}\"" };
            if (!string.IsNullOrEmpty(edge.Detail))
            {
                attributes.Add($"label=\"{Escape(Trim(edge.Detail, 32))}\"");
            }

            writer.WriteLine($"  \"{Escape(edge.SourceId)}\" -> \"{Escape(edge.TargetId)}\" [{string.Join(", ", attributes)}];");
        }

        writer.WriteLine("}");
    }

    private static (IReadOnlyList<GraphNode> Nodes, IReadOnlyList<GraphEdge> Edges) Select(
        DependencyGraph graph,
        GraphDotOptions options)
    {
        HashSet<string>? allowed = null;
        if (options.RootId is { Length: > 0 } root && graph.TryGetNode(root, out _))
        {
            allowed = new HashSet<string>(StringComparer.Ordinal) { root };
            foreach (var edge in graph.Outgoing(root))
            {
                allowed.Add(edge.TargetId);
            }

            foreach (var edge in graph.Incoming(root))
            {
                allowed.Add(edge.SourceId);
            }
        }

        var nodes = new List<GraphNode>();
        foreach (var node in graph.Nodes)
        {
            if (!options.IncludeExternal && node.Kind == GraphNodeKind.External)
            {
                continue;
            }

            if (options.NodeKinds is { Count: > 0 } kinds && !kinds.Contains(node.Kind))
            {
                continue;
            }

            if (allowed is not null && !allowed.Contains(node.Id))
            {
                continue;
            }

            if (nodes.Count >= options.MaxNodes)
            {
                break;
            }

            nodes.Add(node);
        }

        var nodeIds = new HashSet<string>(nodes.Select(static n => n.Id), StringComparer.Ordinal);
        var edges = new List<GraphEdge>();
        foreach (var edge in graph.Edges)
        {
            if (options.EdgeKinds is { Count: > 0 } kinds && !kinds.Contains(edge.Kind))
            {
                continue;
            }

            if (!nodeIds.Contains(edge.SourceId) || !nodeIds.Contains(edge.TargetId))
            {
                continue;
            }

            edges.Add(edge);
        }

        return (nodes, edges);
    }

    private static string ShortLabel(GraphNode node)
    {
        var name = node.Kind switch
        {
            GraphNodeKind.Routine => node.Name,
            GraphNodeKind.Module => node.Name,
            GraphNodeKind.MetadataObject => $"{node.MetadataKind}.{node.Name}",
            _ => node.Name,
        };

        return Trim(name, 48);
    }

    private static string Trim(string value, int max) =>
        value.Length <= max ? value : value[..(max - 1)] + "…";

    private static string ColorOf(GraphNodeKind kind) => kind switch
    {
        GraphNodeKind.Configuration => "#1f4e79",
        GraphNodeKind.MetadataObject => "#2e7d32",
        GraphNodeKind.Module => "#ef6c00",
        GraphNodeKind.Routine => "#1565c0",
        _ => "#9e9e9e",
    };

    private static string ColorOf(GraphEdgeKind kind) => kind switch
    {
        GraphEdgeKind.Contains => "#bdbdbd",
        GraphEdgeKind.References => "#2e7d32",
        GraphEdgeKind.Defines => "#ef6c00",
        GraphEdgeKind.Calls => "#c62828",
        _ => "#6a1b9a",
    };

    private static string Sanitize(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            builder.Append(char.IsLetterOrDigit(ch) || ch == '_' ? ch : '_');
        }

        return builder.ToString();
    }

    private static string Escape(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);
}
