using System.Globalization;
using System.Text;
using Data1c.Core.Analysis;
using Data1c.Core.Bsl;
using Data1c.Core.Dump;
using Data1c.Core.Graph;
using Data1c.Core.Metadata;
using Data1c.Core.Platform;

namespace Data1c.Mcp;

/// <summary>
/// Инструменты MCP-сервера: навигация по выгрузке 1С и чтение кода так, как это нужно при разработке
/// на BSL. Все инструменты работают поверх одного разбора (<see cref="AnalysisSession"/>).
/// </summary>
public sealed class ToolCatalog
{
    private readonly Lock _sessionGate = new();
    private readonly List<ToolSpec> _tools;
    private AnalysisSession _session;

    public ToolCatalog(AnalysisSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
        _tools =
        [
            StatusTool(),
            OpenTool(),
            SearchTool(),
            NodeTool(),
            NeighborsTool(),
            CodeTool(),
            MetadataTool(),
            PlatformTool(),
            CheckTool(),
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
        "Открыть выгрузку конфигурации 1С по пути к каталогу (или переключиться на другую): "
        + "разбор запускается в фоне, следите через status. Нужен, если сервер запущен без --dump.",
        [
            new ToolParameter("path", "string", "Каталог выгрузки, созданный командой «Выгрузить конфигурацию в файлы».", Required: true),
            new ToolParameter("platform", "boolean", "Подключить справку платформы 1С для этой выгрузки."),
            new ToolParameter("sections", "array", "Разбирать только эти секции выгрузки (например, [\"CommonModules\",\"Catalogs\"])."),
        ],
        (arguments, cancellationToken) =>
        {
            _ = cancellationToken;
            var path = arguments.RequireString("path");
            var current = Session;
            var platform = arguments.GetBool("platform", current.Request.PlatformHelp);
            var sections = arguments.GetStringList("sections") ?? current.Request.Sections;

            var opened = new AnalysisSession(current.Request with
            {
                DumpPath = path,
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
        "Поиск объектов конфигурации по идентификатору, имени, синониму, пути файла: даёт идентификаторы "
        + "для node/neighbors/code. Пример: search query=\"Номенклатура\".",
        [
            new ToolParameter("query", "string", "Имя, синоним, часть идентификатора (Catalog.Товары) или путь файла.", Required: true),
            new ToolParameter("limit", "integer", "Сколько результатов вернуть (1–100, по умолчанию 20)."),
            new ToolParameter(
                "kinds",
                "array",
                "Оставить только эти виды узлов.",
                Values: ["Configuration", "MetadataObject", "Module", "Routine", "External", "Platform"]),
        ],
        async (arguments, token) =>
        {
            var query = await QueryAsync(token);
            var text = arguments.RequireString("query");
            var limit = arguments.GetInt("limit", 20, 1, 100);
            var kinds = ParseKinds(arguments.GetStringList("kinds"));

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

            if (filtered.Count == 0)
            {
                return $"Ничего не найдено по запросу «{text}». Попробуйте часть имени, синоним или путь файла.";
            }

            return Render.JsonOf(new { query = text, found = filtered.Count, total = hits.Count, results = filtered });
        });

    private ToolSpec NodeTool() => new(
        "node",
        "Карточка узла конфигурации: вид, имя, файл, теги и связи со строками кода. "
        + "Кто вызывает процедуру и что вызывает она сама — в neighbors с edgeKinds=[\"Calls\"].",
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

            return Render.JsonOf(new
            {
                node = NodeView(details.Node),
                incomingCount = details.Incoming.Count,
                outgoingCount = details.Outgoing.Count,
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
                var node = query.Graph.FindNode(id) ?? throw new ToolException(
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
        "Состав объекта метаданных 1С: реквизиты, табличные части, типы, формы, модули. "
        + "Нужен, чтобы писать код по реальной структуре объекта. Пример: id=\"Catalog.Товары\".",
        [
            new ToolParameter("id", "string", "Идентификатор объекта: Catalog.Товары, Document.Заказ, CommonModule.ОбщегоНазначения.", Required: true),
        ],
        async (arguments, token) =>
        {
            var result = await AnalysisAsync(token);
            var id = arguments.RequireString("id");
            var obj = result.Metadata.Find(id);

            if (obj is null)
            {
                var query = new GraphQueryService(result.Graph);
                var candidates = query.Search(id, 10).Select(hit => hit.Id).ToList();
                var hint = candidates.Count > 0 ? " Похожие идентификаторы: " + string.Join(", ", candidates) : string.Empty;
                throw new ToolException($"Объект метаданных «{id}» не найден.{hint}");
            }

            return Render.JsonOf(new
            {
                id = obj.CanonicalName,
                kind = obj.Kind.ToString(),
                name = obj.Name,
                synonym = obj.Synonym,
                uuid = obj.Uuid,
                parent = obj.Parent?.CanonicalName,
                file = obj.SourcePath,
                comment = obj.Comment,
                isTopLevel = obj.IsTopLevel,
                properties = obj.Properties
                    .Where(static property => !string.IsNullOrWhiteSpace(property.Value))
                    .Take(30)
                    .ToDictionary(static property => property.Key, static property => property.Value),
                children = obj.Children
                    .GroupBy(static child => child.Kind.ToString())
                    .ToDictionary(
                        static group => group.Key,
                        group => group.Take(60).Select(child => new
                        {
                            id = child.CanonicalName,
                            name = child.Name,
                            synonym = child.Synonym,
                            types = child.References
                                .Where(static reference => reference.Kind == MdReferenceKind.Type)
                                .Select(static reference => reference.TargetId)
                                .Distinct(StringComparer.Ordinal)
                                .Take(10)
                                .ToList(),
                        }).ToList()),
                references = obj.References
                    .Where(static reference => reference.Kind != MdReferenceKind.Type)
                    .Take(60)
                    .Select(static reference => new
                    {
                        kind = reference.Kind.ToString(),
                        target = reference.TargetId,
                        detail = reference.Detail,
                    })
                    .ToList(),
                modules = obj.Modules.Select(static module => new
                {
                    path = module.RelativePath,
                    kind = module.Kind.ToString(),
                }).ToList(),
            });
        });

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
            var result = await AnalysisAsync(token);
            var index = result.Platform ?? throw new ToolException(
                "Справка платформы не подключена. Запустите сервер с ключом --platform (нужна установленная платформа 1С).");

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
        "Структурная проверка одного модуля BSL прямо с диска (без 1С): процедуры, области, вызовы, "
        + "обращения к метаданным и замечания разбора. Вызывайте после правки модуля.",
        [
            new ToolParameter("path", "string", "Путь файла модуля внутри выгрузки."),
            new ToolParameter("id", "string", "Идентификатор узла, если путь неизвестен."),
            new ToolParameter("calls", "boolean", "Показать список вызовов модуля."),
        ],
        async (arguments, token) =>
        {
            var path = arguments.GetString("path");
            if (path is null)
            {
                var query = await QueryAsync(token);
                var id = arguments.RequireString("id");
                var node = query.Graph.FindNode(id) ?? throw new ToolException(
                    $"Узел «{id}» не найден. Уточните идентификатор инструментом search.");

                path = node.SourcePath ?? throw new ToolException($"У узла «{id}» нет файла модуля.");
            }

            var text = ReadFresh(path);
            var module = new BslModuleParser().Parse(new BslModuleSource(path, text));
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

            return Render.JsonOf(new
            {
                path = module.Path,
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

    private async Task<GraphQueryService> QueryAsync(CancellationToken cancellationToken) =>
        new((await AnalysisAsync(cancellationToken)).Graph);

    private async Task<AnalysisResult> AnalysisAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await Session.GetAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ToolException)
        {
            throw;
        }
        catch (Exception exception)
        {
            var stage = Session.State;
            throw new ToolException($"Разбор выгрузки не удался ({stage}): {exception.Message}", exception);
        }
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
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
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

    private static object EdgeView(GraphEdge edge, string otherId, GraphQueryService query)
    {
        var other = query.Graph.FindNode(otherId);
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
