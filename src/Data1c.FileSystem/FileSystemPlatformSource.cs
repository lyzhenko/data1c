using Data1c.Core.Platform;

namespace Data1c.FileSystem;

/// <summary>
/// Справочные файлы .hbk установленных платформ 1С на диске.
/// </summary>
/// <remarks>
/// Платформа ставит рядом два набора файлов: локализованный (<c>shcntx_ru.hbk</c>) и
/// общий (<c>shcntx_root.hbk</c>). Индексировать оба смысла нет — это дубли, и в общем
/// наборе текст не на языке пользователя. Поэтому для каждого вида файла выбирается
/// локализованный, а общий берётся только как запасной вариант.
/// </remarks>
public sealed class FileSystemPlatformSource : IPlatformSource
{
    private static readonly string[] DefaultRoots =
    [
        @"C:\Program Files\1cv8",
        @"C:\Program Files (x86)\1cv8",
    ];

    private readonly IReadOnlyList<string> _roots;
    private readonly string _locale;

    public FileSystemPlatformSource(string? locale = null, IEnumerable<string>? roots = null)
    {
        _roots = roots is null ? DefaultRoots : [.. roots];
        _locale = string.IsNullOrWhiteSpace(locale) ? "ru" : locale.Trim();
    }

    public string DisplayName => $"платформы 1С: {string.Join(", ", _roots)} (язык {_locale})";

    public IEnumerable<PlatformInstallation> EnumerateInstallations()
    {
        foreach (var root in _roots)
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (var directory in Directory.EnumerateDirectories(root))
            {
                if (!Version.TryParse(Path.GetFileName(directory), out var version))
                {
                    continue;
                }

                var bin = Path.Combine(directory, "bin");
                if (!Directory.Exists(bin))
                {
                    continue;
                }

                var files = SelectHelpFiles(bin);
                if (files.Count > 0)
                {
                    yield return new PlatformInstallation(version, directory, files);
                }
            }
        }
    }

    public Stream OpenRead(PlatformInstallation installation, PlatformHelpFile file)
    {
        ArgumentNullException.ThrowIfNull(installation);
        ArgumentNullException.ThrowIfNull(file);
        var path = Path.Combine(installation.RootPath, file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
    }

    private List<PlatformHelpFile> SelectHelpFiles(string bin)
    {
        var byKind = new Dictionary<PlatformHelpKind, List<(string Path, long Size)>>();
        foreach (var file in Directory.EnumerateFiles(bin, "*.hbk"))
        {
            var name = Path.GetFileName(file);
            var kind = PlatformHelpKinds.FromFileName(name);
            if (kind == PlatformHelpKind.Other)
            {
                continue;
            }

            if (!byKind.TryGetValue(kind, out var list))
            {
                list = [];
                byKind[kind] = list;
            }

            list.Add((Path.Combine("bin", name), new FileInfo(file).Length));
        }

        var result = new List<PlatformHelpFile>(byKind.Count);
        foreach (var (kind, candidates) in byKind)
        {
            // Приоритет: файл нужной локали, затем общий, затем любой оставшийся.
            var chosen = candidates.FirstOrDefault(c => c.Path.Contains($"_{_locale}.", StringComparison.OrdinalIgnoreCase));
            if (chosen.Path is null)
            {
                chosen = candidates.FirstOrDefault(static c => c.Path.Contains("_root.", StringComparison.OrdinalIgnoreCase));
            }

            if (chosen.Path is null)
            {
                chosen = candidates.OrderBy(static c => c.Path, StringComparer.OrdinalIgnoreCase).First();
            }

            result.Add(new PlatformHelpFile(kind, chosen.Path, chosen.Size));
        }

        result.Sort(static (a, b) => string.CompareOrdinal(a.RelativePath, b.RelativePath));
        return result;
    }
}
