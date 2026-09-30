using Data1c.Core.Analysis;
using Data1c.Core.Dump;
using Data1c.Core.Graph;
using Data1c.Core.Metadata;
using Data1c.Core.Platform;
using Data1c.FileSystem;

namespace Data1c.Mcp;

/// <summary>Что и как разбирать: переносится из <see cref="ServerOptions"/>.</summary>
public sealed record AnalysisRequest
{
    public required string DumpPath { get; init; }

    public bool IncludeBsl { get; init; } = true;

    public bool IncludeCalls { get; init; } = true;

    public IReadOnlyList<string>? Sections { get; init; }

    public int MaxDegreeOfParallelism { get; init; }

    public bool PlatformHelp { get; init; }

    public string PlatformLocale { get; init; } = "ru";

    public IReadOnlyList<string>? PlatformRoots { get; init; }
}

/// <summary>
/// Состояние работы сервера: выгрузка, её разбор и сервисы поверх результата.
/// Разбор идёт один раз и переиспользуется всеми инструментами; <see cref="Reload"/> сбрасывает
/// его — это нужно, когда агент правит модули в выгрузке и хочет видеть новое содержимое.
/// </summary>
public sealed class AnalysisSession
{
    private readonly AnalysisRequest _request;
    private readonly Lock _gate = new();
    private readonly string? _sourceError;
    private readonly string _dumpPath;
    private readonly IDumpSource? _source;

    private SourceCodeReader? _code;
    private Task<AnalysisResult>? _analysis;
    private volatile string _state = "ожидание";
    private Exception? _failure;
    private DateTimeOffset? _completedAt;

    public AnalysisSession(AnalysisRequest request)
        : this(request, null)
    {
    }

    /// <summary>
    /// Создаёт сессию над готовым источником. Источник можно подставить в тестах: тогда каталог
    /// выгрузки на диске не нужен.
    /// </summary>
    public AnalysisSession(AnalysisRequest request, IDumpSource? source)
    {
        ArgumentNullException.ThrowIfNull(request);
        _request = request;

        if (source is not null)
        {
            _source = source;
            _code = new SourceCodeReader(source);
            _dumpPath = source.DisplayName;
            return;
        }

        if (string.IsNullOrWhiteSpace(request.DumpPath))
        {
            _sourceError = "Выгрузка не открыта. Вызовите инструмент open с путём к каталогу выгрузки 1С.";
            _dumpPath = "<не открыта>";
            _state = "выгрузка не открыта";
            return;
        }

        try
        {
            var fileSystem = new FileSystemDumpSource(request.DumpPath);
            _source = fileSystem;
            _code = new SourceCodeReader(fileSystem);
            _dumpPath = fileSystem.RootPath;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _sourceError = exception.Message;
            _dumpPath = request.DumpPath;
            _state = "ошибка: " + exception.Message;
        }
    }

    /// <summary>Настройки, с которыми создана сессия: инструмент open берёт их за основу.</summary>
    public AnalysisRequest Request => _request;

    /// <summary>Каталог выгрузки, как его видит сервер.</summary>
    public string DumpPath => _dumpPath;

    /// <summary>Открыта ли выгрузка: без неё работают только status и open.</summary>
    public bool IsOpen => _source is not null;

    /// <summary>Текущее состояние разбора — человекочитаемая строка для инструмента status.</summary>
    public string State => _state;

    /// <summary>Ошибка разбора, если он не удался.</summary>
    public Exception? Failure => _failure;

    /// <summary>Когда разбор успешно завершился.</summary>
    public DateTimeOffset? CompletedAt => _completedAt;

    public bool IsRunning => _analysis is { IsCompleted: false };

    /// <summary>Результат разбора, если он уже готов.</summary>
    public AnalysisResult? Result =>
        _analysis is { IsCompletedSuccessfully: true } task ? task.Result : null;

    /// <summary>Читатель исходных текстов: доступен сразу, разбора не требует.</summary>
    public SourceCodeReader Code =>
        _code ?? throw new ToolException(_sourceError ?? "Выгрузка недоступна.");

    public IDumpSource Source =>
        _source ?? throw new ToolException(_sourceError ?? "Выгрузка недоступна.");

    /// <summary>Запускает разбор, если он ещё не запущен, и возвращает общую задачу.</summary>
    public Task<AnalysisResult> Start()
    {
        lock (_gate)
        {
            if (_source is null)
            {
                throw new ToolException(_sourceError ?? "Выгрузка недоступна.");
            }

            _analysis ??= Task.Run(RunAnalysis);
            return _analysis;
        }
    }

    /// <summary>Ждёт результат разбора; отмена ожидания не отменяет сам разбор.</summary>
    public Task<AnalysisResult> GetAsync(CancellationToken cancellationToken) =>
        Start().WaitAsync(cancellationToken);

    /// <summary>Сбрасывает разбор и кеш исходников: нужен после правки файлов выгрузки.</summary>
    public void Reload()
    {
        lock (_gate)
        {
            if (_source is null)
            {
                throw new ToolException(_sourceError ?? "Выгрузка недоступна.");
            }

            _code = new SourceCodeReader(_source);
            _failure = null;
            _completedAt = null;
            _state = "ожидание";
            _analysis = Task.Run(RunAnalysis);
        }
    }

    private AnalysisResult RunAnalysis()
    {
        var source = _source!;
        try
        {
            _state = "разбор";
            var options = new AnalysisOptions
            {
                IncludeBsl = _request.IncludeBsl,
                MaxDegreeOfParallelism = _request.MaxDegreeOfParallelism > 0
                    ? _request.MaxDegreeOfParallelism
                    : Environment.ProcessorCount,
                PlatformSource = _request.PlatformHelp
                    ? new FileSystemPlatformSource(_request.PlatformLocale, _request.PlatformRoots)
                    : null,
                Progress = new Progress<AnalysisProgress>(Report),
                Metadata = new MetadataReadOptions
                {
                    Sections = _request.Sections,
                    AttachModules = _request.IncludeBsl,
                },
                Graph = new DependencyGraphOptions
                {
                    IncludeContainment = true,
                    IncludeMetadataReferences = true,
                    IncludeModules = _request.IncludeBsl,
                    IncludeRoutines = _request.IncludeBsl,
                    IncludeCalls = _request.IncludeBsl && _request.IncludeCalls,
                    IncludeMetadataAccess = _request.IncludeBsl,
                    IncludeExternalNodes = true,
                },
            };

            var result = new DumpAnalyzer().Analyze(source, options);
            _completedAt = DateTimeOffset.Now;
            _state = "готов";
            return result;
        }
        catch (Exception exception)
        {
            _failure = exception;
            _state = "ошибка: " + exception.Message;
            throw;
        }
    }

    private void Report(AnalysisProgress progress)
    {
        var stage = progress.Stage switch
        {
            "metadata" => "разбор метаданных",
            "bsl" => "разбор модулей",
            _ => progress.Stage,
        };

        _state = progress.Total > 0 ? $"{stage} {progress.Processed} из {progress.Total}" : stage;
    }
}
