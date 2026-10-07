using System.Diagnostics;
using Data1c.Core.Analysis;
using Data1c.Core.Dump;
using Data1c.Core.Graph;
using Data1c.Core.Metadata;
using Data1c.Core.Platform;
using Data1c.FileSystem;
using Data1c.Store;
using Microsoft.Data.Sqlite;

namespace Data1c.Mcp;

/// <summary>Что и как разбирать: переносится из <see cref="ServerOptions"/>.</summary>
public sealed record AnalysisRequest
{
    /// <summary>
    /// Каталоги выгрузки в порядке наложения: первый — база конфигурации, следующие — расширения.
    /// По совпадающим путям эффективным считается файл из последнего источника; корневые файлы
    /// конфигурации всегда берутся из базы. Пустой список — выгрузка ещё не открыта.
    /// </summary>
    public IReadOnlyList<string> DumpPaths { get; init; } = [];

    public bool IncludeBsl { get; init; } = true;

    public bool IncludeCalls { get; init; } = true;

    public IReadOnlyList<string>? Sections { get; init; }

    public int MaxDegreeOfParallelism { get; init; }

    public bool PlatformHelp { get; init; }

    public string PlatformLocale { get; init; } = "ru";

    public IReadOnlyList<string>? PlatformRoots { get; init; }

    /// <summary>Путь к файлу индекса. По умолчанию — <c>&lt;выгрузка&gt;/.data1c/index.db</c>.</summary>
    public string? IndexPath { get; init; }

    /// <summary>
    /// Отвечать из SQLite-индекса: если его нет, сервер соберёт его один раз и дальше будет
    /// только читать. Выключено — работает старый путь с разбором в память.
    /// </summary>
    public bool UseIndex { get; init; } = true;

    /// <summary>
    /// Держать в индексе внешние цели — узлы для вызовов, которые не удалось разрешить.
    /// Выключено — индекс заметно меньше, но у неразрешённых вызовов не остаётся узла-цели.
    /// </summary>
    public bool IncludeExternal { get; init; } = true;
}

/// <summary>
/// Состояние работы сервера: выгрузка, её разбор и сервисы поверх результата.
/// Разбор идёт один раз и переиспользуется всеми инструментами; <see cref="Reload"/> сбрасывает
/// его — это нужно, когда агент правит модули в выгрузке и хочет видеть новое содержимое.
/// </summary>
public sealed class AnalysisSession : IDisposable
{
    /// <summary>Доля изменённых файлов, после которой частичная переиндексация теряет смысл.</summary>
    private const double PartialShare = 0.25;

    private readonly AnalysisRequest _request;
    private readonly Lock _gate = new();
    private readonly string? _sourceError;
    private readonly string _dumpPath;
    private readonly IDumpSource? _source;

    private SourceCodeReader? _code;
    private Task<AnalysisResult>? _analysis;
    private SqliteIndex? _index;
    private IGraphQuery? _indexGraph;

    /// <summary>
    /// Читатель индекса создаётся один раз вместе с индексом и переиспользуется: инструменты держат
    /// на нём свои кэши (например разбор конвенций — карту целей по методам и рейтинг процедур),
    /// а новый экземпляр на каждый вызов эти кэши обнулял. Читатель не IDisposable: он обёртка
    /// над соединением, которым владеет сессия.
    /// </summary>
    private IndexReader? _reader;
    private string? _indexPath;
    private bool _forceRebuild;
    private PlatformHelpIndex? _platform;
    private Task? _platformWarmup;
    private Exception? _platformError;
    private DateTimeOffset _platformStarted;
    private Timer? _watchTimer;
    private DumpChange? _lastChange;
    private DateTimeOffset? _lastCheckedAt;
    private volatile bool _rebuilding;
    private Task? _dumpCheck;
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

        if (request.DumpPaths.Count == 0)
        {
            _sourceError = "Выгрузка не открыта. Вызовите инструмент open с путём к каталогу выгрузки 1С.";
            _dumpPath = "<не открыта>";
            _state = "выгрузка не открыта";
            return;
        }

        try
        {
            var sources = request.DumpPaths
                .Select(static path => (IDumpSource)new FileSystemDumpSource(path))
                .ToList();

            _source = sources.Count == 1 ? sources[0] : new CompositeDumpSource(sources);
            _code = new SourceCodeReader(_source);
            _dumpPath = _source.DisplayName;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _sourceError = exception.Message;
            _dumpPath = string.Join(" + ", request.DumpPaths);
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

    /// <summary>Сервер настроен отвечать из индекса (или собирать его).</summary>
    public bool IsIndexMode => _request.UseIndex;

    /// <summary>Путь к файлу индекса, если он определён для этой выгрузки.</summary>
    public string? IndexPath => _indexPath ?? ResolveIndexPath();

    /// <summary>Индекс открыт и готов отвечать без разбора в память.</summary>
    public bool IsIndexReady => _indexGraph is not null;

    /// <summary>
    /// Читатель готового индекса для инструментов. Возвращает null, если индекса ещё нет: сборка
    /// длится минуты, и инструмент сам решает — ждать её или ответить без индекса. Индекс открывается
    /// только на чтение и переиспользуется.
    /// </summary>
    public IndexReader? GetIndexReader()
    {
        var path = IndexPath;
        if (path is null || !IsUsableIndex(path))
        {
            return null;
        }

        lock (_gate)
        {
            if (_index is null)
            {
                _index = SqliteIndex.OpenReadOnly(path);
            }

            // Читатель может быть уже создан другим путём (сборкой индекса) или сброшен вместе
            // с индексом: держим ровно один экземпляр на текущий индекс, потому что инструменты
            // кэшируют на нём свои разборы.
            _reader ??= new IndexReader(_index);
            _indexGraph ??= new IndexGraphQuery(_reader);
            return _reader;
        }
    }

    /// <summary>
    /// Сводка по индексу — для инструмента status, когда разбора в память нет.
    /// Если индекс ещё не открыт, читается кратковременно и только на чтение.
    /// </summary>
    public IndexStatistics? GetIndexStatistics()
    {
        var path = IndexPath;
        if (path is null)
        {
            return null;
        }

        try
        {
            lock (_gate)
            {
                if (_index is not null)
                {
                    return new IndexReader(_index).GetStatistics();
                }
            }

            if (!IsUsableIndex(path))
            {
                return null;
            }

            using var index = SqliteIndex.OpenReadOnly(path);
            return new IndexReader(index).GetStatistics();
        }
        catch (Exception exception) when (exception is SqliteException or InvalidOperationException or IOException)
        {
            return null;
        }
    }

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

    /// <summary>
    /// Отдаёт запросы к графу. Если индекс включён и выгрузка лежит на диске, сервер открывает
    /// <c>.data1c/index.db</c>, а при его отсутствии собирает индекс один раз и дальше только читает.
    /// Иначе работает прежний путь: полный разбор в память.
    /// </summary>
    public async Task<IGraphQuery> QueryAsync(CancellationToken cancellationToken)
    {
        if (_source is null)
        {
            throw new ToolException(_sourceError ?? "Выгрузка недоступна.");
        }

        lock (_gate)
        {
            if (_indexGraph is not null && !_forceRebuild)
            {
                return _indexGraph;
            }
        }

        var path = ResolveIndexPath();
        if (path is null)
        {
            // Индекс некуда положить (например, выгрузка в памяти) — считаем граф в память.
            var memory = await GetAsync(cancellationToken).ConfigureAwait(false);
            return new GraphQueryService(memory.Graph, memory.Metadata);
        }

        if (!_forceRebuild && IsUsableIndex(path))
        {
            return OpenIndex(path);
        }

        // Идёт переиндексация: не запускаем вторую сборку, ждём готовый файл (не дольше минуты).
        for (var attempt = 0; attempt < 300 && _rebuilding; attempt++)
        {
            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
        }

        if (!_forceRebuild && IsUsableIndex(path))
        {
            return OpenIndex(path);
        }

        var result = await GetAsync(cancellationToken).ConfigureAwait(false);
        _state = "индексация";
        await Task.Run(() => BuildIndex(path, result), cancellationToken).ConfigureAwait(false);
        return OpenIndex(path);
    }

    private string? ResolveIndexPath()
    {
        if (!_request.UseIndex)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(_request.IndexPath))
        {
            return Path.GetFullPath(_request.IndexPath);
        }

        return _source is FileSystemDumpSource ? Path.Combine(_dumpPath, ".data1c", "index.db") : null;
    }

    private static bool IsUsableIndex(string path) => File.Exists(path) && SqliteIndex.LooksLikeIndex(path);

    private IGraphQuery OpenIndex(string path)
    {
        lock (_gate)
        {
            if (_indexGraph is not null && !_forceRebuild)
            {
                return _indexGraph;
            }

            _index?.Dispose();
            _index = SqliteIndex.OpenReadOnly(path);
            _reader = new IndexReader(_index);
            _indexGraph = new IndexGraphQuery(_reader);
            _indexPath = path;
            _forceRebuild = false;
            _state = "готов (индекс)";
            _completedAt ??= DateTimeOffset.Now;
            return _indexGraph;
        }
    }

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
            _index?.Dispose();
            _index = null;
            _indexGraph = null;
            _reader = null;
            _forceRebuild = true;
            _failure = null;
            _completedAt = null;
            _state = "ожидание";
            _analysis = Task.Run(RunAnalysis);
        }
    }

    /// <summary>
    /// Итог частичной переиндексации выбранных модулей. Если <see cref="Done"/> ложно,
    /// переиндексации не было: <see cref="Reason"/> объясняет почему, <see cref="Hint"/> — что
    /// делать вместо неё, а <see cref="NeedsFullReload"/> говорит, что частичный путь невозможен
    /// в принципе и выручает только <see cref="Reload"/>.
    /// </summary>
    /// <param name="Done">Модули записаны в индекс, индекс переоткрыт.</param>
    /// <param name="Modules">Нормализованные пути модулей из запроса.</param>
    /// <param name="Unknown">Пути, которых нет в индексе: такие модули разбирает только полная сборка.</param>
    /// <param name="Written">Счётчики и время записи, как их вернул <see cref="IndexWriter.WriteModuleFiles"/>.</param>
    /// <param name="Change">Сводка изменений выгрузки на момент вызова; null — сравнивать не с чем.</param>
    /// <param name="CompareDuration">Сколько заняла сверка файлов выгрузки с индексом.</param>
    /// <param name="Reason">Почему частичная переиндексация не выполнена.</param>
    /// <param name="Hint">Что делать вместо неё.</param>
    /// <param name="NeedsFullReload">Помогает только полная перезагрузка.</param>
    public sealed record ReindexOutcome(
        bool Done,
        IReadOnlyList<string> Modules,
        IReadOnlyList<string> Unknown,
        IndexWriteResult? Written,
        DumpChange? Change,
        TimeSpan CompareDuration,
        string? Reason,
        string? Hint,
        bool NeedsFullReload);

    /// <summary>
    /// Частичная переиндексация указанных модулей: тем же механизмом, что и у наблюдателя за
    /// выгрузкой, но список модулей задаёт вызывающий. Метаданные из XML не перечитываются,
    /// поэтому правка одного модуля стоит десятые доли секунды, а не минуты.
    /// </summary>
    /// <param name="modulePaths">Пути модулей BSL относительно корня выгрузки.</param>
    public ReindexOutcome ReindexModules(IReadOnlyList<string> modulePaths)
    {
        ArgumentNullException.ThrowIfNull(modulePaths);
        if (_source is null)
        {
            throw new ToolException(_sourceError ?? "Выгрузка недоступна.");
        }

        var modules = modulePaths
            .Select(static path => NormalizeModulePath(path))
            .Where(static path => path.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (modules.Count == 0)
        {
            return Refuse(
                modules,
                [],
                "аргумент paths пуст: не указан ни один модуль",
                "Укажите пути модулей BSL относительно корня выгрузки "
                + "(например CommonModules/Имя/Ext/Module.bsl) или вызовите reload без paths для полной перезагрузки.");
        }

        if (_forceRebuild)
        {
            // Полная перезагрузка уже запущена: частичная запись легла бы в индекс, который
            // следующая пересборка тут же заменит.
            return Refuse(
                modules,
                [],
                "полная перезагрузка уже запущена, индекс ещё не пересобран",
                "Дождитесь окончания пересборки (status) и повторите вызов.");
        }

        if (!_request.IncludeBsl)
        {
            return Refuse(
                modules,
                [],
                "модули BSL не разбираются (--no-bsl)",
                "Частичная переиндексация обновляет только модули BSL: нужна полная перезагрузка.",
                needsFullReload: true);
        }

        var path = ResolveIndexPath();
        if (path is null)
        {
            return Refuse(
                modules,
                [],
                "индекс не используется (--no-index или индекс некуда положить)",
                "Без индекса частичная переиндексация невозможна: нужна полная перезагрузка.",
                needsFullReload: true);
        }

        if (!IsUsableIndex(path))
        {
            return Refuse(
                modules,
                [],
                "индекс ещё не собран",
                "Частичная переиндексация работает только по готовому индексу: нужна полная перезагрузка.",
                needsFullReload: true);
        }

        var foreign = modules.Where(static module => !module.EndsWith(".bsl", StringComparison.OrdinalIgnoreCase)).ToList();
        if (foreign.Count > 0)
        {
            return Refuse(
                modules,
                [],
                "не модуль BSL: " + string.Join(", ", foreign),
                "Правка XML меняет состав метаданных: нужна полная перезагрузка.");
        }

        var reader = GetIndexReader();
        if (reader is null)
        {
            return Refuse(
                modules,
                [],
                "индекс не открывается, проверить модули не по чему",
                "Частичная переиндексация работает только по открытому индексу: нужна полная перезагрузка.",
                needsFullReload: true);
        }

        var unknown = modules.Where(module => reader.GetNode("module:" + module) is null).ToList();
        if (unknown.Count > 0)
        {
            return Refuse(
                modules,
                unknown,
                "модуль неизвестен индексу: " + string.Join(", ", unknown),
                "Модуль появился вместе с новым объектом метаданных: нужна полная сборка "
                + "(reload без paths), которая разберёт XML метаданных.");
        }

        // Сводка изменений считается до записи: она описывает, что именно устарело.
        var compare = Stopwatch.StartNew();
        var change = SafeCheckDumpChange();
        compare.Stop();

        if (!WriteModules(modules, out var written))
        {
            return new ReindexOutcome(
                false,
                modules,
                unknown,
                null,
                change,
                compare.Elapsed,
                "индекс не знает часть модулей: " + string.Join(", ", modules),
                "Нужна полная сборка (reload без paths).",
                false);
        }

        if (written is { Nodes: 0 })
        {
            // Строки модулей в индексе есть, а файлов в выгрузке нет: обновлять нечего.
            return new ReindexOutcome(
                false,
                modules,
                [],
                written,
                change,
                compare.Elapsed,
                "файлы модулей не найдены в выгрузке: " + string.Join(", ", modules),
                "Полная сборка (reload без paths) уберёт их из индекса.",
                false);
        }

        // Если обновлено всё, чем выгрузка отличалась от индекса, индекс снова свежий.
        if (change is { Removed: 0, ChangedPaths: { } touched }
            && touched.All(item => modules.Contains(item, StringComparer.OrdinalIgnoreCase)))
        {
            _lastChange = new DumpChange(0, 0, 0, change.Total);
            _lastCheckedAt = DateTimeOffset.Now;
        }

        _state = $"готов (индекс), обновлено модулей: {modules.Count}";
        return new ReindexOutcome(true, modules, [], written, change, compare.Elapsed, null, null, false);
    }

    /// <summary>
    /// Путь модуля внутри выгрузки — как в индексе: разделители «/», без ведущего слэша.
    /// Обёртка над <c>DumpPath.Normalize</c> нужна потому, что свойство <see cref="DumpPath"/>
    /// перекрывает одноимённый класс путей.
    /// </summary>
    private static string NormalizeModulePath(string path) => Data1c.Core.Dump.DumpPath.Normalize(path);

    /// <summary>Отказ от частичной переиндексации: причина, что делать вместо неё и признак «нужна полная».</summary>
    private ReindexOutcome Refuse(
        IReadOnlyList<string> modules,
        IReadOnlyList<string> unknown,
        string reason,
        string hint,
        bool needsFullReload = false) =>
        new(false, modules, unknown, null, _lastChange, TimeSpan.Zero, reason, hint, needsFullReload);

    /// <summary>Сверка выгрузки с индексом для сводки: неудача сверки переиндексацию не отменяет.</summary>
    private DumpChange? SafeCheckDumpChange()
    {
        try
        {
            return CheckDumpChange();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SqliteException or InvalidOperationException)
        {
            _failure = exception;
            return _lastChange;
        }
    }

    /// <summary>
    /// Запись модулей в индекс: общий путь для наблюдателя за выгрузкой и инструмента reload.
    /// После записи индекс переоткрывается, а кеш исходных текстов сбрасывается — иначе инструмент
    /// code отдал бы старый текст отредактированного модуля.
    /// </summary>
    /// <returns>false, если индекс не знает часть модулей: тогда нужна полная сборка.</returns>
    private bool WriteModules(IReadOnlyList<string> modules, out IndexWriteResult? written)
    {
        var path = ResolveIndexPath()!;
        _state = $"частичная переиндексация: модулей {modules.Count}";

        // Метаданные из XML не перечитываются: владелец и вид модуля берутся из индекса,
        // поэтому обновление стоит секунды, а не минуты.
        using (var index = SqliteIndex.Open(path))
        {
            written = new IndexWriter(index).WriteModuleFiles(_source!, modules, Platform);
        }

        if (written is null)
        {
            return false;
        }

        lock (_gate)
        {
            _index?.Dispose();
            _index = null;
            _indexGraph = null;
            _reader = null;
            _code = new SourceCodeReader(_source!);
        }

        OpenIndex(path);
        return true;
    }

    /// <summary>Настройки полного разбора выгрузки.</summary>
    private AnalysisOptions CreateAnalysisOptions() => new()
    {
        IncludeBsl = _request.IncludeBsl,
        MaxDegreeOfParallelism = _request.MaxDegreeOfParallelism > 0
            ? _request.MaxDegreeOfParallelism
            : Environment.ProcessorCount,
        PlatformSource = CreatePlatformSource(),
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
            IncludeExternalNodes = _request.IncludeExternal,
        },
    };

    /// <summary>
    /// Частичная переиндексация: пересобираются только изменённые модули BSL. Если изменились
    /// файлы метаданных, что-то удалено или затронута заметная часть выгрузки — нужна полная сборка.
    /// </summary>
    private bool TryPartialReindex(DumpChange change, out string message)
    {
        message = string.Empty;
        var path = ResolveIndexPath();
        if (path is null || _source is null || !_request.IncludeBsl)
        {
            return false;
        }

        if (change.Removed > 0 || change.ChangedPaths is not { Count: > 0 } touched)
        {
            return false;
        }

        if (change.Total > 0 && touched.Count > change.Total * PartialShare)
        {
            return false;
        }

        var modules = new List<string>(touched.Count);
        foreach (var file in touched)
        {
            if (!file.EndsWith(".bsl", StringComparison.OrdinalIgnoreCase))
            {
                // Изменился XML: состав метаданных мог поменяться, надёжнее пересобрать целиком.
                return false;
            }

            modules.Add(file);
        }

        if (!WriteModules(modules, out _))
        {
            // Среди модулей есть неизвестный индексу: он появился вместе с новым объектом
            // метаданных, и разбирать его нужно полной сборкой.
            return false;
        }

        message = $", обновлено модулей: {modules.Count}";
        return true;
    }

    private AnalysisResult RunAnalysis()
    {
        var source = _source!;
        try
        {
            _state = "разбор";
            var result = new DumpAnalyzer().Analyze(source, CreateAnalysisOptions());
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

    /// <summary>
    /// Собирает индекс в стороне и подменяет готовым файлом: во время сборки (десятки секунд)
    /// сервер продолжает отвечать из прежнего индекса, а не из наполовину записанного.
    /// </summary>
    private void BuildIndex(string path, AnalysisResult result)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporary = path + ".building";
        DeleteIndexFiles(temporary);

        using (var index = SqliteIndex.Open(temporary))
        {
            new IndexWriter(index).Write(_source!, result);
        }

        lock (_gate)
        {
            _index?.Dispose();
            _index = null;
            _indexGraph = null;
            _reader = null;
        }

        DeleteIndexFiles(path);
        File.Move(temporary, path);
        _indexPath = path;
        _state = "индекс собран";
    }

    /// <summary>Удаляет файл индекса вместе с журналом: SQLite держит рядом -wal и -shm.</summary>
    private static void DeleteIndexFiles(string path)
    {
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            var file = path + suffix;
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
    }

    /// <summary>Изменения выгрузки относительно индекса: null — сравнить нельзя.</summary>
    public DumpChange? DumpChange => _lastChange;

    /// <summary>Когда состояние выгрузки сравнивалось с индексом в последний раз.</summary>
    public DateTimeOffset? LastCheckedAt => _lastCheckedAt;

    /// <summary>Идёт переиндексация выгрузки.</summary>
    public bool IsRebuilding => _rebuilding;

    /// <summary>Сервер наблюдает за выгрузкой и пересобирает индекс сам.</summary>
    public bool IsWatching => _watchTimer is not null;

    /// <summary>
    /// Сравнивает состояние файлов выгрузки с индексом. Возвращает null, если сравнивать не с чем
    /// (нет выгрузки, индекс не используется или ещё не собран).
    /// </summary>
    public DumpChange? CheckDumpChange(CancellationToken cancellationToken = default)
    {
        if (_source is null || !_request.UseIndex)
        {
            return null;
        }

        var reader = GetIndexReader();
        if (reader is null)
        {
            return null;
        }

        var change = DumpState.Compare(reader, _source, cancellationToken);
        _lastChange = change;
        _lastCheckedAt = DateTimeOffset.Now;
        return change;
    }

    /// <summary>
    /// Запускает проверку свежести индекса в фоне, если её давно не делали. Обход каталога
    /// выгрузки стоит секунды (десятки тысяч файлов), поэтому инструмент не должен его ждать.
    /// В режиме наблюдения проверку делает сам наблюдатель.
    /// </summary>
    public void StartDumpCheck()
    {
        if (_source is null || !_request.UseIndex || IsWatching)
        {
            return;
        }

        lock (_gate)
        {
            if (_dumpCheck is { IsCompleted: false })
            {
                return;
            }

            if (_lastCheckedAt is { } checkedAt && DateTimeOffset.Now - checkedAt < TimeSpan.FromMinutes(1))
            {
                return;
            }

            _dumpCheck = Task.Run(() =>
            {
                try
                {
                    CheckDumpChange();
                }
                catch (Exception exception)
                {
                    _failure = exception;
                }
            });
        }
    }

    /// <summary>
    /// Наблюдение за выгрузкой: раз в <paramref name="interval"/> состояние файлов сравнивается
    /// с индексом, и при изменениях индекс пересобирается в фоне. Так новая выгрузка подхватывается
    /// без ручного шага.
    /// </summary>
    public void StartWatching(TimeSpan interval)
    {
        if (_source is null || !_request.UseIndex || _watchTimer is not null)
        {
            return;
        }

        _watchTimer = new Timer(_ => WatchTick(), null, TimeSpan.Zero, interval);
    }

    private void WatchTick()
    {
        if (_rebuilding)
        {
            return;
        }

        try
        {
            var change = CheckDumpChange();
            if (change is null || change.IsEmpty)
            {
                return;
            }

            RebuildInBackground(change);
        }
        catch (Exception exception)
        {
            _failure = exception;
        }
    }

    /// <summary>Пересборка индекса в фоне: разбор выгрузки заново и запись нового индекса.</summary>
    private void RebuildInBackground(DumpChange change)
    {
        lock (_gate)
        {
            if (_rebuilding)
            {
                return;
            }

            _rebuilding = true;
        }

        _state = "переиндексация: " + change;
        _ = Task.Run(() =>
        {
            try
            {
                if (TryPartialReindex(change, out var message))
                {
                    _lastChange = new DumpChange(0, 0, 0, change.Total);
                    _state = "готов (индекс)" + message;
                    return;
                }

                var result = RunAnalysis();
                var path = ResolveIndexPath();
                if (path is not null)
                {
                    BuildIndex(path, result);
                    OpenIndex(path);
                    _lastChange = new DumpChange(0, 0, 0, change.Total);
                }
            }
            catch (Exception exception)
            {
                _failure = exception;
                _state = "ошибка переиндексации";
            }
            finally
            {
                _rebuilding = false;
            }
        });
    }

    /// <summary>Освобождает соединение с индексом и останавливает наблюдение за выгрузкой.</summary>
    public void Dispose()
    {
        _watchTimer?.Dispose();
        _watchTimer = null;
        _index?.Dispose();
        _index = null;
        _indexGraph = null;
        _reader = null;
    }
    /// <summary>Справка платформы запрошена ключом <c>--platform</c>.</summary>
    public bool PlatformRequested => _request.PlatformHelp;

    /// <summary>Модель платформы: null, пока она не загружена.</summary>
    public PlatformHelpIndex? Platform => _platform ?? Result?.Platform;

    /// <summary>Загрузка модели платформы идёт прямо сейчас.</summary>
    public bool IsPlatformLoading => _platformWarmup is { IsCompleted: false };

    /// <summary>Сколько уже идёт загрузка модели платформы.</summary>
    public TimeSpan PlatformElapsed => _platformStarted == default ? TimeSpan.Zero : DateTimeOffset.Now - _platformStarted;

    /// <summary>Ошибка загрузки модели платформы, если она была.</summary>
    public string? PlatformError => _platformError?.Message;

    /// <summary>Задача фоновой загрузки справки платформы (для журнала сервера).</summary>
    public Task? PlatformWarmup => _platformWarmup;

    /// <summary>
    /// Загружает справку платформы в фоне, не дожидаясь разбора выгрузки. Контейнеры <c>.hbk</c>
    /// разбираются один раз, поэтому вызов инструмента platform не ждёт десятки секунд.
    /// Повторные вызовы ничего не делают.
    /// </summary>
    public void StartPlatformWarmup()
    {
        lock (_gate)
        {
            if (!_request.PlatformHelp || _platformWarmup is not null || _platform is not null)
            {
                return;
            }

            _platformStarted = DateTimeOffset.Now;
            _platformWarmup = Task.Run(() =>
            {
                try
                {
                    var index = new PlatformHelpIndex(CreatePlatformSource()!);
                    _ = index.TopicCount; // разбирает .hbk и строит словарь тем
                    lock (_gate)
                    {
                        _platform = index;
                    }
                }
                catch (Exception exception)
                {
                    _platformError = exception;
                }
            });
        }
    }

    private IPlatformSource? CreatePlatformSource() =>
        _request.PlatformHelp
            ? new FileSystemPlatformSource(_request.PlatformLocale, _request.PlatformRoots)
            : null;
}
