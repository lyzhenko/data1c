using System.Text;
using Data1c.Core.Dump;
using Xunit;

namespace Data1c.Tests.Dump;

/// <summary>
/// Проверки устойчивого чтения текстов выгрузки: UTF-8 с BOM и без BOM, а также
/// запасная кодировка CP1251 для старых выгрузок и сторонних инструментов.
/// </summary>
public sealed class DumpTextReaderTests
{
    private const string ModuleText = """
        Процедура Приветствие() Экспорт
            Сообщить("Привет, мир");
        КонецПроцедуры
        """;

    /// <summary>
    /// Текст модуля с переводами строк «\n»: исходники в репозитории могут быть выгружены
    /// и с CRLF, поэтому тесты не должны зависеть от переводов строк в самом файле.
    /// </summary>
    private static readonly string Module = ModuleText.ReplaceLineEndings("\n");

    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    [Fact]
    public void Читает_UTF8_без_BOM()
    {
        var text = DumpTextReader.ReadAllText(new UTF8Encoding(false).GetBytes(Module));

        Assert.Equal(Module, text);
        Assert.Contains("Процедура Приветствие", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Читает_UTF8_с_BOM_и_не_оставляет_метку_в_тексте()
    {
        byte[] bytes = [.. Utf8Bom, .. new UTF8Encoding(false).GetBytes(Module)];

        var text = DumpTextReader.ReadAllText(bytes);

        Assert.Equal(Module, text);
        Assert.DoesNotContain('\uFEFF', text);
        Assert.StartsWith("Процедура", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Читает_CP1251_когда_байты_не_являются_корректным_UTF8()
    {
        var bytes = DumpTextReader.Fallback.GetBytes(Module);

        // В CP1251 кириллица занимает по одному байту — как UTF-8 эти байты недействительны.
        Assert.False(DumpTextReader.IsValidUtf8(bytes));

        var text = DumpTextReader.ReadAllText(bytes);

        Assert.Equal(Module, text);
        Assert.Contains("Привет, мир", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Запасная_кодировка_это_CP1251()
    {
        Assert.Equal(1251, DumpTextReader.Fallback.CodePage);
        Assert.Equal("Привет", DumpTextReader.Fallback.GetString(DumpTextReader.Fallback.GetBytes("Привет")));
    }

    [Fact]
    public void Читает_CP1251_когда_ASCII_шапка_длиннее_проверяемого_начала()
    {
        // Шапка из одних ASCII-символов не отличает UTF-8 от CP1251, а кириллица начинается
        // позже: такой файл встречается у инструментов, которые пишут заголовок отдельно.
        var text = new string('-', 8192) + "Процедура Тест()\nКонецПроцедуры";
        var bytes = DumpTextReader.Fallback.GetBytes(text);

        Assert.False(DumpTextReader.IsValidUtf8(bytes));
        Assert.Equal(text, DumpTextReader.ReadAllText(bytes));
    }

    [Fact]
    public void Читает_CP1251_из_потока()
    {
        using var stream = new MemoryStream(DumpTextReader.Fallback.GetBytes(Module));

        Assert.Equal(Module, DumpTextReader.ReadAllText(stream));
    }

    [Fact]
    public void Разбивает_строки_для_обоих_кодировок()
    {
        var expected = new[] { "Процедура Тест()", "\tСообщить(\"Строка\");", "КонецПроцедуры" };
        var text = string.Join("\r\n", expected);

        var utf8Lines = DumpTextReader.ReadLines(new UTF8Encoding(false).GetBytes(text));
        var cp1251Lines = DumpTextReader.ReadLines(DumpTextReader.Fallback.GetBytes(text));

        Assert.Equal(expected, utf8Lines);
        Assert.Equal(expected, cp1251Lines);
    }

    [Fact]
    public void Читает_UTF16_по_метке_порядка_байтов()
    {
        var bytes = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(Module)).ToArray();

        Assert.Equal(Module, DumpTextReader.ReadAllText(bytes));
    }

    [Fact]
    public void Пустой_файл_даёт_пустой_текст_и_нет_строк()
    {
        Assert.Equal(string.Empty, DumpTextReader.ReadAllText([]));
        Assert.Empty(DumpTextReader.ReadLines([]));
    }

    [Fact]
    public void Текстовый_читатель_отдаёт_тот_же_текст_что_и_чтение_целиком()
    {
        var cp1251 = DumpTextReader.Fallback.GetBytes(Module);
        using var stream = new MemoryStream(cp1251);
        using var reader = DumpTextReader.CreateTextReader(stream);

        Assert.Equal(Module, reader.ReadToEnd());
    }

    [Fact]
    public void Текстовый_читатель_отдаёт_UTF8_с_BOM_без_метки()
    {
        byte[] bytes = [.. Utf8Bom, .. new UTF8Encoding(false).GetBytes(Module)];
        using var stream = new MemoryStream(bytes);
        using var reader = DumpTextReader.CreateTextReader(stream);

        var text = reader.ReadToEnd();

        Assert.Equal(Module, text);
        Assert.DoesNotContain('\uFEFF', text);
    }

    [Theory]
    [InlineData("Процедура Тест()")]
    [InlineData("Процедура")]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("Тест — значение")]
    public void Корректный_UTF8_распознаётся_как_UTF8(string sample)
    {
        Assert.True(DumpTextReader.IsValidUtf8(new UTF8Encoding(false).GetBytes(sample)));
    }

    [Theory]
    [InlineData(new byte[] { 0xC0 })]
    [InlineData(new byte[] { 0x80 })]
    [InlineData(new byte[] { 0xE0, 0x80 })]
    [InlineData(new byte[] { 0xF0, 0x80, 0x80 })]
    [InlineData(new byte[] { 0xC0, 0x80 })]
    [InlineData(new byte[] { 0xED, 0xA0, 0x80 })]
    public void Битые_последовательности_UTF8_не_принимаются(byte[] bytes)
    {
        Assert.False(DumpTextReader.IsValidUtf8(bytes));
    }
}
