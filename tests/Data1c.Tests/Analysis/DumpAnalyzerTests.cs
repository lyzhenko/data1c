using Data1c.Core.Analysis;
using Data1c.Core.Bsl;
using Data1c.Core.Graph;
using Data1c.Core.Metadata;
using Xunit;

namespace Data1c.Tests.Analysis;

/// <summary>Сквозная проверка: выгрузка в памяти → метаданные → BSL → граф.</summary>
public sealed class DumpAnalyzerTests
{
    [Fact]
    public void Разбирает_выгрузку_целиком()
    {
        var result = new DumpAnalyzer().Analyze(SampleDump.Create());

        Assert.Equal(3, result.Statistics.ModuleFiles);
        Assert.Equal(3, result.Modules.Count);
        Assert.Empty(result.Warnings);
        Assert.Equal(0, result.Statistics.FailedXmlFiles);

        var procedures = result.Modules.SelectMany(static m => m.Routines).ToList();
        Assert.Contains(procedures, static r => r is { Name: "МояПроцедура", IsExport: true, Kind: BslRoutineKind.Procedure });
        Assert.Contains(procedures, static r => r is { Name: "ПриОткрытии" } && r.HasDirective("&НаКлиенте"));

        var graph = result.Graph;
        Assert.Contains(graph.Edges, static e =>
            e is { Kind: GraphEdgeKind.UsesMetadata, TargetId: "Catalog.Товары" });

        var caller = graph.Edges.First(static e =>
            e is { Kind: GraphEdgeKind.Calls, Detail: "РаботаСДанными.ЗагрузитьДанные" });
        Assert.Equal($"routine:module:{SampleDump.SecondCommonModuleBslPath}#ЗагрузитьДанные", caller.TargetId);
    }

    [Fact]
    public void Работает_без_разбора_BSL()
    {
        var result = new DumpAnalyzer().Analyze(
            SampleDump.Create(),
            new AnalysisOptions
            {
                IncludeBsl = false,
                Metadata = new MetadataReadOptions { AttachModules = false },
                Graph = new DependencyGraphOptions { IncludeModules = false },
            });

        Assert.Empty(result.Modules);
        Assert.Equal(0, result.Statistics.ModuleFiles);
        Assert.DoesNotContain(result.Graph.Nodes, static n => n.Kind == GraphNodeKind.Module);
        Assert.DoesNotContain(result.Graph.Edges, static e => e.Kind == GraphEdgeKind.Calls);
    }
}
