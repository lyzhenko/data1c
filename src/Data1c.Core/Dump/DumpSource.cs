using System.Text;

namespace Data1c.Core.Dump;

/// <summary>Файл внутри выгрузки конфигурации 1С.</summary>
/// <param name="RelativePath">Путь относительно корня выгрузки, разделители — «/».</param>
/// <param name="Size">Размер в байтах (может быть устаревшим для файла, который пишется прямо сейчас).</param>
/// <param name="LastWriteTimeUtc">Время последнего изменения, если источник его предоставляет.</param>
public sealed record DumpFile(string RelativePath, long Size, DateTimeOffset LastWriteTimeUtc)
{
    /// <summary>Имя файла с расширением.</summary>
    public string FileName => DumpPath.GetFileName(RelativePath);

    /// <summary>Расширение файла вместе с точкой, в нижнем регистре.</summary>
    public string Extension => DumpPath.GetExtension(RelativePath);

    public override string ToString() => RelativePath;
}

/// <summary>
/// Абстракция источника выгрузки. Библиотека не знает, откуда берутся файлы:
/// каталог на диске, zip-архив, база или тестовый набор в памяти.
/// </summary>
public interface IDumpSource
{
    /// <summary>Человекочитаемое описание источника (путь, имя архива и т. п.).</summary>
    string DisplayName { get; }

    /// <summary>Перечисляет все файлы выгрузки. Порядок не гарантируется.</summary>
    IEnumerable<DumpFile> EnumerateFiles(CancellationToken cancellationToken = default);

    /// <summary>
    /// Открывает файл на чтение. Вызывающий обязан освободить поток.
    /// Реализация может выбросить <see cref="IOException"/>, если файл занят другим процессом.
    /// </summary>
    Stream OpenRead(DumpFile file);
}

/// <summary>Операции над путями внутри выгрузки (всегда «/», без ведущего слэша).</summary>
public static class DumpPath
{
    public static string Normalize(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var normalized = path.Replace('\\', '/').Trim();
        while (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        return normalized.TrimStart('/');
    }

    public static string GetFileName(string path)
    {
        var normalized = Normalize(path);
        var index = normalized.LastIndexOf('/');
        return index < 0 ? normalized : normalized[(index + 1)..];
    }

    public static string GetExtension(string path)
    {
        var name = GetFileName(path);
        var index = name.LastIndexOf('.');
        return index < 0 ? string.Empty : name[index..].ToLowerInvariant();
    }

    public static string GetDirectory(string path)
    {
        var normalized = Normalize(path);
        var index = normalized.LastIndexOf('/');
        return index < 0 ? string.Empty : normalized[..index];
    }

    public static string GetFileNameWithoutExtension(string path)
    {
        var name = GetFileName(path);
        var index = name.LastIndexOf('.');
        return index < 0 ? name : name[..index];
    }

    /// <summary>Первые <paramref name="count"/> сегментов пути, склеенные через «/».</summary>
    public static string TakeSegments(string path, int count)
    {
        var normalized = Normalize(path);
        var segments = normalized.Split('/');
        if (segments.Length <= count)
        {
            return normalized;
        }

        return string.Join('/', segments, 0, count);
    }

    /// <summary>Сегменты пути без пустых элементов.</summary>
    public static string[] Segments(string path) =>
        Normalize(path).Split('/', StringSplitOptions.RemoveEmptyEntries);
}

/// <summary>Выгрузка в памяти: удобна для тестов и для сборки графа из заранее загруженных данных.</summary>
public sealed class InMemoryDumpSource : IDumpSource
{
    private readonly Dictionary<string, Entry> _files = new(StringComparer.OrdinalIgnoreCase);

    public InMemoryDumpSource(string displayName = "<memory>") => DisplayName = displayName;

    public string DisplayName { get; }

    public int Count => _files.Count;

    public InMemoryDumpSource AddText(string relativePath, string content, DateTimeOffset? lastWriteTimeUtc = null) =>
        AddBytes(relativePath, Encoding.UTF8.GetBytes(content), lastWriteTimeUtc);

    public InMemoryDumpSource AddBytes(string relativePath, byte[] content, DateTimeOffset? lastWriteTimeUtc = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        var key = DumpPath.Normalize(relativePath);
        _files[key] = new Entry(content, lastWriteTimeUtc ?? DateTimeOffset.UnixEpoch);
        return this;
    }

    public IEnumerable<DumpFile> EnumerateFiles(CancellationToken cancellationToken = default)
    {
        foreach (var (path, entry) in _files.OrderBy(static p => p.Key, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new DumpFile(path, entry.Content.Length, entry.LastWriteTimeUtc);
        }
    }

    public Stream OpenRead(DumpFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (!_files.TryGetValue(file.RelativePath, out var entry))
        {
            throw new FileNotFoundException($"Файл «{file.RelativePath}» отсутствует в источнике «{DisplayName}».", file.RelativePath);
        }

        return new MemoryStream(entry.Content, writable: false);
    }

    private readonly record struct Entry(byte[] Content, DateTimeOffset LastWriteTimeUtc);
}
