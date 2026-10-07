using System.Collections.Concurrent;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Data1c.Mcp;

/// <summary>
/// Транспорт и протокол MCP: построчный JSON-RPC 2.0 поверх stdio.
/// </summary>
/// <remarks>
/// stdio-транспорт MCP — это по одному JSON-сообщению в строке: сообщения не содержат переводов строк,
/// а stdout целиком принадлежит протоколу. Диагностика поэтому идёт в <see cref="_log"/> (stderr).
/// Запросы обрабатываются параллельно (клиент может прислать отмену, пока идёт долгий вызов),
/// но вызовы инструментов сериализуются: разбор выгрузки один на процесс.
/// </remarks>
public sealed class McpServer
{
    /// <summary>Версия протокола, которую сервер объявляет, если клиент предложил неизвестную.</summary>
    public const string LatestProtocolVersion = "2025-06-18";

    /// <summary>Версии протокола, которые сервер понимает.</summary>
    private static readonly string[] SupportedProtocolVersions = ["2025-06-18", "2025-03-26", "2024-11-05"];

    public const string ServerName = "data1c";
    public const string ServerVersion = "0.1.0";

    /// <summary>Ответы пишутся без экранирования кириллицы: агент видит имена объектов как в 1С.</summary>
    private static readonly JsonSerializerOptions Wire = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private const string Instructions =
        "Инструменты разбора выгрузки конфигурации 1С (формат «Выгрузить конфигурацию в файлы»). "
        + "Порядок работы: status — готов ли разбор; search — найти идентификатор объекта или процедуры; "
        + "metadata — состав объекта (реквизиты, табличные части); code — исходный текст; "
        + "node и neighbors — связи, вызовы и обращения к метаданным; "
        + "entrypoints — код, который вызывает платформа (подписки на события, регламентные задания, обработчики форм); "
        + "platform — справка платформы; "
        + "check — структурная проверка модуля после правки; reload — перечитать выгрузку целиком "
        + "или, с аргументом paths, только указанные модули.";

    private readonly ToolCatalog _catalog;
    private readonly TextReader _input;
    private readonly TextWriter _output;
    private readonly TextWriter _log;
    private readonly SemaphoreSlim _toolGate = new(1, 1);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _requests = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<long, Task> _pending = new();
    private long _handled;

    public McpServer(ToolCatalog catalog, TextReader input, TextWriter output, TextWriter? log = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);

        _catalog = catalog;
        _input = input;
        _output = output;
        _log = log ?? TextWriter.Null;
    }

    /// <summary>Читает сообщения до конца потока (клиент закрыл stdin) или до отмены.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = await _input.ReadLineAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (IOException exception)
            {
                Log("чтение stdin прервано: " + exception.Message);
                break;
            }

            if (line is null)
            {
                break;
            }

            if (line.Length == 0)
            {
                continue;
            }

            // Обработка в отдельной задаче: пока идёт долгий вызов инструмента, сервер должен
            // успевать читать notifications/cancelled. Незавершённые задачи дожидаются при остановке.
            var number = Interlocked.Increment(ref _handled);
            var handling = HandleAsync(line, cancellationToken);
            _pending[number] = handling;
            _ = handling.ContinueWith(
                completed => _pending.TryRemove(number, out _),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        // Все сообщения прочитаны: даём обработчикам дописать ответы, иначе клиент не увидит результат.
        await Task.WhenAll(_pending.Values.ToArray()).ConfigureAwait(false);
        CancelAll();
    }

    private async Task HandleAsync(string line, CancellationToken cancellationToken)
    {
        JsonObject? request;
        try
        {
            request = JsonNode.Parse(line) as JsonObject;
        }
        catch (JsonException exception)
        {
            Log("сообщение не разобрано: " + exception.Message);
            await WriteErrorAsync(null, -32700, "Ошибка разбора JSON: " + exception.Message).ConfigureAwait(false);
            return;
        }

        if (request is null)
        {
            await WriteErrorAsync(null, -32600, "Ожидался объект JSON-RPC.").ConfigureAwait(false);
            return;
        }

        var id = request["id"];
        var method = request["method"] is JsonValue value && value.TryGetValue<string>(out var name) ? name : string.Empty;
        var parameters = request["params"] as JsonObject;

        try
        {
            switch (method)
            {
                case "initialize":
                    await WriteResultAsync(id, Initialize(parameters)).ConfigureAwait(false);
                    break;

                case "notifications/initialized":
                    break;

                case "notifications/cancelled":
                    Cancel(parameters);
                    break;

                case "ping":
                    await WriteResultAsync(id, new JsonObject()).ConfigureAwait(false);
                    break;

                case "tools/list":
                    await WriteResultAsync(id, ToolsList()).ConfigureAwait(false);
                    break;

                case "tools/call":
                    await WriteResultAsync(id, await CallToolAsync(id, parameters, cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
                    break;

                default:
                    if (id is null || method.StartsWith("notifications/", StringComparison.Ordinal))
                    {
                        Log($"уведомление «{method}» не поддерживается и пропущено");
                        break;
                    }

                    await WriteErrorAsync(id, -32601, $"Метод «{method}» не поддерживается.").ConfigureAwait(false);
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            Log($"запрос «{method}» отменён");
        }
        catch (Exception exception)
        {
            Log($"сбой обработки «{method}»: {exception}");
            if (id is not null)
            {
                await WriteErrorAsync(id, -32603, "Внутренняя ошибка сервера: " + exception.Message).ConfigureAwait(false);
            }
        }
    }

    private JsonObject Initialize(JsonObject? parameters)
    {
        var requested = parameters?["protocolVersion"] is JsonValue value && value.TryGetValue<string>(out var version)
            ? version
            : null;

        var negotiated = requested is not null && SupportedProtocolVersions.Contains(requested, StringComparer.Ordinal)
            ? requested
            : LatestProtocolVersion;

        var client = parameters?["clientInfo"]?["name"]?.GetValue<string>();
        Log($"инициализация: клиент «{client ?? "неизвестен"}», протокол {negotiated} (запрошен {requested ?? "не указан"})");

        return new JsonObject
        {
            ["protocolVersion"] = negotiated,
            ["capabilities"] = new JsonObject
            {
                ["tools"] = new JsonObject { ["listChanged"] = false },
            },
            ["serverInfo"] = new JsonObject
            {
                ["name"] = ServerName,
                ["title"] = "Data1C — разбор выгрузки 1С",
                ["version"] = ServerVersion,
            },
            ["instructions"] = Instructions,
        };
    }

    private JsonObject ToolsList()
    {
        var tools = new JsonArray();
        foreach (var tool in _catalog.Tools)
        {
            tools.Add(new JsonObject
            {
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["inputSchema"] = tool.ToSchema(),
            });
        }

        return new JsonObject { ["tools"] = tools };
    }

    private async Task<JsonObject> CallToolAsync(JsonNode? id, JsonObject? parameters, CancellationToken cancellationToken)
    {
        var name = parameters?["name"] is JsonValue value && value.TryGetValue<string>(out var toolName) ? toolName : null;
        if (string.IsNullOrWhiteSpace(name))
        {
            return ErrorContent("Не указано имя инструмента.");
        }

        var tool = _catalog.Find(name);
        if (tool is null)
        {
            return ErrorContent($"Инструмент «{name}» не найден. Доступны: {string.Join(", ", _catalog.Tools.Select(static t => t.Name))}.");
        }

        var key = id?.ToJsonString();
        using var request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (key is not null)
        {
            _requests[key] = request;
        }

        await _toolGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Log($"вызов {name}");
            var arguments = ToolArguments.From(parameters?["arguments"]);
            var text = await tool.Execute(arguments, request.Token).ConfigureAwait(false);
            return TextContent(text);
        }
        catch (OperationCanceledException)
        {
            return ErrorContent("Вызов отменён клиентом.");
        }
        catch (ToolException exception)
        {
            Log($"{name}: {exception.Message}");
            return ErrorContent(exception.Message);
        }
        catch (Exception exception)
        {
            Log($"{name}: сбой — {exception}");
            return ErrorContent($"Сбой выполнения {name}: {exception.Message}");
        }
        finally
        {
            _toolGate.Release();
            if (key is not null)
            {
                _requests.TryRemove(key, out _);
            }
        }
    }

    private void Cancel(JsonObject? parameters)
    {
        var requestId = parameters?["requestId"];
        var key = requestId?.ToJsonString();
        if (key is not null && _requests.TryGetValue(key, out var source))
        {
            Log($"отмена запроса {key}");
            try
            {
                source.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Вызов успел завершиться до отмены — отменять нечего.
            }
        }
    }

    private void CancelAll()
    {
        foreach (var (_, source) in _requests)
        {
            try
            {
                source.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Запрос уже завершился — отменять нечего.
            }
        }
    }

    private static JsonObject TextContent(string text) => new()
    {
        ["content"] = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "text",
                ["text"] = text,
            },
        },
    };

    private static JsonObject ErrorContent(string message) => new()
    {
        ["content"] = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "text",
                ["text"] = message,
            },
        },
        ["isError"] = true,
    };

    private Task WriteResultAsync(JsonNode? id, JsonObject result) =>
        id is null ? Task.CompletedTask : WriteAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id.DeepClone(),
            ["result"] = result,
        });

    private Task WriteErrorAsync(JsonNode? id, int code, string message) => WriteAsync(new JsonObject
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id?.DeepClone(),
        ["error"] = new JsonObject
        {
            ["code"] = code,
            ["message"] = message,
        },
    });

    private async Task WriteAsync(JsonObject message)
    {
        var json = message.ToJsonString(Wire);

        await _writeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await _output.WriteLineAsync(json).ConfigureAwait(false);
            await _output.FlushAsync().ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private void Log(string message) => _log.WriteLine($"data1c-mcp: {message}");
}
