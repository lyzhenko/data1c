using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Data1c.Core.Analysis;
using Data1c.Core.Bsl;
using Data1c.Core.Dump;
using Data1c.Core.Graph;
using Data1c.Core.Metadata;
using Data1c.Core.Platform;
using Microsoft.Data.Sqlite;

namespace Data1c.Store;

/// <summary>Записывает результат разбора выгрузки в индекс.</summary>
/// <remarks>
/// Запись идёт одной транзакцией с <c>synchronous=OFF</c> и подготовленными командами: на полной
/// выгрузке это миллионы строк, и построчная вставка с обычной синхронизацией заняла бы минуты.
/// </remarks>
public sealed class IndexWriter
{
    private readonly SqliteIndex _index;

    public IndexWriter(SqliteIndex index)
    {
        ArgumentNullException.ThrowIfNull(index);
        _index = index;
    }

    /// <summary>Читать комментарии над процедурами для смыслового поиска (нужно чтение файлов модулей).</summary>
    public bool IncludeComments { get; init; } = true;

    /// <summary>
    /// Писать права ролей (<c>Roles/&lt;Имя&gt;/Ext/Rights.xml</c>) в <c>metadata_refs</c>.
    /// Выключено — индекс собирается без прав: на полной выгрузке это десятки тысяч строк.
    /// </summary>
    public bool IncludeRights { get; init; } = true;

    /// <summary>
    /// Куда сообщать время каждой стадии записи. Нужно для замеров: на ERP запись занимает 85 %
    /// времени сборки (399,7 с из 467,5 с), и по стадиям видно, какую из них ускорять.
    /// По умолчанию время стадий никуда не идёт.
    /// </summary>
    public Action<string, TimeSpan>? PhaseReport { get; init; }

    /// <summary>Полностью перезаписывает индекс данными разбора.</summary>
    public IndexWriteResult Write(IDumpSource source, AnalysisResult result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(result);
        var stopwatch = Stopwatch.StartNew();

        return _index.WithLock(() =>
        {
            _index.Execute("PRAGMA synchronous=OFF");
            try
            {
                using var transaction = _index.Connection.BeginTransaction();
                var connection = transaction.Connection!;
                var counters = new Counters();
                var phase = Stopwatch.StartNew();

                Clear(connection);
                Report("очистка", phase);

                WriteFiles(source, connection, counters, cancellationToken);
                Report("файлы", phase);

                WriteNodes(result.Graph.Nodes, connection, counters, cancellationToken);
                Report("узлы", phase);

                WriteEdges(result.Graph.Edges, connection, counters, cancellationToken);
                Report("связи", phase);

                WriteSymbols(source, result.Modules, connection, counters, cancellationToken);
                Report("символы", phase);

                WriteMetadata(result, connection, counters, cancellationToken);
                Report("метаданные", phase);

                WriteForms(result, connection, counters, cancellationToken);
                Report("формы", phase);

                if (IncludeRights)
                {
                    WriteRights(source, connection, counters, cancellationToken);
                    Report("права", phase);
                }

                _index.SetMeta("dump_path", result.SourceName);
                _index.SetMeta("indexed_at", DateTimeOffset.UtcNow.ToString("O"));
                _index.SetMeta("schema_version", IndexSchema.Version.ToString(System.Globalization.CultureInfo.InvariantCulture));
                _index.SetMeta("nodes", counters.Nodes.ToString(System.Globalization.CultureInfo.InvariantCulture));
                _index.SetMeta("edges", counters.Edges.ToString(System.Globalization.CultureInfo.InvariantCulture));

                WriteCounters(connection, counters, cancellationToken);
                transaction.Commit();
                stopwatch.Stop();

                return new IndexWriteResult(
                    counters.Nodes,
                    counters.Edges,
                    counters.Symbols,
                    counters.Calls,
                    counters.MetadataObjects,
                    counters.MetadataItems,
                    counters.MetadataRefs,
                    counters.Files,
                    counters.Forms,
                    stopwatch.Elapsed);
            }
            finally
            {
                _index.Execute("PRAGMA synchronous=NORMAL");
            }
        });
    }

    /// <summary>Сообщает время стадии и запускает отсчёт следующей.</summary>
    private void Report(string phase, Stopwatch watch)
    {
        watch.Stop();
        PhaseReport?.Invoke(phase, watch.Elapsed);
        watch.Restart();
    }

    /// <summary>
    /// Частичная переиндексация модулей: файлы разбираются напрямую, а метаданные из XML
    /// не перечитываются — владелец и вид модуля берутся из самого индекса. Это убирает
    /// основную стоимость обновления (чтение десятков тысяч файлов метаданных).
    /// </summary>
    /// <param name="platform">Модель платформы: по ней разрешаются вызовы методов платформы.</param>
    /// <returns>null, если среди модулей есть неизвестные индексу: тогда нужна полная сборка.</returns>
    public IndexWriteResult? WriteModuleFiles(
        IDumpSource source,
        IReadOnlyList<string> modulePaths,
        PlatformHelpIndex? platform = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(modulePaths);
        var stopwatch = Stopwatch.StartNew();

        return _index.WithLock(() =>
        {
            _index.Execute("PRAGMA synchronous=OFF");
            try
            {
                using var transaction = _index.Connection.BeginTransaction();
                var connection = transaction.Connection!;
                var counters = new Counters();

                var known = ReadModuleInfo(connection, modulePaths);
                var parser = new BslModuleParser();
                var modules = new List<BslModuleInfo>(modulePaths.Count);
                foreach (var path in modulePaths)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!known.TryGetValue(path, out var info))
                    {
                        // Модуля в индексе нет: он появился вместе с новым объектом метаданных,
                        // и разбирать его нужно полной сборкой.
                        transaction.Rollback();
                        return null;
                    }

                    var file = ResolveFile(source, path, cancellationToken);
                    if (file is null)
                    {
                        continue;
                    }

                    string text;
                    using (var stream = source.OpenRead(file))
                    {
                        // Кодировка определяется по содержимому: UTF-8 или запасная CP1251.
                        text = DumpTextReader.ReadAllText(stream);
                    }

                    modules.Add(parser.Parse(new BslModuleSource(path, text, info.OwnerId, info.Kind)));
                }

                if (modules.Count == 0)
                {
                    transaction.Rollback();
                    return new IndexWriteResult(0, 0, 0, 0, 0, 0, 0, 0, 0, stopwatch.Elapsed);
                }

                var routineNames = modules
                    .SelectMany(static module => module.Routines)
                    .Select(static routine => routine.Name.ToLowerInvariant())
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
                var knownNames = routineNames.Count == 0
                    ? []
                    : ReadIds(
                        connection,
                        count => $"SELECT name_lower FROM nodes WHERE kind = 'Routine' AND name_lower IN ({Placeholders(count)})",
                        routineNames);
                var existingNames = new HashSet<string>(knownNames, StringComparer.Ordinal);

                var rows = BuildModuleRows(connection, modules, platform, cancellationToken);
                var scopePaths = new HashSet<string>(modules.Select(static module => module.Path), StringComparer.OrdinalIgnoreCase);

                DeleteScope(connection, new IndexScope([.. scopePaths]), scopePaths, rows.ScopeIds);
                WriteNodes(rows.Nodes, connection, counters, cancellationToken);
                WriteEdges(rows.Edges, connection, counters, cancellationToken);

                // Область перезаписи удаляется вместе со своими обращениями к метаданным: их нужно
                // записать заново, иначе после частичной переиндексации обращения пропадут.
                WriteMetadataRefs(rows.Edges, connection, counters);
                WriteSymbols(source, modules, connection, counters, cancellationToken);
                TouchFiles(source, connection, new IndexScope([.. scopePaths]), counters, cancellationToken);

                // Процедуры, которых раньше не было: вызовы из других модулей шли на внешние
                // заглушки, теперь их можно связать с настоящими узлами.
                var added = routineNames.Where(name => !existingNames.Contains(name)).ToList();
                if (added.Count > 0)
                {
                    RepairIncomingCalls(connection, added, cancellationToken);
                }

                RecountCounters(connection);
                _index.SetMeta("indexed_at", DateTimeOffset.UtcNow.ToString("O"));
                transaction.Commit();
                stopwatch.Stop();

                return new IndexWriteResult(
                    counters.Nodes,
                    counters.Edges,
                    counters.Symbols,
                    counters.Calls,
                    0,
                    0,
                    counters.MetadataRefs,
                    counters.Files,
                    0,
                    stopwatch.Elapsed);
            }
            finally
            {
                _index.Execute("PRAGMA synchronous=NORMAL");
            }
        });
    }

    /// <summary>Владелец и вид модуля по данным индекса: XML для этого читать не нужно.</summary>
    private static Dictionary<string, ModuleIndexInfo> ReadModuleInfo(SqliteConnection connection, IReadOnlyList<string> paths)
    {
        var result = new Dictionary<string, ModuleIndexInfo>(StringComparer.OrdinalIgnoreCase);
        var ids = paths.Select(static path => "module:" + path).ToList();

        foreach (var chunk in Chunk(ids, 400))
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                $"""
                 SELECT n.source_path, n.metadata_kind, COALESCE(e.source_id, '')
                 FROM nodes n
                 LEFT JOIN edges e ON e.target_id = n.id AND e.kind = 'Contains'
                 WHERE n.id IN ({Placeholders(chunk.Count)})
                 """;
            for (var index = 0; index < chunk.Count; index++)
            {
                command.Parameters.AddWithValue("@" + index.ToString(CultureInfo.InvariantCulture), chunk[index]);
            }

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var kind = reader.IsDBNull(1) || !Enum.TryParse<BslModuleKind>(reader.GetString(1), out var parsed)
                    ? BslModuleKind.Unknown
                    : parsed;
                var owner = reader.IsDBNull(2) || reader.GetString(2).Length == 0 ? null : reader.GetString(2);
                result[reader.GetString(0)] = new ModuleIndexInfo(owner, kind);
            }
        }

        return result;
    }

    /// <summary>
    /// Строит узлы и связи для разобранных модулей по тем же правилам, что и полная сборка:
    /// модуль, его процедуры, объявления, вызовы и обращения к метаданным. Цели вызовов
    /// разрешаются по индексу, поэтому частичный разбор не теряет связи между модулями.
    /// </summary>
    private static ModuleRows BuildModuleRows(
        SqliteConnection connection,
        IReadOnlyList<BslModuleInfo> modules,
        PlatformHelpIndex? platform,
        CancellationToken cancellationToken)
    {
        var nodes = new Dictionary<string, GraphNode>(StringComparer.Ordinal);
        var scopeIds = new HashSet<string>(StringComparer.Ordinal);
        var edges = new List<GraphEdge>();
        var keys = new HashSet<(string Source, string Target, GraphEdgeKind Kind, int Line, string? Detail, string? Context)>();

        void AddNode(GraphNode node) => nodes.TryAdd(node.Id, node);

        void AddEdge(
            string sourceId,
            string targetId,
            GraphEdgeKind kind,
            int? line = null,
            string? detail = null,
            string? context = null)
        {
            if (string.Equals(sourceId, targetId, StringComparison.Ordinal))
            {
                return;
            }

            if (!keys.Add((sourceId, targetId, kind, line ?? -1, detail, context)))
            {
                return;
            }

            edges.Add(new GraphEdge(sourceId, targetId, kind, line, detail, context));
        }

        var moduleByOwner = ReadModuleByOwner(connection);
        var localRoutines = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);

        foreach (var module in modules)
        {
            var routines = new Dictionary<string, string>(StringComparer.Ordinal);
            var moduleNodeId = module.Id;
            scopeIds.Add(moduleNodeId);
            AddNode(new GraphNode(
                moduleNodeId,
                GraphNodeKind.Module,
                module.Path,
                module.Path,
                module.Kind.ToString(),
                Tags: new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["owner"] = module.OwnerId,
                    ["routines"] = module.Routines.Count.ToString(CultureInfo.InvariantCulture),
                    ["lines"] = module.LineCount.ToString(CultureInfo.InvariantCulture),
                }));

            if (module.OwnerId is { Length: > 0 } ownerId)
            {
                moduleByOwner.TryAdd(ownerId, moduleNodeId);
            }

            foreach (var routine in module.Routines)
            {
                var routineId = $"routine:{moduleNodeId}#{routine.Name}";
                routines[routine.Name] = routineId;
                scopeIds.Add(routineId);
                AddNode(new GraphNode(
                    routineId,
                    GraphNodeKind.Routine,
                    routine.Name,
                    module.Path,
                    Tags: new Dictionary<string, string?>(StringComparer.Ordinal)
                    {
                        ["routineKind"] = routine.Kind.ToString(),
                        ["export"] = routine.IsExport ? "true" : "false",
                        ["lines"] = routine.LineCount.ToString(CultureInfo.InvariantCulture),
                        ["startLine"] = routine.StartLine.ToString(CultureInfo.InvariantCulture),
                        ["region"] = routine.Region,
                        ["directives"] = routine.Directives.Count == 0 ? null : string.Join(';', routine.Directives),
                    }));
            }

            localRoutines[moduleNodeId] = routines;
        }

        // Какие цели вообще существуют: проверяем одним набором запросов, а не по одной.
        var candidates = new HashSet<string>(StringComparer.Ordinal);
        foreach (var module in modules)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Владелец модуля: связь «объект метаданных → модуль» ставится только если владелец есть.
            if (module.OwnerId is { Length: > 0 } owner)
            {
                candidates.Add(owner);
            }

            foreach (var call in EnumerateCalls(module))
            {
                if (call.IsLocal)
                {
                    candidates.Add($"routine:{module.Id}#{call.Method}");
                    continue;
                }

                var qualifier = call.Qualifier!;
                foreach (var ownerId in new[] { "CommonModule." + qualifier, qualifier })
                {
                    if (moduleByOwner.TryGetValue(ownerId, out var moduleId))
                    {
                        candidates.Add($"routine:{moduleId}#{call.Method}");
                    }
                }
            }

            foreach (var access in EnumerateAccesses(module))
            {
                if (!access.Kind.IsUnknown)
                {
                    candidates.Add(MdNaming.CreateId(access.Kind, access.ObjectName));
                }
            }

            foreach (var reference in EnumerateQueries(module))
            {
                if (!reference.Kind.IsUnknown)
                {
                    candidates.Add(MdNaming.CreateId(reference.Kind, reference.ObjectName));
                }
            }
        }

        var existing = candidates.Count == 0
            ? []
            : ReadIds(connection, count => $"SELECT id FROM nodes WHERE id IN ({Placeholders(count)})", [.. candidates]);
        var known = new HashSet<string>(existing, StringComparer.Ordinal);

        string? ResolveCall(BslModuleInfo module, BslCall call)
        {
            if (call.IsLocal)
            {
                if (localRoutines[module.Id].TryGetValue(call.Method, out var local) || known.Contains($"routine:{module.Id}#{call.Method}"))
                {
                    return local ?? $"routine:{module.Id}#{call.Method}";
                }

                if (platform?.ContainsMember(call.Method) == true)
                {
                    return AddPlatform(module, call.Method);
                }

                return AddPlaceholder($"call:{module.OwnerId ?? module.Path}.{call.Method}", call.Method, GraphNodeKind.External);
            }

            var qualifier = call.Qualifier!;
            foreach (var ownerId in new[] { "CommonModule." + qualifier, qualifier })
            {
                if (moduleByOwner.TryGetValue(ownerId, out var moduleId))
                {
                    var candidate = $"routine:{moduleId}#{call.Method}";
                    if (known.Contains(candidate))
                    {
                        return candidate;
                    }
                }
            }

            if (platform?.ContainsMember(call.Callee) == true)
            {
                return AddPlatform(module, call.Callee);
            }

            // Тип переменной известен (Э2-4): вызов через переменную платформенного типа
            // становится узлом platform:Тип.Метод, как и при полной сборке графа.
            if (TypeInference.TryResolvePlatformCallee(module, call, out var inferredCallee))
            {
                return AddPlatform(module, inferredCallee);
            }

            return AddPlaceholder($"call:{call.Callee}", call.Callee, GraphNodeKind.External);
        }

        string AddPlatform(BslModuleInfo module, string callee)
        {
            var id = "platform:" + callee;
            if (known.Contains(id))
            {
                return id;
            }

            var topic = platform?.Find(callee);
            AddNode(new GraphNode(
                id,
                GraphNodeKind.Platform,
                callee,
                Tags: topic is null
                    ? null
                    : new Dictionary<string, string?>(StringComparer.Ordinal)
                    {
                        ["platformVersion"] = topic.Version.ToString(),
                        ["platformTopic"] = topic.Name,
                        ["platformTitle"] = topic.Title,
                    }));
            return id;
        }

        string AddPlaceholder(string id, string name, GraphNodeKind kind)
        {
            if (!known.Contains(id))
            {
                AddNode(new GraphNode(id, kind, name, IsExternal: true));
            }

            return id;
        }

        foreach (var module in modules)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (module.OwnerId is { Length: > 0 } owner && known.Contains(owner))
            {
                AddEdge(owner, module.Id, GraphEdgeKind.Contains);
            }

            foreach (var routine in module.Routines)
            {
                var routineId = localRoutines[module.Id][routine.Name];
                AddEdge(module.Id, routineId, GraphEdgeKind.Defines);
                foreach (var call in routine.Calls)
                {
                    AddCalls(routineId, module, call);
                }

                foreach (var access in routine.MetadataAccesses)
                {
                    AddAccess(routineId, access);
                }

                foreach (var reference in routine.QueryReferences)
                {
                    AddQuery(routineId, reference);
                }
            }

            foreach (var call in module.Calls)
            {
                AddCalls(module.Id, module, call);
            }

            foreach (var access in module.MetadataAccesses)
            {
                AddAccess(module.Id, access);
            }

            foreach (var reference in module.QueryReferences)
            {
                AddQuery(module.Id, reference);
            }
        }

        void AddCalls(string sourceId, BslModuleInfo module, BslCall call)
        {
            if (ResolveCall(module, call) is { } target)
            {
                AddEdge(sourceId, target, GraphEdgeKind.Calls, call.Line, call.Callee);
            }
        }

        void AddAccess(string sourceId, BslMetadataAccess access)
        {
            if (access.Kind.IsUnknown)
            {
                return;
            }

            var targetId = MdNaming.CreateId(access.Kind, access.ObjectName);
            if (!known.Contains(targetId))
            {
                // Объект есть в конфигурации, но при частичной переиндексации модель метаданных
                // не читается: узел-заглушка нужен, чтобы связь не потерялась.
                AddPlaceholder(targetId, access.ObjectName, GraphNodeKind.External);
            }

            AddEdge(sourceId, targetId, GraphEdgeKind.UsesMetadata, access.Line, access.Text, MetadataRefContexts.Code);
        }

        void AddQuery(string sourceId, BslQueryReference reference)
        {
            if (reference.Kind.IsUnknown)
            {
                return;
            }

            var targetId = MdNaming.CreateId(reference.Kind, reference.ObjectName);
            if (!known.Contains(targetId))
            {
                AddPlaceholder(targetId, reference.ObjectName, GraphNodeKind.External);
            }

            AddEdge(sourceId, targetId, GraphEdgeKind.UsesMetadata, reference.Line, reference.Text, MetadataRefContexts.Query);
        }

        // Пишутся только узлы области и отсутствующие цели: существующие узлы уже в индексе,
        // а повторная запись добавила бы дубли в поисковый индекс.
        var toWrite = new List<GraphNode>(nodes.Count);
        foreach (var (id, node) in nodes)
        {
            if (scopeIds.Contains(id) || !known.Contains(id))
            {
                toWrite.Add(node);
            }
        }

        return new ModuleRows(toWrite, edges, scopeIds);
    }

    private static IEnumerable<BslCall> EnumerateCalls(BslModuleInfo module) =>
        module.Calls.Concat(module.Routines.SelectMany(static routine => routine.Calls));

    private static IEnumerable<BslMetadataAccess> EnumerateAccesses(BslModuleInfo module) =>
        module.MetadataAccesses.Concat(module.Routines.SelectMany(static routine => routine.MetadataAccesses));

    private static IEnumerable<BslQueryReference> EnumerateQueries(BslModuleInfo module) =>
        module.QueryReferences.Concat(module.Routines.SelectMany(static routine => routine.QueryReferences));

    /// <summary>Карта «объект метаданных → узел модуля»: нужна для вызовов вида «Модуль.Метод».</summary>
    private static Dictionary<string, string> ReadModuleByOwner(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT e.source_id, n.id FROM edges e JOIN nodes n ON n.id = e.target_id WHERE e.kind = 'Contains' AND n.kind = 'Module'";
        using var reader = command.ExecuteReader();
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        while (reader.Read())
        {
            result.TryAdd(reader.GetString(0), reader.GetString(1));
        }

        return result;
    }

    /// <summary>Файл выгрузки по пути: источник может отдать его напрямую, иначе перечисляем.</summary>
    private static DumpFile? ResolveFile(IDumpSource source, string path, CancellationToken cancellationToken)
    {
        if (source.FindFile(path) is { } file)
        {
            return file;
        }

        foreach (var candidate in source.EnumerateFiles(cancellationToken))
        {
            if (string.Equals(candidate.RelativePath, path, StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        return null;
    }

    private sealed record ModuleIndexInfo(string? OwnerId, BslModuleKind Kind);

    private sealed record ModuleRows(IReadOnlyList<GraphNode> Nodes, IReadOnlyList<GraphEdge> Edges, HashSet<string> ScopeIds);

    /// <summary>
    /// Убирает из индекса всё, что относится к указанным файлам. Входящие связи удаляются только
    /// у исчезнувших узлов: у процедуры, которая осталась на месте, идентификатор не меняется,
    /// и вызовы из других модулей должны сохраниться.
    /// </summary>
    private static void DeleteScope(
        SqliteConnection connection,
        IndexScope scope,
        HashSet<string> scopePaths,
        HashSet<string> keepIds)
    {
        var paths = new List<string>(scopePaths);
        if (scope.RemovedFiles is { Count: > 0 } removed)
        {
            paths.AddRange(removed);
        }

        if (paths.Count == 0)
        {
            return;
        }

        var oldNodeIds = ReadIds(connection, count => $"SELECT id FROM nodes WHERE source_path IN ({Placeholders(count)})", paths);
        var symbolIds = ReadIds(connection, count => $"SELECT id FROM symbols WHERE module_path IN ({Placeholders(count)})", paths);
        var goneNodeIds = oldNodeIds.Where(id => !keepIds.Contains(id)).ToList();

        ExecuteIds(connection, count => $"DELETE FROM edges WHERE source_id IN ({Placeholders(count)})", oldNodeIds);
        ExecuteIds(connection, count => $"DELETE FROM edges WHERE kind = 'Contains' AND target_id IN ({Placeholders(count)})", oldNodeIds);
        ExecuteIds(connection, count => $"DELETE FROM edges WHERE target_id IN ({Placeholders(count)})", goneNodeIds);
        ExecuteIds(connection, count => $"DELETE FROM metadata_refs WHERE source_id IN ({Placeholders(count)})", oldNodeIds);

        // nodes_fts объявлена с внешним содержимым: записи поиска удаляются по rowid таблицы nodes,
        // и сделать это нужно до удаления самих узлов — иначе FTS не найдёт исходные значения.
        ExecuteIds(
            connection,
            count => $"DELETE FROM nodes_fts WHERE rowid IN (SELECT rowid FROM nodes WHERE id IN ({Placeholders(count)}))",
            oldNodeIds);
        ExecuteIds(connection, count => $"DELETE FROM nodes WHERE id IN ({Placeholders(count)})", oldNodeIds);
        ExecuteIds(connection, count => $"DELETE FROM terms_fts WHERE symbol_id IN ({Placeholders(count)})", symbolIds);
        ExecuteIds(connection, count => $"DELETE FROM symbols WHERE module_path IN ({Placeholders(count)})", paths);
        ExecuteIds(connection, count => $"DELETE FROM files WHERE path IN ({Placeholders(count)})", [.. scope.RemovedFiles ?? []]);
    }

    /// <summary>Обновляет строки файлов, которые перезаписаны, и убирает исчезнувшие.</summary>
    private static void TouchFiles(
        IDumpSource source,
        SqliteConnection connection,
        IndexScope scope,
        Counters counters,
        CancellationToken cancellationToken)
    {
        if (scope.ModuleFiles.Count == 0)
        {
            return;
        }

        // Сначала спрашиваем файлы по одному: обход всей выгрузки ради десятка файлов
        // стоит секунды на большой конфигурации.
        var changed = new List<DumpFile>(scope.ModuleFiles.Count);
        var missing = new List<string>();
        foreach (var path in scope.ModuleFiles)
        {
            var file = source.FindFile(path);
            if (file is null)
            {
                missing.Add(path);
            }
            else
            {
                changed.Add(file);
            }
        }

        if (missing.Count > 0)
        {
            var wanted = new HashSet<string>(missing, StringComparer.OrdinalIgnoreCase);
            foreach (var file in source.EnumerateFiles(cancellationToken))
            {
                if (wanted.Contains(file.RelativePath))
                {
                    changed.Add(file);
                }
            }
        }

        using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR REPLACE INTO files (path, size, mtime) VALUES (@path, @size, @mtime)";
        var pathParameter = command.Parameters.Add("@path", SqliteType.Text);
        var size = command.Parameters.Add("@size", SqliteType.Integer);
        var mtime = command.Parameters.Add("@mtime", SqliteType.Integer);

        foreach (var file in changed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            pathParameter.Value = file.RelativePath;
            size.Value = file.Size;
            mtime.Value = file.LastWriteTimeUtc.ToUnixTimeMilliseconds();
            command.ExecuteNonQuery();
            counters.Files++;
        }
    }

    /// <summary>
    /// Связывает вызовы из других модулей с процедурами, которых раньше не было: до этого
    /// такие вызовы указывали на внешние заглушки по имени.
    /// </summary>
    private static void RepairIncomingCalls(
        SqliteConnection connection,
        IReadOnlyList<string> addedRoutineNames,
        CancellationToken cancellationToken)
    {
        if (addedRoutineNames.Count == 0)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var newRoutines = new HashSet<string>(addedRoutineNames, StringComparer.Ordinal);
        var pending = new List<(long RowId, string Method)>();

        using (var incoming = connection.CreateCommand())
        {
            incoming.CommandText =
                "SELECT rowid, detail FROM edges WHERE kind = 'Calls' AND detail IS NOT NULL AND target_id LIKE 'call:%'";
            using var reader = incoming.ExecuteReader();
            while (reader.Read())
            {
                var method = MethodOf(reader.GetString(1));
                if (newRoutines.Contains(method.ToLowerInvariant()))
                {
                    pending.Add((reader.GetInt64(0), method));
                }
            }
        }

        if (pending.Count == 0)
        {
            return;
        }

        var targets = ResolveRoutineIds(connection, [.. pending.Select(static item => item.Method).Distinct(StringComparer.Ordinal)]);
        using var update = connection.CreateCommand();
        update.CommandText = "UPDATE edges SET target_id = @target WHERE rowid = @row";
        var target = update.Parameters.Add("@target", SqliteType.Text);
        var row = update.Parameters.Add("@row", SqliteType.Integer);
        var repaired = 0;
        foreach (var (rowId, method) in pending)
        {
            if (!targets.TryGetValue(method.ToLowerInvariant(), out var id))
            {
                continue;
            }

            target.Value = id;
            row.Value = rowId;
            update.ExecuteNonQuery();
            repaired++;
        }

        if (repaired > 0)
        {
            using var note = connection.CreateCommand();
            note.CommandText = "INSERT OR REPLACE INTO meta (key, value) VALUES ('repaired_calls', @value)";
            note.Parameters.AddWithValue("@value", repaired.ToString(CultureInfo.InvariantCulture));
            note.ExecuteNonQuery();
        }
    }

    /// <summary>Имя метода из текста вызова: у «Модуль.Метод» берётся часть после последней точки.</summary>
    private static string MethodOf(string callee)
    {
        var separator = callee.LastIndexOf('.');
        return separator < 0 ? callee : callee[(separator + 1)..];
    }

    /// <summary>Узлы процедур по именам: первое совпадение по имени, как и при полной сборке.</summary>
    private static Dictionary<string, string> ResolveRoutineIds(SqliteConnection connection, IReadOnlyList<string> methods)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var lowered = methods.Select(static method => method.ToLowerInvariant()).Distinct(StringComparer.Ordinal).ToList();

        foreach (var chunk in Chunk(lowered, 400))
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                $"SELECT name_lower, id FROM nodes WHERE kind = 'Routine' AND name_lower IN ({Placeholders(chunk.Count)}) ORDER BY length(id), id";
            for (var index = 0; index < chunk.Count; index++)
            {
                command.Parameters.AddWithValue("@" + index.ToString(CultureInfo.InvariantCulture), chunk[index]);
            }

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                result.TryAdd(reader.GetString(0), reader.GetString(1));
            }
        }

        return result;
    }

    /// <summary>Пересчитывает счётчики в meta: частичная переиндексация меняет их неочевидно.</summary>
    private static void RecountCounters(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT OR REPLACE INTO meta (key, value) VALUES
                ('cnt_nodes', (SELECT COUNT(*) FROM nodes)),
                ('cnt_edges', (SELECT COUNT(*) FROM edges)),
                ('cnt_symbols', (SELECT COUNT(*) FROM symbols)),
                ('cnt_calls', (SELECT COUNT(*) FROM edges WHERE kind = 'Calls')),
                ('cnt_metadata_objects', (SELECT COUNT(*) FROM metadata_objects)),
                ('cnt_metadata_items', (SELECT COUNT(*) FROM metadata_items)),
                ('cnt_metadata_refs', (SELECT COUNT(*) FROM metadata_refs)),
                ('cnt_rights_conditions', (SELECT COUNT(*) FROM metadata_refs WHERE context = 'right' AND condition IS NOT NULL)),
                ('cnt_forms', (SELECT COUNT(*) FROM form_models)),
                ('cnt_files', (SELECT COUNT(*) FROM files)),
                ('nodes', (SELECT COUNT(*) FROM nodes)),
                ('edges', (SELECT COUNT(*) FROM edges));
            """;
        command.ExecuteNonQuery();
    }

    private static List<string> ReadIds(SqliteConnection connection, Func<int, string> sql, IReadOnlyList<string> values)
    {
        var result = new List<string>();
        foreach (var chunk in Chunk(values, 400))
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql(chunk.Count);
            for (var index = 0; index < chunk.Count; index++)
            {
                command.Parameters.AddWithValue("@" + index.ToString(CultureInfo.InvariantCulture), chunk[index]);
            }

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                result.Add(reader.GetString(0));
            }
        }

        return result;
    }

    private static void ExecuteIds(SqliteConnection connection, Func<int, string> sql, IReadOnlyList<string> values)
    {
        foreach (var chunk in Chunk(values, 400))
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql(chunk.Count);
            for (var index = 0; index < chunk.Count; index++)
            {
                command.Parameters.AddWithValue("@" + index.ToString(CultureInfo.InvariantCulture), chunk[index]);
            }

            command.ExecuteNonQuery();
        }
    }

    private static string Placeholders(int count) =>
        string.Join(", ", Enumerable.Range(0, count).Select(static index => "@" + index.ToString(CultureInfo.InvariantCulture)));

    private static IEnumerable<List<string>> Chunk(IReadOnlyList<string> values, int size)
    {
        for (var offset = 0; offset < values.Count; offset += size)
        {
            yield return [.. values.Skip(offset).Take(size)];
        }
    }

    /// <summary>
    /// Счётчики пишутся в meta при сборке: инструмент status иначе считает миллионы строк живьём,
    /// а это полсекунды на каждый вызов.
    /// </summary>
    private static void WriteCounters(SqliteConnection connection, Counters counters, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR REPLACE INTO meta (key, value) VALUES (@key, @value)";
        var key = command.Parameters.Add("@key", SqliteType.Text);
        var value = command.Parameters.Add("@value", SqliteType.Text);

        foreach (var (name, count) in new (string Key, int Count)[]
        {
            ("cnt_nodes", counters.Nodes),
            ("cnt_edges", counters.Edges),
            ("cnt_symbols", counters.Symbols),
            ("cnt_calls", counters.Calls),
            ("cnt_metadata_objects", counters.MetadataObjects),
            ("cnt_metadata_items", counters.MetadataItems),
            ("cnt_metadata_refs", counters.MetadataRefs),
            ("cnt_rights_conditions", counters.RightsConditions),
            ("cnt_forms", counters.Forms),
            ("cnt_files", counters.Files),
        })
        {
            cancellationToken.ThrowIfCancellationRequested();
            key.Value = name;
            value.Value = count.ToString(System.Globalization.CultureInfo.InvariantCulture);
            command.ExecuteNonQuery();
        }
    }

    private static void Clear(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            DELETE FROM nodes_fts;
            DELETE FROM terms_fts;
            DELETE FROM edges;
            DELETE FROM symbols;
            DELETE FROM metadata_refs;
            DELETE FROM metadata_items;
            DELETE FROM metadata_objects;
            DELETE FROM form_items;
            DELETE FROM form_models;
            DELETE FROM nodes;
            DELETE FROM files;
            DELETE FROM sqlite_sequence;
            """;
        command.ExecuteNonQuery();
    }

    private void WriteFiles(IDumpSource source, SqliteConnection connection, Counters counters, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR REPLACE INTO files (path, size, mtime) VALUES (@path, @size, @mtime)";
        var path = command.Parameters.Add("@path", SqliteType.Text);
        var size = command.Parameters.Add("@size", SqliteType.Integer);
        var mtime = command.Parameters.Add("@mtime", SqliteType.Integer);

        foreach (var file in source.EnumerateFiles())
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Файлы самого индекса лежат в каталоге выгрузки, но её файлами не являются:
            // иначе индекс считался бы устаревшим сразу после сборки.
            if (DumpState.IsServicePath(file.RelativePath))
            {
                continue;
            }

            path.Value = file.RelativePath;
            size.Value = file.Size;
            mtime.Value = file.LastWriteTimeUtc.ToUnixTimeMilliseconds();
            command.ExecuteNonQuery();
            counters.Files++;
        }
    }

    private void WriteNodes(IReadOnlyList<GraphNode> graphNodes, SqliteConnection connection, Counters counters, CancellationToken cancellationToken)
    {
        using var nodes = connection.CreateCommand();
        nodes.CommandText =
            """
            INSERT OR REPLACE INTO nodes
                (id, kind, name, name_lower, source_path, metadata_kind, is_external, platform_title, platform_version)
            VALUES (@id, @kind, @name, @nameLower, @sourcePath, @metadataKind, @isExternal, @platformTitle, @platformVersion)
            """;
        var id = nodes.Parameters.Add("@id", SqliteType.Text);
        var kind = nodes.Parameters.Add("@kind", SqliteType.Text);
        var name = nodes.Parameters.Add("@name", SqliteType.Text);
        var nameLower = nodes.Parameters.Add("@nameLower", SqliteType.Text);
        var sourcePath = nodes.Parameters.Add("@sourcePath", SqliteType.Text);
        var metadataKind = nodes.Parameters.Add("@metadataKind", SqliteType.Text);
        var isExternal = nodes.Parameters.Add("@isExternal", SqliteType.Integer);
        var platformTitle = nodes.Parameters.Add("@platformTitle", SqliteType.Text);
        var platformVersion = nodes.Parameters.Add("@platformVersion", SqliteType.Text);

        // nodes_fts объявлена с внешним содержимым: имена не дублируются, но поисковые
        // записи нужно создавать явно, указывая rowid строки из nodes.
        using var fts = connection.CreateCommand();
        fts.CommandText = "INSERT INTO nodes_fts (rowid, id, name) VALUES (last_insert_rowid(), @id, @name)";
        var ftsId = fts.Parameters.Add("@id", SqliteType.Text);
        var ftsName = fts.Parameters.Add("@name", SqliteType.Text);

        foreach (var node in graphNodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            id.Value = node.Id;
            kind.Value = node.Kind.ToString();
            name.Value = node.Name;
            nameLower.Value = node.Name.ToLowerInvariant();
            sourcePath.Value = (object?)node.SourcePath ?? DBNull.Value;
            metadataKind.Value = (object?)node.MetadataKind ?? DBNull.Value;
            isExternal.Value = node.IsExternal ? 1 : 0;
            platformTitle.Value = (object?)Tag(node, "platformTitle") ?? DBNull.Value;
            platformVersion.Value = (object?)Tag(node, "platformVersion") ?? DBNull.Value;
            nodes.ExecuteNonQuery();

            ftsId.Value = node.Id;
            ftsName.Value = node.Name;
            fts.ExecuteNonQuery();
            counters.Nodes++;
        }
    }

    private void WriteEdges(IReadOnlyList<GraphEdge> graphEdges, SqliteConnection connection, Counters counters, CancellationToken cancellationToken)
    {
        using var edges = connection.CreateCommand();
        edges.CommandText = "INSERT INTO edges (source_id, target_id, kind, line, detail) VALUES (@source, @target, @kind, @line, @detail)";
        var source = edges.Parameters.Add("@source", SqliteType.Text);
        var target = edges.Parameters.Add("@target", SqliteType.Text);
        var kind = edges.Parameters.Add("@kind", SqliteType.Text);
        var line = edges.Parameters.Add("@line", SqliteType.Integer);
        var detail = edges.Parameters.Add("@detail", SqliteType.Text);

        foreach (var edge in graphEdges)
        {
            cancellationToken.ThrowIfCancellationRequested();
            source.Value = edge.SourceId;
            target.Value = edge.TargetId;
            kind.Value = edge.Kind.ToString();
            line.Value = (object?)edge.Line ?? DBNull.Value;
            detail.Value = (object?)edge.Detail ?? DBNull.Value;
            edges.ExecuteNonQuery();
            counters.Edges++;

            // Отдельная таблица вызовов не нужна: вызов — это связь вида Calls,
            // а имя цели лежит в detail. Так индекс не дублирует 2,1 млн строк.
            if (edge.Kind == GraphEdgeKind.Calls)
            {
                counters.Calls++;
            }
        }
    }

    private void WriteSymbols(
        IDumpSource source,
        IReadOnlyList<BslModuleInfo> modules,
        SqliteConnection connection,
        Counters counters,
        CancellationToken cancellationToken)
    {
        using var insert = connection.CreateCommand();
        insert.CommandText =
            """
            INSERT INTO symbols
                (node_id, module_path, owner_id, name, name_lower, kind, is_export, start_line, end_line,
                 region, parameters, parameters_count, required_count, directives, comment_head)
            VALUES (@nodeId, @module, @owner, @name, @nameLower, @kind, @isExport, @start, @end,
                    @region, @parameters, @parametersCount, @requiredCount, @directives, @comment)
            """;
        var nodeId = insert.Parameters.Add("@nodeId", SqliteType.Text);
        var module = insert.Parameters.Add("@module", SqliteType.Text);
        var owner = insert.Parameters.Add("@owner", SqliteType.Text);
        var name = insert.Parameters.Add("@name", SqliteType.Text);
        var nameLower = insert.Parameters.Add("@nameLower", SqliteType.Text);
        var kind = insert.Parameters.Add("@kind", SqliteType.Text);
        var isExport = insert.Parameters.Add("@isExport", SqliteType.Integer);
        var start = insert.Parameters.Add("@start", SqliteType.Integer);
        var end = insert.Parameters.Add("@end", SqliteType.Integer);
        var region = insert.Parameters.Add("@region", SqliteType.Text);
        var parameters = insert.Parameters.Add("@parameters", SqliteType.Text);
        var parametersCount = insert.Parameters.Add("@parametersCount", SqliteType.Integer);
        var requiredCount = insert.Parameters.Add("@requiredCount", SqliteType.Integer);
        var directives = insert.Parameters.Add("@directives", SqliteType.Text);
        var comment = insert.Parameters.Add("@comment", SqliteType.Text);

        using var termsCommand = connection.CreateCommand();
        termsCommand.CommandText =
            "INSERT INTO terms_fts (symbol_id, name_tokens, comment_head, param_tokens) VALUES (@id, @name, @comment, @params)";
        var termsId = termsCommand.Parameters.Add("@id", SqliteType.Text);
        var termsName = termsCommand.Parameters.Add("@name", SqliteType.Text);
        var termsComment = termsCommand.Parameters.Add("@comment", SqliteType.Text);
        var termsParams = termsCommand.Parameters.Add("@params", SqliteType.Text);

        // Идентификатор символа нужен для связи с таблицей термов: берём фактический rowid,
        // а не порядковый номер, чтобы связь не разъехалась при любом изменении порядка вставки.
        using var lastId = connection.CreateCommand();
        lastId.CommandText = "SELECT last_insert_rowid()";

        foreach (var moduleInfo in modules)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var lines = IncludeComments ? ReadLines(source, moduleInfo.Path) : null;

            foreach (var routine in moduleInfo.Routines)
            {
                var identifier = $"routine:module:{moduleInfo.Path}#{routine.Name}";
                var commentHead = lines is null ? null : CommentHead(lines, routine.StartLine);

                nodeId.Value = identifier;
                module.Value = moduleInfo.Path;
                owner.Value = (object?)moduleInfo.OwnerId ?? DBNull.Value;
                name.Value = routine.Name;
                nameLower.Value = routine.Name.ToLowerInvariant();
                kind.Value = routine.Kind.ToString();
                isExport.Value = routine.IsExport ? 1 : 0;
                start.Value = routine.StartLine;
                end.Value = routine.EndLine;
                region.Value = (object?)routine.Region ?? DBNull.Value;
                parameters.Value = routine.Parameters.Count == 0 ? DBNull.Value : string.Join(", ", routine.Parameters);
                parametersCount.Value = routine.Parameters.Count;
                requiredCount.Value = routine.RequiredCount;
                directives.Value = routine.Directives.Count == 0 ? DBNull.Value : string.Join(", ", routine.Directives);
                comment.Value = (object?)commentHead ?? DBNull.Value;
                insert.ExecuteNonQuery();
                var symbolId = Convert.ToInt64(lastId.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);

                termsId.Value = symbolId.ToString(System.Globalization.CultureInfo.InvariantCulture);
                termsName.Value = Tokenize(routine.Name);
                termsComment.Value = commentHead ?? string.Empty;
                termsParams.Value = routine.Parameters.Count == 0 ? string.Empty : Tokenize(string.Join(' ', routine.Parameters));
                termsCommand.ExecuteNonQuery();

                counters.Symbols++;
            }
        }
    }

    private void WriteMetadata(AnalysisResult result, SqliteConnection connection, Counters counters, CancellationToken cancellationToken)
    {
        using var objects = connection.CreateCommand();
        objects.CommandText =
            """
            INSERT OR REPLACE INTO metadata_objects
                (id, kind, name, name_lower, synonym, synonym_lower, uuid, source_path, comment, is_top_level, parent_id, properties)
            VALUES (@id, @kind, @name, @nameLower, @synonym, @synonymLower, @uuid, @path, @comment, @top, @parent, @properties)
            """;
        var objectId = objects.Parameters.Add("@id", SqliteType.Text);
        var objectKind = objects.Parameters.Add("@kind", SqliteType.Text);
        var objectName = objects.Parameters.Add("@name", SqliteType.Text);
        var objectNameLower = objects.Parameters.Add("@nameLower", SqliteType.Text);
        var synonym = objects.Parameters.Add("@synonym", SqliteType.Text);
        var synonymLower = objects.Parameters.Add("@synonymLower", SqliteType.Text);
        var uuid = objects.Parameters.Add("@uuid", SqliteType.Text);
        var objectPath = objects.Parameters.Add("@path", SqliteType.Text);
        var objectComment = objects.Parameters.Add("@comment", SqliteType.Text);
        var objectTopLevel = objects.Parameters.Add("@top", SqliteType.Integer);
        var objectParent = objects.Parameters.Add("@parent", SqliteType.Text);
        var objectProperties = objects.Parameters.Add("@properties", SqliteType.Text);

        using var items = connection.CreateCommand();
        items.CommandText =
            "INSERT INTO metadata_items (object_id, kind, name, name_lower, type_info, parent_id, synonym, comment) "
            + "VALUES (@object, @kind, @name, @nameLower, @type, @parent, @synonym, @comment)";
        var itemObject = items.Parameters.Add("@object", SqliteType.Text);
        var itemKind = items.Parameters.Add("@kind", SqliteType.Text);
        var itemName = items.Parameters.Add("@name", SqliteType.Text);
        var itemNameLower = items.Parameters.Add("@nameLower", SqliteType.Text);
        var itemType = items.Parameters.Add("@type", SqliteType.Text);
        var itemParent = items.Parameters.Add("@parent", SqliteType.Text);
        var itemSynonym = items.Parameters.Add("@synonym", SqliteType.Text);
        var itemComment = items.Parameters.Add("@comment", SqliteType.Text);

        using var refs = connection.CreateCommand();
        refs.CommandText = "INSERT INTO metadata_refs (source_id, target_id, context, line, detail) VALUES (@source, @target, @context, NULL, @detail)";
        var refSource = refs.Parameters.Add("@source", SqliteType.Text);
        var refTarget = refs.Parameters.Add("@target", SqliteType.Text);
        var refContext = refs.Parameters.Add("@context", SqliteType.Text);
        var refDetail = refs.Parameters.Add("@detail", SqliteType.Text);

        foreach (var obj in result.Metadata.Objects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (obj.Kind == MdKind.Configuration)
            {
                continue;
            }

            objectId.Value = obj.Id;
            objectKind.Value = obj.Kind.Name;
            objectName.Value = obj.Name;
            objectNameLower.Value = obj.Name.ToLowerInvariant();
            synonym.Value = (object?)obj.Synonym ?? DBNull.Value;
            synonymLower.Value = string.IsNullOrWhiteSpace(obj.Synonym)
                ? DBNull.Value
                : obj.Synonym.ToLowerInvariant();
            uuid.Value = obj.Uuid is { } value ? value.ToString() : DBNull.Value;
            // Путь файла объекта, как его показывает карточка: по нему агент читает и правит XML.
            objectPath.Value = (object?)obj.SourcePath ?? DBNull.Value;
            objectComment.Value = (object?)obj.Comment ?? DBNull.Value;
            objectTopLevel.Value = obj.IsTopLevel ? 1 : 0;
            objectParent.Value = obj.Parent is null || obj.Parent.Kind == MdKind.Configuration
                ? DBNull.Value
                : obj.Parent.Id;
            objectProperties.Value = (object?)Properties(obj) ?? DBNull.Value;
            objects.ExecuteNonQuery();
            counters.MetadataObjects++;

            // Ссылки объекта: типы реквизитов, содержимое подсистем, права ролей, формы и макеты.
            foreach (var reference in obj.References)
            {
                refSource.Value = obj.Id;
                refTarget.Value = reference.TargetId;
                refContext.Value = MetadataRefContexts.FromReference(reference.Kind);
                refDetail.Value = (object?)reference.Detail ?? DBNull.Value;
                refs.ExecuteNonQuery();
                counters.MetadataRefs++;
            }

            foreach (var child in obj.Children)
            {
                itemObject.Value = obj.Id;
                itemKind.Value = child.Kind.Name;
                itemName.Value = child.Name;
                itemNameLower.Value = child.Name.ToLowerInvariant();
                itemType.Value = (object?)TypeInfo(obj, child) ?? DBNull.Value;
                itemParent.Value = child.Parent is null || child.Parent.Kind == MdKind.Configuration ? DBNull.Value : child.Parent.Id;
                itemSynonym.Value = (object?)child.Synonym ?? DBNull.Value;
                itemComment.Value = (object?)child.Comment ?? DBNull.Value;
                items.ExecuteNonQuery();
                counters.MetadataItems++;
            }
        }

        // Обращения к метаданным из кода и из текстов запросов: отдельный контекст, чтобы отличать
        // их друг от друга и от типов и прав.
        WriteMetadataRefs(result.Graph.Edges, connection, counters);
    }

    /// <summary>
    /// Пишет обращения к метаданным в <c>metadata_refs</c> из связей <see cref="GraphEdgeKind.UsesMetadata"/>:
    /// контекст берётся из связи, а если он не задан — считается обращением из кода.
    /// </summary>
    private static void WriteMetadataRefs(
        IEnumerable<GraphEdge> graphEdges,
        SqliteConnection connection,
        Counters counters)
    {
        using var refs = connection.CreateCommand();
        refs.CommandText =
            "INSERT INTO metadata_refs (source_id, target_id, context, line, detail) "
            + "VALUES (@source, @target, @context, @line, @detail)";
        var source = refs.Parameters.Add("@source", SqliteType.Text);
        var target = refs.Parameters.Add("@target", SqliteType.Text);
        var context = refs.Parameters.Add("@context", SqliteType.Text);
        var line = refs.Parameters.Add("@line", SqliteType.Integer);
        var detail = refs.Parameters.Add("@detail", SqliteType.Text);

        foreach (var edge in graphEdges)
        {
            if (edge.Kind != GraphEdgeKind.UsesMetadata)
            {
                continue;
            }

            source.Value = edge.SourceId;
            target.Value = edge.TargetId;
            context.Value = string.IsNullOrEmpty(edge.Context) ? MetadataRefContexts.Code : edge.Context;
            line.Value = (object?)edge.Line ?? DBNull.Value;
            detail.Value = (object?)edge.Detail ?? DBNull.Value;
            refs.ExecuteNonQuery();
            counters.MetadataRefs++;
        }
    }

    /// <summary>
    /// Пишет права ролей в <c>metadata_refs</c>: по строке на пару «роль — объект» с контекстом
    /// <see cref="MetadataRefContexts.Right"/>, источником — роль, целью — объект, в <c>detail</c> —
    /// сжатый перечень прав («Read=true;Insert=false») и метка «RLS», а в <c>condition</c> — текст
    /// условия ограничения доступа к данным (NULL, если ограничения нет).
    /// </summary>
    /// <remarks>
    /// Текст условия пишется целиком: в выгрузке встречаются условия в тысячи символов, а SQLite
    /// длину <c>TEXT</c> не ограничивает. Раньше условие в индекс не попадало, и инструмент читал
    /// его из файла роли при каждом запросе — теперь файл нужен только как запасной путь.
    /// Файлы прав разбираются параллельно и потоково, а вставка идёт одной подготовленной командой.
    /// </remarks>
    private static void WriteRights(
        IDumpSource source,
        SqliteConnection connection,
        Counters counters,
        CancellationToken cancellationToken)
    {
        var paths = new List<string>();
        foreach (var file in source.EnumerateFiles(cancellationToken))
        {
            if (RightsDumpReader.IsRightsFile(file.RelativePath))
            {
                paths.Add(file.RelativePath);
            }
        }

        if (paths.Count == 0)
        {
            return;
        }

        var reader = new RightsDumpReader();

        // Условия читаются вместе с правами: строка индекса собирается за один проход по файлу роли.
        var rows = new ConcurrentBag<(string RoleId, string TargetId, string Detail, string? Condition)>();
        Parallel.ForEach(
            paths,
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = cancellationToken },
            path =>
            {
                try
                {
                    using var stream = source.OpenRead(new DumpFile(path, 0, DateTimeOffset.UnixEpoch));
                    var result = reader.Read(stream, path, includeConditions: true);
                    foreach (var obj in result.Rights.Objects)
                    {
                        if (!obj.IsResolved)
                        {
                            continue;
                        }

                        var detail = RightsDetail.Format(obj.Rights, obj.HasRestriction);
                        if (detail.Length > 0)
                        {
                            rows.Add((result.Rights.RoleId, obj.ObjectId, detail, obj.Condition));
                        }
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // Права роли не прочитаны: индекс собирается без них, а причину показывает разбор выгрузки.
                }
            });

        using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO metadata_refs (source_id, target_id, context, line, detail, condition) "
            + "VALUES (@source, @target, @context, NULL, @detail, @condition)";
        var sourceParam = command.Parameters.Add("@source", SqliteType.Text);
        var targetParam = command.Parameters.Add("@target", SqliteType.Text);
        var contextParam = command.Parameters.Add("@context", SqliteType.Text);
        var detailParam = command.Parameters.Add("@detail", SqliteType.Text);
        var conditionParam = command.Parameters.Add("@condition", SqliteType.Text);
        contextParam.Value = MetadataRefContexts.Right;

        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            sourceParam.Value = row.RoleId;
            targetParam.Value = row.TargetId;
            detailParam.Value = row.Detail;
            var condition = string.IsNullOrWhiteSpace(row.Condition) ? null : row.Condition;
            conditionParam.Value = (object?)condition ?? DBNull.Value;
            command.ExecuteNonQuery();
            counters.MetadataRefs++;
            if (condition is not null)
            {
                counters.RightsConditions++;
            }
        }
    }

    /// <summary>
    /// Записывает описания форм: саму форму со счётчиками состава и её строки — реквизиты, элементы,
    /// команды и обработчики событий. Реквизиты пишутся раньше элементов, чтобы при чтении обратно
    /// привязка «элемент → реквизит» восстанавливалась по DataPath однозначно.
    /// </summary>
    private void WriteForms(AnalysisResult result, SqliteConnection connection, Counters counters, CancellationToken cancellationToken)
    {
        using var forms = connection.CreateCommand();
        forms.CommandText =
            """
            INSERT OR REPLACE INTO form_models
                (id, name, name_lower, form_kind, source_path, module_path, object_id,
                 attribute_count, element_count, command_count, handler_count, resolved_handler_count)
            VALUES (@id, @name, @nameLower, @kind, @path, @modulePath, @object,
                    @attributes, @elements, @commands, @handlers, @resolved)
            """;
        var formId = forms.Parameters.Add("@id", SqliteType.Text);
        var formName = forms.Parameters.Add("@name", SqliteType.Text);
        var formNameLower = forms.Parameters.Add("@nameLower", SqliteType.Text);
        var formKind = forms.Parameters.Add("@kind", SqliteType.Text);
        var formPath = forms.Parameters.Add("@path", SqliteType.Text);
        var formModulePath = forms.Parameters.Add("@modulePath", SqliteType.Text);
        var formObject = forms.Parameters.Add("@object", SqliteType.Text);
        var formAttributes = forms.Parameters.Add("@attributes", SqliteType.Integer);
        var formElements = forms.Parameters.Add("@elements", SqliteType.Integer);
        var formCommands = forms.Parameters.Add("@commands", SqliteType.Integer);
        var formHandlers = forms.Parameters.Add("@handlers", SqliteType.Integer);
        var formResolved = forms.Parameters.Add("@resolved", SqliteType.Integer);

        using var items = connection.CreateCommand();
        items.CommandText =
            """
            INSERT INTO form_items
                (form_id, kind, name, view_kind, data_path, type_info, handler, element_name, command_name, line, is_main, is_resolved)
            VALUES (@form, @kind, @name, @viewKind, @dataPath, @type, @handler, @elementName, @commandName, @line, @isMain, @isResolved)
            """;
        var itemForm = items.Parameters.Add("@form", SqliteType.Text);
        var itemKind = items.Parameters.Add("@kind", SqliteType.Text);
        var itemName = items.Parameters.Add("@name", SqliteType.Text);
        var itemViewKind = items.Parameters.Add("@viewKind", SqliteType.Text);
        var itemDataPath = items.Parameters.Add("@dataPath", SqliteType.Text);
        var itemType = items.Parameters.Add("@type", SqliteType.Text);
        var itemHandler = items.Parameters.Add("@handler", SqliteType.Text);
        var itemElementName = items.Parameters.Add("@elementName", SqliteType.Text);
        var itemCommandName = items.Parameters.Add("@commandName", SqliteType.Text);
        var itemLine = items.Parameters.Add("@line", SqliteType.Integer);
        var itemIsMain = items.Parameters.Add("@isMain", SqliteType.Integer);
        var itemIsResolved = items.Parameters.Add("@isResolved", SqliteType.Integer);

        // Форма и её модуль читаются разными проходами, но связь между ними известна: модуль формы
        // объявляет владельцем саму форму. Путь модуля нужен проверке черновика.
        var moduleByOwner = FormModulePaths(result.Modules);

        foreach (var obj in result.Metadata.Objects)
        {
            if (obj.Form is not { } form)
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();

            formId.Value = obj.Id;
            formName.Value = form.Name;
            formNameLower.Value = form.Name.ToLowerInvariant();
            formKind.Value = form.Kind.ToString();
            formPath.Value = (object?)form.SourcePath ?? DBNull.Value;
            formModulePath.Value = moduleByOwner.TryGetValue(obj.Id, out var modulePath) ? modulePath : DBNull.Value;
            formObject.Value = obj.Parent is null || obj.Parent.Kind == MdKind.Configuration
                ? DBNull.Value
                : obj.Parent.Id;
            formAttributes.Value = form.Attributes.Count;
            formElements.Value = form.Elements.Count;
            formCommands.Value = form.Commands.Count;
            formHandlers.Value = form.Handlers.Count;
            formResolved.Value = form.ResolvedHandlers.Count();
            forms.ExecuteNonQuery();
            counters.Forms++;

            itemForm.Value = obj.Id;
            foreach (var attribute in form.Attributes)
            {
                itemKind.Value = FormItemKinds.Attribute;
                itemName.Value = attribute.Name;
                itemViewKind.Value = DBNull.Value;
                itemDataPath.Value = DBNull.Value;
                itemType.Value = (object?)attribute.TypeText ?? DBNull.Value;
                itemHandler.Value = DBNull.Value;
                itemElementName.Value = DBNull.Value;
                itemCommandName.Value = DBNull.Value;
                itemLine.Value = DBNull.Value;
                itemIsMain.Value = attribute.IsMain ? 1 : 0;
                itemIsResolved.Value = 0;
                items.ExecuteNonQuery();
            }

            foreach (var element in form.Elements)
            {
                itemKind.Value = FormItemKinds.Element;
                itemName.Value = element.Name;
                itemViewKind.Value = element.Kind;
                itemDataPath.Value = (object?)element.DataPath ?? DBNull.Value;
                itemType.Value = DBNull.Value;
                itemHandler.Value = DBNull.Value;
                itemElementName.Value = DBNull.Value;
                itemCommandName.Value = (object?)element.CommandName ?? DBNull.Value;
                itemLine.Value = DBNull.Value;
                itemIsMain.Value = 0;
                itemIsResolved.Value = 0;
                items.ExecuteNonQuery();
            }

            foreach (var command in form.Commands)
            {
                itemKind.Value = FormItemKinds.Command;
                itemName.Value = command.Name;
                itemViewKind.Value = DBNull.Value;
                itemDataPath.Value = DBNull.Value;
                itemType.Value = DBNull.Value;
                itemHandler.Value = (object?)command.Handler ?? DBNull.Value;
                itemElementName.Value = DBNull.Value;
                itemCommandName.Value = (object?)command.CommandName ?? DBNull.Value;
                itemLine.Value = DBNull.Value;
                itemIsMain.Value = 0;
                itemIsResolved.Value = 0;
                items.ExecuteNonQuery();
            }

            foreach (var handler in form.Handlers)
            {
                itemKind.Value = FormItemKinds.Handler;
                itemName.Value = handler.Event;
                itemViewKind.Value = DBNull.Value;
                itemDataPath.Value = DBNull.Value;
                itemType.Value = DBNull.Value;
                itemHandler.Value = handler.Procedure;
                itemElementName.Value = (object?)handler.Element ?? DBNull.Value;
                itemCommandName.Value = DBNull.Value;
                itemLine.Value = (object?)handler.Line ?? DBNull.Value;
                itemIsMain.Value = 0;
                itemIsResolved.Value = handler.Resolved ? 1 : 0;
                items.ExecuteNonQuery();
            }
        }
    }

    private static string? Tag(GraphNode node, string key) =>
        node.Tags is not null && node.Tags.TryGetValue(key, out var value) ? value : null;

    /// <summary>
    /// Пути модулей форм по идентификатору формы: модуль формы объявляет владельцем саму форму,
    /// поэтому связь «форма → её модуль» восстанавливается из разбора без чтения XML заново.
    /// </summary>
    private static Dictionary<string, string> FormModulePaths(IReadOnlyList<BslModuleInfo> modules)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var module in modules)
        {
            if (module.Kind == BslModuleKind.FormModule && module.OwnerId is { Length: > 0 } owner)
            {
                result.TryAdd(owner, module.Path);
            }
        }

        return result;
    }

    /// <summary>Свойства объекта метаданных одним JSON-объектом: их читает карточка объекта.</summary>
    private static string? Properties(MdObject obj)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in obj.Properties)
        {
            if (!string.IsNullOrWhiteSpace(property.Value) && values.Count < 40)
            {
                values[property.Key] = property.Value;
            }
        }

        return values.Count == 0 ? null : JsonSerializer.Serialize(values);
    }

    /// <summary>
    /// Тип реквизита: сначала собственные ссылки вложенного объекта (в XML тип описан внутри самого
    /// реквизита), затем — ссылки владельца, отобранные по имени реквизита.
    /// </summary>
    private static string? TypeInfo(MdObject owner, MdObject child)
    {
        var types = new List<string>();
        Collect(child.References, static _ => true);
        Collect(
            owner.References,
            reference => string.IsNullOrEmpty(reference.Detail)
                || reference.Detail!.Contains(child.Name, StringComparison.OrdinalIgnoreCase));

        return types.Count == 0 ? null : string.Join(", ", types);

        void Collect(IReadOnlyList<MdReference> references, Func<MdReference, bool> matches)
        {
            foreach (var reference in references)
            {
                if (types.Count >= 8)
                {
                    return;
                }

                if (reference.Kind == MdReferenceKind.Type
                    && matches(reference)
                    && !types.Contains(reference.TargetId, StringComparer.Ordinal))
                {
                    types.Add(reference.TargetId);
                }
            }
        }
    }

    private static string[]? ReadLines(IDumpSource source, string path)
    {
        try
        {
            using var stream = source.OpenRead(new DumpFile(path, 0, default));

            // Кодировка определяется по содержимому: UTF-8 или запасная CP1251.
            return DumpTextReader.ReadLines(stream);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or KeyNotFoundException)
        {
            return null;
        }
    }

    /// <summary>До трёх строк комментария непосредственно над процедурой.</summary>
    private static string? CommentHead(string[] lines, int startLine)
    {
        var collected = new List<string>(3);
        for (var index = startLine - 2; index >= 0 && collected.Count < 3; index--)
        {
            if (index >= lines.Length)
            {
                continue;
            }

            var line = lines[index].Trim();
            if (line.Length == 0 || line.StartsWith('&'))
            {
                continue;
            }

            if (!line.StartsWith("//", StringComparison.Ordinal))
            {
                break;
            }

            collected.Add(line.TrimStart('/').Trim());
        }

        if (collected.Count == 0)
        {
            return null;
        }

        collected.Reverse();
        return string.Join(' ', collected);
    }

    /// <summary>Разбивает идентификатор на слова: «ЗначениеРеквизитаОбъекта» → «значение реквизит объекта».</summary>
    internal static string Tokenize(string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(identifier.Length + 8);
        for (var index = 0; index < identifier.Length; index++)
        {
            var current = identifier[index];
            if (current is '_' or '.' or '(' or ')' or ',')
            {
                builder.Append(' ');
                continue;
            }

            if (index > 0 && char.IsUpper(current) && !char.IsUpper(identifier[index - 1]))
            {
                builder.Append(' ');
            }

            builder.Append(char.ToLowerInvariant(current));
        }

        return builder.ToString();
    }

    private static string LastSegment(string id)
    {
        var separator = id.LastIndexOfAny(['.', '/', '#', ':']);
        return separator < 0 || separator == id.Length - 1 ? id : id[(separator + 1)..];
    }

    private sealed class Counters
    {
        internal int Nodes;
        internal int Edges;
        internal int Symbols;
        internal int Calls;
        internal int MetadataObjects;
        internal int MetadataItems;
        internal int MetadataRefs;
        internal int RightsConditions;
        internal int Forms;
        internal int Files;
    }
}
