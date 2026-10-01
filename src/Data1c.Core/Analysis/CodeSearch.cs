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

    /// <summary>Ограничить поиск префиксами путей внутри выгрузки: Reports/, Documents/Заказ/.</summary>
    public IReadOnlyList<string>? PathPrefixes { get; init; }

    /// <summary>
    /// Готовый список файлов вместо обхода источника: сервер берёт его из индекса, и поиск
    /// не тратит секунды на обход десятков тысяч файлов выгрузки.
    /// </summary>
    public IReadOnlyList<string>? Paths { get; init; }
}

/// <summary>Одно совпадение в тексте.</summary>
/// <param name="Path">Путь файла внутри выгрузки.</param>
/// <param name="Line">Номер строки (1-based).</param>
/// <param name="Text">Строка, в которой найдено совпадение.</param>
/// <param name="Context">Строки вокруг совпадения с номерами.</param>
/// <param name="SourceIndex">Номер источника: у перекрытых файлов база и расширение дают разные номера.</param>
public sealed record CodeSearchHit(string Path, int Line, string Text, IReadOnlyList<string> Context, int SourceIndex = 0);

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
        var prefixes = options.PathPrefixes is { Count: > 0 } ? options.PathPrefixes : null;
        var candidates = EnumerateCandidates(extensions, prefixes, options.Paths, cancellationToken)
            .OrderByDescending(static entry => entry.File.RelativePath.EndsWith(".bsl", StringComparison.OrdinalIgnoreCase))
            .ThenBy(static entry => entry.File.RelativePath, StringComparer.Ordinal)
            .ThenBy(static entry => entry.SourceIndex)
            .ToList();

        var hits = new List<CodeSearchHit>();
        var perFile = new List<CodeSearchHit>?[candidates.Count];
        var scanned = 0;
        var total = 0;
        var stop = 0;

        // Файлы читаются параллельно: сотни мегабайт модулей иначе просматриваются слишком долго.
        // Порядок выдачи задаётся порядком файлов, поэтому ответ не зависит от расписания потоков.
        Parallel.For(
            0,
            candidates.Count,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Environment.ProcessorCount,
                CancellationToken = cancellationToken,
            },
            index =>
            {
                if (Volatile.Read(ref stop) == 1 || stopwatch.Elapsed > options.Deadline)
                {
                    return;
                }

                var (sourceIndex, fileSource, file) = candidates[index];
                string[] lines;
                try
                {
                    lines = ReadLines(fileSource, file);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
                {
                    return;
                }

                Interlocked.Increment(ref scanned);
                var found = new List<CodeSearchHit>();
                for (var line = 0; line < lines.Length && found.Count < options.MaxMatchesPerFile; line++)
                {
                    if ((line & 0x3FF) == 0 && Volatile.Read(ref stop) == 1)
                    {
                        break;
                    }

                    var isMatch = regex is not null ? regex.IsMatch(lines[line]) : lines[line].Contains(query, comparison);
                    if (!isMatch)
                    {
                        continue;
                    }

                    found.Add(new CodeSearchHit(
                        file.RelativePath,
                        line + 1,
                        lines[line].Trim(),
                        BuildContext(lines, line, options.ContextLines),
                        sourceIndex));
                }

                if (found.Count == 0)
                {
                    return;
                }

                perFile[index] = found;
                if (Interlocked.Add(ref total, found.Count) >= options.MaxResults)
                {
                    Volatile.Write(ref stop, 1);
                }
            });

        for (var index = 0; index < perFile.Length && hits.Count < options.MaxResults; index++)
        {
            if (perFile[index] is not { } found)
            {
                continue;
            }

            foreach (var hit in found)
            {
                if (hits.Count >= options.MaxResults)
                {
                    break;
                }

                hits.Add(hit);
            }
        }

        stopwatch.Stop();
        var truncated = hits.Count >= options.MaxResults || Volatile.Read(ref scanned) < candidates.Count;
        return new CodeSearchResult(hits, Volatile.Read(ref scanned), truncated, stopwatch.Elapsed);
    }

    /// <summary>
    /// Файлы для поиска. У составного источника берутся все версии, включая перекрытые: иначе
    /// версия из базы не попала бы в выдачу. Каждая версия читается из своего источника.
    /// </summary>
    private IEnumerable<(int SourceIndex, IDumpSource Source, DumpFile File)> EnumerateCandidates(
        IReadOnlySet<string> extensions,
        IReadOnlyList<string>? prefixes,
        IReadOnlyList<string>? paths,
        CancellationToken cancellationToken)
    {
        if (_source is IVersionedDumpSource versioned)
        {
            foreach (var entry in versioned.EnumerateVersions(cancellationToken))
            {
                if (Accept(entry.File, extensions, prefixes))
                {
                    yield return entry;
                }
            }

            yield break;
        }

        var files = _source.EnumerateFiles(cancellationToken);
        if (paths is { Count: > 0 } prepared)
        {
            foreach (var path in prepared)
            {
                var file = new DumpFile(path, 0, DateTimeOffset.UnixEpoch);
                if (Accept(file, extensions, prefixes))
                {
                    yield return (0, _source, file);
                }
            }

            yield break;
        }

        foreach (var file in files)
        {
            if (Accept(file, extensions, prefixes))
            {
                yield return (0, _source, file);
            }
        }
    }

    private static bool Accept(DumpFile file, IReadOnlySet<string> extensions, IReadOnlyList<string>? prefixes) =>
        extensions.Contains(Path.GetExtension(file.RelativePath))
        && (prefixes is null
            || prefixes.Any(prefix => file.RelativePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));

    private static string[] ReadLines(IDumpSource source, DumpFile file)
    {
        using var stream = source.OpenRead(file);
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
