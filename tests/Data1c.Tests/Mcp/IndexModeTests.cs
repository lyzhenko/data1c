using Data1c.Core.Analysis;
using Data1c.Mcp;
using Data1c.Store;
using Xunit;

namespace Data1c.Tests.Mcp;

/// <summary>
/// Сторож режима индекса: если индекс готов, сервер обязан отвечать из него и не запускать
/// разбор выгрузки в память. Регрессия ровно этого свойства стоила 26 секунд на первый поиск.
/// </summary>
public sealed class IndexModeTests
{
    [Fact]
    public async Task Индексный_режим_отвечает_без_разбора_в_память()
    {
        var path = Path.Combine(Path.GetTempPath(), "data1c-mcp-index-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            var source = SampleDump.Create();
            var analyzed = new DumpAnalyzer().Analyze(source);
            using (var index = SqliteIndex.Open(path))
            {
                new IndexWriter(index) { IncludeComments = false }.Write(source, analyzed);
            }

            using var session = new AnalysisSession(
                new AnalysisRequest { IndexPath = path, UseIndex = true },
                source);

            var query = await session.QueryAsync(CancellationToken.None);

            // Разбор в память не запускался: результата анализа нет, а индекс открыт.
            Assert.Null(session.Result);
            Assert.True(session.IsIndexReady);
            Assert.Equal(path, session.IndexPath);

            Assert.Contains(query.Search("Товары", 5), static hit => hit.Id == "Catalog.Товары");
            Assert.Contains(query.SearchNested("Артикул", 5), static hit => hit.Id == "Catalog.Товары/Attribute.Артикул");

            // Карточка объекта тоже приходит из индекса: состава и типов достаточно, чтобы писать код.
            var card = query.GetMetadata("Catalog.Товары");
            Assert.NotNull(card);
            Assert.Contains(card.Children, static child => child.Name == "Артикул");
        }
        finally
        {
            foreach (var file in Directory.EnumerateFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + "*"))
            {
                File.Delete(file);
            }
        }
    }

    [Fact]
    public async Task При_выключенном_индексе_работает_разбор_в_память()
    {
        var source = SampleDump.Create();
        using var session = new AnalysisSession(
            new AnalysisRequest { UseIndex = false },
            source);

        var query = await session.QueryAsync(CancellationToken.None);

        Assert.False(session.IsIndexReady);
        Assert.NotNull(session.Result);
        Assert.Contains(query.Search("Товары", 5), static hit => hit.Id == "Catalog.Товары");
        Assert.Contains(query.SearchNested("Артикул", 5), static hit => hit.Id == "Catalog.Товары/Attribute.Артикул");
    }
}
