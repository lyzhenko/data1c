using Data1c.Core.Bsl;
using Data1c.Core.Graph;
using Data1c.Core.Metadata;
using Data1c.Core.Platform;
using Data1c.Tests.Platform;
using Xunit;

namespace Data1c.Tests.Graph;

/// <summary>
/// Проверяет, что при подключённой модели платформы вызовы методов 1С получают собственный тип узла,
/// а вызовы через переменные остаются неразрешёнными.
/// </summary>
public sealed class PlatformClassificationTests
{
    private static readonly Version PlatformVersion = new(8, 3, 27, 2214);

    [Fact]
    public void Вызовы_методов_платформы_получают_свой_тип_узла()
    {
        var graph = Build(WithPlatform());

        Assert.True(graph.TryGetNode("platform:Массив.Добавить", out var array));
        Assert.Equal(GraphNodeKind.Platform, array.Kind);
        Assert.False(array.IsExternal);
        Assert.NotNull(array.Tags);
        Assert.Equal("8.3.27.2214", array.Tags["platformVersion"]);
        Assert.Equal("Массив.Добавить (Array.Add)", array.Tags["platformTitle"]);

        // Глобальная функция платформы вызывается без квалификатора.
        Assert.True(graph.TryGetNode("platform:СтрНайти", out var global));
        Assert.Equal(GraphNodeKind.Platform, global.Kind);

        // Вызов через переменную платформенным не считается.
        Assert.True(graph.TryGetNode("call:Объект.Метод", out var external));
        Assert.True(external.IsExternal);
        Assert.False(graph.TryGetNode("platform:Объект.Метод", out _));
    }

    [Fact]
    public void Без_модели_платформы_поведение_прежнее()
    {
        var graph = Build(platform: null);

        Assert.True(graph.TryGetNode("call:Массив.Добавить", out var external));
        Assert.True(external.IsExternal);
        Assert.DoesNotContain(graph.Nodes, static n => n.Kind == GraphNodeKind.Platform);
    }

    [Fact]
    public void Считает_вызовы_платформы_в_статистике_и_не_относит_их_к_неразрешённым()
    {
        var graph = Build(WithPlatform());

        Assert.Equal(2, graph.Statistics.NodesByKind["Platform"]);

        var unresolvedCalls = graph.Edges.Count(static e =>
            e.Kind == GraphEdgeKind.Calls && e.TargetId.StartsWith("call:", StringComparison.Ordinal));
        Assert.Equal(1, unresolvedCalls);
        Assert.Contains(graph.Edges, static e =>
            e is { Kind: GraphEdgeKind.Calls, TargetId: "platform:Массив.Добавить", Detail: "Массив.Добавить" });
    }

    [Fact]
    public void Недоступная_модель_платформы_не_влияет_на_граф()
    {
        // Пустой источник: индекс создан, но тем нет — классификация работать не должна.
        var graph = Build(new PlatformHelpIndex(new InMemoryPlatformSource()));

        Assert.True(graph.TryGetNode("call:Массив.Добавить", out var external));
        Assert.True(external.IsExternal);
        Assert.DoesNotContain(graph.Nodes, static n => n.Kind == GraphNodeKind.Platform);
    }

    private static DependencyGraph Build(PlatformHelpIndex? platform)
    {
        var metadata = new MetadataDumpReader().Read(SampleDump.Create()).Model;
        return new DependencyGraphBuilder()
            .Build(metadata, [CreateModule()], new DependencyGraphOptions(), platform);
    }

    private static PlatformHelpIndex WithPlatform() =>
        new(new InMemoryPlatformSource("тестовая платформа").AddHelpFile(
            PlatformVersion,
            PlatformHelpKind.SyntaxAssistant,
            "bin/shcntx_ru.hbk",
            HbkTestWriter.Create(
            [
                ("Массив.html", "<h1>Массив (Array)</h1><p>Массив значений произвольного типа.</p>"),
                ("Массив/Добавить.html", "<h1>Массив.Добавить (Array.Add)</h1><p>Добавляет элемент в конец массива.</p>"),
                ("СтрНайти.html", "<h1>СтрНайти (StrFind)</h1><p>Ищет подстроку в строке.</p>"),
            ]),
            @"C:\1cv8\8.3.27.2214"));

    private static BslModuleInfo CreateModule() => new()
    {
        Path = DependencyGraphBuilderTests.FirstModulePath,
        OwnerId = "CommonModule.ОбщегоНазначения",
        Kind = BslModuleKind.CommonModule,
        LineCount = 6,
        Routines =
        [
            new BslRoutine(
                "МояПроцедура",
                BslRoutineKind.Procedure,
                IsExport: true,
                Parameters: [],
                StartLine: 1,
                EndLine: 6,
                Depth: 0,
                Region: null,
                Directives: [],
                Calls:
                [
                    new BslCall("Массив.Добавить", "Массив", "Добавить", 2),
                    new BslCall("СтрНайти", null, "СтрНайти", 3),
                    new BslCall("Объект.Метод", "Объект", "Метод", 4),
                ],
                MetadataAccesses: []),
        ],
    };
}
