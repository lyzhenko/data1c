using System.Text;
using Data1c.Core.Analysis;
using Data1c.Core.Dump;
using Xunit;

namespace Data1c.Tests.Dump;

/// <summary>
/// Сквозные проверки чтения текстов выгрузки в разных кодировках: модуль в CP1251
/// (старые выгрузки и сторонние инструменты) и модуль в UTF-8 с BOM.
/// XML во всех случаях остаётся в UTF-8 — смешанный случай встречается на практике.
/// </summary>
public sealed class DumpEncodingTests
{
    [Fact]
    public void Модуль_в_CP1251_разбирается_и_имена_процедур_правильные()
    {
        var source = SampleDump.Create()
            .AddBytes(SampleDump.CommonModuleBslPath, DumpTextReader.Fallback.GetBytes(SampleDump.CommonModuleBsl));

        var result = new DumpAnalyzer().Analyze(source);

        Assert.Empty(result.Warnings);
        var module = result.Modules.Single(static m => m.Path == SampleDump.CommonModuleBslPath);
        Assert.Contains(module.Routines, static r => r.Name == "МояПроцедура");
        Assert.Contains(module.Routines, static r => r.Name == "ДругаяПроцедура");
        Assert.DoesNotContain(module.Routines, static r => r.Name.Contains('\uFFFD'));
    }

    [Fact]
    public void Модуль_в_UTF8_с_BOM_разбирается_как_раньше()
    {
        var source = SampleDump.Create()
            .AddBytes(SampleDump.CommonModuleBslPath, WithBom(SampleDump.CommonModuleBsl));

        var result = new DumpAnalyzer().Analyze(source);

        Assert.Empty(result.Warnings);
        var module = result.Modules.Single(static m => m.Path == SampleDump.CommonModuleBslPath);
        Assert.Contains(module.Routines, static r => r is { Name: "МояПроцедура", IsExport: true });
        Assert.Contains(module.Routines, static r => r.Name == "ДругаяПроцедура");
    }

    [Fact]
    public void Смешанная_выгрузка_читается_целиком()
    {
        const string FormModule =
            "&НаКлиенте\nПроцедура ПриОткрытии(Отказ)\n\tСообщить(\"Открытие\");\nКонецПроцедуры\n";

        var source = SampleDump.Create()
            .AddBytes(SampleDump.CommonModuleBslPath, DumpTextReader.Fallback.GetBytes(SampleDump.CommonModuleBsl))
            .AddBytes(SampleDump.SecondCommonModuleBslPath, WithBom(SampleDump.SecondCommonModuleBsl))
            .AddBytes(SampleDump.FormModuleBslPath, new UTF8Encoding(false).GetBytes(FormModule));

        var result = new DumpAnalyzer().Analyze(source);

        Assert.Empty(result.Warnings);
        Assert.Equal(3, result.Modules.Count);
        Assert.Contains(
            result.Modules.SelectMany(static m => m.Routines),
            static r => r.Name == "ЗагрузитьДанные");
        Assert.Contains(
            result.Modules.SelectMany(static m => m.Routines),
            static r => r.Name == "ПриОткрытии");
    }

    [Fact]
    public void Вызов_из_модуля_в_CP1251_попадает_в_граф()
    {
        var source = SampleDump.Create()
            .AddBytes(SampleDump.CommonModuleBslPath, DumpTextReader.Fallback.GetBytes(SampleDump.CommonModuleBsl))
            .AddBytes(SampleDump.SecondCommonModuleBslPath, DumpTextReader.Fallback.GetBytes(SampleDump.SecondCommonModuleBsl));

        var result = new DumpAnalyzer().Analyze(source);

        Assert.Contains(result.Graph.Edges, static e => e.Detail == "РаботаСДанными.ЗагрузитьДанные");
    }

    private static byte[] WithBom(string text)
    {
        var bytes = new List<byte> { 0xEF, 0xBB, 0xBF };

        // Переводы строк приводятся к «\n»: исходники репозитория могут быть выгружены
        // как с CRLF, так и с LF, и тест не должен зависеть от этого.
        bytes.AddRange(new UTF8Encoding(false).GetBytes(text.ReplaceLineEndings("\n")));
        return [.. bytes];
    }
}
