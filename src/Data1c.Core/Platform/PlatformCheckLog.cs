namespace Data1c.Core.Platform;

/// <summary>Важность замечания штатной проверки платформы.</summary>
public enum PlatformCheckSeverity
{
    /// <summary>Сообщение без признака ошибки или предупреждения.</summary>
    Info,

    /// <summary>Предупреждение: платформа считает код рабочим, но на что-то указывает.</summary>
    Warning,

    /// <summary>Ошибка: конфигурация в таком виде не собирается.</summary>
    Error,
}

/// <summary>Одно замечание из журнала проверки: файл выгрузки, строка и текст.</summary>
/// <param name="FilePath">Путь файла, как его напечатала платформа (может быть не указан).</param>
/// <param name="Message">Текст сообщения.</param>
/// <param name="Line">Номер строки, если платформа его указала.</param>
/// <param name="Severity">Важность.</param>
public sealed record PlatformCheckProblem(string? FilePath, string Message, int? Line, PlatformCheckSeverity Severity);

/// <summary>Итог разбора журнала.</summary>
/// <param name="Problems">Разобранные замечания.</param>
/// <param name="Other">Строки журнала, которые не удалось отнести к замечаниям (не выбрасываются).</param>
/// <param name="Clean">Платформа сообщила, что синтаксических ошибок нет.</param>
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
/// Разбор журнала, который платформа пишет по ключу <c>/Out</c>.
/// Формат строк: «Файл - &lt;путь&gt;: &lt;сообщение&gt;», сообщение может начинаться со «Строка N:».
/// Итоговая строка «Синтаксических ошибок не обнаружено!» означает, что замечаний нет.
/// Незнакомые строки не теряются, а возвращаются отдельным списком.
/// </summary>
public static class PlatformCheckLog
{
    private const string CleanMarker = "Синтаксических ошибок не обнаружено";

    /// <summary>
    /// Разбирает текст журнала. <paramref name="fallback"/> — важность для строк, у которых есть
    /// путь файла, но нет слов «Ошибка» или «Предупреждение»: у журнала проверки модулей такие
    /// строки ошибки, у журнала загрузки конфигурации — предупреждения.
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
            var line = raw.Trim().TrimEnd('\r');
            if (line.Length == 0)
            {
                continue;
            }

            if (line.Contains(CleanMarker, StringComparison.OrdinalIgnoreCase))
            {
                clean = true;
                continue;
            }

            var (path, body) = SplitPath(line);
            var severity = ExplicitSeverity(line);

            // Строка без пути и без слов об ошибке — это не диагностика, а служебный текст.
            if (path is null && severity is null)
            {
                other.Add(line);
                continue;
            }

            var (number, message) = SplitLineNumber(body);
            problems.Add(new PlatformCheckProblem(path, message, number, severity ?? fallback));
        }

        return new PlatformCheckLogResult(problems, other, clean);
    }

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
