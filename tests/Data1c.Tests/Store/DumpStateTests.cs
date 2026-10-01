using Data1c.Core.Analysis;
using Data1c.Core.Dump;
using Data1c.Store;
using Xunit;

namespace Data1c.Tests.Store;

/// <summary>
/// Индекс обязан замечать изменения выгрузки: иначе после новой выгрузки сервер отвечал бы
/// по устаревшим данным, а агент писал бы код по старой конфигурации.
/// </summary>
public sealed class DumpStateTests
{
    [Fact]
    public void Совпадающая_выгрузка_не_считается_изменённой()
    {
        using var fixture = new StateFixture();

        var change = DumpState.Compare(fixture.Reader, fixture.Source);

        Assert.True(change.IsEmpty);
        Assert.Equal(fixture.Files, change.Total);
    }

    [Fact]
    public void Новый_файл_виден_как_добавленный()
    {
        using var fixture = new StateFixture();
        fixture.Source.AddText("CommonModules/НовыйМодуль/Ext/Module.bsl", "Процедура Тест()\nКонецПроцедуры\n");

        var change = DumpState.Compare(fixture.Reader, fixture.Source);

        Assert.Equal(1, change.Added);
        Assert.False(change.IsEmpty);
    }

    [Fact]
    public void Изменённый_файл_виден_по_размеру()
    {
        using var fixture = new StateFixture();
        fixture.Source.AddText(SampleDump.CommonModuleBslPath, "// совсем другой текст модуля");

        var change = DumpState.Compare(fixture.Reader, fixture.Source);

        Assert.Equal(1, change.Changed);
    }

    [Fact]
    public void Пропавший_файл_виден_как_удалённый()
    {
        // Индекс собран по выгрузке с лишним файлом, сравнение идёт с исходной: файл пропал.
        var extended = SampleDump.Create();
        extended.AddText("Reports/Отчёт/Ext/Module.bsl", "// временный модуль");
        using var index = SqliteIndex.OpenInMemory();
        new IndexWriter(index) { IncludeComments = false }.Write(extended, new DumpAnalyzer().Analyze(extended));

        var change = DumpState.Compare(new IndexReader(index), SampleDump.Create());

        Assert.Equal(1, change.Removed);
    }

    [Fact]
    public void Счётчики_индекса_берутся_из_meta_и_совпадают_с_данными()
    {
        using var fixture = new StateFixture();

        var statistics = fixture.Reader.GetStatistics();

        Assert.True(statistics.Nodes > 0);
        Assert.True(statistics.MetadataObjects > 0);
        Assert.Equal(fixture.Files, statistics.Files);
    }

    private sealed class StateFixture : IDisposable
    {
        private readonly SqliteIndex _index;

        internal StateFixture()
        {
            Source = SampleDump.Create();
            _index = SqliteIndex.OpenInMemory();
            new IndexWriter(_index) { IncludeComments = false }.Write(Source, new DumpAnalyzer().Analyze(Source));
            Reader = new IndexReader(_index);
            Files = Source.Count;
        }

        internal InMemoryDumpSource Source { get; }

        internal IndexReader Reader { get; }

        internal int Files { get; }

        public void Dispose() => _index.Dispose();
    }
}
