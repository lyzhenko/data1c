using Data1c.Core.Analysis;
using Data1c.Core.Dump;
using Data1c.Core.Graph;
using Data1c.Store;
using Xunit;

namespace Data1c.Tests.Store;

/// <summary>
/// Обращения к метаданным из текстов запросов (Э2-1): попадают в <c>metadata_refs</c> с контекстом
/// «query», не смешиваются с обращениями из кода и переживают частичную переиндексацию.
/// </summary>
public sealed class QueryMetadataRefTests
{
    private const string ModulePath = "CommonModules/РаботаСЗапросами/Ext/Module.bsl";
    private const string RoutineId = "routine:module:" + ModulePath + "#ЗагрузитьТовары";

    [Fact]
    public void Запрос_попадает_в_metadata_refs_с_контекстом_query()
    {
        using var fixture = new QueryFixture();

        var usage = Assert.Single(fixture.Reader.Usages("Catalog.Номенклатура", context: MetadataRefContexts.Query));
        Assert.Equal(RoutineId, usage.SourceId);
        Assert.Equal("query", usage.Context);
        Assert.Equal(3, usage.Line);
        Assert.Equal("ИЗ Справочник.Номенклатура КАК Т", usage.Detail);

        // Обращение из кода остаётся в своём контексте: это строка 2, а не строка запроса.
        var code = Assert.Single(fixture.Reader.Usages("Catalog.Номенклатура", context: MetadataRefContexts.Code));
        Assert.Equal(2, code.Line);
        Assert.StartsWith("Справочники.Номенклатура", code.Detail, StringComparison.Ordinal);

        // Та же связь видна в графе индекса: по ней ходят инструменты обхода связей.
        var edges = fixture.Reader.Outgoing(RoutineId, "UsesMetadata", 10);
        Assert.Contains(edges, static edge => edge.TargetId == "Catalog.Номенклатура");
    }

    [Fact]
    public void Частичная_переиндексация_сохраняет_обращения_из_запросов()
    {
        using var fixture = new QueryFixture();

        fixture.Source.AddText(ModulePath, ChangedModule);
        var written = new IndexWriter(fixture.Index) { IncludeComments = false }.WriteModuleFiles(fixture.Source, [ModulePath]);

        Assert.NotNull(written);
        Assert.True(written.MetadataRefs > 0);

        var usage = Assert.Single(fixture.Reader.Usages("Catalog.Номенклатура", context: MetadataRefContexts.Query));
        Assert.Equal(RoutineId, usage.SourceId);
        Assert.Equal(2, usage.Line);

        // Область перезаписи удаляется вместе со своими обращениями: от старого кода ничего не осталось.
        Assert.Empty(fixture.Reader.Usages("Catalog.Номенклатура", context: MetadataRefContexts.Code));
    }

    /// <summary>Минимальная выгрузка: конфигурация, справочник и общий модуль с текстом запроса.</summary>
    private static InMemoryDumpSource CreateDump() =>
        new InMemoryDumpSource("выгрузка с запросом")
            .AddText("Configuration.xml", ConfigurationXml)
            .AddText("Catalogs/Номенклатура.xml", CatalogXml)
            .AddText("CommonModules/РаботаСЗапросами.xml", CommonModuleXml)
            .AddText(ModulePath, QueryModule);

    private const string QueryModule = """
        Процедура ЗагрузитьТовары() Экспорт
            Ссылка = Справочники.Номенклатура.НайтиПоНаименованию("Ручка");
            Текст = "ВЫБРАТЬ * ИЗ Справочник.Номенклатура КАК Т";
            Результат = Новый Запрос(Текст).Выполнить();
        КонецПроцедуры
        """;

    private const string ChangedModule = """
        Процедура ЗагрузитьТовары() Экспорт
            Текст = "ВЫБРАТЬ * ИЗ Справочник.Номенклатура КАК Т";
        КонецПроцедуры
        """;

    private const string ConfigurationXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <MetaDataObject xmlns="http://v8.1c.ru/8.3/MDClasses" version="2.20">
            <Configuration uuid="0f0f0f0f-0000-0000-0000-0000000000a1">
                <Properties>
                    <Name>ВыгрузкаСЗапросами</Name>
                </Properties>
                <ChildObjects>
                    <Catalog>Номенклатура</Catalog>
                    <CommonModule>РаботаСЗапросами</CommonModule>
                </ChildObjects>
            </Configuration>
        </MetaDataObject>
        """;

    private const string CatalogXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <MetaDataObject xmlns="http://v8.1c.ru/8.3/MDClasses" version="2.20">
            <Catalog uuid="11111111-1111-1111-1111-11111111111a">
                <Properties>
                    <Name>Номенклатура</Name>
                </Properties>
            </Catalog>
        </MetaDataObject>
        """;

    private const string CommonModuleXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <MetaDataObject xmlns="http://v8.1c.ru/8.3/MDClasses" version="2.20">
            <CommonModule uuid="66666666-6666-6666-6666-66666666666a">
                <Properties>
                    <Name>РаботаСЗапросами</Name>
                    <Server>true</Server>
                </Properties>
            </CommonModule>
        </MetaDataObject>
        """;

    private sealed class QueryFixture : IDisposable
    {
        private readonly SqliteIndex _index;

        internal QueryFixture()
        {
            Source = CreateDump();
            _index = SqliteIndex.OpenInMemory();
            new IndexWriter(_index) { IncludeComments = false }.Write(Source, new DumpAnalyzer().Analyze(Source));
            Reader = new IndexReader(_index);
        }

        internal InMemoryDumpSource Source { get; }

        internal IndexReader Reader { get; }

        internal SqliteIndex Index => _index;

        public void Dispose() => _index.Dispose();
    }
}
