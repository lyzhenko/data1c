using Data1c.Core.Analysis;
using Data1c.Core.Dump;
using Data1c.Core.Graph;
using Data1c.Store;
using Xunit;

namespace Data1c.Tests.Store;

/// <summary>
/// Проверяет, что запросы к графу поверх индекса дают то же, что граф в памяти: узлы, связи,
/// вложенные объекты (реквизиты и табличные части) и синонимы.
/// </summary>
public sealed class IndexGraphQueryTests
{
    [Fact]
    public void Ищет_узлы_и_вложенные_объекты_из_индекса()
    {
        using var fixture = new QueryFixture();
        var query = fixture.Query;

        var hits = query.Search("Товары", 5);
        Assert.Contains(hits, static hit => hit.Id == "Catalog.Товары" && hit.Kind == GraphNodeKind.MetadataObject);

        var nested = query.SearchNested("Артикул", 5);
        var article = Assert.Single(nested, static hit => hit.Id == "Catalog.Товары/Attribute.Артикул");
        Assert.Equal("Attribute", article.Kind);
        Assert.Equal("Catalog.Товары", article.ObjectId);

        // Тип реквизита «Единица» — ссылка на справочник, которого нет в выгрузке.
        var unit = Assert.Single(query.SearchNested("Единица", 5), static hit => hit.Name == "Единица");
        Assert.Contains("Catalog.ЕдиницыИзмерения", unit.Types);
    }

    [Fact]
    public void Вложенный_поиск_уважает_фильтр_по_видам()
    {
        using var fixture = new QueryFixture();

        Assert.NotEmpty(fixture.Query.SearchNested("Артикул", 5, ["Attribute"]));
        Assert.Empty(fixture.Query.SearchNested("Артикул", 5, ["Form"]));
    }

    [Fact]
    public void Карточка_узла_и_статистика_берутся_из_индекса()
    {
        using var fixture = new QueryFixture();

        var node = fixture.Query.FindNode("Catalog.Товары");
        Assert.NotNull(node);
        Assert.Equal("Товары", node.Name);

        var details = fixture.Query.GetNode("Catalog.Товары");
        Assert.NotNull(details);
        Assert.NotEmpty(details.Outgoing);

        var statistics = fixture.Query.Statistics;
        Assert.True(statistics.NodeCount > 0);
        Assert.Equal(fixture.Nodes, statistics.NodesByKind.GetValueOrDefault("MetadataObject"));
    }

    private sealed class QueryFixture : IDisposable
    {
        private readonly SqliteIndex _index;

        internal QueryFixture()
        {
            var source = SampleDump.Create();
            var result = new DumpAnalyzer().Analyze(source);
            _index = SqliteIndex.OpenInMemory();
            new IndexWriter(_index) { IncludeComments = false }.Write(source, result);
            Query = new IndexGraphQuery(new IndexReader(_index));
            Nodes = result.Graph.Nodes.Count(static node => node.Kind == GraphNodeKind.MetadataObject);
        }

        internal IndexGraphQuery Query { get; }

        internal int Nodes { get; }

        public void Dispose() => _index.Dispose();
    }
}
