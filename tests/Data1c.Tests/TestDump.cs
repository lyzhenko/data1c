using Data1c.Core.Dump;

namespace Data1c.Tests;

/// <summary>
/// Синтетическая выгрузка, разложенная по диску: нужна там, где проверяется настоящий файловый
/// источник — индексация, наблюдение за изменениями, работа сервера в режиме индекса.
/// </summary>
internal static class TestDump
{
    /// <summary>Создаёт временный каталог с файлами выгрузки и возвращает его путь.</summary>
    internal static string Materialize()
    {
        var root = Path.Combine(Path.GetTempPath(), "data1c-dump-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var source = SampleDump.Create();
        foreach (var file in source.EnumerateFiles())
        {
            var path = Path.Combine(root, file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var content = source.OpenRead(file);
            using var target = File.Create(path);
            content.CopyTo(target);
        }

        return root;
    }

    /// <summary>Убирает временный каталог; неудача очистки проверку не ломает.</summary>
    internal static void Remove(string root)
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
            // Каталог останется — на результат проверки это не влияет.
        }
        catch (UnauthorizedAccessException)
        {
            // То же самое: файл мог остаться занятым.
        }
    }
}
