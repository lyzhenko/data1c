using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using Data1c.Core.Analysis;
using Data1c.Core.Dump;
using Data1c.Core.Graph;
using Data1c.Core.Platform;
using Data1c.FileSystem;

namespace Data1c.Cli;

internal static class Program
{
    private static int Main(string[] args)
    {
        TryEnableUtf8();

        try
        {
            if (args.Length == 0 || IsHelp(args[0]))
            {
                PrintHelp();
                return args.Length == 0 ? 1 : 0;
            }

            var command = args[0].ToLowerInvariant();
            var options = CliOptions.Parse(args.Skip(1));

            return command switch
            {
                "scan" => RunScan(options),
                "stats" => RunStats(options),
                "view" => RunView(options),
                "platform" => RunPlatform(options),
                _ => Fail($"Неизвестная команда «{args[0]}». Запустите: data1c help"),
            };
        }
        catch (Exception ex)
        {
            return Fail(ex.Message);
        }
    }

    private static int RunScan(CliOptions options)
    {
        var (source, analyzer, progress) = Prepare(options);
        var result = Analyze(analyzer, source, options, progress);

        var format = options.Format.ToLowerInvariant();
        var output = options.Output ?? (format == "dot" ? "graph.dot" : "graph.json");

        using (var stream = File.Create(output))
        {
            if (format == "dot")
            {
                using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                GraphDotWriter.Write(writer, result.Graph, new GraphDotOptions { MaxNodes = options.DotMaxNodes });
            }
            else
            {
                GraphJsonWriter.Write(stream, result.Graph);
            }
        }

        PrintSummary(result);
        WriteLine($"Граф записан: {Path.GetFullPath(output)} ({new FileInfo(output).Length / 1024.0 / 1024.0:F1} МБ)");
        return 0;
    }

    private static int RunStats(CliOptions options)
    {
        var (source, analyzer, progress) = Prepare(options);
        var result = Analyze(analyzer, source, options, progress);
        PrintSummary(result);
        return 0;
    }

    /// <summary>
    /// Показывает модель платформы из установленной 1С: найденные версии и справочные файлы,
    /// поиск по синтакс-помощнику или тему целиком.
    /// </summary>
    private static int RunPlatform(CliOptions options)
    {
        Version? preferred = null;
        if (options.PlatformVersion is { } versionText)
        {
            if (!Version.TryParse(versionText, out var parsed))
            {
                return Fail($"Не разобран номер версии «{versionText}»: ожидается вид 8.3.27.2214.");
            }

            preferred = parsed;
        }

        var source = new FileSystemPlatformSource();
        var index = new PlatformHelpIndex(source, new PlatformHelpOptions { PreferredVersion = preferred });

        WriteLine($"Источник:   {index.DisplayName}");

        // Позиционный аргумент команды — имя темы или поисковый запрос.
        var topicName = options.PlatformTopic;
        var query = options.PlatformSearch;
        if (topicName is null && query is null && options.Path is { } argument)
        {
            topicName = argument;
            query = argument;
        }

        if (topicName is { } name)
        {
            var topic = index.Find(name) ?? index.Search(name, 1).FirstOrDefault();
            if (topic is null && query is null)
            {
                return Fail($"Тема «{name}» не найдена в справке платформы.");
            }

            if (topic is not null && options.PlatformSearch is null)
            {
                WriteLine($"Тема:       {topic.Title}");
                WriteLine($"Путь темы:  {topic.Name}");
                WriteLine($"Версия:     {topic.Version} ({topic.Kind})");
                WriteLine($"Файл:       {topic.Path}");
                WriteLine(string.Empty);
                WriteLine(topic.Text);
                return 0;
            }
        }

        if (query is { } text)
        {
            var results = index.Search(text, Math.Max(1, options.Top));
            WriteLine($"Платформа:  {Describe(index.Version)}");
            WriteLine($"Тем:        {N(index.TopicCount)}");
            WriteLine($"Поиск:      «{text}» — найдено {N(results.Count)}");
            WriteLine(string.Empty);
            foreach (var topic in results)
            {
                WriteLine(topic.Title);
                WriteLine($"    тема: {topic.Name}");
                WriteLine("    " + (topic.Text.Length > 240 ? topic.Text[..240] + "…" : topic.Text));
            }

            return results.Count > 0 ? 0 : 1;
        }

        WriteLine($"Платформа:  {Describe(index.Version)}");
        WriteLine($"Тем:        {N(index.TopicCount)}");
        if (index.TopicCount > 0)
        {
            WriteLine($"Файлы:      {string.Join(", ", index.Installation!.Files.Select(static f => $"{Path.GetFileName(f.RelativePath)} ({f.Kind})"))}");
        }

        WriteLine(string.Empty);
        WriteLine("Установки:");
        foreach (var installation in index.Installations)
        {
            WriteLine($"  {installation.Version}  {installation.RootPath}");
            foreach (var file in installation.Files)
            {
                WriteLine($"    {file.RelativePath} — {file.Size / 1024.0 / 1024.0:F1} МБ");
            }
        }

        foreach (var warning in index.Warnings)
        {
            WriteLine("Предупреждение: " + warning);
        }

        return index.IsAvailable ? 0 : 1;
    }

    private static string Describe(Version? version) => version?.ToString() ?? "не найдена";

    /// <summary>
    /// Запускает интерактивный просмотрщик графа: по умолчанию — локальный сервер с поиском
    /// и раскрытием соседей, с ключом <c>--static</c> — самодостаточная страница с подграфом.
    /// </summary>
    private static int RunView(CliOptions options)
    {
        var (source, analyzer, progress) = Prepare(options);
        var result = Analyze(analyzer, source, options, progress);
        var query = new GraphQueryService(result.Graph);
        PrintSummary(result);

        if (options.Static)
        {
            return WriteStaticPage(query, result, options);
        }

        using var server = StartViewerServer(query, options, source);
        WriteLine(string.Empty);
        WriteLine($"Просмотрщик запущен: {server.Url}");
        WriteLine($"Процесс {Environment.ProcessId}. Остановить — Ctrl+C или: Get-Process data1c | Stop-Process");

        if (IsRunningFromBuildOutput())
        {
            WriteLine(string.Empty);
            WriteLine("ВНИМАНИЕ: просмотрщик запущен прямо из каталога сборки (bin). Пока он работает,");
            WriteLine("dotnet build и Rider не смогут перезаписать эти файлы. Для длительной работы");
            WriteLine(@"запускайте через tools\view.ps1 — он публикует сборку в artifacts\viewer.");
        }

        if (options.Open)
        {
            OpenBrowser(server.Url);
        }

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        server.RunAsync(cancellation.Token).GetAwaiter().GetResult();
        WriteLine("Просмотрщик остановлен.");
        return 0;
    }

    private static int WriteStaticPage(GraphQueryService query, AnalysisResult result, CliOptions options)
    {
        var maxNodes = Math.Max(1, options.MaxNodes);
        GraphNeighborhood subgraph;
        string? centreId = null;

        if (options.Focus is { Length: > 0 } reference)
        {
            var node = query.Resolve(reference, 5).FirstOrDefault()
                ?? throw new InvalidOperationException($"Узел «{reference}» не найден. Уточните имя: data1c view ... --focus <идентификатор>");
            subgraph = query.GetNeighborhood(new GraphNeighborhoodRequest
            {
                NodeId = node.Id,
                Depth = Math.Clamp(options.Depth, 1, 6),
                MaxNodes = maxNodes,
                // Внешние узлы — заглушки вызовов, у которых нет объекта в выгрузке:
                // в статичной странице они только занимают лимит.
                NodeKinds = [GraphNodeKind.Configuration, GraphNodeKind.MetadataObject, GraphNodeKind.Module, GraphNodeKind.Routine],
            });
            centreId = node.Id;
            WriteLine($"Центр подграфа: {node.Id}");
        }
        else if (result.Graph.Nodes.Count <= maxNodes)
        {
            subgraph = new GraphNeighborhood(result.Graph.Nodes, result.Graph.Edges, false);
        }
        else
        {
            throw new InvalidOperationException(
                $"В графе {result.Graph.Nodes.Count:N0} узлов — для статичной страницы укажите --focus <узел> [--depth N] [--max-nodes N].");
        }

        // Центром раскладки берём узел с наибольшим числом связей: он даёт самую понятную картину.
        if (centreId is null)
        {
            var degrees = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var edge in subgraph.Edges)
            {
                degrees[edge.SourceId] = degrees.GetValueOrDefault(edge.SourceId) + 1;
                degrees[edge.TargetId] = degrees.GetValueOrDefault(edge.TargetId) + 1;
            }

            centreId = subgraph.Nodes
                .OrderByDescending(node => degrees.GetValueOrDefault(node.Id))
                .Select(static node => node.Id)
                .FirstOrDefault();
        }

        var payload = new
        {
            mode = "static",
            center = centreId,
            nodes = subgraph.Nodes,
            edges = subgraph.Edges,
            truncated = subgraph.Truncated,
            stats = new { totalNodes = result.Graph.Nodes.Count, totalEdges = result.Graph.Edges.Count },
        };

        var json = Encoding.UTF8.GetString(ViewerAssets.Serialize(payload));
        var output = options.Output ?? "graph.html";
        File.WriteAllText(output, ViewerAssets.GetPage(json), new UTF8Encoding(false));

        WriteLine(string.Empty);
        WriteLine($"Узлов в подграфе: {subgraph.Nodes.Count:N0}, связей: {subgraph.Edges.Count:N0}{(subgraph.Truncated ? " (обрезано лимитом)" : string.Empty)}");
        WriteLine($"Страница записана: {Path.GetFullPath(output)} ({new FileInfo(output).Length / 1024.0:F0} КБ)");

        if (options.Open)
        {
            OpenBrowser(Path.GetFullPath(output));
        }

        return 0;
    }

    /// <summary>
    /// Поднимает сервер просмотрщика. Если заданный порт занят или зарезервирован системой
    /// (типичная причина — исключённые диапазоны Hyper-V/WSL), берётся любой свободный порт.
    /// </summary>
    private static ViewerServer StartViewerServer(GraphQueryService query, CliOptions options, IDumpSource source)
    {
        var html = ViewerAssets.GetPage(null);
        var code = new SourceCodeReader(source);
        try
        {
            return new ViewerServer(query, html, options.Port, code);
        }
        catch (SocketException exception) when (options.Port != 0)
        {
            Console.Error.WriteLine($"Порт {options.Port} недоступен: {exception.Message}");
            Console.Error.WriteLine("Обычно порт занят или зарезервирован системой (Hyper-V, WSL, Docker).");
            Console.Error.WriteLine("Посмотреть зарезервированные диапазоны: netsh interface ipv4 show excludedportrange protocol=tcp");

            var fallback = new ViewerServer(query, html, 0, code);
            Console.Error.WriteLine($"Взят свободный порт {fallback.Port}.");
            return fallback;
        }
    }

    /// <summary>
    /// Признак того, что просмотрщик запущен из каталога сборки: такой процесс блокирует
    /// пересборку проекта, поэтому о нём стоит предупредить.
    /// </summary>
    private static bool IsRunningFromBuildOutput()
    {
        var directory = AppContext.BaseDirectory;
        return directory.Contains(@"\bin\", StringComparison.OrdinalIgnoreCase)
            || directory.Contains("/bin/", StringComparison.OrdinalIgnoreCase);
    }

    private static void OpenBrowser(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Не удалось открыть браузер: " + exception.Message);
        }
    }

    private static (FileSystemDumpSource Source, DumpAnalyzer Analyzer, IProgress<AnalysisProgress>? Progress) Prepare(CliOptions options)
    {
        if (options.Path is null)
        {
            throw new InvalidOperationException("Не указан путь к каталогу выгрузки. Пример: data1c stats C:\\dump");
        }

        var source = new FileSystemDumpSource(options.Path);
        var analyzer = new DumpAnalyzer();
        return (source, analyzer, options.Quiet ? null : new Progress<AnalysisProgress>(ReportProgress));
    }

    private static AnalysisResult Analyze(
        DumpAnalyzer analyzer,
        FileSystemDumpSource source,
        CliOptions options,
        IProgress<AnalysisProgress>? progress)
    {
        var analysisOptions = new AnalysisOptions
        {
            IncludeBsl = options.IncludeBsl,
            PlatformSource = options.Platform ? new FileSystemPlatformSource() : null,
            MaxDegreeOfParallelism = options.MaxDop,
            Progress = progress,
            Metadata = new Data1c.Core.Metadata.MetadataReadOptions
            {
                IncludeRoleRights = options.RoleRights,
                Sections = options.Sections,
                AttachModules = options.IncludeBsl,
            },
            Graph = new DependencyGraphOptions
            {
                IncludeContainment = true,
                IncludeMetadataReferences = options.IncludeMetadataRefs,
                IncludeModules = options.IncludeBsl,
                IncludeRoutines = options.IncludeRoutines,
                IncludeCalls = options.IncludeCalls,
                IncludeMetadataAccess = options.IncludeMetadataAccess,
                IncludeExternalNodes = options.IncludeExternal,
                IncludeNestedObjects = options.IncludeNested,
            },
        };

        if (!options.Quiet)
        {
            WriteLine($"Разбор выгрузки: {source.DisplayName}");
        }

        var result = analyzer.Analyze(source, analysisOptions);
        ClearProgress();
        return result;
    }

    private static void PrintSummary(AnalysisResult result)
    {
        var stats = result.Statistics;
        var graph = result.Graph.Statistics;

        WriteLine(string.Empty);
        WriteLine($"Источник:      {result.SourceName}");
        WriteLine($"Метаданные:    {N(stats.MetadataObjects)} объектов, из них верхнего уровня {N(stats.TopLevelObjects)}");
        WriteLine($"XML-файлы:     разобрано {N(stats.ParsedXmlFiles)}, ошибок {N(stats.FailedXmlFiles)}");
        WriteLine($"Модули BSL:    {N(stats.Modules)} из {N(stats.ModuleFiles)} файлов ({stats.ModuleBytes / 1024.0 / 1024.0:F1} МБ)");
        WriteLine($"Процедуры:     {N(stats.Procedures)} процедур, {N(stats.Functions)} функций, всего {N(stats.Routines)}");
        WriteLine($"Граф:          {N(graph.NodeCount)} узлов, {N(graph.EdgeCount)} связей");
        WriteLine($"Внешние цели:  {N(graph.ExternalNodeCount)} узлов, {N(graph.UnresolvedEdgeCount)} связей без объекта в выгрузке");
        if (result.Platform is { } platform)
        {
            var platformNodes = graph.NodesByKind.GetValueOrDefault("Platform");
            WriteLine($"Платформа:     {Describe(platform.Version)}, тем {N(platform.TopicCount)}, узлов платформы {N(platformNodes)}");
        }

        WriteLine(string.Empty);
        WriteLine("Узлы по типам: " + string.Join(", ", graph.NodesByKind.OrderByDescending(static p => p.Value).Select(static p => $"{p.Key}={N(p.Value)}")));
        WriteLine("Связи по типам: " + string.Join(", ", graph.EdgesByKind.OrderByDescending(static p => p.Value).Select(static p => $"{p.Key}={N(p.Value)}")));

        var top = TopTargets(result.Graph, 10);
        if (top.Count > 0)
        {
            WriteLine(string.Empty);
            WriteLine("Самые вызываемые процедуры и методы:");
            foreach (var (name, count) in top)
            {
                WriteLine($"  {N(count),8}  {name}");
            }
        }

        if (result.Platform is { TopicCount: > 0 })
        {
            var topPlatform = TopTargets(result.Graph, 8, GraphNodeKind.Platform);
            if (topPlatform.Count > 0)
            {
                WriteLine(string.Empty);
                WriteLine("Самые вызываемые методы платформы:");
                foreach (var (name, count) in topPlatform)
                {
                    WriteLine($"  {N(count),8}  {name}");
                }
            }
        }

        if (result.Warnings.Count > 0)
        {
            WriteLine(string.Empty);
            WriteLine($"Предупреждения ({N(result.Warnings.Count)}):");
            foreach (var warning in result.Warnings.Take(5))
            {
                WriteLine("  " + warning);
            }

            if (result.Warnings.Count > 5)
            {
                WriteLine($"  ... ещё {N(result.Warnings.Count - 5)}");
            }
        }

        WriteLine(string.Empty);
        WriteLine($"Время: {result.Duration:hh\\:mm\\:ss\\.ff}");
    }

    private static List<(string Name, int Count)> TopTargets(DependencyGraph graph, int take, GraphNodeKind? kind = null)
    {
        var incoming = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var edge in graph.Edges)
        {
            if (edge.Kind != GraphEdgeKind.Calls)
            {
                continue;
            }

            incoming[edge.TargetId] = incoming.GetValueOrDefault(edge.TargetId) + 1;
        }

        // Для отфильтрованного списка нужно просмотреть больше кандидатов: редкие типы узлов
        // не попадают в общую верхушку.
        var candidates = incoming.OrderByDescending(static p => p.Value).Take(kind is null ? take * 3 : take * 40);
        var result = new List<(string, int)>();
        foreach (var (id, count) in candidates)
        {
            if (!graph.TryGetNode(id, out var node) || node.IsExternal || (kind is not null && node.Kind != kind))
            {
                continue;
            }

            var name = node.Kind == GraphNodeKind.Routine
                ? $"{node.SourcePath} → {node.Name}"
                : node.Name;
            result.Add((name, count));
            if (result.Count == take)
            {
                break;
            }
        }

        return result;
    }

    private static void ReportProgress(AnalysisProgress progress)
    {
        var text = progress.Stage switch
        {
            "metadata" => "Разбор XML",
            "bsl" => "Разбор BSL",
            _ => progress.Stage,
        };

        try
        {
            Console.Error.Write($"\r{text}: {progress.Processed}/{progress.Total}          ");
        }
        catch (IOException)
        {
            // Вывод в консоль не критичен для результата.
        }
    }

    private static void ClearProgress()
    {
        try
        {
            Console.Error.Write('\r');
            Console.Error.Write(new string(' ', 60));
            Console.Error.Write('\r');
        }
        catch (IOException)
        {
            // Игнорируем проблемы вывода.
        }
    }

    private static bool IsHelp(string value) =>
        value is "-h" or "--help" or "help" or "/?" or "-?";

    private static int Fail(string message)
    {
        Console.Error.WriteLine("Ошибка: " + message);
        return 1;
    }

    private static string N(int value) => value.ToString("N0", CultureInfo.CurrentCulture);

    private static void WriteLine(string value) => Console.Out.WriteLine(value);

    private static void TryEnableUtf8()
    {
        try
        {
            Console.OutputEncoding = new UTF8Encoding(false);
        }
        catch (IOException)
        {
            // Консоль может не поддерживать смену кодировки.
        }
    }

    private static void PrintHelp()
    {
        WriteLine(
            """
            data1c — разбор выгрузки конфигурации 1С (XML + BSL) и построение графа зависимостей.

            Использование:
              data1c stats <каталог-выгрузки> [опции]     сводка по выгрузке и графу
              data1c scan  <каталог-выгрузки> [опции]     разобрать и сохранить граф
              data1c view  <каталог-выгрузки> [опции]     интерактивный просмотрщик графа
              data1c platform [тема|запрос] [опции]       справка установленной платформы 1С (.hbk)
              data1c help                                 эта справка

            Опции команды platform:
              --search <текст>    найти темы справки по имени, заголовку и тексту
              --topic <имя>       показать тему целиком (например, «Массив.Добавить»)
              --version X.Y.Z.N   нужная версия платформы (по умолчанию — самая свежая)
              --top N             сколько результатов показать (по умолчанию 20)
              (без опций)         список установленных платформ и найденных справочных файлов

            Опции просмотрщика (view):
              --port N            порт локального сервера: 0 — любой свободный (по умолчанию 0);
                                  если заданный порт занят или зарезервирован системой, берётся свободный
              --open              сразу открыть браузер
              --static            записать самодостаточную страницу с подграфом вместо сервера
              --focus <узел>      центр подграфа: идентификатор или часть имени
              --depth N           радиус обхода соседей (по умолчанию 2, для статичной страницы тоже)
              --max-nodes N       ограничение числа узлов подграфа (по умолчанию 250)

            Опции:
              --out <файл>        куда сохранить граф (по умолчанию graph.json, graph.dot или graph.html)
              --format json|dot   формат вывода (по умолчанию json)
              --sections a,b,c    ограничить разбор каталогами выгрузки (Catalogs,CommonModules,...)
              --no-bsl            не разбирать модули BSL
              --no-routines       не включать процедуры и функции в граф
              --no-calls          не включать связи вызовов
              --no-metadata-refs  не включать ссылки метаданных
              --no-metadata-use   не включать обращения к метаданным из кода
              --no-external       не создавать узлы для отсутствующих целей
              --nested            включить вложенные реквизиты и табличные части
              --role-rights       разбирать права ролей (Ext/Rights.xml), граф сильно растёт
              --platform          подключить модель платформы: вызовы методов 1С получают
                                  отдельный тип узла вместо безымянной внешней цели
              --dot-max-nodes N   ограничение числа узлов для формата dot (по умолчанию 500)
              --max-dop N         степень параллелизма (по умолчанию — число ядер)
              --quiet             без индикатора прогресса

            Примеры:
              data1c stats C:\dump\1c_files
              data1c stats C:\dump\1c_files --platform
              data1c platform
              data1c platform "Массив.Добавить"
              data1c platform --search "ТаблицаЗначений" --top 10
              data1c scan C:\dump\1c_files --out graph.json
              data1c view C:\dump\1c_files --open
              data1c view C:\dump\1c_files --sections CommonModules --no-routines --open
              data1c view C:\dump\1c_files --focus "CommonModule.ОбщегоНазначения" --depth 2 --static --out module.html --open
              data1c scan C:\dump\1c_files --format dot --sections CommonModules --out modules.dot
            """);
    }
}

/// <summary>Разобранные аргументы командной строки.</summary>
internal sealed record CliOptions
{
    public string? Path { get; init; }

    public string? Output { get; init; }

    public string Format { get; init; } = "json";

    public bool IncludeBsl { get; init; } = true;

    public bool IncludeRoutines { get; init; } = true;

    public bool IncludeCalls { get; init; } = true;

    public bool IncludeMetadataRefs { get; init; } = true;

    public bool IncludeMetadataAccess { get; init; } = true;

    public bool IncludeExternal { get; init; } = true;

    public bool IncludeNested { get; init; }

    public bool RoleRights { get; init; }

    /// <summary>Подключить модель платформы: неразрешённые вызовы проверяются по синтакс-помощнику.</summary>
    public bool Platform { get; init; }

    /// <summary>Поиск по справке платформы (команда platform).</summary>
    public string? PlatformSearch { get; init; }

    /// <summary>Тема справки платформы (команда platform).</summary>
    public string? PlatformTopic { get; init; }

    /// <summary>Нужная версия платформы (команда platform).</summary>
    public string? PlatformVersion { get; init; }

    /// <summary>Сколько результатов показывать.</summary>
    public int Top { get; init; } = 20;

    public bool Quiet { get; init; }

    public int DotMaxNodes { get; init; } = 500;

    public int MaxDop { get; init; } = Environment.ProcessorCount;

    /// <summary>Порт локального просмотрщика; 0 — любой свободный, выбранный системой.</summary>
    public int Port { get; init; }

    /// <summary>Открыть браузер после запуска.</summary>
    public bool Open { get; init; }

    /// <summary>Сделать самодостаточную страницу с подграфом вместо сервера.</summary>
    public bool Static { get; init; }

    /// <summary>Узел, вокруг которого строится статичная страница.</summary>
    public string? Focus { get; init; }

    /// <summary>Радиус обхода соседей для просмотрщика.</summary>
    public int Depth { get; init; } = 2;

    /// <summary>Ограничение числа узлов подграфа для просмотрщика.</summary>
    public int MaxNodes { get; init; } = 250;

    public IReadOnlyCollection<string>? Sections { get; init; }

    public static CliOptions Parse(IEnumerable<string> args)
    {
        var options = new CliOptions();
        var queue = new Queue<string>(args);

        while (queue.Count > 0)
        {
            var arg = queue.Dequeue();
            switch (arg)
            {
                case "--out":
                    options = options with { Output = Next(queue, arg) };
                    break;
                case "--format":
                    options = options with { Format = Next(queue, arg) };
                    break;
                case "--sections":
                    options = options with
                    {
                        Sections = Next(queue, arg)
                            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                    };
                    break;
                case "--dot-max-nodes":
                    options = options with { DotMaxNodes = ParseInt(Next(queue, arg), arg) };
                    break;
                case "--max-dop":
                    options = options with { MaxDop = ParseInt(Next(queue, arg), arg) };
                    break;
                case "--port":
                    options = options with { Port = ParsePort(Next(queue, arg), arg) };
                    break;
                case "--depth":
                    options = options with { Depth = ParseInt(Next(queue, arg), arg) };
                    break;
                case "--max-nodes":
                    options = options with { MaxNodes = ParseInt(Next(queue, arg), arg) };
                    break;
                case "--focus":
                    options = options with { Focus = Next(queue, arg) };
                    break;
                case "--open":
                    options = options with { Open = true };
                    break;
                case "--static":
                    options = options with { Static = true };
                    break;
                case "--no-bsl":
                    options = options with { IncludeBsl = false };
                    break;
                case "--no-routines":
                    options = options with { IncludeRoutines = false };
                    break;
                case "--no-calls":
                    options = options with { IncludeCalls = false };
                    break;
                case "--no-metadata-refs":
                    options = options with { IncludeMetadataRefs = false };
                    break;
                case "--no-metadata-use":
                    options = options with { IncludeMetadataAccess = false };
                    break;
                case "--no-external":
                    options = options with { IncludeExternal = false };
                    break;
                case "--nested":
                    options = options with { IncludeNested = true };
                    break;
                case "--role-rights":
                    options = options with { RoleRights = true };
                    break;
                case "--platform":
                    options = options with { Platform = true };
                    break;
                case "--search":
                    options = options with { PlatformSearch = Next(queue, arg) };
                    break;
                case "--topic":
                    options = options with { PlatformTopic = Next(queue, arg) };
                    break;
                case "--version":
                    options = options with { PlatformVersion = Next(queue, arg) };
                    break;
                case "--top":
                    options = options with { Top = ParseInt(Next(queue, arg), arg) };
                    break;
                case "--quiet":
                    options = options with { Quiet = true };
                    break;
                default:
                    if (arg.StartsWith("--", StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException($"Неизвестная опция «{arg}». Запустите: data1c help");
                    }

                    if (options.Path is not null)
                    {
                        throw new InvalidOperationException($"Лишний аргумент «{arg}»: путь уже задан ({options.Path}).");
                    }

                    options = options with { Path = arg };
                    break;
            }
        }

        if (!string.Equals(options.Format, "json", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(options.Format, "dot", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Формат «{options.Format}» не поддерживается: доступны json и dot.");
        }

        return options;
    }

    private static string Next(Queue<string> queue, string option)
    {
        if (queue.Count == 0)
        {
            throw new InvalidOperationException($"Для опции «{option}» не указано значение.");
        }

        return queue.Dequeue();
    }

    private static int ParseInt(string value, string option) =>
        int.TryParse(value, CultureInfo.InvariantCulture, out var result) && result > 0
            ? result
            : throw new InvalidOperationException($"Для опции «{option}» ожидается положительное число, получено «{value}».");

    /// <summary>Порт: допускает 0 (любой свободный), остальное — 1..65535.</summary>
    private static int ParsePort(string value, string option) =>
        int.TryParse(value, CultureInfo.InvariantCulture, out var result) && result is >= 0 and <= 65535
            ? result
            : throw new InvalidOperationException($"Для опции «{option}» ожидается номер порта 0–65535 (0 — любой свободный), получено «{value}».");
}
