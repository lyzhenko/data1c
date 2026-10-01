using System.Text.Json.Nodes;
using Data1c.Core.Analysis;
using Data1c.Core.Dump;
using Data1c.Mcp;
using Data1c.Store;
using Xunit;

namespace Data1c.Tests.Mcp;

/// <summary>
/// Проверки инструмента entrypoints (Э3-2): подписки на события, регламентные задания и обработчики
/// событий форм должны быть видны агенту вместе с процедурами-обработчиками, путями модулей и строками.
/// </summary>
/// <remarks>
/// Выгрузка собирается своя, внутри теста: общий <see cref="SampleDump"/> менять нельзя — на нём
/// висят точные проверки содержимого индекса. Индекс собирается по временному пути, поэтому
/// инструмент отвечает из SQLite, а не из разбора в памяти.
/// </remarks>
public sealed class EntryPointsTests
{
    private const string ConfigurationXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <MetaDataObject xmlns="http://v8.1c.ru/8.3/MDClasses" xmlns:v8="http://v8.1c.ru/8.1/data/core" xmlns:cfg="http://v8.1c.ru/8.1/data/enterprise/current-config" version="2.20">
        	<Configuration uuid="0f0f0f0f-0000-0000-0000-0000000000e1">
        		<Properties>
        			<Name>КонфигурацияСТочкамиВхода</Name>
        		</Properties>
        		<ChildObjects>
        			<Catalog>Товары</Catalog>
        			<CommonModule>МодульПодписок</CommonModule>
        			<EventSubscription>ПередЗаписьюТовара</EventSubscription>
        			<ScheduledJob>НочноеОбновление</ScheduledJob>
        		</ChildObjects>
        	</Configuration>
        </MetaDataObject>
        """;

    /// <summary>Конфигурация без подписок, заданий и обработчиков форм: проверка пустого ответа.</summary>
    private const string EmptyConfigurationXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <MetaDataObject xmlns="http://v8.1c.ru/8.3/MDClasses" xmlns:v8="http://v8.1c.ru/8.1/data/core" version="2.20">
        	<Configuration uuid="0f0f0f0f-0000-0000-0000-0000000000e2">
        		<Properties>
        			<Name>КонфигурацияБезТочекВхода</Name>
        		</Properties>
        		<ChildObjects>
        			<Catalog>Товары</Catalog>
        		</ChildObjects>
        	</Configuration>
        </MetaDataObject>
        """;

    private const string CatalogXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <MetaDataObject xmlns="http://v8.1c.ru/8.3/MDClasses" xmlns:v8="http://v8.1c.ru/8.1/data/core" version="2.20">
        	<Catalog uuid="11111111-1111-1111-1111-1111111111e1">
        		<Properties>
        			<Name>Товары</Name>
        		</Properties>
        		<ChildObjects>
        			<Form>ФормаЭлемента</Form>
        		</ChildObjects>
        	</Catalog>
        </MetaDataObject>
        """;

    private const string CatalogFormMetadataXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <MetaDataObject xmlns="http://v8.1c.ru/8.3/MDClasses" xmlns:v8="http://v8.1c.ru/8.1/data/core" version="2.20">
        	<Form uuid="55555555-5555-5555-5555-5555555555e1">
        		<Properties>
        			<Name>ФормаЭлемента</Name>
        			<FormType>Managed</FormType>
        		</Properties>
        	</Form>
        </MetaDataObject>
        """;

    /// <summary>Описание управляемой формы: обработчик формы целиком и обработчик элемента.</summary>
    private const string CatalogFormXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <Form xmlns="http://v8.1c.ru/8.3/xcf/logform" xmlns:cfg="http://v8.1c.ru/8.1/data/enterprise/current-config" xmlns:v8="http://v8.1c.ru/8.1/data/core" version="2.18">
        	<Events>
        		<Event name="OnOpen">ПриОткрытии</Event>
        	</Events>
        	<ChildItems>
        		<InputField name="Артикул" id="1">
        			<DataPath>Объект.Артикул</DataPath>
        			<Events>
        				<Event name="OnChange">АртикулПриИзменении</Event>
        			</Events>
        		</InputField>
        	</ChildItems>
        	<Attributes>
        		<Attribute name="Объект" id="1">
        			<Type>
        				<v8:Type>cfg:CatalogObject.Товары</v8:Type>
        			</Type>
        			<MainAttribute>true</MainAttribute>
        		</Attribute>
        	</Attributes>
        </Form>
        """;

    private const string CatalogFormModuleBsl = """
        &НаКлиенте
        Процедура ПриОткрытии(Отказ)
        	Сообщить("Открытие");
        КонецПроцедуры

        &НаКлиенте
        Процедура АртикулПриИзменении(Элемент)
        	Сообщить("Изменение");
        КонецПроцедуры
        """;

    private const string CommonModuleXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <MetaDataObject xmlns="http://v8.1c.ru/8.3/MDClasses" xmlns:v8="http://v8.1c.ru/8.1/data/core" version="2.20">
        	<CommonModule uuid="66666666-6666-6666-6666-6666666666e1">
        		<Properties>
        			<Name>МодульПодписок</Name>
        			<Server>true</Server>
        		</Properties>
        	</CommonModule>
        </MetaDataObject>
        """;

    private const string CommonModuleBsl = """
        Процедура ПередЗаписьюТовара(Источник, Отказ) Экспорт
        	Сообщить("Подписка");
        КонецПроцедуры

        Процедура ОбновитьДанные() Экспорт
        	Сообщить("Обновление");
        КонецПроцедуры
        """;

    /// <summary>Подписка на событие справочника: источник — тип ссылки, обработчик — общий модуль.</summary>
    private const string SubscriptionXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <MetaDataObject xmlns="http://v8.1c.ru/8.3/MDClasses" xmlns:v8="http://v8.1c.ru/8.1/data/core" xmlns:cfg="http://v8.1c.ru/8.1/data/enterprise/current-config" version="2.20">
        	<EventSubscription uuid="77777777-7777-7777-7777-7777777777e1">
        		<Properties>
        			<Name>ПередЗаписьюТовара</Name>
        			<Comment/>
        			<Source>
        				<v8:Type>cfg:CatalogRef.Товары</v8:Type>
        			</Source>
        			<Event>BeforeWrite</Event>
        			<Handler>CommonModule.МодульПодписок.ПередЗаписьюТовара</Handler>
        		</Properties>
        	</EventSubscription>
        </MetaDataObject>
        """;

    /// <summary>Регламентное задание: метод в общем модуле, использование и ключ — в свойствах.</summary>
    private const string JobXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <MetaDataObject xmlns="http://v8.1c.ru/8.3/MDClasses" xmlns:v8="http://v8.1c.ru/8.1/data/core" version="2.20">
        	<ScheduledJob uuid="88888888-8888-8888-8888-8888888888e1">
        		<Properties>
        			<Name>НочноеОбновление</Name>
        			<MethodName>CommonModule.МодульПодписок.ОбновитьДанные</MethodName>
        			<Use>false</Use>
        			<Predefined>true</Predefined>
        		</Properties>
        	</ScheduledJob>
        </MetaDataObject>
        """;

    private const string SubscriptionPath = "EventSubscriptions/ПередЗаписьюТовара.xml";
    private const string JobPath = "ScheduledJobs/НочноеОбновление.xml";
    private const string CommonModuleBslPath = "CommonModules/МодульПодписок/Ext/Module.bsl";
    private const string FormModuleBslPath = "Catalogs/Товары/Forms/ФормаЭлемента/Ext/Form/Module.bsl";
    private const string FormId = "Catalog.Товары/Form.ФормаЭлемента";
    private const string SubscriptionId = "EventSubscription.ПередЗаписьюТовара";
    private const string JobId = "ScheduledJob.НочноеОбновление";

    [Fact]
    public async Task Подписка_и_регламентное_задание_видны_с_обработчиками()
    {
        using var fixture = EntryPointsFixture.Create();

        var payload = await CallAsync(fixture.Session, """{"limit":10}""");

        Assert.Equal(1, payload["counts"]!["subscription"]!.GetValue<int>());
        Assert.Equal(1, payload["counts"]!["job"]!.GetValue<int>());
        Assert.Equal(2, payload["counts"]!["form"]!.GetValue<int>());
        Assert.Equal(4, payload["found"]!.GetValue<int>());

        // Подписка: событие справочника, обработчик в общем модуле и узел процедуры для code.
        var subscription = Item(payload, "subscription");
        Assert.Equal(SubscriptionId, Text(subscription["id"]));
        Assert.Equal("EventSubscription", Text(subscription["objectKind"]));
        Assert.Equal("BeforeWrite", Text(subscription["event"]));
        Assert.Equal("ПередЗаписью", Text(subscription["eventName"]));
        Assert.Equal("Catalog.Товары", Assert.Single(subscription["sources"]!.AsArray().Select(Text)));
        Assert.Equal(CommonModuleBslPath, Text(subscription["module"]));
        Assert.Equal("ПередЗаписьюТовара", Text(subscription["procedure"]));
        Assert.Equal($"routine:module:{CommonModuleBslPath}#ПередЗаписьюТовара", Text(subscription["routine"]));
        Assert.Equal(1, subscription["line"]!.GetValue<int>());
        Assert.True(subscription["resolved"]!.GetValue<bool>());
        Assert.Equal(SubscriptionPath, Text(subscription["file"]));

        // Задание: обработчик там же, свойства задания (использование) видны в ответе.
        var job = Item(payload, "job");
        Assert.Equal(JobId, Text(job["id"]));
        Assert.Equal("ScheduledJob", Text(job["objectKind"]));
        Assert.Equal(CommonModuleBslPath, Text(job["module"]));
        Assert.Equal("ОбновитьДанные", Text(job["procedure"]));
        Assert.Equal($"routine:module:{CommonModuleBslPath}#ОбновитьДанные", Text(job["routine"]));
        Assert.Equal(5, job["line"]!.GetValue<int>());
        Assert.Equal("false", Text(job["properties"]!["Use"]));
        Assert.Equal("true", Text(job["properties"]!["Predefined"]));
        Assert.Equal(JobPath, Text(job["file"]));
    }

    [Fact]
    public async Task Фильтр_по_объекту_метаданных_оставляет_только_относящиеся_точки_входа()
    {
        using var fixture = EntryPointsFixture.Create();

        // Справочник: подписка слушает его события, а обработчики формы этого справочника реагируют на его изменения.
        var forCatalog = await CallAsync(fixture.Session, """{"metadata":"Catalog.Товары","limit":10}""");
        var forCatalogIds = Ids(forCatalog);

        Assert.Equal(1, forCatalog["counts"]!["subscription"]!.GetValue<int>());
        Assert.Equal(0, forCatalog["counts"]!["job"]!.GetValue<int>());
        Assert.Equal(2, forCatalog["counts"]!["form"]!.GetValue<int>());
        Assert.Contains(SubscriptionId, forCatalogIds);
        Assert.Contains($"{FormId}#OnOpen", forCatalogIds);
        Assert.DoesNotContain(JobId, forCatalogIds);

        // Общий модуль-обработчик: подписка и задание, но не обработчики формы.
        var forModule = await CallAsync(fixture.Session, """{"metadata":"CommonModule.МодульПодписок","limit":10}""");
        Assert.Equal(1, forModule["counts"]!["subscription"]!.GetValue<int>());
        Assert.Equal(1, forModule["counts"]!["job"]!.GetValue<int>());
        Assert.Equal(0, forModule["counts"]!["form"]!.GetValue<int>());

        // Сама форма: её обработчики (у общей формы объекта-владельца нет, форма ищется по своему идентификатору).
        var forForm = await CallAsync(fixture.Session, $$"""{"metadata":"{{FormId}}","limit":10}""");
        Assert.Equal(2, forForm["counts"]!["form"]!.GetValue<int>());
        Assert.Equal(0, forForm["counts"]!["subscription"]!.GetValue<int>());
    }

    [Fact]
    public async Task Обработчики_формы_попадают_в_список_с_процедурами()
    {
        using var fixture = EntryPointsFixture.Create();

        var payload = await CallAsync(fixture.Session, """{"kind":"form","limit":10}""");

        Assert.Equal(0, payload["counts"]!["subscription"]!.GetValue<int>());
        Assert.Equal(0, payload["counts"]!["job"]!.GetValue<int>());
        Assert.Equal(2, payload["found"]!.GetValue<int>());

        var open = ById(payload, $"{FormId}#OnOpen");
        Assert.Equal("ФормаЭлемента", Text(open["name"]));
        Assert.Equal("form", Text(open["kind"]));
        Assert.Equal(FormId, Text(open["objectId"]));
        Assert.Equal("ПриОткрытии", Text(open["procedure"]));
        Assert.Equal("OnOpen", Text(open["event"]));
        Assert.Equal(2, open["line"]!.GetValue<int>());
        Assert.Equal(FormModuleBslPath, Text(open["module"]));
        Assert.Equal($"routine:module:{FormModuleBslPath}#ПриОткрытии", Text(open["routine"]));
        Assert.Null(open["element"]);

        // Обработчик элемента: у него указан элемент, а событие переведено на русский.
        var changed = ById(payload, $"{FormId}#Артикул.OnChange");

        Assert.Equal("Артикул", Text(changed["element"]));
        Assert.Equal("АртикулПриИзменении", Text(changed["procedure"]));
        Assert.Equal("OnChange", Text(changed["event"]));
        Assert.Equal("ПриИзменении", Text(changed["eventName"]));
        Assert.Equal(7, changed["line"]!.GetValue<int>());
        Assert.True(changed["resolved"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Пустой_ответ_по_чужому_объекту_подсказывает_поиск()
    {
        using var fixture = EntryPointsFixture.Create();

        var responses = await ExchangeAsync(
            fixture.Session,
            InitializeKnown,
            ToolCall(2, "entrypoints", """{"metadata":"Catalog.НетТакогоОбъекта"}"""));

        var text = ContentText(responses, 2);
        Assert.Contains("не найдено", text, StringComparison.Ordinal);
        Assert.Contains("Catalog.НетТакогоОбъекта", text, StringComparison.Ordinal);
        Assert.Contains("search", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Выгрузка_без_точек_входа_отвечает_понятно()
    {
        // Выгрузка без подписок, заданий и форм: ответ обязан объяснить пустоту, а не отдать пустой JSON.
        var source = new InMemoryDumpSource("выгрузка без точек входа");
        source.AddText("Configuration.xml", EmptyConfigurationXml);
        source.AddText("Catalogs/Товары.xml", CatalogXml);

        using var session = new AnalysisSession(new AnalysisRequest { UseIndex = false }, source);
        await session.GetAsync(CancellationToken.None);

        var responses = await ExchangeAsync(session, InitializeKnown, ToolCall(2, "entrypoints", "{}"));

        var text = ContentText(responses, 2);
        Assert.Contains("не найдено", text, StringComparison.Ordinal);
        Assert.Contains("подписок на события", text, StringComparison.Ordinal);
        Assert.DoesNotContain("{", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Лимит_соблюдается_и_обрезка_помечается()
    {
        using var fixture = EntryPointsFixture.Create();

        var payload = await CallAsync(fixture.Session, """{"kind":"form","limit":1}""");

        Assert.Equal(1, payload["found"]!.GetValue<int>());
        Assert.Single(payload["entryPoints"]!.AsArray());
        Assert.True(payload["truncated"]!.GetValue<bool>());
        Assert.Contains("увеличьте limit", Text(payload["note"]), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Без_индекса_точки_входа_берутся_из_разбора_в_памяти()
    {
        // Режим без индекса: те же точки входа собираются из разбора в памяти (AnalysisEntryPoints).
        using var session = new AnalysisSession(new AnalysisRequest { UseIndex = false }, CreateSource());
        await session.GetAsync(CancellationToken.None);

        Assert.Null(session.GetIndexReader());
        Assert.NotNull(session.Result);

        var responses = await ExchangeAsync(session, InitializeKnown, ToolCall(2, "entrypoints", """{"limit":10}"""));

        var payload = Json(ContentText(responses, 2));
        Assert.Equal(1, payload["counts"]!["subscription"]!.GetValue<int>());
        Assert.Equal(1, payload["counts"]!["job"]!.GetValue<int>());
        Assert.Equal(2, payload["counts"]!["form"]!.GetValue<int>());

        var subscription = Item(payload, "subscription");
        Assert.Equal(CommonModuleBslPath, Text(subscription["module"]));
        Assert.Equal($"routine:module:{CommonModuleBslPath}#ПередЗаписьюТовара", Text(subscription["routine"]));
        Assert.Equal("ПередЗаписью", Text(subscription["eventName"]));
        Assert.Equal("Catalog.Товары", Assert.Single(subscription["sources"]!.AsArray().Select(Text)));
    }

    /// <summary>Выгрузка с подпиской, регламентным заданием и формой с двумя обработчиками.</summary>
    private static InMemoryDumpSource CreateSource()
    {
        var source = new InMemoryDumpSource("выгрузка с точками входа");
        source.AddText("Configuration.xml", ConfigurationXml);
        source.AddText("Catalogs/Товары.xml", CatalogXml);
        source.AddText("Catalogs/Товары/Forms/ФормаЭлемента.xml", CatalogFormMetadataXml);
        source.AddText("Catalogs/Товары/Forms/ФормаЭлемента/Ext/Form.xml", CatalogFormXml);
        source.AddText("Catalogs/Товары/Forms/ФормаЭлемента/Ext/Form/Module.bsl", CatalogFormModuleBsl);
        source.AddText("CommonModules/МодульПодписок.xml", CommonModuleXml);
        source.AddText(CommonModuleBslPath, CommonModuleBsl);
        source.AddText(SubscriptionPath, SubscriptionXml);
        source.AddText(JobPath, JobXml);
        return source;
    }

    /// <summary>Точки входа указанного вида из ответа инструмента.</summary>
    private static JsonObject Item(JsonObject payload, string kind) =>
        payload["entryPoints"]!.AsArray()
            .Select(item => item!.AsObject())
            .Single(item => Text(item["kind"]) == kind);

    /// <summary>Точка входа по её идентификатору.</summary>
    private static JsonObject ById(JsonObject payload, string id) =>
        payload["entryPoints"]!.AsArray()
            .Select(item => item!.AsObject())
            .Single(item => Text(item["id"]) == id);

    private static List<string> Ids(JsonObject payload) =>
        [.. payload["entryPoints"]!.AsArray().Select(item => Text(item!["id"]))];

    /// <summary>
    /// Индекс собирается заранее по временному пути: сессия отвечает из SQLite, разбора в памяти нет.
    /// </summary>
    private sealed class EntryPointsFixture : IDisposable
    {
        private readonly string _indexPath;

        private EntryPointsFixture(AnalysisSession session, string indexPath)
        {
            Session = session;
            _indexPath = indexPath;
        }

        internal AnalysisSession Session { get; }

        internal static EntryPointsFixture Create()
        {
            var source = CreateSource();
            var path = Path.Combine(Path.GetTempPath(), "data1c-entrypoints-" + Guid.NewGuid().ToString("N") + ".db");
            var analyzed = new DumpAnalyzer().Analyze(source);
            using (var index = SqliteIndex.Open(path))
            {
                new IndexWriter(index).Write(source, analyzed);
            }

            var session = new AnalysisSession(new AnalysisRequest { IndexPath = path, UseIndex = true }, source);
            return new EntryPointsFixture(session, path);
        }

        public void Dispose()
        {
            Session.Dispose();
            foreach (var file in Directory.EnumerateFiles(
                Path.GetDirectoryName(_indexPath)!,
                Path.GetFileName(_indexPath) + "*"))
            {
                File.Delete(file);
            }
        }
    }

    private static async Task<JsonObject> CallAsync(AnalysisSession session, string arguments)
    {
        Assert.Null(session.Result);
        var responses = await ExchangeAsync(session, InitializeKnown, ToolCall(2, "entrypoints", arguments));
        return Json(ContentText(responses, 2));
    }

    private const string InitializeKnown =
        """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","clientInfo":{"name":"тест","version":"1"}}}""";

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

    private static string ContentText(IReadOnlyList<JsonObject> responses, int id) =>
        Text(Result(responses, id)["content"]![0]!["text"]);

    private static JsonObject Result(IReadOnlyList<JsonObject> responses, int id) =>
        responses.Single(item => Text(item["id"]) == id.ToString(System.Globalization.CultureInfo.InvariantCulture))["result"]!.AsObject();

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
