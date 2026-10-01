using Data1c.Core.Bsl;
using Data1c.Core.Metadata;
using Xunit;

namespace Data1c.Tests.Bsl;

/// <summary>
/// Тесты разбора текстов запросов 1С (Э2-1): таблицы метаданных после «ИЗ», «СОЕДИНЕНИЕ»,
/// «ПОМЕСТИТЬ» и «ОБЪЕДИНИТЬ», временные таблицы, многострочные строки и номера строк модуля.
/// </summary>
public sealed class QueryParserTests
{
    [Fact]
    public void ПростойЗапросДаётСсылкуНаТаблицуМетаданных()
    {
        var references = QueryParser.ParseQuery("ВЫБРАТЬ * ИЗ Справочник.Товары КАК Т");

        var reference = Assert.Single(references);
        Assert.Equal(MdKind.Catalog, reference.Kind);
        Assert.Equal("Товары", reference.ObjectName);
        Assert.Equal("Справочник", reference.Collection);
        Assert.Equal(1, reference.Line);
        Assert.Equal("ИЗ Справочник.Товары КАК Т", reference.Text);
    }

    [Fact]
    public void СоединениеДвухТаблицИПомещениеВременнойТаблицы()
    {
        var text = """
            ВЫБРАТЬ
                Т.Ссылка КАК Ссылка
            ПОМЕСТИТЬ ВТ_Товары
            ИЗ
                Справочник.Товары КАК Т
                ЛЕВОЕ ВНЕШНЕЕ СОЕДИНЕНИЕ Документ.ЗаказКлиента КАК З
                ПО Т.Ссылка = З.Товар
            ;
            ВЫБРАТЬ
                ВТ.Ссылка
            ИЗ
                ВТ_Товары КАК ВТ
            """;

        var references = QueryParser.ParseQuery(text);

        // Временная таблица «ВТ_Товары» — не объект метаданных: в списке только две настоящие таблицы.
        Assert.Equal(
            [
                (MdKind.Catalog, "Товары", "Справочник", 5),
                (MdKind.Document, "ЗаказКлиента", "Документ", 6),
            ],
            references.Select(Ключи));
        Assert.Equal("ЛЕВОЕ ВНЕШНЕЕ СОЕДИНЕНИЕ Документ.ЗаказКлиента КАК З", references[1].Text);
        Assert.DoesNotContain(references, static reference => reference.ObjectName == "ВТ_Товары");
        Assert.DoesNotContain(references, static reference => reference.Text.StartsWith("ПОМЕСТИТЬ", StringComparison.Ordinal));
    }

    [Fact]
    public void МногострочныйЗапросМодуляСПродолжениемСтрокИПсевдонимами()
    {
        var text = """
            Процедура ЗаполнитьТаблицу()
                Текст =
                    "ВЫБРАТЬ
                    |	Т.Ссылка КАК Ссылка,
                    |	Т.Наименование КАК Наименование
                    |ИЗ
                    |	Справочник.Товары КАК Т
                    |ГДЕ
                    |	Т.ПометкаУдаления = ЛОЖЬ";
                Результат = Новый Запрос(Текст).Выполнить();
            КонецПроцедуры
            """;

        var info = Парсер().Parse(Модуль(text));

        var reference = Assert.Single(Assert.Single(info.Routines).QueryReferences);
        Assert.Equal((MdKind.Catalog, "Товары", "Справочник"), (reference.Kind, reference.ObjectName, reference.Collection));

        // Строка таблицы в модуле — седьмая: продолжения через «|» номера строк не сбивают.
        Assert.Equal(7, reference.Line);
        Assert.Equal("ИЗ Справочник.Товары КАК Т", reference.Text);
    }

    [Fact]
    public void СтрокаБезЗапросаНеДаётСсылок()
    {
        Assert.Empty(QueryParser.ParseQuery("Просто текст без таблиц"));
        Assert.Empty(QueryParser.ParseQuery("Справочник.Товары"));
        Assert.Empty(QueryParser.ParseQuery("ВЫБРАТЬ Т.Ссылка ГДЕ Т.Наименование = \"ИЗ Справочник.Товары\""));

        // Ключевое слово запроса внутри строкового литерала запроса таблицей не считается.
        var inString = QueryParser.ParseQuery("""
            ВЫБРАТЬ
                Т.Ссылка
            ИЗ
                Справочник.Товары КАК Т
            ГДЕ
                Т.Наименование = "ИЗ Документ.ЗаказКлиента"
            """);
        Assert.Equal([(MdKind.Catalog, "Товары", "Справочник", 4)], inString.Select(Ключи));

        // Обычная строка модуля: слово «ИЗ» есть, таблицы нет.
        var info = Парсер().Parse(Модуль("""
            Процедура Сообщение()
                Сообщить("ИЗ этого текста ничего не следует");
            КонецПроцедуры
            """));
        Assert.Empty(Assert.Single(info.Routines).QueryReferences);
    }

    [Fact]
    public void МодульЦеликомДаётСсылкиНаПравильныхСтроках()
    {
        var text = """
            ЗапросКодаМодуля = Новый Запрос("ВЫБРАТЬ * ИЗ Справочник.Товары КАК Т");

            Процедура Первая()
                ПервыйТекст = "ВЫБРАТЬ * ИЗ Документ.ЗаказКлиента КАК З";
            КонецПроцедуры

            Процедура Вторая()
                ВторойТекст = "ВЫБРАТЬ * ИЗ РегистрНакопления.ТоварыНаСкладах КАК Р";
            КонецПроцедуры
            """;

        var info = Парсер().Parse(Модуль(text));

        // Запрос вне процедур остаётся на модуле, запросы процедур — на своих процедурах.
        Assert.Equal([(MdKind.Catalog, "Товары", 1)], info.QueryReferences.Select(КлючиСоСтрокой));

        Assert.Equal(2, info.Routines.Count);
        Assert.Equal([(MdKind.Document, "ЗаказКлиента", 4)], info.Routines[0].QueryReferences.Select(КлючиСоСтрокой));
        Assert.Equal(
            [(MdKind.AccumulationRegister, "ТоварыНаСкладах", 8)],
            info.Routines[1].QueryReferences.Select(КлючиСоСтрокой));
    }

    [Fact]
    public void ВиртуальныеТаблицыИМножественноеЧислоКоллекций()
    {
        var virtualTable = QueryParser.ParseQuery("""
            ВЫБРАТЬ
                К.Курс
            ИЗ
                РегистрСведений.Курсы.СрезПоследних(&Дата, ) КАК К
            """);

        // Виртуальная таблица ограничивается базовой: «РегистрСведений.Курсы».
        Assert.Equal(
            [(MdKind.InformationRegister, "Курсы", "РегистрСведений", 4)],
            virtualTable.Select(Ключи));

        // Множественное число имени коллекции принимается так же, как единственное.
        var plural = QueryParser.ParseQuery("ВЫБРАТЬ * ИЗ Справочники.Товары КАК Т");
        Assert.Equal((MdKind.Catalog, "Товары", "Справочники"), (Assert.Single(plural).Kind, plural[0].ObjectName, plural[0].Collection));

        // Таблица без имени объекта и параметр вместо имени таблицы ссылок не дают.
        Assert.Empty(QueryParser.ParseQuery("ВЫБРАТЬ * ИЗ Справочники"));
        Assert.Empty(QueryParser.ParseQuery("ВЫБРАТЬ * ИЗ &ИмяТаблицы КАК Т"));
    }

    [Fact]
    public void КоллекцииБезИмениВЕдинственномЧислеПеречислены()
    {
        // Коллекции, для которых в разборщике нет единственного числа, видны в списке ограничений.
        Assert.DoesNotContain("Справочники", QueryParser.UnmappedCollections);
        Assert.DoesNotContain("РегистрыНакопления", QueryParser.UnmappedCollections);
        Assert.Contains("Подсистемы", QueryParser.UnmappedCollections);
        Assert.Contains("Роли", QueryParser.UnmappedCollections);
    }

    [Fact]
    public void СложныйЗапросСПодзапросомВременнымиИВиртуальнымиТаблицами()
    {
        var text = """
            ВЫБРАТЬ
            	Товары.Ссылка КАК Ссылка,
            	Товары.Наименование КАК Наименование
            ПОМЕСТИТЬ ВТ_ТоварыСЦенами
            ИЗ
            	Справочник.Товары КАК Товары
            	ВНУТРЕННЕЕ СОЕДИНЕНИЕ РегистрСведений.ЦеныНоменклатуры.СрезПоследних(&Дата, ) КАК Цены
            	ПО Товары.Ссылка = Цены.Номенклатура
            ГДЕ
            	Товары.ПометкаУдаления = ЛОЖЬ

            ОБЪЕДИНИТЬ ВСЕ

            ВЫБРАТЬ
            	Услуги.Ссылка,
            	Услуги.Наименование
            ИЗ
            	Справочник.Услуги КАК Услуги
            ;
            ВЫБРАТЬ
            	ВТ.Ссылка КАК Ссылка
            ИЗ
            	ВТ_ТоварыСЦенами КАК ВТ
            	ЛЕВОЕ СОЕДИНЕНИЕ (ВЫБРАТЬ
            			Д.Ссылка КАК Ссылка
            		ИЗ
            			Документ.РеализацияТоваровУслуг КАК Д) КАК Док
            	ПО ВТ.Ссылка = Док.Ссылка
            """;

        var references = QueryParser.ParseQuery(text);

        Assert.Equal(
            [
                (MdKind.Catalog, "Товары", "Справочник", 6),
                (MdKind.InformationRegister, "ЦеныНоменклатуры", "РегистрСведений", 7),
                (MdKind.Catalog, "Услуги", "Справочник", 16),
                (MdKind.Document, "РеализацияТоваровУслуг", "Документ", 25),
            ],
            references.Select(Ключи));

        // Таблица из подзапроса в соединении найдена, а временная таблица «ВТ_ТоварыСЦенами» — нет.
        Assert.DoesNotContain(references, static reference => reference.ObjectName.StartsWith("ВТ_", StringComparison.Ordinal));
    }

    private static BslModuleParser Парсер() => new();

    private static BslModuleSource Модуль(string text) =>
        new("CommonModules/ТестовыйМодуль/Ext/Module.bsl", text, "CommonModule.ТестовыйМодуль", BslModuleKind.CommonModule);

    private static (MdKind Kind, string ObjectName, string Collection, int Line) Ключи(BslQueryReference reference) =>
        (reference.Kind, reference.ObjectName, reference.Collection, reference.Line);

    private static (MdKind Kind, string ObjectName, int Line) КлючиСоСтрокой(BslQueryReference reference) =>
        (reference.Kind, reference.ObjectName, reference.Line);
}
