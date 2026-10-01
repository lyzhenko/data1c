using Data1c.Core.Dump;
using Data1c.Core.Platform;
using Xunit;

namespace Data1c.Tests.Platform;

/// <summary>
/// Разбор журнала штатной проверки платформы. Формат снят с реальных запусков 1cv8 /CheckModules
/// и /LoadConfigFromFiles на выгрузке 2,9 ГБ.
/// </summary>
public sealed class PlatformCheckLogTests
{
    [Fact]
    public void Чистая_проверка_даёт_признак_отсутствия_ошибок()
    {
        var result = PlatformCheckLog.Parse("Синтаксических ошибок не обнаружено!\r\n");

        Assert.True(result.Clean);
        Assert.True(result.Ok);
        Assert.Empty(result.Problems);
        Assert.Empty(result.Other);
    }

    [Fact]
    public void Ошибка_в_модуле_разбирается_с_файлом_и_строкой()
    {
        var log = "Файл - C:/dump/CommonModules/РаботаСДанными/Ext/Module.bsl: Строка 12: Ожидается выражение\r\n";

        var result = PlatformCheckLog.Parse(log);

        var problem = Assert.Single(result.Problems);
        Assert.Equal("C:/dump/CommonModules/РаботаСДанными/Ext/Module.bsl", problem.FilePath);
        Assert.Equal(12, problem.Line);
        Assert.Equal("Ожидается выражение", problem.Message);
        Assert.Equal(PlatformCheckSeverity.Error, problem.Severity);
        Assert.False(result.Clean);
        Assert.False(result.Ok);
    }

    [Fact]
    public void Предупреждение_загрузки_не_считается_ошибкой()
    {
        var log = "Файл - C:/dump/Catalogs/Организации/Ext/Help/ru.html: Возможно неверная ссылка true внутри справки.\r\n";

        var result = PlatformCheckLog.Parse(log);

        var problem = Assert.Single(result.Problems);
        Assert.Equal(PlatformCheckSeverity.Warning, problem.Severity);
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
        var bytes = encoding.GetBytes("Файл - C:/dump/Catalogs/Товары/Ext/Module.bsl: Строка 3: Ошибка\r\n");

        var text = DumpTextReader.ReadAllText(bytes);
        var result = PlatformCheckLog.Parse(text);

        var problem = Assert.Single(result.Problems);
        Assert.Equal(3, problem.Line);
        Assert.Contains("Товары", problem.FilePath, StringComparison.Ordinal);
        Assert.Contains("Ошибка", problem.Message, StringComparison.Ordinal);
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
