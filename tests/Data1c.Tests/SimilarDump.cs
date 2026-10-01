using System.Text;
using Data1c.Core.Dump;

namespace Data1c.Tests;

/// <summary>
/// Синтетическая выгрузка для поиска похожего кода (Э2-5): две процедуры одной задачи, процедура
/// «сосед по имени» без общих вызовов и заведомо нерелевантная процедура.
/// </summary>
/// <remarks>
/// Набор отдельный, а не общий <see cref="SampleDump"/>: на том висят точные проверки, и менять
/// его из-за похожести нельзя.
/// </remarks>
internal static class SimilarDump
{
    /// <summary>Путь модуля процедуры-«двойника»: делает то же, что черновик, но другими словами.</summary>
    internal const string TwinModulePath = "CommonModules/ОбработкаЗаказов/Ext/Module.bsl";

    /// <summary>Путь модуля процедуры, которая читает тот же справочник, но другими средствами.</summary>
    internal const string ReaderModulePath = "CommonModules/ЗагрузкаТоваров/Ext/Module.bsl";

    /// <summary>Путь модуля вспомогательных процедур: совпадение только по имени и полная нерелевантность.</summary>
    internal const string HelperModulePath = "CommonModules/Вспомогательный/Ext/Module.bsl";

    /// <summary>Процедура, которая должна найтись первой.</summary>
    internal const string TwinId = "routine:module:" + TwinModulePath + "#ЗагрузитьТоварыПоЗаказу";

    /// <summary>Процедура с тем же справочником, но без вызовов платформы из черновика.</summary>
    internal const string ReaderId = "routine:module:" + ReaderModulePath + "#ЗагрузитьТоварыНаСклад";

    /// <summary>Процедура, похожая только именем: вызовов и обращений общих с черновиком нет.</summary>
    internal const string TermsOnlyId = "routine:module:" + HelperModulePath + "#СохранитьТоварыВФайл";

    /// <summary>Процедура, у которой с черновиком нет ничего общего.</summary>
    internal const string IrrelevantId = "routine:module:" + HelperModulePath + "#НапечататьПриветствие";

    /// <summary>
    /// Черновик: вызывает те же методы платформы и читает тот же справочник, что «двойник»,
    /// но текст другой — обычным поиском по тексту такая процедура не находится.
    /// </summary>
    internal const string Draft = """
        Процедура СохранитьТоварыНаСклад()
            Таблица = Новый ТаблицаЗначений;
            Таблица.Свернуть("Товар");
            Запрос = Новый Запрос;
            Запрос.Текст = "ВЫБРАТЬ * ИЗ Справочник.Товары КАК Т";
            Товары = Запрос.Выполнить().Выгрузить();
        КонецПроцедуры
        """;

    /// <summary>Черновик без общих с выгрузкой признаков: на нём проверяется понятный пустой ответ.</summary>
    internal const string ForeignDraft = """
        Процедура УведомитьКурьераОДоставке()
            Письмо = Новый ИнтернетПочтовоеСообщение;
            Письмо.Тема = "Доставка";
        КонецПроцедуры
        """;

    /// <summary>Выгрузка с процедурами для сравнения.</summary>
    internal static InMemoryDumpSource Create()
    {
        var source = new InMemoryDumpSource("выгрузка для поиска похожего кода");
        source.AddText("Configuration.xml", ConfigurationXml);
        source.AddText("Catalogs/Товары.xml", CatalogXml);
        source.AddText("CommonModules/ОбработкаЗаказов.xml", CommonModuleXml("ОбработкаЗаказов", "77777777-7777-7777-7777-777777777771"));
        source.AddText("CommonModules/ЗагрузкаТоваров.xml", CommonModuleXml("ЗагрузкаТоваров", "77777777-7777-7777-7777-777777777772"));
        source.AddText("CommonModules/Вспомогательный.xml", CommonModuleXml("Вспомогательный", "77777777-7777-7777-7777-777777777773"));
        source.AddText(TwinModulePath, TwinModuleBsl);
        source.AddText(ReaderModulePath, ReaderModuleBsl);
        source.AddText(HelperModulePath, HelperModuleBsl);
        return source;
    }

    /// <summary>
    /// Выгрузка, где один и тот же метод платформы вызывают сорок процедур: такой признак частый,
    /// поэтому в поиске похожего он не участвует.
    /// </summary>
    internal static InMemoryDumpSource CreateWithFrequentCall()
    {
        var source = new InMemoryDumpSource("выгрузка с частым вызовом");
        source.AddText("Configuration.xml", FrequentConfigurationXml);
        source.AddText("CommonModules/Частые.xml", CommonModuleXml("Частые", "77777777-7777-7777-7777-777777777774"));
        source.AddText(FrequentModulePath, FrequentModuleBsl());
        return source;
    }

    /// <summary>Путь модуля с сорока однотипными процедурами.</summary>
    internal const string FrequentModulePath = "CommonModules/Частые/Ext/Module.bsl";

    private static string FrequentModuleBsl()
    {
        var text = new StringBuilder();
        for (var index = 1; index <= 40; index++)
        {
            text.AppendLine($"// Готовит таблицу номер {index}.");
            text.AppendLine($"Процедура ПодготовитьТаблицу{index}()");
            text.AppendLine("    Таблица = Новый ТаблицаЗначений;");
            text.AppendLine("    Таблица.Свернуть(\"Товар\");");
            text.AppendLine("КонецПроцедуры");
            text.AppendLine();
        }

        return text.ToString();
    }

    private const string TwinModuleBsl = """
        // Загружает товары по заказу.
        Процедура ЗагрузитьТоварыПоЗаказу()
            Таблица = Новый ТаблицаЗначений;
            Таблица.Свернуть("Товар");
            Запрос = Новый Запрос;
            Запрос.Текст = "ВЫБРАТЬ Т.Ссылка ИЗ Справочник.Товары КАК Т";
            Товары = Запрос.Выполнить().Выгрузить();
        КонецПроцедуры
        """;

    private const string ReaderModuleBsl = """
        // Загружает товары на склад.
        Процедура ЗагрузитьТоварыНаСклад()
            Товар = Справочники.Товары.НайтиПоНаименованию("Тест");
            Массив = Новый Массив;
            Массив.Добавить(Товар);
        КонецПроцедуры
        """;

    private const string HelperModuleBsl = """
        // Сохраняет товары в файл.
        Процедура СохранитьТоварыВФайл()
            Сообщить("Файл");
        КонецПроцедуры

        // Печатает приветствие.
        Процедура НапечататьПриветствие()
            Сообщить("Привет");
        КонецПроцедуры
        """;

    private const string ConfigurationXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <MetaDataObject xmlns="http://v8.1c.ru/8.3/MDClasses" xmlns:v8="http://v8.1c.ru/8.1/data/core" version="2.20">
            <Configuration uuid="0f0f0f0f-0000-0000-0000-0000000000c5">
                <Properties>
                    <Name>КонфигурацияПохожегоКода</Name>
                </Properties>
                <ChildObjects>
                    <Catalog>Товары</Catalog>
                    <CommonModule>ОбработкаЗаказов</CommonModule>
                    <CommonModule>ЗагрузкаТоваров</CommonModule>
                    <CommonModule>Вспомогательный</CommonModule>
                </ChildObjects>
            </Configuration>
        </MetaDataObject>
        """;

    private const string FrequentConfigurationXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <MetaDataObject xmlns="http://v8.1c.ru/8.3/MDClasses" xmlns:v8="http://v8.1c.ru/8.1/data/core" version="2.20">
            <Configuration uuid="0f0f0f0f-0000-0000-0000-0000000000c6">
                <Properties>
                    <Name>КонфигурацияСЧастымВызовом</Name>
                </Properties>
                <ChildObjects>
                    <CommonModule>Частые</CommonModule>
                </ChildObjects>
            </Configuration>
        </MetaDataObject>
        """;

    private const string CatalogXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <MetaDataObject xmlns="http://v8.1c.ru/8.3/MDClasses" xmlns:v8="http://v8.1c.ru/8.1/data/core" xmlns:cfg="http://v8.1c.ru/8.1/data/enterprise/current-config" version="2.20">
            <Catalog uuid="11111111-1111-1111-1111-1111111111c5">
                <Properties>
                    <Name>Товары</Name>
                </Properties>
                <ChildObjects>
                    <Attribute uuid="22222222-2222-2222-2222-2222222222c5">
                        <Properties>
                            <Name>Артикул</Name>
                            <Type>
                                <v8:Type>xs:string</v8:Type>
                            </Type>
                        </Properties>
                    </Attribute>
                </ChildObjects>
            </Catalog>
        </MetaDataObject>
        """;

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
}
