using System.IO.Enumeration;
using Data1c.Core.Dump;

namespace Data1c.FileSystem;

/// <summary>Выгрузка, лежащая в каталоге файловой системы.</summary>
public sealed class FileSystemDumpSource : IDumpSource
{
    private static readonly EnumerationOptions Enumeration = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
        ReturnSpecialDirectories = false,
    };

    public FileSystemDumpSource(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        RootPath = Path.GetFullPath(rootPath);
        if (!Directory.Exists(RootPath))
        {
            throw new DirectoryNotFoundException($"Каталог выгрузки не найден: {RootPath}");
        }
    }

    public string RootPath { get; }

    public string DisplayName => RootPath;

    public IEnumerable<DumpFile> EnumerateFiles(CancellationToken cancellationToken = default)
    {
        var root = RootPath;

        // FileSystemEnumerable берёт размер и время правки прямо из записи каталога: отдельный
        // stat на каждый из десятков тысяч файлов выгрузки стоил несколько секунд на ровном месте.
        var files = new FileSystemEnumerable<DumpFile>(
            root,
            (ref FileSystemEntry entry) =>
            {
                // Путь собирается отсечением корня: GetRelativePath на 65 тысячах файлов
                // занимает секунды, а здесь нужен только хвост полного пути.
                var full = entry.ToFullPath();
                var relative = full.Length > root.Length && full.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                    ? full[(root.Length + 1)..]
                    : Path.GetRelativePath(root, full);
                return new DumpFile(relative.Replace('\\', '/'), entry.Length, entry.LastWriteTimeUtc);
            },
            Enumeration)
        {
            ShouldIncludePredicate = static (ref FileSystemEntry entry) => !entry.IsDirectory,
        };

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return file;
        }
    }

    /// <summary>
    /// Открывает файл с совместным доступом на чтение и запись: выгрузка 1С может ещё продолжаться,
    /// и часть файлов удерживается другим процессом.
    /// </summary>
    public Stream OpenRead(DumpFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        var path = Path.Combine(RootPath, file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
    }

    /// <summary>Один файл по пути: частичная переиндексация не должна обходить всю выгрузку.</summary>
    public DumpFile? FindFile(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        var path = Path.Combine(RootPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
        var info = new FileInfo(path);
        return info.Exists ? new DumpFile(relativePath, info.Length, info.LastWriteTimeUtc) : null;
    }
}
