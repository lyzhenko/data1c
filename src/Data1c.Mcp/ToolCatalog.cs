using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Data1c.Core.Analysis;
using Data1c.Core.Bsl;
using Data1c.Core.Dump;
using Data1c.Core.Graph;
using Data1c.Core.Metadata;
using Data1c.Core.Platform;
using Data1c.FileSystem;
using Data1c.Store;

namespace Data1c.Mcp;

/// <summary>
/// Инструменты MCP-сервера: навигация по выгрузке 1С и чтение кода так, как это нужно при разработке
/// на BSL. Все инструменты работают поверх одного разбора (<see cref="AnalysisSession"/>).
/// </summary>
public sealed class ToolCatalog
{
    /// <summary>Расширения, по которым ищет grep, если агент не задал свои.</summary>
    private static readonly IReadOnlyCollection<string> DefaultGrepExtensions = [".bsl", ".xml"];

    private readonly Lock _sessionGate = new();
    private readonly Lock _rightsGate = new();
    private readonly List<ToolSpec> _tools;
    private AnalysisSession _session;
    private RoleRightsCatalog? _rights;
    private AnalysisSession? _rightsSession;
    private object? _rightsCode;

    public ToolCatalog(AnalysisSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
        _tools =
        [
            StatusTool(),
            OpenTool(),
            SearchTool(),
            SimilarTool(),
            GrepTool(),
            NodeTool(),
            NeighborsTool(),
            CodeTool(),
            MetadataTool(),
            EntryPointsTool(),
            PlatformTool(),
            CheckTool(),
            TypesTool(),
            RightsTool(),
            ConventionsTool(),
            ReloadTool(),
        ];
    }

    /// <summary>Текущая сессия: её заменяет инструмент open.</summary>
    private AnalysisSession Session
    {
        get
        {
            lock (_sessionGate)
            {
                return _session;
            }
        }
    }

    public IReadOnlyList<ToolSpec> Tools => _tools;

    public ToolSpec? Find(string name) =>
        _tools.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.Ordinal));

    private ToolSpec StatusTool() => new(
        "status",
        "Состояние разбора выгрузки 1С: идёт ли разбор, статистика графа, ошибка и предупреждения. "
        + "Остальные инструменты ждут завершения разбора сами, поэтому status нужен для проверки и диагностики.",
        [],
        (_, _) => Task.FromResult(Status()));

    private ToolSpec OpenTool() => new(
        "open",
        "Открыть выгрузку конфигурации 1С по путям к каталогам (или переключиться на другую): первый каталог — "
        + "база, следующие — расширения, они перекрывают базу по совпадающим путям. Разбор идёт в фоне (status).",
        [
            new ToolParameter("path", "string", "Каталог выгрузки, созданный командой «Выгрузить конфигурацию в файлы»."),
            new ToolParameter("paths", "array", "Несколько каталогов: база и расширения. Заменяет path."),
            new ToolParameter("platform", "boolean", "Подключить справку платформы 1С для этой выгрузки."),
            new ToolParameter("sections", "array", "Разбирать только эти секции выгрузки (например, [\"CommonModules\",\"Catalogs\"])."),
        ],
        (arguments, cancellationToken) =>
        {
            _ = cancellationToken;
            var current = Session;
            var paths = arguments.GetStringList("paths");
            if (paths is null)
            {
                paths = [arguments.RequireString("path")];
            }

            var platform = arguments.GetBool("platform", current.Request.PlatformHelp);
            var sections = arguments.GetStringList("sections") ?? current.Request.Sections;

            var opened = new AnalysisSession(current.Request with
            {
                DumpPaths = paths,
                PlatformHelp = platform,
                Sections = sections,
            });

            // Start бросает ToolException, если каталога нет: тогда прежняя выгрузка остаётся рабочей.
            var analysis = opened.Start();

            lock (_sessionGate)
            {
                _session = opened;
            }

            _ = analysis.ContinueWith(
                task => { _ = task.Exception; },
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);

            return Task.FromResult($"Выгрузка открыта: {opened.DumpPath}\nСостояние: {opened.State}\n"
                + "Разбор идёт в фоне — вызовите status, когда он понадобится.");
        });

    private ToolSpec SearchTool() => new(
        "search",
        "Поиск по имени, синониму, идентификатору или пути файла: объекты, модули и процедуры из графа, "
        + "а при includeNested — ещё и реквизиты, табличные части, формы и команды внутри объектов. "
        + "Даёт идентификаторы для node/neighbors/code/metadata. Пример: search query=\"Номенклатура\".",
        [
            new ToolParameter("query", "string", "Имя, синоним, часть идентификатора (Catalog.Товары) или путь файла.", Required: true),
            new ToolParameter("limit", "integer", "Сколько результатов вернуть (1–100, по умолчанию 20)."),
            new ToolParameter(
                "kinds",
                "array",
                "Оставить только эти виды узлов графа.",
                Values: ["Configuration", "MetadataObject", "Module", "Routine", "External", "Platform"]),
            new ToolParameter("includeNested", "boolean", "Искать также реквизиты и табличные части внутри объектов (по умолчанию да)."),
            new ToolParameter(
                "metadataKinds",
                "array",
                "Оставить из вложенных только эти виды: Attribute, TabularSection, Form, Command, Template."),
        ],
        async (arguments, token) =>
        {
            var query = await QueryAsync(token);
            var text = arguments.RequireString("query");
            var limit = arguments.GetInt("limit", 20, 1, 100);
            var kinds = ParseKinds(arguments.GetStringList("kinds"));
            var includeNested = arguments.GetBool("includeNested", true);
            var metadataKinds = arguments.GetStringList("metadataKinds");

            var hits = query.Search(text, kinds is null ? limit : Math.Min(100, limit * 4));
            var filtered = hits
                .Where(hit => kinds is null || kinds.Contains(hit.Kind))
                .Take(limit)
                .Select(hit => new
                {
                    id = hit.Id,
                    kind = hit.Kind.ToString(),
                    name = hit.Name,
                    synonym = hit.Synonym,
                    metadataKind = hit.MetadataKind,
                    file = hit.SourcePath,
                    incoming = hit.IncomingCount,
                    outgoing = hit.OutgoingCount,
                })
                .ToList();

            // Вложенные объекты (реквизиты, табличные части) в графе не представлены, но есть в модели
            // метаданных или в таблицах состава индекса — иначе по имени реквизита ничего не находится.
            var nested = includeNested ? query.SearchNested(text, limit, metadataKinds) : [];
            var nestedView = nested
                .Select(static hit => new
                {
                    id = hit.Id,
                    kind = hit.Kind,
                    name = hit.Name,
                    synonym = hit.Synonym,
                    objectId = hit.ObjectId,
                    parent = hit.ParentId,
                    types = hit.Types,
                })
                .ToList();

            var results = new List<object>(filtered);
            if (results.Count == 0 && nestedView.Count == 0 && Session.GetIndexReader() is { } reader)
            {
                // Запасной путь: смысловой поиск по термам — имя по частям, комментарий, параметры.
                // Он нужен там, где совпадения по имени нет вовсе («посчитает налог» → процедура).
                foreach (var symbol in reader.SmartSearch(text, limit))
                {
                    results.Add(new
                    {
                        id = symbol.NodeId,
                        kind = "Routine",
                        name = symbol.Name,
                        synonym = (string?)null,
                        metadataKind = (string?)null,
                        file = symbol.ModulePath,
                        incoming = (int?)null,
                        outgoing = (int?)null,
                    });
                }
            }

            if (results.Count == 0 && nestedView.Count == 0)
            {
                return $"Ничего не найдено по запросу «{text}». Попробуйте часть имени, синоним или путь файла.";
            }

            return Render.JsonOf(new
            {
                query = text,
                found = results.Count,
                total = hits.Count,
                results,
                nestedFound = nestedView.Count,
                nested = nestedView,
            });
        });

    private ToolSpec SimilarTool() => new(
        "similar",
        "Поиск похожих реализаций в конфигурации: по черновику кода (text) или по уже существующей процедуре (id/path) "
        + "находит процедуры с теми же вызовами методов платформы, обращениями к объектам метаданных (в том числе "
        + "из текстов запросов), вызовами процедур конфигурации и близким именем. Вызовы процедур сравниваются по "
        + "идентификатору цели, поэтому квалификация вызова («ОбщийМодуль.Метод» и «Метод» внутри того же модуля) "
        + "на совпадение не влияет. Вызовы, цель которых не разрешилась, дают только слабое совпадение по имени "
        + "метода. Нужен, чтобы найти готовую реализацию и переиспользовать её вместо написания заново: код "
        + "кандидата открывается инструментом code. Работает по индексу выгрузки и отвечает за доли секунды.",
        [
            new ToolParameter("text", "string", "Черновик кода (процедура целиком), которого ещё нет в выгрузке."),
            new ToolParameter("id", "string", "Идентификатор существующей процедуры: routine:module:…#Имя."),
            new ToolParameter("path", "string", "Путь модуля выгрузки, если идентификатора нет."),
            new ToolParameter("line", "integer", "Строка внутри модуля: процедура определяется по ней."),
            new ToolParameter("limit", "integer", "Сколько кандидатов вернуть (1–50, по умолчанию 10)."),
        ],
        (arguments, token) => SimilarAsync(arguments, token));

    /// <summary>
    /// Похожие реализации: признаки черновика сравниваются с признаками процедур из индекса.
    /// Индекс для этого обязателен — по нему видно, кто что вызывает и к чему обращается.
    /// </summary>
    private async Task<string> SimilarAsync(ToolArguments arguments, CancellationToken cancellationToken)
    {
        var draft = arguments.GetString("text");
        var id = arguments.GetString("id");
        var path = arguments.GetString("path");
        if (draft is null && id is null && path is null)
        {
            throw new ToolException("Укажите text (черновик кода), id или path существующей процедуры.");
        }

        var limit = arguments.GetInt("limit", 10, 1, 50);
        var reader = Session.GetIndexReader();
        if (reader is null)
        {
            // Индекс собирается при первом запросе к графу: подождём его так же, как это делают search и check.
            _ = await QueryAsync(cancellationToken).ConfigureAwait(false);
            reader = Session.GetIndexReader();
        }

        if (reader is null)
        {
            throw new ToolException(
                "Поиск похожего кода работает по SQLite-индексу выгрузки, а индекса пока нет: он собирается в фоне "
                + "(готовность показывает status). Если выгрузка открыта каталогом на диске, дождитесь сборки и повторите запрос.");
        }

        var source = "text";
        string? excludeId = null;
        string? draftModule = null;
        if (draft is null)
        {
            var (text, routineId, modulePath) = ReadRoutine(reader, id, path, arguments.GetInt("line", 0, 0, int.MaxValue));
            draft = text;
            excludeId = routineId;
            draftModule = modulePath;
            source = id is null ? "path" : "id";
        }

        // Признак вызова процедуры конфигурации — идентификатор разрешённой цели, поэтому вызовы
        // черновика разрешает индекс: по модулю черновика или по имени общего модуля.
        var candidatesSource = reader.SimilarCodeSource(draftModule);
        var found = new SimilarCode(candidatesSource).Find(
            SimilarCode.Describe(draft, draftModule, candidatesSource.ResolveCall),
            limit,
            excludeId);

        var candidates = found.Candidates
            .Select(static candidate => new
            {
                id = candidate.Id,
                name = candidate.Name,
                module = candidate.ModulePath,
                owner = candidate.OwnerId,
                lines = $"{candidate.StartLine}-{candidate.EndLine}",
                lineCount = candidate.Lines,
                statements = candidate.Statements,
                score = Math.Round(candidate.Score, 3),
                matches = candidate.Matches
                    .GroupBy(static match => match.Signal)
                    .Select(group => new
                    {
                        signal = SignalName(group.Key),
                        values = group
                            .Select(match => FeatureText(group.Key, match.Value, match.Detail))
                            .Take(6)
                            .ToList(),
                    })
                    .ToList(),
                why = candidate.Reason,
            })
            .ToList();

        if (candidates.Count == 0)
        {
            var explanation = found.Notes.Count > 0 ? " " + string.Join(" ", found.Notes) : string.Empty;
            return $"Похожих реализаций не найдено.{explanation} Попробуйте другую формулировку кода: "
                + "в нём не нашлось ни редких вызовов, ни обращений к метаданным, ни близких термов имени.";
        }

        return Render.JsonOf(new
        {
            source,
            draft = new
            {
                name = found.Draft.Name.Length > 0 ? found.Draft.Name : null,
                lines = found.Draft.Lines,
                statements = found.Draft.Statements,
                // Показываются только те признаки, которые действительно есть в конфигурации:
                // «выбрать», «заполнить» — это вызовы своего же модуля, а не методы платформы.
                platformCalls = Recognized(found, SimilarCodeSignal.PlatformCall, found.Draft.PlatformCalls),
                metadataReferences = Recognized(found, SimilarCodeSignal.MetadataReference, found.Draft.MetadataReferences),
                routineCalls = Recognized(found, SimilarCodeSignal.RoutineCall, found.Draft.RoutineCalls),
                // Вызовы с неразрешённой целью: их сравнивает только слабый сигнал, поэтому они
                // показаны отдельно — по ним точного совпадения с кандидатом нет.
                unresolvedCalls = Recognized(found, SimilarCodeSignal.UnresolvedCall, found.Draft.UnresolvedCalls),
                terms = found.Draft.Terms,
            },
            considered = found.Considered,
            found = candidates.Count,
            notes = found.Notes.Count > 0 ? found.Notes : null,
            candidates,
            hint = "Кандидаты — это места, где та же задача уже решена. Откройте код подходящего "
                + "инструментом code (id из ответа) и переиспользуйте его вместо написания заново.",
        });
    }

    /// <summary>
    /// Признаки черновика, которые нашлись в конфигурации. Если источник их не отметил
    /// (например, поиск идёт по разбору в памяти), показываются все собранные признаки.
    /// </summary>
    private static IReadOnlyList<string> Recognized(
        SimilarCodeResult found,
        SimilarCodeSignal signal,
        IReadOnlyList<SimilarCodeFeature> features)
    {
        var known = found.Recognized is { } recognized
            && recognized.TryGetValue(signal, out var values)
            && values.Count > 0
                ? new HashSet<string>(values, StringComparer.OrdinalIgnoreCase)
                : null;

        return
        [
            .. features
                .Where(feature => known is null || known.Contains(feature.Value))
                .Select(feature => FeatureText(signal, feature.Value, feature.Detail))
        ];
    }

    /// <summary>
    /// Как показать признак в ответе. У вызова процедуры значение — идентификатор цели, и агенту
    /// полезнее текст вызова, как он записан в коде; у неразрешённого вызова значение и есть имя
    /// метода, поэтому показывается текст вызова. Остальные признаки показываются со своим
    /// уточнением (у обращения к метаданным это контекст — <c>code</c> или <c>query</c>).
    /// </summary>
    private static string FeatureText(SimilarCodeSignal signal, string value, string? detail) => signal switch
    {
        SimilarCodeSignal.RoutineCall or SimilarCodeSignal.UnresolvedCall =>
            string.IsNullOrEmpty(detail) ? value : detail,
        _ => detail is null ? value : $"{value} [{detail}]",
    };

    /// <summary>Текст существующей процедуры: по идентификатору узла, по пути модуля или по строке.</summary>
    private (string Text, string Id, string ModulePath) ReadRoutine(IndexReader reader, string? id, string? path, int line)
    {
        if (path is null)
        {
            var node = reader.GetNode(id!) ?? throw new ToolException(
                $"Узел «{id}» не найден. Уточните идентификатор инструментом search.");
            path = node.SourcePath ?? throw new ToolException(
                $"У узла «{id}» нет файла модуля: сравнить его код нельзя.");
        }

        path = DumpPath.Normalize(path);
        SymbolRow? symbol = null;
        if (id is not null && id.StartsWith("routine:", StringComparison.Ordinal))
        {
            symbol = reader
                .FindSymbolsInModule(path)
                .FirstOrDefault(item => string.Equals(item.NodeId, id, StringComparison.Ordinal));
        }

        if (symbol is null && line > 0)
        {
            symbol = reader.FindSymbolAt(path, line);
        }

        symbol ??= reader.FindSymbolsInModule(path).FirstOrDefault()
            ?? throw new ToolException($"В модуле «{path}» не нашлось ни одной процедуры.");

        var fragment = Session.Code.Read(symbol.ModulePath, symbol.StartLine, symbol.EndLine)
            ?? throw new ToolException($"Файл «{symbol.ModulePath}» не найден в выгрузке или не является текстовым.");

        return (string.Join('\n', fragment.Lines), symbol.NodeId, symbol.ModulePath);
    }

    /// <summary>Имя сигнала похожести для ответа агента.</summary>
    private static string SignalName(SimilarCodeSignal signal) => signal switch
    {
        SimilarCodeSignal.PlatformCall => "platformCalls",
        SimilarCodeSignal.MetadataReference => "metadataReferences",
        SimilarCodeSignal.RoutineCall => "routineCalls",
        SimilarCodeSignal.Terms => "terms",
        SimilarCodeSignal.UnresolvedCall => "unresolvedCallsWeak",
        _ => "size",
    };

    private ToolSpec GrepTool() => new(
        "grep",
        "Поиск по тексту файлов выгрузки (BSL, XML): подстрока или регулярное выражение. Ищет во всех "
        + "подключённых источниках (база и расширения) и для каждого совпадения указывает источник и "
        + "объект-владелец. Пример: найти реквизит в текстах запросов — grep pattern=\"Артикул\" paths=[\"Reports/\"].",
        [
            new ToolParameter("pattern", "string", "Что искать: подстрока или регулярное выражение.", Required: true),
            new ToolParameter("regex", "boolean", "Считать pattern регулярным выражением (по умолчанию — подстрока)."),
            new ToolParameter("ignoreCase", "boolean", "Не учитывать регистр (по умолчанию да)."),
            new ToolParameter("extensions", "array", "Расширения файлов (по умолчанию .bsl и .xml)."),
            new ToolParameter("paths", "array", "Ограничить префиксами путей: [\"Reports/\", \"Documents/Заказ/\" ]."),
            new ToolParameter("limit", "integer", "Предел числа совпадений (1–500, по умолчанию 50)."),
            new ToolParameter("context", "integer", "Сколько строк до и после совпадения показать (0–3, по умолчанию 1)."),
            new ToolParameter("waitMs", "integer", "Сколько миллисекунд ждать разбор ради имён владельцев (0–600000, по умолчанию 60000; 0 — не ждать)."),
        ],
        (arguments, cancellationToken) => GrepAsync(arguments, cancellationToken));

    private ToolSpec NodeTool() => new(
        "node",
        "Карточка узла конфигурации: вид, имя, файл, теги и связи со строками кода. "
        + "Кто вызывает процедуру и что вызывает она сама — в neighbors с edgeKinds=[\"Calls\"]. "
        + "У объекта метаданных показана короткая сводка обращений (usages): сколько раз и в каком "
        + "контексте его читают, а читатели и примеры строк — в metadata.",
        [
            new ToolParameter("id", "string", "Идентификатор узла: Catalog.Товары, module:CommonModules/.../Module.bsl, routine:module:...#Имя.", Required: true),
            new ToolParameter("edges", "integer", "Сколько связей показать в каждую сторону (1–200, по умолчанию 40)."),
        ],
        async (arguments, token) =>
        {
            var query = await QueryAsync(token);
            var id = arguments.RequireString("id");
            var edges = arguments.GetInt("edges", 40, 1, 200);

            var details = query.GetNode(id) ?? throw new ToolException(
                $"Узел «{id}» не найден. Уточните идентификатор инструментом search.");

            var incoming = details.Incoming.Take(edges).Select(edge => EdgeView(edge, edge.SourceId, query)).ToList();
            var outgoing = details.Outgoing.Take(edges).Select(edge => EdgeView(edge, edge.TargetId, query)).ToList();

            // Сводка обращений нужна только объектам метаданных: обращения адресуются именно им,
            // а полный список читателей и примеров отдаёт metadata. Списки здесь не нужны — только
            // счётчики, поэтому лимит примеров минимальный.
            var usages = details.Node.Kind == GraphNodeKind.MetadataObject
                ? UsageBrief(query.GetMetadataUsages(id, limit: 1), id)
                : null;

            return Render.JsonOf(new
            {
                node = NodeView(details.Node),
                incomingCount = details.Incoming.Count,
                outgoingCount = details.Outgoing.Count,
                usages,
                incoming,
                outgoing,
            });
        });

    private ToolSpec NeighborsTool() => new(
        "neighbors",
        "Окружение узла в графе: кто вызывает и что вызывается на заданной глубине. "
        + "Для поиска вызовов процедуры укажите edgeKinds=[\"Calls\"], direction=\"in\".",
        [
            new ToolParameter("id", "string", "Идентификатор узла (см. search).", Required: true),
            new ToolParameter("depth", "integer", "Радиус обхода в шагах связей (1–4, по умолчанию 1)."),
            new ToolParameter("maxNodes", "integer", "Предел числа узлов в ответе (1–500, по умолчанию 60)."),
            new ToolParameter("direction", "string", "Направление связей.", Values: ["both", "in", "out"]),
            new ToolParameter(
                "edgeKinds",
                "array",
                "Типы связей: Calls (вызов), Defines (объявление), UsesMetadata (обращение к метаданным), References, Contains.",
                Values: ["Calls", "Defines", "UsesMetadata", "References", "Contains"]),
            new ToolParameter(
                "nodeKinds",
                "array",
                "Виды узлов, которые попадут в ответ.",
                Values: ["Configuration", "MetadataObject", "Module", "Routine", "External", "Platform"]),
        ],
        async (arguments, token) =>
        {
            var query = await QueryAsync(token);
            var id = arguments.RequireString("id");
            var direction = arguments.GetString("direction") ?? "both";

            var request = new GraphNeighborhoodRequest
            {
                NodeId = id,
                Depth = arguments.GetInt("depth", 1, 1, 4),
                MaxNodes = arguments.GetInt("maxNodes", 60, 1, 500),
                Incoming = direction is not "out",
                Outgoing = direction is not "in",
                EdgeKinds = ParseEnums<GraphEdgeKind>(arguments.GetStringList("edgeKinds")),
                NodeKinds = ParseKinds(arguments.GetStringList("nodeKinds")),
            };

            var neighborhood = query.GetNeighborhood(request);
            if (neighborhood.Nodes.Count == 0)
            {
                throw new ToolException($"Узел «{id}» не найден. Уточните идентификатор инструментом search.");
            }

            return Render.JsonOf(new
            {
                center = id,
                truncated = neighborhood.Truncated,
                nodes = neighborhood.Nodes.Select(NodeView).ToList(),
                edges = neighborhood.Edges.Select(edge => new
                {
                    kind = edge.Kind.ToString(),
                    from = edge.SourceId,
                    to = edge.TargetId,
                    line = edge.Line,
                }).ToList(),
            });
        });

    private ToolSpec CodeTool() => new(
        "code",
        "Исходный текст модуля BSL или XML объекта из выгрузки, с номерами строк. "
        + "Для процедуры по умолчанию отдаёт её тело с окружением; после правки файла вызывайте reload.",
        [
            new ToolParameter("id", "string", "Идентификатор узла (модуль, процедура или объект метаданных)."),
            new ToolParameter("path", "string", "Путь файла внутри выгрузки, если идентификатора нет."),
            new ToolParameter("from", "integer", "Первая строка (1-based)."),
            new ToolParameter("to", "integer", "Последняя строка (1-based)."),
            new ToolParameter("all", "boolean", "Вернуть файл целиком (до предела сервера)."),
        ],
        async (arguments, token) =>
        {
            var path = arguments.GetString("path");
            var from = arguments.GetInt("from", 0, 0, int.MaxValue);
            var to = arguments.GetInt("to", 0, 0, int.MaxValue);
            var all = arguments.GetBool("all", false);
            var highlightFrom = 0;
            var highlightTo = 0;
            var title = path;

            if (path is null)
            {
                var query = await QueryAsync(token);
                var id = arguments.RequireString("id");
                var node = query.FindNode(id) ?? throw new ToolException(
                    $"Узел «{id}» не найден. Уточните идентификатор инструментом search.");

                path = node.SourcePath;
                title = node.Id;
                if (path is null)
                {
                    throw new ToolException($"У узла «{id}» нет файла в выгрузке: код для него недоступен.");
                }

                if (node.Kind == GraphNodeKind.Routine)
                {
                    highlightFrom = TagInt(node, "startLine");
                    highlightTo = highlightFrom > 0 ? highlightFrom + Math.Max(1, TagInt(node, "lines")) - 1 : 0;
                }
            }

            if (all)
            {
                from = 1;
                to = 0;
            }
            else
            {
                if (from <= 0)
                {
                    from = highlightFrom > 0 ? Math.Max(1, highlightFrom - 15) : 1;
                }

                if (to <= 0)
                {
                    to = highlightTo > 0 ? highlightTo + 15 : 400;
                }
            }

            var fragment = Session.Code.Read(path, from, to) ?? throw new ToolException(
                $"Файл «{path}» не найден в выгрузке или не является текстовым.");

            var text = new StringBuilder();
            text.Append(CultureInfo.InvariantCulture, $"файл: {fragment.Path}\n");
            text.Append(CultureInfo.InvariantCulture, $"строки: {fragment.StartLine}-{fragment.EndLine} из {fragment.TotalLines}\n");
            if (title is not null)
            {
                text.Append(CultureInfo.InvariantCulture, $"узел: {title}\n");
            }

            if (highlightFrom > 0)
            {
                text.Append(CultureInfo.InvariantCulture, $"процедура: строки {highlightFrom}-{highlightTo}\n");
            }

            if (fragment.Truncated)
            {
                text.Append("фрагмент обрезан пределом чтения; запросите диапазон через from/to\n");
            }

            text.Append('\n');
            for (var index = 0; index < fragment.Lines.Count; index++)
            {
                var number = fragment.StartLine + index;
                text.Append(CultureInfo.InvariantCulture, $"{number,5}: {fragment.Lines[index]}\n");
            }

            return Render.Truncate(text.ToString());
        });

    private ToolSpec MetadataTool() => new(
        "metadata",
        "Состав объекта метаданных 1С деревом: реквизиты, табличные части и их реквизиты, формы, команды, "
        + "типы, модули. У формы дополнительно показаны её реквизиты, элементы с привязкой DataPath, "
        + "команды и обработчики событий с именами процедур модуля формы — запросите саму форму, "
        + "например id=\"Catalog.Товары/Form.ФормаЭлемента\"; списки формы ограничивает maxChildren. "
        + "Раздел usages показывает, кто и где читает объект: счётчики по контекстам (в коде, в запросах, "
        + "в типах, в составе) и видам связи, топ модулей-читателей и примеры обращений со строками — "
        + "по ним агент находит образцы работы с объектом. "
        + "Нужен, чтобы писать код по реальной структуре объекта. Пример: id=\"Catalog.Товары\".",
        [
            new ToolParameter("id", "string", "Идентификатор: Catalog.Товары, Document.Заказ, Document.Заказ/TabularSection.Строки.", Required: true),
            new ToolParameter("depth", "integer", "Глубина дерева состава (1–4, по умолчанию 3)."),
            new ToolParameter("maxChildren", "integer", "Сколько детей показывать у одного узла (1–500, по умолчанию 200)."),
            new ToolParameter("usages", "integer", "Сколько обращений показать в разделе usages (1–500, по умолчанию 20); счётчики всегда полные."),
        ],
        async (arguments, token) =>
        {
            var query = await QueryAsync(token);
            var id = arguments.RequireString("id");
            var depth = arguments.GetInt("depth", 3, 1, 4);
            var maxChildren = arguments.GetInt("maxChildren", 200, 1, 500);
            var usages = arguments.GetInt("usages", 20, 1, 500);

            var card = query.GetMetadata(id, depth, maxChildren);
            if (card is null)
            {
                var candidates = query.Search(id, 10).Select(hit => hit.Id).ToList();
                var hint = candidates.Count > 0 ? " Похожие идентификаторы: " + string.Join(", ", candidates) : string.Empty;
                throw new ToolException($"Объект метаданных «{id}» не найден.{hint}");
            }

            // Обращения к объекту идут в начало карточки, до дерева состава: у крупных объектов
            // ответ обрезается пределом длины, и раздел usages должен пережить обрезку.
            var usageView = UsageView(query.GetMetadataUsages(card.Id, usages));

            var root = MetadataNode(card, usageView);
            root["uuid"] = card.Uuid;
            root["file"] = card.SourcePath;
            root["isTopLevel"] = card.IsTopLevel;
            root["parent"] = card.ParentId;

            var properties = new JsonObject();
            foreach (var property in card.Properties)
            {
                properties[property.Key] = property.Value;
            }

            root["properties"] = properties.Count > 0 ? properties : null;

            var references = new JsonArray();
            foreach (var reference in card.References)
            {
                references.Add(new JsonObject
                {
                    ["kind"] = reference.Kind,
                    ["target"] = reference.Target,
                    ["detail"] = reference.Detail,
                });
            }

            root["references"] = references.Count > 0 ? references : null;

            var modules = new JsonArray();
            foreach (var path in card.ModulePaths)
            {
                modules.Add(new JsonObject { ["path"] = path });
            }

            root["modules"] = modules.Count > 0 ? modules : null;

            // Описание формы (Ext/Form.xml) добавляется только форме: у остальных объектов его нет,
            // а существующие поля ответа не меняются.
            if (card.Form is { } form)
            {
                root["form"] = FormView(form, maxChildren);
            }

            return Render.JsonOf(root);
        });

    /// <summary>
    /// Раздел ответа «usages»: сколько раз и в каком контексте читают объект, кто читает и где
    /// это видно. Счётчики берутся из полного набора обращений, а списки уже обрезаны лимитом.
    /// </summary>
    private static JsonObject UsageView(MetadataUsageSummary usage)
    {
        var view = new JsonObject
        {
            ["total"] = usage.Total,
        };

        if (usage.ByContext.Count > 0)
        {
            view["byContext"] = ContextsView(usage.ByContext);
        }

        if (usage.ByKind.Count > 0)
        {
            var kinds = new JsonArray();
            foreach (var group in usage.ByKind)
            {
                kinds.Add(new JsonObject
                {
                    ["kind"] = group.Name,
                    ["count"] = group.Count,
                });
            }

            view["byKind"] = kinds;
        }

        if (usage.Readers.Count > 0)
        {
            var readers = new JsonArray();
            foreach (var reader in usage.Readers)
            {
                readers.Add(UsageItemView(
                    reader.SourceId,
                    reader.Name,
                    reader.File,
                    reader.Count,
                    reader.Context,
                    reader.Line,
                    reader.Detail));
            }

            view["readers"] = readers;
        }

        if (usage.Items.Count > 0)
        {
            var items = new JsonArray();
            foreach (var item in usage.Items)
            {
                items.Add(UsageItemView(item.SourceId, item.Name, item.File, null, item.Context, item.Line, item.Detail));
            }

            view["items"] = items;
        }

        view["shown"] = usage.Shown;
        return view;
    }

    /// <summary>Короткая сводка обращений для карточки узла: счётчики по контекстам и ссылка на metadata.</summary>
    private static JsonObject UsageBrief(MetadataUsageSummary usage, string id)
    {
        var brief = new JsonObject
        {
            ["total"] = usage.Total,
        };

        if (usage.ByContext.Count > 0)
        {
            brief["byContext"] = ContextsView(usage.ByContext);
        }

        if (usage.Total > 0)
        {
            brief["hint"] = $"Подробнее — metadata с id=\"{id}\": читатели и примеры обращений.";
        }

        return brief;
    }

    /// <summary>Разбивка по контекстам с человеческими подписями: «в коде», «в запросах», «в типах».</summary>
    private static JsonArray ContextsView(IReadOnlyList<MetadataUsageCount> contexts)
    {
        var view = new JsonArray();
        foreach (var group in contexts)
        {
            view.Add(new JsonObject
            {
                ["context"] = group.Name,
                ["label"] = MetadataRefContexts.Label(group.Name),
                ["count"] = group.Count,
            });
        }

        return view;
    }

    /// <summary>Одно обращение или читатель в ответе: кто, где и что именно.</summary>
    private static JsonObject UsageItemView(
        string sourceId,
        string? name,
        string? file,
        int? count,
        string context,
        int? line,
        string? detail)
    {
        var item = new JsonObject
        {
            ["source"] = sourceId,
            ["context"] = context,
        };

        // У модуля имя совпадает с путём файла: второй раз оно не нужно, а у процедуры — нужно.
        if (!string.IsNullOrWhiteSpace(name) && !string.Equals(name, file, StringComparison.Ordinal))
        {
            item["name"] = name;
        }

        if (!string.IsNullOrWhiteSpace(file))
        {
            item["file"] = file;
        }

        if (count is { } readerCount)
        {
            item["count"] = readerCount;
        }

        if (line is { } number)
        {
            item["line"] = number;
        }

        if (!string.IsNullOrWhiteSpace(detail))
        {
            item["detail"] = detail;
        }

        return item;
    }

    /// <summary>
    /// Раздел ответа «form»: состав формы так, как его видит агент. Списки ограничены тем же пределом,
    /// что и дерево состава, а полные размеры остаются в полях-счётчиках.
    /// </summary>
    private static JsonObject FormView(FormModel form, int maxChildren)
    {
        var view = new JsonObject
        {
            ["name"] = form.Name,
            ["kind"] = form.Kind switch
            {
                FormKind.Managed => "Managed",
                FormKind.Ordinary => "Ordinary",
                _ => null,
            },
            ["file"] = form.SourcePath,
            ["attributesCount"] = form.Attributes.Count,
            ["elementsCount"] = form.Elements.Count,
            ["commandsCount"] = form.Commands.Count,
            ["handlersCount"] = form.Handlers.Count,
            ["handlersResolved"] = form.Handlers.Count(static handler => handler.Resolved),
        };

        if (form.Attributes.Count > 0)
        {
            var attributes = new JsonArray();
            foreach (var attribute in form.Attributes.Take(maxChildren))
            {
                var types = new JsonArray();
                foreach (var type in attribute.Types)
                {
                    types.Add(type);
                }

                attributes.Add(new JsonObject
                {
                    ["name"] = attribute.Name,
                    ["types"] = types.Count > 0 ? types : null,
                    ["main"] = attribute.IsMain ? true : null,
                });
            }

            view["attributes"] = attributes;
            if (form.Attributes.Count > maxChildren)
            {
                view["attributesTruncated"] = form.Attributes.Count;
            }
        }

        if (form.Elements.Count > 0)
        {
            var elements = new JsonArray();
            foreach (var element in form.Elements.Take(maxChildren))
            {
                elements.Add(new JsonObject
                {
                    ["name"] = element.Name,
                    ["kind"] = element.Kind,
                    ["dataPath"] = element.DataPath,
                    ["attribute"] = element.Attribute,
                    ["command"] = element.CommandName,
                });
            }

            view["elements"] = elements;
            if (form.Elements.Count > maxChildren)
            {
                view["elementsTruncated"] = form.Elements.Count;
            }
        }

        if (form.Commands.Count > 0)
        {
            var commands = new JsonArray();
            foreach (var command in form.Commands.Take(maxChildren))
            {
                commands.Add(new JsonObject
                {
                    ["name"] = command.Name,
                    ["handler"] = command.Handler,
                    ["commandName"] = command.CommandName,
                });
            }

            view["commands"] = commands;
            if (form.Commands.Count > maxChildren)
            {
                view["commandsTruncated"] = form.Commands.Count;
            }
        }

        if (form.Handlers.Count > 0)
        {
            var handlers = new JsonArray();
            foreach (var handler in form.Handlers.Take(maxChildren))
            {
                handlers.Add(new JsonObject
                {
                    ["event"] = handler.Event,
                    ["element"] = handler.Element,
                    ["procedure"] = handler.Procedure,
                    ["line"] = handler.Line,
                    ["resolved"] = handler.Resolved,
                });
            }

            view["handlers"] = handlers;
            if (form.Handlers.Count > maxChildren)
            {
                view["handlersTruncated"] = form.Handlers.Count;
            }
        }

        return view;
    }

    /// <summary>
    /// Узел дерева состава объекта: сам объект, его типы и дети. Вложенность важна для табличных частей —
    /// плоский список реквизитов не показывает, какие из них относятся к табличной части, а какие к объекту.
    /// Карточка уже ограничена глубиной, поэтому здесь только сборка JSON.
    /// </summary>
    /// <param name="card">Карточка объекта метаданных.</param>
    /// <param name="usages">
    /// Раздел обращений, который добавляется только корню карточки — до детей, чтобы он не терялся
    /// при обрезке длинного ответа. У вложенных узлов его нет.
    /// </param>
    private static JsonObject MetadataNode(MetadataCard card, JsonObject? usages = null)
    {
        var node = new JsonObject
        {
            ["id"] = card.Id,
            ["kind"] = card.Kind,
            ["name"] = card.Name,
        };

        if (!string.IsNullOrWhiteSpace(card.Synonym))
        {
            node["synonym"] = card.Synonym;
        }

        if (!string.IsNullOrWhiteSpace(card.Comment))
        {
            node["comment"] = card.Comment;
        }

        if (card.Types.Count > 0)
        {
            var types = new JsonArray();
            foreach (var type in card.Types)
            {
                types.Add(type);
            }

            node["types"] = types;
        }

        if (usages is not null)
        {
            node["usages"] = usages;
        }

        if (card.Children.Count > 0)
        {
            var children = new JsonArray();
            foreach (var child in card.Children)
            {
                children.Add(MetadataNode(child));
            }

            node["children"] = children;
        }

        if (card.ChildrenNotShown > 0)
        {
            node[card.Children.Count > 0 ? "childrenTruncated" : "childrenCount"] = card.ChildrenNotShown;
        }

        return node;
    }

    /// <summary>Сколько источников подписки показывать: у «регистрации удаления» их сотни.</summary>
    private const int SourcePreviewLimit = 20;

    /// <summary>Сколько свойств объекта показывать в ответе.</summary>
    private const int PropertyPreviewLimit = 12;

    private ToolSpec EntryPointsTool() => new(
        "entrypoints",
        "Точки входа: код, который вызывает платформа, а не другая процедура конфигурации — подписки на события, "
        + "регламентные задания и обработчики событий форм. Видны обработчик (путь модуля, процедура, строка), "
        + "узел процедуры для инструмента code и объекты метаданных, на которые точка входа реагирует: "
        + "правку обработчика видно с её последствиями. Пример: entrypoints metadata=\"Catalog.Товары\".",
        [
            new ToolParameter(
                "metadata",
                "string",
                "Кто реагирует на изменения этого объекта: объект-источник подписки, общий модуль-обработчик, "
                + "форма или объект-владелец формы (Catalog.Товары, CommonModule.ОбщегоНазначения)."),
            new ToolParameter(
                "kind",
                "string",
                "Оставить только эти точки входа.",
                Values: ["subscription", "job", "form"]),
            new ToolParameter(
                "limit",
                "integer",
                $"Сколько точек входа вернуть ({1}–{EntryPoints.MaxLimit}, по умолчанию {EntryPoints.DefaultLimit})."),
        ],
        async (arguments, token) =>
        {
            if (!Session.IsOpen)
            {
                throw new ToolException("Выгрузка не открыта: вызовите open с путём к каталогу выгрузки 1С.");
            }

            var metadata = arguments.GetString("metadata");
            var kind = ParseEntryPointKind(arguments.GetString("kind"));
            var limit = arguments.GetInt("limit", EntryPoints.DefaultLimit, 1, EntryPoints.MaxLimit);

            var facts = await EntryPointFactsAsync(token).ConfigureAwait(false);
            if (facts is null)
            {
                return "Разбор выгрузки ещё идёт: точки входа появятся, когда он закончится. "
                    + "Повторите запрос через несколько секунд (status покажет готовность).";
            }

            var report = EntryPoints.Collect(facts, new EntryPointFilter(metadata, kind, limit));
            if (report.Items.Count == 0)
            {
                return EmptyEntryPointsMessage(metadata, kind);
            }

            return Render.JsonOf(new
            {
                metadata,
                kind = kind is { } value ? EntryPoints.KindName(value) : null,
                limit,
                found = report.Items.Count,
                truncated = report.Truncated ? true : (bool?)null,
                counts = new
                {
                    subscription = report.CountsByKind[EntryPointKind.Subscription],
                    job = report.CountsByKind[EntryPointKind.Job],
                    form = report.CountsByKind[EntryPointKind.Form],
                },
                note = EntryPointsNote(report),
                entryPoints = report.Items.Select(item => EntryPointView(item, metadata)).ToList(),
            });
        });

    /// <summary>
    /// Раздел ответа: одна точка входа так, как её видит агент. Списки источников и свойств
    /// ограничены — подписка на «регистрацию удаления» иначе занимает весь ответ, — а полные
    /// размеры остаются в полях-счётчиках. Источник, по которому сработал фильтр, показывается первым.
    /// </summary>
    private static object EntryPointView(EntryPointInfo item, string? metadata)
    {
        var sources = SourcePreview(item.Sources, metadata);

        return new
        {
            kind = EntryPoints.KindName(item.Kind),
            id = item.Id,
            name = item.Name,
            objectId = item.ObjectId,
            objectKind = item.ObjectKind,
            // event — ключевое слово C#: в ответе поле называется «event», поэтому идентификатор взят дословно.
            @event = item.Event,
            eventName = item.EventName,
            element = item.Element,
            sources,
            sourcesCount = item.Sources.Count > SourcePreviewLimit ? item.Sources.Count : (int?)null,
            module = item.ModulePath,
            procedure = item.Procedure,
            routine = item.RoutineId,
            line = item.Line,
            resolved = item.Resolved,
            properties = item.Properties.Count == 0
                ? null
                : item.Properties.Take(PropertyPreviewLimit).ToDictionary(static property => property.Key, static property => property.Value),
            file = item.SourcePath,
        };
    }

    /// <summary>
    /// Первые источники подписки для ответа: объект из фильтра идёт первым, чтобы при сотнях
    /// источников было видно, почему точка входа попала в выдачу.
    /// </summary>
    private static List<string>? SourcePreview(IReadOnlyList<string> sources, string? metadata)
    {
        if (sources.Count == 0)
        {
            return null;
        }

        var preview = new List<string>(Math.Min(sources.Count, SourcePreviewLimit));
        if (metadata is { Length: > 0 } filter && sources.Contains(filter, StringComparer.Ordinal))
        {
            preview.Add(filter);
        }

        foreach (var source in sources)
        {
            if (preview.Count >= SourcePreviewLimit)
            {
                break;
            }

            if (!preview.Contains(source, StringComparer.Ordinal))
            {
                preview.Add(source);
            }
        }

        return preview;
    }

    /// <summary>Что осталось за пределами ответа: обрезка по пределу и ненайденные процедуры-обработчики.</summary>
    private static string? EntryPointsNote(EntryPointReport report)
    {
        var parts = new List<string>(2);
        if (report.Truncated)
        {
            parts.Add("Точек входа больше, чем показано: увеличьте limit или сузьте выборку аргументами kind и metadata.");
        }

        if (report.Items.Any(static item => !item.Resolved))
        {
            parts.Add("У части точек входа процедура-обработчик в модуле не найдена: узел процедуры пуст, проверьте имя процедуры в файле.");
        }

        return parts.Count == 0 ? null : string.Join(' ', parts);
    }

    /// <summary>
    /// Источник точек входа: индекс, если он готов, иначе разбор в памяти. Ожидание разбора короткое —
    /// инструмент не должен висеть, пока собирается индекс большой выгрузки.
    /// </summary>
    private async Task<IEntryPointFacts?> EntryPointFactsAsync(CancellationToken cancellationToken)
    {
        if (Session.GetIndexReader() is { } reader)
        {
            return new IndexEntryPoints(reader);
        }

        var result = Session.Result ?? await WaitForAnalysisAsync(cancellationToken).ConfigureAwait(false);
        return result is null ? null : new AnalysisEntryPoints(result);
    }

    /// <summary>Ответ, когда подходящих точек входа нет: без него агент не отличит пустоту от ошибки фильтра.</summary>
    private static string EmptyEntryPointsMessage(string? metadata, EntryPointKind? kind)
    {
        var what = kind switch
        {
            EntryPointKind.Subscription => "подписок на события",
            EntryPointKind.Job => "регламентных заданий",
            EntryPointKind.Form => "обработчиков событий форм",
            _ => "точек входа (подписок на события, регламентных заданий, обработчиков событий форм)",
        };

        return metadata is null
            ? $"В выгрузке не найдено {what}."
            : $"По объекту «{metadata}» не найдено {what}. Убедитесь, что это идентификатор объекта метаданных "
                + "(подскажет search, например Catalog.Товары), или уберите аргумент metadata.";
    }

    /// <summary>Вид точек входа из аргумента инструмента: subscription, job, form.</summary>
    private static EntryPointKind? ParseEntryPointKind(string? value)
    {
        if (value is null)
        {
            return null;
        }

        if (EntryPoints.TryParseKind(value, out var kind))
        {
            return kind;
        }

        throw new ToolException(
            $"Недопустимое значение kind: «{value}». Возможные: subscription (подписки на события), "
            + "job (регламентные задания), form (обработчики событий форм).");
    }

    private ToolSpec PlatformTool() => new(
        "platform",
        "Справка установленной платформы 1С по методам и типам (синтакс-помощник из .hbk): "
        + "точное имя, синтаксис и описание. Нужна, чтобы писать корректный BSL. Требует запуска с ключом --platform.",
        [
            new ToolParameter("query", "string", "Имя метода или типа: Массив.Добавить, СтрНайти, ТаблицаЗначений.", Required: true),
            new ToolParameter("mode", "string", "find — точное имя темы, search — поиск по подстроке.", Values: ["find", "search"]),
            new ToolParameter("limit", "integer", "Сколько тем вернуть в режиме search (1–20, по умолчанию 5)."),
            new ToolParameter("full", "boolean", "Вернуть текст темы целиком (по умолчанию — начало с пометкой)."),
        ],
        async (arguments, token) =>
        {
            if (!Session.PlatformRequested)
            {
                throw new ToolException(
                    "Справка платформы не подключена. Запустите сервер с ключом --platform (нужна установленная платформа 1С).");
            }

            // Модель платформы грузится в фоне: вместо двадцати секунд ожидания внутри вызова
            // ждём готовности совсем недолго, а дальше честно говорим, что загрузка ещё идёт.
            Session.StartPlatformWarmup();
            var index = Session.Platform ?? await WaitForPlatformAsync(token).ConfigureAwait(false);
            if (index is null)
            {
                if (Session.IsPlatformLoading)
                {
                    return $"Справка платформы ещё загружается ({Session.PlatformElapsed.TotalSeconds:F0} с). "
                        + "Повторите этот же запрос через несколько секунд — дальше ответы будут мгновенными.";
                }

                throw new ToolException("Справка платформы не загрузилась: "
                    + (Session.PlatformError ?? "установленная платформа 1С не найдена."));
            }

            var query = arguments.RequireString("query");
            var mode = arguments.GetString("mode") ?? "find";
            var limit = arguments.GetInt("limit", 5, 1, 20);
            var full = arguments.GetBool("full", false);

            if (mode == "find")
            {
                var topic = index.Find(query);
                if (topic is null)
                {
                    var similar = index.Search(query, limit)
                        .Select(static item => item.Name)
                        .ToList();
                    var hint = similar.Count > 0
                        ? " Похожие темы: " + string.Join(", ", similar)
                        : index.ContainsMember(query) ? string.Empty : " Такого имени в справке нет.";
                    throw new ToolException($"Тема «{query}» не найдена.{hint}");
                }

                return TopicText(topic, full);
            }

            var topics = index.Search(query, limit);
            if (topics.Count == 0)
            {
                var known = index.IsKnownType(query) || index.KnownIdentifier(query) || index.ContainsMember(query);
                return known
                    ? $"Темы «{query}» нет, но такое имя встречается в справке платформы. Уточните имя типа или метода и повторите поиск."
                    : $"В справке платформы нет тем по запросу «{query}».";
            }

            return Render.JsonOf(new
            {
                query,
                found = topics.Count,
                topics = topics.Select(topic => new
                {
                    name = topic.Name,
                    title = topic.Title,
                    kind = topic.Kind.ToString(),
                    version = topic.Version.ToString(),
                }).ToList(),
            });
        });

    private ToolSpec CheckTool() => new(
        "check",
        "Проверка модуля BSL без запуска 1С: структура (процедуры, области, вызовы, обращения к метаданным) "
        + "и замечания с номерами строк — неизвестные процедуры, неверное число аргументов у процедур своего "
        + "модуля и методов общих модулей (лишний аргумент и недостача обязательных — ошибка; параметры "
        + "со значением по умолчанию можно не передавать), вызов неэкспортного метода общего модуля "
        + "из другого модуля, отсутствующие объекты метаданных, неиспользуемые переменные и параметры "
        + "(параметры обработчиков формы не считаются неиспользуемыми), функция без «Возврат», код после «Возврат». "
        + "Принимает path или id модуля из выгрузки либо text — черновик, которого в выгрузке ещё нет: "
        + "проверяйте text перед вставкой кода в Конфигуратор. Передавайте path и для черновика: по нему "
        + "проверка отличает свой модуль от чужого и узнаёт обработчики формы.",
        [
            new ToolParameter("path", "string", "Путь файла модуля внутри выгрузки."),
            new ToolParameter("id", "string", "Идентификатор узла, если путь неизвестен."),
            new ToolParameter("text", "string", "Текст черновика модуля: проверяется без файла в выгрузке (главный сценарий)."),
            new ToolParameter("calls", "boolean", "Показать список вызовов модуля."),
        ],
        async (arguments, token) =>
        {
            var draft = arguments.GetString("text");
            var path = arguments.GetString("path");
            if (draft is null && path is null)
            {
                var id = arguments.RequireString("id");
                var query = await QueryAsync(token);
                var node = query.FindNode(id) ?? throw new ToolException(
                    $"Узел «{id}» не найден. Уточните идентификатор инструментом search.");

                path = node.SourcePath ?? throw new ToolException($"У узла «{id}» нет файла модуля.");
            }

            // Черновик проверяется без файла на диске; path в этом случае — только подпись места,
            // куда код собираются вставить.
            var modulePath = path is null ? DraftCheck.DefaultModulePath : DumpPath.Normalize(path);
            var text = draft ?? ReadFresh(modulePath);
            var module = new BslModuleParser().Parse(new BslModuleSource(modulePath, text));
            var showCalls = arguments.GetBool("calls", false);

            // Вызовы и обращения внутри процедур лежат в описании процедуры, а не модуля,
            // поэтому для ответа агенту их нужно объединить.
            var allCalls = module.Calls
                .Select(static call => ((string?)null, call.Callee, call.Line))
                .Concat(module.Routines.SelectMany(static routine => routine.Calls
                    .Select(call => ((string?)routine.Name, call.Callee, call.Line))))
                .ToList();

            var allAccesses = module.MetadataAccesses
                .Concat(module.Routines.SelectMany(static routine => routine.MetadataAccesses))
                .ToList();

            var (checker, contextName) = await CreateDraftCheckAsync(token).ConfigureAwait(false);
            var check = checker.Check(text, modulePath);

            return Render.JsonOf(new
            {
                path = module.Path,
                source = draft is null ? "file" : "text",
                lines = module.LineCount,
                routines = module.Routines.Select(routine => new
                {
                    name = routine.Name,
                    kind = routine.Kind.ToString(),
                    export = routine.IsExport,
                    parameters = routine.Parameters,
                    lines = $"{routine.StartLine}-{routine.EndLine}",
                    region = routine.Region,
                    directives = routine.Directives,
                }).ToList(),
                regions = module.Regions.Select(static region => new
                {
                    name = region.Name,
                    lines = $"{region.StartLine}-{region.EndLine}",
                }).ToList(),
                calls = showCalls
                    ? allCalls.Take(200).Select(static call => new { routine = call.Item1, callee = call.Item2, line = call.Item3 }).ToList()
                    : null,
                callsCount = allCalls.Count,
                metadataAccesses = allAccesses.Take(100).Select(static access => new
                {
                    kind = access.Kind.ToString(),
                    name = access.ObjectName,
                    collection = access.Collection,
                    line = access.Line,
                }).ToList(),
                diagnostics = module.Diagnostics.Select(static diagnostic => new
                {
                    kind = diagnostic.Kind.ToString(),
                    message = diagnostic.Message,
                    line = diagnostic.Line,
                }).ToList(),
                problems = check.Problems.Select(static problem => new
                {
                    line = problem.Line,
                    severity = problem.Severity.ToString(),
                    code = problem.Kind.ToString(),
                    message = problem.Message,
                    hint = problem.Hint,
                }).ToList(),
                problemsCount = check.Problems.Count,
                problemsBySeverity = new
                {
                    errors = check.Count(DraftProblemSeverity.Error),
                    warnings = check.Count(DraftProblemSeverity.Warning),
                    infos = check.Count(DraftProblemSeverity.Info),
                },
                context = contextName,
                notes = check.Notes.Count > 0 ? check.Notes : null,
            });
        });

    /// <summary>
    /// Готовит проверку черновика: факты о конфигурации берутся из индекса, а если его нет — из уже
    /// готового разбора в памяти. Разбор специально не запускается: check должен отвечать быстро,
    /// но если он уже идёт, подождём совсем немного.
    /// </summary>
    private async Task<(DraftCheck Checker, string? ContextName)> CreateDraftCheckAsync(CancellationToken cancellationToken)
    {
        if (Session.GetIndexReader() is { } reader)
        {
            return (new DraftCheck(new IndexDraftContext(reader), Session.Platform), "индекс");
        }

        var result = Session.Result ?? await WaitForAnalysisAsync(cancellationToken).ConfigureAwait(false);
        return result is null
            ? (new DraftCheck(null, Session.Platform), null)
            : (new DraftCheck(new AnalysisDraftContext(result), Session.Platform), "разбор в памяти");
    }

    /// <summary>Короткое ожидание разбора, который уже идёт: иначе проверка обойдётся без конфигурации.</summary>
    private async Task<AnalysisResult?> WaitForAnalysisAsync(CancellationToken cancellationToken)
    {
        if (!Session.IsOpen || !Session.IsRunning)
        {
            return Session.Result;
        }

        try
        {
            return await Session
                .GetAsync(cancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(3), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return Session.Result;
        }
    }

    private ToolSpec TypesTool() => new(
        "types",
        "Таблица символов модуля BSL и консервативный вывод типов: объявления (переменные модуля, параметры, "
        + "локальные переменные) с позициями и областью видимости, выведенные типы, вызовы, разрешённые как методы "
        + "платформы, и места, где тип вывести не удалось. Вызывайте перед правкой кода, чтобы понимать типы "
        + "переменных. Пример: id=\"routine:module:CommonModules/ОбщегоНазначения/Ext/Module.bsl#МояПроцедура\".",
        [
            new ToolParameter("path", "string", "Путь файла модуля внутри выгрузки."),
            new ToolParameter("id", "string", "Идентификатор узла модуля или процедуры, если путь неизвестен."),
            new ToolParameter("line", "integer", "Строка модуля: дополнительно вернуть имена, видимые в этой строке, с типами."),
            new ToolParameter("limit", "integer", "Сколько объявлений, типов и вызовов показать (1–500, по умолчанию 200)."),
        ],
        async (arguments, token) =>
        {
            var path = arguments.GetString("path");
            if (path is null)
            {
                var query = await QueryAsync(token);
                var id = arguments.RequireString("id");
                var node = query.FindNode(id) ?? throw new ToolException(
                    $"Узел «{id}» не найден. Уточните идентификатор инструментом search.");

                path = node.SourcePath ?? throw new ToolException($"У узла «{id}» нет файла модуля.");
            }

            var limit = arguments.GetInt("limit", 200, 1, 500);
            var line = arguments.GetInt("line", 0, 0, int.MaxValue);

            // Модуль читается с диска заново: агент правит код и хочет видеть текущий текст,
            // а не разобранный при старте сервера.
            var text = ReadFresh(path);
            var module = new BslModuleParser().Parse(new BslModuleSource(path, text));
            var symbols = module.Symbols ?? SymbolTable.Build(module.Routines, BslLexer.Tokenize(text), module.LineCount);
            var types = module.Types ?? TypeInference.Infer(module, symbols, BslLexer.Tokenize(text));

            return Render.JsonOf(new
            {
                path = module.Path,
                lines = module.LineCount,
                note = "Типы выведены консервативно: «Новый X», «Новый(\"X\")», «ОписаниеТипов(…)», "
                    + "«X.Создать()»/«X.Скопировать()», присваивание известного типа и типы параметров по аргументам "
                    + "вызовов этого модуля. Поток данных не считается: ветвления и порядок вызовов не анализируются.",
                routines = module.Routines.Select(routine => new
                {
                    name = routine.Name,
                    kind = routine.Kind.ToString(),
                    export = routine.IsExport,
                    lines = $"{routine.StartLine}-{routine.EndLine}",
                    parameters = routine.Parameters,
                }).ToList(),
                symbolsCount = symbols.Symbols.Count,
                symbols = symbols.Symbols.Take(limit).Select(symbol => new
                {
                    name = symbol.Name,
                    kind = symbol.Kind.ToString(),
                    scope = symbol.Scope.ToString(),
                    routine = symbol.Routine,
                    line = symbol.Line,
                    column = symbol.Column,
                    export = symbol.IsExport ? true : (bool?)null,
                    byValue = symbol.IsByValue ? true : (bool?)null,
                    implicitlyDeclared = symbol.IsImplicit ? true : (bool?)null,
                }).ToList(),
                moduleVariables = symbols.ModuleVariables.Select(static symbol => symbol.Name).ToList(),
                inferredTypesCount = types.Inferred.Count,
                inferredTypes = types.Inferred.Take(limit).Select(inferred => new
                {
                    name = inferred.Name,
                    type = inferred.TypeName,
                    source = inferred.Source.ToString(),
                    platformType = types.IsPlatformType(inferred.TypeName) ? true : (bool?)null,
                    line = inferred.Line,
                    column = inferred.Column,
                    routine = inferred.Routine,
                }).ToList(),
                resolvedCallsCount = types.ResolvedCallCount,
                resolvedCalls = types.PlatformCalls.Take(limit).Select(call => new
                {
                    call = call.Call.Callee,
                    method = call.Call.Method,
                    type = call.TypeName,
                    platformCall = call.Callee,
                    node = call.NodeId,
                    line = call.Call.Line,
                    routine = call.Routine,
                }).ToList(),
                unresolvedCallsCount = types.UnresolvedCallCount,
                unresolvedCalls = types.UnresolvedCalls.Take(limit).Select(call => new
                {
                    call = call.Call.Callee,
                    qualifier = call.Qualifier,
                    reason = call.Reason,
                    line = call.Call.Line,
                    routine = call.Routine,
                }).ToList(),
                unresolvedTypesCount = types.Unresolved.Count,
                unresolvedTypes = types.Unresolved.Take(limit).Select(unresolved => new
                {
                    name = unresolved.Name,
                    reason = unresolved.Reason,
                    line = unresolved.Line,
                    column = unresolved.Column,
                    routine = unresolved.Routine,
                }).ToList(),
                visibleAt = line > 0
                    ? symbols.VisibleAt(line).Select(symbol => new
                    {
                        name = symbol.Name,
                        kind = symbol.Kind.ToString(),
                        declaredAt = symbol.Line,
                        column = symbol.Column,
                        type = types.TryGetType(symbol.Name, line, out var typeName) ? typeName : null,
                    }).ToList()
                    : null,
            });
        });

    private ToolSpec RightsTool() => new(
        "rights",
        "Права ролей конфигурации 1С: какие роли и какие права имеют на объект метаданных и что может конкретная роль. "
        + "Источник — файлы Roles/<Имя>/Ext/Rights.xml: аргумент metadata отвечает «роль × права» на объект, "
        + "включая ограничение доступа к данным (RLS) с текстом условия из файла роли, аргумент role — "
        + "что может роль: её объекты с правами. В выгрузке видны только сами роли: назначение ролей пользователям "
        + "(какие пользователи входят в роль) в файлы конфигурации не входит. "
        + "Примеры: rights metadata=\"Catalog.Товары\"; rights role=\"Менеджер\".",
        [
            new ToolParameter("metadata", "string", "Идентификатор объекта метаданных: Catalog.Товары, Document.Заказ, Configuration."),
            new ToolParameter("role", "string", "Роль: Менеджер, Role.Менеджер или Roles/Менеджер/Ext/Rights.xml."),
            new ToolParameter("limit", "integer", "Сколько строк показать — ролей или объектов (1–500, по умолчанию 50); счётчики всегда полные."),
        ],
        (arguments, token) => RightsToolAsync(arguments, token));

    /// <summary>Предел длины условия RLS в ответе: полный текст всегда лежит в файле роли.</summary>
    private const int RightsConditionLimit = 4000;

    /// <summary>
    /// Права ролей: ответ об объекте, о роли или о том и другом сразу, если заданы оба аргумента.
    /// </summary>
    private async Task<string> RightsToolAsync(ToolArguments arguments, CancellationToken cancellationToken)
    {
        var metadata = arguments.GetString("metadata");
        var role = arguments.GetString("role");
        if (metadata is null && role is null)
        {
            throw new ToolException(
                "Укажите metadata (объект метаданных) или role (роль): например rights metadata=\"Catalog.Товары\" "
                + "или rights role=\"Менеджер\".");
        }

        var limit = arguments.GetInt("limit", 50, 1, 500);
        var catalog = await RightsCatalogAsync(cancellationToken).ConfigureAwait(false);
        var response = new JsonObject();
        if (metadata is not null)
        {
            response["metadata"] = ObjectRightsView(catalog, metadata, limit);
        }

        if (role is not null)
        {
            response["role"] = RoleRightsView(catalog, role, limit);
        }

        if (catalog.Warnings.Count > 0)
        {
            var warnings = new JsonArray();
            foreach (var warning in catalog.Warnings.Take(5))
            {
                warnings.Add(warning);
            }

            response["warnings"] = warnings;
            response["warningsCount"] = catalog.Warnings.Count;
        }

        response["note"] = RightsNote(catalog);
        return Render.JsonOf(response);
    }

    /// <summary>Что за права есть на объект: роли × права, признаки RLS и тексты условий.</summary>
    private JsonObject ObjectRightsView(RoleRightsCatalog catalog, string metadata, int limit)
    {
        var objectId = RightsTargetResolver.TryResolve(metadata, out var resolved, out var kind)
            ? resolved
            : metadata.Trim();
        var rows = catalog.RolesOnObject(objectId)
            .OrderByDescending(static row => row.HasRestriction)
            .ThenByDescending(static row => row.GrantedCount)
            .ThenBy(static row => row.RoleName, StringComparer.Ordinal)
            .ToList();
        var found = SessionObjectExists(objectId);

        var view = new JsonObject
        {
            ["object"] = objectId,
            ["kind"] = kind.IsUnknown ? null : JsonValue.Create(kind.Name),
            ["rolesTotal"] = catalog.RoleCount,
            ["rolesWithRights"] = rows.Count,
            ["rolesShown"] = Math.Min(limit, rows.Count),
            ["rightsGranted"] = rows.Sum(static row => row.GrantedCount),
            ["rightsDenied"] = rows.Sum(static row => row.DeniedCount),
            ["rlsRoles"] = rows.Count(static row => row.HasRestriction),
        };

        if (found is not null)
        {
            view["found"] = found.Value;
        }

        var roles = new JsonArray();
        foreach (var row in rows.Take(limit))
        {
            var item = new JsonObject
            {
                ["role"] = row.RoleId,
                ["name"] = row.RoleName,
                ["file"] = row.RoleFile,
                ["granted"] = row.GrantedCount,
                ["denied"] = row.DeniedCount,
                ["rights"] = RightsJson(row.Rights),
                ["rls"] = row.HasRestriction,
            };

            if (row.HasRestriction)
            {
                // Текст условия читается из файла роли: в сводке прав его нет.
                item["condition"] = ConditionText(catalog.Condition(row.RoleName, objectId));
            }

            roles.Add(item);
        }

        view["roles"] = roles;
        if (rows.Count == 0)
        {
            view["note"] = NoRightsNote(catalog, objectId, found);
        }

        return view;
    }

    /// <summary>Что может роль: признаки роли, объекты с правами и условия RLS показанных объектов.</summary>
    private JsonObject RoleRightsView(RoleRightsCatalog catalog, string role, int limit)
    {
        if (!catalog.TryGetRole(role, out var summary))
        {
            throw new ToolException(MissingRoleText(catalog, role));
        }

        var detailed = catalog.RoleWithConditions(summary.Role) ?? summary;
        var objects = detailed.Objects
            .OrderByDescending(static obj => obj.GrantedCount)
            .ThenBy(static obj => obj.Name, StringComparer.Ordinal)
            .ToList();

        var view = new JsonObject
        {
            ["role"] = detailed.RoleId,
            ["name"] = detailed.Role,
            ["file"] = detailed.SourcePath,
            ["setForNewObjects"] = detailed.SetForNewObjects,
            ["setForAttributesByDefault"] = detailed.SetForAttributesByDefault,
            ["independentRightsOfChildObjects"] = detailed.IndependentRightsOfChildObjects,
            ["objectsTotal"] = objects.Count,
            ["objectsShown"] = Math.Min(limit, objects.Count),
            ["objectsResolved"] = objects.Count(static obj => obj.IsResolved),
            ["rightsGranted"] = objects.Sum(static obj => obj.GrantedCount),
            ["rightsDenied"] = objects.Sum(static obj => obj.DeniedCount),
            ["rlsObjects"] = objects.Count(static obj => obj.HasRestriction),
        };

        var items = new JsonArray();
        foreach (var obj in objects.Take(limit))
        {
            var item = new JsonObject
            {
                ["object"] = obj.IsResolved ? obj.ObjectId : obj.Name,
                ["resolved"] = obj.IsResolved,
                ["kind"] = obj.Kind.IsUnknown ? null : JsonValue.Create(obj.Kind.Name),
                ["granted"] = obj.GrantedCount,
                ["denied"] = obj.DeniedCount,
                ["rights"] = RightsJson(obj.Rights),
                ["rls"] = obj.HasRestriction,
            };

            if (obj.HasRestriction)
            {
                item["condition"] = ConditionText(obj.Condition);
            }

            items.Add(item);
        }

        view["objects"] = items;
        return view;
    }

    /// <summary>
    /// Сводка прав ролей собирается один раз на сессию: файлы прав читаются с диска, а после
    /// open или reload сводка пересобирается — их видно по подмене сессии и читателя исходников.
    /// </summary>
    private async Task<RoleRightsCatalog> RightsCatalogAsync(CancellationToken cancellationToken)
    {
        var session = Session;
        var code = session.Code;
        lock (_rightsGate)
        {
            if (_rights is not null && ReferenceEquals(_rightsSession, session) && ReferenceEquals(_rightsCode, code))
            {
                return _rights;
            }
        }

        // Список файлов прав берётся из индекса, если он готов: обход каталога выгрузки стоит секунды.
        var paths = session.GetIndexReader()?.FilePaths([".xml"], ["Roles/"]);
        var catalog = await Task.Run(
            () => RoleRightsCatalog.Build(session.Source, paths, cancellationToken),
            cancellationToken).ConfigureAwait(false);

        lock (_rightsGate)
        {
            _rights = catalog;
            _rightsSession = session;
            _rightsCode = code;
        }

        return catalog;
    }

    /// <summary>Права в виде объекта «имя права → значение»: читаемее, чем строка detail из индекса.</summary>
    private static JsonObject RightsJson(IReadOnlyList<RoleRightEntry> rights)
    {
        var result = new JsonObject();
        foreach (var right in rights.OrderBy(static right => right.Name, StringComparer.Ordinal))
        {
            result[right.Name] = right.Value;
        }

        return result;
    }

    /// <summary>Условие RLS для ответа: длинный текст обрезается, полный лежит в файле роли.</summary>
    private static string? ConditionText(string? condition)
    {
        if (string.IsNullOrEmpty(condition))
        {
            return null;
        }

        return condition.Length <= RightsConditionLimit
            ? condition
            : condition[..RightsConditionLimit]
                + $"\n… условие обрезано до {RightsConditionLimit} символов: полный текст лежит в файле роли.";
    }

    /// <summary>
    /// Есть ли такой объект в конфигурации: из индекса — сразу, из готового разбора — по модели,
    /// а пока нет ни того ни другого, проверка не делается (null).
    /// </summary>
    private bool? SessionObjectExists(string objectId)
    {
        if (Session.GetIndexReader() is { } reader)
        {
            return reader.GetMetadataObject(objectId) is not null;
        }

        var result = Session.Result;
        return result is null ? null : result.Metadata.Find(objectId) is not null;
    }

    /// <summary>Понятный ответ, когда объект в правах не упомянут ни одной ролью.</summary>
    private static string NoRightsNote(RoleRightsCatalog catalog, string objectId, bool? found) => found switch
    {
        false => $"Объект «{objectId}» не найден в конфигурации: проверьте идентификатор инструментом search.",
        true => $"Ни одна из {catalog.RoleCount} ролей выгрузки не даёт прав на «{objectId}»: прав на объект нет ни в одной роли.",
        _ => $"Ни одна из {catalog.RoleCount} ролей выгрузки не упоминает «{objectId}» в правах; "
            + "существование объекта не проверено — разбор выгрузки ещё не готов (status).",
    };

    /// <summary>Ответ на неизвестную роль: сколько ролей есть и какие имена похожи.</summary>
    private static string MissingRoleText(RoleRightsCatalog catalog, string role)
    {
        if (catalog.RoleCount == 0)
        {
            return "В выгрузке не найдено файлов прав ролей (Roles/<Имя>/Ext/Rights.xml): права не разобраны.";
        }

        var name = RoleRightsCatalog.NormalizeRoleName(role);
        var similar = catalog.RoleNames
            .Where(candidate => candidate.Contains(name, StringComparison.OrdinalIgnoreCase))
            .Take(8)
            .ToList();
        var hint = similar.Count > 0 ? " Похожие роли: " + string.Join(", ", similar) + "." : string.Empty;
        return $"Роль «{role}» не найдена среди {catalog.RoleCount} ролей выгрузки.{hint}";
    }

    /// <summary>Оговорка о том, чего в выгрузке конфигурации нет: назначения ролей пользователям.</summary>
    private static string RightsNote(RoleRightsCatalog catalog)
    {
        var note = "Права собраны из файлов Roles/<Имя>/Ext/Rights.xml: видны только сами роли и их права. "
            + "Назначение ролей пользователям (какие пользователи входят в роль) в выгрузку конфигурации не входит — "
            + "это данные информационной базы, а не файлов конфигурации. RLS — ограничение доступа к данным "
            + "на уровне записей: в ответе приведён текст условия, прочитанный из файла роли.";
        if (catalog.RoleCount == 0)
        {
            note += " Файлов прав ролей в выгрузке не найдено.";
        }

        return note;
    }

    /// <summary>
    /// Готовит разбор конвенций: на индексе — запросами к базе, без него — по разбору в памяти.
    /// Разбор специально не запускается: инструмент должен отвечать быстро, а если разбор уже идёт,
    /// подождём совсем немного — как это делает проверка черновика.
    /// </summary>
    private async Task<ConventionAnswer> FindConventionsAsync(
        string? intent,
        string? platform,
        int limit,
        CancellationToken cancellationToken)
    {
        if (Session.GetIndexReader() is { } reader)
        {
            return Conventions.Suggest(reader.ConventionQuery(), intent, platform, limit);
        }

        var result = Session.Result ?? await WaitForAnalysisAsync(cancellationToken).ConfigureAwait(false);
        return result is null
            ? throw new ToolException(
                "Конфигурация ещё не разобрана: конвенции строятся по графу вызовов. "
                + "Повторите запрос через несколько секунд (status покажет готовность разбора).")
            : Conventions.Suggest(new AnalysisConventionQuery(result), intent, platform, limit);
    }

    private ToolSpec ConventionsTool() => new(
        "conventions",
        "«В конфигурации это уже делают так»: рейтинг общих модулей и процедур по числу вызовов и подбор "
        + "типовых приёмов под намерение агента — получить реквизит объекта, записать объект, найти по "
        + "наименованию, прочитать данные запросом, вывести сообщение пользователю, выполнить на сервере "
        + "или в фоне. В ответе процедуры с числом использований, пример вызова (модуль и строка) и "
        + "пояснение, почему они подходят. Вместо intent можно задать точный метод платформы — тогда "
        + "инструмент покажет, кто его вызывает и на каких строках. Пример: intent=\"записать объект\" "
        + "или platform=\"Записать\". Код найденных процедур открывается инструментом code по их id.",
        [
            new ToolParameter("intent", "string", "Намерение словами: «получить реквизит объекта», «записать объект», «найти по наименованию»."),
            new ToolParameter("platform", "string", "Точный метод платформы: Записать, ЗначениеРеквизитаОбъекта, НайтиПоНаименованию, Сообщить."),
            new ToolParameter("limit", "integer", "Сколько процедур и модулей вернуть (1–50, по умолчанию 8)."),
        ],
        async (arguments, token) =>
        {
            var intent = arguments.GetString("intent");
            var platform = arguments.GetString("platform");
            if (intent is null && platform is null)
            {
                throw new ToolException(
                    "Укажите intent (намерение словами) или platform (точное имя метода платформы), например "
                    + "intent=\"записать объект\" либо platform=\"Записать\".");
            }

            var limit = arguments.GetInt("limit", 8, 1, 50);
            var answer = await FindConventionsAsync(intent, platform, limit, token).ConfigureAwait(false);

            var modules = answer.Modules
                .Take(limit)
                .Select(static module => new
                {
                    module = module.Name,
                    path = module.ModulePath,
                    calls = module.Uses,
                })
                .ToList();

            var routines = answer.Routines
                .Take(limit)
                .Select(static routine => new
                {
                    id = routine.RoutineId,
                    name = routine.Name,
                    module = routine.ModulePath,
                    owner = routine.OwnerId,
                    uses = routine.Uses,
                    callers = routine.Callers,
                    export = routine.Symbol is { IsExport: true } ? true : (bool?)null,
                    lines = routine.Symbol is { } symbol ? $"{symbol.StartLine}-{symbol.EndLine}" : null,
                    parameters = routine.Symbol?.Parameters,
                    comment = routine.Symbol?.CommentHead,
                    platformMethod = routine.CallMethod,
                    example = routine.ExampleLine is { } line
                        ? new { module = routine.ExampleModule, line, call = routine.ExampleDetail }
                        : null,
                    reasons = routine.Reasons,
                })
                .ToList();

            var callSites = answer.CallSites
                .Take(limit * 2)
                .Select(static site => new
                {
                    routine = site.RoutineId,
                    module = site.ModulePath,
                    line = site.Line,
                    call = site.Detail,
                })
                .ToList();

            return Render.JsonOf(new
            {
                intent = answer.Intent,
                question = answer.Question,
                platformMethod = answer.PlatformMethod,
                found = new
                {
                    modules = answer.Modules.Count,
                    routines = answer.Routines.Count,
                    callSites = answer.CallSites.Count,
                },
                modules,
                routines,
                callSites,
                note = answer.Note,
                hint = answer.Hint,
            });
        });

    private ToolSpec ReloadTool() => new(
        "reload",
        "Перечитать выгрузку заново: сбрасывает разбор и кеш исходников, чтобы поиск и граф увидели "
        + "изменения после правки модулей. Разбор продолжится в фоне, следите через status.",
        [],
        (_, _) =>
        {
            Session.Reload();
            return Task.FromResult($"Разбор запущен заново. Состояние: {Session.State}");
        });

    /// <summary>Готовое совпадение: где найдено, из какого источника и какому объекту принадлежит файл.</summary>
    private sealed record GrepHit(
        string? Owner,
        string Source,
        bool Overridden,
        string File,
        int Line,
        string Text,
        List<string> Context);

    /// <summary>Поиск по тексту файлов выгрузки.</summary>
    private async Task<string> GrepAsync(ToolArguments arguments, CancellationToken cancellationToken)
    {
        var session = Session;
        var source = session.Source;
        var pattern = arguments.RequireString("pattern");
        var isRegex = arguments.GetBool("regex", false);
        var ignoreCase = arguments.GetBool("ignoreCase", true);
        var extensions = NormalizeExtensions(arguments.GetStringList("extensions"));
        var prefixes = arguments.GetStringList("paths");
        var limit = arguments.GetInt("limit", 50, 1, 500);
        var contextLines = arguments.GetInt("context", 1, 0, 3);
        var waitMs = arguments.GetInt("waitMs", 60_000, 0, 600_000);

        CodeSearchResult found;
        try
        {
            // Поиск по тексту живёт в ядре (CodeSearchService): своей реализации сканирования у сервера нет.
            // Список файлов берётся из индекса — обход каталога выгрузки стоил несколько секунд.
            // У составного источника список не подставляем: нужно показать обе версии перекрытых файлов.
            var reader = source is IVersionedDumpSource ? null : Session.GetIndexReader();
            found = new CodeSearchService(source).Search(
                pattern,
                new CodeSearchOptions
                {
                    MaxResults = limit,
                    MaxMatchesPerFile = 10,
                    ContextLines = contextLines,
                    Regex = isRegex,
                    CaseSensitive = !ignoreCase,
                    Extensions = extensions,
                    PathPrefixes = prefixes,
                    Paths = reader?.FilePaths(extensions, prefixes),
                },
                cancellationToken);
        }
        catch (ArgumentException exception)
        {
            throw new ToolException($"Шаблон поиска не разобран: {exception.Message}");
        }

        var composite = source as CompositeDumpSource;
        var files = found.Hits.Select(static entry => entry.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        // Владельцы: в режиме индекса они берутся из базы сразу, иначе ждём разбор не дольше waitMs.
        var (owners, ownersResolved) = await OwnersAsync(files, waitMs, cancellationToken);

        var matchedFiles = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);
        var overriddenFound = false;
        var hits = new List<GrepHit>(found.Hits.Count);
        foreach (var entry in found.Hits)
        {
            var overridden = composite?.IsOverridden(entry.Path) ?? false;
            overriddenFound |= overridden;
            var sourceName = composite is not null && entry.SourceIndex >= 0 && entry.SourceIndex < composite.Sources.Count
                ? composite.Sources[entry.SourceIndex].DisplayName
                : source.DisplayName;
            hits.Add(new GrepHit(
                owners.TryGetValue(entry.Path, out var owner) ? owner : null,
                sourceName,
                overridden,
                entry.Path,
                entry.Line,
                entry.Text,
                [.. entry.Context]));
        }

        cancellationToken.ThrowIfCancellationRequested();

        var ownerSummary = hits
            .Where(static hit => !string.IsNullOrEmpty(hit.Owner))
            .GroupBy(static hit => hit.Owner!, StringComparer.Ordinal)
            .OrderByDescending(static group => group.Count())
            .Select(static group => new { owner = group.Key, count = group.Count() })
            .ToList();

        return Render.JsonOf(new
        {
            pattern,
            regex = isRegex,
            ignoreCase,
            sources = SourceNames(source),
            filesScanned = found.ScannedFiles,
            filesMatched = matchedFiles.Count,
            matches = hits.Count,
            truncated = found.Truncated,
            ownersResolved,
            byOwner = ownerSummary,
            note = BuildGrepNote(overriddenFound, ownersResolved),
            hits = hits.Select(static hit => new
            {
                owner = hit.Owner,
                source = hit.Source,
                overridden = hit.Overridden ? true : (bool?)null,
                file = hit.File,
                line = hit.Line,
                text = hit.Text,
                context = hit.Context.Count > 0 ? hit.Context : null,
            }).ToList(),
        });
    }

    private static string? BuildGrepNote(bool overridden, bool ownersResolved)
    {
        var parts = new List<string>(2);
        if (!ownersResolved)
        {
            parts.Add("Разбор выгрузки ещё идёт, поэтому владельцы не определены: повторите поиск позже "
                + "(разбор продолжается в фоне, status покажет готовность) или увеличьте waitMs.");
        }

        if (overridden)
        {
            parts.Add("Часть файлов перекрыта другим источником (расширением): показаны обе версии, у совпадения указан source.");
        }

        return parts.Count == 0 ? null : string.Join(' ', parts);
    }

    /// <summary>
    /// Карта «файл выгрузки → объект метаданных». Модули сопоставляются точно (по пути модуля),
    /// остальные файлы — по каталогу объекта: у схемы компоновки данных или макета отчёта нет
    /// собственного объекта в модели, но владелец у них всё равно есть — сам отчёт.
    /// </summary>
    /// <remarks>
    /// В режиме индекса карта строится по таблицам сразу — ждать разбор не нужно. Без индекса
    /// приходится ждать разбор, но не дольше <paramref name="waitMs"/>; если он не успел,
    /// поиск всё равно отдаёт совпадения, просто без имён объектов.
    /// </remarks>
    private async Task<(IReadOnlyDictionary<string, string> Owners, bool Resolved)> OwnersAsync(
        IReadOnlyList<string> files,
        int waitMs,
        CancellationToken cancellationToken)
    {
        var empty = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (files.Count == 0)
        {
            return (empty, Session.Result is not null || Session.GetIndexReader() is not null);
        }

        if (Session.GetIndexReader() is { } reader)
        {
            var owners = new Dictionary<string, string>(reader.OwnersOf(files), StringComparer.OrdinalIgnoreCase);
            var directories = reader.TopLevelObjectDirectories()
                .OrderByDescending(static entry => entry.Directory.Length)
                .ToList();

            foreach (var file in files)
            {
                if (owners.ContainsKey(file))
                {
                    continue;
                }

                var match = directories.FirstOrDefault(entry =>
                    file.StartsWith(entry.Directory + "/", StringComparison.OrdinalIgnoreCase));
                if (match.Owner is not null)
                {
                    owners[file] = match.Owner;
                }
            }

            return (owners, true);
        }

        var result = Session.Result;
        if (result is null && waitMs > 0)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(waitMs);
            try
            {
                result = await Session.GetAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Разбор не успел за отведённое время: отдаём совпадения без владельцев.
                result = null;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                result = null;
            }
        }

        if (result is null)
        {
            return (empty, false);
        }

        var byFile = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var module in result.Modules)
        {
            if (!string.IsNullOrEmpty(module.OwnerId))
            {
                byFile[module.Path] = module.OwnerId!;
            }
        }

        var byDirectory = result.Metadata.Objects
            .Where(static obj => obj.IsTopLevel && !string.IsNullOrEmpty(obj.Directory))
            .Select(static obj => (Prefix: obj.Directory + "/", Owner: obj.Id))
            .OrderByDescending(static entry => entry.Prefix.Length)
            .ToList();

        foreach (var file in files)
        {
            if (byFile.ContainsKey(file))
            {
                continue;
            }

            var match = byDirectory.FirstOrDefault(entry =>
                file.StartsWith(entry.Prefix, StringComparison.OrdinalIgnoreCase));
            if (match.Owner is not null)
            {
                byFile[file] = match.Owner;
            }
        }

        return (byFile, true);
    }

    private static IReadOnlyCollection<string> NormalizeExtensions(IReadOnlyList<string>? values)
    {
        if (values is null || values.Count == 0)
        {
            return DefaultGrepExtensions;
        }

        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values)
        {
            result.Add(value.StartsWith('.') ? value : "." + value);
        }

        return result;
    }

    private static IReadOnlyList<string> SourceNames(IDumpSource source) =>
        source is CompositeDumpSource composite
            ? [.. composite.Sources.Select(static item => item.DisplayName)]
            : [source.DisplayName];

    private string Status()
    {
        var result = Session.Result;
        var warnings = result?.Warnings ?? [];

        return Render.JsonOf(new
        {
            dump = Session.DumpPath,
            dumpOpen = Session.IsOpen,
            state = Session.State,
            running = Session.IsRunning,
            completedAt = Session.CompletedAt,
            indexMode = Session.IsIndexMode,
            indexReady = Session.IsIndexReady,
            indexPath = Session.IndexPath,
            watching = Session.IsWatching,
            rebuilding = Session.IsRebuilding,
            dumpChange = DumpChangeView(),
            lastCheckedAt = Session.LastCheckedAt,
            indexStatistics = Session.GetIndexStatistics() is { } index
                ? new
                {
                    nodes = index.Nodes,
                    edges = index.Edges,
                    symbols = index.Symbols,
                    metadataObjects = index.MetadataObjects,
                    metadataItems = index.MetadataItems,
                    metadataRefs = index.MetadataRefs,
                    metadataRefsCode = index.MetadataRefsCode,
                    metadataRefsQuery = index.MetadataRefsQuery,
                    metadataRefsByContext = index.MetadataRefsByContext,
                    forms = index.Forms,
                    platformNodes = index.PlatformNodes,
                    externalNodes = index.ExternalNodes,
                    indexedAt = index.IndexedAt,
                }
                : null,
            durationSeconds = result is null ? (double?)null : Math.Round(result.Duration.TotalSeconds, 1),
            source = result?.SourceName,
            statistics = result is null
                ? null
                : new
                {
                    metadataObjects = result.Statistics.MetadataObjects,
                    topLevelObjects = result.Statistics.TopLevelObjects,
                    modules = result.Statistics.Modules,
                    routines = result.Statistics.Routines,
                    graphNodes = result.Statistics.GraphNodes,
                    graphEdges = result.Statistics.GraphEdges,
                    platformTopics = result.Platform is null ? 0 : result.Platform.TopicCount,
                },
            warnings = warnings.Take(10).ToList(),
            warningsCount = warnings.Count,
            error = Session.Failure?.Message,
            hint = Session.IsOpen ? null : "Выгрузка не открыта: вызовите open с путём к каталогу выгрузки 1С.",
        });
    }

    /// <summary>
    /// Свежесть индекса. Проверка идёт в фоне: она обходит десятки тысяч файлов выгрузки,
    /// и инструмент status не должен её ждать. В режиме наблюдения состояние держит наблюдатель.
    /// </summary>
    private object? DumpChangeView()
    {
        Session.StartDumpCheck();
        var change = Session.DumpChange;

        return change is null
            ? null
            : new
            {
                added = change.Added,
                changed = change.Changed,
                removed = change.Removed,
                total = change.Total,
                fresh = change.IsEmpty,
            };
    }

    private Task<IGraphQuery> QueryAsync(CancellationToken cancellationToken) =>
        Session.QueryAsync(cancellationToken);

    /// <summary>
    /// Короткое ожидание готовности справки платформы: если загрузка почти закончилась, отвечаем
    /// сразу; если нет — инструмент вернёт сообщение, что модель ещё грузится.
    /// </summary>
    private async Task<PlatformHelpIndex?> WaitForPlatformAsync(CancellationToken cancellationToken)
    {
        var warmup = Session.PlatformWarmup;
        if (warmup is null)
        {
            return Session.Platform;
        }

        await Task.WhenAny(warmup, Task.Delay(TimeSpan.FromSeconds(3), cancellationToken)).ConfigureAwait(false);
        return Session.Platform;
    }

    /// <summary>
    /// Читает файл выгрузки заново, минуя кеш разбора: инструмент check должен видеть
    /// только что сохранённую правку.
    /// </summary>
    private string ReadFresh(string path)
    {
        var file = new DumpFile(DumpPath.Normalize(path), 0, DateTimeOffset.UnixEpoch);
        try
        {
            using var stream = Session.Source.OpenRead(file);

            // Кодировка определяется по содержимому: UTF-8 либо запасная CP1251.
            return DumpTextReader.ReadAllText(stream);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FileNotFoundException)
        {
            throw new ToolException($"Файл «{path}» не прочитан: {exception.Message}");
        }
    }

    private static object NodeView(GraphNode node) => new
    {
        id = node.Id,
        kind = node.Kind.ToString(),
        name = node.Name,
        metadataKind = node.MetadataKind,
        file = node.SourcePath,
        external = node.IsExternal ? true : (bool?)null,
        tags = node.Tags is { Count: > 0 } ? node.Tags : null,
    };

    private static object EdgeView(GraphEdge edge, string otherId, IGraphQuery query)
    {
        var other = query.FindNode(otherId);
        return new
        {
            kind = edge.Kind.ToString(),
            node = otherId,
            name = other?.Name,
            nodeKind = other?.Kind.ToString(),
            line = edge.Line,
            detail = edge.Detail,
        };
    }

    private static string TopicText(PlatformTopic topic, bool full)
    {
        const int preview = 4000;
        var text = topic.Text ?? string.Empty;
        var body = full || text.Length <= preview
            ? text
            : text[..preview] + "\n… текст обрезан; вызовите platform с full=true, чтобы получить тему целиком.";

        var header = new StringBuilder();
        header.Append(CultureInfo.InvariantCulture, $"# {topic.Title}\n");
        header.Append(CultureInfo.InvariantCulture, $"имя: {topic.Name}\n");
        header.Append(CultureInfo.InvariantCulture, $"версия платформы: {topic.Version}\n");
        header.Append(CultureInfo.InvariantCulture, $"раздел справки: {topic.Kind}\n\n");
        header.Append(body);
        return Render.Truncate(header.ToString());
    }

    private static IReadOnlyCollection<GraphNodeKind>? ParseKinds(IReadOnlyList<string>? values) =>
        ParseEnums<GraphNodeKind>(values);

    private static IReadOnlyCollection<TEnum>? ParseEnums<TEnum>(IReadOnlyList<string>? values)
        where TEnum : struct, Enum
    {
        if (values is null || values.Count == 0)
        {
            return null;
        }

        var result = new List<TEnum>(values.Count);
        foreach (var value in values)
        {
            if (Enum.TryParse<TEnum>(value, ignoreCase: true, out var parsed))
            {
                result.Add(parsed);
            }
            else
            {
                throw new ToolException(
                    $"Недопустимое значение «{value}». Возможные: {string.Join(", ", Enum.GetNames<TEnum>())}.");
            }
        }

        return result.Count == 0 ? null : result;
    }

    private static int TagInt(GraphNode node, string key) =>
        node.Tags is not null && node.Tags.TryGetValue(key, out var value) &&
        int.TryParse(value, CultureInfo.InvariantCulture, out var result)
            ? result
            : 0;
}
