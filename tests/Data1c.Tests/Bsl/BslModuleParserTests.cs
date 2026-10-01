using Data1c.Core.Bsl;
using Data1c.Core.Metadata;
using Xunit;

namespace Data1c.Tests.Bsl;

/// <summary>Тесты структурного разбора модулей 1С (BSL): процедуры, области, вызовы, метаданные, диагностика.</summary>
public sealed class BslModuleParserTests
{
    [Fact]
    public void ОбластьЭкспортнаяФункцияСПараметрамиИЗначениямиПоУмолчанию()
    {
        var text = """
            #Область СлужебныйПрограммныйИнтерфейс

            // Печать документа.
            Функция СформироватьПечатнуюФорму(Документ, ИмяМакета = "Основной", Режим = Неопределено) Экспорт
                Возврат Истина;
            КонецФункции

            #КонецОбласти
            """;

        var info = Парсер().Parse(Модуль(text));

        var routine = Assert.Single(info.Routines);
        Assert.Equal("СформироватьПечатнуюФорму", routine.Name);
        Assert.Equal(BslRoutineKind.Function, routine.Kind);
        Assert.True(routine.IsExport);
        Assert.Equal(["Документ", "ИмяМакета", "Режим"], routine.Parameters);

        // Значения по умолчанию делают параметры необязательными: обязателен только «Документ».
        Assert.Equal(1, routine.RequiredCount);
        Assert.Equal(["Документ"], routine.RequiredParameters);
        Assert.Equal(4, routine.StartLine);
        Assert.Equal(6, routine.EndLine);
        Assert.Equal(3, routine.LineCount);
        Assert.Equal("СлужебныйПрограммныйИнтерфейс", routine.Region);
        Assert.Equal(1, routine.Depth);
        Assert.Empty(routine.Directives);
        Assert.Empty(info.Diagnostics);
    }

    [Fact]
    public void ДирективыКомпиляцииПривязываютсяКПроцедуреЧерезПустыеСтрокиИКомментарии()
    {
        var text = """
            &НаСервере
            &НаСервереБезКонтекста
            // Комментарий перед заголовком.
            Процедура ЗаписатьДанные() Экспорт
            КонецПроцедуры
            """;

        var info = Парсер().Parse(Модуль(text));

        var routine = Assert.Single(info.Routines);
        Assert.Equal(["&НаСервере", "&НаСервереБезКонтекста"], routine.Directives);
        Assert.True(routine.HasDirective("&насервере"));
        Assert.True(routine.IsExport);
        Assert.Equal((4, 5), (routine.StartLine, routine.EndLine));
    }

    [Fact]
    public void ВложенныеОбластиПолучаютГлубинуИБлижайшуюОбластьПроцедуры()
    {
        var text = """
            #Область ПрограммныйИнтерфейс
            #Область Методы

            Процедура Первая()
            КонецПроцедуры

            #Область Внутренняя

            Процедура Вторая()
            КонецПроцедуры

            #КонецОбласти
            #КонецОбласти
            #КонецОбласти
            """;

        var info = Парсер().Parse(Модуль(text));

        // Области попадают в список по мере закрытия, поэтому вложенные идут раньше внешних.
        Assert.Equal(
            [("Внутренняя", 7, 12, 2), ("Методы", 2, 13, 1), ("ПрограммныйИнтерфейс", 1, 14, 0)],
            info.Regions.Select(r => (r.Name, r.StartLine, r.EndLine, r.Depth)));

        var первая = info.Routines[0];
        Assert.Equal("Методы", первая.Region);
        Assert.Equal(2, первая.Depth);

        var вторая = info.Routines[1];
        Assert.Equal("Внутренняя", вторая.Region);
        Assert.Equal(3, вторая.Depth);
        Assert.Empty(info.Diagnostics);
    }

    [Fact]
    public void ЛокальныеИКвалифицированныеВызовыРазбираются()
    {
        var text = """
            Процедура Обработка()
                ПроверитьЗаполнение();
                Результат = ОбщегоНазначения.ЗначениеРеквизитаОбъекта(Ссылка, "Код");
            КонецПроцедуры
            """;

        var info = Парсер().Parse(Модуль(text));

        var calls = Assert.Single(info.Routines).Calls;
        Assert.Equal(2, calls.Count);

        Assert.Equal("ПроверитьЗаполнение", calls[0].Callee);
        Assert.Null(calls[0].Qualifier);
        Assert.Equal("ПроверитьЗаполнение", calls[0].Method);
        Assert.True(calls[0].IsLocal);
        Assert.Equal(2, calls[0].Line);

        Assert.Equal("ОбщегоНазначения.ЗначениеРеквизитаОбъекта", calls[1].Callee);
        Assert.Equal("ОбщегоНазначения", calls[1].Qualifier);
        Assert.Equal("ЗначениеРеквизитаОбъекта", calls[1].Method);
        Assert.False(calls[1].IsLocal);
        Assert.Equal(3, calls[1].Line);
    }

    [Fact]
    public void КлючевыеСловаНеСчитаютсяВызовами()
    {
        var text = """
            Процедура Циклы()
                Если А = 1 Тогда
                    Пока Б < 2 Цикл
                        Прервать;
                    КонецЦикла;
                КонецЕсли;
            КонецПроцедуры
            """;

        var info = Парсер().Parse(Модуль(text));

        Assert.Empty(Assert.Single(info.Routines).Calls);
    }

    [Fact]
    public void КонструкторНовыйНеСчитаетсяВызовомМетода()
    {
        var text = """
            Процедура Создание()
                Структура = Новый Структура("Ключ", 1);
                Массив = Новый Массив;
            КонецПроцедуры
            """;

        var info = Парсер().Parse(Модуль(text));

        Assert.Empty(Assert.Single(info.Routines).Calls);
    }

    [Fact]
    public void ОбращениеКМетаданнымЧерезТретийЭлементЦепочки()
    {
        var text = """
            Процедура СозданиеЭлемента()
                Элемент = Справочники.Товары.СоздатьЭлемент();
            КонецПроцедуры
            """;

        var info = Парсер().Parse(Модуль(text));

        var routine = Assert.Single(info.Routines);
        var access = Assert.Single(routine.MetadataAccesses);
        Assert.Equal(MdKind.Catalog, access.Kind);
        Assert.Equal("Товары", access.ObjectName);
        Assert.Equal("Справочники", access.Collection);
        Assert.Equal("Справочники.Товары", access.Text);
        Assert.Equal(2, access.Line);

        Assert.Equal("Справочники.Товары.СоздатьЭлемент", Assert.Single(routine.Calls).Callee);
    }

    [Fact]
    public void ОбращениеКМетаданнымИзДвухЭлементовБезВызова()
    {
        var text = """
            Процедура Поиск()
                Ссылка = Справочники.Товары.НайтиПоНаименованию("Ручка");
                МетаданныеОбъекта = Метаданные.Справочники.Товары;
                Имя = Метаданные.Справочники.Товары.Имя;
            КонецПроцедуры
            """;

        var info = Парсер().Parse(Модуль(text));

        var accesses = Assert.Single(info.Routines).MetadataAccesses;
        Assert.Equal(3, accesses.Count);

        // Для обращения через два первых элемента текст ограничивается ими:
        // «Справочники.Товары.НайтиПоНаименованию(...)» → «Справочники.Товары».
        Assert.Equal(
            (MdKind.Catalog, "Товары", "Справочники", "Справочники.Товары"),
            Ключи(accesses[0]));
        Assert.Equal(
            (MdKind.Catalog, "Товары", "Справочники", "Метаданные.Справочники.Товары"),
            Ключи(accesses[1]));
    }

    [Fact]
    public void ДваЭлементаСВызовомЭтоВызовМенеджераАНеОбращениеКМетаданным()
    {
        var text = """
            Процедура Проведение()
                Документы.ЗаказКлиента.Провести();
                Документы.Провести();
                РегистрыСведений.Курсы.Записать();
            КонецПроцедуры
            """;

        var info = Парсер().Parse(Модуль(text));

        var routine = Assert.Single(info.Routines);
        Assert.Equal(
            ["Документы.ЗаказКлиента.Провести", "Документы.Провести", "РегистрыСведений.Курсы.Записать"],
            routine.Calls.Select(c => c.Callee));

        // «Документы.Провести()» — вызов метода менеджера, а не обращение к объекту метаданных,
        // поэтому обращение к «Документы.Провести» не фиксируется: имени объекта в цепочке нет.
        Assert.Equal(
            [(MdKind.Document, "ЗаказКлиента", "Документы", "Документы.ЗаказКлиента"),
             (MdKind.InformationRegister, "Курсы", "РегистрыСведений", "РегистрыСведений.Курсы")],
            routine.MetadataAccesses.Select(Ключи));
    }

    [Fact]
    public void ВызовыИОбращенияВКодеМодуляПопадаютВОписаниеМодуля()
    {
        var text = """
            // Код модуля.
            ЗарегистрироватьОбработку();
            Константы.ВерсияКонфигурации.Получить();
            Документы.ЗаказКлиента.ПолучитьСсылку();

            Процедура Внутренняя()
            КонецПроцедуры
            """;

        var info = Парсер().Parse(Модуль(text));

        Assert.Equal(
            ["ЗарегистрироватьОбработку", "Константы.ВерсияКонфигурации.Получить", "Документы.ЗаказКлиента.ПолучитьСсылку"],
            info.Calls.Select(c => c.Callee));

        var accesses = info.MetadataAccesses;
        Assert.Equal(2, accesses.Count);
        Assert.Equal((MdKind.Constant, "ВерсияКонфигурации", "Константы"), (accesses[0].Kind, accesses[0].ObjectName, accesses[0].Collection));
        Assert.Equal((MdKind.Document, "ЗаказКлиента", "Документы"), (accesses[1].Kind, accesses[1].ObjectName, accesses[1].Collection));

        Assert.Empty(Assert.Single(info.Routines).Calls);
    }

    [Fact]
    public void АргументыВызоваПропускаютсяЦеликом()
    {
        // Осознанное упрощение: вызовы и обращения внутри аргументов не учитываются.
        var text = """
            Процедура Вызов()
                ОбщегоНазначения.Метод(Справочники.Товары, 1);
            КонецПроцедуры
            """;

        var info = Парсер().Parse(Модуль(text));

        var routine = Assert.Single(info.Routines);
        Assert.Equal("ОбщегоНазначения.Метод", Assert.Single(routine.Calls).Callee);

        // Внутри аргументов обращение к «Справочники.Товары» не разбирается.
        Assert.Empty(routine.MetadataAccesses);
        Assert.Empty(info.Diagnostics);
    }

    [Fact]
    public void НезакрытаяПроцедураЗавершаетсяКонцомМодуляИДаётДиагностику()
    {
        var text = """
            Процедура Незакрытая()

                А = 1;
            """;

        var info = Парсер().Parse(Модуль(text));

        var routine = Assert.Single(info.Routines);
        Assert.Equal(1, routine.StartLine);
        Assert.Equal(info.LineCount, routine.EndLine);

        var diagnostic = Assert.Single(info.Diagnostics);
        Assert.Equal(BslDiagnosticKind.UnclosedRoutine, diagnostic.Kind);
        Assert.Equal(1, diagnostic.Line);
    }

    [Fact]
    public void ЛишнийКонецПроцедурыДаётДиагностикуИНеЛомаетРазбор()
    {
        var text = """
            КонецПроцедуры

            Процедура Нормальная()
            КонецПроцедуры
            """;

        var info = Парсер().Parse(Модуль(text));

        var diagnostic = Assert.Single(info.Diagnostics);
        Assert.Equal(BslDiagnosticKind.UnexpectedRoutineEnd, diagnostic.Kind);
        Assert.Equal(1, diagnostic.Line);

        var routine = Assert.Single(info.Routines);
        Assert.Equal("Нормальная", routine.Name);
        Assert.Equal((3, 4), (routine.StartLine, routine.EndLine));
    }

    [Fact]
    public void НезакрытаяОбластьЗавершаетсяКонцомМодуляИДаётДиагностику()
    {
        var text = """
            #Область Незакрытая

            Процедура Метод()
            КонецПроцедуры
            """;

        var info = Парсер().Parse(Модуль(text));

        var region = Assert.Single(info.Regions);
        Assert.Equal("Незакрытая", region.Name);
        Assert.Equal(info.LineCount, region.EndLine);

        var diagnostic = Assert.Single(info.Diagnostics);
        Assert.Equal(BslDiagnosticKind.UnclosedRegion, diagnostic.Kind);
        Assert.Equal(1, diagnostic.Line);
    }

    [Fact]
    public void ЛишнийКонецОбластиДаётДиагностику()
    {
        var info = Парсер().Parse(Модуль("#КонецОбласти\n"));

        var diagnostic = Assert.Single(info.Diagnostics);
        Assert.Equal(BslDiagnosticKind.UnexpectedRegionEnd, diagnostic.Kind);
        Assert.Equal(1, diagnostic.Line);
        Assert.Empty(info.Regions);
    }

    [Fact]
    public void ПовторИмениПроцедурыДаётДиагностикуДублирования()
    {
        var text = """
            Процедура Метод()
            КонецПроцедуры

            Процедура метод()
            КонецПроцедуры
            """;

        var info = Парсер().Parse(Модуль(text));

        var diagnostic = Assert.Single(info.Diagnostics);
        Assert.Equal(BslDiagnosticKind.DuplicateRoutine, diagnostic.Kind);
        Assert.Equal(4, diagnostic.Line);
        Assert.Equal(2, info.Routines.Count);
    }

    [Fact]
    public void ПроцедурыВнутриДирективПрепроцессораНаходятся()
    {
        // В реальных выгрузках почти весь код обёрнут в #Если. Раньше заголовки внутри них
        // не разбирались: модуль оставался без символов, а строка «Процедура Метод(Отказ)»
        // попадала в вызовы. Проверяем и объединение одноимённых ветвей.
        var text = """
            #Если Сервер Тогда
            Процедура Обработка(Отказ)
                Сообщить("сервер");
            КонецПроцедуры
            #Иначе
            Процедура Обработка(Отказ)
                Сообщить("клиент");
            КонецПроцедуры
            #КонецЕсли

            #Если Клиент Тогда
            Функция Значение() Экспорт
                Возврат 1;
            КонецФункции
            #КонецЕсли
            """;

        var info = Парсер().Parse(Модуль(text));

        // Ветви одного #Если дают одну процедуру, вызовы обеих ветвей остаются при ней.
        Assert.Equal(2, info.Routines.Count);
        var handler = Assert.Single(info.Routines, static routine => routine.Name == "Обработка");
        Assert.Equal(BslRoutineKind.Procedure, handler.Kind);
        Assert.Equal(new[] { "Отказ" }, handler.Parameters);
        Assert.Equal(2, handler.Calls.Count);
        Assert.DoesNotContain(info.Diagnostics, static diagnostic => diagnostic.Kind == BslDiagnosticKind.DuplicateRoutine);

        var value = Assert.Single(info.Routines, static routine => routine.Name == "Значение");
        Assert.Equal(BslRoutineKind.Function, value.Kind);
        Assert.True(value.IsExport);
        Assert.True(value.StartLine > handler.StartLine);
    }

    [Fact]
    public void ВложеннаяПроцедураЗакрываетПредыдущуюИДаётДиагностику()
    {
        // Вложенные процедуры в 1С запрещены: разбор не должен падать на таком тексте.
        var text = """
            Процедура Внешняя()
                Процедура Внутренняя()
                КонецПроцедуры
            КонецПроцедуры
            """;

        var info = Парсер().Parse(Модуль(text));

        Assert.Equal(
            [("Внешняя", 1, 2), ("Внутренняя", 2, 3)],
            info.Routines.Select(r => (r.Name, r.StartLine, r.EndLine)));

        // Первая диагностика — о незакрытой внешней процедуре, вторая — про «КонецПроцедуры»,
        // которому уже не соответствует открытая процедура: разбор продолжается до конца файла.
        Assert.Equal(
            [(BslDiagnosticKind.UnclosedRoutine, 2), (BslDiagnosticKind.UnexpectedRoutineEnd, 4)],
            info.Diagnostics.Select(d => (d.Kind, d.Line)));
    }

    [Fact]
    public void ЧислоСтрокМодуляУчитываетРазныеПереводыСтрок()
    {
        Assert.Equal(0, Парсер().Parse(Модуль(string.Empty)).LineCount);
        Assert.Equal(1, Парсер().Parse(Модуль("А = 1;")).LineCount);
        Assert.Equal(2, Парсер().Parse(Модуль("А = 1;\nБ = 2;")).LineCount);
        Assert.Equal(3, Парсер().Parse(Модуль("А = 1;\r\nБ = 2;\r\nВ = 3;")).LineCount);
    }

    [Fact]
    public void ПустойМодульНеСодержитНиСтруктурНиДиагностик()
    {
        var info = Парсер().Parse(Модуль(string.Empty));

        Assert.True(info.IsEmpty);
        Assert.Empty(info.Routines);
        Assert.Empty(info.Regions);
        Assert.Empty(info.Diagnostics);
    }

    [Fact]
    public void КодМодуляИОбластиРазбираютсяВНекорректномТекстеБезИсключений()
    {
        var text = "Процедура A( && ) Экспорт\n\"строка\nСправочники.Товары.СоздатьЭлемент(\n";

        var info = Парсер().Parse(Модуль(text));

        Assert.True(info.LineCount > 0);
        Assert.NotNull(info.Diagnostics);
    }

    [Fact]
    public void ОбработчикПодпискиНаСобытиеРазбираетсяКакПроцедура()
    {
        var text = """
            &НаСервере
            Процедура ПриСозданииНаСервере(Отказ, СтандартнаяОбработка)
                ОбщегоНазначения.ПроверитьЗаполнение();
            КонецПроцедуры
            """;

        var info = Парсер().Parse(new BslModuleSource(
            "CommonForms/Форма/Ext/Form/Module.bsl",
            text,
            "CommonForm.Форма",
            BslModuleKind.FormModule));

        var routine = Assert.Single(info.Routines);
        Assert.Equal(BslRoutineKind.Procedure, routine.Kind);
        Assert.Equal(["Отказ", "СтандартнаяОбработка"], routine.Parameters);
        Assert.True(routine.HasDirective("&НаСервере"));
        Assert.Equal("ОбщегоНазначения.ПроверитьЗаполнение", Assert.Single(routine.Calls).Callee);
        Assert.Equal(BslModuleKind.FormModule, info.Kind);
        Assert.Equal("CommonForm.Форма", info.OwnerId);
        Assert.Equal("module:CommonForms/Форма/Ext/Form/Module.bsl", info.Id);
    }

    [Fact]
    public void ЗначенияПоУмолчаниюВыражениямиНеСчитаютсяПараметрами()
    {
        // Значение по умолчанию — выражение: имена внутри него параметрами не являются,
        // а обязательные параметры заканчиваются на первом «=».
        var text = """
            Функция Период(Дата, Начало = НачалоМесяца(Дата), Конец = КонецМесяца(Дата))
                Возврат Начало;
            КонецФункции
            """;

        var info = Парсер().Parse(Модуль(text));

        var routine = Assert.Single(info.Routines);
        Assert.Equal(["Дата", "Начало", "Конец"], routine.Parameters);
        Assert.Equal(1, routine.RequiredCount);
        Assert.Empty(info.Diagnostics);
    }

    [Fact]
    public void ПараметрБезИмениПередЗначениемПоУмолчаниюДаётДиагностику()
    {
        var info = Парсер().Parse(Модуль("Процедура П(А, = 1)\nКонецПроцедуры"));

        var routine = Assert.Single(info.Routines);
        Assert.Equal(["А"], routine.Parameters);

        // «=» без имени — ошибка заголовка; объявленный параметр от этого обязательным быть не перестаёт.
        Assert.Equal(1, routine.RequiredCount);
        Assert.Contains(info.Diagnostics, static diagnostic => diagnostic.Kind == BslDiagnosticKind.UnexpectedToken);
    }

    private static BslModuleParser Парсер() => new();

    private static BslModuleSource Модуль(string text) =>
        new("CommonModules/ТестовыйМодуль/Ext/Module.bsl", text, "CommonModule.ТестовыйМодуль", BslModuleKind.CommonModule);

    private static (MdKind Kind, string ObjectName, string Collection, string Text) Ключи(BslMetadataAccess access) =>
        (access.Kind, access.ObjectName, access.Collection, access.Text);
}
