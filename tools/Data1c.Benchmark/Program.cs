using System.Diagnostics;
using System.Globalization;
using Data1c.Core.Analysis;
using Data1c.Core.Dump;
using Data1c.Core.Graph;
using Data1c.Core.Platform;
using Data1c.FileSystem;
using Data1c.Store;
using Microsoft.Data.Sqlite;

// Замеры индекса на реальной выгрузке: сборка, размер, время запросов и частичная переиндексация.
//
//   dotnet tools\Data1c.Benchmark\bin\Debug\net10.0\data1c-benchmark.dll --dump <каталог> [ключи]
//
// Ключи:
//   --index <путь>   куда собирать индекс (по умолчанию <выгрузка>/.data1c/benchmark.db)
//   --build          собрать индекс с нуля (иначе используется готовый)
//   --keep           не удалять собранный индекс после замеров
//   --module <путь>  модуль для замера частичной переиндексации
//   --no-external    не включать внешние узлы в индекс (меньше размер, меньше связей)
//   --platform       подключать справку платформы при разборе
//   --conventions    замерить разбор конвенций: рейтинг модулей и процедур, типовые приёмы
//   --similar <текст> замерить поиск похожего кода (Э2-5) на черновике
//   --plans          показать планы запросов поиска похожего кода (EXPLAIN QUERY PLAN)
//   --read-only      ничего не писать в индекс: пропустить частичную переиндексацию
//
// Нормы (выгрузка УНФ 2,9 ГБ, 65 045 файлов): разбор ~20 с, запись ~80 с, индекс ~3,0 ГБ;
// частичная переиндексация модуля ~1 с; поиск по имени единицы миллисекунд, по подстроке — десятки.

var dump = Value("--dump") ?? @"C:\Users\alexa\RiderProjects\Data1C\1c_files";
var indexPath = Value("--index") ?? Path.Combine(dump, ".data1c", "benchmark.db");
var modulePath = Value("--module");
var build = Has("--build");
var keep = Has("--keep");
var noExternal = Has("--no-external");
var withPlatform = Has("--platform");

Console.WriteLine($"выгрузка: {dump}");
Console.WriteLine($"индекс:   {indexPath}");
Console.WriteLine();

var source = new FileSystemDumpSource(dump);
var files = source.EnumerateFiles().ToList();
Console.WriteLine($"файлов {files.Count.ToString("N0", CultureInfo.InvariantCulture)}, " +
    $"объём {files.Sum(static file => file.Size) / 1024.0 / 1024.0 / 1024.0:F2} ГБ");
Console.WriteLine();

if (!build && !File.Exists(indexPath))
{
    Console.WriteLine($"индекса нет: {indexPath}");
    Console.WriteLine("запустите с ключом --build, чтобы собрать его (на выгрузке 2,9 ГБ это около двух минут)");
    return 2;
}

if (build)
{
    if (File.Exists(indexPath))
    {
        File.Delete(indexPath);
    }

    foreach (var suffix in new[] { "-wal", "-shm" })
    {
        if (File.Exists(indexPath + suffix))
        {
            File.Delete(indexPath + suffix);
        }
    }

    var options = new AnalysisOptions
    {
        PlatformSource = withPlatform ? new FileSystemPlatformSource() : null,
        Graph = new DependencyGraphOptions { IncludeExternalNodes = !noExternal },
    };

    var analysisWatch = Stopwatch.StartNew();
    var analysis = new DumpAnalyzer().Analyze(source, options);
    analysisWatch.Stop();

    using var fresh = SqliteIndex.Open(indexPath);
    var writeWatch = Stopwatch.StartNew();
    var written = new IndexWriter(fresh).Write(source, analysis);
    writeWatch.Stop();

    Console.WriteLine($"разбор выгрузки:            {analysisWatch.Elapsed.TotalSeconds,8:F1} с");
    Console.WriteLine($"запись индекса:              {writeWatch.Elapsed.TotalSeconds,8:F1} с");
    Console.WriteLine($"размер индекса:              {new FileInfo(indexPath).Length / 1024.0 / 1024.0,8:F1} МБ");
    Console.WriteLine($"байт на узел / на связь:     {new FileInfo(indexPath).Length / (double)written.Nodes,8:F0} / " +
        $"{new FileInfo(indexPath).Length / (double)written.Edges:F0}");
    Console.WriteLine();
}

using var index = SqliteIndex.OpenReadOnly(indexPath);
var reader = new IndexReader(index);
var statistics = reader.GetStatistics();
Console.WriteLine($"узлов {statistics.Nodes.ToString("N0", CultureInfo.InvariantCulture)}, " +
    $"связей {statistics.Edges.ToString("N0", CultureInfo.InvariantCulture)}, " +
    $"символов {statistics.Symbols.ToString("N0", CultureInfo.InvariantCulture)}, " +
    $"объектов метаданных {statistics.MetadataObjects.ToString("N0", CultureInfo.InvariantCulture)}");

Console.WriteLine();
Console.WriteLine("запросы:");
Measure("узел по идентификатору", () => reader.GetNode("Catalog.Номенклатура") is null ? 0 : 1);
Measure("поиск по имени (префикс)", () => reader.Search("Номенклатура", 20).Count);
Measure("поиск по подстроке", () => reader.Search("оменклат", 20).Count);
Measure("поиск по подстроке без совпадений", () => reader.Search("нетакогословавовсе", 20).Count);
Measure("символы по имени", () => reader.FindSymbols("ЗагрузитьДанные", 20).Count);
Measure("символы по подстроке", () => reader.FindSymbols("агрузить", 20).Count);
Measure("объекты по имени", () => reader.FindMetadataObjects("Номенклатура", 20).Count);
Measure("объекты по синониму", () => reader.FindMetadataObjects("Незавершенное", 20).Count);
Measure("состав объекта метаданных", () => reader.MetadataChildren("Catalog.Номенклатура", 200).Items.Count);
Measure("карточка узла со связями", () => (reader.Incoming("Catalog.Номенклатура", null, 200).Count + reader.Outgoing("Catalog.Номенклатура", null, 200).Count));
Measure("обход связей (глубина 2)", () => reader.Reach("Catalog.Номенклатура", 2, 200).Count);

if (Has("--conventions"))
{
    Console.WriteLine();
    Console.WriteLine("конвенции (Э2-6):");
    var conventions = reader.ConventionQuery();
    Measure("рейтинг процедур (топ 50)", () => reader.RankRoutines(50).Count);
    Measure("кто вызывает Записать", () => reader.PlatformMatches("Записать", 50).Count);
    Measure("кто вызывает Запрос.Выполнить", () => reader.PlatformMatches("Запрос.Выполнить", 50).Count);
    Measure("кто вызывает Структура.Вставить", () => reader.PlatformMatches("Структура.Вставить", 50).Count);
    Measure("символы топ-50 процедур", () => reader.SymbolsOf([.. reader.RankRoutines(50).Select(static item => item.RoutineId)]).Count);
    Measure("намерение «записать объект»", () => Conventions.Suggest(conventions, "записать объект", null, 8).Routines.Count);
    Measure("намерение «прочитать данные запросом»", () => Conventions.Suggest(conventions, "прочитать данные запросом", null, 8).Routines.Count);
    Measure("намерение «вывести сообщение»", () => Conventions.Suggest(conventions, "вывести сообщение пользователю", null, 8).Routines.Count);
    Measure("намерение «найти по наименованию»", () => Conventions.Suggest(conventions, "найти по наименованию", null, 8).Routines.Count);

    var answer = Conventions.Suggest(conventions, "записать объект", null, 5);
    Console.WriteLine();
    Console.WriteLine($"  приём: {answer.Intent}");
    foreach (var routine in answer.Routines)
    {
        Console.WriteLine($"    {routine.Name,-40} {routine.ModulePath}  вызовов {routine.Uses}, пример {routine.ExampleLine}");
    }

    var method = Conventions.Suggest(conventions, null, "Записать", 5);
    Console.WriteLine($"  метод «Записать»: процедур {method.Routines.Count}, примеров вызова {method.CallSites.Count}");
    foreach (var call in method.CallSites.Take(5))
    {
        Console.WriteLine($"    {call.ModulePath}:{call.Line}  {call.Detail}");
    }
}

if (withPlatform)
{
    var platformWatch = Stopwatch.StartNew();
    var platform = new PlatformHelpIndex(new FileSystemPlatformSource());
    var topics = platform.TopicCount;
    platformWatch.Stop();
    Console.WriteLine($"  {"справка платформы (загрузка)",-38}{platformWatch.Elapsed.TotalMilliseconds,8:F0} мс   тем {topics:N0}");
}

if (Value("--similar") is { } draft)
{
    Console.WriteLine();
    Console.WriteLine("поиск похожего кода (Э2-5):");
    var similarSource = reader.SimilarCodeSource();
    SimilarCodeFeatures? features = null;
    var parseBest = double.MaxValue;
    var resolveBest = double.MaxValue;
    for (var attempt = 0; attempt < 3; attempt++)
    {
        var plainWatch = Stopwatch.StartNew();
        _ = SimilarCode.Describe(draft);
        plainWatch.Stop();
        parseBest = Math.Min(parseBest, plainWatch.Elapsed.TotalMilliseconds);

        var resolveWatch = Stopwatch.StartNew();
        features = SimilarCode.Describe(draft, null, similarSource.ResolveCall);
        resolveWatch.Stop();
        resolveBest = Math.Min(resolveBest, resolveWatch.Elapsed.TotalMilliseconds);
    }

    Console.WriteLine($"  признаки черновика:            платформа {features!.PlatformCalls.Count}, "
        + $"метаданные {features.MetadataReferences.Count}, вызовы процедур {features.RoutineCalls.Count}, "
        + $"неразрешённые вызовы {features.UnresolvedCalls.Count}");
    Console.WriteLine($"  разбор черновика:            {parseBest,8:F0} мс");
    Console.WriteLine($"  разбор и разрешение вызовов: {resolveBest,8:F0} мс");

    var engine = new SimilarCode(similarSource);
    SimilarCodeResult? answer = null;
    var bestWatch = double.MaxValue;
    for (var attempt = 0; attempt < 3; attempt++)
    {
        var watch = Stopwatch.StartNew();
        answer = engine.Find(features, 10);
        watch.Stop();
        bestWatch = Math.Min(bestWatch, watch.Elapsed.TotalMilliseconds);
    }

    Console.WriteLine($"  ответ similar (предел 10):   {bestWatch,8:F0} мс   "
        + $"кандидатов {answer!.Candidates.Count} из {answer.Considered}");
    foreach (var candidate in answer.Candidates.Take(3))
    {
        Console.WriteLine($"    {candidate.Score:0.000}  {candidate.Name,-40} {candidate.ModulePath}");
    }
}

if (Has("--plans"))
{
    Console.WriteLine();
    Console.WriteLine("планы запросов поиска похожего кода (копии запросов из IndexSimilarCodeSource):");
    using var planConnection = new SqliteConnection(new SqliteConnectionStringBuilder
    {
        DataSource = indexPath,
        Mode = SqliteOpenMode.ReadOnly,
        Pooling = false,
    }.ToString());
    planConnection.Open();
    Plan(
        planConnection,
        "вызовы процедур по цели (стало)",
        "SELECT source_id FROM edges WHERE kind = 'Calls' AND target_id = @value AND source_id LIKE 'routine:%' LIMIT @cap",
        ("@value", "routine:module:CommonModules/Общий/Ext/Module.bsl#ОбработатьТовар"),
        ("@cap", 10000L));
    Plan(
        planConnection,
        "вызовы процедур по тексту (было)",
        "SELECT source_id FROM edges WHERE kind = 'Calls' AND detail = @value AND source_id LIKE 'routine:%' LIMIT @cap",
        ("@value", "Общий.ОбработатьТовар"),
        ("@cap", 10000L));
    Plan(
        planConnection,
        "разрешение вызова: общий модуль",
        "SELECT node_id FROM symbols WHERE owner_id = @owner AND name_lower = @method LIMIT 1",
        ("@owner", "CommonModule.Общий"),
        ("@method", "обработатьтовар"));
    Plan(
        planConnection,
        "разрешение вызова: модуль черновика",
        "SELECT node_id FROM symbols WHERE module_path = @module AND name_lower = @method LIMIT 1",
        ("@module", "CommonModules/Общий/Ext/Module.bsl"),
        ("@method", "обработатьтовар"));
    Plan(
        planConnection,
        "разрешение вызова: единственное имя",
        "SELECT node_id FROM symbols WHERE name_lower = @method LIMIT 2",
        ("@method", "обработатьтовар"));
}

// Частичная переиндексация: правится первый модуль выгрузки, если не задан явно.
var module = modulePath ?? files.FirstOrDefault(static file => file.RelativePath.EndsWith(".bsl", StringComparison.OrdinalIgnoreCase))?.RelativePath;
if (module is not null && !Has("--read-only"))
{
    Console.WriteLine();
    using var writable = SqliteIndex.Open(indexPath);
    var writer = new IndexWriter(writable);
    var partialWatch = Stopwatch.StartNew();
    var written = writer.WriteModuleFiles(source, [module]);
    partialWatch.Stop();
    Console.WriteLine(written is null
        ? $"частичная переиндексация модуля: недоступна ({module} неизвестен индексу)"
        : $"частичная переиндексация модуля: {partialWatch.Elapsed.TotalSeconds,6:F2} с   {module} " +
          $"(узлов {written.Nodes}, связей {written.Edges}, символов {written.Symbols})");
}

if (build)
{
    Console.WriteLine();
    Console.WriteLine(keep
        ? "индекс оставлен по ключу --keep: " + indexPath
        : "индекс оставлен для повторных замеров (удалите вручную или запускайте с --build): " + indexPath);
}

return 0;

string? Value(string name)
{
    var position = Array.IndexOf(args, name);
    return position >= 0 && position + 1 < args.Length ? args[position + 1] : null;
}

bool Has(string name) => Array.IndexOf(args, name) >= 0;

void Measure(string title, Func<int> action, int repeat = 3)
{
    var best = double.MaxValue;
    var count = 0;
    for (var attempt = 0; attempt < repeat; attempt++)
    {
        var watch = Stopwatch.StartNew();
        count = action();
        watch.Stop();
        best = Math.Min(best, watch.Elapsed.TotalMilliseconds);
    }

    Console.WriteLine($"  {title,-38}{best,8:F0} мс   строк {count:N0}");
}

void Plan(SqliteConnection connection, string title, string sql, params (string Name, object Value)[] parameters)
{
    using var command = connection.CreateCommand();
    command.CommandText = "EXPLAIN QUERY PLAN " + sql;
    foreach (var (name, value) in parameters)
    {
        command.Parameters.AddWithValue(name, value);
    }

    using var reader = command.ExecuteReader();
    var steps = new List<string>();
    while (reader.Read())
    {
        steps.Add(reader.GetString(3));
    }

    Console.WriteLine($"  {title,-38} {string.Join(" → ", steps)}");
}
