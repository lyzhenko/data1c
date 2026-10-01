using System.Diagnostics;
using System.Globalization;
using Data1c.Core.Dump;

namespace Data1c.Core.Platform;

/// <summary>Итог штатной проверки выгрузки платформой 1С.</summary>
/// <param name="Clean">Платформа сообщила, что синтаксических ошибок нет.</param>
/// <param name="Problems">Замечания проверки модулей.</param>
/// <param name="Other">Строки журнала проверки, не отнесённые к замечаниям.</param>
/// <param name="LoadProblems">Замечания, выданные при загрузке конфигурации (например, ссылки в справке).</param>
/// <param name="CreateTime">Сколько заняло создание информационной базы.</param>
/// <param name="LoadTime">Сколько заняла загрузка конфигурации; <see langword="null"/>, если она пропущена.</param>
/// <param name="CheckTime">Сколько заняла проверка модулей.</param>
/// <param name="InfobasePath">Каталог информационной базы.</param>
/// <param name="LoadSkipped">Загрузка конфигурации пропущена как ненужная.</param>
public sealed record PlatformCheckOutcome(
    bool Clean,
    IReadOnlyList<PlatformCheckProblem> Problems,
    IReadOnlyList<string> Other,
    IReadOnlyList<PlatformCheckProblem> LoadProblems,
    TimeSpan CreateTime,
    TimeSpan? LoadTime,
    TimeSpan CheckTime,
    string InfobasePath,
    bool LoadSkipped)
{
    /// <summary>Число ошибок в модулях.</summary>
    public int ErrorCount => Problems.Count(static problem => problem.Severity == PlatformCheckSeverity.Error);

    /// <summary>Число предупреждений в модулях.</summary>
    public int WarningCount => Problems.Count(static problem => problem.Severity == PlatformCheckSeverity.Warning);
}

/// <summary>
/// Штатная проверка выгрузки платформой 1С: создать файловую информационную базу, загрузить в неё
/// конфигурацию из файлов и запустить <c>/CheckModules</c>.
///
/// Проверено на выгрузке 2,9 ГБ: создание базы 4 с, загрузка конфигурации 844 с, проверка 10 с.
/// Без информационной базы платформа уходит в диалог и не отвечает — поэтому база обязательна,
/// а её создание и загрузка конфигурации занимают четверть часа, что стоит помнить.
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

    /// <summary>Проверяет выгрузку: при <paramref name="reuse"/> загрузка пропускается, если база свежее выгрузки.</summary>
    public PlatformCheckOutcome Run(
        string dumpPath,
        string infobasePath,
        bool reuse = false,
        TimeSpan? loadTimeout = null,
        TimeSpan? checkTimeout = null)
    {
        var dump = Path.GetFullPath(dumpPath);
        var infobase = Path.GetFullPath(infobasePath);

        RequireNoSpaces(dump, "выгрузка");
        RequireNoSpaces(infobase, "информационная база");

        var configuration = Path.Combine(dump, "Configuration.xml");
        if (!File.Exists(configuration))
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

        var upToDate = File.Exists(dataFile)
            && File.GetLastWriteTimeUtc(dataFile) > File.GetLastWriteTimeUtc(configuration);
        var skipLoad = reuse && upToDate;

        TimeSpan? loadTime = null;
        if (skipLoad)
        {
            _log?.Invoke("база новее выгрузки — загрузку конфигурации пропускаю");
        }
        else
        {
            _log?.Invoke("загружаю конфигурацию из файлов (на большой выгрузке это минуты)");
            loadTime = RunOneC(
                ["DESIGNER", "/F", infobase, "/LoadConfigFromFiles", dump, "/Out", LogPath(infobase, "load"), "/DisableStartupDialogs", "/DisableStartupMessages"],
                loadTimeout ?? TimeSpan.FromMinutes(45));
        }

        _log?.Invoke("проверяю модули");
        var checkTime = RunOneC(
            ["DESIGNER", "/F", infobase, "/CheckModules", "/Out", LogPath(infobase, "check"), "/DisableStartupDialogs", "/DisableStartupMessages"],
            checkTimeout ?? TimeSpan.FromMinutes(15));

        var loadLog = ReadLog(LogPath(infobase, "load"));
        var checkLog = ReadLog(LogPath(infobase, "check"));
        var parsed = PlatformCheckLog.Parse(checkLog);
        var loadParsed = PlatformCheckLog.Parse(loadLog, PlatformCheckSeverity.Warning);

        _log?.Invoke(parsed.Clean
            ? "платформа: синтаксических ошибок не обнаружено"
            : $"платформа: ошибок {parsed.ErrorCount}, предупреждений {parsed.WarningCount}");

        return new PlatformCheckOutcome(
            parsed.Clean && parsed.Ok,
            parsed.Problems,
            parsed.Other,
            loadParsed.Problems,
            createTime,
            loadTime,
            checkTime,
            infobase,
            skipLoad);
    }

    /// <summary>Читает журнал 1С: кодировка определяется так же, как для текстов выгрузки.</summary>
    private static string? ReadLog(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        return DumpTextReader.ReadAllText(File.ReadAllBytes(path));
    }

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

        _log?.Invoke("  " + Path.GetFileName(ExecutablePath) + " " + string.Join(' ', arguments.Take(3)) + " …");

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
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Платформа вернула код {process.ExitCode.ToString(CultureInfo.InvariantCulture)}: {string.Join(' ', arguments)}");
        }

        return watch.Elapsed;
    }
}
