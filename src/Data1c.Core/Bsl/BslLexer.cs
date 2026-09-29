using System.Collections.Frozen;
using System.Text;

namespace Data1c.Core.Bsl;

/// <summary>Вид лексической единицы языка 1С (BSL).</summary>
public enum BslTokenKind
{
    /// <summary>Служебное значение по умолчанию (текст отсутствует).</summary>
    None = 0,

    /// <summary>Идентификатор: буквы (в том числе кириллица), цифры (не первым символом) и «_».</summary>
    Identifier,

    /// <summary>Ключевое слово языка (<c>Процедура</c>, <c>Если</c>, <c>И</c> …). Регистр не важен.</summary>
    Keyword,

    /// <summary>Числовой литерал. Разделитель дробной части — запятая: «1,5».</summary>
    Number,

    /// <summary>Строковый литерал вместе с кавычками: «"текст"». Может занимать несколько строк.</summary>
    String,

    /// <summary>Литерал даты вместе с апострофами: «'20240131'».</summary>
    Date,

    /// <summary>Комментарий вместе с «//» и текстом до конца строки.</summary>
    Comment,

    /// <summary>Директива препроцессора начиная с «#». Текст — вся строка, например «#Область Служебный».</summary>
    Directive,

    /// <summary>Директива компиляции начиная с «&amp;»: «&amp;НаСервере».</summary>
    Annotation,

    /// <summary>Перевод строки. Текст — «\n», «\r\n» или «\r».</summary>
    NewLine,

    /// <summary>Оператор или знак пунктуации (один символ, кроме двухсимвольных «&lt;=», «&gt;=», «&lt;&gt;»).</summary>
    Operator,

    /// <summary>Символ, который не удалось отнести ни к одной категории; разбор продолжается.</summary>
    Unexpected,

    /// <summary>Признак конца текста; всегда последний токен результата.</summary>
    EndOfFile,
}

/// <summary>
/// Лексическая единица BSL. Текст хранится как срез исходной строки, поэтому токенизация
/// не создаёт отдельную строку на каждый токен.
/// </summary>
/// <param name="Kind">Вид токена.</param>
/// <param name="Text">Срез текста модуля, относящийся к токену (для строк — вместе с кавычками).</param>
/// <param name="Line">Номер строки, 1-based.</param>
/// <param name="Column">Номер позиции в строке, 1-based (в единицах UTF-16, как индексация строк .NET).</param>
public readonly record struct BslToken(BslTokenKind Kind, ReadOnlyMemory<char> Text, int Line, int Column)
{
    /// <summary>Текст токена без кавычек/фигурных скобок — «сырое» значение, как в исходнике.</summary>
    public ReadOnlySpan<char> Span => Text.Span;

    /// <summary>Текст токена как строка. Создаёт новую строку — используйте там, где нужна именно строка.</summary>
    public string GetText() => new(Text.Span);

    public override string ToString() => $"{Kind} «{Text.Span}» ({Line}:{Column})";
}

/// <summary>
/// Лексер языка 1С (BSL). Один проход по тексту, без регулярных выражений и без посимвольного
/// создания строк: все токены — срезы исходного текста.
/// </summary>
/// <remarks>
/// Особенности разметки:
/// <list type="bullet">
/// <item>ключевые слова распознаются без учёта регистра (1С не различает регистр: <c>ПРОЦЕДУРА</c> == <c>Процедура</c>);</item>
/// <item>переводы строк выдаются отдельными токенами <see cref="BslTokenKind.NewLine"/> — так разборщику
/// не нужны отдельные счётчики строк, а строка/позиция любого токена всегда точны;</item>
/// <item>строка продолжается переводом строки, если следующая строка начинается с «|»
/// (необязательные пробелы перед «|» допускаются); такой токен <see cref="BslTokenKind.String"/>
/// занимает несколько строк, и счётчик строк учитывает все переводы внутри;</item>
/// <item>удвоение кавычек («""») внутри строки — экранирование и на длину токена не влияет:
/// токен заканчивается на последней одиночной кавычке;</item>
/// <item>директивы «#…» и аннотации «&amp;…» выдаются целиком, до конца строки
/// (для «#Область» имя области — это остаток строки после ключевого слова, регистр сохраняется);</item>
/// <item>незакрытая строка, дата или строка без завершающей кавычки завершаются переводом строки
/// (кроме продолжения через «|»), после чего разбор продолжается — лексер не бросает исключений.</item>
/// </list>
/// </remarks>
public static class BslLexer
{
    /// <summary>Токенизирует текст модуля. Последний токен результата — всегда <see cref="BslTokenKind.EndOfFile"/>.</summary>
    /// <param name="text">Текст модуля BSL.</param>
    /// <returns>Список токенов; для <see langword="null"/>/пустого текста — единственный токен конца файла.</returns>
    public static List<BslToken> Tokenize(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [new BslToken(BslTokenKind.EndOfFile, ReadOnlyMemory<char>.Empty, 1, 1)];
        }

        var tokens = new List<BslToken>(Math.Max(16, text.Length / 6));
        var source = text.AsMemory();
        var length = text.Length;
        var index = 0;
        var line = 1;
        var lineStart = 0;

        while (index < length)
        {
            var start = index;
            var c = text[index];
            var column = start - lineStart + 1;

            if (c == '\r' || c == '\n')
            {
                index++;
                if (c == '\r' && index < length && text[index] == '\n')
                {
                    index++;
                }

                tokens.Add(new BslToken(BslTokenKind.NewLine, source[start..index], line, column));
                line++;
                lineStart = index;
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                index++;
                continue;
            }

            if (IsIdentifierStart(c))
            {
                index++;
                while (index < length && IsIdentifierPart(text[index]))
                {
                    index++;
                }

                var kind = BslKeywords.Contains(FoldToLower(text, start, index - start))
                    ? BslTokenKind.Keyword
                    : BslTokenKind.Identifier;
                tokens.Add(new BslToken(kind, source[start..index], line, column));
                continue;
            }

            if (c == '"')
            {
                // Строка может занимать несколько строк: токен получает строку начала,
                // а счётчик строк сдвигается на все переводы внутри литерала.
                var stringLine = line;
                ReadString(text, ref index, ref line, ref lineStart);
                tokens.Add(new BslToken(BslTokenKind.String, source[start..index], stringLine, column));
                continue;
            }

            if (c == '\'')
            {
                ReadDate(text, ref index);
                tokens.Add(new BslToken(BslTokenKind.Date, source[start..index], line, column));
                continue;
            }

            if (char.IsAsciiDigit(c))
            {
                ReadNumber(text, ref index);
                tokens.Add(new BslToken(BslTokenKind.Number, source[start..index], line, column));
                continue;
            }

            if (c == '/' && index + 1 < length && text[index + 1] == '/')
            {
                index += 2;
                while (index < length && text[index] != '\r' && text[index] != '\n')
                {
                    index++;
                }

                tokens.Add(new BslToken(BslTokenKind.Comment, source[start..index], line, column));
                continue;
            }

            if (c == '#')
            {
                index++;
                while (index < length && text[index] != '\r' && text[index] != '\n')
                {
                    index++;
                }

                tokens.Add(new BslToken(BslTokenKind.Directive, source[start..index], line, column));
                continue;
            }

            if (c == '&')
            {
                index++;
                while (index < length && IsIdentifierPart(text[index]))
                {
                    index++;
                }

                tokens.Add(new BslToken(BslTokenKind.Annotation, source[start..index], line, column));
                continue;
            }

            if (IsOperatorChar(c))
            {
                index++;
                if ((c == '<' && index < length && (text[index] == '=' || text[index] == '>')) ||
                    (c == '>' && index < length && text[index] == '='))
                {
                    index++;
                }

                tokens.Add(new BslToken(BslTokenKind.Operator, source[start..index], line, column));
                continue;
            }

            index++;
            tokens.Add(new BslToken(BslTokenKind.Unexpected, source[start..index], line, column));
        }

        tokens.Add(new BslToken(BslTokenKind.EndOfFile, source[length..length], line, index - lineStart + 1));
        return tokens;
    }

    /// <summary>
    /// Возвращает значение строкового литерала без обрамляющих кавычек: «""» → «"»,
    /// служебные «|» многострочного продолжения и переводы строк отбрасываются.
    /// </summary>
    public static string UnescapeString(ReadOnlySpan<char> tokenText)
    {
        var start = 0;
        var end = tokenText.Length;
        if (end >= 2 && tokenText[0] == '"' && tokenText[end - 1] == '"')
        {
            start = 1;
            end--;
        }

        var builder = new StringBuilder(end - start);
        for (var i = start; i < end; i++)
        {
            var c = tokenText[i];
            if (c == '"')
            {
                // Удвоенная кавычка — экранированная; одиночная (закрывающая) уже отброшена выше.
                if (i + 1 < end && tokenText[i + 1] == '"')
                {
                    builder.Append('"');
                    i++;
                }

                continue;
            }

            if (c == '\r' || c == '\n')
            {
                // Перевод строки внутри литерала означает продолжение через «|»; сам «|» отбросим ниже.
                continue;
            }

            if (c == '|')
            {
                continue;
            }

            builder.Append(c);
        }

        return builder.ToString();
    }

    /// <summary>Приводит идентификатор к нижнему регистру (кириллица тоже), чтобы искать ключевые слова без учёта регистра.</summary>
    internal static string FoldToLower(string text, int start, int length)
    {
        var end = start + length;
        var alreadyLower = true;
        for (var i = start; i < end; i++)
        {
            var folded = FoldChar(text[i]);
            alreadyLower &= folded == text[i];
        }

        if (alreadyLower)
        {
            return text.Substring(start, length);
        }

        return string.Create(length, (Text: text, Start: start), static (destination, state) =>
        {
            for (var i = 0; i < destination.Length; i++)
            {
                destination[i] = FoldChar(state.Text[state.Start + i]);
            }
        });
    }

    /// <summary>Свёртка символа к нижнему регистру для ASCII и кириллицы (быстрее, чем <see cref="char.ToLowerInvariant(char)"/>).</summary>
    private static char FoldChar(char c)
    {
        if (c is >= 'A' and <= 'Z')
        {
            return (char)(c + 32);
        }

        // Кириллица: А-Я (U+0410..U+042F) и Ё (U+0401).
        if (c is >= 'А' and <= 'Я')
        {
            return (char)(c + 32);
        }

        return c == 'Ё' ? 'ё' : c;
    }

    /// <summary>Расширенный набор ключевых слов BSL, включая английские синонимы и слова, которые не могут быть именем метода.</summary>
    internal static readonly FrozenSet<string> BslKeywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Процедура", "КонецПроцедуры", "Функция", "КонецФункции", "Экспорт", "Перем", "Знач",
        "Если", "Тогда", "ИначеЕсли", "Иначе", "КонецЕсли",
        "Пока", "Для", "Каждого", "Из", "По", "Цикл", "КонецЦикла",
        "Возврат", "Прервать", "Продолжить",
        "Попытка", "Исключение", "КонецПопытки", "ВызватьИсключение",
        "Новый", "И", "Или", "Не", "Истина", "Ложь", "Неопределено",
        "Null", "Ждать", "Асинх",
        "Procedure", "EndProcedure", "Function", "EndFunction",
        "Var", "Val", "If", "Then", "ElsIf", "Else", "EndIf",
        "While", "For", "Each", "In", "To", "Do", "EndDo",
        "Return", "Break", "Continue", "Try", "Except", "EndTry", "Raise",
        "New", "And", "Or", "Not", "True", "False", "Undefined", "Await", "Async",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static bool IsIdentifierStart(char c) => char.IsLetter(c) || c == '_';

    /// <summary>Символ, допустимый в идентификаторе: буква (включая кириллицу), цифра или «_».</summary>
    internal static bool IsIdentifierPart(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static bool IsOperatorChar(char c) => c switch
    {
        '+' or '-' or '*' or '/' or '%' or '=' or '<' or '>' or
        '(' or ')' or '[' or ']' or ',' or '.' or ';' or '?' or ':' or '~' => true,
        _ => false,
    };

    /// <summary>
    /// Читает строковый литерал. По завершении <paramref name="index"/> стоит за закрывающей кавычкой,
    /// либо за последним символом незакрытой строки (перед переводом строки).
    /// </summary>
    private static void ReadString(string text, ref int index, ref int line, ref int lineStart)
    {
        var length = text.Length;
        index++; // открывающая кавычка

        while (index < length)
        {
            var c = text[index];
            if (c == '"')
            {
                if (index + 1 < length && text[index + 1] == '"')
                {
                    index += 2; // экранированная кавычка внутри литерала
                    continue;
                }

                index++;
                return;
            }

            if (c != '\r' && c != '\n')
            {
                index++;
                continue;
            }

            // Конец физической строки: литерал продолжается, только если следующая строка начинается с «|».
            var next = index + 1;
            if (c == '\r' && next < length && text[next] == '\n')
            {
                next++;
            }

            var probe = next;
            while (probe < length && (text[probe] == ' ' || text[probe] == '\t'))
            {
                probe++;
            }

            if (probe >= length || text[probe] != '|')
            {
                // Незакрытая строка: не выходим за перевод строки, разбор продолжится со следующей строки.
                return;
            }

            index = next;
            line++;
            lineStart = next;
        }
    }

    /// <summary>Читает литерал даты «'20240131'»; незакрытый литерал заканчивается в конце текста.</summary>
    private static void ReadDate(string text, ref int index)
    {
        var length = text.Length;
        index++; // открывающий апостроф

        while (index < length)
        {
            var c = text[index];
            if (c == '\r' || c == '\n')
            {
                return;
            }

            index++;
            if (c == '\'')
            {
                return;
            }
        }
    }

    /// <summary>Читает числовой литерал: целая часть, необязательная дробная через запятую, «e»/«E» с показателем.</summary>
    private static void ReadNumber(string text, ref int index)
    {
        var length = text.Length;
        while (index < length && char.IsAsciiDigit(text[index]))
        {
            index++;
        }

        if (index + 1 < length && text[index] == ',' && char.IsAsciiDigit(text[index + 1]))
        {
            index++;
            while (index < length && char.IsAsciiDigit(text[index]))
            {
                index++;
            }
        }

        if (index < length && (text[index] == 'e' || text[index] == 'E'))
        {
            var exponent = index + 1;
            if (exponent < length && (text[exponent] == '+' || text[exponent] == '-'))
            {
                exponent++;
            }

            if (exponent < length && char.IsAsciiDigit(text[exponent]))
            {
                index = exponent;
                while (index < length && char.IsAsciiDigit(text[index]))
                {
                    index++;
                }
            }
        }
    }
}
