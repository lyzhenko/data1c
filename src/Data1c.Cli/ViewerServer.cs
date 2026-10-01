using System.Net;
using System.Net.Sockets;
using System.Text;
using Data1c.Core.Analysis;
using Data1c.Core.Graph;

namespace Data1c.Cli;

/// <summary>
/// Минимальный HTTP-сервер просмотрщика графа: отдаёт страницу и несколько JSON-методов поверх
/// <see cref="GraphQueryService"/>. Сделан на <see cref="TcpListener"/>, а не на
/// <see cref="HttpListener"/>: последний требует прав администратора на регистрацию префикса.
/// </summary>
internal sealed class ViewerServer : IDisposable
{
    private const string JsonContentType = "application/json; charset=utf-8";
    private const string HtmlContentType = "text/html; charset=utf-8";

    private readonly TcpListener _listener;
    private readonly GraphQueryService _query;
    private readonly SourceCodeReader? _code;
    private readonly string _html;

    public ViewerServer(GraphQueryService query, string html, int port, SourceCodeReader? code = null)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(html);

        _query = query;
        _html = html;
        _code = code;
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
    }

    public int Port { get; }

    public string Url => $"http://127.0.0.1:{Port}/";

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException)
            {
                break;
            }

            _ = Task.Run(() => HandleAsync(client, cancellationToken), CancellationToken.None);
        }
    }

    public void Dispose() => _listener.Stop();

    private async Task HandleAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            try
            {
                client.NoDelay = true;
                await using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);

                var requestLine = await reader.ReadLineAsync(cancellationToken);
                if (string.IsNullOrEmpty(requestLine))
                {
                    return;
                }

                // Заголовки до пустой строки; тело запроса просмотрщик не отправляет.
                while (true)
                {
                    var line = await reader.ReadLineAsync(cancellationToken);
                    if (string.IsNullOrEmpty(line))
                    {
                        break;
                    }
                }

                var parts = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2 || !string.Equals(parts[0], "GET", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteAsync(stream, "405 Method Not Allowed", "text/plain; charset=utf-8", "Только GET"u8.ToArray(), cancellationToken);
                    return;
                }

                var (status, contentType, body) = Route(parts[1]);
                await WriteAsync(stream, status, contentType, body, cancellationToken);
            }
            catch (Exception)
            {
                // Обрыв соединения браузером — обычное дело при навигации, шуметь не нужно.
            }
        }
    }

    private (string Status, string ContentType, byte[] Body) Route(string target)
    {
        var separator = target.IndexOf('?');
        var path = separator < 0 ? target : target[..separator];
        var parameters = ParseQuery(separator < 0 ? string.Empty : target[(separator + 1)..]);

        try
        {
            switch (path)
            {
                case "/":
                case "/index.html":
                    return ("200 OK", HtmlContentType, Encoding.UTF8.GetBytes(_html));

                case "/api/stats":
                    return ("200 OK", JsonContentType, Json(_query.Statistics));

                case "/api/search":
                    return ("200 OK", JsonContentType, Json(_query
                        .Search(Get(parameters, "q"), ParseInt(Get(parameters, "limit"), 30))
                        .ToList()));

                case "/api/node":
                    var details = _query.GetNode(Get(parameters, "id"));
                    return details is null
                        ? ("404 Not Found", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("Узел не найден"))
                        : ("200 OK", JsonContentType, Json(details));

                case "/api/expand":
                    return ("200 OK", JsonContentType, Json(_query.GetNeighborhood(BuildRequest(parameters))));

                case "/api/code":
                    var code = BuildCode(parameters);
                    return code is null
                        ? ("404 Not Found", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("Исходный текст этого узла недоступен"))
                        : ("200 OK", JsonContentType, Json(code));

                default:
                    return ("404 Not Found", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("Не найдено"));
            }
        }
        catch (Exception exception)
        {
            return ("500 Internal Server Error", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(exception.Message));
        }
    }

    private static GraphNeighborhoodRequest BuildRequest(Dictionary<string, string> parameters) => new()
    {
        NodeId = Get(parameters, "id") ?? string.Empty,
        Depth = Math.Clamp(ParseInt(Get(parameters, "depth"), 1), 1, 6),
        MaxNodes = Math.Clamp(ParseInt(Get(parameters, "max"), 250), 1, 5000),
        Incoming = ParseBool(Get(parameters, "in"), true),
        Outgoing = ParseBool(Get(parameters, "out"), true),
        EdgeKinds = ParseEnums<GraphEdgeKind>(Get(parameters, "edges")),
        NodeKinds = ParseEnums<GraphNodeKind>(Get(parameters, "nodes")),
    };

    private static byte[] Json<T>(T value) => ViewerAssets.Serialize(value);

    /// <summary>
    /// Возвращает исходный текст узла: для процедуры — её тело (по умолчанию вместе с окружением),
    /// для модуля и объекта — файл целиком или его начало. Диапазон можно задать явно
    /// (<c>from</c>/<c>to</c>) или запросить весь файл (<c>all=1</c>).
    /// </summary>
    private CodeResponse? BuildCode(Dictionary<string, string> parameters)
    {
        if (_code is null)
        {
            return null;
        }

        var id = Get(parameters, "id");
        if (id is null || !_query.Graph.TryGetNode(id, out var node) || string.IsNullOrEmpty(node.SourcePath))
        {
            return null;
        }

        var highlightFrom = 0;
        var highlightTo = 0;
        if (node.Kind == GraphNodeKind.Routine)
        {
            var start = TagInt(node, "startLine");
            var length = TagInt(node, "lines");
            if (start > 0)
            {
                highlightFrom = start;
                highlightTo = start + Math.Max(1, length) - 1;
            }
        }

        var all = ParseBool(Get(parameters, "all"), false);
        var from = ParseInt(Get(parameters, "from"), 0);
        var to = ParseInt(Get(parameters, "to"), 0);
        if (all)
        {
            from = 1;
            to = 0;
        }
        else
        {
            if (from <= 0)
            {
                from = highlightFrom > 0 ? Math.Max(1, highlightFrom - 300) : 1;
            }

            if (to <= 0)
            {
                to = highlightTo > 0 ? highlightTo + 300 : 6000;
            }
        }

        var fragment = _code.Read(node.SourcePath, from, to);
        if (fragment is null)
        {
            return null;
        }

        return new CodeResponse(
            node.Id,
            TitleOf(node),
            fragment.Path,
            fragment.StartLine,
            fragment.EndLine,
            fragment.TotalLines,
            fragment.Lines,
            fragment.Truncated,
            highlightFrom,
            highlightTo);
    }

    private static string TitleOf(GraphNode node) => node.Kind switch
    {
        GraphNodeKind.MetadataObject when node.MetadataKind is not null => $"{node.MetadataKind}.{node.Name}",
        GraphNodeKind.Module => (node.Tags is not null && node.Tags.TryGetValue("owner", out var owner) ? owner : null) ?? node.Name,
        _ => node.Name,
    };

    private static int TagInt(GraphNode node, string key) =>
        node.Tags is not null && node.Tags.TryGetValue(key, out var value) &&
        int.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var result)
            ? result
            : 0;

    private sealed record CodeResponse(
        string Id,
        string Title,
        string Path,
        int StartLine,
        int EndLine,
        int TotalLines,
        IReadOnlyList<string> Lines,
        bool Truncated,
        int HighlightFrom,
        int HighlightTo);

    private static async Task WriteAsync(Stream stream, string status, string contentType, byte[] body, CancellationToken cancellationToken)
    {
        var header = $"HTTP/1.1 {status}\r\nContent-Type: {contentType}\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(header), cancellationToken);
        await stream.WriteAsync(body, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            var key = separator < 0 ? pair : pair[..separator];
            var value = separator < 0 ? string.Empty : pair[(separator + 1)..];
            result[Uri.UnescapeDataString(key)] = Uri.UnescapeDataString(value.Replace('+', ' '));
        }

        return result;
    }

    private static string? Get(Dictionary<string, string> parameters, string key) =>
        parameters.TryGetValue(key, out var value) && value.Length > 0 ? value : null;

    private static int ParseInt(string? value, int fallback) =>
        int.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var result) ? result : fallback;

    private static bool ParseBool(string? value, bool fallback) => value switch
    {
        null => fallback,
        "1" or "true" or "on" => true,
        "0" or "false" or "off" => false,
        _ => fallback,
    };

    private static IReadOnlyCollection<TEnum>? ParseEnums<TEnum>(string? value)
        where TEnum : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var result = new List<TEnum>();
        foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Enum.TryParse<TEnum>(part, ignoreCase: true, out var parsed))
            {
                result.Add(parsed);
            }
        }

        return result.Count == 0 ? null : result;
    }
}
