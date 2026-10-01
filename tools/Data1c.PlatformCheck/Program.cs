using System.Text.Json;
using Data1c.Core.Platform;

// Штатная проверка выгрузки платформой 1С — финальный фильтр перед выгрузкой конфигурации обратно в 1С.
//
//   dotnet tools\Data1c.PlatformCheck\bin\Debug\net10.0\data1c-platform-check.dll --dump <каталог> [ключи]
//
// Ключи:
//   --dump <путь>       выгрузка конфигурации в файлы (обязательно)
//   --infobase <путь>   каталог информационной базы (по умолчанию <выгрузка>/.data1c/platform-base)
//   --one-c <путь>      путь к 1cv8.exe, если платформа не находится автоматически
//   --modules           быстрая проверка модулей (/CheckModules): секунды, но ошибок в коде НЕ находит
//   --config            проверка конфигурации (/CheckConfig) — по умолчанию; 5–10 минут, ловит ошибки
//   --prepare           только создать базу и загрузить конфигурацию (дорогая часть), проверку не запускать
//   --check-only        проверить уже подготовленную базу и отказаться, если она старше выгрузки
//   --reuse             не перечитывать конфигурацию в базу, даже если выгрузка новее (итог помечается)
//   --json              напечатать итог в JSON
//   --clean             удалить каталог базы после проверки
//   --timeout <мин>     предел ожидания проверки (по умолчанию 45)
//   --load-timeout <мин> предел ожидания загрузки конфигурации (по умолчанию 60)
//
// Коды возврата: 0 — ошибок нет, 1 — есть ошибки, 2 — проверку не удалось выполнить.
//
// Замеры на выгрузке 2,9 ГБ: создание базы 4 с, загрузка конфигурации 844 с, проверка 430 с.
// Проверка модулей синтаксических ошибок не находит — это проверено внесением заведомой ошибки.
// Дешёвого пути нет и это проверено отдельно: `CheckConfig -ConfigDir <файлы>` ошибок в коде
// не находит (484 с, та же картина, что у загруженной конфигурации), а частичная загрузка
// `LoadConfigFromFiles -ListFile <файл>` отклоняется платформой («редактирование объекта
// метаданных запрещено»). Поэтому правку модулей проверяет офлайн-`check`, а платформа —
// финальный фильтр: `--prepare` заранее, `--check-only` после него.

var dump = Value("--dump");
if (dump is null)
{
    Console.Error.WriteLine("укажите выгрузку: --dump <каталог>");
    return 2;
}

var infobase = Value("--infobase") ?? Path.Combine(dump, ".data1c", "platform-base");
var mode = Has("--modules") ? PlatformCheckMode.Modules : PlatformCheckMode.Config;
var json = Has("--json");
var clean = Has("--clean");
var reuse = Has("--reuse");
var timeout = TimeSpan.FromMinutes(Number("--timeout", 45));
var loadTimeout = TimeSpan.FromMinutes(Number("--load-timeout", 60));

Console.WriteLine($"выгрузка: {Path.GetFullPath(dump)}");
Console.WriteLine($"база:     {Path.GetFullPath(infobase)}");
Console.WriteLine($"проверка: {(mode == PlatformCheckMode.Modules ? "модули (быстрая, ошибок в коде не находит)" : "конфигурация")}");
Console.WriteLine();

try
{
    var runner = new PlatformCheckRunner(Value("--one-c"), message => Console.WriteLine("  " + message));

    if (Has("--prepare"))
    {
        var prepared = runner.Prepare(dump, infobase, loadTimeout);
        Console.WriteLine();
        Console.WriteLine($"создание базы:          {prepared.Create.TotalSeconds,8:F1} с");
        Console.WriteLine($"загрузка конфигурации:  {prepared.Load.TotalSeconds,8:F1} с");
        Console.WriteLine();
        Console.WriteLine("база подготовлена: проверку можно запустить ключом --check-only");
        return 0;
    }

    var outcome = runner.Run(dump, infobase, mode, reuse, timeout, loadTimeout, Has("--check-only"));

    Console.WriteLine();
    Console.WriteLine($"создание базы:          {outcome.CreateTime.TotalSeconds,8:F1} с");
    Console.WriteLine(outcome.LoadTime is { } load
        ? $"загрузка конфигурации:  {load.TotalSeconds,8:F1} с"
        : "загрузка конфигурации:  пропущена");
    Console.WriteLine($"проверка:               {outcome.CheckTime.TotalSeconds,8:F1} с");
    if (outcome.StaleBase && outcome.LoadSkipped)
    {
        Console.WriteLine("ВНИМАНИЕ: база старше выгрузки — проверялась прежняя конфигурация");
    }

    Console.WriteLine();

    if (json)
    {
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            mode = outcome.Mode.ToString(),
            clean = outcome.Clean,
            errors = outcome.ErrorCount,
            warnings = outcome.WarningCount,
            staleBase = outcome.StaleBase,
            loadSeconds = outcome.LoadTime is { } value ? Math.Round(value.TotalSeconds, 1) : (double?)null,
            checkSeconds = Math.Round(outcome.CheckTime.TotalSeconds, 1),
            problems = outcome.Problems.Select(problem => new
            {
                file = problem.FilePath,
                place = problem.Place,
                line = problem.Line,
                severity = problem.Severity.ToString(),
                message = problem.Message,
                snippet = problem.Snippet,
            }),
            other = outcome.Other,
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
    else if (outcome.ErrorCount == 0)
    {
        Console.WriteLine(outcome.Clean
            ? "Ошибок не обнаружено."
            : $"Ошибок нет, предупреждений: {outcome.WarningCount}");
    }
    else
    {
        Console.WriteLine($"Ошибок: {outcome.ErrorCount}, предупреждений: {outcome.WarningCount}");
        foreach (var problem in outcome.Problems)
        {
            var where = problem.FilePath ?? problem.Place ?? "(без места)";
            var line = problem.Line is { } number ? $":{number}" : string.Empty;
            Console.WriteLine($"  {problem.Severity,-8} {where}{line}");
            Console.WriteLine($"           {problem.Message}");
            if (problem.Snippet is { Length: > 0 } snippet)
            {
                Console.WriteLine($"           {snippet}");
            }
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

int Number(string name, int fallback) =>
    int.TryParse(Value(name), out var value) ? value : fallback;
