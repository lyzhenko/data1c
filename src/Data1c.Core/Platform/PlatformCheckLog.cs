namespace Data1c.Core.Platform;

/// <summary>Важность замечания штатной проверки платформы.</summary>
public enum PlatformCheckSeverity
{
    /// <summary>Сообщение без признака ошибки или предупреждения.</summary>
    Info,

    /// <summary>Предупреждение: конфигурация рабочая, но платформа на что-то указывает.</summary>
    Warning,

    /// <summary>Ошибка: конфигурация в таком виде не собирается.</summary>
    Error,
}

/// <summary>Одно замечание из журнала проверки.</summary>
/// <param name="FilePath">Путь файла выгрузки, если его удалось определить.</param>
/// <param name="Message">Текст сообщения.</param>
/// <param name="Line">Номер строки, если платформа его указала.</param>
/// <param name="Severity">Важность.</param>
/// <param name="Place">Место в терминах платформы («ОбщийМодуль.X.Модуль(3,9)») или путь из журнала.</param>
/// <param name="Snippet">Строка кода, на которую указала платформа (со вставкой <c>&lt;&lt;?&gt;&gt;</c>).</param>
public sealed record PlatformCheckProblem(
    string? FilePath,
    string Message,
    int? Line,
    PlatformCheckSeverity Severity,
    string? Place = null,
    string? Snippet = null);

/// <summary>Итог разбора журнала.</summary>
/// <param name="Problems">Разобранные замечания.</param>
/// <param name="Other">Строки журнала, которые не удалось отнести к замечаниям (не выбрасываются).</param>
/// <param name="Clean">Платформа сообщила, что ошибок нет.</param>
public sealed record PlatformCheckLogResult(
    IReadOnlyList<PlatformCheckProblem> Problems,
    IReadOnlyList<string> Other,
    bool Clean)
{
    /// <summary>Число ошибок.</summary>
    public int ErrorCount => Problems.Count(static problem => problem.Severity == PlatformCheckSeverity.Error);

    /// <summary>Число предупреждений.</summary>
    public int WarningCount => Problems.Count(static problem => problem.Severity == PlatformCheckSeverity.Warning);

    /// <summary>Нет ни одной ошибки (предупреждения допускаются).</summary>
    public bool Ok => ErrorCount == 0;
}

/// <summary>
/// Разбор журнала, который платформа пишет по ключу <c>/Out</c>. Форматы сняты с реальных запусков
/// на выгрузке 2,9 ГБ:
/// <list type="bullet">
/// <item>ошибки компиляции модулей: <c>{ОбщийМодуль.Имя.Модуль(3,9)}: Ожидается выражение</c>,
/// следующая строка с отступом — фрагмент кода со вставкой <c>&lt;&lt;?&gt;&gt;</c>;</item>
/// <item>замечания проверки: <c>Подсистема.Имя.Справка Неразрешимые ссылки на объекты метаданных (1)</c>;</item>
/// <item>предупреждения загрузки конфигурации: <c>Файл - &lt;путь&gt;: Возможно неверная ссылка …</c>;</item>
/// <item>итог: «Синтаксических ошибок не обнаружено!» или «Ошибок не обнаружено».</item>
/// </list>
/// Незнакомые строки не теряются, а возвращаются отдельным списком.
/// </summary>
public static class PlatformCheckLog
{
    private static readonly string[] CleanMarkers =
    [
        "Синтаксических ошибок не обнаружено",
        "Ошибок не обнаружено",
    ];

    /// <summary>
    /// Разбирает текст журнала. <paramref name="fallback"/> — важность для строк, у которых нет
    /// ни пути, ни явных слов платформы: у журнала проверки модулей это ошибки, у журнала загрузки —
    /// предупреждения.
    /// </summary>
    public static PlatformCheckLogResult Parse(string? text, PlatformCheckSeverity fallback = PlatformCheckSeverity.Error)
    {
        var problems = new List<PlatformCheckProblem>();
        var other = new List<string>();
        var clean = false;

        if (string.IsNullOrWhiteSpace(text))
        {
            return new PlatformCheckLogResult(problems, other, clean);
        }

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            if (CleanMarkers.Any(marker => trimmed.Contains(marker, StringComparison.OrdinalIgnoreCase)))
            {
                clean = true;
                continue;
            }

            // Продолжение предыдущего сообщения: платформа печатает строку кода с отступом.
            if (line.Length > 0 && char.IsWhiteSpace(line[0]) && problems.Count > 0 && LooksLikeSnippet(trimmed))
            {
                var previous = problems[^1];
                problems[^1] = previous with { Snippet = previous.Snippet is null ? trimmed : previous.Snippet + " " + trimmed };
                continue;
            }

            if (TryParseBraced(trimmed, out var place, out var message, out var lineNumber))
            {
                problems.Add(new PlatformCheckProblem(
                    PlatformLocation.ResolveFilePath(place),
                    message,
                    lineNumber,
                    PlatformCheckSeverity.Error,
                    place,
                    null));
                continue;
            }

            if (TryParseReportItem(trimmed, out var reported, out var reportMessage))
            {
                problems.Add(new PlatformCheckProblem(
                    PlatformLocation.ResolveFilePath(reported),
                    reportMessage,
                    null,
                    PlatformCheckSeverity.Warning,
                    reported,
                    null));
                continue;
            }

            var (path, body) = SplitPath(trimmed);
            var severity = ExplicitSeverity(trimmed);
            if (path is null && severity is null)
            {
                other.Add(trimmed);
                continue;
            }

            var (number, body_text) = SplitLineNumber(body);
            problems.Add(new PlatformCheckProblem(path, body_text, number, severity ?? fallback, path));
        }

        return new PlatformCheckLogResult(problems, other, clean);
    }

    /// <summary>Строка кода из сообщения платформы — она содержит вставку <c>&lt;&lt;?&gt;&gt;</c> или пометку контекста.</summary>
    private static bool LooksLikeSnippet(string line) =>
        line.Contains("<<?>>", StringComparison.Ordinal) || line.Contains("(Проверка:", StringComparison.Ordinal);

    /// <summary>Разбирает «{ОбщийМодуль.Имя.Модуль(3,9)}: Ожидается выражение».</summary>
    private static bool TryParseBraced(string line, out string place, out string message, out int? lineNumber)
    {
        place = string.Empty;
        message = string.Empty;
        lineNumber = null;

        if (!line.StartsWith('{'))
        {
            return false;
        }

        var close = line.IndexOf('}');
        if (close < 2)
        {
            return false;
        }

        place = line[1..close].Trim();
        var rest = line[(close + 1)..].TrimStart();
        if (rest.StartsWith(':'))
        {
            rest = rest[1..].Trim();
        }

        message = rest;
        lineNumber = PlatformLocation.LineOf(place);
        return message.Length > 0;
    }

    /// <summary>Разбирает «Подсистема.Имя.Справка Неразрешимые ссылки на объекты метаданных (1)».</summary>
    private static bool TryParseReportItem(string line, out string place, out string message)
    {
        place = string.Empty;
        message = string.Empty;

        var marker = line.LastIndexOf(" (", StringComparison.Ordinal);
        if (marker <= 0 || !line.EndsWith(')'))
        {
            return false;
        }

        var count = line[(marker + 2)..^1];
        if (!int.TryParse(count, out _))
        {
            return false;
        }

        var head = line[..marker];
        var space = head.IndexOf(' ');
        if (space <= 0)
        {
            return false;
        }

        place = head[..space];
        message = head[(space + 1)..] + " (" + count + ")";
        return looksLikeMetadataPath(place);
    }

    private static bool looksLikeMetadataPath(string value) =>
        value.Contains('.') && !value.Contains('/') && !value.Contains('\\') && !value.Contains(':');

    /// <summary>Отделяет путь файла от сообщения: платформа пишет «Файл - &lt;путь&gt;: сообщение».</summary>
    private static (string? FilePath, string Body) SplitPath(string line)
    {
        const string marker = "Файл - ";
        if (line.StartsWith(marker, StringComparison.Ordinal))
        {
            var body = line[marker.Length..];
            var separator = body.IndexOf(": ", StringComparison.Ordinal);
            return separator > 0
                ? (body[..separator].Trim(), body[(separator + 2)..].Trim())
                : (body.Trim(), string.Empty);
        }

        return (null, line);
    }

    /// <summary>Отделяет «Строка N:» от текста сообщения.</summary>
    private static (int? Line, string Message) SplitLineNumber(string body)
    {
        const string marker = "Строка ";
        if (!body.StartsWith(marker, StringComparison.Ordinal))
        {
            return (null, body);
        }

        var separator = body.IndexOf(':');
        if (separator < 0)
        {
            return (null, body);
        }

        var number = body[marker.Length..separator].Trim();
        return int.TryParse(number, out var parsed)
            ? (parsed, body[(separator + 1)..].Trim())
            : (null, body);
    }

    /// <summary>Важность, о которой платформа сказала словами; иначе <see langword="null"/>.</summary>
    private static PlatformCheckSeverity? ExplicitSeverity(string line)
    {
        if (line.Contains("Ошибк", StringComparison.OrdinalIgnoreCase))
        {
            return PlatformCheckSeverity.Error;
        }

        if (line.Contains("Предупрежд", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Возможно", StringComparison.OrdinalIgnoreCase))
        {
            return PlatformCheckSeverity.Warning;
        }

        return null;
    }
}
