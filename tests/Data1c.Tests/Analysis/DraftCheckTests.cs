using Data1c.Core.Analysis;
using Data1c.Core.Dump;
using Data1c.Core.Platform;
using Data1c.Store;
using Data1c.Tests.Platform;
using Xunit;

namespace Data1c.Tests.Analysis;

/// <summary>
/// Проверки черновика модуля (Э2-2): замечания обязаны приходить с номером строки и кодом,
/// чтобы агент нашёл место в Конфигураторе. Контекст — синтетическая выгрузка <see cref="SampleDump"/>
/// в SQLite-индексе, как в рабочем режиме сервера; справка платформы — синтетическая.
/// </summary>
public sealed class DraftCheckTests
{
    private static readonly Version PlatformVersion = new(8, 3, 27, 2214);

    [Fact]
    public void Неизвестная_процедура_находит_строку_и_код()
    {
        using var fixture = new Fixture();

        var result = fixture.Check(
            """
            Процедура Тест()
                РаботаСДанными.ЗагрузитьДанные();
                НеизвестнаяПроцедура();
            КонецПроцедуры
            """);

        var problem = Assert.Single(result.Problems);
        Assert.Equal(3, problem.Line);
        Assert.Equal(DraftProblemKind.UnknownProcedure, problem.Kind);
        Assert.Equal(DraftProblemSeverity.Error, problem.Severity);
        Assert.Contains("НеизвестнаяПроцедура", problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Без_справки_платформы_неизвестный_вызов_становится_предупреждением()
    {
        // Глобальные функции платформы («Сообщить») без справки неотличимы от опечатки,
        // поэтому замечание понижается до предупреждения и об этом пишется в оговорках.
        using var fixture = new Fixture(withPlatform: false);

        var result = fixture.Check(
            """
            Процедура Тест()
                Сообщить("текст");
            КонецПроцедуры
            """);

        var problem = Assert.Single(result.Problems);
        Assert.Equal(2, problem.Line);
        Assert.Equal(DraftProblemKind.UnknownProcedure, problem.Kind);
        Assert.Equal(DraftProblemSeverity.Warning, problem.Severity);
        Assert.Contains(result.Notes, static note => note.Contains("Справка платформы", StringComparison.Ordinal));
    }

    [Fact]
    public void Неверное_число_аргументов_находит_обе_строки()
    {
        using var fixture = new Fixture();

        var result = fixture.Check(
            """
            Процедура Тест()
                РаботаСДанными.ЗагрузитьДанные(1);
                ДругаяПроцедура(1, 2);
            КонецПроцедуры

            Процедура ДругаяПроцедура()
            КонецПроцедуры
            """);

        var problems = result.Problems.Where(static p => p.Kind == DraftProblemKind.WrongArgumentCount).ToList();
        Assert.Equal(2, problems.Count);
        Assert.Equal(2, problems[0].Line);
        Assert.Contains("ожидается параметров 0, передано 1", problems[0].Message, StringComparison.Ordinal);
        Assert.Equal(3, problems[1].Line);
        Assert.Contains("ожидается параметров 0, передано 2", problems[1].Message, StringComparison.Ordinal);
        Assert.All(problems, static p => Assert.Equal(DraftProblemSeverity.Error, p.Severity));
    }

    [Fact]
    public void Отсутствующий_объект_метаданных_получает_подсказку()
    {
        using var fixture = new Fixture();

        var result = fixture.Check(
            """
            Процедура Тест()
                Справочники.Товар.НайтиПоНаименованию("Тест");
            КонецПроцедуры
            """);

        var problem = Assert.Single(result.Problems);
        Assert.Equal(2, problem.Line);
        Assert.Equal(DraftProblemKind.UnknownMetadataObject, problem.Kind);
        Assert.Equal(DraftProblemSeverity.Error, problem.Severity);
        Assert.Contains("Справочники.Товар", problem.Message, StringComparison.Ordinal);
        Assert.Contains("Catalog.Товары", problem.Hint, StringComparison.Ordinal);
    }

    [Fact]
    public void Существующий_объект_метаданных_замечаний_не_даёт()
    {
        using var fixture = new Fixture();

        var result = fixture.Check(
            """
            Процедура Тест()
                Справочники.Товары.НайтиПоНаименованию("Тест");
            КонецПроцедуры
            """);

        Assert.Empty(result.Problems);
    }

    [Fact]
    public void Неиспользуемые_переменные_и_параметр_находят_свои_строки()
    {
        using var fixture = new Fixture();

        var result = fixture.Check(
            """
            Перем МодульнаяПеременная;

            Процедура Тест(НеиспользуемыйПараметр)
                Перем ЛокальнаяПеременная;
                Сообщить("текст");
            КонецПроцедуры
            """);

        Assert.Equal(3, result.Problems.Count);

        var module = result.Problems[0];
        Assert.Equal(1, module.Line);
        Assert.Equal(DraftProblemKind.UnusedVariable, module.Kind);
        Assert.Contains("МодульнаяПеременная", module.Message, StringComparison.Ordinal);

        var parameter = result.Problems[1];
        Assert.Equal(3, parameter.Line);
        Assert.Equal(DraftProblemKind.UnusedParameter, parameter.Kind);
        Assert.Contains("НеиспользуемыйПараметр", parameter.Message, StringComparison.Ordinal);

        var local = result.Problems[2];
        Assert.Equal(4, local.Line);
        Assert.Equal(DraftProblemKind.UnusedVariable, local.Kind);
        Assert.Contains("ЛокальнаяПеременная", local.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Функция_без_Возврат_получает_замечание()
    {
        using var fixture = new Fixture();

        var result = fixture.Check(
            """
            Функция БезВозврата()
                Перем Значение;
                Значение = 1;
            КонецФункции
            """);

        var problem = Assert.Single(result.Problems);
        Assert.Equal(1, problem.Line);
        Assert.Equal(DraftProblemKind.FunctionWithoutReturn, problem.Kind);
        Assert.Equal(DraftProblemSeverity.Error, problem.Severity);
        Assert.Contains("БезВозврата", problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Код_после_Возврат_считается_недостижимым()
    {
        using var fixture = new Fixture();

        var result = fixture.Check(
            """
            Функция Значение()
                Возврат 1;
                РаботаСДанными.ЗагрузитьДанные();
            КонецФункции
            """);

        var problem = Assert.Single(result.Problems);
        Assert.Equal(3, problem.Line);
        Assert.Equal(DraftProblemKind.CodeAfterReturn, problem.Kind);
        Assert.Equal(DraftProblemSeverity.Warning, problem.Severity);
    }

    [Fact]
    public void Процедура_вызванная_как_функция_получает_замечание()
    {
        using var fixture = new Fixture();

        var result = fixture.Check(
            """
            Процедура Тест()
                Результат = РаботаСДанными.ЗагрузитьДанные();
            КонецПроцедуры
            """);

        var problem = Assert.Single(result.Problems);
        Assert.Equal(2, problem.Line);
        Assert.Equal(DraftProblemKind.ProcedureUsedAsFunction, problem.Kind);
        Assert.Equal(DraftProblemSeverity.Error, problem.Severity);
    }

    [Fact]
    public void Вызовы_методов_платформы_замечаний_не_дают()
    {
        using var fixture = new Fixture();

        var result = fixture.Check(
            """
            Процедура Тест()
                Сообщить("привет");
                Массив = Новый Массив;
                Массив.Добавить("значение");
                ВызватьИсключение("ошибка");
            КонецПроцедуры
            """);

        Assert.Empty(result.Problems);
    }

    [Fact]
    public void Неверное_число_аргументов_метода_платформы_берётся_из_справки()
    {
        using var fixture = new Fixture();

        var result = fixture.Check(
            """
            Процедура Тест()
                Сообщить("привет", Ложь, 1);
            КонецПроцедуры
            """);

        var problem = Assert.Single(result.Problems);
        Assert.Equal(2, problem.Line);
        Assert.Equal(DraftProblemKind.WrongArgumentCount, problem.Kind);
        Assert.Contains("ожидается параметров от 1 до 2, передано 3", problem.Message, StringComparison.Ordinal);
        Assert.Contains("Справка платформы", problem.Hint, StringComparison.Ordinal);
    }

    [Fact]
    public void Чистый_модуль_не_даёт_замечаний()
    {
        using var fixture = new Fixture();

        var result = fixture.Check(
            """
            Перем Счётчик;

            Процедура Увеличить(Знач НаСколькоУгодно)
                Счётчик = Счётчик + НаСколькоУгодно;
                РаботаСДанными.ЗагрузитьДанные();
            КонецПроцедуры

            Функция Получить()
                Возврат Счётчик;
            КонецФункции
            """);

        Assert.True(result.IsClean);
        Assert.Equal(0, result.Count(DraftProblemSeverity.Error));
    }

    [Fact]
    public void Нехватка_аргументов_даёт_ошибку_с_числом_и_именами()
    {
        // В BSL необязательных параметров нет: и «ни одного из двух», и «один из двух» — ошибка,
        // и в сообщении должны быть и число, и имена, чтобы агент не гадал, что передать.
        using var fixture = new Fixture(dump: () => CreateArgumentsDump());

        var result = fixture.Check(
            """
            Процедура Тест()
                РаботаСДанными.ЗагрузитьДанные();
                РаботаСДанными.ЗагрузитьДанные("Ссылка");
            КонецПроцедуры
            """);

        Assert.Equal(2, result.Problems.Count);
        Assert.All(result.Problems, static problem =>
        {
            Assert.Equal(DraftProblemKind.WrongArgumentCount, problem.Kind);
            Assert.Equal(DraftProblemSeverity.Error, problem.Severity);
        });

        Assert.Equal(2, result.Problems[0].Line);
        Assert.Contains("ЗагрузитьДанные", result.Problems[0].Message, StringComparison.Ordinal);
        Assert.Contains("ожидается 2 (Ссылка, Режим), передано 0", result.Problems[0].Message, StringComparison.Ordinal);

        Assert.Equal(3, result.Problems[1].Line);
        Assert.Contains("ожидается 2 (Ссылка, Режим), передано 1", result.Problems[1].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Значения_по_умолчанию_не_считаются_нехваткой_аргументов()
    {
        // У «ЗагрузитьДанныеПоФильтру» обязателен только первый параметр: остальные объявлены
        // со значениями по умолчанию, поэтому вызов с одним и с двумя аргументами верен.
        using var fixture = new Fixture(dump: () => CreateArgumentsDump());

        var result = fixture.Check(
            """
            Процедура Тест()
                РаботаСДанными.ЗагрузитьДанныеПоФильтру("Ссылка");
                РаботаСДанными.ЗагрузитьДанныеПоФильтру("Ссылка", Истина);
                ОбработатьЛокально("Ссылка");
            КонецПроцедуры

            Процедура ОбработатьЛокально(Ссылка, Режим = Неопределено)
                Сообщить(Ссылка);
                Сообщить(Режим);
            КонецПроцедуры
            """);

        Assert.Empty(result.Problems);
    }

    [Fact]
    public void Нехватка_обязательных_аргументов_учитывает_значения_по_умолчанию()
    {
        using var fixture = new Fixture(dump: () => CreateArgumentsDump());

        var result = fixture.Check(
            """
            Процедура Тест()
                РаботаСДанными.ЗагрузитьДанныеПоФильтру();
            КонецПроцедуры
            """);

        var problem = Assert.Single(result.Problems);
        Assert.Equal(DraftProblemKind.WrongArgumentCount, problem.Kind);
        Assert.Equal(DraftProblemSeverity.Error, problem.Severity);
        Assert.Contains("ожидается 1 (Ссылка), передано 0", problem.Message, StringComparison.Ordinal);
        Assert.Contains("Режим, Дата", problem.Hint, StringComparison.Ordinal);
    }

    [Fact]
    public void Вызов_без_квалификатора_не_судится_по_одноимённому_методу_другого_модуля()
    {
        // Вызов без получателя адресует свой модуль или глобальный метод общего модуля; одноимённые
        // процедуры разных модулей объявлены по-разному, поэтому по первому совпадению не судим.
        using var fixture = new Fixture(dump: () => CreateArgumentsDump());

        var result = fixture.Check(
            """
            Процедура Тест()
                ЗагрузитьДанные("Ссылка");
            КонецПроцедуры
            """);

        Assert.Empty(result.Problems);
    }

    [Fact]
    public void Вызов_неэкспортного_метода_общего_модуля_даёт_ошибку()
    {
        using var fixture = new Fixture(dump: () => CreateArgumentsDump());

        var result = fixture.Check(
            """
            Процедура Тест()
                РаботаСДанными.ВнутренняяОбработка();
            КонецПроцедуры
            """);

        var problem = Assert.Single(result.Problems);
        Assert.Equal(2, problem.Line);
        Assert.Equal(DraftProblemKind.MethodNotExported, problem.Kind);
        Assert.Equal(DraftProblemSeverity.Error, problem.Severity);
        Assert.Contains("РаботаСДанными.ВнутренняяОбработка", problem.Message, StringComparison.Ordinal);
        Assert.Contains("не экспортирован", problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Вызов_неэкспортного_метода_из_своего_модуля_разрешён()
    {
        // На реальной выгрузке таких вызовов сотни тысяч: свой модуль видит свои же процедуры
        // без «Экспорт», и замечание на них было бы ложным.
        using var fixture = new Fixture(dump: () => CreateArgumentsDump());

        var result = fixture.Check(
            """
            Процедура Тест()
                РаботаСДанными.ВнутренняяОбработка();
            КонецПроцедуры
            """,
            SampleDump.SecondCommonModuleBslPath);

        Assert.Empty(result.Problems);
    }

    [Fact]
    public void Параметры_обработчиков_формы_не_считаются_неиспользуемыми()
    {
        // Обработчики формы зарегистрированы в её описании: параметры им задаёт платформа,
        // поэтому «Отказ», «Элемент» и «Команда» не обязаны употребляться в теле процедуры.
        using var fixture = new Fixture(dump: () => FormSampleDump.Create());

        var result = fixture.Check(FormSampleDump.FormModuleBsl, FormSampleDump.FormModulePath);

        Assert.Empty(result.Problems);
    }

    [Fact]
    public void Неиспользуемый_параметр_зарегистрированного_обработчика_не_даёт_замечания()
    {
        // Имя параметра здесь нештатное: признак «это обработчик» берётся из данных формы,
        // а не из списка привычных имён.
        var source = FormSampleDump.Create();
        source.AddText(
            FormSampleDump.FormModulePath,
            """
            &НаКлиенте
            Процедура ПриОткрытии(ДополнительныйКонтекст)
            	Сообщить("Открытие");
            КонецПроцедуры
            """);

        using var fixture = new Fixture(dump: () => source);

        var result = fixture.Check(
            """
            &НаКлиенте
            Процедура ПриОткрытии(ДополнительныйКонтекст)
            	Сообщить("Открытие");
            КонецПроцедуры
            """,
            FormSampleDump.FormModulePath);

        Assert.Empty(result.Problems);
    }

    [Fact]
    public void Параметры_штатных_обработчиков_не_считаются_неиспользуемыми_без_данных_формы()
    {
        // Формы в конфигурации может ещё не быть: тогда обработчик узнаётся по штатным параметрам.
        using var fixture = new Fixture();

        var result = fixture.Check(
            """
            Процедура ПередЗаписью(Отказ, СтандартнаяОбработка)
                Сообщить("запись");
            КонецПроцедуры
            """);

        Assert.Empty(result.Problems);
    }

    [Fact]
    public void Обычный_неиспользуемый_параметр_остаётся_замечанием()
    {
        using var fixture = new Fixture();

        var result = fixture.Check(
            """
            Процедура Обработать(Ссылка, Режим)
                Сообщить(Ссылка);
            КонецПроцедуры
            """);

        var problem = Assert.Single(result.Problems);
        Assert.Equal(DraftProblemKind.UnusedParameter, problem.Kind);
        Assert.Equal(DraftProblemSeverity.Warning, problem.Severity);
        Assert.Contains("Режим", problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Черновик_без_пути_проверяется_по_контексту_в_памяти()
    {
        // Путь у черновика может отсутствовать, а индекс — ещё не собраться: тогда факты берутся
        // из готового разбора в памяти (AnalysisDraftContext).
        var analysis = new DumpAnalyzer().Analyze(SampleDump.Create());
        var check = new DraftCheck(new AnalysisDraftContext(analysis));

        var result = check.Check(
            """
            Процедура Тест()
                Справочники.Товар.НайтиПоНаименованию("Тест");
                РаботаСДанными.ЗагрузитьДанные(1);
            КонецПроцедуры
            """);

        Assert.Equal(2, result.Problems.Count);
        Assert.Equal(DraftProblemKind.UnknownMetadataObject, result.Problems[0].Kind);
        Assert.Equal(2, result.Problems[0].Line);
        Assert.Equal(DraftProblemKind.WrongArgumentCount, result.Problems[1].Kind);
        Assert.Equal(3, result.Problems[1].Line);
    }

    [Fact]
    public void Замечания_идут_по_возрастанию_строки()
    {
        using var fixture = new Fixture();

        var result = fixture.Check(
            """
            Перем Ненужная;

            Процедура Тест()
                НеизвестнаяПроцедура();
                Справочники.Товар.НайтиПоНаименованию("Тест");
            КонецПроцедуры
            """);

        Assert.Equal(3, result.Problems.Count);
        Assert.Equal(new[] { 1, 4, 5 }, result.Problems.Select(static p => p.Line).ToArray());
    }

    /// <summary>
    /// Выгрузка <see cref="SampleDump"/> с другим общим модулем «РаботаСДанными»: в нём есть процедура
    /// с двумя параметрами и процедура без «Экспорт» — на них проверяются аргументы и доступность.
    /// </summary>
    private static InMemoryDumpSource CreateArgumentsDump()
    {
        var source = SampleDump.Create();
        source.AddText(SampleDump.SecondCommonModuleBslPath, ArgumentsModuleBsl);
        return source;
    }

    private const string ArgumentsModuleBsl = """
        Процедура ЗагрузитьДанные(Ссылка, Режим) Экспорт
            Сообщить(Ссылка);
        КонецПроцедуры

        Процедура ЗагрузитьДанныеПоФильтру(Ссылка, Режим = Неопределено, Дата = Неопределено) Экспорт
            Сообщить(Ссылка);
        КонецПроцедуры

        Процедура ВнутренняяОбработка()
            Сообщить("внутри");
        КонецПроцедуры
        """;

    /// <summary>Выгрузка <see cref="SampleDump"/> в SQLite-индексе плюс синтетическая справка платформы.</summary>
    private sealed class Fixture : IDisposable
    {
        private readonly SqliteIndex _index;

        internal Fixture(bool withPlatform = true, Func<IDumpSource>? dump = null)
        {
            var source = (dump ?? (() => SampleDump.Create()))();
            var analysis = new DumpAnalyzer().Analyze(source);
            _index = SqliteIndex.OpenInMemory();
            new IndexWriter(_index) { IncludeComments = false }.Write(source, analysis);
            Context = new IndexDraftContext(new IndexReader(_index));
            Platform = withPlatform ? CreatePlatform() : null;
        }

        internal IndexDraftContext Context { get; }

        internal PlatformHelpIndex? Platform { get; }

        internal DraftCheckResult Check(string text, string? path = null) =>
            new DraftCheck(Context, Platform).Check(text, path);

        public void Dispose() => _index.Dispose();

        /// <summary>Мини-справка платформы: метод типа и глобальная функция с разделами «Синтаксис» и «Параметры».</summary>
        private static PlatformHelpIndex CreatePlatform()
        {
            var source = new InMemoryPlatformSource("синтетическая справка");
            source.AddHelpFile(
                PlatformVersion,
                PlatformHelpKind.SyntaxAssistant,
                "bin/shcntx_ru.hbk",
                HbkTestWriter.Create(
                [
                    // Имена тем — служебные, как в настоящем .hbk: короткое имя метода живёт в заголовке.
                    ("objects.catalog234.Array.methods.Add772.html", "<h1>Массив.Добавить (Array.Add)</h1><p>Синтаксис: Добавить(&lt;Значение&gt;) Параметры: &lt;Значение&gt; (необязательный) Тип: Произвольный. Описание: Добавляет значение в массив.</p>"),
                    ("objects.Global context.methods.catalog27.Message30.html", "<h1>Глобальный контекст.Сообщить (Global context.Message)</h1><p>Синтаксис: Сообщить(&lt;Текст&gt;, &lt;Статус&gt;) Параметры: &lt;Текст&gt; (обязательный) Тип: Строка. &lt;Статус&gt; (необязательный) Тип: СтатусСообщения. Описание: Выводит сообщение.</p>"),
                ]));
            return new PlatformHelpIndex(source);
        }
    }
}
