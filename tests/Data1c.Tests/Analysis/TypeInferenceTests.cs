using Data1c.Core.Analysis;
using Data1c.Core.Bsl;
using Data1c.Core.Dump;
using Data1c.Core.Graph;
using Xunit;

namespace Data1c.Tests.Analysis;

/// <summary>
/// Проверки вывода типов (Э2-4): присваивания конструкторов, типы параметров по аргументам вызовов
/// и разрешение вызовов через переменную известного платформенного типа.
/// </summary>
public sealed class TypeInferenceTests
{
    private const string ModulePath = "CommonModules/Типы/Ext/Module.bsl";

    private const string ModuleText = """
        Перем Данные;

        Процедура Тест(Параметр)
            Массив = Новый Массив;
            Таблица = Новый ТаблицаЗначений;
            Структура = Новый("Структура");
            Описание = ОписаниеТипов("Число");
            Копия = Таблица.Скопировать();
            Данные = ПолучитьДанные();
            Таблица.Свернуть("Колонка");
            Данные.Обработать();
            ПроверитьТип(Массив);
        КонецПроцедуры

        Процедура ПроверитьТип(Значение)
            Значение.Добавить(1);
        КонецПроцедуры
        """;

    [Fact]
    public void Новый_Новый_по_строке_и_ОписаниеТипов_дают_тип()
    {
        var result = Infer();

        Assert.Equal("Массив", result.Types["Массив"]);
        Assert.Equal("ТаблицаЗначений", result.Types["Таблица"]);
        Assert.Equal("Структура", result.Types["Структура"]);
        Assert.Equal("ОписаниеТипов", result.Types["Описание"]);

        // X.Скопировать() сохраняет тип переменной.
        Assert.Equal("ТаблицаЗначений", result.Types["Копия"]);

        var массив = result.Inferred.Single(item => item.Name == "Массив");
        Assert.Equal(InferredTypeSource.Constructor, массив.Source);
        Assert.Equal(4, массив.Line);
        Assert.Equal("Тест", массив.Routine);

        var структура = result.Inferred.Single(item => item.Name == "Структура");
        Assert.Equal(InferredTypeSource.ConstructorByName, структура.Source);

        var копия = result.Inferred.Single(item => item.Name == "Копия");
        Assert.Equal(InferredTypeSource.CreateOrCopy, копия.Source);
    }

    [Fact]
    public void Типы_известны_для_позиции_модуля()
    {
        var result = Infer();

        var до = result.TypesAt(4);
        Assert.DoesNotContain("Таблица", до.Keys);

        var после = result.TypesAt(10);
        Assert.Equal("ТаблицаЗначений", после["Таблица"]);
        Assert.Equal("Массив", после["Массив"]);
        Assert.True(result.TryGetType("Таблица", 10, out var type));
        Assert.Equal("ТаблицаЗначений", type);
    }

    [Fact]
    public void Параметр_получает_тип_по_аргументу_вызова()
    {
        var result = Infer();

        Assert.Equal("Массив", result.Types["Значение"]);

        var параметр = result.Inferred.Single(item => item.Name == "Значение");
        Assert.Equal(InferredTypeSource.Parameter, параметр.Source);
        Assert.Equal("ПроверитьТип", параметр.Routine);

        Assert.True(result.TryGetType("Значение", 16, out var type));
        Assert.Equal("Массив", type);
    }

    [Fact]
    public void Вызов_через_переменную_известного_типа_разрешается_как_метод_платформы()
    {
        var result = Infer();

        Assert.Equal(3, result.ResolvedCallCount);

        var свернуть = result.PlatformCalls.Single(call => call.Call.Callee == "Таблица.Свернуть");
        Assert.Equal("ТаблицаЗначений", свернуть.TypeName);
        Assert.Equal("ТаблицаЗначений.Свернуть", свернуть.Callee);
        Assert.Equal("platform:ТаблицаЗначений.Свернуть", свернуть.NodeId);
        Assert.Equal(10, свернуть.Call.Line);

        var скопировать = result.PlatformCalls.Single(call => call.Call.Line == 8);
        Assert.Equal("ТаблицаЗначений.Скопировать", скопировать.Callee);

        // Тип параметра пришёл из вызова: «Значение.Добавить» — это «Массив.Добавить».
        var добавить = result.PlatformCalls.Single(call => call.Call.Line == 16);
        Assert.Equal("Массив.Добавить", добавить.Callee);
    }

    [Fact]
    public void Переменная_неизвестного_типа_остаётся_неразрешённой()
    {
        var result = Infer();

        var вызов = result.UnresolvedCalls.Single();
        Assert.Equal("Данные.Обработать", вызов.Call.Callee);
        Assert.Equal("Данные", вызов.Qualifier);
        Assert.Contains("не выведен", вызов.Reason, StringComparison.Ordinal);
        Assert.Equal(1, result.UnresolvedCallCount);

        var тип = result.Unresolved.Single();
        Assert.Equal("Данные", тип.Name);
        Assert.Equal(9, тип.Line);
        Assert.Contains("ПолучитьДанные", тип.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Типы_платформы_отличаются_от_чужих_имён()
    {
        var result = Infer();

        Assert.True(result.IsPlatformType("ТаблицаЗначений"));
        Assert.False(result.IsPlatformType("МойСамодельныйТип"));

        // Конструктор с неизвестным именем типа всё равно даёт имя типа, но платформенным не считается.
        var свой = TypeInference.Infer(new BslModuleSource(
            ModulePath,
            """
            Процедура Тест()
                Объект = Новый МойСамодельныйТип;
                Объект.Сделать();
            КонецПроцедуры
            """));

        Assert.Equal("МойСамодельныйТип", свой.Types["Объект"]);
        Assert.Empty(свой.PlatformCalls);
        Assert.Single(свой.UnresolvedCalls);
        Assert.Contains("не отнесён к типам платформы", свой.UnresolvedCalls[0].Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Вывод_типов_подключается_к_графу_зависимостей()
    {
        var source = new InMemoryDumpSource("выгрузка с типами");
        source.AddText("Configuration.xml", ConfigurationXml);
        source.AddText("CommonModules/Типы.xml", CommonModuleXml);
        source.AddText(ModulePath, ModuleText);

        var graph = new DumpAnalyzer().Analyze(source).Graph;

        Assert.True(graph.TryGetNode("platform:ТаблицаЗначений.Свернуть", out var свернуть));
        Assert.Equal(GraphNodeKind.Platform, свернуть.Kind);
        Assert.False(graph.TryGetNode("call:Таблица.Свернуть", out _));
        Assert.Contains(graph.Edges, edge =>
            edge is { Kind: GraphEdgeKind.Calls, TargetId: "platform:ТаблицаЗначений.Свернуть", Line: 10 });

        // Тип параметра пришёл из вызова этого же модуля.
        Assert.True(graph.TryGetNode("platform:Массив.Добавить", out _));
        Assert.False(graph.TryGetNode("call:Значение.Добавить", out _));

        // Тип переменной неизвестен: вызов остаётся внешней заглушкой.
        Assert.True(graph.TryGetNode("call:Данные.Обработать", out var внешний));
        Assert.True(внешний.IsExternal);
    }

    private static TypeInferenceResult Infer() =>
        TypeInference.Infer(new BslModuleSource(ModulePath, ModuleText));

    private const string ConfigurationXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <MetaDataObject xmlns="http://v8.1c.ru/8.3/MDClasses" xmlns:v8="http://v8.1c.ru/8.1/data/core" version="2.20">
            <Configuration uuid="0f0f0f0f-0000-0000-0000-00000000000a">
                <Properties>
                    <Name>КонфигурацияСТипами</Name>
                </Properties>
                <ChildObjects>
                    <CommonModule>Типы</CommonModule>
                </ChildObjects>
            </Configuration>
        </MetaDataObject>
        """;

    private const string CommonModuleXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <MetaDataObject xmlns="http://v8.1c.ru/8.3/MDClasses" xmlns:v8="http://v8.1c.ru/8.1/data/core" version="2.20">
            <CommonModule uuid="66666666-6666-6666-6666-66666666666a">
                <Properties>
                    <Name>Типы</Name>
                    <Server>true</Server>
                </Properties>
            </CommonModule>
        </MetaDataObject>
        """;
}
