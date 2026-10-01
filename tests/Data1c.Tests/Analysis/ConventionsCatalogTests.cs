using Data1c.Core.Analysis;
using Data1c.Core.Dump;
using Data1c.Store;
using Xunit;

namespace Data1c.Tests.Analysis;

/// <summary>
/// Проверяет расширенный каталог приёмов конвенций: приёмы из типовых задач разработки на 1С
/// распознаются по естественной формулировке, находят подходящую процедуру в синтетике,
/// а распознавание объясняется словами и методом платформы.
/// </summary>
/// <remarks>
/// Выгрузка своя, а не общая <see cref="SampleDump"/>: общий набор не меняется, а здесь нужны
/// процедуры на каждый новый приём — проведение документа, запись набора записей, печатная форма,
/// файл, JSON, представление, проверка заполнения, константа, замер, журнал и транзакция.
/// </remarks>
public sealed class ConventionsCatalogTests
{
    /// <summary>Путь модуля с процедурами: по нему проверяется, что найдена именно своя процедура.</summary>
    private const string ModulePath = "CommonModules/ПриёмыКонфигурации/Ext/Module.bsl";

    [Fact]
    public void Проведение_документа_распознаётся_и_находит_проведение()
    {
        using var fixture = new CatalogFixture();
        var answer = fixture.Answer("провести документ");

        Assert.Equal("провести документ", Recognition(answer).IntentId);
        Assert.Equal(ConventionMatchStep.PlatformMethod, Recognition(answer).Step);
        Assert.Equal("Провести", Recognition(answer).PlatformMethod);
        Assert.Equal("Проведение документа", answer.Intent);
        Assert.Contains(answer.Routines, static routine => routine.Name == "ПровестиРеализациюТоваров");
    }

    [Fact]
    public void Поиск_по_коду_распознаётся_и_находит_поиск_по_коду()
    {
        using var fixture = new CatalogFixture();
        var answer = fixture.Answer("найти товар по коду");

        Assert.Equal("найти по коду или артикулу", Recognition(answer).IntentId);
        Assert.Contains("код", Recognition(answer).Keywords, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(answer.Routines, static routine => routine.Name == "НайтиТоварПоКоду");
    }

    [Fact]
    public void Запись_набора_записей_регистра_распознаётся_и_находит_запись_остатков()
    {
        using var fixture = new CatalogFixture();
        var answer = fixture.Answer("записать набор записей регистра");

        Assert.Equal("записать набор записей регистра", Recognition(answer).IntentId);
        Assert.Contains("регистр", Recognition(answer).Keywords, StringComparer.OrdinalIgnoreCase);

        // Вид метаданных — регистр накопления — тоже признак приёма.
        var found = Assert.Single(answer.Routines, static routine => routine.Name == "ЗаписатьОстаткиТоваров");
        Assert.Contains(found.Reasons ?? [], static reason => reason.Contains("AccumulationRegister", StringComparison.Ordinal));
    }

    [Fact]
    public void Печатная_форма_распознаётся_и_находит_формирование_печати()
    {
        using var fixture = new CatalogFixture();
        var answer = fixture.Answer("сформировать печатную форму");

        Assert.Equal("сформировать печатную форму", Recognition(answer).IntentId);
        Assert.Contains("печатная", Recognition(answer).Keywords, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(answer.Routines, static routine => routine.Name == "СформироватьПечатнуюФормуРеализации");
        Assert.Contains(
            answer.Routines.Single(static routine => routine.Name == "СформироватьПечатнуюФормуРеализации").Reasons ?? [],
            static reason => reason.Contains("ПолучитьМакет", StringComparison.Ordinal));
    }

    [Fact]
    public void Работа_с_файлом_распознаётся_и_находит_сохранение_файла()
    {
        using var fixture = new CatalogFixture();
        var answer = fixture.Answer("сохранить файл на диске");

        Assert.Equal("сохранить или прочитать файл", Recognition(answer).IntentId);
        Assert.Contains("файл", Recognition(answer).Keywords, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(answer.Routines, static routine => routine.Name == "СохранитьФайлНаДиске");
    }

    [Fact]
    public void Сериализация_в_JSON_распознаётся_и_находит_запись_JSON()
    {
        using var fixture = new CatalogFixture();
        var answer = fixture.Answer("сериализовать настройки в JSON");

        Assert.Equal("сериализовать в XML или JSON", Recognition(answer).IntentId);
        Assert.Contains("json", Recognition(answer).Keywords, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(answer.Routines, static routine => routine.Name == "СериализоватьНастройкиВJSON");
    }

    [Fact]
    public void Представление_объекта_распознаётся_и_находит_представление()
    {
        using var fixture = new CatalogFixture();
        var answer = fixture.Answer("получить представление объекта");

        Assert.Equal("получить представление объекта", Recognition(answer).IntentId);
        Assert.Contains("представление", Recognition(answer).Keywords, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(answer.Routines, static routine => routine.Name == "ПолучитьПредставлениеТовара");
    }

    [Fact]
    public void Проверка_заполнения_распознаётся_и_находит_проверку()
    {
        using var fixture = new CatalogFixture();
        var answer = fixture.Answer("проверить заполнение реквизитов");

        Assert.Equal("проверить заполнение реквизитов", Recognition(answer).IntentId);
        Assert.Contains("заполнение", Recognition(answer).Keywords, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(answer.Routines, static routine => routine.Name == "ПроверитьЗаполнениеРеализации");
    }

    [Fact]
    public void Константа_распознаётся_и_находит_чтение_настройки()
    {
        using var fixture = new CatalogFixture();
        var answer = fixture.Answer("прочитать настройку программы");

        Assert.Equal("получить константу или настройку", Recognition(answer).IntentId);
        Assert.Contains("настройка", Recognition(answer).Keywords, StringComparer.OrdinalIgnoreCase);

        // Вид метаданных тоже работает признаком: процедура обращается к константе.
        var found = Assert.Single(answer.Routines, static routine => routine.Name == "ПолучитьНастройкуПрограммы");
        Assert.Contains(found.Reasons ?? [], static reason => reason.Contains("Constant", StringComparison.Ordinal));
    }

    [Fact]
    public void Замер_производительности_распознаётся_и_находит_замер()
    {
        using var fixture = new CatalogFixture();
        var answer = fixture.Answer("замерить производительность загрузки");

        Assert.Equal("замерить производительность", Recognition(answer).IntentId);
        Assert.Contains("замерить", Recognition(answer).Keywords, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(answer.Routines, static routine => routine.Name == "ЗамеритьВремяЗагрузки");
    }

    [Fact]
    public void Журнал_регистрации_распознаётся_и_находит_запись_в_журнал()
    {
        using var fixture = new CatalogFixture();
        var answer = fixture.Answer("записать ошибку в журнал регистрации");

        Assert.Equal("записать в журнал регистрации", Recognition(answer).IntentId);
        Assert.Contains("журнал", Recognition(answer).Keywords, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(answer.Routines, static routine => routine.Name == "ЗаписатьОшибкуВЖурнал");
    }

    [Fact]
    public void Транзакция_распознаётся_и_находит_выполнение_в_транзакции()
    {
        using var fixture = new CatalogFixture();
        var answer = fixture.Answer("выполнить действие в транзакции");

        Assert.Equal("выполнить в транзакции", Recognition(answer).IntentId);
        Assert.Contains("транзакция", Recognition(answer).Keywords, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(answer.Routines, static routine => routine.Name == "ВыполнитьВТранзакции");
    }

    [Fact]
    public void Распознавание_объясняется_словами_и_методом()
    {
        using var fixture = new CatalogFixture();
        var answer = fixture.Answer("провести документ");
        var recognition = Recognition(answer);

        // Объяснение видно в ответе: слова запроса, признаки приёма и готовое пояснение.
        Assert.Contains("провести", recognition.Words, StringComparer.Ordinal);
        Assert.Contains("документ", recognition.Words, StringComparer.Ordinal);
        Assert.Contains("Провести", recognition.NameTokens, StringComparer.Ordinal);
        Assert.True(recognition.Score > 0, $"вес совпадения: {recognition.Score}");
        Assert.Contains("Провести", recognition.Summary, StringComparison.Ordinal);
        Assert.Contains("распознан", recognition.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Нераспознанная_формулировка_ищется_по_словам_и_честно_об_этом_говорит()
    {
        using var fixture = new CatalogFixture();
        var answer = fixture.Answer("телепортировать документ");

        // Приём не выдумывается: ступень none, в объяснении и оговорке сказано прямо.
        Assert.Null(Recognition(answer).IntentId);
        Assert.Equal(ConventionMatchStep.None, Recognition(answer).Step);
        Assert.Contains("не распознан", Recognition(answer).Summary, StringComparison.Ordinal);
        Assert.Contains("документ", Recognition(answer).Words, StringComparer.Ordinal);
        Assert.NotNull(answer.Note);
        Assert.Contains("не распознан", answer.Note, StringComparison.Ordinal);

        // В оговорке перечислены известные приёмы: агенту видно, как переформулировать запрос.
        Assert.Contains("записать объект", answer.Note, StringComparison.Ordinal);
        Assert.Contains("сформировать печатную форму", answer.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void Каталог_содержит_двадцать_приёмов_с_признаками()
    {
        var intents = Conventions.Intents;

        Assert.Equal(20, intents.Count);
        Assert.Equal(intents.Count, intents.Select(static intent => intent.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.All(intents, static intent =>
        {
            Assert.NotEmpty(intent.Id);
            Assert.NotEmpty(intent.Title);
            Assert.True(intent.Keywords.Count >= 2, $"у приёма «{intent.Id}» мало ключевых слов");
            Assert.NotEmpty(intent.PlatformMethods);
            Assert.NotEmpty(intent.NameTokens);
        });

        // Приёмы, ради которых каталог и расширялся: каждый из них должен быть на месте.
        string[] expected =
        [
            "записать объект", "провести документ", "получить реквизит объекта", "установить реквизит объекта",
            "найти по наименованию", "найти по коду или артикулу", "прочитать данные запросом",
            "записать набор записей регистра", "вывести сообщение пользователю", "выполнить на сервере или в фоне",
            "добавить в коллекцию", "сформировать печатную форму", "сохранить или прочитать файл",
            "сериализовать в XML или JSON", "получить представление объекта", "проверить заполнение реквизитов",
            "получить константу или настройку", "замерить производительность", "записать в журнал регистрации",
            "выполнить в транзакции",
        ];
        Assert.All(expected, id => Assert.Contains(intents, intent => intent.Id == id));

        // У самой частой задачи метод-признак «Записать» есть, иначе приём не нашёл бы процедуры.
        Assert.Contains(
            Conventions.Intents.Single(static intent => intent.Id == "записать объект").PlatformMethods,
            static method => method == "Записать");
    }

    /// <summary>Объяснение распознавания: в ответе оно обязано быть.</summary>
    private static ConventionRecognition Recognition(ConventionAnswer answer)
    {
        Assert.NotNull(answer.Recognition);
        return answer.Recognition;
    }

    private const string ConfigurationXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <MetaDataObject xmlns="http://v8.1c.ru/8.3/MDClasses" xmlns:v8="http://v8.1c.ru/8.1/data/core" xmlns:cfg="http://v8.1c.ru/8.1/data/enterprise/current-config" version="2.20">
            <Configuration uuid="0f0f0f0f-0000-0000-0000-0000000000d1">
                <Properties>
                    <Name>КонфигурацияСПриёмами</Name>
                </Properties>
                <ChildObjects>
                    <Catalog>Товары</Catalog>
                    <Document>РеализацияТоваров</Document>
                    <AccumulationRegister>ОстаткиТоваров</AccumulationRegister>
                    <Constant>НастройкаПрограммы</Constant>
                    <CommonModule>ПриёмыКонфигурации</CommonModule>
                </ChildObjects>
            </Configuration>
        </MetaDataObject>
        """;

    private const string CatalogXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <MetaDataObject xmlns="http://v8.1c.ru/8.3/MDClasses" xmlns:v8="http://v8.1c.ru/8.1/data/core" xmlns:cfg="http://v8.1c.ru/8.1/data/enterprise/current-config" version="2.20">
            <Catalog uuid="11111111-1111-1111-1111-1111111111d1">
                <Properties>
                    <Name>Товары</Name>
                </Properties>
                <ChildObjects>
                    <Attribute uuid="22222222-2222-2222-2222-2222222222d2">
                        <Properties>
                            <Name>Артикул</Name>
                            <Type>
                                <v8:Type>xs:string</v8:Type>
                                <v8:StringQualifiers>
                                    <v8:Length>25</v8:Length>
                                </v8:StringQualifiers>
                            </Type>
                        </Properties>
                    </Attribute>
                </ChildObjects>
            </Catalog>
        </MetaDataObject>
        """;

    private const string DocumentXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <MetaDataObject xmlns="http://v8.1c.ru/8.3/MDClasses" xmlns:v8="http://v8.1c.ru/8.1/data/core" xmlns:cfg="http://v8.1c.ru/8.1/data/enterprise/current-config" version="2.20">
            <Document uuid="33333333-3333-3333-3333-3333333333d3">
                <Properties>
                    <Name>РеализацияТоваров</Name>
                </Properties>
                <ChildObjects>
                    <Attribute uuid="44444444-4444-4444-4444-4444444444d4">
                        <Properties>
                            <Name>Организация</Name>
                            <Type>
                                <v8:Type>xs:string</v8:Type>
                                <v8:StringQualifiers>
                                    <v8:Length>100</v8:Length>
                                </v8:StringQualifiers>
                            </Type>
                        </Properties>
                    </Attribute>
                </ChildObjects>
            </Document>
        </MetaDataObject>
        """;

    private const string AccumulationRegisterXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <MetaDataObject xmlns="http://v8.1c.ru/8.3/MDClasses" xmlns:v8="http://v8.1c.ru/8.1/data/core" xmlns:cfg="http://v8.1c.ru/8.1/data/enterprise/current-config" version="2.20">
            <AccumulationRegister uuid="55555555-5555-5555-5555-5555555555d5">
                <Properties>
                    <Name>ОстаткиТоваров</Name>
                </Properties>
            </AccumulationRegister>
        </MetaDataObject>
        """;

    private const string ConstantXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <MetaDataObject xmlns="http://v8.1c.ru/8.3/MDClasses" xmlns:v8="http://v8.1c.ru/8.1/data/core" xmlns:cfg="http://v8.1c.ru/8.1/data/enterprise/current-config" version="2.20">
            <Constant uuid="66666666-6666-6666-6666-6666666666d6">
                <Properties>
                    <Name>НастройкаПрограммы</Name>
                </Properties>
            </Constant>
        </MetaDataObject>
        """;

    private const string CommonModuleXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <MetaDataObject xmlns="http://v8.1c.ru/8.3/MDClasses" xmlns:v8="http://v8.1c.ru/8.1/data/core" version="2.20">
            <CommonModule uuid="77777777-7777-7777-7777-7777777777d7">
                <Properties>
                    <Name>ПриёмыКонфигурации</Name>
                    <Server>true</Server>
                </Properties>
            </CommonModule>
        </MetaDataObject>
        """;

    /// <summary>Модуль с процедурами: по одной на каждый новый приём каталога.</summary>
    private const string ModuleBsl = """
        // Проводит документ реализации товаров.
        Процедура ПровестиРеализациюТоваров() Экспорт
            ДокументСсылка = Документы.РеализацияТоваров.СоздатьДокумент();
            ДокументОбъект = ДокументСсылка.ПолучитьОбъект();
            ДокументОбъект.Записать(РежимЗаписиДокумента.Проведение);
        КонецПроцедуры

        // Ищет товар по коду.
        Функция НайтиТоварПоКоду(Код) Экспорт
            Возврат Справочники.Товары.НайтиПоКоду(Код);
        КонецФункции

        // Записывает остатки товаров в регистр.
        Процедура ЗаписатьОстаткиТоваров() Экспорт
            НаборЗаписей = РегистрыНакопления.ОстаткиТоваров.СоздатьНаборЗаписей();
            НаборЗаписей.Записать();
        КонецПроцедуры

        // Формирует печатную форму реализации.
        Функция СформироватьПечатнуюФормуРеализации() Экспорт
            ТабличныйДокумент = Новый ТабличныйДокумент;
            Макет = Документы.РеализацияТоваров.ПолучитьМакет("РеализацияТоваров");
            Область = Макет.ПолучитьОбласть("Шапка");
            ТабличныйДокумент.Вывести(Область);
            Возврат ТабличныйДокумент;
        КонецФункции

        // Сохраняет данные в файл на диске.
        Процедура СохранитьФайлНаДиске(Путь, Данные) Экспорт
            ТекстовыйДокумент = Новый ТекстовыйДокумент;
            ТекстовыйДокумент.УстановитьТекст(Данные);
            ТекстовыйДокумент.Записать(Путь);
        КонецПроцедуры

        // Сериализует настройки в JSON.
        Функция СериализоватьНастройкиВJSON(Настройки) Экспорт
            ЗаписьJSON = Новый ЗаписьJSON;
            ЗаписьJSON.УстановитьСтроку();
            ЗаписатьJSON(ЗаписьJSON, Настройки);
            Возврат ЗаписьJSON.Закрыть();
        КонецФункции

        // Возвращает представление товара.
        Функция ПолучитьПредставлениеТовара(Ссылка) Экспорт
            Возврат Строка(Ссылка);
        КонецФункции

        // Проверяет заполнение реквизитов реализации.
        Функция ПроверитьЗаполнениеРеализации(Объект) Экспорт
            Если Не ЗначениеЗаполнено(Объект) Тогда
                Возврат Ложь;
            КонецЕсли;
            Возврат Истина;
        КонецФункции

        // Читает значение константы настроек программы.
        Функция ПолучитьНастройкуПрограммы() Экспорт
            Возврат Константы.НастройкаПрограммы.Получить();
        КонецФункции

        // Замеряет время загрузки данных.
        Процедура ЗамеритьВремяЗагрузки() Экспорт
            Начало = ТекущаяУниверсальнаяДатаВМиллисекундах();
            Конец = ТекущаяУниверсальнаяДатаВМиллисекундах();
            Сообщить("Загрузка заняла " + (Конец - Начало) + " мс");
        КонецПроцедуры

        // Записывает ошибку в журнал регистрации.
        Процедура ЗаписатьОшибкуВЖурнал(Текст) Экспорт
            ЗаписьЖурналаРегистрации("Загрузка", УровеньЖурналаРегистрации.Ошибка, , , Текст);
        КонецПроцедуры

        // Выполняет действие в транзакции.
        Процедура ВыполнитьВТранзакции(Действие) Экспорт
            НачатьТранзакцию();
            Попытка
                Действие();
                ЗафиксироватьТранзакцию();
            Исключение
                ОтменитьТранзакцию();
                ВызватьИсключение;
            КонецПопытки;
        КонецПроцедуры
        """;

    /// <summary>Своя выгрузка в памяти: общий <see cref="SampleDump"/> не меняется.</summary>
    private static InMemoryDumpSource CreateSource()
    {
        var source = new InMemoryDumpSource("выгрузка с приёмами каталога");
        source.AddText("Configuration.xml", ConfigurationXml);
        source.AddText("Catalogs/Товары.xml", CatalogXml);
        source.AddText("Documents/РеализацияТоваров.xml", DocumentXml);
        source.AddText("AccumulationRegisters/ОстаткиТоваров.xml", AccumulationRegisterXml);
        source.AddText("Constants/НастройкаПрограммы.xml", ConstantXml);
        source.AddText("CommonModules/ПриёмыКонфигурации.xml", CommonModuleXml);
        source.AddText(ModulePath, ModuleBsl);
        return source;
    }

    /// <summary>Индекс на своей выгрузке: приёмы проверяются на том же пути, что и в работе инструмента.</summary>
    private sealed class CatalogFixture : IDisposable
    {
        private readonly SqliteIndex _index;

        internal CatalogFixture()
        {
            var source = CreateSource();
            var analyzed = new DumpAnalyzer().Analyze(source);
            _index = SqliteIndex.OpenInMemory();
            new IndexWriter(_index) { IncludeComments = true }.Write(source, analyzed);
            Query = new IndexReader(_index).ConventionQuery();
        }

        internal IConventionQuery Query { get; }

        internal ConventionAnswer Answer(string intent, int limit = 20) => Conventions.Suggest(Query, intent, limit: limit);

        public void Dispose() => _index.Dispose();
    }
}
