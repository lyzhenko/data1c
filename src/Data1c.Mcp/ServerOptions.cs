namespace Data1c.Mcp;

/// <summary>Разбор аргументов командной строки MCP-сервера.</summary>
/// <param name="Options">Готовые настройки, если разбор удался.</param>
/// <param name="Help">Запрошена справка.</param>
/// <param name="Error">Текст ошибки, если аргументы неверны.</param>
public sealed record ServerOptionsParse(ServerOptions? Options, bool Help, string? Error);

/// <summary>
/// Настройки сервера: что разбирать и как. Сервер запускается MCP-клиентом (например, harness)
/// как дочерний процесс, поэтому вывод в stdout занят протоколом, а вся диагностика идёт в stderr.
/// </summary>
public sealed record ServerOptions
{
    /// <summary>
    /// Каталоги выгрузки в порядке наложения: первый — база конфигурации, следующие — расширения.
    /// Ключ --dump можно повторять; можно не задавать вовсе — тогда выгрузку открывает инструмент open.
    /// </summary>
    public IReadOnlyList<string> DumpPaths { get; init; } = [];

    /// <summary>Разбирать модули BSL. Без них работает только навигация по метаданным.</summary>
    public bool IncludeBsl { get; init; } = true;

    /// <summary>Строить связи вызовов.</summary>
    public bool IncludeCalls { get; init; } = true;

    /// <summary>Ограничить разбор секциями выгрузки (например, <c>CommonModules,Catalogs</c>).</summary>
    public IReadOnlyList<string>? Sections { get; init; }

    /// <summary>Степень параллелизма разбора; 0 — по числу процессоров.</summary>
    public int MaxDegreeOfParallelism { get; init; }

    /// <summary>Подключить справку установленной платформы (.hbk) для проверки методов.</summary>
    public bool PlatformHelp { get; init; }

    /// <summary>Язык справочных файлов платформы.</summary>
    public string PlatformLocale { get; init; } = "ru";

    /// <summary>Каталоги установленных платформ 1С; пусто — стандартные пути.</summary>
    public IReadOnlyList<string>? PlatformRoots { get; init; }

    /// <summary>Не начинать разбор при старте: первый вызов инструмента запустит его сам.</summary>
    public bool Lazy { get; init; }

    /// <summary>
    /// Путь к файлу SQLite-индекса. По умолчанию — <c>&lt;выгрузка&gt;/.data1c/index.db</c>:
    /// если индекса нет, сервер соберёт его один раз и дальше будет только читать.
    /// </summary>
    public string? IndexPath { get; init; }

    /// <summary>Отвечать из индекса. Выключено — работает разбор в память на каждый запуск.</summary>
    public bool UseIndex { get; init; } = true;

    public const string Usage = """
        Data1c.Mcp — MCP-сервер над библиотекой Data1c.Core (stdio, JSON-RPC 2.0).

        Использование:
          Data1c.Mcp [--dump <каталог выгрузки>] [ключи]

        Ключи:
          --dump <путь>          каталог выгрузки конфигурации 1С; ключ можно повторять: первый
                                 каталог — база, следующие — расширения (они перекрывают базу
                                 по совпадающим путям). Можно не задавать и открыть выгрузку
                                 позже инструментом open
          --index <путь>         путь к файлу индекса (по умолчанию <выгрузка>/.data1c/index.db);
                                 если индекса нет, сервер соберёт его один раз
          --no-index             не использовать индекс: разбирать выгрузку в память при каждом запуске
          --platform             подключить справку установленной платформы 1С (.hbk)
          --locale <код>         язык справочных файлов платформы (по умолчанию ru)
          --platform-root <путь> каталог установленных платформ (можно повторять)
          --sections <список>    разбирать только эти секции выгрузки, через запятую
          --no-bsl               не разбирать модули BSL
          --no-calls             не строить связи вызовов
          --max-dop <N>          степень параллелизма разбора (по умолчанию — по числу процессоров)
          --lazy                 начать разбор при первом вызове инструмента, а не при старте
          --help                 показать эту справку

        Диагностика пишется в stderr, stdout занят протоколом MCP.
        """;

    public static ServerOptionsParse Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        string? dump = null;
        List<string>? dumps = null;
        var platform = false;
        var locale = "ru";
        List<string>? roots = null;
        List<string>? sections = null;
        var includeBsl = true;
        var includeCalls = true;
        var maxDop = 0;
        var lazy = false;
        string? indexPath = null;
        var useIndex = true;

        for (var index = 0; index < args.Length; index++)
        {
            var (name, inline) = Split(args[index]);
            switch (name)
            {
                case "--help" or "-h" or "help":
                    return new ServerOptionsParse(null, true, null);

                case "--dump":
                    if (!TryValue(args, ref index, inline, name, out dump, out var dumpError))
                    {
                        return new ServerOptionsParse(null, false, dumpError);
                    }

                    (dumps ??= []).Add(dump!);
                    break;

                case "--platform":
                    platform = true;
                    break;

                case "--no-bsl":
                    includeBsl = false;
                    break;

                case "--no-calls":
                    includeCalls = false;
                    break;

                case "--lazy":
                    lazy = true;
                    break;

                case "--index":
                    if (!TryValue(args, ref index, inline, name, out var indexPathValue, out var indexPathError))
                    {
                        return new ServerOptionsParse(null, false, indexPathError);
                    }

                    indexPath = indexPathValue;
                    break;

                case "--no-index":
                    useIndex = false;
                    break;

                case "--locale":
                    if (!TryValue(args, ref index, inline, name, out var localeValue, out var localeError))
                    {
                        return new ServerOptionsParse(null, false, localeError);
                    }

                    locale = localeValue!;
                    break;

                case "--platform-root":
                    if (!TryValue(args, ref index, inline, name, out var root, out var rootError))
                    {
                        return new ServerOptionsParse(null, false, rootError);
                    }

                    (roots ??= []).Add(root!);
                    break;

                case "--sections":
                    if (!TryValue(args, ref index, inline, name, out var sectionsValue, out var sectionsError))
                    {
                        return new ServerOptionsParse(null, false, sectionsError);
                    }

                    sections = [.. sectionsValue!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
                    if (sections.Count == 0)
                    {
                        return new ServerOptionsParse(null, false, "Ключ --sections задан без списка секций.");
                    }

                    break;

                case "--max-dop":
                    if (!TryValue(args, ref index, inline, name, out var dopValue, out var dopError))
                    {
                        return new ServerOptionsParse(null, false, dopError);
                    }

                    if (!int.TryParse(dopValue, System.Globalization.CultureInfo.InvariantCulture, out maxDop) || maxDop < 0)
                    {
                        return new ServerOptionsParse(null, false, $"Ключ --max-dop: ожидалось неотрицательное число, получено «{dopValue}».");
                    }

                    break;

                default:
                    return new ServerOptionsParse(null, false, $"Неизвестный ключ «{args[index]}».");
            }
        }

        return new ServerOptionsParse(
            new ServerOptions
            {
                DumpPaths = dumps ?? [],
                IncludeBsl = includeBsl,
                IncludeCalls = includeCalls,
                Sections = sections,
                MaxDegreeOfParallelism = maxDop,
                PlatformHelp = platform,
                PlatformLocale = locale,
                PlatformRoots = roots,
                Lazy = lazy,
                IndexPath = indexPath,
                UseIndex = useIndex,
            },
            false,
            null);
    }

    private static (string Name, string? Value) Split(string argument)
    {
        var separator = argument.IndexOf('=');
        return separator < 0
            ? (argument, null)
            : (argument[..separator], argument[(separator + 1)..]);
    }

    private static bool TryValue(
        string[] args,
        ref int index,
        string? inline,
        string name,
        out string? value,
        out string? error)
    {
        if (inline is not null)
        {
            value = inline;
            error = string.IsNullOrWhiteSpace(inline) ? $"Ключ {name} задан без значения." : null;
            return error is null;
        }

        if (index + 1 >= args.Length)
        {
            value = null;
            error = $"Ключ {name} задан без значения.";
            return false;
        }

        value = args[++index];
        error = null;
        return true;
    }
}
