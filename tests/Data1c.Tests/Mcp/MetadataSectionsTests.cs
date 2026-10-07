using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Data1c.Core.Analysis;
using Data1c.Core.Dump;
using Data1c.Mcp;
using Xunit;

namespace Data1c.Tests.Mcp;

/// <summary>
/// Проверки способов сузить ответ инструмента <c>metadata</c>: виды разделов, счётчики состава,
/// смещение по детям и признак обрезки по пределу длины. Своя выгрузка в памяти — общие
/// <see cref="SampleDump"/> и <see cref="FormSampleDump"/> не меняются.
/// </summary>
public sealed class MetadataSectionsTests
{
    /// <summary>Сколько реквизитов у крупного документа: ответ не помещается в предел длины.</summary>
    private const int BigAttributeCount = 120;

    /// <summary>Сколько табличных частей у крупного документа.</summary>
    private const int BigTabularCount = 10;

    /// <summary>Сколько реквизитов внутри каждой табличной части.</summary>
    private const int BigTabularAttributeCount = 5;

    private const string InitializeKnown =
        """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","clientInfo":{"name":"тест","version":"1"}}}""";

    [Fact]
    public async Task Счётчики_summary_описывают_состав_без_деревьев()
    {
        var payload = await CallAsync(BigSession(), """{"id":"Document.Большой","summary":true}""");

        // Реквизиты табличных частей тоже входят в счётчики: у документа 120 своих и 10 частей по 5.
        var counts = payload["sectionCounts"]!.AsArray();
        Assert.Equal(BigAttributeCount + (BigTabularCount * BigTabularAttributeCount), Count(counts, "Attribute"));
        Assert.Equal(BigTabularCount, Count(counts, "TabularSection"));
        Assert.Equal(BigAttributeCount + (BigTabularCount * BigTabularAttributeCount) + BigTabularCount, payload["total"]!.GetValue<int>());

        // Дерева и примеров нет, а счётчики обращений остаются: по ним агент решает, куда спускаться.
        Assert.Null(payload["children"]);
        Assert.Null(payload["references"]);
        Assert.NotNull(payload["usages"]);
        Assert.Null(payload["usages"]!.AsObject()["items"]);

        // Числа полные: ни предел ветки, ни глубина их не подрезали.
        Assert.Null(payload["partial"]);
        Assert.Equal("Большой", Text(payload["name"]));
    }

    [Fact]
    public async Task Sections_оставляет_только_запрошенные_виды_разделов()
    {
        var payload = await CallAsync(SmallSession(), """{"id":"Document.Заказ","sections":["form","COMMAND"]}""");

        var kinds = payload["children"]!.AsArray().Select(child => Text(child!["kind"])).Distinct().ToList();
        Assert.Equal(["Command", "Form"], kinds.Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(2, payload["children"]!.AsArray().Count);
    }

    [Fact]
    public async Task Sections_действует_на_всех_уровнях_дерева()
    {
        // Запрошены только табличные части: реквизиты внутри них тоже не показываются.
        var onlySections = await CallAsync(SmallSession(), """{"id":"Document.Заказ","sections":["TabularSection"]}""");
        var section = Assert.Single(onlySections["children"]!.AsArray())!;
        Assert.Equal("TabularSection", Text(section["kind"]));
        Assert.Null(section["children"]);
        Assert.Null(section["childrenCount"]);

        // Запрошены и табличные части, и реквизиты: состав части остаётся на месте.
        var withAttributes = await CallAsync(
            SmallSession(),
            """{"id":"Document.Заказ","sections":["TabularSection","Attribute"]}""");
        var children = withAttributes["children"]!.AsArray();
        var nested = children.Single(child => Text(child!["kind"]) == "TabularSection")!;
        Assert.Equal(2, nested["children"]!.AsArray().Count);
        Assert.Equal(3, children.Count(child => Text(child!["kind"]) == "Attribute"));
    }

    [Fact]
    public async Task Offset_пропускает_заданное_число_детей()
    {
        var all = await CallAsync(SmallSession(), """{"id":"Document.Заказ","sections":["Attribute"]}""");
        Assert.Equal(3, all["children"]!.AsArray().Count);

        var tail = await CallAsync(SmallSession(), """{"id":"Document.Заказ","sections":["Attribute"],"offset":2}""");
        var names = tail["children"]!.AsArray().Select(child => Text(child!["name"])).ToList();
        Assert.Equal(["РеквизитТретий"], names);

        // Пропущенные смещением дети тоже не показаны: их число видно в счётчике.
        Assert.Equal(2, tail["childrenTruncated"]!.GetValue<int>());
    }

    [Fact]
    public async Task Sections_и_summary_вместе_считают_только_запрошенные_виды()
    {
        var payload = await CallAsync(
            BigSession(),
            """{"id":"Document.Большой","sections":["TabularSection"],"summary":true}""");

        var counts = payload["sectionCounts"]!.AsArray();
        var only = Assert.Single(counts)!;
        Assert.Equal("TabularSection", Text(only["kind"]));
        Assert.Equal(BigTabularCount, only["count"]!.GetValue<int>());
        Assert.Equal(BigTabularCount, payload["total"]!.GetValue<int>());
    }

    [Fact]
    public async Task Неизвестный_вид_раздела_отвергается_со_списком_допустимых()
    {
        var responses = await ExchangeAsync(
            SmallSession(),
            InitializeKnown,
            ToolCall(2, "metadata", """{"id":"Document.Заказ","sections":["Module"]}"""));

        var result = Result(responses, 2);
        Assert.True(result["isError"]!.GetValue<bool>());

        var message = Text(result["content"]![0]!["text"]);
        Assert.Contains("Недопустимое значение sections", message, StringComparison.Ordinal);
        Assert.Contains("Module", message, StringComparison.Ordinal);
        foreach (var section in new[] { "Attribute", "TabularSection", "Form", "Command", "Template", "Dimension", "Resource", "EnumValue", "Parameter" })
        {
            Assert.Contains(section, message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Крупный_объект_помечается_обрезкой_и_подсказкой_о_своих_аргументах()
    {
        var responses = await ExchangeAsync(
            BigSession(),
            InitializeKnown,
            ToolCall(2, "metadata", """{"id":"Document.Большой"}"""));

        var text = ContentText(responses, 2);
        Assert.True(text.Length > Render.MaxChars);

        var payload = Json(text);
        Assert.True(payload["truncated"]!.GetValue<bool>());
        Assert.Equal(Render.MaxChars, payload["limit"]!.GetValue<int>());
        Assert.False(string.IsNullOrWhiteSpace(Text(payload["head"])));

        // Подсказка называет аргументы metadata, а не limit с диапазоном строк.
        var message = Text(payload["message"]);
        foreach (var argument in new[] { "sections", "summary", "depth", "maxChildren", "offset", "usages" })
        {
            Assert.Contains(argument, message, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("диапазон строк", message, StringComparison.Ordinal);
        Assert.DoesNotContain("limit", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Небольшой_объект_без_обрезки_флага_обрезки_не_несёт()
    {
        var payload = await CallAsync(SmallSession(), """{"id":"Document.Заказ"}""");

        Assert.Null(payload["truncated"]);
        Assert.Equal("Заказ", Text(payload["name"]));
        Assert.NotEmpty(payload["children"]!.AsArray());
    }

    [Fact]
    public async Task Счётчики_формы_остаются_в_режиме_summary()
    {
        var session = new AnalysisSession(new AnalysisRequest(), FormSampleDump.Create());
        var payload = await CallAsync(session, $$"""{"id":"{{FormSampleDump.FormId}}","summary":true}""");

        var form = payload["form"]!;
        Assert.Equal(2, form["attributesCount"]!.GetValue<int>());
        Assert.Equal(1, form["commandsCount"]!.GetValue<int>());
        Assert.Equal(3, form["handlersCount"]!.GetValue<int>());

        // Детали формы — не счётчики: списков в режиме summary нет.
        Assert.Null(form["attributes"]);
        Assert.Null(form["handlers"]);
    }

    /// <summary>Выгрузка с документом: три реквизита, табличная часть, форма, команда и макет.</summary>
    private static AnalysisSession SmallSession()
    {
        var source = new InMemoryDumpSource("выгрузка с документом");
        source.AddText("Configuration.xml", SmallConfigurationXml);
        source.AddText("Documents/Заказ.xml", SmallDocumentXml);
        source.AddText("Documents/Заказ/Forms/ФормаДокумента.xml", FormMetadataXml);
        return new AnalysisSession(new AnalysisRequest(), source);
    }

    /// <summary>Выгрузка с крупным документом: сотня с лишним реквизитов и десяток табличных частей.</summary>
    private static AnalysisSession BigSession()
    {
        var source = new InMemoryDumpSource("выгрузка с крупным документом");
        source.AddText("Configuration.xml", BigConfigurationXml);
        source.AddText("Documents/Большой.xml", BigDocumentXml());
        return new AnalysisSession(new AnalysisRequest(), source);
    }

    private static string BigDocumentXml()
    {
        var text = new StringBuilder();
        text.Append(
            """
            <?xml version="1.0" encoding="UTF-8"?>
            <MetaDataObject xmlns="http://v8.1c.ru/8.3/MDClasses" xmlns:v8="http://v8.1c.ru/8.1/data/core" xmlns:cfg="http://v8.1c.ru/8.1/data/enterprise/current-config" version="2.20">
                <Document uuid="99999999-9999-9999-9999-999999999999">
                    <Properties>
                        <Name>Большой</Name>
                    </Properties>
                    <ChildObjects>
            """);

        for (var index = 1; index <= BigAttributeCount; index++)
        {
            AppendAttribute(text, "РеквизитБольшогоОбъекта" + Number(index), "xs:string");
        }

        for (var section = 1; section <= BigTabularCount; section++)
        {
            text.Append(CultureInfo.InvariantCulture, $"        <TabularSection>\n");
            text.Append(CultureInfo.InvariantCulture, $"            <Properties><Name>Строки{section:D2}</Name></Properties>\n");
            text.Append("            <ChildObjects>\n");
            for (var index = 1; index <= BigTabularAttributeCount; index++)
            {
                AppendAttribute(text, "Колонка" + Number(index) + "Строки" + Number(section), "xs:decimal");
            }

            text.Append("            </ChildObjects>\n");
            text.Append("        </TabularSection>\n");
        }

        text.Append(
            """
                    </ChildObjects>
                </Document>
            </MetaDataObject>
            """);
        return text.ToString();
    }

    private static void AppendAttribute(StringBuilder text, string name, string type)
    {
        text.Append("        <Attribute>\n");
        text.Append(CultureInfo.InvariantCulture, $"            <Properties><Name>{name}</Name><Type><v8:Type>{type}</v8:Type></Type></Properties>\n");
        text.Append("        </Attribute>\n");
    }

    private static string Number(int value) => value.ToString("D3", CultureInfo.InvariantCulture);

    private static int Count(JsonArray counts, string kind) =>
        counts.Select(node => node!).Single(node => Text(node["kind"]) == kind)["count"]!.GetValue<int>();

    private static async Task<JsonObject> CallAsync(AnalysisSession session, string arguments)
    {
        var responses = await ExchangeAsync(session, InitializeKnown, ToolCall(2, "metadata", arguments));
        return Json(ContentText(responses, 2));
    }

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
        responses.Single(item => Text(item["id"]) == id.ToString(CultureInfo.InvariantCulture))["result"]!.AsObject();

    private static string ContentText(IReadOnlyList<JsonObject> responses, int id) =>
        Text(Result(responses, id)["content"]![0]!["text"]);

    private static JsonObject Json(string text) => JsonNode.Parse(text)!.AsObject();

    /// <summary>Текст значения JSON: строки как есть, числа и логические — в текстовом виде.</summary>
    private static string Text(JsonNode? node) => node switch
    {
        null => string.Empty,
        JsonValue value when value.TryGetValue<string>(out var text) => text,
        JsonValue value when value.TryGetValue<int>(out var number) => number.ToString(CultureInfo.InvariantCulture),
        JsonValue value when value.TryGetValue<bool>(out var flag) => flag ? "true" : "false",
        _ => node.ToJsonString(),
    };

    private const string SmallConfigurationXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <MetaDataObject xmlns="http://v8.1c.ru/8.3/MDClasses" xmlns:v8="http://v8.1c.ru/8.1/data/core" version="2.20">
            <Configuration uuid="0f0f0f0f-0000-0000-0000-000000000003">
                <Properties>
                    <Name>КонфигурацияСЗаказом</Name>
                </Properties>
                <ChildObjects>
                    <Document>Заказ</Document>
                </ChildObjects>
            </Configuration>
        </MetaDataObject>
        """;

    private const string BigConfigurationXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <MetaDataObject xmlns="http://v8.1c.ru/8.3/MDClasses" xmlns:v8="http://v8.1c.ru/8.1/data/core" version="2.20">
            <Configuration uuid="0f0f0f0f-0000-0000-0000-000000000004">
                <Properties>
                    <Name>КонфигурацияСКрупнымДокументом</Name>
                </Properties>
                <ChildObjects>
                    <Document>Большой</Document>
                </ChildObjects>
            </Configuration>
        </MetaDataObject>
        """;

    private const string SmallDocumentXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <MetaDataObject xmlns="http://v8.1c.ru/8.3/MDClasses" xmlns:v8="http://v8.1c.ru/8.1/data/core" xmlns:cfg="http://v8.1c.ru/8.1/data/enterprise/current-config" version="2.20">
            <Document uuid="11111111-1111-1111-1111-111111111112">
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
                    <Attribute uuid="22222222-2222-2222-2222-222222222221">
                        <Properties>
                            <Name>РеквизитПервый</Name>
                            <Type>
                                <v8:Type>xs:string</v8:Type>
                            </Type>
                        </Properties>
                    </Attribute>
                    <Attribute uuid="22222222-2222-2222-2222-222222222222">
                        <Properties>
                            <Name>РеквизитВторой</Name>
                            <Type>
                                <v8:Type>xs:string</v8:Type>
                            </Type>
                        </Properties>
                    </Attribute>
                    <Attribute uuid="22222222-2222-2222-2222-222222222223">
                        <Properties>
                            <Name>РеквизитТретий</Name>
                            <Type>
                                <v8:Type>xs:decimal</v8:Type>
                            </Type>
                        </Properties>
                    </Attribute>
                    <TabularSection uuid="33333333-3333-3333-3333-333333333331">
                        <Properties>
                            <Name>Строки</Name>
                        </Properties>
                        <ChildObjects>
                            <Attribute uuid="44444444-4444-4444-4444-444444444441">
                                <Properties>
                                    <Name>Номенклатура</Name>
                                    <Type>
                                        <v8:Type>cfg:CatalogRef.Номенклатура</v8:Type>
                                    </Type>
                                </Properties>
                            </Attribute>
                            <Attribute uuid="44444444-4444-4444-4444-444444444442">
                                <Properties>
                                    <Name>Количество</Name>
                                    <Type>
                                        <v8:Type>xs:decimal</v8:Type>
                                    </Type>
                                </Properties>
                            </Attribute>
                        </ChildObjects>
                    </TabularSection>
                    <Form>ФормаДокумента</Form>
                    <Command uuid="55555555-5555-5555-5555-555555555551">
                        <Properties>
                            <Name>Печать</Name>
                        </Properties>
                    </Command>
                    <Template uuid="66666666-6666-6666-6666-666666666661">
                        <Properties>
                            <Name>МакетПечати</Name>
                        </Properties>
                    </Template>
                </ChildObjects>
            </Document>
        </MetaDataObject>
        """;

    private const string FormMetadataXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <MetaDataObject xmlns="http://v8.1c.ru/8.3/MDClasses" xmlns:v8="http://v8.1c.ru/8.1/data/core" version="2.20">
            <Form uuid="77777777-7777-7777-7777-777777777771">
                <Properties>
                    <Name>ФормаДокумента</Name>
                    <FormType>Managed</FormType>
                </Properties>
            </Form>
        </MetaDataObject>
        """;
}
