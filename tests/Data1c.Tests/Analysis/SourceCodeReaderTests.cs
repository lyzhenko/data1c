using Data1c.Core.Analysis;
using Data1c.Core.Dump;
using Xunit;

namespace Data1c.Tests.Analysis;

public sealed class SourceCodeReaderTests
{
    private static SourceCodeReader CreateReader() => new(SampleDump.Create());

    [Fact]
    public void Знает_файлы_выгрузки_с_текстовым_расширением()
    {
        var reader = CreateReader();

        Assert.True(reader.Contains(SampleDump.CommonModuleBslPath));
        Assert.True(reader.Contains(SampleDump.CatalogPath));
        Assert.False(reader.Contains("Catalogs/НетТакого.xml"));
        Assert.False(reader.Contains(null));
        Assert.False(reader.Contains("   "));
    }

    [Fact]
    public void Не_отдаёт_файлы_неподдерживаемых_расширений()
    {
        var source = SampleDump.Create();
        source.AddBytes("CommonTemplates/Драйвер/Ext/Template.bin", [1, 2, 3]);

        var reader = new SourceCodeReader(source);

        Assert.False(reader.Contains("CommonTemplates/Драйвер/Ext/Template.bin"));
        Assert.Null(reader.Read("CommonTemplates/Драйвер/Ext/Template.bin"));
    }

    [Fact]
    public void Читает_диапазон_строк_с_нумерацией()
    {
        var reader = CreateReader();

        var fragment = reader.Read(SampleDump.CommonModuleBslPath, 1, 4);

        Assert.NotNull(fragment);
        Assert.Equal(1, fragment.StartLine);
        Assert.Equal(4, fragment.EndLine);
        Assert.Equal(4, fragment.Lines.Count);
        Assert.StartsWith("Процедура МояПроцедура", fragment.Lines[0], StringComparison.Ordinal);
        Assert.Contains("РаботаСДанными.ЗагрузитьДанные", fragment.Lines[1], StringComparison.Ordinal);
        Assert.Equal("КонецПроцедуры", fragment.Lines[3].Trim());
        Assert.False(fragment.Truncated);
        Assert.True(fragment.TotalLines >= 8);
    }

    [Fact]
    public void Читает_файл_целиком_по_умолчанию()
    {
        var reader = CreateReader();

        var fragment = reader.Read(SampleDump.CommonModuleBslPath);

        Assert.NotNull(fragment);
        Assert.Equal(1, fragment.StartLine);
        Assert.Equal(fragment.TotalLines, fragment.EndLine);
        Assert.Equal(fragment.TotalLines, fragment.Lines.Count);
        Assert.Equal("КонецПроцедуры", fragment.Lines[^1].Trim());
    }

    [Theory]
    [InlineData(0, 3, 1, 3)]
    [InlineData(-5, 2, 1, 2)]
    [InlineData(5, 2, 5, 5)]
    [InlineData(1, 10_000, 1, 8)]
    public void Приводит_границы_диапазона_к_допустимым(int from, int to, int expectedFrom, int expectedTo)
    {
        var reader = CreateReader();

        var fragment = reader.Read(SampleDump.CommonModuleBslPath, from, to);

        Assert.NotNull(fragment);
        Assert.Equal(expectedFrom, fragment.StartLine);
        Assert.Equal(expectedTo, fragment.EndLine);
    }

    [Fact]
    public void Обрезает_слишком_длинный_фрагмент()
    {
        var reader = new SourceCodeReader(SampleDump.Create()) { MaxLines = 2 };

        var fragment = reader.Read(SampleDump.CommonModuleBslPath, 1, 8);

        Assert.NotNull(fragment);
        Assert.Equal(2, fragment.Lines.Count);
        Assert.True(fragment.Truncated);
    }

    [Fact]
    public void Повторное_чтение_даёт_тот_же_результат()
    {
        var reader = CreateReader();

        var first = reader.Read(SampleDump.CommonModuleBslPath, 2, 3);
        var second = reader.Read(SampleDump.CommonModuleBslPath, 2, 3);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(first.Lines, second.Lines);
    }

    [Fact]
    public void Возвращает_null_для_отсутствующего_файла()
    {
        Assert.Null(CreateReader().Read("Catalogs/НетТакого/Ext/Module.bsl"));
    }

    [Fact]
    public void Читает_модуль_в_CP1251()
    {
        var source = SampleDump.Create();
        source.AddBytes(SampleDump.CommonModuleBslPath, DumpTextReader.Fallback.GetBytes(SampleDump.CommonModuleBsl));

        var fragment = new SourceCodeReader(source).Read(SampleDump.CommonModuleBslPath, 1, 2);

        Assert.NotNull(fragment);
        Assert.StartsWith("Процедура МояПроцедура", fragment.Lines[0], StringComparison.Ordinal);
        Assert.Contains("РаботаСДанными.ЗагрузитьДанные", fragment.Lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public void Читает_модуль_в_UTF8_с_BOM()
    {
        var source = SampleDump.Create();
        var bytes = new List<byte> { 0xEF, 0xBB, 0xBF };
        bytes.AddRange(new System.Text.UTF8Encoding(false).GetBytes(SampleDump.CommonModuleBsl));
        source.AddBytes(SampleDump.CommonModuleBslPath, [.. bytes]);

        var fragment = new SourceCodeReader(source).Read(SampleDump.CommonModuleBslPath, 1, 1);

        Assert.NotNull(fragment);
        Assert.StartsWith("Процедура МояПроцедура", fragment.Lines[0], StringComparison.Ordinal);
        Assert.DoesNotContain('\uFEFF', fragment.Lines[0]);
    }
}
