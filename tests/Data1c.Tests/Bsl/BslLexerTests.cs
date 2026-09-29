using Data1c.Core.Bsl;
using Xunit;

namespace Data1c.Tests.Bsl;

/// <summary>Тесты лексического разбора модулей 1С (BSL).</summary>
public sealed class BslLexerTests
{
    [Fact]
    public void СтрокаСУдвоеннымиКавычкамиРазбираетсяКакОдинТокен()
    {
        var tokens = BslLexer.Tokenize("Сообщить(\"ООО \"\"Ромашка\"\"\");");

        Assert.Equal(6, tokens.Count);
        Assert.Equal(BslTokenKind.Identifier, tokens[0].Kind);
        Assert.Equal("Сообщить", tokens[0].GetText());
        Assert.Equal(BslTokenKind.Operator, tokens[1].Kind);
        Assert.Equal("(", tokens[1].GetText());
        Assert.Equal(BslTokenKind.String, tokens[2].Kind);
        // Токен строки включает обрамляющие кавычки, удвоения внутри остаются как в исходнике.
        Assert.Equal("\"ООО \"\"Ромашка\"\"\"", tokens[2].GetText());

        // Удвоенные кавычки — экранирование, а не конец литерала.
        Assert.Equal("ООО \"Ромашка\"", BslLexer.UnescapeString(tokens[2].Span));
        Assert.Equal(BslTokenKind.EndOfFile, tokens[^1].Kind);
    }

    [Fact]
    public void МногострочнаяСтрокаСПродолжениемЧерезВертикальнуюЧерту()
    {
        var text = "\"Первая строка\n|Вторая строка\n|  Третья строка\";\nПосле();";
        var tokens = BslLexer.Tokenize(text);

        Assert.Equal(BslTokenKind.String, tokens[0].Kind);
        var literal = tokens[0].GetText();
        Assert.Contains("|Вторая строка", literal, StringComparison.Ordinal);
        Assert.Contains("|  Третья строка", literal, StringComparison.Ordinal);

        // Один токен строки занял две строки исходника, счётчик строк учёл перевод внутри литерала.
        Assert.Equal(BslTokenKind.Operator, tokens[1].Kind);
        Assert.Equal(";", tokens[1].GetText());
        Assert.Equal(3, tokens[1].Line);
        Assert.Equal(18, tokens[1].Column);
        Assert.Equal(BslTokenKind.NewLine, tokens[2].Kind);
        Assert.Equal(BslTokenKind.Identifier, tokens[3].Kind);
        Assert.Equal("После", tokens[3].GetText());
        Assert.Equal(4, tokens[3].Line);
    }

    [Fact]
    public void МногострочныйЛитералБезОбрамляющихКавычекДаётЗначение()
    {
        var tokens = BslLexer.Tokenize("\"Строка\n|Продолжение\";");

        Assert.Equal("СтрокаПродолжение", BslLexer.UnescapeString(tokens[0].Span));
    }

    [Fact]
    public void СтрокаСПереносомБезВертикальнойЧертыНеСъедаетСледующуюСтроку()
    {
        var tokens = BslLexer.Tokenize("\"Незакрытая\nСледующая();");

        Assert.Equal(BslTokenKind.String, tokens[0].Kind);
        Assert.Equal("\"Незакрытая", tokens[0].GetText());
        Assert.Equal(BslTokenKind.NewLine, tokens[1].Kind);
        Assert.Equal(BslTokenKind.Identifier, tokens[2].Kind);
        Assert.Equal("Следующая", tokens[2].GetText());
        Assert.Equal(2, tokens[2].Line);
    }

    [Fact]
    public void КомментарийЗанимаетОтдельныйТокенДоКонцаСтроки()
    {
        var tokens = BslLexer.Tokenize("// Справочники.Товары.СоздатьЭлемент()\nПерем А;");

        Assert.Equal(BslTokenKind.Comment, tokens[0].Kind);
        Assert.Equal("// Справочники.Товары.СоздатьЭлемент()", tokens[0].GetText());
        Assert.Equal(1, tokens[0].Line);
        Assert.Equal(1, tokens[0].Column);

        // Содержимое комментария токенами не становится: следующая строка начинается с ключевого слова.
        Assert.Equal(BslTokenKind.Keyword, tokens[2].Kind);
        Assert.Equal("Перем", tokens[2].GetText());
        Assert.Equal(BslTokenKind.Identifier, tokens[3].Kind);
        Assert.Equal("А", tokens[3].GetText());
        Assert.Equal(2, tokens[3].Line);
        Assert.Equal(7, tokens[3].Column);
    }

    [Theory]
    [InlineData("Процедура")]
    [InlineData("ПРОЦЕДУРА")]
    [InlineData("процедура")]
    [InlineData("ПрОцЕдУрА")]
    [InlineData("Функция")]
    [InlineData("ЭКСПОРТ")]
    [InlineData("если")]
    [InlineData("Тогда")]
    public void КлючевыеСловаРаспознаютсяБезУчётаРегистра(string word)
    {
        var tokens = BslLexer.Tokenize(word);

        Assert.Equal(BslTokenKind.Keyword, tokens[0].Kind);
    }

    [Theory]
    [InlineData("Истина")]
    [InlineData("ЛОЖЬ")]
    [InlineData("Неопределено")]
    [InlineData("Null")]
    [InlineData("Ждать")]
    [InlineData("Асинх")]
    public void ЛитералыИСлужебныеСловаКлючевые(string word)
    {
        var tokens = BslLexer.Tokenize(word);

        Assert.Equal(BslTokenKind.Keyword, tokens[0].Kind);
    }

    [Fact]
    public void ИдентификаторМожетСодержатьКириллицуПодчёркиваниеИЦифры()
    {
        var tokens = BslLexer.Tokenize("_МояФункция1 и Ёжик");

        Assert.Equal(BslTokenKind.Identifier, tokens[0].Kind);
        Assert.Equal("_МояФункция1", tokens[0].GetText());
        Assert.Equal(BslTokenKind.Keyword, tokens[1].Kind); // «и»
        Assert.Equal(BslTokenKind.Identifier, tokens[2].Kind);
        Assert.Equal("Ёжик", tokens[2].GetText());
    }

    [Fact]
    public void ДробноеЧислоИспользуетЗапятуюКакРазделитель()
    {
        var tokens = BslLexer.Tokenize("Значение = 1,5 + 10;");

        Assert.Equal(BslTokenKind.Number, tokens[2].Kind);
        Assert.Equal("1,5", tokens[2].GetText());
        Assert.Equal(BslTokenKind.Number, tokens[4].Kind);
        Assert.Equal("10", tokens[4].GetText());
    }

    [Fact]
    public void ЗапятаяПослеЦелогоЧислаОстаётсяРазделителемСписка()
    {
        var tokens = BslLexer.Tokenize("Метод(1, 2);");

        Assert.Equal(BslTokenKind.Number, tokens[2].Kind);
        Assert.Equal("1", tokens[2].GetText());
        Assert.Equal(",", tokens[3].GetText());
        Assert.Equal("2", tokens[4].GetText());
    }

    [Fact]
    public void ЛитералДатыВыдаётсяОднимТокеном()
    {
        var tokens = BslLexer.Tokenize("Если Дата > '20240131' Тогда");

        Assert.Equal(BslTokenKind.Date, tokens[3].Kind);
        Assert.Equal("'20240131'", tokens[3].GetText());
    }

    [Fact]
    public void ОператорыИПунктуацияРазбираютсяПоОтдельности()
    {
        var tokens = BslLexer.Tokenize("<> <= >= = ; : ? ~ [ ] . ,");

        var operators = tokens
            .Where(t => t.Kind == BslTokenKind.Operator)
            .Select(t => t.GetText())
            .ToArray();

        Assert.Equal(["<>", "<=", ">=", "=", ";", ":", "?", "~", "[", "]", ".", ","], operators);
    }

    [Fact]
    public void ДирективаОбластиСохраняетИмяВИсходномРегистре()
    {
        var tokens = BslLexer.Tokenize("#Область СлужебныйПрограммныйИнтерфейс");

        Assert.Equal(BslTokenKind.Directive, tokens[0].Kind);
        Assert.Equal("#Область СлужебныйПрограммныйИнтерфейс", tokens[0].GetText());
    }

    [Theory]
    [InlineData("#КонецОбласти")]
    [InlineData("#Если Сервер Тогда")]
    [InlineData("#Иначе")]
    [InlineData("#КонецЕсли")]
    [InlineData("#Вставка")]
    [InlineData("#Удаление")]
    [InlineData("#Использовать \"МойМодуль\"")]
    public void ДирективыПрепроцессораВыдаютсяЦеликом(string line)
    {
        var tokens = BslLexer.Tokenize(line);

        Assert.Equal(BslTokenKind.Directive, tokens[0].Kind);
        Assert.Equal(line, tokens[0].GetText());
    }

    [Theory]
    [InlineData("&НаСервере")]
    [InlineData("&НаКлиенте")]
    [InlineData("&НаСервереБезКонтекста")]
    [InlineData("&НаКлиентеНаСервереБезКонтекста")]
    [InlineData("&НаСервереНаКлиенте")]
    public void АннотацииКомпиляцииВыдаютсяЦеликом(string annotation)
    {
        var tokens = BslLexer.Tokenize(annotation);

        Assert.Equal(BslTokenKind.Annotation, tokens[0].Kind);
        Assert.Equal(annotation, tokens[0].GetText());
    }

    [Fact]
    public void СтрокаИПозицияТокенаСчитаютсяПоФактическимПереводамСтрок()
    {
        var tokens = BslLexer.Tokenize("А = 1;\n\tБ = 2;\r\nВ = 3;");

        var identifiers = tokens.Where(t => t.Kind == BslTokenKind.Identifier).ToArray();

        Assert.Equal(3, identifiers.Length);
        Assert.Equal((1, 1), (identifiers[0].Line, identifiers[0].Column));
        Assert.Equal((2, 2), (identifiers[1].Line, identifiers[1].Column));
        Assert.Equal((3, 1), (identifiers[2].Line, identifiers[2].Column));
    }

    [Fact]
    public void ПустойТекстДаётТолькоКонецФайла()
    {
        Assert.Equal(BslTokenKind.EndOfFile, Assert.Single(BslLexer.Tokenize(string.Empty)).Kind);
        Assert.Equal(BslTokenKind.EndOfFile, Assert.Single(BslLexer.Tokenize(null)).Kind);
    }
}
