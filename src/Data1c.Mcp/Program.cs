using System.Text;

namespace Data1c.Mcp;

/// <summary>
/// Точка входа MCP-сервера. Сервер запускается MCP-клиентом (например, harness) как дочерний процесс:
/// stdin и stdout заняты протоколом, диагностика идёт в stderr.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        var parsed = ServerOptions.Parse(args);
        if (parsed.Help)
        {
            Console.Error.WriteLine(ServerOptions.Usage);
            return 0;
        }

        if (parsed.Options is null)
        {
            Console.Error.WriteLine("data1c-mcp: " + parsed.Error);
            Console.Error.WriteLine();
            Console.Error.WriteLine(ServerOptions.Usage);
            return 2;
        }

        var options = parsed.Options;
        using var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        using var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = false };
        using var stderr = new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) { AutoFlush = true };
        using var cancellation = new CancellationTokenSource();

        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        var session = new AnalysisSession(new AnalysisRequest
        {
            DumpPaths = options.DumpPaths,
            IncludeBsl = options.IncludeBsl,
            IncludeCalls = options.IncludeCalls,
            Sections = options.Sections,
            MaxDegreeOfParallelism = options.MaxDegreeOfParallelism,
            PlatformHelp = options.PlatformHelp,
            PlatformLocale = options.PlatformLocale,
            PlatformRoots = options.PlatformRoots,
            IndexPath = options.IndexPath,
            UseIndex = options.UseIndex,
        });

        // Готовый индекс означает, что разбор в память при старте не нужен: инструменты
        // ответят из базы. Разбор запускаем только когда индекс нужно собрать.
        var indexReady = options.UseIndex
            && !string.IsNullOrWhiteSpace(session.IndexPath)
            && File.Exists(session.IndexPath);

        stderr.WriteLine(
            $"data1c-mcp {McpServer.ServerVersion}: выгрузка {session.DumpPath}; состояние: {session.State}" +
            (options.UseIndex
                ? $"; индекс: {session.IndexPath}{(indexReady ? " (готов)" : " (будет собран при первом запросе)")}"
                : "; индекс выключен, разбор в память"));

        if (!options.Lazy && !indexReady)
        {
            StartInBackground(session, stderr);
        }

        // Справка платформы — самая долгая операция сервера: .hbk разбираются десятки секунд.
        // Грузим её в фоне сразу, чтобы первый вызов инструмента platform не ждал.
        if (options.PlatformHelp && !options.Lazy)
        {
            StartPlatformWarmup(session, stderr);
        }

        // Наблюдение за выгрузкой: новая выгрузка подхватывается сама, без ручного шага.
        if (options.WatchSeconds > 0 && options.UseIndex)
        {
            session.StartWatching(TimeSpan.FromSeconds(options.WatchSeconds));
            stderr.WriteLine($"data1c-mcp: наблюдение за выгрузкой каждые {options.WatchSeconds} с");
        }

        var server = new McpServer(new ToolCatalog(session), stdin, stdout, stderr);
        try
        {
            server.RunAsync(cancellation.Token).GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            stderr.WriteLine("data1c-mcp: сбой сервера: " + exception);
            return 1;
        }

        stderr.WriteLine("data1c-mcp: stdin закрыт, сервер остановлен");
        return 0;
    }

    /// <summary>
    /// Разбор стартует сразу: пока агент читает задачу, граф уже строится. Ошибку разбора
    /// не роняем — её покажет инструмент status, а сам сервер продолжит отвечать.
    /// </summary>
    private static void StartInBackground(AnalysisSession session, TextWriter log)
    {
        try
        {
            _ = session.Start().ContinueWith(
                task => log.WriteLine("data1c-mcp: разбор не удался: " + task.Exception?.GetBaseException().Message),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
        }
        catch (ToolException exception)
        {
            log.WriteLine("data1c-mcp: " + exception.Message);
        }
    }

    /// <summary>Фоновая загрузка справки платформы с записью итога в журнал сервера.</summary>
    private static void StartPlatformWarmup(AnalysisSession session, TextWriter log)
    {
        session.StartPlatformWarmup();
        var warmup = session.PlatformWarmup;
        if (warmup is null)
        {
            return;
        }

        _ = warmup.ContinueWith(
            _ => log.WriteLine(
                session.Platform is { } platform
                    ? $"data1c-mcp: справка платформы готова за {session.PlatformElapsed.TotalSeconds:F1} с ({platform.TopicCount} тем)"
                    : $"data1c-mcp: справка платформы не загрузилась за {session.PlatformElapsed.TotalSeconds:F1} с: {session.PlatformError}"),
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);
    }
}
