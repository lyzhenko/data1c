using Data1c.Core.Bsl;
using Xunit;

namespace Data1c.Tests.Bsl;

/// <summary>
/// Проверки таблицы символов модуля (Э2-3): переменные модуля, параметры и локальные переменные
/// с позициями, областями видимости и запросами «что видно в строке» и «где объявлено имя».
/// </summary>
public sealed class SymbolTableTests
{
    private const string ModuleText = """
        Перем Кэш, Настройки;
        Перем Значение Экспорт;

        Процедура Обработка(Знач Параметр)
            Перем Локальная;
            Локальная = Параметр;
        КонецПроцедуры

        Процедура БезОбъявлений(Аргумент)
            Аргумент = 1;
            Значение = Аргумент;
        КонецПроцедуры
        """;

    [Fact]
    public void Собирает_переменные_модуля_параметры_и_локальные_с_позициями()
    {
        var table = SymbolTable.Build(ModuleText);

        var кэш = table.FindDeclarations("Кэш").Single();
        Assert.Equal(BslSymbolKind.ModuleVariable, кэш.Kind);
        Assert.Equal(BslScopeKind.Module, кэш.Scope);
        Assert.Equal(1, кэш.Line);
        Assert.Equal(7, кэш.Column);
        Assert.False(кэш.IsExport);
        Assert.Null(кэш.Routine);

        var значение = table.FindDeclarations("Значение").Single();
        Assert.Equal(BslSymbolKind.ModuleVariable, значение.Kind);
        Assert.Equal(2, значение.Line);
        Assert.Equal(7, значение.Column);
        Assert.True(значение.IsExport);

        var параметр = table.FindDeclarations("Параметр").Single();
        Assert.Equal(BslSymbolKind.Parameter, параметр.Kind);
        Assert.Equal("Обработка", параметр.Routine);
        Assert.Equal(4, параметр.Line);
        Assert.Equal(26, параметр.Column);
        Assert.True(параметр.IsByValue);
        Assert.False(параметр.IsImplicit);

        var локальная = table.FindDeclarations("Локальная").Single();
        Assert.Equal(BslSymbolKind.LocalVariable, локальная.Kind);
        Assert.Equal("Обработка", локальная.Routine);
        Assert.Equal(5, локальная.Line);
        Assert.Equal(11, локальная.Column);
        Assert.False(локальная.IsImplicit);

        var аргумент = table.FindDeclarations("Аргумент").Single();
        Assert.Equal(BslSymbolKind.Parameter, аргумент.Kind);
        Assert.Equal("БезОбъявлений", аргумент.Routine);
        Assert.Equal(9, аргумент.Line);
        Assert.Equal(25, аргумент.Column);
    }

    [Fact]
    public void Локальная_переменная_видна_только_в_своей_процедуре()
    {
        var table = SymbolTable.Build(ModuleText);

        var вОбработке = table.VisibleAt(6).Select(static symbol => symbol.Name).ToList();
        Assert.Contains("Параметр", вОбработке);
        Assert.Contains("Локальная", вОбработке);

        var вБезОбъявлений = table.VisibleAt(11).Select(static symbol => symbol.Name).ToList();
        Assert.DoesNotContain("Локальная", вБезОбъявлений);
        Assert.DoesNotContain("Параметр", вБезОбъявлений);
        Assert.Contains("Аргумент", вБезОбъявлений);

        // «Где объявлено имя»: параметр и локальная переменная другой процедуры здесь не видны.
        Assert.Null(table.Resolve("Локальная", 11));
        Assert.Null(table.Resolve("Параметр", 11));
        Assert.Equal("Обработка", table.Resolve("Локальная", 6)!.Routine);
    }

    [Fact]
    public void Переменная_модуля_видна_везде()
    {
        var table = SymbolTable.Build(ModuleText);

        Assert.Contains(table.VisibleAt(1), static symbol => symbol.Name == "Кэш");
        Assert.Contains(table.VisibleAt(3), static symbol => symbol.Name == "Настройки");
        Assert.Contains(table.VisibleAt(12), static symbol => symbol.Name == "Кэш");

        Assert.Equal(BslSymbolKind.ModuleVariable, table.Resolve("Кэш", 12)!.Kind);
        Assert.Null(table.Resolve("Кэш", 12)!.Routine);
    }

    [Fact]
    public void Локальная_переменная_видна_со_строки_объявления()
    {
        var table = SymbolTable.Build("""
            Процедура Тест()
                Х = 1;
                Перем Поздняя;
            КонецПроцедуры
            """);

        Assert.DoesNotContain(table.VisibleAt(2), static symbol => symbol.Name == "Поздняя");
        Assert.Contains(table.VisibleAt(3), static symbol => symbol.Name == "Поздняя");
    }

    [Fact]
    public void Неявные_переменные_присваивания_и_цикла_попадают_в_свою_процедуру()
    {
        var table = SymbolTable.Build("""
            Процедура Тест()
                Данные = Новый Массив;
                Для Каждого Элемент Из Данные Цикл
                    Элемент = 1;
                КонецЦикла;
            КонецПроцедуры
            """);

        var данные = table.FindDeclarations("Данные").Single();
        Assert.Equal(BslSymbolKind.LocalVariable, данные.Kind);
        Assert.Equal("Тест", данные.Routine);
        Assert.True(данные.IsImplicit);
        Assert.Equal(2, данные.Line);
        Assert.Equal(5, данные.Column);

        var элемент = table.FindDeclarations("Элемент").Single();
        Assert.Equal(3, элемент.Line);
        Assert.Equal("Тест", элемент.Routine);

        Assert.Contains(table.VisibleAt(4), static symbol => symbol.Name == "Данные");
    }

    [Fact]
    public void Присваивание_переменной_модуля_не_создаёт_локальную()
    {
        var table = SymbolTable.Build("""
            Перем Значение;

            Процедура Тест()
                Значение = 1;
                Другое = 2;
            КонецПроцедуры
            """);

        var значение = table.FindDeclarations("Значение").Single();
        Assert.Equal(BslSymbolKind.ModuleVariable, значение.Kind);
        Assert.Equal(1, значение.Line);

        var другое = table.FindDeclarations("Другое").Single();
        Assert.Equal(BslSymbolKind.LocalVariable, другое.Kind);
        Assert.Equal("Тест", другое.Routine);
    }

    [Fact]
    public void Область_строки_определяется_по_границам_процедур()
    {
        var table = SymbolTable.Build(ModuleText);

        Assert.Equal(BslScopeKind.Module, table.ScopeAt(1).Kind);
        Assert.Equal("Обработка", table.ScopeAt(6).Routine);
        Assert.Equal("БезОбъявлений", table.ScopeAt(10).Routine);
        Assert.Equal(2, table.Scopes.Count);
        Assert.Equal((4, 7), (table.Scopes[0].StartLine, table.Scopes[0].EndLine));
        Assert.Equal(["Параметр"], table.Scopes[0].Parameters);
    }

    [Fact]
    public void Пустой_модуль_даёт_пустую_таблицу()
    {
        var table = SymbolTable.Build(string.Empty);

        Assert.Empty(table.Symbols);
        Assert.Empty(table.Scopes);
        Assert.Empty(table.VisibleAt(1));
        Assert.Empty(table.FindDeclarations("НетТакого"));
        Assert.Null(table.Resolve("НетТакого", 1));
    }
}
