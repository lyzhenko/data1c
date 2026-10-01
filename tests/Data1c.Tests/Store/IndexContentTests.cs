using Data1c.Core.Analysis;
using Data1c.Store;
using Xunit;

namespace Data1c.Tests.Store;

/// <summary>
/// Защита содержимого индекса (Э1-7): счётчики обязаны совпадать с данными, а связи — не теряться.
/// Тест ловит случайную потерю или дублирование строк при изменениях схемы и записи.
/// </summary>
public sealed class IndexContentTests
{
    [Fact]
    public void Счётчики_совпадают_с_данными_таблиц()
    {
        using var fixture = new ContentFixture();

        var statistics = fixture.Reader.GetStatistics();

        Assert.Equal(fixture.Written.Nodes, (int)statistics.Nodes);
        Assert.Equal(fixture.Written.Edges, (int)statistics.Edges);
        Assert.Equal(fixture.Written.Symbols, (int)statistics.Symbols);
        Assert.Equal(fixture.Written.Calls, (int)statistics.Calls);
        Assert.Equal(fixture.Written.MetadataObjects, (int)statistics.MetadataObjects);
        Assert.Equal(fixture.Written.MetadataItems, (int)statistics.MetadataItems);
        Assert.Equal(fixture.Written.MetadataRefs, (int)statistics.MetadataRefs);
        Assert.Equal(fixture.Written.Files, (int)statistics.Files);
    }

    [Fact]
    public void Разбивка_по_видам_покрывает_все_узлы_и_связи()
    {
        using var fixture = new ContentFixture();

        var statistics = fixture.Reader.GetStatistics();
        var nodesByKind = fixture.Reader.CountNodesByKind();
        var edgesByKind = fixture.Reader.CountEdgesByKind();

        Assert.Equal((int)statistics.Nodes, nodesByKind.Values.Sum());
        Assert.Equal((int)statistics.Edges, edgesByKind.Values.Sum());
        Assert.Equal((int)statistics.Calls, edgesByKind.GetValueOrDefault("Calls"));
    }

    [Fact]
    public void У_каждого_символа_есть_узел_а_у_процедуры_объявление()
    {
        using var fixture = new ContentFixture();

        foreach (var module in fixture.Analysis.Modules)
        {
            var symbols = fixture.Reader.FindSymbolsInModule(module.Path);
            Assert.Equal(module.Routines.Count, symbols.Count);

            foreach (var symbol in symbols)
            {
                Assert.NotNull(fixture.Reader.GetNode(symbol.NodeId));

                // Процедура объявлена в модуле: связь Defines от узла модуля.
                var incoming = fixture.Reader.Incoming(symbol.NodeId, "Defines", 5);
                Assert.Single(incoming);
                Assert.Equal("module:" + module.Path, incoming[0].SourceId);
            }

            // Модуль принадлежит своему объекту метаданных: связь Contains от владельца.
            if (module.OwnerId is { Length: > 0 } owner)
            {
                var owners = fixture.Reader.Incoming("module:" + module.Path, "Contains", 5);
                Assert.Contains(owners, edge => edge.SourceId == owner);
            }
        }
    }

    [Fact]
    public void Обращения_к_метаданным_ведут_в_существующие_объекты()
    {
        using var fixture = new ContentFixture();

        var usages = fixture.Reader.Usages("Catalog.Товары", 50);

        Assert.NotEmpty(usages);
        Assert.All(usages, static usage => Assert.False(string.IsNullOrWhiteSpace(usage.SourceId)));
        Assert.NotNull(fixture.Reader.GetNode("Catalog.Товары"));
    }

    private sealed class ContentFixture : IDisposable
    {
        private readonly SqliteIndex _index;

        internal ContentFixture()
        {
            var source = SampleDump.Create();
            Analysis = new DumpAnalyzer().Analyze(source);
            _index = SqliteIndex.OpenInMemory();
            Written = new IndexWriter(_index) { IncludeComments = false }.Write(source, Analysis);
            Reader = new IndexReader(_index);
        }

        internal AnalysisResult Analysis { get; }

        internal IndexWriteResult Written { get; }

        internal IndexReader Reader { get; }

        public void Dispose() => _index.Dispose();
    }
}
