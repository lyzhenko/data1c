using System.Text.Json.Nodes;
using Data1c.Core.Analysis;
using Data1c.Core.Dump;
using Data1c.Mcp;
using Data1c.Store;
using Xunit;

namespace Data1c.Tests.Mcp;

/// <summary>
/// Поиск по содержимому модулей через полнотекстовый индекс (T-10): <c>grep</c> сначала находит
/// модули-кандидаты в <c>modules_fts</c> и читает только их, поэтому строки, контекст и границы
/// процедур остаются точными. Проверяется и разбор в памяти, и индекс на диске.
/// </summary>
public sealed class ModuleTextSearchTests
{
    private const string BigModulePath = "CommonModules/РаботаСТоварами/Ext/Module.bsl";
    private const string SmallModulePath = "CommonModules/Прочее/Ext/Module.bsl";

    [Fact]
    public async Task Поиск_по_индексу_даёт_те_же_находки_что_поиск_по_файлам()
    {
        using var fixture = new IndexFixture();

        // Шаблон есть в двух модулях: индекс отбирает их, а находки даёт чтение файлов.
        var byIndex = await CallAsync(fixture.Session, """{"pattern":"НайтиПоНаименованию"}""");

        Assert.Equal("index", Text(byIndex["scan"]!["mode"]));
        Assert.True(byIndex["scan"]!["candidates"]!.GetValue<int>() >= 2);
        var files = byIndex["hits"]!.AsArray().Select(hit => Text(hit!["file"])).Distinct(StringComparer.Ordinal).ToList();
        Assert.Contains(BigModulePath, files);
        Assert.Contains(SmallModulePath, files);

        // Строка, контекст и процедура на месте: индекс только отбирает файлы.
        var hit = byIndex["hits"]!.AsArray().First(item => Text(item!["file"]) == BigModulePath)!;
        Assert.Equal(3, hit["line"]!.GetValue<int>());
        Assert.Equal("ЗагрузитьТовары", Text(hit["routine"]!["name"]));
        Assert.Contains("НайтиПоНаименованию", Text(hit["text"]), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Подстрока_внутри_слова_находится()
    {
        using var fixture = new IndexFixture();

        // Токенизатор — триграммы: подстрока внутри слова ищется так же, как поиском по файлам.
        var payload = await CallAsync(fixture.Session, """{"pattern":"айтиПоНа"}""");

        Assert.Equal("index", Text(payload["scan"]!["mode"]));
        Assert.Contains(payload["hits"]!.AsArray(), hit => Text(hit!["file"]) == BigModulePath);
    }

    [Fact]
    public async Task Слишком_короткий_шаблон_и_регулярка_ищутся_по_файлам()
    {
        using var fixture = new IndexFixture();

        // Триграммам нужно три знака: короткий шаблон индекс не подтверждает — идём по выгрузке.
        var short_ = await CallAsync(fixture.Session, """{"pattern":"ов"}""");
        Assert.Equal("scan", Text(short_["scan"]!["mode"]));
        Assert.Null(short_["scan"]!["candidates"]);
        Assert.Contains(short_["hits"]!.AsArray(), hit => Text(hit!["file"]) == BigModulePath);

        // Регулярное выражение полнотекстовый индекс не выражает: та же дорога.
        var regex = await CallAsync(fixture.Session, """{"pattern":"НайтиПоНаименов\\w+","regex":true}""");
        Assert.Equal("scan", Text(regex["scan"]!["mode"]));
        Assert.NotEmpty(regex["hits"]!.AsArray());

        // Не тот вид файлов: тела модулей тут не при чём.
        var xml = await CallAsync(fixture.Session, """{"pattern":"РаботаСТоварами","extensions":[".xml"]}""");
        Assert.Equal("scan", Text(xml["scan"]!["mode"]));
    }

    [Fact]
    public void Индекс_хранит_тела_модулей_и_обновляет_их()
    {
        var path = Path.Combine(Path.GetTempPath(), "data1c-text-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            var source = CreateDump();
            var analysis = new DumpAnalyzer().Analyze(source);
            var bothModules = new[] { BigModulePath, SmallModulePath }.Order(StringComparer.Ordinal);
            using (var index = SqliteIndex.Open(path))
            {
                new IndexWriter(index) { IncludeComments = false }.Write(source, analysis);
                var reader = new IndexReader(index);

                Assert.Equal(bothModules, reader.SearchModuleText("\"НайтиПоНаименованию\"").Order(StringComparer.Ordinal));

                // Слово из модуля поменьше: индекс отвечает и по нему, и по подстроке внутри слова.
                Assert.Equal([SmallModulePath], reader.SearchModuleText("\"ПодготовитьОтчёт\""));
                Assert.Equal([SmallModulePath], reader.SearchModuleText("\"товитьОтч\""));
                Assert.Empty(reader.SearchModuleText("\"ТакогоСловаНет\""));
            }

            // Повторная запись не удваивает строки: таблица без содержимого удаляется целиком.
            using (var index = SqliteIndex.Open(path))
            {
                new IndexWriter(index) { IncludeComments = false }.Write(source, analysis);
                var reader = new IndexReader(index);
                Assert.Equal(bothModules, reader.SearchModuleText("\"НайтиПоНаименованию\"").Order(StringComparer.Ordinal));
            }

            // Частичная переиндексация заменяет строку модуля: старых слов в индексе не остаётся.
            using (var index = SqliteIndex.Open(path))
            {
                var changed = new InMemoryDumpSource("правка")
                    .AddText("Configuration.xml", ConfigurationXml)
                    .AddText("Catalogs/Товары.xml", CatalogXml)
                    .AddText("CommonModules/РаботаСТоварами.xml", CommonModuleXml)
                    .AddText("CommonModules/Прочее.xml", OtherModuleXml)
                    .AddText(BigModulePath, "Процедура ЗагрузитьТовары() Экспорт\n    Сообщить(\"Привет\");\nКонецПроцедуры\n")
                    .AddText(SmallModulePath, SmallModuleBsl);
                var writer = new IndexWriter(index) { IncludeComments = false };
                Assert.NotNull(writer.WriteModuleFiles(changed, [BigModulePath]));

                var reader = new IndexReader(index);
                Assert.Equal([SmallModulePath], reader.SearchModuleText("\"НайтиПоНаименованию\""));
                Assert.Equal([BigModulePath], reader.SearchModuleText("\"Привет\""));
            }
        }
        finally
        {
            foreach (var file in Directory.EnumerateFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + "*"))
            {
                File.Delete(file);
            }
        }
    }

    /// <summary>Инструмент поверх сессии: ответ разбирается как JSON, как его увидит агент.</summary>
    private static async Task<JsonObject> CallAsync(AnalysisSession session, string arguments)
    {
        var tool = new ToolCatalog(session).Find("grep") ?? throw new InvalidOperationException("Нет инструмента grep.");
        var text = await tool.Execute(ToolArguments.From(JsonNode.Parse(arguments)), CancellationToken.None);
        return JsonNode.Parse(text)!.AsObject();
    }

    private static string Text(JsonNode? node) => node switch
    {
        null => string.Empty,
        JsonValue value when value.TryGetValue<string>(out var text) => text,
        _ => node.ToJsonString(),
    };

    /// <summary>Выгрузка на диске и сессия в режиме индекса: grep отвечает из SQLite.</summary>
    private sealed class IndexFixture : IDisposable
    {
        private readonly string _root;

        internal IndexFixture()
        {
            _root = TestDump.Materialize(CreateDump());
            Session = new AnalysisSession(new AnalysisRequest { DumpPaths = [_root] });
            Session.QueryAsync(CancellationToken.None).GetAwaiter().GetResult();
        }

        internal AnalysisSession Session { get; }

        public void Dispose()
        {
            Session.Dispose();
            TestDump.Remove(_root);
        }
    }

    private static InMemoryDumpSource CreateDump() =>
        new InMemoryDumpSource("выгрузка с телами модулей")
            .AddText("Configuration.xml", ConfigurationXml)
            .AddText("Catalogs/Товары.xml", CatalogXml)
            .AddText("CommonModules/РаботаСТоварами.xml", CommonModuleXml)
            .AddText("CommonModules/Прочее.xml", OtherModuleXml)
            .AddText(BigModulePath, BigModuleBsl)
            .AddText(SmallModulePath, SmallModuleBsl);

    private const string BigModuleBsl = """
        Процедура ЗагрузитьТовары() Экспорт
            // Строка ниже — цель поиска: она в обоих модулях.
            Ссылка = Справочники.Товары.НайтиПоНаименованию("Ручка");
        КонецПроцедуры

        Процедура ВызватьЗагрузку()
            ЗагрузитьТовары();
        КонецПроцедуры
        """;

    private const string SmallModuleBsl = """
        Процедура ПодготовитьОтчёт()
            Товар = Справочники.Товары.НайтиПоНаименованию("Гайка");
        КонецПроцедуры
        """;

    private const string ConfigurationXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <MetaDataObject xmlns="http://v8.1c.ru/8.3/MDClasses" version="2.20">
            <Configuration uuid="0f0f0f0f-0000-0000-0000-0000000000e1">
                <Properties>
                    <Name>ВыгрузкаСТеламиМодулей</Name>
                </Properties>
                <ChildObjects>
                    <Catalog>Товары</Catalog>
                    <CommonModule>РаботаСТоварами</CommonModule>
                    <CommonModule>Прочее</CommonModule>
                </ChildObjects>
            </Configuration>
        </MetaDataObject>
        """;

    private const string CatalogXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <MetaDataObject xmlns="http://v8.1c.ru/8.3/MDClasses" version="2.20">
            <Catalog uuid="11111111-1111-1111-1111-11111111111e">
                <Properties>
                    <Name>Товары</Name>
                </Properties>
            </Catalog>
        </MetaDataObject>
        """;

    private const string CommonModuleXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <MetaDataObject xmlns="http://v8.1c.ru/8.3/MDClasses" version="2.20">
            <CommonModule uuid="55555555-5555-5555-5555-55555555555e">
                <Properties>
                    <Name>РаботаСТоварами</Name>
                    <Server>true</Server>
                </Properties>
            </CommonModule>
        </MetaDataObject>
        """;

    private const string OtherModuleXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <MetaDataObject xmlns="http://v8.1c.ru/8.3/MDClasses" version="2.20">
            <CommonModule uuid="66666666-6666-6666-6666-66666666666e">
                <Properties>
                    <Name>Прочее</Name>
                    <Server>true</Server>
                </Properties>
            </CommonModule>
        </MetaDataObject>
        """;
}
