using Data1c.Core.Analysis;
using Data1c.Core.Dump;
using Data1c.Store;
using Xunit;

namespace Data1c.Tests.Store;

/// <summary>
/// Частичная переиндексация: после правки одного модуля обновляются только его данные,
/// а вызовы к процедурам других модулей остаются связанными с настоящими узлами.
/// </summary>
public sealed class PartialReindexTests
{
    [Fact]
    public void Обновляет_изменённый_модуль_и_не_трогает_остальные()
    {
        using var fixture = new PartialFixture();
        var otherModuleSymbols = fixture.Reader.FindSymbolsInModule(SampleDump.SecondCommonModuleBslPath).Count;

        fixture.Source.AddText(SampleDump.CommonModuleBslPath, """
            Процедура МояПроцедура() Экспорт
                ДругаяПроцедура();
            КонецПроцедуры

            Процедура ДругаяПроцедура()
            КонецПроцедуры

            Функция НоваяФункцияЧастичнойПереиндексации() Экспорт
                Возврат 1;
            КонецФункции
            """);

        var written = fixture.Reindex(SampleDump.CommonModuleBslPath);

        Assert.True(written.Symbols > 0);
        Assert.NotEmpty(fixture.Reader.FindSymbols("НоваяФункцияЧастичнойПереиндексации", exact: true));
        Assert.Equal(otherModuleSymbols, fixture.Reader.FindSymbolsInModule(SampleDump.SecondCommonModuleBslPath).Count);

        // Счётчики в meta пересчитаны, иначе status показывал бы старые числа.
        var statistics = fixture.Reader.GetStatistics();
        var actual = fixture.Reader.FindSymbolsInModule(SampleDump.CommonModuleBslPath).Count
            + fixture.Reader.FindSymbolsInModule(SampleDump.SecondCommonModuleBslPath).Count
            + fixture.Reader.FindSymbolsInModule(SampleDump.FormModuleBslPath).Count;
        Assert.Equal(actual, statistics.Symbols);
    }

    [Fact]
    public void Связывает_вызов_с_процедурой_другого_модуля()
    {
        using var fixture = new PartialFixture();

        // Модуль вызывает процедуру другого общего модуля: частичный разбор его не видит
        // и ставит внешнюю заглушку, а после записи вызов должен указывать на настоящий узел.
        fixture.Source.AddText(SampleDump.SecondCommonModuleBslPath, """
            Процедура ЗагрузитьДанные() Экспорт
                ОбщегоНазначения.МояПроцедура();
            КонецПроцедуры
            """);

        fixture.Reindex(SampleDump.SecondCommonModuleBslPath);

        var caller = "routine:module:" + SampleDump.SecondCommonModuleBslPath + "#ЗагрузитьДанные";
        var target = "routine:module:" + SampleDump.CommonModuleBslPath + "#МояПроцедура";

        var diagnostic = string.Join(
            " | ",
            "callees=" + string.Join(",", fixture.Reader.Callees(caller, 50)),
            "byName=" + string.Join(",", fixture.Reader.CallersByName("МояПроцедура", 20)));

        Assert.True(fixture.Reader.Callees(caller, 50).Contains(target), diagnostic);
    }

    [Fact]
    public void Локальный_вызов_остаётся_внутри_модуля_после_переиндексации()
    {
        using var fixture = new PartialFixture();

        fixture.Source.AddText(SampleDump.CommonModuleBslPath, """
            Процедура МояПроцедура() Экспорт
                ДругаяПроцедура();
            КонецПроцедуры

            Процедура ДругаяПроцедура()
            КонецПроцедуры
            """);

        fixture.Reindex(SampleDump.CommonModuleBslPath);

        var caller = "routine:module:" + SampleDump.CommonModuleBslPath + "#МояПроцедура";
        var target = "routine:module:" + SampleDump.CommonModuleBslPath + "#ДругаяПроцедура";
        Assert.Contains(target, fixture.Reader.Callees(caller, 50));
    }

    private sealed class PartialFixture : IDisposable
    {
        private readonly SqliteIndex _index;
        private readonly IndexWriter _writer;

        internal PartialFixture()
        {
            Source = SampleDump.Create();
            _index = SqliteIndex.OpenInMemory();
            _writer = new IndexWriter(_index) { IncludeComments = false };
            _writer.Write(Source, new DumpAnalyzer().Analyze(Source));
            Reader = new IndexReader(_index);
        }

        internal InMemoryDumpSource Source { get; }

        internal IndexReader Reader { get; }

        internal IndexWriteResult Reindex(params string[] modules)
        {
            var analyzed = new DumpAnalyzer().Analyze(Source, new AnalysisOptions { OnlyModuleFiles = modules });
            return _writer.WriteModules(Source, analyzed, new IndexScope(modules));
        }

        public void Dispose() => _index.Dispose();
    }
}
