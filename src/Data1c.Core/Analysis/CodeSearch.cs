using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Data1c.Core.Dump;

namespace Data1c.Core.Analysis;

/// <summary>Настройки поиска по тексту файлов выгрузки.</summary>
public sealed record CodeSearchOptions
{
    /// <summary>Сколько совпадений вернуть всего.</summary>
    public int MaxResults { get; init; } = 50;

    /// <summary>Сколько совпадений брать из одного файла.</summary>
    public int MaxMatchesPerFile { get; init; } = 10;

    /// <summary>Сколько строк контекста показывать вокруг совпадения.</summary>
    public int ContextLines { get; init; } = 2;

    /// <summary>Предел времени: поиск по сотням мегабайт обязан укладываться в него.</summary>
    public TimeSpan Deadline { get; init; } = TimeSpan.FromSeconds(8);

    /// <summary>Искать регулярным выражением.</summary>
    public bool Regex { get; init; }

    /// <summary>Учитывать регистр (по умолчанию нет).</summary>
    public bool CaseSensitive { get; init; }

    /// <summary>Расширения файлов, по которым идёт поиск. Пустой набор — текстовые по умолчанию.</summary>
    public IReadOnlyCollection<string>? Extensions { get; init; }
}

/// <summary>Одно совпадение в тексте.</summary>
/// <param name="Path">Путь файла внутри выгрузки.</param>
/// <param name="Line">Номер строки (1-based).</param>
/// <param name="Text">Строка, в которой найдено совпадение.</param>
/// <param name="Context">Строки вокруг совпадения с номерами.</param>
public sealed record CodeSearchHit(string Path, int Line, string Text, IReadOnlyList<string> Context);

/// <summary>Результат поиска по тексту.</summary>
public sealed record CodeSearchResult(
    IReadOnlyList<CodeSearchHit> Hits,
    int ScannedFiles,
    bool Truncated,
    TimeSpan Duration);

/// <summary>
/// Поиск по тексту модулей и XML прямо по файлам выгрузки: индекс для этого не нужен,
/// а сотни мегабайт исходников не хранятся в базе.
/// </summary>
/// <remarks>
/// Поиск ограничен по времени и по числу совпадений на файл: агенту нужен короткий ответ,
/// а не все вхождения по всей конфигурации. Модули BSL просматриваются раньше XML.
/// </remarks>
public sealed class CodeSearchService
{
    private static readonly HashSet<string> DefaultExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".bsl", ".xml", ".txt", ".md", ".json", ".csv", ".ini",
    };

    private readonly IDumpSource _source;

    public CodeSearchService(IDumpSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        _source = source;
    }

    public CodeSearchResult Search(string query, CodeSearchOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        options ??= new CodeSearchOptions();
        var stopwatch = Stopwatch.StartNew();

        var extensions = options.Extensions is { Count: > 0 }
            ? new HashSet<string>(options.Extensions, StringComparer.OrdinalIgnoreCase)
            : DefaultExtensions;

        Regex? regex = null;
        if (options.Regex)
        {
            regex = new Regex(
                query,
                (options.CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase) | RegexOptions.Compiled,
                TimeSpan.FromSeconds(2));
        }

        var comparison = options.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var hits = new List<CodeSearchHit>();
        var scanned = 0;
        var truncated = false;

        var candidates = _source.EnumerateFiles(cancellationToken)
            .Where(file => extensions.Contains(Path.GetExtension(file.RelativePath)))
            .OrderByDescending(static file => file.RelativePath.EndsWith(".bsl", StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var file in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (hits.Count >= options.MaxResults || stopwatch.Elapsed > options.Deadline)
            {
                truncated = hits.Count >= options.MaxResults || scanned < candidates.Count;
                break;
            }

            string[] lines;
            try
            {
                lines = ReadLines(file);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                continue;
            }

            scanned++;
            var inFile = 0;
            for (var index = 0; index < lines.Length && inFile < options.MaxMatchesPerFile; index++)
            {
                var isMatch = regex is not null ? regex.IsMatch(lines[index]) : lines[index].Contains(query, comparison);
                if (!isMatch)
                {
                    continue;
                }

                hits.Add(new CodeSearchHit(file.RelativePath, index + 1, lines[index].Trim(), BuildContext(lines, index, options.ContextLines)));
                inFile++;
                if (hits.Count >= options.MaxResults)
                {
                    truncated = true;
                    break;
                }
            }
        }

        stopwatch.Stop();
        return new CodeSearchResult(hits, scanned, truncated, stopwatch.Elapsed);
    }

    private string[] ReadLines(DumpFile file)
    {
        using var stream = _source.OpenRead(file);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd().Split('\n');
    }

    private static IReadOnlyList<string> BuildContext(string[] lines, int index, int contextLines)
    {
        if (contextLines <= 0)
        {
            return [];
        }

        var from = Math.Max(0, index - contextLines);
        var to = Math.Min(lines.Length - 1, index + contextLines);
        var result = new List<string>(to - from + 1);
        for (var line = from; line <= to; line++)
        {
            result.Add($"{line + 1}: {lines[line].TrimEnd()}");
        }

        return result;
    }
}
