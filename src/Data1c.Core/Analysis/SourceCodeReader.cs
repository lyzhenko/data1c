using Data1c.Core.Dump;

namespace Data1c.Core.Analysis;

/// <summary>Фрагмент исходного текста файла выгрузки.</summary>
/// <param name="Path">Путь файла внутри выгрузки.</param>
/// <param name="StartLine">Номер первой строки фрагмента (1-based).</param>
/// <param name="EndLine">Номер последней строки фрагмента (1-based).</param>
/// <param name="TotalLines">Всего строк в файле.</param>
/// <param name="Lines">Строки фрагмента без переводов строк.</param>
/// <param name="Truncated">Фрагмент обрезан ограничением числа строк.</param>
public sealed record CodeFragment(
    string Path,
    int StartLine,
    int EndLine,
    int TotalLines,
    IReadOnlyList<string> Lines,
    bool Truncated);

/// <summary>
/// Читает исходные тексты файлов выгрузки по требованию: модули BSL и XML объектов.
/// Используется просмотрщиком, чтобы показывать код выбранной процедуры, не держа
/// в памяти все 628 МБ модулей.
/// </summary>
/// <remarks>
/// Читать можно только файлы, которые есть в источнике (белый список строится по
/// <see cref="IDumpSource.EnumerateFiles"/>), и только текстовые расширения. Прочитанные
/// модули кешируются: последние <see cref="CacheCapacity"/> файлов остаются в памяти.
/// </remarks>
public sealed class SourceCodeReader
{
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".bsl", ".xml", ".txt", ".html", ".htm", ".md", ".json", ".ini", ".csv",
    };

    private readonly IDumpSource _source;
    private readonly Dictionary<string, string[]> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _cacheOrder = new();
    private readonly Lock _gate = new();
    private readonly Lazy<HashSet<string>> _knownPaths;

    public SourceCodeReader(IDumpSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        _source = source;
        _knownPaths = new Lazy<HashSet<string>>(
            () => [.. source.EnumerateFiles().Select(static file => file.RelativePath)],
            isThreadSafe: true);
    }

    /// <summary>Сколько файлов остаётся в кеше.</summary>
    public int CacheCapacity { get; init; } = 16;

    /// <summary>Максимальное число строк, возвращаемых за один запрос (хватает на самый большой модуль выгрузки).</summary>
    public int MaxLines { get; init; } = 40_000;

    /// <summary>Есть ли такой файл в выгрузке и поддерживается ли он для чтения.</summary>
    public bool Contains(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return false;
        }

        var path = DumpPath.Normalize(relativePath);
        return TextExtensions.Contains(DumpPath.GetExtension(path)) && _knownPaths.Value.Contains(path);
    }

    /// <summary>
    /// Возвращает строки файла в диапазоне <paramref name="startLine"/>..<paramref name="endLine"/>.
    /// Границы приводятся к допустимым; для отсутствующих или нечитаемых файлов возвращается null.
    /// </summary>
    public CodeFragment? Read(string? relativePath, int startLine = 1, int endLine = 0)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || !Contains(relativePath))
        {
            return null;
        }

        var path = DumpPath.Normalize(relativePath);
        string[] lines;
        try
        {
            lines = GetLines(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Файл мог быть занят выгрузкой 1С или удалён между запросами.
            return null;
        }

        var total = lines.Length;
        var from = Math.Clamp(startLine, 1, Math.Max(1, total));
        var to = endLine <= 0 ? total : Math.Clamp(endLine, from, total);
        var truncated = to - from + 1 > MaxLines;
        if (truncated)
        {
            to = from + MaxLines - 1;
        }

        var slice = new string[to - from + 1];
        Array.Copy(lines, from - 1, slice, 0, slice.Length);
        return new CodeFragment(path, from, to, total, slice, truncated);
    }

    private string[] GetLines(string path)
    {
        lock (_gate)
        {
            if (_cache.TryGetValue(path, out var cached))
            {
                return cached;
            }
        }

        var lines = ReadAllLines(path);

        lock (_gate)
        {
            if (_cache.TryGetValue(path, out var cached))
            {
                return cached;
            }

            _cache[path] = lines;
            _cacheOrder.Enqueue(path);
            while (_cacheOrder.Count > Math.Max(1, CacheCapacity) && _cacheOrder.Count > 0)
            {
                var oldest = _cacheOrder.Dequeue();
                if (!string.Equals(oldest, path, StringComparison.OrdinalIgnoreCase))
                {
                    _cache.Remove(oldest);
                }
            }

            return lines;
        }
    }

    private string[] ReadAllLines(string path)
    {
        var file = new DumpFile(path, 0, DateTimeOffset.UnixEpoch);
        using var stream = _source.OpenRead(file);

        // Читатель сам выбирает кодировку: UTF-8 (с BOM или без) либо запасная CP1251.
        return DumpTextReader.ReadLines(stream);
    }
}
