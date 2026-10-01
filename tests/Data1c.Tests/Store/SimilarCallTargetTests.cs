using Data1c.Core.Analysis;
using Data1c.Core.Dump;
using Data1c.Store;
using Xunit;

namespace Data1c.Tests.Store;

/// <summary>
/// Признак вызова процедуры конфигурации (Э2-5): сравнивается идентификатор разрешённой цели, а не
/// текст вызова. Поэтому «Общий.ОбработатьТовар» и «ОбработатьТовар» внутри того же модуля дают
/// один признак и находят друг друга, а вызов с неразрешённой целью (<c>call:…</c>) даёт только
/// слабое совпадение по имени метода.
/// </summary>
/// <remarks>
/// У каждого теста своя выгрузка в памяти: общий <see cref="SimilarDump"/> держит точные проверки
/// поиска похожего кода, и менять его ради этих правил нельзя.
/// </remarks>
public sealed class SimilarCallTargetTests
{
    /// <summary>Цель вызова: метод общего модуля «Общий», одинаковый для обеих записей вызова.</summary>
    private const string TargetId = "routine:module:CommonModules/Общий/Ext/Module.bsl#ОбработатьТовар";

    /// <summary>Процедура модуля «Общий»: вызывает метод без квалификатора (локальный вызов).</summary>
    private const string LocalId = "routine:module:CommonModules/Общий/Ext/Module.bsl#СохранитьТоварПоЗаказу";

    /// <summary>Процедура другого модуля: вызывает тот же метод с квалификатором.</summary>
    private const string QualifiedId = "routine:module:CommonModules/Загрузка/Ext/Module.bsl#СохранитьТоварПоЗаказу";

    [Theory]
    [InlineData("Общий.ОбработатьТовар", null)]
    [InlineData("ОбработатьТовар", "CommonModules/Общий/Ext/Module.bsl")]
    // Черновик текстом: модуля нет, но имя метода в конфигурации одно — цель тоже разрешается.
    [InlineData("ОбработатьТовар", null)]
    public void Квалификация_вызова_не_мешает_совпадению_по_цели(string callee, string? draftModule)
    {
        // Черновик вызывает тот же метод: с квалификатором («Общий.ОбработатьТовар») или без него
        // (когда известен модуль черновика). У кандидатов запись вызова разная — совпадать должен
        // не текст, а идентификатор цели.
        using var fixture = new Fixture(CallDump());

        var found = fixture.Find(Draft($"{callee}(\"Тест\");"), draftModule: draftModule);

        var target = Assert.Single(found.Draft.RoutineCalls);
        Assert.Equal(TargetId, target.Value);
        Assert.Equal(callee, target.Detail);

        var qualified = found.Candidates.Single(static candidate => candidate.Id == QualifiedId);
        var local = found.Candidates.Single(static candidate => candidate.Id == LocalId);
        Assert.DoesNotContain(found.Draft.UnresolvedCalls, static feature => feature.Value == "обработатьтовар");

        // Тексты вызовов у кандидатов разные, а совпавший признак — один и тот же идентификатор цели.
        var qualifiedMatch = qualified.Matches.Single(static match => match.Signal == SimilarCodeSignal.RoutineCall);
        var localMatch = local.Matches.Single(static match => match.Signal == SimilarCodeSignal.RoutineCall);
        Assert.Equal(TargetId, qualifiedMatch.Value);
        Assert.Equal(TargetId, localMatch.Value);
        Assert.Equal("Общий.ОбработатьТовар", qualifiedMatch.Detail);
        Assert.Equal("ОбработатьТовар", localMatch.Detail);
        Assert.Contains("вызовы процедур", qualified.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Неразрешённый_вызов_даёт_слабое_совпадение_ниже_платформенного()
    {
        using var fixture = new Fixture(WeakDump());

        var found = fixture.Find(WeakDraft);

        var byPlatform = found.Candidates.Single(static candidate => candidate.Id == PlatformId);
        var byMetadata = found.Candidates.Single(static candidate => candidate.Id == MetadataId);
        var byName = found.Candidates.Single(static candidate => candidate.Id == WeakId);

        // Вызов «Справочники.Товары.НайтиПоНаименованию» в конфигурации ни на что не разрешается:
        // остаётся только имя метода без квалификатора — слабый сигнал.
        var weak = Assert.Single(found.Draft.UnresolvedCalls);
        Assert.Equal("найтипонаименованию", weak.Value);
        Assert.Equal("Справочники.Товары.НайтиПоНаименованию", weak.Detail);

        Assert.Contains(byName.Matches, static match =>
            match.Signal == SimilarCodeSignal.UnresolvedCall && match.Value == "найтипонаименованию");
        Assert.DoesNotContain(byPlatform.Matches, static match => match.Signal == SimilarCodeSignal.UnresolvedCall);
        Assert.DoesNotContain(byMetadata.Matches, static match => match.Signal == SimilarCodeSignal.UnresolvedCall);

        // Слабый сигнал не перевешивает ни вызовы платформы, ни обращения к метаданным.
        Assert.True(byName.Score < byMetadata.Score, $"слабое совпадение {byName.Score:0.000} обогнало метаданные {byMetadata.Score:0.000}");
        Assert.True(byMetadata.Score < byPlatform.Score, $"метаданные {byMetadata.Score:0.000} обогнали платформу {byPlatform.Score:0.000}");

        // В ответе видно, что сигнал слабый: и в объяснении кандидата, и в примечаниях.
        Assert.Contains("слаб", byName.Reason, StringComparison.Ordinal);
        Assert.Contains(found.Notes, static note => note.Contains("неразрешённой целью", StringComparison.Ordinal));
    }

    /// <summary>Черновик: строка вызова подставляется, остальное — как у кандидатов.</summary>
    private static string Draft(string call) => $"""
        // Сохраняет товар по заказу.
        Процедура СохранитьТоварПоЗаказу()
            {call}
            Таблица = Новый ТаблицаЗначений;
            Таблица.Свернуть("Товар");
        КонецПроцедуры
        """;

    /// <summary>Выгрузка: две процедуры вызывают один метод общего модуля по-разному.</summary>
    private static InMemoryDumpSource CallDump()
    {
        var source = new InMemoryDumpSource("выгрузка с двумя записями одного вызова");
        source.AddText("Configuration.xml", Configuration("КонфигурацияСВызовом", ["Общий", "Загрузка"]));
        source.AddText("CommonModules/Общий.xml", CommonModuleXml("Общий", "88888888-8888-8888-8888-888888888881"));
        source.AddText("CommonModules/Загрузка.xml", CommonModuleXml("Загрузка", "88888888-8888-8888-8888-888888888882"));
        source.AddText("CommonModules/Общий/Ext/Module.bsl", """
            // Обрабатывает товар.
            Процедура ОбработатьТовар(Товар)
                Сообщить(Товар);
            КонецПроцедуры

            // Сохраняет товар по заказу.
            Процедура СохранитьТоварПоЗаказу()
                ОбработатьТовар("Тест");
                Таблица = Новый ТаблицаЗначений;
                Таблица.Свернуть("Товар");
            КонецПроцедуры
            """);
        source.AddText("CommonModules/Загрузка/Ext/Module.bsl", """
            // Сохраняет товар по заказу.
            Процедура СохранитьТоварПоЗаказу()
                Общий.ОбработатьТовар("Тест");
                Таблица = Новый ТаблицаЗначений;
                Таблица.Свернуть("Товар");
            КонецПроцедуры
            """);
        return source;
    }

    private const string PlatformId = "routine:module:CommonModules/Платформенный/Ext/Module.bsl#СохранитьТоварыПоЗаказу";
    private const string MetadataId = "routine:module:CommonModules/Метаданный/Ext/Module.bsl#СохранитьТоварыПоЗаказу";
    private const string WeakId = "routine:module:CommonModules/Слабый/Ext/Module.bsl#СохранитьТоварыПоЗаказу";

    /// <summary>
    /// Выгрузка для проверки слабого сигнала: три процедуры с одинаковым именем, из которых одна
    /// совпадает с черновиком вызовом платформы, вторая — обращением к метаданным из запроса,
    /// третья — только неразрешённым вызовом по имени метода.
    /// </summary>
    private static InMemoryDumpSource WeakDump()
    {
        var source = new InMemoryDumpSource("выгрузка со слабым совпадением");
        source.AddText("Configuration.xml", Configuration("КонфигурацияСоСлабымВызовом", ["Платформенный", "Метаданный", "Слабый"], withCatalog: true));
        source.AddText("Catalogs/Товары.xml", CatalogXml);
        source.AddText("CommonModules/Платформенный.xml", CommonModuleXml("Платформенный", "88888888-8888-8888-8888-888888888883"));
        source.AddText("CommonModules/Метаданный.xml", CommonModuleXml("Метаданный", "88888888-8888-8888-8888-888888888884"));
        source.AddText("CommonModules/Слабый.xml", CommonModuleXml("Слабый", "88888888-8888-8888-8888-888888888885"));
        source.AddText("CommonModules/Платформенный/Ext/Module.bsl", """
            // Сохраняет товары по заказу.
            Процедура СохранитьТоварыПоЗаказу()
                Таблица = Новый ТаблицаЗначений;
                Таблица.Свернуть("Товар");
            КонецПроцедуры
            """);
        source.AddText("CommonModules/Метаданный/Ext/Module.bsl", """
            // Сохраняет товары по заказу.
            Процедура СохранитьТоварыПоЗаказу()
                Запрос = Новый Запрос;
                Запрос.Текст = "ВЫБРАТЬ Т.Ссылка ИЗ Справочник.Товары КАК Т";
                Выборка = Запрос.Выполнить().Выгрузить();
            КонецПроцедуры
            """);
        source.AddText("CommonModules/Слабый/Ext/Module.bsl", """
            // Сохраняет товары по заказу.
            Процедура СохранитьТоварыПоЗаказу()
                Товар = Товары.НайтиПоНаименованию("Тест");
            КонецПроцедуры
            """);
        return source;
    }

    /// <summary>Черновик: обращение к справочнику идёт через метод менеджера — цель не разрешается.</summary>
    private const string WeakDraft = """
        // Сохраняет товары по заказу.
        Процедура СохранитьТоварыПоЗаказу()
            Таблица = Новый ТаблицаЗначений;
            Таблица.Свернуть("Товар");
            Товар = Справочники.Товары.НайтиПоНаименованию("Тест");
        КонецПроцедуры
        """;

    private static string Configuration(string name, IReadOnlyList<string> commonModules, bool withCatalog = false)
    {
        var modules = string.Concat(commonModules.Select(static module => $"        <CommonModule>{module}</CommonModule>\n"));
        var catalog = withCatalog ? "        <Catalog>Товары</Catalog>\n" : string.Empty;
        return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <MetaDataObject xmlns="http://v8.1c.ru/8.3/MDClasses" xmlns:v8="http://v8.1c.ru/8.1/data/core" version="2.20">
                <Configuration uuid="0f0f0f0f-0000-0000-0000-0000000000d5">
                    <Properties>
                        <Name>{name}</Name>
                    </Properties>
                    <ChildObjects>
            {modules}{catalog}        </ChildObjects>
                </Configuration>
            </MetaDataObject>
            """;
    }

    private static string CommonModuleXml(string name, string uuid) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <MetaDataObject xmlns="http://v8.1c.ru/8.3/MDClasses" xmlns:v8="http://v8.1c.ru/8.1/data/core" version="2.20">
            <CommonModule uuid="{uuid}">
                <Properties>
                    <Name>{name}</Name>
                    <Server>true</Server>
                </Properties>
            </CommonModule>
        </MetaDataObject>
        """;

    private const string CatalogXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <MetaDataObject xmlns="http://v8.1c.ru/8.3/MDClasses" xmlns:v8="http://v8.1c.ru/8.1/data/core" xmlns:cfg="http://v8.1c.ru/8.1/data/enterprise/current-config" version="2.20">
            <Catalog uuid="33333333-3333-3333-3333-3333333333d5">
                <Properties>
                    <Name>Товары</Name>
                </Properties>
            </Catalog>
        </MetaDataObject>
        """;

    /// <summary>
    /// Индекс на синтетической выгрузке: поиск идёт ровно так, как в инструменте similar, вместе
    /// с разрешением вызовов черновика в узлы процедур.
    /// </summary>
    private sealed class Fixture : IDisposable
    {
        private readonly SqliteIndex _index;

        internal Fixture(InMemoryDumpSource dump)
        {
            var analyzed = new DumpAnalyzer().Analyze(dump);
            _index = SqliteIndex.OpenInMemory();
            new IndexWriter(_index).Write(dump, analyzed);
        }

        internal SimilarCodeResult Find(string draft, int limit = 10, string? excludeId = null, string? draftModule = null)
        {
            var source = new IndexSimilarCodeSource(_index, draftModule);
            return new SimilarCode(source).Find(
                SimilarCode.Describe(draft, draftModule, source.ResolveCall),
                limit,
                excludeId);
        }

        public void Dispose() => _index.Dispose();
    }
}
