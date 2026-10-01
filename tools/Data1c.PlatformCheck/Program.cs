using System.Text.Json;
using Data1c.Core.Platform;

// Штатная проверка выгрузки платформой 1С — финальный фильтр перед выгрузкой конфигурации обратно в 1С.
//
//   dotnet tools\Data1c.PlatformCheck\bin\Debug\net10.0\data1c-platform-check.dll --dump <каталог> [ключи]
//
// Ключи:
//   --dump <путь>       выгрузка конфигурации в файлы (обязательно)
//   --infobase <путь>   каталог пустой информационной базы (по умолчанию <выгрузка>/.data1c/platform-base)
//   --one-c <путь>      путь к 1cv8.exe, если платформа не находится автоматически
//   --config            проверять конфигурацию целиком (/CheckConfig) — дольше и строже
//   --json              напечатать итог в JSON
//   --clean             удалить каталог базы после проверки
//   --timeout <мин>     предел ожидания платформы (по умолчанию 10, для --config 45)
//
// Коды возврата: 0 — ошибок нет, 1 — есть ошибки, 2 — проверку не удалось выполнить.
//
// Конфигурацию в базу загружать не нужно: платформа читает файлы прямо из каталога (-ConfigDir).
// Замеры на выгрузке 2,9 ГБ: создание пустой базы 4 с, проверка модулей 4–10 с.

var dump = Value("--dump");
if (dump is null)
{
    Console.Error.WriteLine("укажите выгрузку: --dump <каталог>");
    return 2;
}

var infobase = Value("--infobase") ?? Path.Combine(dump, ".data1c", "platform-base");
var mode = Has("--config") ? PlatformCheckMode.Config : PlatformCheckMode.Modules;
var json = Has("--json");
var clean = Has("--clean");
var timeout = TimeSpan.FromMinutes(int.TryParse(Value("--timeout"), out var minutes)
    ? minutes
    : mode == PlatformCheckMode.Config ? 45 : 10);

Console.WriteLine($"выгрузка: {Path.GetFullPath(dump)}");
Console.WriteLine($"база:     {Path.GetFullPath(infobase)}");
Console.WriteLine($"проверка: {(mode == PlatformCheckMode.Config ? "вся конфигурация" : "синтаксис модулей")}");
Console.WriteLine();

try
{
    var runner = new PlatformCheckRunner(Value("--one-c"), message => Console.WriteLine("  " + message));
    var outcome = runner.Run(dump, infobase, mode, timeout);

    Console.WriteLine();
    Console.WriteLine($"создание базы:  {outcome.CreateTime.TotalSeconds,8:F1} с");
    Console.WriteLine($"проверка:       {outcome.CheckTime.TotalSeconds,8:F1} с");
    Console.WriteLine();

    if (json)
    {
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            mode = outcome.Mode.ToString(),
            clean = outcome.Clean,
            errors = outcome.ErrorCount,
            warnings = outcome.WarningCount,
            createSeconds = Math.Round(outcome.CreateTime.TotalSeconds, 1),
            checkSeconds = Math.Round(outcome.CheckTime.TotalSeconds, 1),
            problems = outcome.Problems.Select(problem => new
            {
                file = problem.FilePath,
                line = problem.Line,
                severity = problem.Severity.ToString(),
                message = problem.Message,
            }),
            other = outcome.Other,
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
    else if (outcome.ErrorCount == 0)
    {
        Console.WriteLine(outcome.Clean
            ? "Синтаксических ошибок не обнаружено."
            : $"Ошибок нет, предупреждений: {outcome.WarningCount}");
    }
    else
    {
        Console.WriteLine($"Ошибок: {outcome.ErrorCount}, предупреждений: {outcome.WarningCount}");
        foreach (var problem in outcome.Problems)
        {
            var where = problem.FilePath ?? "(без файла)";
            var line = problem.Line is { } number ? $":{number}" : string.Empty;
            Console.WriteLine($"  {problem.Severity,-8} {where}{line}");
            Console.WriteLine($"           {problem.Message}");
        }
    }

    if (outcome.Other.Count > 0)
    {
        Console.WriteLine();
        Console.WriteLine($"строк журнала, не отнесённых к замечаниям: {outcome.Other.Count}");
        foreach (var line in outcome.Other.Take(15))
        {
            Console.WriteLine("  " + line);
        }

        if (outcome.Other.Count > 15)
        {
            Console.WriteLine($"  … ещё {outcome.Other.Count - 15}");
        }
    }

    if (clean && Directory.Exists(infobase))
    {
        Directory.Delete(infobase, recursive: true);
        Console.WriteLine();
        Console.WriteLine("каталог базы удалён по ключу --clean");
    }

    return outcome.ErrorCount > 0 ? 1 : 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine("проверка не выполнена: " + exception.Message);
    return 2;
}

string? Value(string name)
{
    var position = Array.IndexOf(args, name);
    return position >= 0 && position + 1 < args.Length ? args[position + 1] : null;
}

bool Has(string name) => Array.IndexOf(args, name) >= 0;
