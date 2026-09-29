using Data1c.Core.Dump;

namespace Data1c.Cli;

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
        foreach (var path in Directory.EnumerateFiles(RootPath, "*", Enumeration))
        {
            cancellationToken.ThrowIfCancellationRequested();

            FileInfo info;
            try
            {
                info = new FileInfo(path);
            }
            catch (IOException)
            {
                continue;
            }

            var relative = Path.GetRelativePath(RootPath, path).Replace('\\', '/');
            yield return new DumpFile(relative, info.Exists ? info.Length : 0, info.Exists ? info.LastWriteTimeUtc : DateTimeOffset.UnixEpoch);
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
}
