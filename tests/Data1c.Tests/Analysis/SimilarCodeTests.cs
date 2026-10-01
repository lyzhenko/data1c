using Data1c.Core.Analysis;
using Data1c.Core.Graph;
using Xunit;

namespace Data1c.Tests.Analysis;

/// <summary>
/// Правила похожести кода (Э2-5): признаки черновика, веса сигналов, предел выдачи и понятный
/// пустой ответ. Источник кандидатов здесь подставной — проверяются сами правила, без индекса.
/// </summary>
public sealed class SimilarCodeTests
{
    [Fact]
    public void Признаки_черновика_собираются_из_вызовов_обращений_и_запросов()
    {
        var features = SimilarCode.Describe(SimilarDump.Draft);

        Assert.Equal("СохранитьТоварыНаСклад", features.Name);
        Assert.Equal(7, features.Lines);
        Assert.True(features.Statements > 0);

        // Тип переменной выведен: «Таблица.Свернуть» — это метод платформы, а не вызов процедуры.
        Assert.Contains(features.PlatformCalls, static feature => feature.Value == "ТаблицаЗначений.Свернуть");
        Assert.Contains(features.PlatformCalls, static feature => feature.Value == "Запрос.Выполнить");

        var reference = Assert.Single(features.MetadataReferences);
        Assert.Equal("Catalog.Товары", reference.Value);
        Assert.Equal(MetadataRefContexts.Query, reference.Detail);

        Assert.Contains("товары", features.Terms);
        Assert.Contains("склад", features.Terms);
        Assert.Contains("сохранить", features.Terms);

        // Короткие слова («на») в поиск не идут: они не различают код.
        Assert.DoesNotContain("на", features.Terms);
    }

    [Fact]
    public void Совпадение_по_вызовам_весомее_совпадения_по_имени()
    {
        var features = SimilarCode.Describe(SimilarDump.Draft);
        var source = new FakeSource()
            .Add(Profile("routine:module:A.bsl#ДругоеИмя", "ДругоеИмя", features.Lines, features.Statements,
                platform: ["ТаблицаЗначений.Свернуть", "Запрос.Выполнить"]))
            .Add(Profile("routine:module:B.bsl#СохранитьТоварыНаСклад", "СохранитьТоварыНаСклад", features.Lines, features.Statements));

        var found = new SimilarCode(source).Find(features);

        Assert.Equal(2, found.Candidates.Count);
        var byCalls = found.Candidates[0];
        var byName = found.Candidates[1];

        Assert.Equal("ДругоеИмя", byCalls.Name);
        Assert.Equal([SimilarCodeSignal.PlatformCall], byCalls.Matches.Select(static match => match.Signal).Distinct());
        Assert.Equal([SimilarCodeSignal.Terms], byName.Matches.Select(static match => match.Signal).Distinct());
        Assert.True(byName.Score < byCalls.Score, "совпадение только по имени не должно обгонять вызовы");
        Assert.Contains("вызовы платформы", byCalls.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Признаки_без_веса_в_оценке_не_участвуют()
    {
        // Источник не дал веса признаку: он встречается у слишком многих процедур и не различает код.
        var features = SimilarCode.Describe(SimilarDump.Draft);
        var source = new FakeSource { Skip = ["ТаблицаЗначений.Свернуть", "Запрос.Выполнить"] }
            .Add(Profile("routine:module:A.bsl#ДругоеИмя", "ДругоеИмя", features.Lines, features.Statements,
                platform: ["ТаблицаЗначений.Свернуть", "Запрос.Выполнить"]));

        var found = new SimilarCode(source).Find(features);

        Assert.Empty(found.Candidates);
        Assert.NotEmpty(found.Notes);
        Assert.Equal(1, found.Considered);
    }

    [Fact]
    public void Штраф_за_объём_слабый()
    {
        var features = SimilarCode.Describe(SimilarDump.Draft);
        var source = new FakeSource()
            .Add(Profile("routine:module:A.bsl#Первая", "Первая", features.Lines, features.Statements,
                platform: ["ТаблицаЗначений.Свернуть", "Запрос.Выполнить"]))
            .Add(Profile("routine:module:B.bsl#Вторая", "Вторая", features.Lines * 10, features.Statements,
                platform: ["ТаблицаЗначений.Свернуть", "Запрос.Выполнить"]));

        var found = new SimilarCode(source).Find(features);

        var close = found.Candidates[0];
        var huge = found.Candidates[1];
        Assert.Equal("Первая", close.Name);

        // Даже десятикратная разница в объёме отнимает меньше десятой части оценки:
        // «гиганта» отсекает поправка на лишние признаки, а не штраф за размер.
        Assert.True(huge.Score > close.Score * 0.85, $"штраф за объём слишком велик: {close.Score:0.000} против {huge.Score:0.000}");
        Assert.True(huge.Score < close.Score);
        Assert.Contains("объём", huge.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Лишние_признаки_кандидата_гасят_оценку()
    {
        var features = SimilarCode.Describe(SimilarDump.Draft);
        var exact = Profile("routine:module:A.bsl#Точная", "Точная", features.Lines, features.Statements,
            platform: ["ТаблицаЗначений.Свернуть", "Запрос.Выполнить"]);
        var noisy = Profile("routine:module:B.bsl#Шумная", "Шумная", features.Lines, features.Statements,
            platform: ["ТаблицаЗначений.Свернуть", "Запрос.Выполнить", "Структура.Вставить", "Массив.Добавить",
                "Соответствие.Вставить", "СписокЗначений.Добавить", "ДеревоЗначений.Добавить", "ТабличныйДокумент.Вывести"]);

        var found = new SimilarCode(new FakeSource().Add(exact).Add(noisy)).Find(features);

        Assert.Equal("Точная", found.Candidates[0].Name);
        Assert.True(found.Candidates[0].Score > found.Candidates[1].Score);
    }

    [Fact]
    public void Предел_числа_кандидатов_соблюдается()
    {
        var features = SimilarCode.Describe(SimilarDump.Draft);
        var source = new FakeSource();
        for (var index = 1; index <= 5; index++)
        {
            source.Add(Profile($"routine:module:{index}.bsl#Процедура{index}", $"Процедура{index}", features.Lines, features.Statements,
                platform: ["ТаблицаЗначений.Свернуть", "Запрос.Выполнить"]));
        }

        var found = new SimilarCode(source).Find(features, limit: 2);

        Assert.Equal(2, found.Candidates.Count);
        Assert.Equal(5, found.Considered);
    }

    [Fact]
    public void Оценка_ниже_порога_в_ответ_не_попадает()
    {
        var features = SimilarCode.Describe(SimilarDump.Draft);
        var source = new FakeSource().Add(Profile("routine:module:A.bsl#СохранитьТоварыНаСклад",
            "СохранитьТоварыНаСклад", features.Lines, features.Statements));

        var found = new SimilarCode(source, new SimilarCodeOptions { MinScore = 0.9 }).Find(features);

        Assert.Empty(found.Candidates);
        Assert.NotEmpty(found.Notes);
    }

    [Fact]
    public void Пустой_ответ_объясняет_причину()
    {
        var features = SimilarCode.Describe(SimilarDump.Draft);

        var found = new SimilarCode(new FakeSource()).Find(features);

        Assert.Empty(found.Candidates);
        Assert.Contains(found.Notes, static note => note.Contains("не нашлось", StringComparison.Ordinal));
    }

    [Fact]
    public void Исключённая_процедура_в_ответ_не_попадает()
    {
        var features = SimilarCode.Describe(SimilarDump.Draft);
        var source = new FakeSource()
            .Add(Profile("routine:module:A.bsl#Первая", "Первая", features.Lines, features.Statements,
                platform: ["ТаблицаЗначений.Свернуть", "Запрос.Выполнить"]))
            .Add(Profile("routine:module:B.bsl#Вторая", "Вторая", features.Lines, features.Statements,
                platform: ["ТаблицаЗначений.Свернуть", "Запрос.Выполнить"]));

        var found = new SimilarCode(source).Find(features, limit: 10, excludeId: "routine:module:A.bsl#Первая");

        Assert.Single(found.Candidates);
        Assert.Equal("Вторая", found.Candidates[0].Name);
    }

    /// <summary>Процедура-кандидат с заданными признаками.</summary>
    private static SimilarCodeProfile Profile(
        string id,
        string name,
        int lines,
        int statements,
        IReadOnlyList<string>? platform = null,
        IReadOnlyList<string>? unresolved = null)
    {
        var start = 2;
        return new SimilarCodeProfile(
            id,
            name,
            "CommonModules/Модуль/Ext/Module.bsl",
            "CommonModule.Модуль",
            start,
            start + Math.Max(0, lines - 1),
            statements,
            [.. (platform ?? []).Select(static value => new SimilarCodeFeature(value))],
            [],
            [],
            [.. (unresolved ?? []).Select(static value => new SimilarCodeFeature(value))],
            0);
    }

    /// <summary>
    /// Подставной источник: отдаёт заранее заданные профили и веса признаков. Он повторяет поведение
    /// настоящего источника (Data1c.Store): вес получают только значимые, то есть редкие признаки.
    /// </summary>
    private sealed class FakeSource : ISimilarCodeSource
    {
        private readonly List<SimilarCodeProfile> _profiles = [];

        /// <summary>Признаки, которым источник намеренно не даёт веса: они слишком частые.</summary>
        internal IReadOnlyList<string> Skip { get; init; } = [];

        internal FakeSource Add(SimilarCodeProfile profile)
        {
            _profiles.Add(profile);
            return this;
        }

        public SimilarCodeCandidates FindCandidates(SimilarCodeFeatures features, int candidateLimit, string? excludeId)
        {
            var weights = new Dictionary<SimilarCodeSignal, IReadOnlyDictionary<string, double>>
            {
                [SimilarCodeSignal.PlatformCall] = Build(features.PlatformCalls),
                [SimilarCodeSignal.MetadataReference] = Build(features.MetadataReferences),
                [SimilarCodeSignal.RoutineCall] = Build(features.RoutineCalls),
                [SimilarCodeSignal.UnresolvedCall] = Build(features.UnresolvedCalls),
            };

            var profiles = _profiles
                .Where(profile => !string.Equals(profile.Id, excludeId, StringComparison.Ordinal))
                .Take(candidateLimit)
                .ToList();

            return new SimilarCodeCandidates(profiles, weights, []);
        }

        private Dictionary<string, double> Build(IReadOnlyList<SimilarCodeFeature> features)
        {
            var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var feature in features)
            {
                if (!Skip.Contains(feature.Value, StringComparer.OrdinalIgnoreCase))
                {
                    result[feature.Value] = 1;
                }
            }

            return result;
        }
    }
}
