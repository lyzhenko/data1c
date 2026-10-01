using Data1c.Core.Analysis;
using Data1c.Core.Dump;
using Data1c.Store;
using Xunit;

namespace Data1c.Tests.Store;

/// <summary>
/// Частичная переиндексация модулей: файлы разбираются напрямую, метаданные из XML не читаются.
/// Главная проверка — результат обязан совпасть с полной сборкой той же выгрузки.
/// </summary>
public sealed class PartialReindexTests
{
    /// <summary>Модуль с локальным вызовом, вызовом другого модуля и обращением к метаданным.</summary>
    private const string ChangedModule = """
        Процедура МояПроцедура() Экспорт
            ДругаяПроцедура();
            РаботаСДанными.ЗагрузитьДанные();
            Ссылка = Справочники.Товары.НайтиПоНаименованию("x");
        КонецПроцедуры

        Процедура ДругаяПроцедура()
        КонецПроцедуры

        Функция НоваяФункцияЧастичнойПереиндексации() Экспорт
            Возврат 1;
        КонецФункции
        """;

    [Fact]
    public void Обновляет_изменённый_модуль_и_не_трогает_остальные()
    {
        using var fixture = new PartialFixture();
        var otherModuleSymbols = fixture.Reader.FindSymbolsInModule(SampleDump.SecondCommonModuleBslPath).Count;

        fixture.Source.AddText(SampleDump.CommonModuleBslPath, ChangedModule);
        var written = fixture.Reindex(SampleDump.CommonModuleBslPath);

        Assert.NotNull(written);
        Assert.True(written.Symbols > 0);
        Assert.NotEmpty(fixture.Reader.FindSymbols("НоваяФункцияЧастичнойПереиндексации", exact: true));
        Assert.Equal(otherModuleSymbols, fixture.Reader.FindSymbolsInModule(SampleDump.SecondCommonModuleBslPath).Count);
    }

    [Fact]
    public void Связывает_вызов_с_процедурой_другого_модуля()
    {
        using var fixture = new PartialFixture();
        fixture.Source.AddText(SampleDump.CommonModuleBslPath, ChangedModule);

        fixture.Reindex(SampleDump.CommonModuleBslPath);

        var caller = "routine:module:" + SampleDump.CommonModuleBslPath + "#МояПроцедура";
        var target = "routine:module:" + SampleDump.SecondCommonModuleBslPath + "#ЗагрузитьДанные";
        Assert.Contains(target, fixture.Reader.Callees(caller, 50));
    }

    [Fact]
    public void Связывает_обращение_к_метаданным_с_объектом()
    {
        using var fixture = new PartialFixture();
        fixture.Source.AddText(SampleDump.CommonModuleBslPath, ChangedModule);

        fixture.Reindex(SampleDump.CommonModuleBslPath);

        var caller = "routine:module:" + SampleDump.CommonModuleBslPath + "#МояПроцедура";
        var outgoing = fixture.Reader.Outgoing(caller, "UsesMetadata", 50);
        Assert.Contains(outgoing, static edge => edge.TargetId == "Catalog.Товары");
    }

    [Fact]
    public void Совпадает_с_полной_сборкой()
    {
        var source = SampleDump.Create();
        using var partial = SqliteIndex.OpenInMemory();
        new IndexWriter(partial) { IncludeComments = false }.Write(source, new DumpAnalyzer().Analyze(source));

        // Правим модуль и обновляем только его: метаданные при этом не перечитываются.
        source.AddText(SampleDump.CommonModuleBslPath, ChangedModule);
        var written = new IndexWriter(partial) { IncludeComments = false }.WriteModuleFiles(source, [SampleDump.CommonModuleBslPath]);
        Assert.NotNull(written);

        // Эталон — полная сборка той же выгрузки в отдельный индекс.
        using var full = SqliteIndex.OpenInMemory();
        new IndexWriter(full) { IncludeComments = false }.Write(source, new DumpAnalyzer().Analyze(source));

        var partialReader = new IndexReader(partial);
        var fullReader = new IndexReader(full);

        // Разбивка по видам идёт первой: она показывает, что именно разошлось.
        Assert.Equal(fullReader.CountNodesByKind(), partialReader.CountNodesByKind());
        Assert.Equal(fullReader.CountEdgesByKind(), partialReader.CountEdgesByKind());

        var partialStatistics = partialReader.GetStatistics();
        var fullStatistics = fullReader.GetStatistics();
        Assert.Equal(fullStatistics.Nodes, partialStatistics.Nodes);
        Assert.Equal(fullStatistics.Edges, partialStatistics.Edges);
        Assert.Equal(fullStatistics.Symbols, partialStatistics.Symbols);
        Assert.Equal(fullStatistics.Calls, partialStatistics.Calls);

        AssertSameSymbols(partialReader, fullReader, SampleDump.CommonModuleBslPath);
        AssertSameSymbols(partialReader, fullReader, SampleDump.SecondCommonModuleBslPath);

        // Узлы изменённого модуля и его процедур совпадают по составу связей.
        var moduleId = "module:" + SampleDump.CommonModuleBslPath;
        AssertSameNeighborhood(partialReader, fullReader, moduleId);
        foreach (var symbol in fullReader.FindSymbolsInModule(SampleDump.CommonModuleBslPath))
        {
            AssertSameNeighborhood(partialReader, fullReader, symbol.NodeId);
        }
    }

    private static void AssertSameSymbols(IndexReader partial, IndexReader full, string modulePath)
    {
        static string[] View(IReadOnlyList<SymbolRow> rows) =>
            [.. rows
                .Select(static row => $"{row.Name}|{row.Kind}|{row.IsExport}|{row.StartLine}-{row.EndLine}|{row.Region}|{row.OwnerId}")
                .OrderBy(static text => text, StringComparer.Ordinal)];

        Assert.Equal(View(full.FindSymbolsInModule(modulePath)), View(partial.FindSymbolsInModule(modulePath)));
    }

    private static void AssertSameNeighborhood(IndexReader partial, IndexReader full, string nodeId)
    {
        static string[] View(IndexReader reader, string id)
        {
            Assert.NotNull(reader.GetNode(id));
            return
            [
                .. reader.Incoming(id, kind: null, limit: 500)
                    .Select(static edge => $"in|{edge.SourceId}|{edge.Kind}|{edge.Line}|{edge.Detail}")
                    .Concat(reader.Outgoing(id, kind: null, limit: 500)
                        .Select(static edge => $"out|{edge.TargetId}|{edge.Kind}|{edge.Line}|{edge.Detail}"))
                    .OrderBy(static text => text, StringComparer.Ordinal)
            ];
        }

        Assert.Equal(View(full, nodeId), View(partial, nodeId));
    }

    private sealed class PartialFixture : IDisposable
    {
        private readonly SqliteIndex _index;
        private readonly IndexWriter _writer;

        internal PartialFixture()
        {
            Source = SampleDump.Create();
            _index = SqliteIndex.OpenInMemory();
            _writer = new IndexWriter(_index) { IncludeComments = false };
            _writer.Write(Source, new DumpAnalyzer().Analyze(Source));
            Reader = new IndexReader(_index);
        }

        internal InMemoryDumpSource Source { get; }

        internal IndexReader Reader { get; }

        internal IndexWriteResult? Reindex(params string[] modules) =>
            _writer.WriteModuleFiles(Source, modules);

        public void Dispose() => _index.Dispose();
    }
}
