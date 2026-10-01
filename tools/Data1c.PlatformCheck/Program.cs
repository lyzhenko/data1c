using System.Text.Json;
using Data1c.Core.Platform;

// Штатная проверка выгрузки платформой 1С: создать файловую базу, загрузить конфигурацию,
// запустить /CheckModules. Это финальный фильтр перед выгрузкой конфигурации обратно в 1С.
//
//   dotnet tools\Data1c.PlatformCheck\bin\Debug\net10.0\data1c-platform-check.dll --dump <каталог> [ключи]
//
// Ключи:
//   --dump <путь>       выгрузка конфигурации в файлы (обязательно)
//   --infobase <путь>   каталог информационной базы (по умолчанию <выгрузка>/.data1c/platform-base)
//   --one-c <путь>      путь к 1cv8.exe, если платформа не находится автоматически
//   --reuse             не загружать конфигурацию, если база новее выгрузки (экономит минуты)
//   --json              напечатать итог в JSON
//   --clean             удалить каталог базы после проверки
//   --timeout-load <мин>, --timeout-check <мин>   пределы ожидания платформы
//
// Коды возврата: 0 — ошибок нет, 1 — есть ошибки, 2 — проверку не удалось выполнить.
//
// Замеры на выгрузке 2,9 ГБ: создание базы 4 с, загрузка конфигурации 844 с, проверка модулей 10 с.
// Пути к выгрузке и базе не должны содержать пробелов — такова особенность строки соединения 1С.

var dump = Value("--dump");
if (dump is null)
{
    Console.Error.WriteLine("укажите выгрузку: --dump <каталог>");
    return 2;
}

var infobase = Value("--infobase") ?? Path.Combine(dump, ".data1c", "platform-base");
var json = Has("--json");
var reuse = Has("--reuse");
var clean = Has("--clean");
var loadTimeout = Minutes("--timeout-load", 45);
var checkTimeout = Minutes("--timeout-check", 15);

Console.WriteLine($"выгрузка: {Path.GetFullPath(dump)}");
Console.WriteLine($"база:     {Path.GetFullPath(infobase)}");
if (!json)
{
    Console.WriteLine("загрузка конфигурации на большой выгрузке занимает минуты — это нормально");
}

Console.WriteLine();

try
{
    var runner = new PlatformCheckRunner(Value("--one-c"), message => Console.WriteLine("  " + message));
    var outcome = runner.Run(dump, infobase, reuse, loadTimeout, checkTimeout);

    Console.WriteLine();
    Console.WriteLine($"создание базы:      {outcome.CreateTime.TotalSeconds,8:F1} с");
    Console.WriteLine(outcome.LoadSkipped
        ? "загрузка конфигурации: пропущена (база новее выгрузки)"
        : $"загрузка конфигурации: {outcome.LoadTime!.Value.TotalSeconds,6:F1} с");
    Console.WriteLine($"проверка модулей:   {outcome.CheckTime.TotalSeconds,8:F1} с");
    Console.WriteLine();

    if (json)
    {
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            clean = outcome.Clean,
            errors = outcome.ErrorCount,
            warnings = outcome.WarningCount,
            problems = outcome.Problems.Select(problem => new
            {
                file = problem.FilePath,
                line = problem.Line,
                severity = problem.Severity.ToString(),
                message = problem.Message,
            }),
            other = outcome.Other,
            loadWarnings = outcome.LoadProblems.Count,
            infobase = outcome.InfobasePath,
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
    else
    {
        if (outcome.Clean && outcome.ErrorCount == 0)
        {
            Console.WriteLine("Синтаксических ошибок не обнаружено.");
        }
        else
        {
            Console.WriteLine($"Ошибок: {outcome.ErrorCount}, предупреждений: {outcome.WarningCount}");
            foreach (var problem in outcome.Problems)
            {
                var where = problem.FilePath is null ? "(без файла)" : problem.FilePath;
                var line = problem.Line is { } number ? $":{number}" : string.Empty;
                Console.WriteLine($"  {problem.Severity,-8} {where}{line}");
                Console.WriteLine($"           {problem.Message}");
            }
        }

        if (outcome.Other.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"строк журнала, не отнесённых к замечаниям: {outcome.Other.Count}");
            foreach (var line in outcome.Other.Take(10))
            {
                Console.WriteLine("  " + line);
            }
        }

        if (outcome.LoadProblems.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"замечания загрузки конфигурации (обычно ссылки в справке): {outcome.LoadProblems.Count}");
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

TimeSpan? Minutes(string name, int fallback)
{
    var text = Value(name);
    return TimeSpan.FromMinutes(text is not null && int.TryParse(text, out var minutes) ? minutes : fallback);
}
