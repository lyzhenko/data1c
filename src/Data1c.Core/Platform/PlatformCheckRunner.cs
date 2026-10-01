using System.Diagnostics;
using System.Globalization;
using Data1c.Core.Dump;

namespace Data1c.Core.Platform;

/// <summary>Что именно проверяет платформа.</summary>
public enum PlatformCheckMode
{
    /// <summary>Синтаксис модулей (<c>/CheckModules</c>) — быстрая проверка, секунды.</summary>
    Modules,

    /// <summary>Вся конфигурация (<c>/CheckConfig</c>) — дольше и строже, сообщает о неразрешимых ссылках.</summary>
    Config,
}

/// <summary>Итог штатной проверки выгрузки платформой 1С.</summary>
/// <param name="Mode">Что проверялось.</param>
/// <param name="Clean">Платформа сообщила, что синтаксических ошибок нет.</param>
/// <param name="Problems">Замечания проверки.</param>
/// <param name="Other">Строки журнала, не отнесённые к замечаниям.</param>
/// <param name="CreateTime">Сколько заняло создание информационной базы.</param>
/// <param name="CheckTime">Сколько заняла проверка.</param>
/// <param name="InfobasePath">Каталог информационной базы.</param>
public sealed record PlatformCheckOutcome(
    PlatformCheckMode Mode,
    bool Clean,
    IReadOnlyList<PlatformCheckProblem> Problems,
    IReadOnlyList<string> Other,
    TimeSpan CreateTime,
    TimeSpan CheckTime,
    string InfobasePath)
{
    /// <summary>Число ошибок.</summary>
    public int ErrorCount => Problems.Count(static problem => problem.Severity == PlatformCheckSeverity.Error);

    /// <summary>Число предупреждений.</summary>
    public int WarningCount => Problems.Count(static problem => problem.Severity == PlatformCheckSeverity.Warning);
}

/// <summary>
/// Штатная проверка выгрузки платформой 1С. Нужна только пустая файловая информационная база:
/// конфигурацию в неё загружать не требуется, платформа читает файлы прямо из каталога
/// (<c>-ConfigDir</c>), а ключ <c>/Out</c> пишет журнал, который разбирает
/// <see cref="PlatformCheckLog"/>.
///
/// Замеры на выгрузке 2,9 ГБ: создание пустой базы 4 с, <c>/CheckModules</c> 4–10 с.
/// Полная загрузка конфигурации в базу (<c>/LoadConfigFromFiles</c>, 844 с) для проверки модулей
/// не нужна — она понадобилась бы только для работы с конфигурацией как с базой.
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

    /// <summary>Проверяет выгрузку; информационная база создаётся пустой, если её ещё нет.</summary>
    public PlatformCheckOutcome Run(
        string dumpPath,
        string infobasePath,
        PlatformCheckMode mode = PlatformCheckMode.Modules,
        TimeSpan? timeout = null)
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
            _log?.Invoke("создаю пустую файловую информационную базу");
            createTime = RunOneC(
                ["CREATEINFOBASE", $"File={infobase};", "/Out", LogPath(infobase, "create"), "/DisableStartupDialogs", "/DisableStartupMessages"],
                TimeSpan.FromMinutes(5));
        }
        else
        {
            _log?.Invoke("информационная база уже есть");
        }

        var check = LogPath(infobase, "check");
        var key = mode == PlatformCheckMode.Config ? "/CheckConfig" : "/CheckModules";
        _log?.Invoke(mode == PlatformCheckMode.Config
            ? "проверяю конфигурацию целиком (это долго)"
            : "проверяю синтаксис модулей");

        var checkTime = RunOneC(
            ["DESIGNER", "/F", infobase, key, "-ConfigDir", dump, "/Out", check, "/DisableStartupDialogs", "/DisableStartupMessages"],
            timeout ?? (mode == PlatformCheckMode.Config ? TimeSpan.FromMinutes(45) : TimeSpan.FromMinutes(10)));

        var parsed = PlatformCheckLog.Parse(ReadLog(check));

        _log?.Invoke(parsed.Clean
            ? "платформа: синтаксических ошибок не обнаружено"
            : $"платформа: ошибок {parsed.ErrorCount}, предупреждений {parsed.WarningCount}");

        return new PlatformCheckOutcome(
            mode,
            parsed.Clean && parsed.Ok,
            parsed.Problems,
            parsed.Other,
            createTime,
            checkTime,
            infobase);
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
