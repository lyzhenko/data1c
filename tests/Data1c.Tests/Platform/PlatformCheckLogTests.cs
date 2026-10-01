using Data1c.Core.Dump;
using Data1c.Core.Platform;
using Xunit;

namespace Data1c.Tests.Platform;

/// <summary>
/// Разбор журнала штатной проверки платформы. Форматы сняты с реальных запусков 1cv8 на выгрузке
/// 2,9 ГБ: <c>/CheckConfig</c> ловит ошибку в модуле, <c>/CheckModules</c> — нет.
/// </summary>
public sealed class PlatformCheckLogTests
{
    [Fact]
    public void Чистая_проверка_даёт_признак_отсутствия_ошибок()
    {
        Assert.True(PlatformCheckLog.Parse("Синтаксических ошибок не обнаружено!\r\n").Clean);
        Assert.True(PlatformCheckLog.Parse("Ошибок не обнаружено\r\n").Clean);

        var result = PlatformCheckLog.Parse("Ошибок не обнаружено\r\n");
        Assert.True(result.Ok);
        Assert.Empty(result.Problems);
        Assert.Empty(result.Other);
    }

    [Fact]
    public void Ошибка_модуля_разбирается_с_местом_строкой_и_фрагментом()
    {
        // Строки взяты из журнала реального запуска после внесения ошибки «Если Тогда».
        const string log =
            "{ОбщийМодуль.НастройкиПубликацииМЛКВызовСервера.Модуль(3,9)}: Ожидается выражение\r\n" +
            "    Если<<?>> Тогда (Проверка: Сервер)\r\n" +
            "{ОбщийМодуль.НастройкиПубликацииМЛКВызовСервера.Модуль(4,1)}: Ожидается ключевое слово 'КонецЕсли' ('EndIf')\r\n";

        var result = PlatformCheckLog.Parse(log);

        Assert.Equal(2, result.Problems.Count);
        var first = result.Problems[0];
        Assert.Equal(PlatformCheckSeverity.Error, first.Severity);
        Assert.Equal("Ожидается выражение", first.Message);
        Assert.Equal(3, first.Line);
        Assert.Equal("ОбщийМодуль.НастройкиПубликацииМЛКВызовСервера.Модуль(3,9)", first.Place);
        Assert.Equal("CommonModules/НастройкиПубликацииМЛКВызовСервера/Ext/Module.bsl", first.FilePath);
        Assert.Contains("Если<<?>> Тогда", first.Snippet!, StringComparison.Ordinal);
        Assert.False(result.Clean);
        Assert.False(result.Ok);
    }

    [Fact]
    public void Замечания_проверки_конфигурации_считаются_предупреждениями()
    {
        const string log =
            "Подсистема.ЭлектронноеВзаимодействие.Справка Неразрешимые ссылки на объекты метаданных (1)\r\n" +
            "Справочник.НоменклатураКонтрагентов.Форма.ФормаВыбора.Справка Неразрешимые ссылки на объекты метаданных (3)\r\n";

        var result = PlatformCheckLog.Parse(log);

        Assert.Equal(2, result.Problems.Count);
        Assert.All(result.Problems, problem => Assert.Equal(PlatformCheckSeverity.Warning, problem.Severity));
        Assert.Equal("Подсистема.ЭлектронноеВзаимодействие.Справка", result.Problems[0].Place);
        Assert.Contains("Неразрешимые ссылки", result.Problems[0].Message, StringComparison.Ordinal);
        Assert.True(result.Ok);
    }

    [Fact]
    public void Предупреждение_загрузки_не_считается_ошибкой()
    {
        const string log =
            "Файл - C:/dump/Catalogs/Организации/Ext/Help/ru.html: Возможно неверная ссылка true внутри справки.\r\n";

        var result = PlatformCheckLog.Parse(log, PlatformCheckSeverity.Warning);

        var problem = Assert.Single(result.Problems);
        Assert.Equal(PlatformCheckSeverity.Warning, problem.Severity);
        Assert.Equal("C:/dump/Catalogs/Организации/Ext/Help/ru.html", problem.FilePath);
        Assert.True(result.Ok);
    }

    [Fact]
    public void Незнакомые_строки_не_теряются()
    {
        var result = PlatformCheckLog.Parse("проверка завершена\r\nСтрока без пути\r\n");

        Assert.Empty(result.Problems);
        Assert.Equal(2, result.Other.Count);
        Assert.False(result.Clean);
    }

    [Fact]
    public void Пустой_журнал_не_ломает_разбор()
    {
        Assert.Empty(PlatformCheckLog.Parse(null).Problems);
        Assert.Empty(PlatformCheckLog.Parse(string.Empty).Problems);
        Assert.False(PlatformCheckLog.Parse("   \r\n\r\n").Clean);
    }

    [Fact]
    public void Журнал_в_CP1251_читается_тем_же_читателем_что_и_выгрузка()
    {
        // Так журнал выглядит на русской Windows: 1С пишет его в CP1251.
        var encoding = DumpTextReader.Fallback;
        var bytes = encoding.GetBytes(
            "{ОбщийМодуль.ОбщегоНазначения.Модуль(3,9)}: Ожидается выражение\r\n" +
            "    Если<<?>> Тогда (Проверка: Сервер)\r\n");

        var result = PlatformCheckLog.Parse(DumpTextReader.ReadAllText(bytes));

        var problem = Assert.Single(result.Problems);
        Assert.Equal(3, problem.Line);
        Assert.Equal(PlatformCheckSeverity.Error, problem.Severity);
        Assert.Contains("ОбщегоНазначения", problem.FilePath!, StringComparison.Ordinal);
    }

    [Fact]
    public void Платформа_находится_если_установлена()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "1cv8");

        if (!Directory.Exists(root) || !Directory.EnumerateFileSystemEntries(root).Any())
        {
            return;
        }

        var executable = PlatformCheckRunner.TryFindExecutable();

        Assert.NotNull(executable);
        Assert.True(File.Exists(executable));
    }
}

/// <summary>Перевод мест платформы в пути файлов выгрузки.</summary>
public sealed class PlatformLocationTests
{
    [Theory]
    [InlineData("ОбщийМодуль.ОбщегоНазначения.Модуль(3,9)", "CommonModules/ОбщегоНазначения/Ext/Module.bsl", 3)]
    [InlineData("Справочник.Товары.МодульОбъекта(10,5)", "Catalogs/Товары/Ext/ObjectModule.bsl", 10)]
    [InlineData("Справочник.Товары.МодульМенеджера(1,1)", "Catalogs/Товары/Ext/ManagerModule.bsl", 1)]
    [InlineData("Справочник.Товары.Форма.ФормаЭлемента.Модуль(2,1)", "Catalogs/Товары/Forms/ФормаЭлемента/Ext/Form/Module.bsl", 2)]
    [InlineData("Документ.ЗаказПокупателя.Форма.ФормаДокумента.Форма", "Documents/ЗаказПокупателя/Forms/ФормаДокумента/Ext/Form.xml", null)]
    [InlineData("РегистрСведений.КурсыВалют.МодульНабораЗаписей(7,3)", "InformationRegisters/КурсыВалют/Ext/RecordSetModule.bsl", 7)]
    public void Место_платформы_переводится_в_путь_файла(string place, string expected, int? line)
    {
        Assert.Equal(expected, PlatformLocation.ResolveFilePath(place));
        Assert.Equal(line, PlatformLocation.LineOf(place));
    }

    [Theory]
    [InlineData("Подсистема.Продажи.Справка")]
    [InlineData("Справочник.Товары.Справка")]
    [InlineData("нечто непонятное")]
    [InlineData("")]
    public void Неизвестные_места_дают_пустой_путь(string place)
    {
        Assert.Null(PlatformLocation.ResolveFilePath(place));
        Assert.Null(PlatformLocation.LineOf(place));
    }
}
