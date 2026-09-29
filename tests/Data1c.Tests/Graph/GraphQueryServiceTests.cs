using Data1c.Core.Graph;
using Data1c.Core.Metadata;
using Xunit;

namespace Data1c.Tests.Graph;

public sealed class GraphQueryServiceTests
{
    private static GraphQueryService CreateService()
    {
        var metadata = new MetadataDumpReader().Read(SampleDump.Create()).Model;
        var modules = new[]
        {
            DependencyGraphBuilderTests.CreateFirstModule(),
            DependencyGraphBuilderTests.CreateSecondModule(),
        };

        return new GraphQueryService(new DependencyGraphBuilder().Build(metadata, modules));
    }

    [Fact]
    public void Находит_объект_по_имени_и_синониму()
    {
        var service = CreateService();

        Assert.Equal("Catalog.Товары", service.Search("Товары")[0].Id);

        var bySynonym = service.Search("Номенклатура");
        Assert.Contains(bySynonym, static hit => hit.Id == "Catalog.Товары");
    }

    [Fact]
    public void Ищет_процедуры_по_имени()
    {
        var service = CreateService();

        var hits = service.Search("ЗагрузитьДанные");

        var routine = Assert.Single(hits.Where(static h => h.Kind == GraphNodeKind.Routine).ToList());
        Assert.Equal(DependencyGraphBuilderTests.RoutineId(DependencyGraphBuilderTests.SecondModulePath, "ЗагрузитьДанные"), routine.Id);
        Assert.Equal(SampleDump.SecondCommonModuleBslPath, routine.SourcePath);
    }

    [Fact]
    public void Пустой_запрос_ничего_не_возвращает()
    {
        var service = CreateService();

        Assert.Empty(service.Search(null));
        Assert.Empty(service.Search("   "));
    }

    [Fact]
    public void Возвращает_окружение_вызываемой_процедуры()
    {
        var service = CreateService();
        var callee = DependencyGraphBuilderTests.RoutineId(DependencyGraphBuilderTests.SecondModulePath, "ЗагрузитьДанные");

        var neighborhood = service.GetNeighborhood(new GraphNeighborhoodRequest { NodeId = callee, Depth = 1, Incoming = true, Outgoing = false });

        var caller = DependencyGraphBuilderTests.RoutineId(DependencyGraphBuilderTests.FirstModulePath, "МояПроцедура");
        Assert.Contains(neighborhood.Nodes, n => n.Id == caller);
        Assert.Contains(neighborhood.Edges, e => e.Kind == GraphEdgeKind.Calls && e.SourceId == caller && e.TargetId == callee);
        Assert.False(neighborhood.Truncated);
    }

    [Fact]
    public void Учитывает_направление_связей()
    {
        var service = CreateService();
        var callee = DependencyGraphBuilderTests.RoutineId(DependencyGraphBuilderTests.SecondModulePath, "ЗагрузитьДанные");

        var outgoingOnly = service.GetNeighborhood(new GraphNeighborhoodRequest
        {
            NodeId = callee,
            Depth = 1,
            Incoming = false,
            Outgoing = true,
            NodeKinds = [GraphNodeKind.Routine, GraphNodeKind.Module, GraphNodeKind.MetadataObject],
        });

        Assert.Single(outgoingOnly.Nodes);
        Assert.DoesNotContain(outgoingOnly.Nodes, n => n.Name == "МояПроцедура");
    }

    [Fact]
    public void Фильтрует_по_типу_связей()
    {
        var service = CreateService();
        var routine = DependencyGraphBuilderTests.RoutineId(DependencyGraphBuilderTests.FirstModulePath, "ДругаяПроцедура");

        var onlyCalls = service.GetNeighborhood(new GraphNeighborhoodRequest
        {
            NodeId = routine,
            Depth = 1,
            EdgeKinds = [GraphEdgeKind.Calls],
        });

        Assert.All(onlyCalls.Edges, static e => Assert.Equal(GraphEdgeKind.Calls, e.Kind));
        Assert.DoesNotContain(onlyCalls.Nodes, static n => n.Id == "Catalog.Товары");
    }

    [Fact]
    public void Ограничивает_число_узлов_и_сообщает_об_усечении()
    {
        var service = CreateService();

        var neighborhood = service.GetNeighborhood(new GraphNeighborhoodRequest
        {
            NodeId = "Configuration",
            Depth = 8,
            MaxNodes = 3,
        });

        Assert.True(neighborhood.Truncated);
        Assert.True(neighborhood.Nodes.Count <= 3);
    }

    [Fact]
    public void Центр_показывается_даже_если_его_тип_исключён_фильтром()
    {
        var service = CreateService();

        var neighborhood = service.GetNeighborhood(new GraphNeighborhoodRequest
        {
            NodeId = "Configuration",
            Depth = 1,
            NodeKinds = [GraphNodeKind.MetadataObject],
        });

        Assert.Contains(neighborhood.Nodes, static n => n.Id == "Configuration");
        Assert.All(neighborhood.Edges, static e => Assert.Equal(GraphEdgeKind.Contains, e.Kind));
    }

    [Fact]
    public void Возвращает_карточку_узла_со_связями()
    {
        var service = CreateService();
        var caller = DependencyGraphBuilderTests.RoutineId(DependencyGraphBuilderTests.FirstModulePath, "МояПроцедура");

        var details = service.GetNode(caller);

        Assert.NotNull(details);
        Assert.Equal("МояПроцедура", details.Node.Name);
        Assert.Equal(2, details.Outgoing.Count(static e => e.Kind == GraphEdgeKind.Calls));
        Assert.Contains(details.Incoming, static e => e.Kind == GraphEdgeKind.Defines);
        Assert.Null(service.GetNode("нет-такого-узла"));
    }

    [Fact]
    public void Находит_узлы_по_ссылке_из_командной_строки()
    {
        var service = CreateService();

        var byId = service.Resolve("Catalog.Товары");
        Assert.Equal("Catalog.Товары", byId[0].Id);

        var byName = service.Resolve("ЗагрузитьДанные");
        Assert.Contains(byName, static n => n.Kind == GraphNodeKind.Routine);
    }
}
