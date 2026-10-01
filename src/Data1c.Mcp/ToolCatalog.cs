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

namespace Data1c.Mcp;

/// <summary>
/// Инструменты MCP-сервера: навигация по выгрузке 1С и чтение кода так, как это нужно при разработке
/// на BSL. Все инструменты работают поверх одного разбора (<see cref="AnalysisSession"/>).
/// </summary>
public sealed class ToolCatalog
{
    /// <summary>Расширения, по которым ищет grep, если агент не задал свои.</summary>
    private static readonly IReadOnlyCollection<string> DefaultGrepExtensions = [".bsl", ".xml"];

    /// <summary>Предел строк, читаемых из одного файла при поиске: защита от гигантских модулей.</summary>
    private const int MaxLinesPerFile = 60_000;

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
            GrepTool(),
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

            if (filtered.Count == 0 && nestedView.Count == 0)
            {
                return $"Ничего не найдено по запросу «{text}». Попробуйте часть имени, синоним или путь файла.";
            }

            return Render.JsonOf(new
            {
                query = text,
                found = filtered.Count,
                total = hits.Count,
                results = filtered,
                nestedFound = nestedView.Count,
                nested = nestedView,
            });
        });

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
        + "типы, модули. Нужен, чтобы писать код по реальной структуре объекта. Пример: id=\"Catalog.Товары\".",
        [
            new ToolParameter("id", "string", "Идентификатор: Catalog.Товары, Document.Заказ, Document.Заказ/TabularSection.Строки.", Required: true),
            new ToolParameter("depth", "integer", "Глубина дерева состава (1–4, по умолчанию 3)."),
            new ToolParameter("maxChildren", "integer", "Сколько детей показывать у одного узла (1–500, по умолчанию 200)."),
        ],
        async (arguments, token) =>
        {
            var query = await QueryAsync(token);
            var id = arguments.RequireString("id");
            var depth = arguments.GetInt("depth", 3, 1, 4);
            var maxChildren = arguments.GetInt("maxChildren", 200, 1, 500);

            var card = query.GetMetadata(id, depth, maxChildren);
            if (card is null)
            {
                var candidates = query.Search(id, 10).Select(hit => hit.Id).ToList();
                var hint = candidates.Count > 0 ? " Похожие идентификаторы: " + string.Join(", ", candidates) : string.Empty;
                throw new ToolException($"Объект метаданных «{id}» не найден.{hint}");
            }

            var root = MetadataNode(card);
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
            return Render.JsonOf(root);
        });

    /// <summary>
    /// Узел дерева состава объекта: сам объект, его типы и дети. Вложенность важна для табличных частей —
    /// плоский список реквизитов не показывает, какие из них относятся к табличной части, а какие к объекту.
    /// Карточка уже ограничена глубиной, поэтому здесь только сборка JSON.
    /// </summary>
    private static JsonObject MetadataNode(MetadataCard card)
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
                var node = query.FindNode(id) ?? throw new ToolException(
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

    /// <summary>Совпадение без сведений об объекте: их добавляет вызывающий, когда знает владельца.</summary>
    private sealed record RawHit(int Line, string Text, List<string> Context);

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

        var matcher = BuildMatcher(pattern, isRegex, ignoreCase);
        var composite = source as CompositeDumpSource;

        // Разбор нужен только ради имён владельцев, поэтому ждём его ограниченное время:
        // на большой выгрузке первый вызов иначе упирается в таймаут клиента.
        var (owners, ownersResolved) = await OwnerIndexAsync(waitMs, cancellationToken);

        var files = new List<SourcedDumpFile>();
        if (composite is not null)
        {
            files.AddRange(composite
                .EnumerateAll(cancellationToken)
                .Where(entry => Accept(entry.File, extensions, prefixes)));
        }
        else
        {
            files.AddRange(source
                .EnumerateFiles(cancellationToken)
                .Where(file => Accept(file, extensions, prefixes))
                .Select(file => new SourcedDumpFile(0, source, file)));
        }

        var hits = new List<GrepHit>();
        var gate = new Lock();
        var scanned = 0;
        var matchedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stop = new int[1];
        var overriddenFound = false;

        Parallel.ForEach(
            files,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Environment.ProcessorCount,
                CancellationToken = cancellationToken,
            },
            (entry, state) =>
            {
                if (Volatile.Read(ref stop[0]) == 1)
                {
                    state.Stop();
                    return;
                }

                Interlocked.Increment(ref scanned);
                List<RawHit> found;
                try
                {
                    found = ScanFile(source, entry.File, matcher, contextLines, limit, cancellationToken);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
                {
                    // Файл занят выгрузкой 1С или исчез между запросами — поиск продолжается.
                    return;
                }

                if (found.Count == 0)
                {
                    return;
                }

                var owner = owners.Find(entry.File.RelativePath);
                var overridden = composite?.IsOverridden(entry.File.RelativePath) ?? false;

                lock (gate)
                {
                    foreach (var hit in found)
                    {
                        if (hits.Count >= limit)
                        {
                            Volatile.Write(ref stop[0], 1);
                            break;
                        }

                        matchedFiles.Add(entry.File.RelativePath);
                        overriddenFound |= overridden;
                        hits.Add(new GrepHit(owner, entry.Source.DisplayName, overridden, entry.File.RelativePath, hit.Line, hit.Text, hit.Context));
                    }
                }
            });

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
            filesScanned = Volatile.Read(ref scanned),
            filesMatched = matchedFiles.Count,
            matches = hits.Count,
            truncated = Volatile.Read(ref stop[0]) == 1,
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

    /// <summary>Читает файл и возвращает совпадения с окружением.</summary>
    private static List<RawHit> ScanFile(
        IDumpSource source,
        DumpFile file,
        Func<string, bool> matcher,
        int contextLines,
        int limit,
        CancellationToken cancellationToken)
    {
        var lines = new List<string>(1024);
        using (var stream = source.OpenRead(file))
        using (var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
        {
            while (reader.ReadLine() is { } line)
            {
                lines.Add(line);
                if (lines.Count >= MaxLinesPerFile)
                {
                    break;
                }
            }
        }

        var found = new List<RawHit>();
        for (var index = 0; index < lines.Count && found.Count < limit; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!matcher(lines[index]))
            {
                continue;
            }

            var from = Math.Max(0, index - contextLines);
            var to = Math.Min(lines.Count - 1, index + contextLines);
            var context = new List<string>(Math.Max(0, to - from));
            for (var neighbour = from; neighbour <= to; neighbour++)
            {
                if (neighbour != index)
                {
                    context.Add(lines[neighbour].Trim());
                }
            }

            found.Add(new RawHit(index + 1, lines[index].Trim(), context));
        }

        return found;
    }

    /// <summary>
    /// Карта «файл выгрузки → объект метаданных». Модули сопоставляются точно (по пути модуля),
    /// остальные файлы — по каталогу объекта: у схемы компоновки данных или макета отчёта нет
    /// собственного объекта в модели, но владелец у них всё равно есть — сам отчёт.
    /// </summary>
    private sealed class OwnerIndex
    {
        private readonly IReadOnlyDictionary<string, string> _byFile;
        private readonly IReadOnlyList<(string Prefix, string Owner)> _byDirectory;

        public OwnerIndex(
            IReadOnlyDictionary<string, string> byFile,
            IReadOnlyList<(string Prefix, string Owner)> byDirectory)
        {
            _byFile = byFile;
            _byDirectory = byDirectory;
        }

        public string? Find(string relativePath)
        {
            if (_byFile.TryGetValue(relativePath, out var owner))
            {
                return owner;
            }

            string? best = null;
            var bestLength = 0;
            foreach (var (prefix, candidate) in _byDirectory)
            {
                if (prefix.Length > bestLength && relativePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    best = candidate;
                    bestLength = prefix.Length;
                }
            }

            return best;
        }
    }

    /// <summary>
    /// Строит карту владельцев, ожидая разбор не дольше <paramref name="waitMs"/>. Если разбор не успел
    /// или не удался, поиск всё равно отдаёт совпадения — просто без имён объектов.
    /// </summary>
    private async Task<(OwnerIndex Index, bool Resolved)> OwnerIndexAsync(int waitMs, CancellationToken cancellationToken)
    {
        var session = Session;
        var result = session.Result;

        if (result is null && waitMs > 0)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(waitMs);
            try
            {
                result = await session.GetAsync(timeout.Token);
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
            return (new OwnerIndex(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), []), false);
        }

        var byFile = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var module in result.Modules)
        {
            if (!string.IsNullOrEmpty(module.OwnerId))
            {
                byFile[module.Path] = module.OwnerId!;
            }
        }

        var byDirectory = new List<(string Prefix, string Owner)>();
        foreach (var obj in result.Metadata.Objects)
        {
            if (obj.IsTopLevel && !string.IsNullOrEmpty(obj.Directory))
            {
                byDirectory.Add((obj.Directory + "/", obj.Id));
            }
        }

        return (new OwnerIndex(byFile, byDirectory), true);
    }

    private static Func<string, bool> BuildMatcher(string pattern, bool isRegex, bool ignoreCase)
    {
        if (!isRegex)
        {
            var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return line => line.Contains(pattern, comparison);
        }

        var options = RegexOptions.CultureInvariant;
        if (ignoreCase)
        {
            options |= RegexOptions.IgnoreCase;
        }

        Regex regex;
        try
        {
            regex = new Regex(pattern, options);
        }
        catch (ArgumentException exception)
        {
            throw new ToolException($"Регулярное выражение не разобрано: {exception.Message}");
        }

        return line => regex.IsMatch(line);
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

    private static bool Accept(DumpFile file, IReadOnlyCollection<string> extensions, IReadOnlyList<string>? prefixes)
    {
        if (!extensions.Contains(file.Extension))
        {
            return false;
        }

        if (prefixes is null || prefixes.Count == 0)
        {
            return true;
        }

        foreach (var prefix in prefixes)
        {
            var normalized = DumpPath.Normalize(prefix);
            if (file.RelativePath.StartsWith(normalized, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
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
            indexStatistics = Session.GetIndexStatistics() is { } index
                ? new
                {
                    nodes = index.Nodes,
                    edges = index.Edges,
                    symbols = index.Symbols,
                    metadataObjects = index.MetadataObjects,
                    metadataItems = index.MetadataItems,
                    metadataRefs = index.MetadataRefs,
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
