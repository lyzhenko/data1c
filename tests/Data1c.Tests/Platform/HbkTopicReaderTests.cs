using Data1c.Core.Platform;
using Xunit;

namespace Data1c.Tests.Platform;

public sealed class HbkTopicReaderTests
{
    [Fact]
    public void Извлекает_заголовок_и_текст()
    {
        const string html = "<html><body><h1>Массив.Добавить</h1><p>Добавляет значение в конец массива.</p></body></html>";

        var (title, text) = HbkTopicReader.ExtractText(html);

        Assert.Equal("Массив.Добавить", title);
        Assert.Equal("Массив.Добавить Добавляет значение в конец массива.", text);
    }

    [Fact]
    public void Вырезает_скрипты_стили_и_навигацию()
    {
        const string html = """
            <html><head><style>.a { color: red; }</style><script>var secret = 42;</script></head>
            <body><nav>Оглавление</nav><h2>Метод</h2><p>Описание метода.</p></body></html>
            """;

        var (title, text) = HbkTopicReader.ExtractText(html);

        Assert.Equal("Метод", title);
        Assert.DoesNotContain("secret", text, StringComparison.Ordinal);
        Assert.DoesNotContain("color: red", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Оглавление", text, StringComparison.Ordinal);
        Assert.Contains("Описание метода.", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Декодирует_html_сущности()
    {
        const string html = "<h1>Синтаксис</h1><p>&laquo;Массив&raquo; &amp; &lt;Значение&gt;</p>";

        var (_, text) = HbkTopicReader.ExtractText(html);

        Assert.Contains("«Массив» & <Значение>", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Схлопывает_пробелы_и_переводы_строк()
    {
        const string html = "<h1>Заголовок</h1>\n\n\t<p>Первое       слово</p>\r\n\r\n<p>второе</p>";

        var (_, text) = HbkTopicReader.ExtractText(html);

        Assert.Equal("Заголовок Первое слово второе", text);
    }

    [Fact]
    public void Обрезает_текст_по_лимиту()
    {
        var html = "<h1>Тема</h1><p>" + new string('я', 500) + "</p>";

        var (_, text) = HbkTopicReader.ExtractText(html, maxTextLength: 60);

        Assert.Equal(60, text.Length);
    }

    [Fact]
    public void Без_тега_заголовка_имя_берётся_из_пути()
    {
        var container = HbkTestWriter.Create([("Массив/Добавить.html", "<p>Только текст.</p>")]);

        var topics = ReadTopics(container);

        var topic = Assert.Single(topics);
        Assert.Equal("Массив.Добавить", topic.Name);
        Assert.Equal("Массив.Добавить", topic.Title);
    }

    [Fact]
    public void Пропускает_файлы_не_html()
    {
        var container = HbkTestWriter.Create(
        [
            ("Массив/Добавить.html", "<h1>Массив.Добавить</h1><p>Описание.</p>"),
            ("Массив/картинка.png", "не html"),
            ("Массив/данные.xml", "<x/>"),
        ]);

        var topics = ReadTopics(container);

        Assert.Single(topics);
    }

    [Theory]
    [InlineData("Массив/Добавить.html", "Массив.Добавить")]
    [InlineData("Массив\\Добавить.htm", "Массив.Добавить")]
    [InlineData("/Массив/Добавить.HTML", "Массив.Добавить")]
    [InlineData("Массив.Добавить.html", "Массив.Добавить")]
    [InlineData("БезРасширения", "БезРасширения")]
    [InlineData("Массив.Добавить", "Массив.Добавить")]
    public void Нормализует_имя_темы(string path, string expected)
    {
        Assert.Equal(expected, HbkTopicReader.NormalizeName(path));
    }

    private static IReadOnlyList<HbkTopic> ReadTopics(byte[] container)
    {
        var payload = HbkArchive.ReadFileStorage(new MemoryStream(container));
        using var zip = new MemoryStream(payload, writable: false);
        return HbkTopicReader.ReadTopics(zip);
    }
}
