using Data1c.Core.Dump;

namespace Data1c.Store;

/// <summary>Изменения выгрузки относительно последней сборки индекса.</summary>
/// <param name="Added">Файлы, которых в индексе нет.</param>
/// <param name="Changed">Файлы, у которых изменился размер или время правки.</param>
/// <param name="Removed">Файлы, которые из индекса пропали.</param>
/// <param name="Total">Сколько файлов сейчас в выгрузке.</param>
public sealed record DumpChange(int Added, int Changed, int Removed, int Total)
{
    /// <summary>Индекс соответствует выгрузке.</summary>
    public bool IsEmpty => Added == 0 && Changed == 0 && Removed == 0;

    public override string ToString() => IsEmpty
        ? $"изменений нет (файлов {Total:N0})"
        : $"добавлено {Added:N0}, изменено {Changed:N0}, удалено {Removed:N0} (файлов {Total:N0})";
}

/// <summary>Сравнение выгрузки с индексом: нужно, чтобы понять, устарел ли индекс.</summary>
public static class DumpState
{
    /// <summary>
    /// Сравнивает список файлов выгрузки с тем, что записан в индексе. Время правки хранится
    /// в миллисекундах, поэтому сравнивается точно: пересборка не запускается на ровном месте.
    /// </summary>
    public static DumpChange Compare(IndexReader reader, IDumpSource source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(source);

        var known = reader.ReadFileStates();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var added = 0;
        var changed = 0;
        var total = 0;

        foreach (var file in source.EnumerateFiles(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            total++;
            seen.Add(file.RelativePath);

            if (!known.TryGetValue(file.RelativePath, out var state))
            {
                added++;
                continue;
            }

            if (state.Size != file.Size || state.Mtime != file.LastWriteTimeUtc.ToUnixTimeMilliseconds())
            {
                changed++;
            }
        }

        var removed = known.Keys.Count(path => !seen.Contains(path));
        return new DumpChange(added, changed, removed, total);
    }
}
