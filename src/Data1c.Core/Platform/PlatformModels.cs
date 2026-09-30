namespace Data1c.Core.Platform;

/// <summary>Вид справочного файла установленной платформы 1С.</summary>
public enum PlatformHelpKind
{
    /// <summary>Файл, который мы не разбираем.</summary>
    Other = 0,

    /// <summary>Синтакс-помощник: объекты платформы, их методы и свойства (<c>shcntx_*.hbk</c>).</summary>
    SyntaxAssistant,

    /// <summary>Описание встроенного языка (<c>shlang_*.hbk</c>).</summary>
    Language,

    /// <summary>Описание языка запросов (<c>shquery_*.hbk</c>).</summary>
    QueryLanguage,
}

/// <summary>Определение вида справочного файла по имени.</summary>
public static class PlatformHelpKinds
{
    private static readonly string[] Prefixes = ["shcntx", "shlang", "shquery"];

    /// <summary>Файлы, которые имеет смысл индексировать, для русской локали платформы.</summary>
    public static IReadOnlyList<string> PreferredFileNames { get; } = ["shcntx_ru.hbk", "shlang_ru.hbk", "shquery_ru.hbk"];

    public static PlatformHelpKind FromFileName(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        var name = fileName.Trim();
        if (name.StartsWith("shcntx", StringComparison.OrdinalIgnoreCase))
        {
            return PlatformHelpKind.SyntaxAssistant;
        }

        if (name.StartsWith("shlang", StringComparison.OrdinalIgnoreCase))
        {
            return PlatformHelpKind.Language;
        }

        if (name.StartsWith("shquery", StringComparison.OrdinalIgnoreCase))
        {
            return PlatformHelpKind.QueryLanguage;
        }

        return PlatformHelpKind.Other;
    }

    /// <summary>Файл относится к одному из видов, которые мы разбираем.</summary>
    public static bool IsKnown(string fileName) => FromFileName(fileName) != PlatformHelpKind.Other;

    /// <summary>Все известные префиксы имён справочных файлов.</summary>
    public static IEnumerable<string> KnownPrefixes => Prefixes;
}

/// <summary>Справочный файл внутри установленной платформы.</summary>
/// <param name="Kind">Вид файла.</param>
/// <param name="RelativePath">Путь относительно каталога платформы (обычно <c>bin\shcntx_ru.hbk</c>).</param>
/// <param name="Size">Размер в байтах.</param>
public sealed record PlatformHelpFile(PlatformHelpKind Kind, string RelativePath, long Size);

/// <summary>Установленная версия платформы 1С с найденными справочными файлами.</summary>
/// <param name="Version">Версия платформы, например 8.3.27.2214.</param>
/// <param name="RootPath">Каталог установки версии.</param>
/// <param name="Files">Справочные файлы этой версии.</param>
public sealed record PlatformInstallation(Version Version, string RootPath, IReadOnlyList<PlatformHelpFile> Files);

/// <summary>Тема справки платформы.</summary>
/// <param name="Id">Устойчивый идентификатор: версия, вид файла и имя темы.</param>
/// <param name="Name">Имя темы, нормализованное в точечный путь (<c>Массив.Добавить</c>).</param>
/// <param name="Path">Исходный путь темы внутри контейнера (для диагностики).</param>
/// <param name="Title">Заголовок темы.</param>
/// <param name="Text">Текст темы без разметки.</param>
/// <param name="Kind">Из какого файла пришла тема.</param>
/// <param name="Version">Версия платформы.</param>
public sealed record PlatformTopic(
    string Id,
    string Name,
    string Path,
    string Title,
    string Text,
    PlatformHelpKind Kind,
    Version Version);

/// <summary>
/// Источник справочных файлов платформы. Библиотека не знает, откуда они берутся:
/// с диска (реализация в CLI), из архива или из памяти (тесты).
/// </summary>
public interface IPlatformSource
{
    /// <summary>Человекочитаемое описание источника.</summary>
    string DisplayName { get; }

    /// <summary>Найденные установки платформы; порядок не гарантируется.</summary>
    IEnumerable<PlatformInstallation> EnumerateInstallations();

    /// <summary>Открывает справочный файл на чтение. Поток освобождает вызывающий.</summary>
    Stream OpenRead(PlatformInstallation installation, PlatformHelpFile file);
}

/// <summary>Источник справочных файлов в памяти: нужен для тестов и для сборки индекса из готовых данных.</summary>
public sealed class InMemoryPlatformSource : IPlatformSource
{
    private readonly List<Entry> _entries = [];

    public InMemoryPlatformSource(string displayName = "<memory>") => DisplayName = displayName;

    public string DisplayName { get; }

    public int Count => _entries.Count;

    /// <summary>Добавляет содержимое справочного файла (сырые байты контейнера .hbk).</summary>
    public InMemoryPlatformSource AddHelpFile(
        Version version,
        PlatformHelpKind kind,
        string relativePath,
        byte[] content,
        string rootPath = "<memory>")
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(content);
        _entries.Add(new Entry(version, rootPath, new PlatformHelpFile(kind, relativePath, content.Length), content));
        return this;
    }

    public IEnumerable<PlatformInstallation> EnumerateInstallations()
    {
        foreach (var group in _entries.GroupBy(static e => (e.Version, e.RootPath)))
        {
            yield return new PlatformInstallation(
                group.Key.Version,
                group.Key.RootPath,
                [.. group.Select(static e => e.File).OrderBy(static f => f.RelativePath, StringComparer.Ordinal)]);
        }
    }

    public Stream OpenRead(PlatformInstallation installation, PlatformHelpFile file)
    {
        ArgumentNullException.ThrowIfNull(installation);
        ArgumentNullException.ThrowIfNull(file);
        var entry = _entries.FirstOrDefault(e =>
            e.Version == installation.Version &&
            string.Equals(e.File.RelativePath, file.RelativePath, StringComparison.OrdinalIgnoreCase));

        if (entry is null)
        {
            throw new FileNotFoundException($"Файл «{file.RelativePath}» отсутствует в источнике «{DisplayName}».", file.RelativePath);
        }

        return new MemoryStream(entry.Content, writable: false);
    }

    private sealed record Entry(Version Version, string RootPath, PlatformHelpFile File, byte[] Content);
}
