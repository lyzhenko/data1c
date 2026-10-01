using Data1c.Core.Analysis;
using Data1c.Core.Dump;
using Data1c.Mcp;
using Xunit;

namespace Data1c.Tests.Mcp;

/// <summary>
/// Наблюдение за выгрузкой должно обновлять индекс частично: правка одного модуля не должна
/// пересобирать весь индекс. Проверяется на настоящих файлах выгрузки во временном каталоге.
/// </summary>
public sealed class PartialWatchTests
{
    [Fact]
    public async Task Наблюдение_переиндексирует_только_изменённый_модуль()
    {
        var root = TestDump.Materialize();

        try
        {
            using var session = new AnalysisSession(new AnalysisRequest { DumpPaths = [root] });
            await session.QueryAsync(CancellationToken.None);
            Assert.True(session.IsIndexReady);

            var before = session.GetIndexReader()!.FindSymbolsInModule(SampleDump.SecondCommonModuleBslPath).Count;

            // Правим один модуль: добавляем экспортную функцию.
            var modulePath = Path.Combine(root, SampleDump.CommonModuleBslPath.Replace('/', Path.DirectorySeparatorChar));
            await File.AppendAllTextAsync(
                modulePath,
                "\nФункция ФункцияИзНаблюдения() Экспорт\n\tВозврат 1;\nКонецФункции\n");

            session.StartWatching(TimeSpan.FromMilliseconds(200));
            Assert.True(session.IsWatching);

            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (DateTime.UtcNow < deadline && !session.State.Contains("обновлено модулей", StringComparison.Ordinal))
            {
                await Task.Delay(200);
            }

            Assert.Contains("обновлено модулей: 1", session.State, StringComparison.Ordinal);

            var query = await session.QueryAsync(CancellationToken.None);
            Assert.Contains(query.Search("ФункцияИзНаблюдения", 5), static hit => hit.Name == "ФункцияИзНаблюдения");

            // Модуль, который не менялся, остался на месте: частичная переиндексация его не трогала.
            var after = session.GetIndexReader()!.FindSymbolsInModule(SampleDump.SecondCommonModuleBslPath).Count;
            Assert.Equal(before, after);
        }
        finally
        {
            TestDump.Remove(root);
        }
    }
}
