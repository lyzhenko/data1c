using Data1c.Core.Platform;
using Xunit;

namespace Data1c.Tests.Platform;

/// <summary>
/// Проверка на реальном синтакс-помощнике установленной платформы 1С.
/// Тест пропускается (точнее, проходит вхолостую), если платформа не найдена,
/// поэтому его можно держать в обычном наборе: на машине разработчика он даёт настоящие цифры.
/// </summary>
public sealed class RealPlatformTests
{
    [Fact]
    public void Читает_справку_установленной_платформы()
    {
        var source = TryCreateLocalSource();
        if (source is null)
        {
            Console.WriteLine("Платформа 1С не найдена — проверка на реальной справке пропущена.");
            return;
        }

        var index = new PlatformHelpIndex(source);

        Console.WriteLine($"платформа: {index.Version} | файлов: {index.Installation?.Files.Count} | тем: {index.TopicCount}");
        foreach (var warning in index.Warnings)
        {
            Console.WriteLine("предупреждение: " + warning);
        }

        Assert.NotNull(index.Version);
        Assert.True(index.TopicCount > 100, $"тем найдено: {index.TopicCount}");
        Assert.Empty(index.Warnings);

        var first = index.Topics[0];
        Console.WriteLine($"первая тема: {first.Name} | {first.Title} | символов текста: {first.Text.Length}");
        foreach (var topic in index.Topics.Take(6))
        {
            Console.WriteLine($"  тема: {topic.Name} | {topic.Title}");
        }

        Console.WriteLine($"известен «Массив»: {index.KnownIdentifier("Массив")}");
        Console.WriteLine($"известен «Добавить»: {index.KnownIdentifier("Добавить")}");
        Console.WriteLine($"известен «Объект»: {index.KnownIdentifier("Объект")}");
        foreach (var probe in new[] { "Массив", "Массив.Добавить", "ТаблицаЗначений.Добавить", "Объект", "Объект.Добавить" })
        {
            var found = index.Find(probe);
            Console.WriteLine($"  Find({probe}) = {(found is null ? "не найдено" : $"{found.Name} | {found.Title}")}");
        }

        foreach (var probe in new[] { "Объект", "Массив.Добавить" })
        {
            var hits = index.Search(probe, 4);
            Console.WriteLine($"  Search({probe}): " + string.Join(" | ", hits.Select(static t => t.Title)));
        }

        var search = index.Search("ТаблицаЗначений", 5);
        Console.WriteLine("поиск «ТаблицаЗначений»: " + string.Join(" | ", search.Select(static t => t.Title)));

        Assert.True(index.ContainsMember("Массив.Добавить"), "метод платформы должен распознаваться");
        Assert.False(index.ContainsMember("Объект.Добавить"), "вызов через переменную не должен считаться платформенным");
    }

    private static IPlatformSource? TryCreateLocalSource()
    {
        string[] roots = [@"C:\Program Files\1cv8", @"C:\Program Files (x86)\1cv8"];
        var source = new LocalPlatformSource();

        foreach (var root in roots)
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

                var files = new List<PlatformHelpFile>();
                foreach (var file in Directory.EnumerateFiles(bin, "*.hbk"))
                {
                    var fileName = Path.GetFileName(file);
                    if (!PlatformHelpKinds.IsKnown(fileName))
                    {
                        continue;
                    }

                    files.Add(new PlatformHelpFile(
                        PlatformHelpKinds.FromFileName(fileName),
                        Path.Combine("bin", fileName),
                        new FileInfo(file).Length));
                }

                if (files.Count > 0)
                {
                    source.Add(new PlatformInstallation(version, directory, files));
                }
            }
        }

        return source.Count > 0 ? source : null;
    }

    private sealed class LocalPlatformSource : IPlatformSource
    {
        private readonly List<PlatformInstallation> _installations = [];

        public string DisplayName => "локальная установка 1С";

        public int Count => _installations.Count;

        public void Add(PlatformInstallation installation) => _installations.Add(installation);

        public IEnumerable<PlatformInstallation> EnumerateInstallations() => _installations;

        public Stream OpenRead(PlatformInstallation installation, PlatformHelpFile file) =>
            File.Open(Path.Combine(installation.RootPath, file.RelativePath), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    }
}
