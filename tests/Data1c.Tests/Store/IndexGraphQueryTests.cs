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

    [Fact]
    public void Карточка_объекта_берётся_из_индекса_с_составом_и_типами()
    {
        using var fixture = new QueryFixture();

        var card = fixture.Query.GetMetadata("Catalog.Товары", depth: 3, maxChildren: 50);

        Assert.NotNull(card);
        Assert.Equal("Catalog", card.Kind);
        Assert.Equal("Товары", card.Name);
        Assert.True(card.IsTopLevel);
        Assert.Null(card.ParentId);
        Assert.Equal("Catalogs/Товары.xml", card.SourcePath);

        // Реквизиты объекта лежат в составе: имя, вид и тип значения.
        var article = Assert.Single(card.Children, static child => child.Name == "Артикул");
        Assert.Equal("Attribute", article.Kind);
        Assert.Equal("Catalog.Товары/Attribute.Артикул", article.Id);

        var unit = Assert.Single(card.Children, static child => child.Name == "Единица");
        Assert.Contains("Catalog.ЕдиницыИзмерения", unit.Types);

        // Модули — только файлы BSL: XML форм и макетов в этом списке быть не должно.
        Assert.All(card.ModulePaths, static path => Assert.EndsWith(".bsl", path));
    }

    [Fact]
    public void Вложенный_объект_находится_по_составному_идентификатору()
    {
        using var fixture = new QueryFixture();

        var card = fixture.Query.GetMetadata("Catalog.Товары/Attribute.Единица");

        Assert.NotNull(card);
        Assert.Equal("Attribute", card.Kind);
        Assert.Equal("Catalog.Товары", card.ParentId);
        Assert.Contains("Catalog.ЕдиницыИзмерения", card.Types);
    }

    [Fact]
    public void Подстрочный_поиск_находит_середину_имени()
    {
        using var fixture = new QueryFixture();

        // «овар» не является началом имени: работает только путь по подстроке.
        var hits = fixture.Query.Search("овар", 10);

        Assert.Contains(hits, static hit => hit.Id == "Catalog.Товары");
    }

    [Fact]
    public void Точное_совпадение_идёт_раньше_подстрочного()
    {
        using var fixture = new QueryFixture();

        var hits = fixture.Query.Search("Товары", 10);

        Assert.NotEmpty(hits);
        Assert.Equal("Catalog.Товары", hits[0].Id);
    }

    [Fact]
    public void Поиск_символов_находит_середину_имени()
    {
        using var fixture = new QueryFixture();

        var symbols = fixture.Reader.FindSymbols("агрузить", 10);

        Assert.Contains(symbols, static symbol => symbol.Name == "ЗагрузитьДанные");
    }

    [Fact]
    public void Смысловой_поиск_находит_процедуру_по_имени_параметра()
    {
        using var fixture = new QueryFixture();

        // В имени процедуры нет слова «Отказ»: оно есть только среди параметров.
        var symbols = fixture.Reader.SmartSearch("Отказ", 5);

        Assert.Contains(symbols, static symbol => symbol.Name == "ПриОткрытии");
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

        internal IndexReader Reader => new(_index);

        internal int Nodes { get; }

        public void Dispose() => _index.Dispose();
    }
}
