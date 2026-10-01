using Data1c.Core.Analysis;
using Xunit;

namespace Data1c.Tests.Analysis;

/// <summary>
/// Проверяет предсказуемость распознавания приёмов: у каждой формулировки ровно один ожидаемый приём,
/// а формулировка вне каталога приёмом не подменяется.
/// </summary>
/// <remarks>
/// Источник данных — пустой: распознавание намерения не зависит от индекса, поэтому таблица формулировок
/// проверяется без сборки выгрузки и отвечает мгновенно. Поиск процедур под распознанный приём проверяется
/// отдельно, на индексе (<see cref="ConventionsCatalogTests"/> и <see cref="ConventionsTests"/>).
/// </remarks>
public sealed class ConventionsRecognitionTests
{
    /// <summary>Формулировки, которые агент пишет чаще всего, и приём, которым они должны распознаваться.</summary>
    private static readonly (string Phrase, string Intent)[] Phrases =
    [
        ("записать объект", "записать объект"),
        ("сохранить объект", "записать объект"),
        ("провести документ", "провести документ"),
        ("проведение документа", "провести документ"),
        ("получить реквизит объекта", "получить реквизит объекта"),
        ("прочитать реквизит товара", "получить реквизит объекта"),
        ("установить реквизит объекта", "установить реквизит объекта"),
        ("заполнить свойства объекта", "установить реквизит объекта"),
        ("найти по наименованию", "найти по наименованию"),
        ("найти товар по названию", "найти по наименованию"),
        ("найти по коду", "найти по коду или артикулу"),
        ("найти товар по артикулу", "найти по коду или артикулу"),
        ("прочитать данные запросом", "прочитать данные запросом"),
        ("выполнить запрос", "прочитать данные запросом"),
        ("записать набор записей регистра", "записать набор записей регистра"),
        ("записать движения регистра", "записать набор записей регистра"),
        ("вывести сообщение пользователю", "вывести сообщение пользователю"),
        ("сообщить об ошибке", "вывести сообщение пользователю"),
        ("выполнить на сервере", "выполнить на сервере или в фоне"),
        ("запустить в фоне", "выполнить на сервере или в фоне"),
        ("добавить в коллекцию", "добавить в коллекцию"),
        ("добавить строку в таблицу значений", "добавить в коллекцию"),
        ("сформировать печатную форму", "сформировать печатную форму"),
        ("вывести печатную форму", "сформировать печатную форму"),
        ("сохранить файл", "сохранить или прочитать файл"),
        ("прочитать файл", "сохранить или прочитать файл"),
        ("сериализовать в XML", "сериализовать в XML или JSON"),
        ("сохранить в JSON", "сериализовать в XML или JSON"),
        ("получить представление объекта", "получить представление объекта"),
        ("проверить заполнение реквизитов", "проверить заполнение реквизитов"),
        ("проверить обязательные реквизиты", "проверить заполнение реквизитов"),
        ("получить константу", "получить константу или настройку"),
        ("прочитать настройку программы", "получить константу или настройку"),
        ("замерить производительность", "замерить производительность"),
        ("замерить время выполнения", "замерить производительность"),
        ("записать в журнал регистрации", "записать в журнал регистрации"),
        ("записать ошибку в журнал", "записать в журнал регистрации"),
        ("выполнить в транзакции", "выполнить в транзакции"),
        ("начать транзакцию", "выполнить в транзакции"),
    ];

    [Fact]
    public void Формулировки_распознаются_одним_и_тем_же_приёмом()
    {
        var mistakes = new List<string>();
        foreach (var (phrase, expected) in Phrases)
        {
            var answer = Conventions.Suggest(new EmptyQuery(), phrase, limit: 3);
            var actual = answer.Recognition?.IntentId;
            if (!string.Equals(actual, expected, StringComparison.Ordinal))
            {
                mistakes.Add($"«{phrase}» → {actual ?? "(не распознан)"}, ожидался «{expected}»");
            }
        }

        Assert.True(mistakes.Count == 0, string.Join("; ", mistakes));
    }

    [Fact]
    public void Формулировка_вне_каталога_приёмом_не_подменяется()
    {
        string[] unknown = ["телепортировать документ", "починить принтер", "сделать красиво", "перекрасить кнопку"];

        foreach (var phrase in unknown)
        {
            var answer = Conventions.Suggest(new EmptyQuery(), phrase, limit: 3);
            Assert.Null(answer.Recognition!.IntentId);
            Assert.Equal(ConventionMatchStep.None, answer.Recognition.Step);
            Assert.Equal("поиск по словам запроса", answer.Intent);
        }
    }

    [Fact]
    public void Точное_имя_метода_платформы_распознаётся_по_методу()
    {
        var answer = Conventions.Suggest(new EmptyQuery(), "ЗначениеРеквизитаОбъекта", limit: 3);
        var recognition = answer.Recognition!;

        // Запрос одним именем метода — самый точный случай: ступень «метод платформы».
        Assert.Equal("получить реквизит объекта", recognition.IntentId);
        Assert.Equal(ConventionMatchStep.PlatformMethod, recognition.Step);
        Assert.Equal("ЗначениеРеквизитаОбъекта", recognition.PlatformMethod);
    }

    /// <summary>Источник без данных: распознаванию приёма индекс не нужен, ответ выходит мгновенно.</summary>
    private sealed class EmptyQuery : IConventionQuery
    {
        public IReadOnlyList<ConventionModuleUsage> RankRoutines(int limit) => [];

        public IReadOnlyList<ConventionSymbol> SymbolsOf(IReadOnlyCollection<string> routineIds) => [];

        public IReadOnlyList<ConventionCallSite> CallerSites(string routineId, int limit) => [];

        public IReadOnlyList<ConventionRoutine> PlatformMatches(string platformMethod, int limit) => [];

        public IReadOnlyList<string> MetadataKindsOf(string routineId) => [];

        public IReadOnlyList<string> RoutinesByNamePrefix(IReadOnlyCollection<string> namePrefixes, int limit) => [];

        public IReadOnlyList<string> RoutinesByTerms(IReadOnlyCollection<string> terms, int limit) => [];

        public IReadOnlyDictionary<string, string> ModuleOwners(IReadOnlyCollection<string> modulePaths) =>
            new Dictionary<string, string>(StringComparer.Ordinal);

        public IReadOnlyList<ConventionEvidence> EvidenceOf(IReadOnlyCollection<string> routineIds, string? platformMethod) => [];
    }
}
