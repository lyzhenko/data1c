using System.Text.Json.Nodes;
using Data1c.Core.Analysis;
using Data1c.Core.Dump;
using Data1c.Mcp;
using Xunit;

namespace Data1c.Tests.Mcp;

/// <summary>
/// Обращения к объекту метаданных в карточках инструментов: раздел <c>usages</c> у metadata со
/// счётчиками по контекстам и примерами строк, короткая сводка у node и счётчики по контекстам
/// в status. Проверяется и разбор в памяти, и индекс на диске.
/// </summary>
public sealed class MetadataUsagesTests
{
    private const string ModulePath = "CommonModules/РаботаСНоменклатурой/Ext/Module.bsl";
    private const string RoutineId = "routine:module:" + ModulePath + "#ЗагрузитьТовары";

    [Fact]
    public async Task Карточка_объекта_показывает_обращения_из_кода_и_запросов()
    {
        using var session = new AnalysisSession(new AnalysisRequest(), CreateDump());

        var usages = (await CallAsync(session, "metadata", """{"id":"Catalog.Номенклатура"}"""))["usages"]!.AsObject();

        // Объект читают трижды: строка кода, строка запроса и тип реквизита другого справочника.
        Assert.Equal(3, Count(usages["total"]));
        Assert.Equal(1, Contexts(usages)["code"]);
        Assert.Equal(1, Contexts(usages)["query"]);
        Assert.Equal(1, Contexts(usages)["type"]);

        // Обращения из кода и запросов — вид связи UsesMetadata, тип реквизита — References.
        var kinds = usages["byKind"]!.AsArray()
            .ToDictionary(item => Text(item!["kind"]), item => Count(item!["count"]));
        Assert.Equal(2, kinds["UsesMetadata"]);
        Assert.Equal(1, kinds["References"]);

        // Подписи контекстов человеческие: агент читает их, а не коды.
        var code = usages["byContext"]!.AsArray().Single(item => Text(item!["context"]) == "code")!;
        Assert.Equal("в коде", Text(code["label"]));

        // Модуль-читатель один: процедура читает объект дважды, первое место — строка 2.
        var reader = usages["readers"]!.AsArray().Single(item => Text(item!["source"]) == RoutineId)!;
        Assert.Equal(2, Count(reader["count"]));
        Assert.Equal(2, Count(reader["line"]));
        Assert.Equal("code", Text(reader["context"]));
        Assert.StartsWith("Справочники.Номенклатура", Text(reader["detail"]), StringComparison.Ordinal);

        // Конкретные обращения несут строку, контекст и фрагмент.
        var items = usages["items"]!.AsArray();
        Assert.Equal(3, items.Count);
        Assert.Equal(3, Count(usages["shown"]));

        var fromCode = items.Single(item => Text(item!["context"]) == "code")!;
        Assert.Equal(2, Count(fromCode["line"]));
        Assert.Equal(ModulePath, Text(fromCode["file"]));

        var fromQuery = items.Single(item => Text(item!["context"]) == "query")!;
        Assert.Equal(3, Count(fromQuery["line"]));
        Assert.Equal("ИЗ Справочник.Номенклатура КАК Т", Text(fromQuery["detail"]));
    }

    [Fact]
    public async Task Лимит_списка_обращений_не_режет_счётчики()
    {
        using var session = new AnalysisSession(new AnalysisRequest(), CreateDump());

        var usages = (await CallAsync(session, "metadata", """{"id":"Catalog.Номенклатура","usages":1}"""))["usages"]!.AsObject();

        Assert.Equal(3, Count(usages["total"]));
        Assert.Equal(1, Count(usages["shown"]));
        Assert.Single(usages["items"]!.AsArray());

        // Разбивка по контекстам и читатели считаются по всем обращениям, а не по показанным строкам.
        Assert.Equal(3, usages["byContext"]!.AsArray().Count);
        Assert.Equal(2, usages["readers"]!.AsArray().Count);
        Assert.Equal(2, Count(usages["readers"]!.AsArray().Single(item => Text(item!["source"]) == RoutineId)!["count"]));
    }

    [Fact]
    public async Task Объект_без_обращений_отвечает_пустым_разделом()
    {
        using var session = new AnalysisSession(new AnalysisRequest(), CreateDump());

        var payload = await CallAsync(session, "metadata", """{"id":"Catalog.Пустой"}""");
        var usages = payload["usages"]!.AsObject();

        Assert.Equal(0, Count(usages["total"]));
        Assert.Equal(0, Count(usages["shown"]));
        Assert.Null(usages["byContext"]);
        Assert.Null(usages["readers"]);
        Assert.Null(usages["items"]);

        // Существующие поля карточки остаются на месте.
        Assert.Equal("Catalog", Text(payload["kind"]));
        Assert.Equal("Пустой", Text(payload["name"]));
    }

    [Fact]
    public async Task Карточка_узла_даёт_краткую_сводку_обращений_без_списка()
    {
        using var session = new AnalysisSession(new AnalysisRequest(), CreateDump());

        var payload = await CallAsync(session, "node", """{"id":"Catalog.Номенклатура"}""");
        var usages = payload["usages"]!.AsObject();

        Assert.Equal(3, Count(usages["total"]));
        Assert.Equal(1, Contexts(usages)["query"]);
        Assert.Contains("metadata", Text(usages["hint"]), StringComparison.Ordinal);

        // Полный список читателей и строк не дублируется: за ним идут в metadata.
        Assert.Null(usages["readers"]);
        Assert.Null(usages["items"]);
        Assert.NotNull(payload["incoming"]);

        // У узла-модуля сводки нет: обращения адресуются объектам метаданных.
        var module = await CallAsync(session, "node", $$"""{"id":"module:{{ModulePath}}"}""");
        Assert.Null(module["usages"]);
    }

    [Fact]
    public async Task Обращения_и_контексты_читаются_из_индекса()
    {
        using var fixture = new IndexFixture();

        var usages = (await CallAsync(fixture.Session, "metadata", """{"id":"Catalog.Номенклатура"}"""))["usages"]!.AsObject();

        // Индекс отвечает тем же, что разбор в памяти: счётчики и примеры сходятся.
        Assert.Equal(3, Count(usages["total"]));
        Assert.Equal(1, Contexts(usages)["code"]);
        Assert.Equal(1, Contexts(usages)["query"]);
        Assert.Equal(1, Contexts(usages)["type"]);

        var reader = usages["readers"]!.AsArray().Single(item => Text(item!["source"]) == RoutineId)!;
        Assert.Equal(2, Count(reader["count"]));
        Assert.Equal(ModulePath, Text(reader["file"]));

        var fromQuery = usages["items"]!.AsArray().Single(item => Text(item!["context"]) == "query")!;
        Assert.Equal(3, Count(fromQuery["line"]));
        Assert.Equal("ИЗ Справочник.Номенклатура КАК Т", Text(fromQuery["detail"]));
    }

    [Fact]
    public async Task Фильтр_по_контексту_оставляет_только_обращения_из_запросов()
    {
        using var session = new AnalysisSession(new AnalysisRequest(), CreateDump());

        var usages = (await CallAsync(
            session,
            "metadata",
            """{"id":"Catalog.Номенклатура","usageContext":"query"}"""))["usages"]!.AsObject();

        // Счётчики пересчитаны по фильтру: одно обращение, один контекст и один вид связи.
        Assert.Equal(1, Count(usages["total"]));
        Assert.Equal(new Dictionary<string, int> { ["query"] = 1 }, Contexts(usages));
        var kinds = usages["byKind"]!.AsArray()
            .ToDictionary(item => Text(item!["kind"]), item => Count(item!["count"]));
        Assert.Equal(new Dictionary<string, int> { ["UsesMetadata"] = 1 }, kinds);

        // Фильтр помечен в ответе: агент видит, что счётчики описывают только запросы.
        var filter = usages["filter"]!.AsObject();
        Assert.Equal("query", Text(filter["context"]));
        Assert.Equal("в запросах", Text(filter["label"]));
        Assert.Equal("UsesMetadata", Text(filter["kind"]));

        // Читатели и примеры — только из запросов: у процедуры одно обращение вместо двух.
        var reader = usages["readers"]!.AsArray().Single(item => Text(item!["source"]) == RoutineId)!;
        Assert.Equal(1, Count(reader["count"]));
        Assert.Equal("query", Text(reader["context"]));
        Assert.Equal(3, Count(reader["line"]));

        var item = Assert.Single(usages["items"]!.AsArray())!;
        Assert.Equal("query", Text(item["context"]));
        Assert.Equal("ИЗ Справочник.Номенклатура КАК Т", Text(item["detail"]));

        // Другой контекст того же объекта даёт другую строку того же читателя.
        var fromCode = (await CallAsync(
            session,
            "metadata",
            """{"id":"Catalog.Номенклатура","usageContext":"code"}"""))["usages"]!.AsObject();
        Assert.Equal(1, Count(fromCode["total"]));
        Assert.Equal(2, Count(fromCode["readers"]!.AsArray().Single(item => Text(item!["source"]) == RoutineId)!["line"]));
        Assert.StartsWith("Справочники.Номенклатура", Text(Assert.Single(fromCode["items"]!.AsArray())!["detail"]), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Фильтр_по_контексту_ссылок_оставляет_только_типы()
    {
        using var session = new AnalysisSession(new AnalysisRequest(), CreateDump());

        var usages = (await CallAsync(
            session,
            "metadata",
            """{"id":"Catalog.Номенклатура","usageContext":"type"}"""))["usages"]!.AsObject();

        Assert.Equal(1, Count(usages["total"]));
        Assert.Equal(new Dictionary<string, int> { ["type"] = 1 }, Contexts(usages));

        // Ссылка на тип — это References, и в отфильтрованном ответе остаётся только этот вид.
        var kinds = usages["byKind"]!.AsArray()
            .ToDictionary(item => Text(item!["kind"]), item => Count(item!["count"]));
        Assert.Equal(new Dictionary<string, int> { ["References"] = 1 }, kinds);

        var reader = Assert.Single(usages["readers"]!.AsArray())!;
        Assert.Equal("type", Text(reader["context"]));
        Assert.Equal(1, Count(reader["count"]));
        Assert.NotEqual(RoutineId, Text(reader["source"]));
    }

    [Fact]
    public async Task Фильтр_без_обращений_помечает_пустой_раздел()
    {
        using var session = new AnalysisSession(new AnalysisRequest(), CreateDump());

        var usages = (await CallAsync(
            session,
            "metadata",
            """{"id":"Catalog.Номенклатура","usageContext":"content"}"""))["usages"]!.AsObject();

        Assert.Equal(0, Count(usages["total"]));
        Assert.Equal(0, Count(usages["shown"]));
        Assert.Null(usages["byContext"]);
        Assert.Null(usages["readers"]);
        Assert.Null(usages["items"]);

        // Даже пустой ответ говорит, какой контекст искали: фильтр не теряется.
        Assert.Equal("content", Text(usages["filter"]!["context"]));
        Assert.Equal("в составе объекта", Text(usages["filter"]!["label"]));
    }

    [Fact]
    public async Task Неизвестный_контекст_даёт_ошибку_со_списком_допустимых()
    {
        using var session = new AnalysisSession(new AnalysisRequest(), CreateDump());

        var error = await Assert.ThrowsAsync<ToolException>(() => CallAsync(
            session,
            "metadata",
            """{"id":"Catalog.Номенклатура","usageContext":"queries"}"""));

        Assert.Contains("usageContext", error.Message, StringComparison.Ordinal);
        Assert.Contains("«queries»", error.Message, StringComparison.Ordinal);
        Assert.Contains("code (в коде)", error.Message, StringComparison.Ordinal);
        Assert.Contains("query (в запросах)", error.Message, StringComparison.Ordinal);
        Assert.Contains("type (в типах)", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task У_узла_тоже_работает_фильтр_по_контексту()
    {
        using var session = new AnalysisSession(new AnalysisRequest(), CreateDump());

        var usages = (await CallAsync(
            session,
            "node",
            """{"id":"Catalog.Номенклатура","usageContext":"query"}"""))["usages"]!.AsObject();

        Assert.Equal(1, Count(usages["total"]));
        Assert.Equal(new Dictionary<string, int> { ["query"] = 1 }, Contexts(usages));
        Assert.Equal("query", Text(usages["filter"]!["context"]));
        Assert.Contains("usageContext=\"query\"", Text(usages["hint"]), StringComparison.Ordinal);

        // Списки читателей и примеров остаются за metadata.
        Assert.Null(usages["readers"]);
        Assert.Null(usages["items"]);

        // Без фильтра сводка прежняя: три обращения в трёх контекстах.
        var all = (await CallAsync(session, "node", """{"id":"Catalog.Номенклатура"}"""))["usages"]!.AsObject();
        Assert.Equal(3, Count(all["total"]));
        Assert.Equal(3, all["byContext"]!.AsArray().Count);
        Assert.Null(all["filter"]);
    }

    [Fact]
    public async Task Фильтр_по_контексту_работает_и_по_индексу()
    {
        using var fixture = new IndexFixture();

        var usages = (await CallAsync(
            fixture.Session,
            "metadata",
            """{"id":"Catalog.Номенклатура","usageContext":"query"}"""))["usages"]!.AsObject();

        // Индекс отвечает тем же, что разбор в памяти: счётчики и примеры сходятся.
        Assert.Equal(1, Count(usages["total"]));
        Assert.Equal(new Dictionary<string, int> { ["query"] = 1 }, Contexts(usages));
        Assert.Equal("query", Text(usages["filter"]!["context"]));
        Assert.Equal("в запросах", Text(usages["filter"]!["label"]));

        var reader = usages["readers"]!.AsArray().Single(item => Text(item!["source"]) == RoutineId)!;
        Assert.Equal(1, Count(reader["count"]));
        Assert.Equal("query", Text(reader["context"]));
        Assert.Equal(ModulePath, Text(reader["file"]));

        var item = Assert.Single(usages["items"]!.AsArray())!;
        Assert.Equal("query", Text(item["context"]));
        Assert.Equal("ИЗ Справочник.Номенклатура КАК Т", Text(item["detail"]));
    }

    [Fact]
    public async Task Состояние_показывает_собранные_обращения_по_контекстам()
    {
        using var fixture = new IndexFixture();

        var statistics = (await CallAsync(fixture.Session, "status", "{}"))["indexStatistics"]!;

        Assert.Equal(3, Count(statistics["metadataRefs"]));
        Assert.Equal(1, Count(statistics["metadataRefsCode"]));
        Assert.Equal(1, Count(statistics["metadataRefsQuery"]));

        var byContext = statistics["metadataRefsByContext"]!.AsObject();
        Assert.Equal(1, Count(byContext["code"]));
        Assert.Equal(1, Count(byContext["query"]));
        Assert.Equal(1, Count(byContext["type"]));
    }

    /// <summary>Выгрузка с объектом, который читают из кода, из запроса и как тип реквизита.</summary>
    private static InMemoryDumpSource CreateDump() =>
        new InMemoryDumpSource("выгрузка с обращениями")
            .AddText("Configuration.xml", ConfigurationXml)
            .AddText("Catalogs/Номенклатура.xml", CatalogXml)
            .AddText("Catalogs/Заказы.xml", OrdersCatalogXml)
            .AddText("Catalogs/Пустой.xml", EmptyCatalogXml)
            .AddText("CommonModules/РаботаСНоменклатурой.xml", CommonModuleXml)
            .AddText(ModulePath, ModuleBsl);

    /// <summary>Инструмент поверх сессии: ответ разбирается как JSON, как его увидит агент.</summary>
    private static async Task<JsonObject> CallAsync(AnalysisSession session, string name, string arguments)
    {
        var tool = new ToolCatalog(session).Find(name) ?? throw new InvalidOperationException($"Нет инструмента «{name}».");
        var text = await tool.Execute(ToolArguments.From(JsonNode.Parse(arguments)), CancellationToken.None);
        return JsonNode.Parse(text)!.AsObject();
    }

    /// <summary>Числа из ответа: счётчики контекстов и лимитов.</summary>
    private static int Count(JsonNode? node) => node!.GetValue<int>();

    private static Dictionary<string, int> Contexts(JsonObject usages) =>
        usages["byContext"]!.AsArray().ToDictionary(item => Text(item!["context"]), item => Count(item!["count"]));

    private static string Text(JsonNode? node) => node switch
    {
        null => string.Empty,
        JsonValue value when value.TryGetValue<string>(out var text) => text,
        _ => node.ToJsonString(),
    };

    /// <summary>Выгрузка на диске и сессия в режиме индекса: проверяет ответы из SQLite.</summary>
    private sealed class IndexFixture : IDisposable
    {
        private readonly string _root;

        internal IndexFixture()
        {
            _root = TestDump.Materialize(CreateDump());
            Session = new AnalysisSession(new AnalysisRequest { DumpPaths = [_root] });

            // Индекс собирается при первом запросе: дальше инструменты отвечают из него.
            Session.QueryAsync(CancellationToken.None).GetAwaiter().GetResult();
        }

        internal AnalysisSession Session { get; }

        public void Dispose()
        {
            Session.Dispose();
            TestDump.Remove(_root);
        }
    }

    private const string ModuleBsl = """
        Процедура ЗагрузитьТовары() Экспорт
            Ссылка = Справочники.Номенклатура.НайтиПоНаименованию("Ручка");
            Текст = "ВЫБРАТЬ * ИЗ Справочник.Номенклатура КАК Т";
            Результат = Новый Запрос(Текст).Выполнить();
        КонецПроцедуры
        """;

    private const string ConfigurationXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <MetaDataObject xmlns="http://v8.1c.ru/8.3/MDClasses" version="2.20">
            <Configuration uuid="0f0f0f0f-0000-0000-0000-0000000000c1">
                <Properties>
                    <Name>ВыгрузкаСОбращениями</Name>
                </Properties>
                <ChildObjects>
                    <Catalog>Номенклатура</Catalog>
                    <Catalog>Заказы</Catalog>
                    <Catalog>Пустой</Catalog>
                    <CommonModule>РаботаСНоменклатурой</CommonModule>
                </ChildObjects>
            </Configuration>
        </MetaDataObject>
        """;

    private const string CatalogXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <MetaDataObject xmlns="http://v8.1c.ru/8.3/MDClasses" xmlns:v8="http://v8.1c.ru/8.1/data/core" version="2.20">
            <Catalog uuid="11111111-1111-1111-1111-11111111111c">
                <Properties>
                    <Name>Номенклатура</Name>
                    <Synonym>
                        <v8:item>
                            <v8:lang>ru</v8:lang>
                            <v8:content>Номенклатура</v8:content>
                        </v8:item>
                    </Synonym>
                </Properties>
            </Catalog>
        </MetaDataObject>
        """;

    private const string OrdersCatalogXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <MetaDataObject xmlns="http://v8.1c.ru/8.3/MDClasses" xmlns:v8="http://v8.1c.ru/8.1/data/core" xmlns:cfg="http://v8.1c.ru/8.1/data/enterprise/current-config" version="2.20">
            <Catalog uuid="22222222-2222-2222-2222-22222222222c">
                <Properties>
                    <Name>Заказы</Name>
                </Properties>
                <ChildObjects>
                    <Attribute uuid="33333333-3333-3333-3333-33333333333c">
                        <Properties>
                            <Name>Номенклатура</Name>
                            <Type>
                                <v8:Type>cfg:CatalogRef.Номенклатура</v8:Type>
                            </Type>
                        </Properties>
                    </Attribute>
                </ChildObjects>
            </Catalog>
        </MetaDataObject>
        """;

    private const string EmptyCatalogXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <MetaDataObject xmlns="http://v8.1c.ru/8.3/MDClasses" version="2.20">
            <Catalog uuid="44444444-4444-4444-4444-44444444444c">
                <Properties>
                    <Name>Пустой</Name>
                </Properties>
            </Catalog>
        </MetaDataObject>
        """;

    private const string CommonModuleXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <MetaDataObject xmlns="http://v8.1c.ru/8.3/MDClasses" version="2.20">
            <CommonModule uuid="55555555-5555-5555-5555-55555555555c">
                <Properties>
                    <Name>РаботаСНоменклатурой</Name>
                    <Server>true</Server>
                </Properties>
            </CommonModule>
        </MetaDataObject>
        """;
}
