using Data1c.Core.Analysis;
using Data1c.Core.Dump;
using Data1c.Store;
using Xunit;

namespace Data1c.Tests.Analysis;

/// <summary>
/// Проверяет разбор конвенций конфигурации: рейтинг процедур по числу вызовов, распознавание
/// типовых приёмов по намерению агента и поиск по точному методу платформы.
/// </summary>
/// <remarks>
/// Выгрузка своя, а не общая <see cref="SampleDump"/>: для рейтинга нужны процедуры с разным
/// числом вызовов, а для приёма «записать объект» — вызов «Записать» с известной строкой.
/// </remarks>
public sealed class ConventionsTests
{
    [Fact]
    public void Рейтинг_процедур_сортируется_по_числу_вызовов()
    {
        using var fixture = new ConventionFixture();
        var ranked = fixture.Query.RankRoutines(limit: 10);

        Assert.True(ranked.Count >= 3, $"процедур в рейтинге: {ranked.Count}");
        Assert.Equal("ОбщаяПроцедура", ranked[0].Name);
        Assert.True(ranked[0].Uses >= 3, $"вызовов: {ranked[0].Uses}");
        Assert.Equal(ConventionsTests.ModulePath, ranked[0].ModulePath);
        Assert.Equal("CommonModule.ОбщийМодуль", ranked[0].OwnerId);

        // Порядок строго по убыванию: рейтинг нужен именно для ответа «кто чаще используется».
        for (var index = 1; index < ranked.Count; index++)
        {
            Assert.True(
                ranked[index - 1].Uses >= ranked[index].Uses,
                $"порядок нарушен на {index}: {ranked[index - 1].Uses} < {ranked[index].Uses}");
        }

        Assert.DoesNotContain(ranked, static item => item.Name == "НикемНеВызванная");
        Assert.All(ranked, static item => Assert.NotEmpty(item.ModulePath));

        // В рейтинг попадают только процедуры: вызовы из кода модуля (вне процедур) не должны
        // подменять собой узел процедуры именем вида «Ext» или «Module.bsl».
        Assert.All(ranked, static item => Assert.Contains("#", item.RoutineId, StringComparison.Ordinal));
        Assert.All(ranked, static item => Assert.NotEqual("Ext", item.Name));
    }

    [Fact]
    public void Намерение_записать_объект_находит_процедуру_с_вызовом_Записать()
    {
        using var fixture = new ConventionFixture();
        var answer = fixture.Answer(intent: "записать объект");

        // Одно и то же имя бывает в нескольких модулях: в ответе всегда есть модуль, иначе совет
        // «так уже делают» некуда применить.
        Assert.All(answer.Routines, static routine => Assert.NotEmpty(routine.ModulePath));

        var found = Assert.Single(
            answer.Routines,
            routine => routine.Name == "ЗаписатьТовар" && routine.ModulePath == ConventionsTests.ModulePath);
        Assert.Equal(Conventions.Intents.Single(static intent => intent.Id == "записать объект").Title, answer.Intent);
        Assert.Contains("Записать", found.CallMethod, StringComparison.Ordinal);
        Assert.Contains("Записать", found.ExampleDetail, StringComparison.Ordinal);
        Assert.NotNull(found.Symbol);
        Assert.True(found.Symbol.IsExport);
        Assert.NotNull(found.Reasons);
        Assert.Contains(found.Reasons, static reason => reason.Contains("Записать", StringComparison.Ordinal));

        // Пример вызова: модуль и строка. Приёмка требует именно их: агент открывает код инструментом
        // code по модулю и строке и видит, как приём применяют в конфигурации.
        Assert.False(string.IsNullOrEmpty(found.ExampleModule));
        Assert.NotNull(found.ExampleLine);
        Assert.NotNull(found.ExampleDetail);
        Assert.NotNull(found.Symbol.CommentHead);
        Assert.Contains(ConventionsTests.ModulePath, answer.Modules.Select(static module => module.ModulePath));
    }

    [Fact]
    public void Намерение_найти_по_наименованию_находит_поиск_по_наименованию()
    {
        using var fixture = new ConventionFixture();
        var answer = fixture.Answer(intent: "найти по наименованию");

        Assert.Contains(answer.Routines, static routine => routine.Name == "НайтиТовар");
        Assert.Contains("наименованию", answer.Intent, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Намерение_вывести_сообщение_находит_процедуру_с_Сообщить()
    {
        using var fixture = new ConventionFixture();
        var answer = fixture.Answer(intent: "вывести сообщение пользователю");

        var found = Assert.Single(answer.Routines, static routine => routine.Name == "ПредупредитьПользователя");
        Assert.Contains("Сообщить", found.CallMethod, StringComparison.Ordinal);
    }

    [Fact]
    public void Точный_метод_платформы_находит_вызовы_с_позициями()
    {
        using var fixture = new ConventionFixture();
        var answer = fixture.Answer(platform: "Записать");

        Assert.Equal("Записать", answer.PlatformMethod);
        Assert.Contains(
            answer.Routines,
            static routine => routine.Name == "ЗаписатьТовар" && routine.ModulePath == ConventionsTests.ModulePath);

        var found = answer.Routines.Single(routine => routine.Name == "ЗаписатьТовар" && routine.ModulePath == ConventionsTests.ModulePath);
        Assert.True(found.Uses >= 1, $"вызовов: {found.Uses}");

        // Позиции: у процедуры есть своя строка с вызовом «Записать», и у её вызывающих — свои.
        Assert.Contains(answer.CallSites, site => site.RoutineId.EndsWith("#ЗаписатьТовар", StringComparison.Ordinal) && site.Line == ConventionsTests.WriteLine);
        Assert.Contains(answer.CallSites, static site => site.Detail?.Contains("Записать", StringComparison.Ordinal) == true);
        Assert.All(answer.CallSites, static site => Assert.NotNull(site.Line));

        // Вызовы процедуры конфигурации с похожим именем в ответ не попадают: «ЗаписатьТовар»
        // и «РаботаСДанными.ЗагрузитьДанные» — это не метод платформы «Записать».
        var names = answer.Routines.Select(static routine => routine.Name).ToList();
        Assert.DoesNotContain("ЗагрузитьДанные", names);

        // Метод, который в конфигурации никто не вызывает, даёт пустой ответ с понятным объяснением.
        var empty = fixture.Answer(platform: "ТакогоМетодаНет");
        Assert.Empty(empty.Routines);
        Assert.Empty(empty.CallSites);
        Assert.Contains("Ничего не найдено", empty.Hint, StringComparison.Ordinal);
    }

    [Fact]
    public void Пустой_ответ_объясняет_причину()
    {
        using var fixture = new ConventionFixture();

        var unknown = fixture.Answer(intent: "телепортировать документ");
        Assert.Empty(unknown.Routines);
        Assert.NotEmpty(unknown.Hint);
        Assert.Contains("Ничего не найдено", unknown.Hint, StringComparison.Ordinal);
        Assert.DoesNotContain("code", unknown.Hint, StringComparison.Ordinal);
        Assert.NotNull(unknown.Note);
        Assert.Contains("не распознан", unknown.Note, StringComparison.Ordinal);

        // Неизвестный метод платформы: процедур нет, но рейтинг модулей всё равно показывает,
        // где в конфигурации вообще сосредоточена основная работа.
        var platform = fixture.Answer(platform: "НикемНеВызываемыйМетод");
        Assert.Empty(platform.Routines);
        Assert.Empty(platform.CallSites);
        Assert.Contains("platform", platform.Hint, StringComparison.Ordinal);
        Assert.NotEmpty(platform.Modules);
        Assert.Null(platform.Note);
    }

    [Fact]
    public void Лимит_соблюдается_и_на_рейтинге_и_на_примерах()
    {
        using var fixture = new ConventionFixture();

        var limited = fixture.Answer(intent: "записать объект", limit: 1);
        Assert.True(limited.Routines.Count <= 1, $"процедур: {limited.Routines.Count}");
        Assert.True(limited.Modules.Count <= 1, $"модулей: {limited.Modules.Count}");

        var ranked = fixture.Query.RankRoutines(limit: 2);
        Assert.True(ranked.Count <= 2, $"процедур: {ranked.Count}");

        var full = fixture.Answer(intent: "записать объект", limit: 10);
        Assert.True(full.Routines.Count >= limited.Routines.Count);
    }

    [Fact]
    public void Пустой_запрос_не_находит_ничего_и_не_падает()
    {
        using var fixture = new ConventionFixture();

        var answer = Conventions.Suggest(fixture.Query, intent: "   ", limit: 5);

        Assert.Empty(answer.Routines);
        Assert.NotNull(answer.Hint);

        // Рейтинг модулей не зависит от слов запроса: он и есть ответ «где это делают чаще всего».
        Assert.NotEmpty(answer.Modules);
        Assert.Equal("ОбщийМодуль", answer.Modules[0].Name);
    }

    /// <summary>Выгрузка с общим модулем: разные процедуры вызываются разное число раз.</summary>
    private const string ModulePath = "CommonModules/ОбщийМодуль/Ext/Module.bsl";

    /// <summary>Строка вызова «Записать» в модуле: по ней проверяется позиция примера.</summary>
    internal static int WriteLine { get; } = WriteLineOf(ModuleBsl);

    private const string ModuleBsl = """
        // Записывает товар в базе.
        Процедура ЗаписатьТовар(Товар) Экспорт
            Товар.Записать();
        КонецПроцедуры

        // Ищет товар по наименованию.
        Функция НайтиТовар(Наименование) Экспорт
            Возврат Справочники.Товары.НайтиПоНаименованию(Наименование);
        КонецФункции

        // Сообщает пользователю о проблеме.
        Процедура ПредупредитьПользователя(Текст) Экспорт
            Сообщить(Текст);
        КонецПроцедуры

        Процедура ОбщаяПроцедура()
        КонецПроцедуры

        Процедура ВтораяПроцедура()
        КонецПроцедуры

        Процедура НикемНеВызванная()
        КонецПроцедуры
        """;

    private const string SecondModulePath = "CommonModules/ВторойМодуль/Ext/Module.bsl";

    private const string SecondModuleBsl = """
        Процедура ПервыйВызов()
            ОбщийМодуль.ОбщаяПроцедура();
            ОбщийМодуль.ЗаписатьТовар(Неопределено);
            ОбщийМодуль.ПредупредитьПользователя("раз");
        КонецПроцедуры

        Процедура ВторойВызов()
            ОбщийМодуль.ОбщаяПроцедура();
            ОбщийМодуль.ЗаписатьТовар(Неопределено);
            ОбщийМодуль.НайтиТовар("два");
        КонецПроцедуры
        """;

    private const string ThirdModulePath = "CommonModules/ТретийМодуль/Ext/Module.bsl";

    private const string ThirdModuleBsl = """
        Процедура ТретийВызов()
            ОбщийМодуль.ОбщаяПроцедура();
            ОбщийМодуль.ВтораяПроцедура();
        КонецПроцедуры
        """;

    private const string ConfigurationXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <MetaDataObject xmlns="http://v8.1c.ru/8.3/MDClasses" xmlns:v8="http://v8.1c.ru/8.1/data/core" version="2.20">
            <Configuration uuid="0f0f0f0f-0000-0000-0000-0000000000c1">
                <Properties>
                    <Name>КонфигурацияСКонвенциями</Name>
                </Properties>
                <ChildObjects>
                    <CommonModule>ОбщийМодуль</CommonModule>
                    <CommonModule>ВторойМодуль</CommonModule>
                    <CommonModule>ТретийМодуль</CommonModule>
                </ChildObjects>
            </Configuration>
        </MetaDataObject>
        """;

    /// <summary>Общая часть трёх общих модулей: различается только имя.</summary>
    private static string CommonModuleXml(string name, string uuid) => $$"""
        <?xml version="1.0" encoding="UTF-8"?>
        <MetaDataObject xmlns="http://v8.1c.ru/8.3/MDClasses" xmlns:v8="http://v8.1c.ru/8.1/data/core" version="2.20">
            <CommonModule uuid="{{uuid}}">
                <Properties>
                    <Name>{{name}}</Name>
                    <Server>true</Server>
                </Properties>
            </CommonModule>
        </MetaDataObject>
        """;

    /// <summary>Строка вызова «Записать» в тексте модуля: считается по тексту, а не задаётся числом.</summary>
    private static int WriteLineOf(string text)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            if (lines[index].Contains(".Записать(", StringComparison.Ordinal))
            {
                return index + 1;
            }
        }

        return 0;
    }

    /// <summary>Своя выгрузка в памяти и разбор поверх неё: общий <see cref="SampleDump"/> не меняется.</summary>
    internal static InMemoryDumpSource CreateSource()
    {
        var source = new InMemoryDumpSource("выгрузка с конвенциями");
        source.AddText("Configuration.xml", ConfigurationXml);
        source.AddText("CommonModules/ОбщийМодуль.xml", CommonModuleXml("ОбщийМодуль", "11111111-1111-1111-1111-1111111111c1"));
        source.AddText("CommonModules/ВторойМодуль.xml", CommonModuleXml("ВторойМодуль", "22222222-2222-2222-2222-2222222222c2"));
        source.AddText("CommonModules/ТретийМодуль.xml", CommonModuleXml("ТретийМодуль", "33333333-3333-3333-3333-3333333333c3"));
        source.AddText(ModulePath, ModuleBsl);
        source.AddText(SecondModulePath, SecondModuleBsl);
        source.AddText(ThirdModulePath, ThirdModuleBsl);
        return source;
    }

    /// <summary>Индекс на своей выгрузке: проверяет тот же ответ, что и разбор в память.</summary>
    private sealed class ConventionFixture : IDisposable
    {
        private readonly InMemoryDumpSource _source;
        private readonly SqliteIndex _index;

        internal ConventionFixture()
        {
            _source = CreateSource();
            var analyzed = new DumpAnalyzer().Analyze(_source);
            _index = SqliteIndex.OpenInMemory();
            new IndexWriter(_index) { IncludeComments = true }.Write(_source, analyzed);
            Query = new IndexReader(_index).ConventionQuery();
        }

        internal IConventionQuery Query { get; }

        internal ConventionAnswer Answer(string? intent = null, string? platform = null, int limit = 8) =>
            Conventions.Suggest(Query, intent, platform, limit);

        public void Dispose() => _index.Dispose();
    }
}
