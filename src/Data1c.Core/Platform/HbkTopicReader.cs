using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Data1c.Core.Platform;

/// <summary>
/// Превращает HTML-темы из контейнера .hbk в текст: заголовок и содержимое без разметки.
/// </summary>
public static partial class HbkTopicReader
{
    /// <summary>По умолчанию текст темы обрезается до 10 000 символов.</summary>
    public const int DefaultMaxTextLength = 10_000;

    /// <summary>Читает темы из ZIP-архива, полученного из контейнера .hbk.</summary>
    /// <param name="zipStream">Поток с ZIP-архивом (результат <see cref="HbkArchive.ReadFileStorage"/>).</param>
    /// <param name="maxTextLength">Ограничение длины текста темы.</param>
    public static IReadOnlyList<HbkTopic> ReadTopics(Stream zipStream, int maxTextLength = DefaultMaxTextLength)
    {
        ArgumentNullException.ThrowIfNull(zipStream);
        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: true);
        var topics = new List<HbkTopic>(Math.Max(16, archive.Entries.Count));

        foreach (var entry in archive.Entries)
        {
            if (!IsHtml(entry.FullName) || entry.Length == 0)
            {
                continue;
            }

            string html;
            try
            {
                using var stream = entry.Open();
                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                html = reader.ReadToEnd();
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(html))
            {
                continue;
            }

            var (title, text) = ExtractText(html, maxTextLength);
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            var name = NormalizeName(entry.FullName);
            topics.Add(new HbkTopic(name, entry.FullName, title ?? name, text));
        }

        return topics;
    }

    /// <summary>Вырезает заголовок и текст из HTML-темы.</summary>
    public static (string? Title, string Text) ExtractText(string html, int maxTextLength = DefaultMaxTextLength)
    {
        ArgumentNullException.ThrowIfNull(html);
        string? title = null;

        var titleMatch = TitleRegex().Match(html);
        if (titleMatch.Success)
        {
            title = TagRegex().Replace(titleMatch.Groups[1].Value, string.Empty).Trim();
            if (title.Length == 0)
            {
                title = null;
            }
        }

        var text = ScriptStyleNavRegex().Replace(html, " ");
        text = TagRegex().Replace(text, " ");
        text = WhitespaceRegex().Replace(WebUtility.HtmlDecode(text), " ").Trim();
        if (maxTextLength > 0 && text.Length > maxTextLength)
        {
            text = text[..maxTextLength];
        }

        return (title, text);
    }

    /// <summary>
    /// Приводит путь темы внутри контейнера к точечному имени: <c>Массив/Добавить.html</c> → <c>Массив.Добавить</c>.
    /// </summary>
    public static string NormalizeName(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var name = path.Replace('\\', '/').Trim('/');
        var lastDot = name.LastIndexOf('.');
        if (lastDot > 0 && IsHtml(name[lastDot..]))
        {
            name = name[..lastDot];
        }

        return name.Replace('/', '.');
    }

    private static bool IsHtml(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".html", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".htm", StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex(@"<(script|style|nav)[^>]*>[\s\S]*?</\1>", RegexOptions.IgnoreCase)]
    private static partial Regex ScriptStyleNavRegex();

    [GeneratedRegex(@"<(?:h1|h2|title)[^>]*>(.*?)</(?:h1|h2|title)>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TitleRegex();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex WhitespaceRegex();
}
