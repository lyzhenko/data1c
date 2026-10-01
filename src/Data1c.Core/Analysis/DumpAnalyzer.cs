using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Data1c.Core.Bsl;
using Data1c.Core.Dump;
using Data1c.Core.Graph;
using Data1c.Core.Metadata;
using Data1c.Core.Platform;

namespace Data1c.Core.Analysis;

/// <summary>Настройки полного анализа выгрузки.</summary>
public sealed record AnalysisOptions
{
    public MetadataReadOptions Metadata { get; init; } = new();

    public DependencyGraphOptions Graph { get; init; } = new();

    /// <summary>Разбирать модули BSL.</summary>
    public bool IncludeBsl { get; init; } = true;

    /// <summary>
    /// Источник справки установленной платформы. Если задан, неразрешённые вызовы проверяются
    /// по синтакс-помощнику и получают собственный тип узла вместо безымянной заглушки.
    /// </summary>
    public IPlatformSource? PlatformSource { get; init; }

    public int MaxDegreeOfParallelism { get; init; } = Environment.ProcessorCount;

    public IProgress<AnalysisProgress>? Progress { get; init; }
}

public sealed record AnalysisProgress(string Stage, int Processed, int Total);

public sealed record AnalysisStatistics(
    int MetadataObjects,
    int TopLevelObjects,
    int ParsedXmlFiles,
    int FailedXmlFiles,
    int ModuleFiles,
    long ModuleBytes,
    int Modules,
    int Routines,
    int Procedures,
    int Functions,
    int GraphNodes,
    int GraphEdges);

/// <summary>Результат анализа выгрузки.</summary>
public sealed record AnalysisResult(
    MdObjectModel Metadata,
    IReadOnlyList<BslModuleInfo> Modules,
    DependencyGraph Graph,
    AnalysisStatistics Statistics,
    IReadOnlyList<string> Warnings,
    TimeSpan Duration,
    string SourceName,
    PlatformHelpIndex? Platform = null);

/// <summary>Точка входа библиотеки: разбирает выгрузку и строит граф зависимостей.</summary>
public sealed class DumpAnalyzer
{
    private readonly Func<IBslModuleParser> _bslParserFactory;
    private readonly MetadataDumpReader _metadataReader;

    public DumpAnalyzer() => (_bslParserFactory, _metadataReader) = (static () => new BslModuleParser(), new MetadataDumpReader());

    /// <summary>
    /// Создаёт анализатор с готовым разборщиком модулей.
    /// </summary>
    /// <remarks>
    /// Переданный экземпляр используется всеми рабочими потоками, поэтому модули разбираются
    /// последовательно. Чтобы разбор шёл параллельно, используйте
    /// <see cref="DumpAnalyzer(Func{IBslModuleParser}, MetadataDumpReader?)"/> — тогда на каждый поток
    /// создаётся свой разборщик.
    /// </remarks>
    public DumpAnalyzer(IBslModuleParser? bslParser, MetadataDumpReader? metadataReader = null)
        : this(bslParser is null ? static () => new BslModuleParser() : () => bslParser, metadataReader)
    {
    }

    /// <summary>Создаёт анализатор с фабрикой разборщиков модулей: по экземпляру на рабочий поток.</summary>
    public DumpAnalyzer(Func<IBslModuleParser> bslParserFactory, MetadataDumpReader? metadataReader = null)
    {
        ArgumentNullException.ThrowIfNull(bslParserFactory);
        _bslParserFactory = bslParserFactory;
        _metadataReader = metadataReader ?? new MetadataDumpReader();
    }

    public AnalysisResult Analyze(IDumpSource source, AnalysisOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        options ??= new AnalysisOptions();

        var stopwatch = Stopwatch.StartNew();
        var warnings = new List<string>();

        var metadataOptions = options.Metadata;
        if (options.Progress is not null)
        {
            var progress = options.Progress;
            metadataOptions = metadataOptions with
            {
                Progress = new Progress<MetadataReadProgress>(p => progress.Report(new AnalysisProgress("metadata", p.Processed, p.Total))),
            };
        }

        var read = _metadataReader.Read(source, metadataOptions, cancellationToken);
        warnings.AddRange(read.Warnings);

        List<BslModuleInfo> modules = options.IncludeBsl
            ? ParseModules(source, read.ModuleFiles, options, warnings, cancellationToken)
            : [];

        // Обработчики формы назначаются процедурами её модуля: имена берутся из Ext/Form.xml,
        // а строки процедур — из разобранного модуля формы, который читается позже метаданных.
        FormModelLinker.Link(read.Model, modules);

        var platform = CreatePlatformIndex(options, warnings);
        var graph = new DependencyGraphBuilder().Build(read.Model, modules, options.Graph, platform);
        stopwatch.Stop();

        var statistics = new AnalysisStatistics(
            read.Model.Count,
            read.Model.Configuration.Children.Count,
            read.ParsedFiles,
            read.FailedFiles,
            read.ModuleFiles.Count,
            read.ModuleFiles.Sum(static m => m.File.Size),
            modules.Count,
            modules.Sum(static m => m.Routines.Count),
            modules.Sum(static m => m.Routines.Count(static r => r.Kind == BslRoutineKind.Procedure)),
            modules.Sum(static m => m.Routines.Count(static r => r.Kind == BslRoutineKind.Function)),
            graph.Statistics.NodeCount,
            graph.Statistics.EdgeCount);

        return new AnalysisResult(
            read.Model,
            modules,
            graph,
            statistics,
            warnings,
            stopwatch.Elapsed,
            source.DisplayName,
            platform);
    }

    /// <summary>Загружает модель платформы, если задан источник справки.</summary>
    private static PlatformHelpIndex? CreatePlatformIndex(AnalysisOptions options, List<string> warnings)
    {
        if (options.PlatformSource is null)
        {
            return null;
        }

        var index = new PlatformHelpIndex(options.PlatformSource);
        foreach (var warning in index.Warnings)
        {
            warnings.Add("Модель платформы: " + warning);
        }

        return index;
    }

    private List<BslModuleInfo> ParseModules(
        IDumpSource source,
        IReadOnlyList<MdModuleRef> moduleRefs,
        AnalysisOptions options,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        var results = new BslModuleInfo?[moduleRefs.Count];
        var errors = new ConcurrentBag<string>();
        var processed = 0;
        var total = moduleRefs.Count;
        var progress = options.Progress;

        // Разборщик хранит состояние в полях, поэтому каждому потоку выдаётся свой экземпляр.
        using var parsers = new ThreadLocal<IBslModuleParser>(_bslParserFactory);

        Parallel.ForEach(
            Enumerable.Range(0, moduleRefs.Count),
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, options.MaxDegreeOfParallelism),
                CancellationToken = cancellationToken,
            },
            index =>
            {
                var moduleRef = moduleRefs[index];
                try
                {
                    using var stream = source.OpenRead(moduleRef.File);
                    var text = ReadText(stream);
                    results[index] = parsers.Value!.Parse(new BslModuleSource(
                        moduleRef.File.RelativePath,
                        text,
                        moduleRef.OwnerId,
                        moduleRef.Kind));
                }
                catch (Exception ex)
                {
                    errors.Add($"Модуль «{moduleRef.File.RelativePath}» не разобран: {ex.Message}");
                    results[index] = new BslModuleInfo
                    {
                        Path = moduleRef.File.RelativePath,
                        OwnerId = moduleRef.OwnerId,
                        Kind = moduleRef.Kind,
                    };
                }

                var done = Interlocked.Increment(ref processed);
                if (progress is not null && (done % 256 == 0 || done == total))
                {
                    progress.Report(new AnalysisProgress("bsl", done, total));
                }
            });

        warnings.AddRange(errors.OrderBy(static e => e, StringComparer.Ordinal));
        return [.. results.Where(static r => r is not null).Select(static r => r!)];
    }

    private static string ReadText(Stream stream)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}
