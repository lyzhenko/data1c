using Data1c.Core.Platform;
using Data1c.Tests.Platform;
using Xunit;

namespace Data1c.Tests.Platform;

/// <summary>
/// Разбор раздела «Синтаксис» справки платформы: сколько параметров принимает метод. Нужен проверке
/// черновика, чтобы ругаться на «Массив.Добавить(1, 2)», но молчать там, где запись неоднозначна.
/// </summary>
public sealed class PlatformSyntaxTests
{
    private static readonly Version Version27 = new(8, 3, 27, 2214);

    [Fact]
    public void Определяет_обязательные_и_все_параметры()
    {
        const string Text =
            "СтрНайти (StrFind) Синтаксис: СтрНайти(<Строка>, <ПодстрокаПоиска>, <НаправлениеПоиска>, "
            + "<НачальнаяПозиция>, <НомерВхождения>) Параметры: <Строка> (обязательный) Тип: Строка. "
            + "<ПодстрокаПоиска> (обязательный) Тип: Строка. <НаправлениеПоиска> (необязательный) Тип: НаправлениеПоиска. "
            + "<НачальнаяПозиция> (необязательный) Тип: Число. <НомерВхождения> (необязательный) Тип: Число. "
            + "Описание: Ищет подстроку.";

        Assert.True(PlatformSyntax.TryGetParameterBounds(Text, out var required, out var total));
        Assert.Equal(2, required);
        Assert.Equal(5, total);
    }

    [Fact]
    public void Метод_без_параметров_даёт_ноль()
    {
        const string Text = "Сообщить (Message) Синтаксис: Сообщить() Описание: Выводит сообщение.";

        Assert.True(PlatformSyntax.TryGetParameterBounds(Text, out var required, out var total));
        Assert.Equal(0, required);
        Assert.Equal(0, total);
    }

    [Fact]
    public void Диапазон_параметров_не_разбирается()
    {
        const string Text =
            "СтрШаблон (StrTemplate) Синтаксис: СтрШаблон(<Шаблон>, <Значение1-Значение10>) Параметры: "
            + "<Шаблон> (обязательный) Тип: Строка. <Значение1-Значение10> (необязательный) Тип: Произвольный. "
            + "Описание: Подставляет значения.";

        Assert.False(PlatformSyntax.TryGetParameterBounds(Text, out _, out _));
    }

    [Fact]
    public void Несколько_вариантов_синтаксиса_не_разбираются()
    {
        const string Text =
            "Получить (Get) Синтаксис: Получить(<Ключ>) или Получить(<Ключ>, <Значение>) Параметры: "
            + "<Ключ> (обязательный) Тип: Строка. <Значение> (необязательный) Тип: Произвольный. Описание: Читает значение.";

        Assert.False(PlatformSyntax.TryGetParameterBounds(Text, out _, out _));
    }

    [Fact]
    public void Несогласованные_пометки_параметров_не_разбираются()
    {
        // В сигнатуре три параметра, а в разделе «Параметры» помечен один: запись непонятна.
        const string Text =
            "Метод (Method) Синтаксис: Метод(<Первый>, <Второй>, <Третий>) Параметры: "
            + "<Первый> (обязательный) Тип: Строка. Описание: Что-то делает.";

        Assert.False(PlatformSyntax.TryGetParameterBounds(Text, out _, out _));
    }

    [Fact]
    public void Без_раздела_синтаксис_параметры_неизвестны()
    {
        Assert.False(PlatformSyntax.TryGetParameterBounds("Просто описание без синтаксиса.", out _, out _));
        Assert.False(PlatformSyntax.TryGetParameterBounds(null, out _, out _));
    }

    [Fact]
    public void Глобальная_функция_находится_по_короткому_имени()
    {
        // В справке глобальные функции названы с уточнением, поэтому ContainsMember по короткому
        // имени их не видит, и вызов «Сообщить("текст")» выглядел бы неизвестной процедурой.
        var index = CreateIndex();

        Assert.False(index.ContainsMember("Сообщить"));
        Assert.Equal("Глобальный контекст.Сообщить (Global context.Message)", index.FindGlobalFunction("Сообщить")?.Title);
        Assert.Null(index.FindGlobalFunction("Массив.Добавить"));
        Assert.Null(index.FindGlobalFunction("НетТакойФункции"));
        Assert.Null(index.FindGlobalFunction(null));
    }

    [Fact]
    public void Свойства_и_события_глобального_контекста_не_считаются_функциями()
    {
        // В разделе «Глобальный контекст» справки лежат ещё свойства и события модулей: вызвать их
        // нельзя, поэтому глобальными функциями они не считаются — иначе вызов несуществующей
        // процедуры с таким именем не был бы замечен.
        var index = CreateIndex();

        Assert.Null(index.FindGlobalFunction("ОбработкаОшибок"));
        Assert.Null(index.FindGlobalFunction("ПриНачалеРаботыСистемы"));
        Assert.DoesNotContain("ОбработкаОшибок", index.GlobalFunctionNames);
        Assert.DoesNotContain("ErrorProcessing", index.GlobalFunctionNames);
        Assert.Contains("Сообщить", index.GlobalFunctionNames);
        Assert.Contains("Message", index.GlobalFunctionNames);
    }

    private static PlatformHelpIndex CreateIndex()
    {
        var source = new InMemoryPlatformSource("синтетическая справка");
        source.AddHelpFile(
            Version27,
            PlatformHelpKind.SyntaxAssistant,
            "bin/shcntx_ru.hbk",
            HbkTestWriter.Create(
            [
                // Пути тем повторяют настоящие: имя темы — служебный идентификатор, а человекочитаемое
                // имя живёт в заголовке. Именно поэтому ContainsMember по короткому имени не работает.
                ("objects.catalog234.Array.methods.Add772.html", "<h1>Массив.Добавить (Array.Add)</h1><p>Добавляет значение в массив.</p>"),
                ("objects.Global context.methods.catalog27.Message30.html", "<h1>Глобальный контекст.Сообщить (Global context.Message)</h1><p>Синтаксис: Сообщить() Описание: Выводит сообщение.</p>"),
                ("objects.Global context.properties.prop5975.html", "<h1>Глобальный контекст.ОбработкаОшибок (Global context.ErrorProcessing)</h1><p>Описание: Признак обработки ошибок.</p>"),
                ("objects.Global context.events.catalog375.OnStart377.html", "<h1>Глобальный контекст.ПриНачалеРаботыСистемы (Global context.OnStart)</h1><p>Описание: Обработчик события.</p>"),
            ]));
        return new PlatformHelpIndex(source);
    }
}
