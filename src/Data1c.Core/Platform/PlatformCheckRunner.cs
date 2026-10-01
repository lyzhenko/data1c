using System.Diagnostics;
using System.Globalization;
using Data1c.Core.Dump;

namespace Data1c.Core.Platform;

/// <summary>Что именно проверяет платформа.</summary>
public enum PlatformCheckMode
{
    /// <summary>
    /// Проверка конфигурации (<c>/CheckConfig</c>) — единственная, которая ловит ошибки в модулях.
    /// По умолчанию используется она.
    /// </summary>
    Config,

    /// <summary>
    /// Быстрая проверка модулей (<c>/CheckModules</c>). Замерено: **не находит** синтаксическую ошибку,
    /// специально внесённую в модуль (журнал — «Синтаксических ошибок не обнаружено»), поэтому
    /// полагаться на неё нельзя. Оставлена для случаев, когда нужен быстрый прогон без разбора кода.
    /// </summary>
    Modules,
}

/// <summary>Итог штатной проверки выгрузки платформой 1С.</summary>
/// <param name="Mode">Что проверялось.</param>
/// <param name="Clean">Платформа сообщила, что ошибок нет.</param>
/// <param name="Problems">Замечания проверки.</param>
/// <param name="Other">Строки журнала, не отнесённые к замечаниям.</param>
/// <param name="CreateTime">Сколько заняло создание информационной базы.</param>
/// <param name="LoadTime">Сколько заняла загрузка конфигурации; <see langword="null"/>, если пропущена.</param>
/// <param name="CheckTime">Сколько заняла проверка.</param>
/// <param name="InfobasePath">Каталог информационной базы.</param>
/// <param name="LoadSkipped">Загрузка конфигурации пропущена.</param>
/// <param name="StaleBase">База старше выгрузки: проверялась прежняя конфигурация.</param>
public sealed record PlatformCheckOutcome(
    PlatformCheckMode Mode,
    bool Clean,
    IReadOnlyList<PlatformCheckProblem> Problems,
    IReadOnlyList<string> Other,
    TimeSpan CreateTime,
    TimeSpan? LoadTime,
    TimeSpan CheckTime,
    string InfobasePath,
    bool LoadSkipped,
    bool StaleBase)
{
    /// <summary>Число ошибок.</summary>
    public int ErrorCount => Problems.Count(static problem => problem.Severity == PlatformCheckSeverity.Error);

    /// <summary>Число предупреждений.</summary>
    public int WarningCount => Problems.Count(static problem => problem.Severity == PlatformCheckSeverity.Warning);
}

/// <summary>
/// Штатная проверка выгрузки платформой 1С. Нужна файловая информационная база с загруженной
/// конфигурацией: платформа проверяет то, что находится в базе, а не файлы в каталоге.
///
/// Замеры на выгрузке 2,9 ГБ: создание базы 4 с, загрузка конфигурации 844 с, проверка
/// конфигурации 430 с. Проверка модулей (<c>/CheckModules</c>, 10 с) синтаксических ошибок
/// **не находит** — проверено внесением заведомой ошибки в модуль; ловит их только
/// <c>/CheckConfig</c>, поэтому по умолчанию используется он.
///
/// Пути к базе и выгрузке не должны содержать пробелов: строка соединения 1С передаётся одним
/// аргументом, и надёжно закавычить её в этом случае нельзя.
/// </summary>
public sealed class PlatformCheckRunner
{
    private readonly Action<string>? _log;

    /// <summary>Создаёт запускатель; <paramref name="executablePath"/> — путь к 1cv8.exe (иначе ищется).</summary>
    public PlatformCheckRunner(string? executablePath = null, Action<string>? log = null)
    {
        ExecutablePath = executablePath ?? TryFindExecutable()
            ?? throw new FileNotFoundException("Не найден 1cv8.exe: укажите путь ключом или установите платформу 1С.");
        _log = log;
    }

    /// <summary>Путь к исполняемому файлу платформы.</summary>
    public string ExecutablePath { get; }

    /// <summary>Ищет самую свежую установленную платформу 8.3.</summary>
    public static string? TryFindExecutable()
    {
        var roots = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "1cv8"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "1cv8"),
        };

        var candidates = new List<(Version Version, string Path)>();
        foreach (var root in roots)
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (var versionDirectory in Directory.EnumerateDirectories(root))
            {
                var executable = Path.Combine(versionDirectory, "bin", "1cv8.exe");
                if (!File.Exists(executable))
                {
                    continue;
                }

                var name = Path.GetFileName(versionDirectory);
                candidates.Add((Version.TryParse(name, out var version) ? version : new Version(0, 0), executable));
            }
        }

        return candidates.Count == 0
            ? null
            : candidates.OrderByDescending(static candidate => candidate.Version).First().Path;
    }

    /// <summary>
    /// Готовит базу: создаёт её при необходимости и загружает конфигурацию из выгрузки, не запуская
    /// проверку. Так дорогую часть (на выгрузке 2,9 ГБ — около 14 минут) можно выполнить заранее,
    /// а саму проверку запустить потом ключом <c>--check-only</c>.
    /// </summary>
    public (TimeSpan Create, TimeSpan Load) Prepare(string dumpPath, string infobasePath, TimeSpan? loadTimeout = null)
    {
        var dump = Path.GetFullPath(dumpPath);
        var infobase = Path.GetFullPath(infobasePath);
        RequireNoSpaces(dump, "выгрузка");
        RequireNoSpaces(infobase, "информационная база");

        if (!File.Exists(Path.Combine(dump, "Configuration.xml")))
        {
            throw new FileNotFoundException($"В выгрузке нет Configuration.xml: {dump}");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(infobase)!);
        var dataFile = Path.Combine(infobase, "1cv8.1CD");

        var create = TimeSpan.Zero;
        if (!File.Exists(dataFile))
        {
            _log?.Invoke("создаю файловую информационную базу");
            create = RunOneC(
                ["CREATEINFOBASE", $"File={infobase};", "/Out", LogPath(infobase, "create"), "/DisableStartupDialogs", "/DisableStartupMessages"],
                TimeSpan.FromMinutes(5));
        }
        else
        {
            _log?.Invoke("информационная база уже есть");
        }

        _log?.Invoke("загружаю конфигурацию из файлов (на большой выгрузке это 10–15 минут)");
        var load = RunOneC(
            ["DESIGNER", "/F", infobase, "/LoadConfigFromFiles", dump, "/Out", LogPath(infobase, "load"), "/DisableStartupDialogs", "/DisableStartupMessages"],
            loadTimeout ?? TimeSpan.FromMinutes(60));

        return (create, load);
    }

    /// <summary>
    /// Проверяет выгрузку. База создаётся при необходимости, конфигурация загружается в неё, если
    /// база старше самого свежего файла выгрузки; при <paramref name="reuse"/> загрузка не делается
    /// и в итоге отмечается, что проверялась прежняя конфигурация. При <paramref name="checkOnly"/>
    /// устаревшая база не допускается вовсе: иначе проверка молча подтвердила бы старый код.
    /// </summary>
    public PlatformCheckOutcome Run(
        string dumpPath,
        string infobasePath,
        PlatformCheckMode mode = PlatformCheckMode.Config,
        bool reuse = false,
        TimeSpan? timeout = null,
        TimeSpan? loadTimeout = null,
        bool checkOnly = false)
    {
        var dump = Path.GetFullPath(dumpPath);
        var infobase = Path.GetFullPath(infobasePath);

        RequireNoSpaces(dump, "выгрузка");
        RequireNoSpaces(infobase, "информационная база");

        if (!File.Exists(Path.Combine(dump, "Configuration.xml")))
        {
            throw new FileNotFoundException($"В выгрузке нет Configuration.xml: {dump}");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(infobase)!);
        var dataFile = Path.Combine(infobase, "1cv8.1CD");

        var createTime = TimeSpan.Zero;
        if (!File.Exists(dataFile))
        {
            _log?.Invoke("создаю файловую информационную базу");
            createTime = RunOneC(
                ["CREATEINFOBASE", $"File={infobase};", "/Out", LogPath(infobase, "create"), "/DisableStartupDialogs", "/DisableStartupMessages"],
                TimeSpan.FromMinutes(5));
        }
        else
        {
            _log?.Invoke("информационная база уже есть");
        }

        // Конфигурацию в базе нужно обновлять, когда изменился любой файл выгрузки: правка модуля
        // не трогает Configuration.xml, поэтому сравнения только с ним недостаточно.
        var newest = NewestWriteTime(dump);
        var stale = !File.Exists(dataFile) || File.GetLastWriteTimeUtc(dataFile) < newest;

        if (checkOnly && stale)
        {
            throw new InvalidOperationException(
                "База старше выгрузки: сначала выполните подготовку (--prepare), иначе проверка подтвердила бы прежний код.");
        }

        var skipLoad = reuse && stale;

        TimeSpan? loadTime = null;
        if (skipLoad)
        {
            _log?.Invoke("ВНИМАНИЕ: база старше выгрузки, проверяется прежняя конфигурация (--reuse)");
        }
        else if (stale)
        {
            _log?.Invoke("загружаю конфигурацию из файлов (на большой выгрузке это 10–15 минут)");
            loadTime = RunOneC(
                ["DESIGNER", "/F", infobase, "/LoadConfigFromFiles", dump, "/Out", LogPath(infobase, "load"), "/DisableStartupDialogs", "/DisableStartupMessages"],
                loadTimeout ?? TimeSpan.FromMinutes(60));
        }
        else
        {
            _log?.Invoke("база новее выгрузки — загрузку конфигурации пропускаю");
        }

        var check = LogPath(infobase, "check");
        var key = mode == PlatformCheckMode.Modules ? "/CheckModules" : "/CheckConfig";
        _log?.Invoke(mode == PlatformCheckMode.Modules
            ? "проверяю модули (быстрая проверка, ошибок в коде не находит)"
            : "проверяю конфигурацию (это несколько минут)");

        var checkTime = RunOneC(
            ["DESIGNER", "/F", infobase, key, "/Out", check, "/DisableStartupDialogs", "/DisableStartupMessages"],
            timeout ?? (mode == PlatformCheckMode.Modules ? TimeSpan.FromMinutes(10) : TimeSpan.FromMinutes(45)));

        var parsed = PlatformCheckLog.Parse(ReadLog(check));

        _log?.Invoke(parsed.Clean
            ? "платформа: ошибок не обнаружено"
            : $"платформа: ошибок {parsed.ErrorCount}, предупреждений {parsed.WarningCount}");

        return new PlatformCheckOutcome(
            mode,
            parsed.Clean && parsed.Ok,
            parsed.Problems,
            parsed.Other,
            createTime,
            loadTime,
            checkTime,
            infobase,
            skipLoad || loadTime is null,
            stale);
    }

    /// <summary>Самый свежий файл выгрузки: по нему решается, нужно ли перечитывать конфигурацию в базу.</summary>
    private static DateTime NewestWriteTime(string dump)
    {
        var newest = File.GetLastWriteTimeUtc(Path.Combine(dump, "Configuration.xml"));
        foreach (var file in Directory.EnumerateFiles(dump, "*", SearchOption.AllDirectories))
        {
            // Служебный каталог .data1c (индекс, базы проверки) к выгрузке не относится.
            if (file.Contains(Path.DirectorySeparatorChar + ".data1c" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                continue;
            }

            var written = File.GetLastWriteTimeUtc(file);
            if (written > newest)
            {
                newest = written;
            }
        }

        return newest;
    }

    /// <summary>Читает журнал 1С: кодировка определяется так же, как для текстов выгрузки.</summary>
    private static string? ReadLog(string path) =>
        File.Exists(path) ? DumpTextReader.ReadAllText(File.ReadAllBytes(path)) : null;

    private static string LogPath(string infobase, string name) => infobase + "." + name + ".log";

    private static void RequireNoSpaces(string path, string what)
    {
        if (path.Contains(' ', StringComparison.Ordinal))
        {
            throw new ArgumentException($"{what}: путь не должен содержать пробелов (ограничение строки соединения 1С): {path}");
        }
    }

    /// <summary>Запускает платформу и ждёт завершения; по таймауту снимает процесс вместе с потомками.</summary>
    private TimeSpan RunOneC(IReadOnlyList<string> arguments, TimeSpan timeout)
    {
        var info = new ProcessStartInfo
        {
            FileName = ExecutablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        _log?.Invoke("  " + Path.GetFileName(ExecutablePath) + " " + string.Join(' ', arguments.Take(4)) + " …");

        var watch = Stopwatch.StartNew();
        using var process = Process.Start(info)
            ?? throw new InvalidOperationException("Не удалось запустить платформу 1С.");

        if (!process.WaitForExit((int)Math.Min(timeout.TotalMilliseconds, int.MaxValue)))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Процесс уже завершился — ничего страшного.
            }

            throw new TimeoutException($"Платформа не ответила за {timeout.TotalMinutes:F0} мин: {string.Join(' ', arguments)}");
        }

        watch.Stop();

        // У проверки конфигурации код возврата 101 означает «есть замечания» — это не сбой запуска.
        if (process.ExitCode != 0 && process.ExitCode != 101)
        {
            throw new InvalidOperationException(
                $"Платформа вернула код {process.ExitCode.ToString(CultureInfo.InvariantCulture)}: {string.Join(' ', arguments)}");
        }

        return watch.Elapsed;
    }
}
