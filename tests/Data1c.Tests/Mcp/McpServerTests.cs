using System.Text.Json.Nodes;
using Data1c.Core.Dump;
using Data1c.FileSystem;
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
        Assert.Equal(11, tools.Count);

        var names = tools.Select(tool => Text(tool!["name"])).ToList();
        Assert.Contains("status", names);
        Assert.Contains("open", names);
        Assert.Contains("search", names);
        Assert.Contains("grep", names);
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

        var nodes = payload["children"]!.AsArray().Select(node => node!).ToList();
        var attribute = nodes.Where(node => Text(node["kind"]) == "Attribute").ToList();
        var names = attribute.Select(node => Text(node["name"])).ToList();
        Assert.Contains("Артикул", names);
        Assert.Contains("Единица", names);

        var unit = attribute.Single(node => Text(node["name"]) == "Единица");
        Assert.Contains("Catalog.ЕдиницыИзмерения", unit["types"]!.AsArray().Select(Text));
        Assert.Contains(nodes, node => Text(node["kind"]) == "Form" && Text(node["name"]) == "ФормаЭлемента");
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
        var session = new AnalysisSession(new AnalysisRequest());
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

    [Fact]
    public async Task Поиск_по_тексту_находит_строку_и_объект_владелец()
    {
        var responses = await ExchangeAsync(
            InitializeKnown,
            ToolCall(2, "grep", """{"pattern":"НайтиПоНаименованию"}"""));

        var payload = Json(ContentText(responses, 2));
        Assert.Equal(1, payload["matches"]!.GetValue<int>());

        var hit = payload["hits"]!.AsArray()[0]!;
        Assert.Equal(SampleDump.CommonModuleBslPath, Text(hit["file"]));
        Assert.Equal(7, hit["line"]!.GetValue<int>());
        Assert.Equal("CommonModule.ОбщегоНазначения", Text(hit["owner"]));
        Assert.Equal("CommonModule.ОбщегоНазначения", Text(payload["byOwner"]!.AsArray()[0]!["owner"]));
    }

    [Fact]
    public async Task Поиск_по_тексту_уважает_регистр_расширения_и_пути()
    {
        var sensitive = await ExchangeAsync(
            InitializeKnown,
            ToolCall(2, "grep", """{"pattern":"сообщить","ignoreCase":false}"""));
        Assert.Equal(0, Json(ContentText(sensitive, 2))["matches"]!.GetValue<int>());

        var insensitive = await ExchangeAsync(
            InitializeKnown,
            ToolCall(2, "grep", """{"pattern":"сообщить"}"""));
        Assert.True(Json(ContentText(insensitive, 2))["matches"]!.GetValue<int>() > 0);

        var xmlOnly = await ExchangeAsync(
            InitializeKnown,
            ToolCall(2, "grep", """{"pattern":"Товары","extensions":[".xml"],"limit":10}"""));
        var xmlPayload = Json(ContentText(xmlOnly, 2));
        Assert.True(xmlPayload["matches"]!.GetValue<int>() > 0);
        Assert.All(xmlPayload["hits"]!.AsArray(), hit => Assert.EndsWith(".xml", Text(hit!["file"]), StringComparison.Ordinal));

        var filtered = await ExchangeAsync(
            InitializeKnown,
            ToolCall(2, "grep", """{"pattern":"Товары","paths":["Reports/"]}"""));
        Assert.Equal(0, Json(ContentText(filtered, 2))["matches"]!.GetValue<int>());
    }

    [Fact]
    public async Task Поиск_по_тексту_отдаёт_окружение_и_ограничивает_выдачу()
    {
        var withContext = await ExchangeAsync(
            InitializeKnown,
            ToolCall(2, "grep", """{"pattern":"НайтиПоНаименованию","context":1}"""));

        var hit = Json(ContentText(withContext, 2))["hits"]!.AsArray()[0]!;
        var context = string.Join('\n', hit["context"]!.AsArray().Select(Text));
        Assert.Contains("Процедура ДругаяПроцедура", context, StringComparison.Ordinal);

        var limited = await ExchangeAsync(
            InitializeKnown,
            ToolCall(2, "grep", """{"pattern":"Процедура","limit":2}"""));
        var payload = Json(ContentText(limited, 2));
        Assert.Equal(2, payload["matches"]!.GetValue<int>());
        Assert.True(payload["truncated"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Поиск_по_тексту_ищет_в_базе_и_расширении_и_помечает_источник()
    {
        var extension = new InMemoryDumpSource("расширение");
        extension.AddText(
            SampleDump.CommonModuleBslPath,
            "Процедура МояПроцедура() Экспорт\n\tСправочники.Товары.НайтиПоНаименованию(\"Расширение\");\nКонецПроцедуры\n");

        var session = new AnalysisSession(
            new AnalysisRequest(),
            new CompositeDumpSource([SampleDump.Create(), extension]));

        var responses = await ExchangeAsync(
            session,
            InitializeKnown,
            ToolCall(2, "grep", """{"pattern":"НайтиПоНаименованию","extensions":[".bsl"]}"""));

        var payload = Json(ContentText(responses, 2));
        var hits = payload["hits"]!.AsArray();

        Assert.Equal(2, hits.Count);
        Assert.Contains(hits, hit => Text(hit!["source"]) == "тестовая выгрузка");
        Assert.Contains(hits, hit => Text(hit!["source"]) == "расширение");
        Assert.All(hits, hit => Assert.True(hit!["overridden"]!.GetValue<bool>()));
        Assert.All(hits, hit => Assert.Equal("CommonModule.ОбщегоНазначения", Text(hit!["owner"])));
        Assert.Contains("перекрыта", Text(payload["note"]), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Открытие_несуществующего_пути_не_ломает_текущую_выгрузку()
    {
        var session = Session();
        var failed = await ExchangeAsync(
            session,
            InitializeKnown,
            ToolCall(2, "open", """{"paths":["D:\\нет-такого-каталога-1c"]}"""));

        Assert.True(failed.Single(item => Text(item["id"]) == "2")["result"]!["isError"]!.GetValue<bool>());

        var still = await ExchangeAsync(session, ToolCall(3, "search", """{"query":"МояПроцедура"}"""));
        Assert.True(Json(ContentText(still, 3))["found"]!.GetValue<int>() > 0);
    }

    [Fact]
    public async Task Карточка_объекта_показывает_состав_табличной_части()
    {
        var responses = await ExchangeAsync(
            TabularSession(),
            ToolCall(2, "metadata", """{"id":"Document.Заказ"}"""));

        var payload = Json(ContentText(responses, 2));
        var section = payload["children"]!.AsArray()
            .Select(node => node!)
            .Single(node => Text(node["id"]) == "Document.Заказ/TabularSection.Строки");

        Assert.Equal("TabularSection", Text(section["kind"]));
        Assert.Equal("Строки", Text(section["name"]));

        var names = section["children"]!.AsArray().Select(child => Text(child!["name"])).ToList();
        Assert.Contains("Номенклатура", names);
        Assert.Contains("Количество", names);
    }

    [Fact]
    public async Task Карточка_табличной_части_открывается_по_вложенному_идентификатору()
    {
        var responses = await ExchangeAsync(
            TabularSession(),
            ToolCall(2, "metadata", """{"id":"Document.Заказ/TabularSection.Строки"}"""));

        var payload = Json(ContentText(responses, 2));
        Assert.Equal("TabularSection", Text(payload["kind"]));
        Assert.Equal("Document.Заказ", Text(payload["parent"]));
        Assert.Equal(2, payload["children"]!.AsArray().Count);
    }

    [Fact]
    public async Task Поиск_находит_реквизит_табличной_части()
    {
        var responses = await ExchangeAsync(
            TabularSession(),
            ToolCall(2, "search", """{"query":"Номенклатура"}"""));

        var payload = Json(ContentText(responses, 2));
        var nested = payload["nested"]!.AsArray();

        Assert.Equal(1, payload["nestedFound"]!.GetValue<int>());
        Assert.Equal("Document.Заказ/TabularSection.Строки/Attribute.Номенклатура", Text(nested[0]!["id"]));
        Assert.Equal("Document.Заказ", Text(nested[0]!["objectId"]));
        Assert.Equal("Document.Заказ/TabularSection.Строки", Text(nested[0]!["parent"]));
        Assert.Contains("Catalog.Номенклатура", nested[0]!["types"]!.AsArray().Select(Text));
    }

    [Fact]
    public async Task Поиск_с_фильтром_видов_оставляет_только_табличные_части()
    {
        var responses = await ExchangeAsync(
            TabularSession(),
            ToolCall(2, "search", """{"query":"Строки","metadataKinds":["TabularSection"]}"""));

        var nested = Json(ContentText(responses, 2))["nested"]!.AsArray();
        Assert.All(nested, hit => Assert.Equal("TabularSection", Text(hit!["kind"])));
        Assert.Contains(nested, hit => Text(hit!["id"]) == "Document.Заказ/TabularSection.Строки");
    }

    [Fact]
    public async Task Поиск_без_includeNested_не_заглядывает_внутрь_объектов()
    {
        var responses = await ExchangeAsync(
            TabularSession(),
            ToolCall(2, "search", """{"query":"Номенклатура","includeNested":false}"""));

        var payload = Json(ContentText(responses, 2));
        Assert.Equal(0, payload["nestedFound"]!.GetValue<int>());
        Assert.Empty(payload["nested"]!.AsArray());
    }

    [Fact]
    public async Task Владелец_совпадения_определяется_и_по_каталогу_объекта()
    {
        var responses = await ExchangeAsync(
            TabularSession(),
            ToolCall(2, "grep", """{"pattern":"dataPath","extensions":[".xml"]}"""));

        var payload = Json(ContentText(responses, 2));
        var hit = payload["hits"]!.AsArray()[0]!;

        Assert.Equal("Documents/Заказ/Templates/ПечатнаяФорма/Ext/Template.xml", Text(hit["file"]));
        Assert.Equal("Document.Заказ", Text(hit["owner"]));
        Assert.Equal("Document.Заказ", Text(payload["byOwner"]!.AsArray()[0]!["owner"]));
    }

    [Fact]
    public async Task Поиск_по_тексту_без_ожидания_разбора_отдаёт_совпадения_без_владельцев()
    {
        var responses = await ExchangeAsync(
            Session(),
            ToolCall(2, "grep", """{"pattern":"НайтиПоНаименованию","waitMs":0}"""));

        var payload = Json(ContentText(responses, 2));
        Assert.Equal(1, payload["matches"]!.GetValue<int>());
        Assert.False(payload["ownersResolved"]!.GetValue<bool>());
        Assert.Null(payload["hits"]!.AsArray()[0]!["owner"]);
        Assert.Contains("Разбор выгрузки ещё идёт", Text(payload["note"]), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Поиск_по_смыслу_находит_процедуру_по_имени_параметра()
    {
        // Смысловой поиск по термам работает в режиме индекса: выгрузка раскладывается на диск,
        // сервер собирает индекс, и уже по нему ищется процедура, у которой «Отказ» — параметр.
        var root = TestDump.Materialize();
        try
        {
            using var session = new AnalysisSession(new AnalysisRequest { DumpPaths = [root] });
            await session.QueryAsync(CancellationToken.None);

            var responses = await ExchangeAsync(
                session,
                InitializeKnown,
                ToolCall(2, "search", """{"query":"Отказ"}"""));

            var payload = Json(ContentText(responses, 2));
            Assert.True(payload["found"]!.GetValue<int>() > 0);
            Assert.Contains("ПриОткрытии", ContentText(responses, 2), StringComparison.Ordinal);
        }
        finally
        {
            TestDump.Remove(root);
        }
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

    /// <summary>Выгрузка с документом, у которого есть табличная часть с собственными реквизитами.</summary>
    private static AnalysisSession TabularSession()
    {
        var source = new InMemoryDumpSource("выгрузка с табличной частью");
        source.AddText("Configuration.xml", TabularConfigurationXml);
        source.AddText("Documents/Заказ.xml", TabularDocumentXml);

        // Схема компоновки макета: своего объекта в модели у неё нет, владелец — сам документ.
        source.AddText(
            "Documents/Заказ/Templates/ПечатнаяФорма/Ext/Template.xml",
            "<DataCompositionSchema><dataPath>Номенклатура</dataPath></DataCompositionSchema>");
        return new AnalysisSession(new AnalysisRequest(), source);
    }

    private const string TabularConfigurationXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <MetaDataObject xmlns="http://v8.1c.ru/8.3/MDClasses" xmlns:v8="http://v8.1c.ru/8.1/data/core" xmlns:cfg="http://v8.1c.ru/8.1/data/enterprise/current-config" version="2.20">
            <Configuration uuid="0f0f0f0f-0000-0000-0000-000000000002">
                <Properties>
                    <Name>КонфигурацияСТабличнойЧастью</Name>
                </Properties>
                <ChildObjects>
                    <Document>Заказ</Document>
                </ChildObjects>
            </Configuration>
        </MetaDataObject>
        """;

    private const string TabularDocumentXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <MetaDataObject xmlns="http://v8.1c.ru/8.3/MDClasses" xmlns:v8="http://v8.1c.ru/8.1/data/core" xmlns:cfg="http://v8.1c.ru/8.1/data/enterprise/current-config" version="2.20">
            <Document uuid="11111111-1111-1111-1111-111111111111">
                <Properties>
                    <Name>Заказ</Name>
                    <Synonym>
                        <v8:item>
                            <v8:lang>ru</v8:lang>
                            <v8:content>Заказ покупателя</v8:content>
                        </v8:item>
                    </Synonym>
                </Properties>
                <ChildObjects>
                    <Attribute uuid="22222222-2222-2222-2222-222222222222">
                        <Properties>
                            <Name>Сумма</Name>
                            <Type>
                                <v8:Type>xs:decimal</v8:Type>
                            </Type>
                        </Properties>
                    </Attribute>
                    <TabularSection uuid="33333333-3333-3333-3333-333333333333">
                        <Properties>
                            <Name>Строки</Name>
                        </Properties>
                        <ChildObjects>
                            <Attribute uuid="44444444-4444-4444-4444-444444444444">
                                <Properties>
                                    <Name>Номенклатура</Name>
                                    <Type>
                                        <v8:Type>cfg:CatalogRef.Номенклатура</v8:Type>
                                    </Type>
                                </Properties>
                            </Attribute>
                            <Attribute uuid="55555555-5555-5555-5555-555555555555">
                                <Properties>
                                    <Name>Количество</Name>
                                    <Type>
                                        <v8:Type>xs:decimal</v8:Type>
                                    </Type>
                                </Properties>
                            </Attribute>
                        </ChildObjects>
                    </TabularSection>
                </ChildObjects>
            </Document>
        </MetaDataObject>
        """;

    private static AnalysisSession Session() => new(new AnalysisRequest(), SampleDump.Create());

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
