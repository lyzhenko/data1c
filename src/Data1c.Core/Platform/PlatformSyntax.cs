namespace Data1c.Core.Platform;

/// <summary>
/// Разбор раздела «Синтаксис» темы справки: сколько параметров принимает метод платформы
/// и сколько из них обязательны.
/// </summary>
/// <remarks>
/// <para>Текст темы в справке устроен единообразно:
/// «… Синтаксис: СтрНайти(&lt;Строка&gt;, &lt;ПодстрокаПоиска&gt;, &lt;НаправлениеПоиска&gt;) Параметры:
/// &lt;Строка&gt; (обязательный) Тип: Строка. … &lt;НаправлениеПоиска&gt; (необязательный) …».</para>
/// <para>Разбор намеренно осторожный: если запись неоднозначна (несколько вариантов синтаксиса,
/// диапазон параметров вида «&lt;Значение1-Значение10&gt;», число параметров в сигнатуре не совпало
/// с числом пометок «обязательный/необязательный»), метод возвращает <see langword="false"/> —
/// лучше промолчать, чем выдать замечание на верный код.</para>
/// </remarks>
public static class PlatformSyntax
{
    private const string SyntaxMarker = "Синтаксис:";
    private const string ParametersMarker = "Параметры:";
    private const string RequiredMarker = "(обязательный)";
    private const string OptionalMarker = "(необязательный)";

    /// <summary>Заголовки разделов, которые завершают раздел параметров.</summary>
    private static readonly string[] SectionEnds =
    [
        "Описание:", "Возвращаемое значение:", "Доступность:", "Примечание:", "Пример:",
        "Использование в версии:", "Методическая информация",
    ];

    /// <summary>
    /// Определяет границы числа параметров метода платформы по тексту темы справки.
    /// </summary>
    /// <param name="text">Текст темы (<see cref="PlatformTopic.Text"/>).</param>
    /// <param name="required">Сколько параметров обязательно.</param>
    /// <param name="total">Сколько параметров принимает метод всего.</param>
    /// <returns><see langword="false"/>, если по тексту число параметров определить нельзя.</returns>
    public static bool TryGetParameterBounds(string? text, out int required, out int total)
    {
        required = 0;
        total = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var syntaxAt = text.IndexOf(SyntaxMarker, StringComparison.OrdinalIgnoreCase);
        if (syntaxAt < 0)
        {
            return false;
        }

        var open = text.IndexOf('(', syntaxAt);
        if (open < 0)
        {
            return false;
        }

        var close = FindClosingParenthesis(text, open);
        if (close < 0)
        {
            return false;
        }

        if (!TryCountSignature(text[(open + 1)..close], out var signature))
        {
            return false;
        }

        var parametersAt = text.IndexOf(ParametersMarker, close, StringComparison.OrdinalIgnoreCase);
        if (parametersAt < 0)
        {
            // У метода без параметров раздела «Параметры» в справке нет.
            total = signature;
            return signature == 0;
        }

        // Между сигнатурой и разделом параметров второй сигнатуры быть не должно: несколько
        // вариантов синтаксиса означают разное число параметров, и сравнивать не с чем.
        if (text.AsSpan(close + 1, parametersAt - close - 1).Contains('('))
        {
            return false;
        }

        var section = text[parametersAt..SectionEnd(text, parametersAt)];
        var requiredCount = CountOccurrences(section, RequiredMarker);
        var optionalCount = CountOccurrences(section, OptionalMarker);
        if (requiredCount + optionalCount != signature)
        {
            return false;
        }

        required = requiredCount;
        total = signature;
        return true;
    }

    /// <summary>Считает параметры сигнатуры: запятые верхнего уровня плюс один.</summary>
    private static bool TryCountSignature(string signature, out int count)
    {
        count = 0;
        var text = signature.Trim();
        if (text.Length == 0)
        {
            return true;
        }

        // Диапазон значений («&lt;Значение1-Значение10&gt;») или варианты записи делают число
        // параметров неоднозначным.
        if (text.Contains("...", StringComparison.Ordinal) || text.Contains('…'))
        {
            return false;
        }

        var depth = 0;
        var commas = 0;
        var start = 0;
        for (var index = 0; index < text.Length; index++)
        {
            var symbol = text[index];
            if (symbol == '(')
            {
                depth++;
            }
            else if (symbol == ')')
            {
                depth--;
                if (depth < 0)
                {
                    return false;
                }
            }
            else if (depth == 0 && (symbol == '[' || symbol == ']' || symbol == '{' || symbol == '}'))
            {
                return false;
            }
            else if (depth == 0 && symbol == ',')
            {
                if (!TryCountRange(text[start..index]))
                {
                    return false;
                }

                commas++;
                start = index + 1;
            }
        }

        if (depth != 0 || !TryCountRange(text[start..]))
        {
            return false;
        }

        count = commas + 1;
        return true;
    }

    /// <summary>
    /// Один параметр сигнатуры: диапазон вида «Значение1-Значение10» описывает несколько значений,
    /// поэтому число параметров по такой записи определить нельзя.
    /// </summary>
    private static bool TryCountRange(string parameter)
    {
        for (var index = 1; index + 1 < parameter.Length; index++)
        {
            if (parameter[index] != '-')
            {
                continue;
            }

            if (ContainsDigit(parameter, 0, index) && ContainsDigit(parameter, index + 1, parameter.Length))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ContainsDigit(string text, int start, int end)
    {
        for (var index = start; index < end; index++)
        {
            if (char.IsAsciiDigit(text[index]))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Индекс закрывающей скобки для «(» на позиции <paramref name="open"/>; −1 — не найдена.</summary>
    private static int FindClosingParenthesis(string text, int open)
    {
        var depth = 0;
        for (var index = open; index < text.Length; index++)
        {
            if (text[index] == '(')
            {
                depth++;
            }
            else if (text[index] == ')')
            {
                depth--;
                if (depth == 0)
                {
                    return index;
                }
            }
        }

        return -1;
    }

    /// <summary>Начало следующего раздела справки после раздела параметров.</summary>
    private static int SectionEnd(string text, int from)
    {
        var end = text.Length;
        foreach (var marker in SectionEnds)
        {
            var at = text.IndexOf(marker, from, StringComparison.OrdinalIgnoreCase);
            if (at >= 0 && at < end)
            {
                end = at;
            }
        }

        return end;
    }

    private static int CountOccurrences(string text, string marker)
    {
        var count = 0;
        var at = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        while (at >= 0)
        {
            count++;
            at = text.IndexOf(marker, at + marker.Length, StringComparison.OrdinalIgnoreCase);
        }

        return count;
    }
}
