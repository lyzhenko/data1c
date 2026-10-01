namespace Data1c.Core.Platform;

/// <summary>Настройки индекса справки платформы.</summary>
public sealed record PlatformHelpOptions
{
    /// <summary>Нужная версия платформы. Если не найдена, берётся самая свежая из установленных.</summary>
    public Version? PreferredVersion { get; init; }

    /// <summary>Ограничение длины текста темы.</summary>
    public int MaxTopicTextLength { get; init; } = HbkTopicReader.DefaultMaxTextLength;

    /// <summary>Какие справочные файлы разбирать. Пустой набор — все известные.</summary>
    public IReadOnlyCollection<PlatformHelpKind> Kinds { get; init; } =
    [
        PlatformHelpKind.SyntaxAssistant,
        PlatformHelpKind.Language,
        PlatformHelpKind.QueryLanguage,
    ];
}

/// <summary>
/// Модель платформы 1С из установленной версии: темы синтакс-помощника, описания языка и языка запросов.
/// </summary>
/// <remarks>
/// Разбор ленивый: контейнеры .hbk (десятки мегабайт) читаются при первом обращении к темам,
/// поэтому создание индекса ничего не стоит, если модель платформы не понадобилась.
/// </remarks>
public sealed class PlatformHelpIndex
{
    private readonly IPlatformSource _source;
    private readonly PlatformHelpOptions _options;
    private readonly Lock _gate = new();
    private readonly List<string> _warnings = [];
    private readonly Dictionary<string, PlatformTopic> _byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PlatformTopic> _byLastSegment = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PlatformTopic> _byTitle = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PlatformTopic> _byTitleSegment = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PlatformTopic> _globalFunctions = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<PlatformInstallation>? _installations;
    private IReadOnlyList<PlatformTopic>? _topics;
    private PlatformInstallation? _installation;

    public PlatformHelpIndex(IPlatformSource source, PlatformHelpOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        _source = source;
        _options = options ?? new PlatformHelpOptions();
    }

    /// <summary>Описание источника справочных файлов.</summary>
    public string DisplayName => _source.DisplayName;

    /// <summary>Установленные версии платформы, от свежей к старой.</summary>
    public IReadOnlyList<PlatformInstallation> Installations =>
        _installations ??= [.. _source.EnumerateInstallations().OrderByDescending(static i => i.Version)];

    /// <summary>Версия, из которой построена модель.</summary>
    public PlatformInstallation? Installation
    {
        get
        {
            EnsureLoaded();
            return _installation;
        }
    }

    /// <summary>Версия платформы, из которой построена модель.</summary>
    public Version? Version => Installation?.Version;

    /// <summary>Темы справки.</summary>
    public IReadOnlyList<PlatformTopic> Topics
    {
        get
        {
            EnsureLoaded();
            return _topics!;
        }
    }

    public int TopicCount => Topics.Count;

    /// <summary>Есть ли хоть одна тема: без этого модель платформы бесполезна.</summary>
    public bool IsAvailable => TopicCount > 0;

    /// <summary>Замечания разбора (отсутствующая платформа, битые файлы и т. п.).</summary>
    public IReadOnlyList<string> Warnings
    {
        get
        {
            EnsureLoaded();
            return _warnings;
        }
    }

    /// <summary>Поиск тем по имени, заголовку и тексту.</summary>
    public IReadOnlyList<PlatformTopic> Search(string? query, int limit = 20)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        var text = query.Trim();
        var results = new List<(int Score, PlatformTopic Topic)>();
        foreach (var topic in Topics)
        {
            var score = Rank(topic, text);
            if (score >= 0)
            {
                results.Add((score, topic));
            }
        }

        return
        [
            .. results
                .OrderBy(static r => r.Score)
                .ThenBy(static r => r.Topic.Name, StringComparer.OrdinalIgnoreCase)
                .Take(Math.Max(1, limit))
                .Select(static r => r.Topic)
        ];
    }

    /// <summary>
    /// Находит тему по имени: сначала по внутреннему идентификатору, затем по человекочитаемому
    /// заголовку (в справке платформы заголовок — это «Массив.Добавить», а идентификатор темы —
    /// служебный, вроде <c>objects.catalog234.ValueTable.methods.Add110</c>).
    /// </summary>
    public PlatformTopic? Find(string? name)
    {
        var key = NormalizeQuery(name);
        if (key.Length == 0)
        {
            return null;
        }

        EnsureLoaded();
        return Lookup(_byName, key)
            ?? Lookup(_byTitle, key)
            ?? Lookup(_byLastSegment, LastSegment(key))
            ?? Lookup(_byTitleSegment, LastSegment(key));
    }

    /// <summary>
    /// Проверяет, что вызов относится к платформе. Признак один: в справке есть тема или заголовок
    /// с ровно таким именем. Методы платформы названы в справке полным именем
    /// (<c>Массив.Добавить (Array.Add)</c>, <c>Запрос.Выполнить</c>), поэтому правило точное:
    /// <c>Массив.Добавить</c> — платформа, а <c>Объект.Добавить</c> и
    /// <c>ДополнительныеПараметры.Вставить</c> (вызовы через переменные) — нет.
    /// </summary>
    public bool ContainsMember(string? callee)
    {
        var key = NormalizeQuery(callee);
        if (key.Length == 0)
        {
            return false;
        }

        EnsureLoaded();
        return _byName.ContainsKey(key) || _byTitle.ContainsKey(key);
    }

    /// <summary>
    /// Глобальная функция платформы по короткому имени: «Сообщить», «СтрШаблон», «СтрНайти».
    /// В справке такие функции названы с уточнением («Глобальный контекст.Сообщить»), поэтому
    /// <see cref="ContainsMember"/> по короткому имени их не находит: строка «Сообщить()» в модуле —
    /// это вызов глобальной функции, а не пользовательской процедуры.
    /// </summary>
    /// <param name="name">Короткое имя без точки.</param>
    public PlatformTopic? FindGlobalFunction(string? name)
    {
        var key = NormalizeQuery(name);
        if (key.Length == 0 || key.Contains('.'))
        {
            return null;
        }

        EnsureLoaded();
        return _globalFunctions.TryGetValue(key, out var topic) ? topic : null;
    }

    /// <summary>Известен ли такой идентификатор платформы: тип, метод, свойство или глобальная функция.</summary>
    public bool KnownIdentifier(string? name)
    {
        var key = NormalizeQuery(name);
        if (key.Length == 0)
        {
            return false;
        }

        EnsureLoaded();
        var segment = LastSegment(key);
        return _byName.ContainsKey(key)
            || _byTitle.ContainsKey(key)
            || _byLastSegment.ContainsKey(segment)
            || _byTitleSegment.ContainsKey(segment);
    }

    /// <summary>
    /// Есть ли в справке страница с таким именем без точки. Точка в заголовке означает страницу
    /// члена (<c>Массив.Добавить</c>), а без точки бывают и типы (<c>Массив</c>), и свойства
    /// (<c>Настройки</c>), поэтому для классификации вызовов этого признака недостаточно.
    /// </summary>
    public bool IsKnownType(string? name)
    {
        var key = NormalizeQuery(name);
        if (key.Length == 0 || key.Contains('.'))
        {
            return false;
        }

        EnsureLoaded();
        if (_byTitle.TryGetValue(key, out var byTitle))
        {
            return !byTitle.Title.Contains('.');
        }

        return _byName.TryGetValue(key, out var byName) && !byName.Name.Contains('.');
    }

    private static PlatformTopic? Lookup(Dictionary<string, PlatformTopic> map, string key) =>
        map.TryGetValue(key, out var topic) ? topic : null;

    private void EnsureLoaded()
    {
        if (_topics is not null)
        {
            return;
        }

        lock (_gate)
        {
            if (_topics is null)
            {
                LoadTopics();
            }
        }
    }

    private void LoadTopics()
    {
        var topics = new List<PlatformTopic>();
        var installations = Installations;
        var installation = SelectInstallation(installations);
        if (installation is null)
        {
            _warnings.Add("Установленные платформы 1С не найдены: модель платформы недоступна.");
            _topics = topics;
            return;
        }

        foreach (var file in installation.Files)
        {
            if (_options.Kinds.Count > 0 && !_options.Kinds.Contains(file.Kind))
            {
                continue;
            }

            try
            {
                using var stream = _source.OpenRead(installation, file);
                var payload = HbkArchive.TryReadFileStorage(stream);
                if (payload is null)
                {
                    _warnings.Add($"«{file.RelativePath}»: запись FileStorage не найдена, файл пропущен.");
                    continue;
                }

                using var zip = new MemoryStream(payload, writable: false);
                foreach (var topic in HbkTopicReader.ReadTopics(zip, _options.MaxTopicTextLength))
                {
                    var item = new PlatformTopic(
                        $"{installation.Version}/{file.Kind}/{topic.Name}",
                        topic.Name,
                        topic.Path,
                        topic.Title,
                        topic.Text,
                        file.Kind,
                        installation.Version);

                    topics.Add(item);
                    _byName.TryAdd(topic.Name, item);
                    _byLastSegment.TryAdd(LastSegment(topic.Name), item);
                    foreach (var alias in TitleAliases(topic.Title))
                    {
                        _byTitle.TryAdd(alias, item);
                        _byTitleSegment.TryAdd(LastSegment(alias), item);
                        if (TryGlobalFunctionName(alias, out var globalName))
                        {
                            _globalFunctions.TryAdd(globalName, item);
                        }
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException)
            {
                _warnings.Add($"«{file.RelativePath}»: {exception.Message}");
            }
        }

        _installation = installation;
        _topics = topics;
        if (topics.Count == 0)
        {
            _warnings.Add("Темы справки не найдены: возможно, изменился формат контейнера .hbk.");
        }
    }

    private PlatformInstallation? SelectInstallation(IReadOnlyList<PlatformInstallation> installations)
    {
        if (installations.Count == 0)
        {
            return null;
        }

        if (_options.PreferredVersion is { } preferred)
        {
            var exact = installations.FirstOrDefault(i => i.Version == preferred);
            if (exact is not null)
            {
                return exact;
            }

            _warnings.Add($"Платформа {preferred} не найдена, используется {installations[0].Version}.");
        }

        return installations[0];
    }

    private static int Rank(PlatformTopic topic, string query)
    {
        if (topic.Name.Equals(query, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (topic.Name.EndsWith("." + query, StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        if (topic.Title.Equals(query, StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }

        if (topic.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
        {
            return 3;
        }

        if (topic.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            return 4;
        }

        if (topic.Title.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            return 5;
        }

        return topic.Text.Contains(query, StringComparison.OrdinalIgnoreCase) ? 6 : -1;
    }

    private static string NormalizeQuery(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var text = value.Trim().Replace('\\', '.').Replace('/', '.');
        while (text.Contains("..", StringComparison.Ordinal))
        {
            text = text.Replace("..", ".", StringComparison.Ordinal);
        }

        return text.Trim('.');
    }

    private static string LastSegment(string name)
    {
        var separator = name.LastIndexOf('.');
        return separator < 0 ? name : name[(separator + 1)..];
    }

    /// <summary>
    /// Разбирает заголовок темы на псевдонимы: «КолонкаТаблицыЗначений (ValueTableColumn)» даёт
    /// «КолонкаТаблицыЗначений» и «ValueTableColumn», «Добавить» — само себя.
    /// </summary>
    private static IEnumerable<string> TitleAliases(string title)
    {
        var text = title.Trim();
        if (text.Length == 0)
        {
            yield break;
        }

        var open = text.IndexOf(" (", StringComparison.Ordinal);
        if (open > 0 && text.EndsWith(')'))
        {
            yield return text[..open].Trim();
            var inner = text[(open + 2)..^1].Trim();
            if (inner.Length > 0)
            {
                yield return inner;
            }

            yield break;
        }

        yield return text;
    }

    /// <summary>
    /// Выделяет короткое имя глобальной функции из псевдонима заголовка: «Глобальный контекст.Сообщить»
    /// даёт «Сообщить». Для остальных страниц возвращает <see langword="false"/>.
    /// </summary>
    private static bool TryGlobalFunctionName(string alias, out string name)
    {
        foreach (var prefix in GlobalContextPrefixes)
        {
            if (alias.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                name = alias[prefix.Length..].Trim();
                return name.Length > 0;
            }
        }

        name = string.Empty;
        return false;
    }

    /// <summary>Как в справке платформы назван раздел глобальных функций: русская и английская локаль.</summary>
    private static readonly string[] GlobalContextPrefixes = ["Глобальный контекст.", "Global context."];
}
