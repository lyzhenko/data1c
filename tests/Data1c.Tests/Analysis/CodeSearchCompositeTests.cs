using System.Text;
using Data1c.Core.Analysis;
using Data1c.Core.Dump;
using Data1c.FileSystem;
using Xunit;

namespace Data1c.Tests.Analysis;

/// <summary>
/// Поиск по тексту в составной выгрузке: у adopted-файлов расширения тот же относительный путь,
/// что и в базе, поэтому каждая версия обязана искаться по своему содержимому.
/// </summary>
public sealed class CodeSearchCompositeTests
{
    private const string OrderPath = "Documents/ЗаказКлиента.xml";

    [Fact]
    public void Строка_только_из_расширения_даёт_одно_совпадение()
    {
        var result = Search("ЧекПробитВБухгалтерии");

        var hit = Assert.Single(result.Hits);
        Assert.Equal(1, hit.SourceIndex);
        Assert.Equal(670, hit.Line);
        Assert.Equal("<Name>ЧекПробитВБухгалтерии</Name>", hit.Text);
    }

    [Fact]
    public void Строка_только_из_базы_даёт_одно_совпадение()
    {
        var result = Search("ТолькоВБазе");

        var hit = Assert.Single(result.Hits);
        Assert.Equal(0, hit.SourceIndex);
        Assert.Equal(671, hit.Line);
    }

    [Fact]
    public void Строка_в_обеих_версиях_даёт_две_находки_с_номерами_своих_версий()
    {
        var result = Search("ОбщаяСтрока");

        Assert.Equal(2, result.Hits.Count);

        var fromBase = result.Hits.Single(hit => hit.SourceIndex == 0);
        var fromExtension = result.Hits.Single(hit => hit.SourceIndex == 1);

        Assert.Equal(673, fromBase.Line);
        Assert.Equal(674, fromExtension.Line);
    }

    [Fact]
    public void Строка_страницы_базы_не_находится_в_версии_расширения()
    {
        // Строка есть в базе (строка 670), но расширение перекрыло файл своим содержимым:
        // в действующей версии её нет, и поиск не должен показывать её как находку расширения.
        var result = Search("СтрокаБазыКоторойНетВРасширении");

        var hit = Assert.Single(result.Hits);
        Assert.Equal(0, hit.SourceIndex);
    }

    [Fact]
    public void Файловая_выгрузка_с_расширением_ищется_по_своим_версиям()
    {
        var baseRoot = Path.Combine(Path.GetTempPath(), "data1c-search-base-" + Guid.NewGuid().ToString("N"));
        var extensionRoot = Path.Combine(Path.GetTempPath(), "data1c-search-ext-" + Guid.NewGuid().ToString("N"));

        try
        {
            WriteFile(baseRoot, OrderPath, Text(
                (670, "<ChoiceFoldersAndItems>Items</ChoiceFoldersAndItems>"),
                (671, "<Name>ТолькоВБазе</Name>")));

            WriteFile(extensionRoot, OrderPath, Text(
                (670, "<Name>ЧекПробитВБухгалтерии</Name>"),
                (671, "<Name>ПравкаРасширения</Name>")));

            var composite = new CompositeDumpSource(
            [
                new FileSystemDumpSource(baseRoot),
                new FileSystemDumpSource(extensionRoot),
            ]);

            // Строка расширения: одна находка, версия расширения, её собственная строка.
            var fromExtension = Assert.Single(Search(composite, "ЧекПробитВБухгалтерии").Hits);
            Assert.Equal(1, fromExtension.SourceIndex);
            Assert.Equal(670, fromExtension.Line);
            Assert.Equal("<Name>ЧекПробитВБухгалтерии</Name>", fromExtension.Text);

            // Строка базы: тоже одна находка, но под меткой базы — расширение её не «наследует».
            var fromBase = Assert.Single(Search(composite, "ТолькоВБазе").Hits);
            Assert.Equal(0, fromBase.SourceIndex);
            Assert.Equal(671, fromBase.Line);
        }
        finally
        {
            Delete(baseRoot);
            Delete(extensionRoot);
        }
    }

    private static CodeSearchResult Search(IDumpSource source, string query) =>
        new CodeSearchService(source).Search(
            query,
            new CodeSearchOptions
            {
                Extensions = [".xml"],
                ContextLines = 0,
                MaxResults = 50,
                MaxMatchesPerFile = 10,
            });

    private static void WriteFile(string root, string relativePath, string content)
    {
        var full = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content, new UTF8Encoding(false));
    }

    private static void Delete(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static CodeSearchResult Search(string query) =>
        new CodeSearchService(CreateComposite()).Search(
            query,
            new CodeSearchOptions
            {
                Extensions = [".xml"],
                ContextLines = 0,
                MaxResults = 50,
                MaxMatchesPerFile = 10,
            });

    /// <summary>База и расширение с одним и тем же путём, но с разным содержимым.</summary>
    private static CompositeDumpSource CreateComposite()
    {
        var baseSource = new InMemoryDumpSource("база");
        baseSource.AddText(OrderPath, Text(
            (670, "<ChoiceFoldersAndItems>Items</ChoiceFoldersAndItems>"),
            (671, "<Name>ТолькоВБазе</Name>"),
            (672, "<Name>СтрокаБазыКоторойНетВРасширении</Name>"),
            (673, "<Name>ОбщаяСтрока</Name>")));

        var extension = new InMemoryDumpSource("расширение");
        extension.AddText(OrderPath, Text(
            (670, "<Name>ЧекПробитВБухгалтерии</Name>"),
            (671, "<Name>ПравкаРасширения</Name>"),
            (674, "<Name>ОбщаяСтрока</Name>")));

        return new CompositeDumpSource([baseSource, extension]);
    }

    /// <summary>Собирает файл из строк «номер — текст»: между указанными строками пустые.</summary>
    private static string Text(params (int Number, string Value)[] lines)
    {
        var total = lines.Max(static line => line.Number);
        var result = new StringBuilder();
        for (var number = 1; number <= total; number++)
        {
            var found = lines.FirstOrDefault(line => line.Number == number);
            result.AppendLine(found.Value ?? $"<пусто>{number}</пусто>");
        }

        return result.ToString();
    }
}
