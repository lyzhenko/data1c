using System.Text.Json.Nodes;
using Data1c.Mcp;
using Xunit;

namespace Data1c.Tests.Mcp;

/// <summary>
/// Проверки MCP-сервера: согласование протокола, список инструментов и работа инструментов
/// на синтетической выгрузке <see cref="SampleDump"/>.
/// </summary>
public sealed class McpServerTests
{
    private const string InitializeKnown =
        """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","clientInfo":{"name":"тест","version":"1"}}}""";

    private const string Initialized = """{"jsonrpc":"2.0","method":"notifications/initialized"}""";

    [Fact]
    public async Task Инициализация_согласует_известную_версию_протокола()
    {
        var responses = await ExchangeAsync(InitializeKnown);

        var result = Result(responses, 1);
        Assert.Equal("2024-11-05", Text(result["protocolVersion"]));
        Assert.Equal("data1c", Text(result["serverInfo"]!["name"]));
        Assert.NotNull(result["capabilities"]!["tools"]);
        Assert.False(string.IsNullOrWhiteSpace(Text(result["instructions"])));
    }

    [Fact]
    public async Task Инициализация_подменяет_неизвестную_версию_протокола()
    {
        var responses = await ExchangeAsync(
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"1999-01-01"}}""");

        Assert.Equal(McpServer.LatestProtocolVersion, Text(Result(responses, 1)["protocolVersion"]));
    }

    [Fact]
    public async Task Список_инструментов_содержит_схемы_и_описания()
    {
        var responses = await ExchangeAsync(InitializeKnown, Initialized, """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");

        var tools = Result(responses, 2)["tools"]!.AsArray();
        Assert.Equal(10, tools.Count);

        var names = tools.Select(tool => Text(tool!["name"])).ToList();
        Assert.Contains("status", names);
        Assert.Contains("open", names);
        Assert.Contains("search", names);
        Assert.Contains("code", names);
        Assert.Contains("metadata", names);
        Assert.Contains("check", names);
        Assert.Contains("reload", names);

        foreach (var tool in tools)
        {
            Assert.False(string.IsNullOrWhiteSpace(Text(tool!["description"])));
            Assert.Equal("object", Text(tool["inputSchema"]!["type"]));
            Assert.NotNull(tool["inputSchema"]!["properties"]);
        }
    }

    [Fact]
    public async Task Поиск_находит_процедуру_а_code_читает_её_текст()
    {
        var search = await ExchangeAsync(
            InitializeKnown,
            ToolCall(2, "search", """{"query":"МояПроцедура"}"""));

        var results = Json(ContentText(search, 2))["results"]!.AsArray();
        var routine = results
            .Select(item => Text(item!["id"]))
            .Single(id => id.EndsWith("#МояПроцедура", StringComparison.Ordinal));

        var code = await ExchangeAsync(
            InitializeKnown,
            ToolCall(2, "code", $$"""{"id":"{{routine}}"}"""));

        var text = ContentText(code, 2);
        Assert.Contains("Процедура МояПроцедура", text, StringComparison.Ordinal);
        Assert.Contains("ДругаяПроцедура();", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Окружение_процедуры_показывает_вызовы()
    {
        var responses = await ExchangeAsync(
            InitializeKnown,
            ToolCall(
                2,
                "neighbors",
                $$"""{"id":"routine:module:{{SampleDump.CommonModuleBslPath}}#МояПроцедура","edgeKinds":["Calls"],"direction":"out"}"""));

        var payload = Json(ContentText(responses, 2));
        var targets = payload["edges"]!.AsArray().Select(edge => Text(edge!["to"])).ToList();

        Assert.Contains(targets, target => target.Contains("ЗагрузитьДанные", StringComparison.Ordinal));
        Assert.Contains(targets, target => target.EndsWith("#ДругаяПроцедура", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Метаданные_возвращают_реквизиты_и_формы()
    {
        var responses = await ExchangeAsync(
            InitializeKnown,
            ToolCall(2, "metadata", """{"id":"Catalog.Товары"}"""));

        var payload = Json(ContentText(responses, 2));
        Assert.Equal("Catalog", Text(payload["kind"]));
        Assert.Equal("Номенклатура", Text(payload["synonym"]));

        var attribute = payload["children"]!["Attribute"]!.AsArray();
        var names = attribute.Select(item => Text(item!["name"])).ToList();
        Assert.Contains("Артикул", names);
        Assert.Contains("Единица", names);

        var unit = attribute.Single(item => Text(item!["name"]) == "Единица");
        Assert.Contains("Catalog.ЕдиницыИзмерения", unit!["types"]!.AsArray().Select(Text));
        Assert.Contains("ФормаЭлемента", payload["children"]!["Form"]!.AsArray().Select(item => Text(item!["name"])));
    }

    [Fact]
    public async Task Проверка_модуля_перечисляет_процедуры()
    {
        var responses = await ExchangeAsync(
            InitializeKnown,
            ToolCall(2, "check", $$"""{"path":"{{SampleDump.CommonModuleBslPath}}"}"""));

        var payload = Json(ContentText(responses, 2));
        var routines = payload["routines"]!.AsArray().Select(item => Text(item!["name"])).ToList();

        Assert.Contains("МояПроцедура", routines);
        Assert.Contains("ДругаяПроцедура", routines);
        Assert.Empty(payload["diagnostics"]!.AsArray());
        Assert.Equal(3, payload["callsCount"]!.GetValue<int>());
    }

    [Fact]
    public async Task Состояние_сообщает_о_готовом_разборе()
    {
        var session = Session();
        await ExchangeAsync(session, InitializeKnown, ToolCall(2, "search", """{"query":"Товары","limit":1}"""));

        var responses = await ExchangeAsync(session, ToolCall(3, "status", "{}"));

        var payload = Json(ContentText(responses, 3));
        Assert.Equal("готов", Text(payload["state"]));
        Assert.False(payload["running"]!.GetValue<bool>());
        Assert.True(payload["statistics"]!["metadataObjects"]!.GetValue<int>() > 0);
    }

    [Fact]
    public async Task Вызов_без_обязательного_аргумента_помечается_ошибкой()
    {
        var responses = await ExchangeAsync(
            InitializeKnown,
            ToolCall(2, "search", "{}"));

        var response = responses.Single(item => Text(item["id"]) == "2");
        Assert.True(response["result"]!["isError"]!.GetValue<bool>());
        Assert.Contains("query", ContentText(responses, 2), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Неизвестный_инструмент_и_метод_дают_понятные_ошибки()
    {
        var responses = await ExchangeAsync(
            InitializeKnown,
            ToolCall(2, "не-существует", "{}"),
            """{"jsonrpc":"2.0","id":3,"method":"no/such"}""");

        Assert.True(responses.Single(item => Text(item["id"]) == "2")["result"]!["isError"]!.GetValue<bool>());
        Assert.Equal(-32601, responses.Single(item => Text(item["id"]) == "3")["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public async Task Битый_JSON_даёт_ошибку_разбора()
    {
        var responses = await ExchangeAsync("{");

        Assert.Single(responses);
        Assert.Equal(-32700, responses[0]["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public async Task Отмена_вызова_не_ломает_сервер()
    {
        var responses = await ExchangeAsync(
            InitializeKnown,
            """{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":99,"reason":"тест"}}""",
            """{"jsonrpc":"2.0","id":5,"method":"ping"}""");

        Assert.Empty(Result(responses, 5));
    }

    [Fact]
    public async Task Без_открытой_выгрузки_инструменты_подсказывают_open()
    {
        var session = new AnalysisSession(new AnalysisRequest { DumpPath = string.Empty });
        var responses = await ExchangeAsync(
            session,
            ToolCall(2, "status", "{}"),
            ToolCall(3, "search", """{"query":"Товары"}"""));

        var status = Json(ContentText(responses, 2));
        Assert.Equal("выгрузка не открыта", Text(status["state"]));
        Assert.False(status["dumpOpen"]!.GetValue<bool>());
        Assert.Contains("open", Text(status["hint"]), StringComparison.Ordinal);

        var search = responses.Single(item => Text(item["id"]) == "3");
        Assert.True(search["result"]!["isError"]!.GetValue<bool>());
        Assert.Contains("open", ContentText(responses, 3), StringComparison.Ordinal);
    }

    private static async Task<IReadOnlyList<JsonObject>> ExchangeAsync(params string[] messages) =>
        await ExchangeAsync(Session(), messages);

    private static async Task<IReadOnlyList<JsonObject>> ExchangeAsync(AnalysisSession session, params string[] messages)
    {
        var output = new StringWriter();
        var log = new StringWriter();
        var server = new McpServer(new ToolCatalog(session), new StringReader(string.Join('\n', messages) + "\n"), output, log);

        await server.RunAsync(CancellationToken.None);

        return
        [
            .. output
                .ToString()
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => JsonNode.Parse(line)!.AsObject()),
        ];
    }

    private static AnalysisSession Session() =>
        new(new AnalysisRequest { DumpPath = "<память>" }, SampleDump.Create());

    private static string ToolCall(int id, string name, string arguments) => new JsonObject
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["method"] = "tools/call",
        ["params"] = new JsonObject
        {
            ["name"] = name,
            ["arguments"] = JsonNode.Parse(arguments),
        },
    }.ToJsonString();

    private static JsonObject Result(IReadOnlyList<JsonObject> responses, int id) =>
        responses.Single(item => Text(item["id"]) == id.ToString(System.Globalization.CultureInfo.InvariantCulture))["result"]!.AsObject();

    private static string ContentText(IReadOnlyList<JsonObject> responses, int id) =>
        Text(Result(responses, id)["content"]![0]!["text"]);

    private static JsonObject Json(string text) => JsonNode.Parse(text)!.AsObject();

    /// <summary>Текст значения JSON: строки как есть, числа и логические — в текстовом виде.</summary>
    private static string Text(JsonNode? node) => node switch
    {
        null => string.Empty,
        JsonValue value when value.TryGetValue<string>(out var text) => text,
        JsonValue value when value.TryGetValue<int>(out var number) => number.ToString(System.Globalization.CultureInfo.InvariantCulture),
        JsonValue value when value.TryGetValue<bool>(out var flag) => flag ? "true" : "false",
        _ => node.ToJsonString(),
    };
}
