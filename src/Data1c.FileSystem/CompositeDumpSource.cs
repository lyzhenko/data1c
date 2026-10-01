using Data1c.Core.Dump;

namespace Data1c.FileSystem;

/// <summary>Файл одного из источников составной выгрузки.</summary>
/// <param name="SourceIndex">Номер источника в порядке, заданном при создании.</param>
/// <param name="Source">Сам источник.</param>
/// <param name="File">Файл внутри источника.</param>
public sealed record SourcedDumpFile(int SourceIndex, IDumpSource Source, DumpFile File);

/// <summary>
/// Несколько выгрузок как одна: база конфигурации и расширения.
/// </summary>
/// <remarks>
/// Порядок источников — это порядок наложения: файл с одним и тем же относительным путём берётся
/// из последнего источника (эффективный код, как его видит 1С). Исключение — корневые файлы
/// конфигурации (<c>Configuration.xml</c>, <c>ConfigDumpInfo.xml</c>): они всегда берутся из базовой
/// выгрузки, иначе расширение подменило бы корень конфигурации.
/// <para>
/// Перекрытые версии не теряются: <see cref="EnumerateAll"/> отдаёт файлы всех источников, а
/// <see cref="IsOverridden"/> показывает, что путь есть более чем в одном источнике. Это нужно
/// текстовому поиску: в расширении 1С типична ситуация, когда adopted-модуль лежит по тому же пути,
/// что и в базе, и искать надо в обеих версиях.
/// </para>
/// </remarks>
public sealed class CompositeDumpSource : IDumpSource, IVersionedDumpSource
{
    /// <summary>Корневые файлы выгрузки, которые не перекрываются расширением.</summary>
    private static readonly HashSet<string> RootFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "Configuration.xml",
        "ConfigDumpInfo.xml",
    };

    private readonly IReadOnlyList<IDumpSource> _sources;
    private readonly Lazy<Dictionary<string, Entry>> _index;

    public CompositeDumpSource(IEnumerable<IDumpSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        _sources = [.. sources];
        if (_sources.Count == 0)
        {
            throw new ArgumentException("Нужен хотя бы один источник выгрузки.", nameof(sources));
        }

        _index = new Lazy<Dictionary<string, Entry>>(BuildIndex, isThreadSafe: true);
    }

    /// <summary>Источники в порядке наложения.</summary>
    public IReadOnlyList<IDumpSource> Sources => _sources;

    public string DisplayName => _sources.Count == 1
        ? _sources[0].DisplayName
        : string.Join(" + ", _sources.Select(static source => source.DisplayName));

    /// <summary>Файлы, эффективные для чтения кода: по одному на относительный путь.</summary>
    public IEnumerable<DumpFile> EnumerateFiles(CancellationToken cancellationToken = default)
    {
        foreach (var entry in Ordered())
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return entry.Winner.File;
        }
    }

    /// <summary>Все версии всех файлов: один и тот же путь может прийти и из базы, и из расширения.</summary>
    public IEnumerable<SourcedDumpFile> EnumerateAll(CancellationToken cancellationToken = default)
    {
        foreach (var entry in Ordered())
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return entry.Winner;

            if (entry.Overridden is null)
            {
                continue;
            }

            foreach (var version in entry.Overridden)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return version;
            }
        }
    }

    /// <summary>Версии файлов с источником каждой версии: поиск по тексту читает их напрямую.</summary>
    public IEnumerable<(int SourceIndex, IDumpSource Source, DumpFile File)> EnumerateVersions(
        CancellationToken cancellationToken = default)
    {
        foreach (var entry in EnumerateAll(cancellationToken))
        {
            yield return (entry.SourceIndex, entry.Source, entry.File);
        }
    }

    /// <summary>Путь есть более чем в одном источнике: файл перекрыт.</summary>
    public bool IsOverridden(string relativePath) =>
        _index.Value.TryGetValue(DumpPath.Normalize(relativePath), out var entry) && entry.Overridden is not null;

    /// <summary>Источник, из которого читается этот путь (победитель), или null.</summary>
    public IDumpSource? SourceOf(string relativePath) =>
        _index.Value.TryGetValue(DumpPath.Normalize(relativePath), out var entry) ? _sources[entry.Winner.SourceIndex] : null;

    public Stream OpenRead(DumpFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (!_index.Value.TryGetValue(DumpPath.Normalize(file.RelativePath), out var entry))
        {
            throw new FileNotFoundException($"Файл «{file.RelativePath}» отсутствует в источниках «{DisplayName}».", file.RelativePath);
        }

        return _sources[entry.Winner.SourceIndex].OpenRead(entry.Winner.File);
    }

    private IEnumerable<Entry> Ordered() =>
        _index.Value.Values.OrderBy(static entry => entry.Winner.File.RelativePath, StringComparer.Ordinal);

    private Dictionary<string, Entry> BuildIndex()
    {
        var index = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

        for (var sourceIndex = 0; sourceIndex < _sources.Count; sourceIndex++)
        {
            var source = _sources[sourceIndex];
            foreach (var file in source.EnumerateFiles())
            {
                var key = DumpPath.Normalize(file.RelativePath);
                if (!index.TryGetValue(key, out var entry))
                {
                    entry = new Entry(new SourcedDumpFile(sourceIndex, source, file));
                    index[key] = entry;
                    continue;
                }

                // Победитель — последний источник, кроме корневых файлов конфигурации: их не перекрываем.
                if (IsRootFile(key))
                {
                    entry.AddOverridden(new SourcedDumpFile(sourceIndex, source, file));
                }
                else
                {
                    entry.AddOverridden(entry.Winner);
                    entry.Winner = new SourcedDumpFile(sourceIndex, source, file);
                }
            }
        }

        return index;
    }

    private static bool IsRootFile(string normalizedPath) =>
        !normalizedPath.Contains('/') && RootFiles.Contains(normalizedPath);

    private sealed class Entry(SourcedDumpFile winner)
    {
        public SourcedDumpFile Winner { get; set; } = winner;

        /// <summary>Перекрытые версии; null, пока путь встречается только в одном источнике.</summary>
        public List<SourcedDumpFile>? Overridden { get; private set; }

        public void AddOverridden(SourcedDumpFile file) => (Overridden ??= []).Add(file);
    }
}
