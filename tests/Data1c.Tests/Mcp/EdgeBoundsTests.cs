using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Data1c.Core.Analysis;
using Data1c.Core.Dump;
using Data1c.Mcp;
using Xunit;

namespace Data1c.Tests.Mcp;

/// <summary>
/// Границы процедур и модуль стороны в связях (T-9): карточка узла и окружение отдают
/// <c>startLine</c>/<c>endLine</c> процедуры и файл второй стороны связи, чтобы цепочку вызовов
/// читать по одному ответу. Проверяются оба режима: разбор в памяти кладёт границы в теги узла,
/// а индекс теги не хранит — там они берутся из символов модуля.
/// </summary>
public sealed class EdgeBoundsTests
{
    private const string ModulePath = "CommonModules/РаботаСТоварами/Ext/Module.bsl";
    private const string LoadRoutineId = "routine:module:" + ModulePath + "#ЗагрузитьТовары";
    private const string CallerRoutineId = "routine:module:" + ModulePath + "#ВызватьЗагрузку";

    /// <summary>ЗагрузитьТовары — строки 1–3, ВызватьЗагрузку — 5–7, вызов — строка 6 (см. ModuleBsl).</summary>
    private const int LoadStartLine = 1;

    private const int LoadEndLine = 3;
    private const int CallerStartLine = 5;
    private const int CallerEndLine = 7;
    private const int CallLine = 6;

    /// <summary>Сколько процедур читают справочник в выгрузке для проверки обрезки ответов.</summary>
    private const int ReadersCount = 200;

    [Fact]
    public async Task Карточка_процедуры_отдаёт_границы()
    {
        using var session = new AnalysisSession(new AnalysisRequest(), CreateDump());

        var node = (await CallAsync(session, "node", $$"""{"id":"{{LoadRoutineId}}"}"""))["node"]!.AsObject();

        Assert.Equal("Routine", Text(node["kind"]));
        Assert.Equal(ModulePath, Text(node["file"]));
        Assert.Equal(LoadStartLine, Count(node["startLine"]));
        Assert.Equal(LoadEndLine, Count(node["endLine"]));
    }

    [Fact]
    public async Task Связь_несёт_модуль_и_границы_второй_стороны()
    {
        using var session = new AnalysisSession(new AnalysisRequest(), CreateDump());

        var payload = await CallAsync(session, "node", """{"id":"Catalog.Товары"}""");
        var edge = payload["incoming"]!.AsArray()
            .Select(item => item!.AsObject())
            .First(item => Text(item["node"]) == LoadRoutineId);

        // Вторая сторона связи — процедура-читатель: видно и её модуль, и докуда она идёт.
        Assert.Equal("UsesMetadata", Text(edge["kind"]));
        Assert.Equal(ModulePath, Text(edge["file"]));
        Assert.Equal(LoadStartLine, Count(edge["startLine"]));
        Assert.Equal(LoadEndLine, Count(edge["endLine"]));
    }

    [Fact]
    public async Task Окружение_отдаёт_границы_узлов_и_строки_связей()
    {
        using var session = new AnalysisSession(new AnalysisRequest(), CreateDump());

        var payload = await CallAsync(
            session,
            "neighbors",
            $$"""{"id":"{{CallerRoutineId}}","edgeKinds":["Calls"],"direction":"out"}""");

        var called = payload["nodes"]!.AsArray()
            .Select(item => item!.AsObject())
            .Single(item => Text(item["id"]) == LoadRoutineId);
        Assert.Equal(ModulePath, Text(called["file"]));
        Assert.Equal(LoadStartLine, Count(called["startLine"]));
        Assert.Equal(LoadEndLine, Count(called["endLine"]));

        var edge = Assert.Single(payload["edges"]!.AsArray())!.AsObject();
        Assert.Equal(CallerRoutineId, Text(edge["from"]));
        Assert.Equal(LoadRoutineId, Text(edge["to"]));
        Assert.Equal(CallLine, Count(edge["line"]));
    }

    [Fact]
    public async Task По_индексу_границы_берутся_из_символов_модуля()
    {
        // Индекс теги узлов не хранит: без поиска по символам границы процедур пропали бы именно
        // в основном режиме работы сервера, а вместе с ними — «докуда идёт вызывающая процедура».
        using var fixture = new IndexFixture();

        var card = (await CallAsync(fixture.Session, "node", $$"""{"id":"{{LoadRoutineId}}"}"""))["node"]!.AsObject();
        Assert.Equal(ModulePath, Text(card["file"]));
        Assert.Equal(LoadStartLine, Count(card["startLine"]));
        Assert.Equal(LoadEndLine, Count(card["endLine"]));

        var neighbors = await CallAsync(
            fixture.Session,
            "neighbors",
            $$"""{"id":"{{CallerRoutineId}}","edgeKinds":["Calls"],"direction":"out"}""");
        var called = neighbors["nodes"]!.AsArray()
            .Select(item => item!.AsObject())
            .Single(item => Text(item["id"]) == LoadRoutineId);
        Assert.Equal(ModulePath, Text(called["file"]));
        Assert.Equal(LoadEndLine, Count(called["endLine"]));

        // Связь карточки объекта тоже несёт модуль и границы читателя.
        var edges = (await CallAsync(fixture.Session, "node", """{"id":"Catalog.Товары"}"""))["incoming"]!.AsArray();
        var edge = edges.Select(item => item!.AsObject()).First(item => Text(item["node"]) == LoadRoutineId);
        Assert.Equal(ModulePath, Text(edge["file"]));
        Assert.Equal(LoadEndLine, Count(edge["endLine"]));
    }

    [Fact]
    public async Task У_узла_без_процедуры_границ_нет()
    {
        using var session = new AnalysisSession(new AnalysisRequest(), CreateDump());

        // У модуля и объекта метаданных процедуры нет: выдуманных границ быть не должно.
        var module = (await CallAsync(session, "node", $$"""{"id":"module:{{ModulePath}}"}"""))["node"]!.AsObject();
        Assert.Null(module["startLine"]);
        Assert.Null(module["endLine"]);

        var catalog = (await CallAsync(session, "node", """{"id":"Catalog.Товары"}"""))["node"]!.AsObject();
        Assert.Null(catalog["startLine"]);
        Assert.Null(catalog["endLine"]);
    }

    [Fact]
    public async Task Вызов_внутри_процедуры_виден_с_границами_обеих_сторон()
    {
        using var session = new AnalysisSession(new AnalysisRequest(), CreateDump());

        // Цепочка «кто вызывает» собирается по одному ответу: у обеих сторон видны модуль и границы.
        var payload = await CallAsync(
            session,
            "neighbors",
            $$"""{"id":"{{LoadRoutineId}}","edgeKinds":["Calls"],"direction":"in"}""");

        var caller = payload["nodes"]!.AsArray()
            .Select(item => item!.AsObject())
            .Single(item => Text(item["id"]) == CallerRoutineId);
        Assert.Equal(CallerStartLine, Count(caller["startLine"]));
        Assert.Equal(CallerEndLine, Count(caller["endLine"]));

        var edge = Assert.Single(payload["edges"]!.AsArray())!.AsObject();
        Assert.Equal(CallerRoutineId, Text(edge["from"]));
        Assert.Equal(LoadRoutineId, Text(edge["to"]));
    }

    [Fact]
    public async Task Обрезка_подсказывает_свои_аргументы()
    {
        // Окружение объекта больше предела длины: подсказка обязана называть аргументы node/neighbors,
        // а не чужие limit и «диапазон строк» — иначе агент пробует то, чего у инструмента нет.
        using var session = new AnalysisSession(new AnalysisRequest(), BigNeighborhoodDump());

        var node = await CallAsync(session, "node", """{"id":"Catalog.Товары","edges":200}""");
        Assert.True(node["truncated"]!.GetValue<bool>());
        var nodeMessage = Text(node["message"]);
        Assert.Contains("edges", nodeMessage, StringComparison.Ordinal);
        Assert.Contains("id", nodeMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("диапазон строк", nodeMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("limit", nodeMessage, StringComparison.Ordinal);

        var neighbors = await CallAsync(session, "neighbors", """{"id":"Catalog.Товары","maxNodes":500}""");
        Assert.True(neighbors["truncated"]!.GetValue<bool>());
        var neighborsMessage = Text(neighbors["message"]);
        foreach (var argument in new[] { "maxNodes", "depth", "edgeKinds", "nodeKinds", "direction" })
        {
            Assert.Contains(argument, neighborsMessage, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("диапазон строк", neighborsMessage, StringComparison.Ordinal);
    }

    /// <summary>Выгрузка с общим модулем: одна процедура читает справочник, вторая её вызывает.</summary>
    private static InMemoryDumpSource CreateDump() =>
        new InMemoryDumpSource("выгрузка с цепочкой вызовов")
            .AddText("Configuration.xml", ConfigurationXml)
            .AddText("Catalogs/Товары.xml", CatalogXml)
            .AddText("CommonModules/РаботаСТоварами.xml", CommonModuleXml)
            .AddText(ModulePath, ModuleBsl);

    /// <summary>
    /// Выгрузка с большим окружением: двести процедур читают один справочник, поэтому ответы node
    /// и neighbors не помещаются в предел длины и приходят с подсказкой.
    /// </summary>
    private static InMemoryDumpSource BigNeighborhoodDump()
    {
        var text = new StringBuilder();
        for (var index = 1; index <= ReadersCount; index++)
        {
            text.Append(CultureInfo.InvariantCulture, $"Процедура Читатель{index:D3}()\n");
            text.Append("    Ссылка = Справочники.Товары.НайтиПоНаименованию(\"Ручка\");\n");
            text.Append("КонецПроцедуры\n\n");
        }

        return new InMemoryDumpSource("выгрузка с большим окружением")
            .AddText("Configuration.xml", ConfigurationXml)
            .AddText("Catalogs/Товары.xml", CatalogXml)
            .AddText("CommonModules/РаботаСТоварами.xml", CommonModuleXml)
            .AddText(ModulePath, text.ToString());
    }

    private static async Task<JsonObject> CallAsync(AnalysisSession session, string name, string arguments)
    {
        var tool = new ToolCatalog(session).Find(name) ?? throw new InvalidOperationException($"Нет инструмента «{name}».");
        var text = await tool.Execute(ToolArguments.From(JsonNode.Parse(arguments)), CancellationToken.None);
        return JsonNode.Parse(text)!.AsObject();
    }

    private static int Count(JsonNode? node) => node!.GetValue<int>();

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

    /// <summary>
    /// Строки 1–3 — процедура-читатель, 5–7 — вызывающая: границы в проверках привязаны к тексту,
    /// поэтому правка модуля без правки проверок сразу видна.
    /// </summary>
    private const string ModuleBsl = """
        Процедура ЗагрузитьТовары() Экспорт
            Ссылка = Справочники.Товары.НайтиПоНаименованию("Ручка");
        КонецПроцедуры

        Процедура ВызватьЗагрузку()
            ЗагрузитьТовары();
        КонецПроцедуры
        """;

    private const string ConfigurationXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <MetaDataObject xmlns="http://v8.1c.ru/8.3/MDClasses" version="2.20">
            <Configuration uuid="0f0f0f0f-0000-0000-0000-0000000000d1">
                <Properties>
                    <Name>ВыгрузкаСЦепочкойВызовов</Name>
                </Properties>
                <ChildObjects>
                    <Catalog>Товары</Catalog>
                    <CommonModule>РаботаСТоварами</CommonModule>
                </ChildObjects>
            </Configuration>
        </MetaDataObject>
        """;

    private const string CatalogXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <MetaDataObject xmlns="http://v8.1c.ru/8.3/MDClasses" xmlns:v8="http://v8.1c.ru/8.1/data/core" version="2.20">
            <Catalog uuid="11111111-1111-1111-1111-11111111111d">
                <Properties>
                    <Name>Товары</Name>
                </Properties>
            </Catalog>
        </MetaDataObject>
        """;

    private const string CommonModuleXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <MetaDataObject xmlns="http://v8.1c.ru/8.3/MDClasses" version="2.20">
            <CommonModule uuid="55555555-5555-5555-5555-55555555555d">
                <Properties>
                    <Name>РаботаСТоварами</Name>
                    <Server>true</Server>
                </Properties>
            </CommonModule>
        </MetaDataObject>
        """;
}
