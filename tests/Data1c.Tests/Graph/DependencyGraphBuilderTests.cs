using Data1c.Core.Bsl;
using Data1c.Core.Graph;
using Data1c.Core.Metadata;
using Xunit;

namespace Data1c.Tests.Graph;

public sealed class DependencyGraphBuilderTests
{
    internal const string FirstModulePath = SampleDump.CommonModuleBslPath;
    internal const string SecondModulePath = SampleDump.SecondCommonModuleBslPath;

    internal static string RoutineId(string modulePath, string name) => $"routine:module:{modulePath}#{name}";

    [Fact]
    public void Строит_вложенность_и_ссылки_метаданных()
    {
        var metadata = new MetadataDumpReader().Read(SampleDump.Create()).Model;
        var graph = new DependencyGraphBuilder().Build(metadata, []);

        Assert.Contains(graph.Edges, static e =>
            e is { Kind: GraphEdgeKind.Contains, SourceId: "Configuration", TargetId: "Catalog.Товары" });

        Assert.Contains(graph.Edges, static e =>
            e is { Kind: GraphEdgeKind.Contains, SourceId: "Catalog.Товары", TargetId: "Catalog.Товары/Form.ФормаЭлемента" });

        Assert.Contains(graph.Edges, static e =>
            e is { Kind: GraphEdgeKind.References, SourceId: "Subsystem.Продажи", TargetId: "Catalog.Товары" });

        // Тип реквизита «Единица» ссылается на отсутствующий в выгрузке справочник:
        // ссылка приписывается самому справочнику «Товары», а цель помечается внешней.
        var typeEdge = graph.Edges.First(static e =>
            e is { Kind: GraphEdgeKind.References, SourceId: "Catalog.Товары", TargetId: "Catalog.ЕдиницыИзмерения" });
        Assert.Contains("Единица", typeEdge.Detail, StringComparison.Ordinal);

        Assert.True(graph.TryGetNode("Catalog.ЕдиницыИзмерения", out var externalNode));
        Assert.True(externalNode.IsExternal);

        // Реквизиты и табличные части в граф по умолчанию не попадают.
        Assert.False(graph.TryGetNode("Catalog.Товары/Attribute.Артикул", out _));
    }

    [Fact]
    public void Разрешает_вызовы_между_общими_модулями()
    {
        var metadata = new MetadataDumpReader().Read(SampleDump.Create()).Model;
        var modules = new[] { CreateFirstModule(), CreateSecondModule() };
        var graph = new DependencyGraphBuilder().Build(metadata, modules);

        var callerId = RoutineId(FirstModulePath, "МояПроцедура");
        var calleeId = RoutineId(SecondModulePath, "ЗагрузитьДанные");

        Assert.True(graph.TryGetNode(callerId, out _));
        Assert.True(graph.TryGetNode(calleeId, out _));

        Assert.Contains(graph.Edges, e =>
            e.Kind == GraphEdgeKind.Calls && e.SourceId == callerId && e.TargetId == calleeId);

        var localCall = graph.Edges.First(static e =>
            e is { Kind: GraphEdgeKind.Calls, Detail: "ДругаяПроцедура" });
        Assert.Equal(RoutineId(FirstModulePath, "ДругаяПроцедура"), localCall.TargetId);

        Assert.Contains(callerId, graph.Callers(calleeId));
        Assert.Contains(calleeId, graph.Callees(callerId));
    }

    [Fact]
    public void Связывает_обращения_к_метаданным_с_объектами()
    {
        var metadata = new MetadataDumpReader().Read(SampleDump.Create()).Model;
        var graph = new DependencyGraphBuilder().Build(metadata, [CreateFirstModule()]);

        var accessEdge = graph.Edges.First(static e =>
            e is { Kind: GraphEdgeKind.UsesMetadata, TargetId: "Catalog.Товары" });

        Assert.Equal("routine:module:" + FirstModulePath + "#ДругаяПроцедура", accessEdge.SourceId);
        Assert.Equal(7, accessEdge.Line);
    }

    [Fact]
    public void Связывает_модуль_с_владельцем_и_объявляет_процедуры()
    {
        var metadata = new MetadataDumpReader().Read(SampleDump.Create()).Model;
        var graph = new DependencyGraphBuilder().Build(metadata, [CreateFirstModule()]);

        var moduleId = "module:" + FirstModulePath;
        Assert.Contains(graph.Edges, e =>
            e is { Kind: GraphEdgeKind.Contains, SourceId: "CommonModule.ОбщегоНазначения" } && e.TargetId == moduleId);

        Assert.Contains(graph.Edges, e =>
            e is { Kind: GraphEdgeKind.Defines } && e.SourceId == moduleId &&
            e.TargetId == RoutineId(FirstModulePath, "МояПроцедура"));
    }

    [Fact]
    public void Связывает_таблицы_из_запросов_с_объектами_метаданных()
    {
        var metadata = new MetadataDumpReader().Read(SampleDump.Create()).Model;
        var module = new BslModuleParser().Parse(new BslModuleSource(
            FirstModulePath,
            """
            Процедура Запрос()
                Ссылка = Справочники.Товары.НайтиПоНаименованию("x");
                Текст = "ВЫБРАТЬ * ИЗ Справочник.Товары КАК Т
                        |ЛЕВОЕ СОЕДИНЕНИЕ Документ.ЗаказКлиента КАК З ПО Т.Ссылка = З.Товар";
            КонецПроцедуры
            """,
            "CommonModule.ОбщегоНазначения",
            BslModuleKind.CommonModule));

        var graph = new DependencyGraphBuilder().Build(metadata, [module]);
        var routineId = "routine:module:" + FirstModulePath + "#Запрос";

        var queryEdge = graph.Edges.First(static e => e is
        {
            Kind: GraphEdgeKind.UsesMetadata,
            Context: MetadataRefContexts.Query,
            TargetId: "Catalog.Товары",
        });

        Assert.Equal(routineId, queryEdge.SourceId);
        Assert.Equal(3, queryEdge.Line);
        Assert.Equal("ИЗ Справочник.Товары КАК Т", queryEdge.Detail);

        // Обращение из кода остаётся в своём контексте: он не смешивается с текстом запроса.
        var codeEdge = graph.Edges.First(static e => e is
        {
            Kind: GraphEdgeKind.UsesMetadata,
            Context: MetadataRefContexts.Code,
            TargetId: "Catalog.Товары",
        });
        Assert.Equal(2, codeEdge.Line);

        // Таблица соединения не попала в выгрузку: ей соответствует внешний узел.
        var joinEdge = graph.Edges.First(static e => e is
        {
            Kind: GraphEdgeKind.UsesMetadata,
            Context: MetadataRefContexts.Query,
            TargetId: "Document.ЗаказКлиента",
        });
        Assert.Equal(4, joinEdge.Line);
        Assert.Equal("ЛЕВОЕ СОЕДИНЕНИЕ Документ.ЗаказКлиента КАК З", joinEdge.Detail);
        Assert.True(graph.TryGetNode("Document.ЗаказКлиента", out var external));
        Assert.True(external.IsExternal);
    }

    [Fact]
    public void Запрос_в_коде_модуля_связывается_с_модулем_и_отключается_настройкой()
    {
        var metadata = new MetadataDumpReader().Read(SampleDump.Create()).Model;
        var module = new BslModuleParser().Parse(new BslModuleSource(
            FirstModulePath,
            "Запрос = Новый Запрос(\"ВЫБРАТЬ * ИЗ Справочник.Товары КАК Т\");",
            "CommonModule.ОбщегоНазначения",
            BslModuleKind.CommonModule));

        var graph = new DependencyGraphBuilder().Build(metadata, [module]);
        Assert.Contains(graph.Edges, static e => e is
        {
            Kind: GraphEdgeKind.UsesMetadata,
            Context: MetadataRefContexts.Query,
            SourceId: "module:" + FirstModulePath,
            TargetId: "Catalog.Товары",
            Line: 1,
        });

        var withoutQueries = new DependencyGraphBuilder().Build(
            metadata,
            [module],
            new DependencyGraphOptions { IncludeQueryReferences = false });

        Assert.DoesNotContain(withoutQueries.Edges, static e => e.Context == MetadataRefContexts.Query);
    }

    [Fact]
    public void Учитывает_настройки_построения()
    {
        var metadata = new MetadataDumpReader().Read(SampleDump.Create()).Model;
        var graph = new DependencyGraphBuilder().Build(
            metadata,
            [CreateFirstModule()],
            new DependencyGraphOptions { IncludeCalls = false, IncludeExternalNodes = false });

        Assert.DoesNotContain(graph.Edges, static e => e.Kind == GraphEdgeKind.Calls);
        Assert.DoesNotContain(graph.Nodes, static n => n.Kind == GraphNodeKind.External);
    }

    internal static BslModuleInfo CreateFirstModule() => new()
    {
        Path = FirstModulePath,
        OwnerId = "CommonModule.ОбщегоНазначения",
        Kind = BslModuleKind.CommonModule,
        LineCount = 9,
        Routines =
        [
            new BslRoutine(
                "МояПроцедура",
                BslRoutineKind.Procedure,
                IsExport: true,
                Parameters: [],
                StartLine: 1,
                EndLine: 4,
                Depth: 0,
                Region: null,
                Directives: [],
                Calls:
                [
                    new BslCall("РаботаСДанными.ЗагрузитьДанные", "РаботаСДанными", "ЗагрузитьДанные", 2),
                    new BslCall("ДругаяПроцедура", null, "ДругаяПроцедура", 3),
                ],
                MetadataAccesses: []),
            new BslRoutine(
                "ДругаяПроцедура",
                BslRoutineKind.Procedure,
                IsExport: false,
                Parameters: [],
                StartLine: 6,
                EndLine: 8,
                Depth: 0,
                Region: null,
                Directives: [],
                Calls: [],
                MetadataAccesses:
                [
                    new BslMetadataAccess(MdKind.Catalog, "Товары", "Справочники", 7, "Справочники.Товары"),
                ]),
        ],
    };

    internal static BslModuleInfo CreateSecondModule() => new()
    {
        Path = SecondModulePath,
        OwnerId = "CommonModule.РаботаСДанными",
        Kind = BslModuleKind.CommonModule,
        LineCount = 3,
        Routines =
        [
            new BslRoutine(
                "ЗагрузитьДанные",
                BslRoutineKind.Procedure,
                IsExport: true,
                Parameters: [],
                StartLine: 1,
                EndLine: 3,
                Depth: 0,
                Region: null,
                Directives: [],
                Calls: [],
                MetadataAccesses: []),
        ],
    };
}
