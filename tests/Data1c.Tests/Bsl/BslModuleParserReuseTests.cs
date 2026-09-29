using Data1c.Core.Bsl;
using Xunit;

namespace Data1c.Tests.Bsl;

/// <summary>
/// Регрессии на повторное использование одного разборщика: раньше он возвращал те же списки,
/// которые очищал при следующем разборе, и не был защищён от одновременных вызовов.
/// </summary>
public sealed class BslModuleParserReuseTests
{
    private const string FirstText = """
        Процедура Первая()
            ДругойМодуль.Метод();
        КонецПроцедуры
        """;

    [Fact]
    public void Повторный_разбор_не_портит_предыдущий_результат()
    {
        var parser = new BslModuleParser();

        var first = parser.Parse(new BslModuleSource("a.bsl", FirstText, "CommonModule.А", BslModuleKind.CommonModule));
        _ = parser.Parse(new BslModuleSource("b.bsl", "Процедура Вторая()\nКонецПроцедуры", "CommonModule.Б", BslModuleKind.CommonModule));

        var routine = Assert.Single(first.Routines);
        Assert.Equal("Первая", routine.Name);
        var call = Assert.Single(routine.Calls);
        Assert.Equal("ДругойМодуль.Метод", call.Callee);
        Assert.Equal("ДругойМодуль", call.Qualifier);
    }

    [Fact]
    public void Один_экземпляр_можно_вызывать_из_нескольких_потоков()
    {
        var parser = new BslModuleParser();
        var results = new BslModuleInfo[64];

        Parallel.For(0, results.Length, index =>
        {
            results[index] = parser.Parse(new BslModuleSource($"m{index}.bsl", FirstText));
        });

        Assert.All(results, static result =>
        {
            var routine = Assert.Single(result.Routines);
            Assert.Single(routine.Calls);
        });
    }
}
