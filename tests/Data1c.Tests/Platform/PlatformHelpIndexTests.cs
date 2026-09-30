using Data1c.Core.Platform;
using Xunit;

namespace Data1c.Tests.Platform;

public sealed class PlatformHelpIndexTests
{
    private static readonly Version Version27 = new(8, 3, 27, 2214);
    private static readonly Version Version25 = new(8, 3, 25, 1200);

    private static readonly (string Path, string Html)[] SyntaxTopics =
    [
        ("Массив.html", "<h1>Массив</h1><p>Массив значений произвольного типа.</p>"),
        ("Массив/Добавить.html", "<h1>Массив.Добавить</h1><p>Добавляет значение в конец массива.</p>"),
        ("Массив/Количество.html", "<h1>Массив.Количество</h1><p>Количество элементов массива.</p>"),
        ("ТаблицаЗначений.html", "<h1>ТаблицаЗначений (ValueTable)</h1><p>Таблица значений.</p>"),
        ("ТаблицаЗначений/Свернуть.html", "<h1>ТаблицаЗначений.Свернуть</h1><p>Сворачивает таблицу значений по колонкам.</p>"),
    ];

    private static readonly (string Path, string Html)[] QueryTopics =
    [
        ("ВЫБРАТЬ.html", "<h1>ВЫБРАТЬ</h1><p>Ключевое слово языка запросов.</p>"),
    ];

    [Fact]
    public void Строит_модель_из_источника()
    {
        var index = new PlatformHelpIndex(CreateSource(Version27));

        Assert.Equal(Version27, index.Version);
        Assert.True(index.IsAvailable);
        Assert.Equal(6, index.TopicCount);
        Assert.Empty(index.Warnings);
        Assert.Contains(index.Topics, static t => t.Kind == PlatformHelpKind.QueryLanguage && t.Name == "ВЫБРАТЬ");
        Assert.Equal("8.3.27.2214/SyntaxAssistant/Массив", index.Topics[0].Id);
    }

    [Fact]
    public void Выбирает_самую_свежую_установленную_версию()
    {
        var source = CreateSource(Version25, [("Массив/Старое.html", "<h1>Старое</h1><p>Старая версия.</p>")]);
        AddInstallation(source, Version27, SyntaxTopics);

        var index = new PlatformHelpIndex(source);

        Assert.Equal(Version27, index.Version);
        Assert.Contains(index.Topics, static t => t.Name == "Массив.Добавить");
        Assert.DoesNotContain(index.Topics, static t => t.Name == "Старое");
    }

    [Fact]
    public void Уважает_предпочтительную_версию()
    {
        var source = CreateSource(Version25);
        AddInstallation(source, Version27, SyntaxTopics);

        var index = new PlatformHelpIndex(source, new PlatformHelpOptions { PreferredVersion = Version25 });

        Assert.Equal(Version25, index.Version);
        Assert.Equal(6, index.TopicCount);
    }

    [Fact]
    public void Предупреждает_о_ненайденной_версии_и_берёт_свежую()
    {
        var index = new PlatformHelpIndex(
            CreateSource(Version27),
            new PlatformHelpOptions { PreferredVersion = new Version(8, 3, 99, 1) });

        Assert.Equal(Version27, index.Version);
        Assert.Contains(index.Warnings, static w => w.Contains("8.3.99.1", StringComparison.Ordinal));
    }

    [Fact]
    public void Сообщает_об_отсутствии_платформы()
    {
        var index = new PlatformHelpIndex(new InMemoryPlatformSource());

        Assert.Null(index.Version);
        Assert.False(index.IsAvailable);
        Assert.Equal(0, index.TopicCount);
        Assert.Contains(index.Warnings, static w => w.Contains("не найдены", StringComparison.Ordinal));
    }

    [Fact]
    public void Пропускает_битый_файл_и_читает_остальные()
    {
        var source = new InMemoryPlatformSource()
            .AddHelpFile(Version27, PlatformHelpKind.SyntaxAssistant, "bin/shcntx_ru.hbk", new byte[1024])
            .AddHelpFile(Version27, PlatformHelpKind.QueryLanguage, "bin/shquery_ru.hbk", HbkTestWriter.Create(QueryTopics));

        var index = new PlatformHelpIndex(source);

        Assert.Equal(1, index.TopicCount);
        Assert.Equal("ВЫБРАТЬ", index.Topics[0].Name);
        Assert.Contains(index.Warnings, static w => w.Contains("shcntx_ru.hbk", StringComparison.Ordinal));
    }

    [Fact]
    public void Ищет_по_имени_титулу_и_тексту()
    {
        var index = new PlatformHelpIndex(CreateSource(Version27));

        Assert.Equal("Массив.Добавить", index.Search("Добавить")[0].Name);
        Assert.Equal("Массив.Добавить", index.Search("Массив.Добавить")[0].Name);
        Assert.Contains(index.Search("Свернуть"), static t => t.Name == "ТаблицаЗначений.Свернуть");
        Assert.Contains(index.Search("колонкам"), static t => t.Name == "ТаблицаЗначений.Свернуть");
        Assert.Empty(index.Search("такого-точно-нет"));
        Assert.Empty(index.Search("   "));
    }

    [Fact]
    public void Отличает_метод_платформы_от_вызова_через_переменную()
    {
        var index = new PlatformHelpIndex(CreateSource(Version27));

        Assert.True(index.ContainsMember("Массив.Добавить"));
        Assert.True(index.ContainsMember("Массив"));
        Assert.False(index.ContainsMember("Объект.Добавить"));
        Assert.False(index.ContainsMember("НетТакогоТипа.Добавить"));
        Assert.False(index.ContainsMember("Массив.НетТакогоМетода"));
        Assert.False(index.ContainsMember(null));
        Assert.False(index.ContainsMember("  "));

        // Завершающие разделители нормализуются: «Массив.» — это «Массив».
        Assert.True(index.ContainsMember("Массив."));
    }

    [Fact]
    public void Отличает_страницу_типа_от_страницы_члена()
    {
        var index = new PlatformHelpIndex(CreateSource(Version27));

        Assert.True(index.IsKnownType("Массив"));
        Assert.True(index.IsKnownType("ТаблицаЗначений"));
        Assert.False(index.IsKnownType("Массив.Добавить"));
        Assert.False(index.IsKnownType("Объект"));
        Assert.False(index.IsKnownType(null));
    }

    [Fact]
    public void Название_переменной_совпавшее_со_страницей_свойства_не_делает_вызов_платформенным()
    {
        // В справке есть страницы свойств с именем без точки («Настройки», «ДополнительныеПараметры»),
        // но вызов «Настройки.Вставить()» — это вызов через переменную, а не метод платформы.
        var source = new InMemoryPlatformSource()
            .AddHelpFile(
                Version27,
                PlatformHelpKind.SyntaxAssistant,
                "bin/shcntx_ru.hbk",
                HbkTestWriter.Create(
                [
                    ("Массив.html", "<h1>Массив (Array)</h1><p>Массив значений.</p>"),
                    ("Массив/Добавить.html", "<h1>Массив.Добавить (Array.Add)</h1><p>Добавляет элемент.</p>"),
                    ("Настройки.html", "<h1>Настройки (Settings)</h1><p>Свойство с плоским именем.</p>"),
                    ("Вставить.html", "<h1>Вставить (Insert)</h1><p>Метод структуры.</p>"),
                ]));

        var index = new PlatformHelpIndex(source);

        Assert.True(index.ContainsMember("Массив.Добавить"));
        Assert.True(index.ContainsMember("Настройки"));
        Assert.False(index.ContainsMember("Настройки.Вставить"));
        Assert.False(index.ContainsMember("ДополнительныеПараметры.Вставить"));
    }

    [Fact]
    public void Находит_тему_по_имени_и_по_последнему_сегменту()
    {
        var index = new PlatformHelpIndex(CreateSource(Version27));

        Assert.Equal("Массив.Добавить", index.Find("Массив.Добавить")?.Name);
        Assert.Equal("Массив.Добавить", index.Find("Массив/Добавить")?.Name);
        Assert.Equal("Массив.Добавить", index.Find("НесуществующийТип.Добавить")?.Name);
        Assert.Null(index.Find("нет-такой-темы"));
        Assert.Null(index.Find(null));
    }

    [Fact]
    public void Повторное_обращение_не_перечитывает_источник()
    {
        var source = new CountingPlatformSource(CreateSource(Version27));

        var index = new PlatformHelpIndex(source);
        _ = index.TopicCount;
        _ = index.TopicCount;
        _ = index.Search("Массив", 5);

        Assert.Equal(2, source.OpenCount);
    }

    private static InMemoryPlatformSource CreateSource(Version version, (string Path, string Html)[]? syntax = null)
    {
        var source = new InMemoryPlatformSource($"тест {version}");
        return AddInstallation(source, version, syntax ?? SyntaxTopics);
    }

    private static InMemoryPlatformSource AddInstallation(
        InMemoryPlatformSource source,
        Version version,
        (string Path, string Html)[] syntaxTopics)
    {
        source.AddHelpFile(version, PlatformHelpKind.SyntaxAssistant, "bin/shcntx_ru.hbk", HbkTestWriter.Create(syntaxTopics), $@"C:\1cv8\{version}");
        source.AddHelpFile(version, PlatformHelpKind.QueryLanguage, "bin/shquery_ru.hbk", HbkTestWriter.Create(QueryTopics), $@"C:\1cv8\{version}");
        return source;
    }

    /// <summary>Считает открытия файлов, чтобы проверить ленивую загрузку и кеш.</summary>
    private sealed class CountingPlatformSource : IPlatformSource
    {
        private readonly InMemoryPlatformSource _inner;

        public CountingPlatformSource(InMemoryPlatformSource inner) => _inner = inner;

        public string DisplayName => _inner.DisplayName;

        public int OpenCount { get; private set; }

        public IEnumerable<PlatformInstallation> EnumerateInstallations() => _inner.EnumerateInstallations();

        public Stream OpenRead(PlatformInstallation installation, PlatformHelpFile file)
        {
            OpenCount++;
            return _inner.OpenRead(installation, file);
        }
    }
}
