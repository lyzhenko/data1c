using Data1c.Core.Analysis;
using Data1c.Core.Dump;
using Data1c.Core.Graph;
using Data1c.Store;
using Xunit;

namespace Data1c.Tests.Store;

/// <summary>
/// Поиск похожего кода по SQLite-индексу (Э2-5): кандидаты набираются по редким признакам,
/// поэтому «двойник» находится первой, а нерелевантная процедура в выдачу не попадает.
/// </summary>
public sealed class IndexSimilarCodeTests
{
    [Fact]
    public void Похожая_реализация_находится_первой()
    {
        using var fixture = new SimilarFixture();

        var found = fixture.Find(SimilarDump.Draft);

        Assert.NotEmpty(found.Candidates);
        var twin = found.Candidates[0];
        Assert.Equal(SimilarDump.TwinId, twin.Id);
        Assert.Equal(SimilarDump.TwinModulePath, twin.ModulePath);
        Assert.Equal("CommonModule.ОбработкаЗаказов", twin.OwnerId);
        Assert.Equal(2, twin.StartLine);
        Assert.True(twin.Score > 0.5, $"оценка двойника {twin.Score:0.00} слишком мала");

        Assert.Contains(twin.Matches, static match =>
            match.Signal == SimilarCodeSignal.PlatformCall && match.Value == "ТаблицаЗначений.Свернуть");
        Assert.Contains(twin.Matches, static match =>
            match.Signal == SimilarCodeSignal.PlatformCall && match.Value == "Запрос.Выполнить");

        // Обращение к справочнику найдено в тексте запроса: это и есть «похоже по смыслу», а не по буквам.
        Assert.Contains(twin.Matches, static match =>
            match.Signal == SimilarCodeSignal.MetadataReference
            && match.Value == "Catalog.Товары"
            && match.Detail == MetadataRefContexts.Query);

        Assert.Contains("вызовы платформы", twin.Reason, StringComparison.Ordinal);
        Assert.Contains("из запроса", twin.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Нерелевантная_процедура_в_выдачу_не_попадает()
    {
        using var fixture = new SimilarFixture();

        var found = fixture.Find(SimilarDump.Draft);

        Assert.DoesNotContain(found.Candidates, static candidate => candidate.Id == SimilarDump.IrrelevantId);
        Assert.All(found.Candidates, static candidate => Assert.NotEmpty(candidate.Matches));
    }

    [Fact]
    public void Совпадение_по_вызовам_весомее_совпадения_по_термам()
    {
        using var fixture = new SimilarFixture();

        var found = fixture.Find(SimilarDump.Draft);

        var twin = found.Candidates.Single(static candidate => candidate.Id == SimilarDump.TwinId);

        // Процедура, совпавшая только именем, в списке есть — но её оценка меньше.
        var byTerms = found.Candidates.Single(static candidate => candidate.Id == SimilarDump.TermsOnlyId);
        Assert.Contains(SimilarCodeSignal.Terms, byTerms.Matches.Select(static match => match.Signal));
        Assert.DoesNotContain(SimilarCodeSignal.PlatformCall, byTerms.Matches.Select(static match => match.Signal));
        Assert.DoesNotContain(SimilarCodeSignal.MetadataReference, byTerms.Matches.Select(static match => match.Signal));

        // Процедура с тем же справочником, но без общих вызовов платформы, стоит между ними.
        var byMetadata = found.Candidates.Single(static candidate => candidate.Id == SimilarDump.ReaderId);
        Assert.Contains(SimilarCodeSignal.MetadataReference, byMetadata.Matches.Select(static match => match.Signal));

        Assert.True(byTerms.Score < byMetadata.Score, "совпадение только по термам не должно обгонять обращения к метаданным");
        Assert.True(byMetadata.Score < twin.Score, "обращения к метаданным не должны обгонять совпадение вызовов");
    }

    [Fact]
    public void Предел_числа_кандидатов_соблюдается()
    {
        using var fixture = new SimilarFixture();

        var found = fixture.Find(SimilarDump.Draft, limit: 2);

        Assert.Equal(2, found.Candidates.Count);
        Assert.Equal(SimilarDump.TwinId, found.Candidates[0].Id);
        Assert.True(found.Considered >= found.Candidates.Count);
    }

    [Fact]
    public void Сама_процедура_из_выдачи_исключается()
    {
        using var fixture = new SimilarFixture();

        var found = fixture.Find(SimilarDump.Draft, excludeId: SimilarDump.TwinId);

        Assert.DoesNotContain(found.Candidates, static candidate => candidate.Id == SimilarDump.TwinId);
        Assert.NotEmpty(found.Candidates);
    }

    [Fact]
    public void Пустой_ответ_объясняется()
    {
        using var fixture = new SimilarFixture();

        var found = fixture.Find(SimilarDump.ForeignDraft);

        Assert.Empty(found.Candidates);
        Assert.NotEmpty(found.Notes);
        Assert.Contains(found.Notes, static note => note.Contains("не нашлось", StringComparison.Ordinal));
    }

    [Fact]
    public void Частый_вызов_в_выборку_кандидатов_не_идёт()
    {
        // Сорок процедур вызывают один и тот же метод платформы: такой признак не различает реализации,
        // и поиск обязан сказать об этом, а не выдать сорок случайных процедур.
        using var fixture = new SimilarFixture(SimilarDump.CreateWithFrequentCall());

        var found = fixture.Find(SimilarDump.Draft, limit: 50);

        Assert.Contains(found.Notes, static note => note.Contains("неразличающие", StringComparison.Ordinal));
        Assert.Contains(found.Notes, static note => note.Contains("Редких признаков", StringComparison.Ordinal));
        Assert.DoesNotContain(found.Candidates, static candidate => candidate.Score > 0.15);
    }

    [Fact]
    public void Признаки_кандидата_берутся_из_индекса()
    {
        using var fixture = new SimilarFixture();

        var profiles = fixture.Source.FindCandidates(SimilarCode.Describe(SimilarDump.Draft), 600, null);

        Assert.NotEmpty(profiles.Profiles);
        var twin = profiles.Profiles.Single(static profile => profile.Id == SimilarDump.TwinId);
        Assert.Equal(7, twin.Lines);
        Assert.Equal(2, twin.PlatformCalls.Count);
        Assert.Equal("Загружает товары по заказу.", twin.CommentHead);

        // Вес признака — обратная частота: у редкого вызова он больше, чем у частого.
        var platformWeights = profiles.Weights[SimilarCodeSignal.PlatformCall];
        Assert.True(platformWeights["ТаблицаЗначений.Свернуть"] > 0);
    }

    /// <summary>Индекс на синтетической выгрузке: поиск идёт ровно так, как в инструменте similar.</summary>
    private sealed class SimilarFixture : IDisposable
    {
        private readonly SqliteIndex _index;

        internal SimilarFixture(InMemoryDumpSource? source = null)
        {
            var dump = source ?? SimilarDump.Create();
            var analyzed = new DumpAnalyzer().Analyze(dump);
            _index = SqliteIndex.OpenInMemory();
            new IndexWriter(_index).Write(dump, analyzed);
            Source = new IndexSimilarCodeSource(_index);
            Engine = new SimilarCode(Source);
        }

        internal IndexSimilarCodeSource Source { get; }

        internal SimilarCode Engine { get; }

        internal SimilarCodeResult Find(string draft, int limit = 10, string? excludeId = null) =>
            // Вызовы черновика разрешает индекс — так же, как это делает инструмент similar.
            Engine.Find(SimilarCode.Describe(draft, null, Source.ResolveCall), limit, excludeId);

        public void Dispose() => _index.Dispose();
    }
}
