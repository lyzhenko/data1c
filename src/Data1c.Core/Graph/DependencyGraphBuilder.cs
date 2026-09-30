using Data1c.Core.Bsl;
using Data1c.Core.Metadata;
using Data1c.Core.Platform;

namespace Data1c.Core.Graph;

/// <summary>
/// Собирает граф зависимостей из модели метаданных и разобранных модулей BSL:
/// вложенность объектов, ссылки метаданных, объявления процедур, вызовы и обращения к метаданным.
/// </summary>
public sealed class DependencyGraphBuilder
{
    private readonly Dictionary<string, GraphNode> _nodes = new(StringComparer.Ordinal);
    private readonly List<GraphEdge> _edges = [];
    private readonly HashSet<(string Source, string Target, GraphEdgeKind Kind, int Line, string? Detail)> _edgeKeys = [];
    private readonly Dictionary<(string ModuleId, string Routine), string> _routineIndex = [];
    private readonly Dictionary<string, string> _moduleByOwner = new(StringComparer.Ordinal);

    private DependencyGraphOptions _options = new();
    private PlatformHelpIndex? _platform;

    /// <summary>
    /// Строит граф. Если передан <paramref name="platform"/>, неразрешённые вызовы проверяются
    /// по справке платформы и получают собственный тип узла вместо безымянной заглушки.
    /// </summary>
    public DependencyGraph Build(
        MdObjectModel metadata,
        IReadOnlyList<BslModuleInfo> modules,
        DependencyGraphOptions? options = null,
        PlatformHelpIndex? platform = null)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(modules);
        _options = options ?? new DependencyGraphOptions();
        _platform = platform is { IsAvailable: true } ? platform : null;

        _nodes.Clear();
        _edges.Clear();
        _edgeKeys.Clear();
        _routineIndex.Clear();
        _moduleByOwner.Clear();

        AddMetadataTree(metadata);
        AddModules(modules);
        AddMetadataReferences(metadata);
        AddCodeLinks(modules);

        var nodes = _nodes.Values
            .OrderBy(static n => n.Kind)
            .ThenBy(static n => n.Id, StringComparer.Ordinal)
            .ToList();

        return new DependencyGraph(nodes, _edges);
    }

    private void AddMetadataTree(MdObjectModel metadata)
    {
        AddNode(new GraphNode(
            metadata.Configuration.Id,
            GraphNodeKind.Configuration,
            metadata.Configuration.Name,
            metadata.Configuration.SourcePath,
            Tags: BuildTags(metadata.Configuration)));

        foreach (var child in metadata.Configuration.Children)
        {
            AddMetadataObject(child, metadata.Configuration.Id);
        }
    }

    private void AddMetadataObject(MdObject obj, string parentId)
    {
        if (!_options.IncludeNestedObjects && IsNestedBookkeepingKind(obj.Kind))
        {
            return;
        }

        AddNode(new GraphNode(
            obj.Id,
            GraphNodeKind.MetadataObject,
            obj.Name,
            obj.SourcePath,
            obj.Kind.Name,
            Tags: BuildTags(obj)));

        if (_options.IncludeContainment)
        {
            AddEdge(parentId, obj.Id, GraphEdgeKind.Contains);
        }

        foreach (var child in obj.Children)
        {
            AddMetadataObject(child, obj.Id);
        }
    }

    private void AddModules(IReadOnlyList<BslModuleInfo> modules)
    {
        if (!_options.IncludeModules)
        {
            return;
        }

        foreach (var module in modules)
        {
            AddNode(new GraphNode(
                module.Id,
                GraphNodeKind.Module,
                module.Path,
                module.Path,
                module.Kind.ToString(),
                Tags: new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["owner"] = module.OwnerId,
                    ["routines"] = module.Routines.Count.ToString(),
                    ["lines"] = module.LineCount.ToString(),
                }));

            if (module.OwnerId is { Length: > 0 } ownerId)
            {
                _moduleByOwner.TryAdd(ownerId, module.Id);
                if (_options.IncludeContainment && _nodes.ContainsKey(ownerId))
                {
                    AddEdge(ownerId, module.Id, GraphEdgeKind.Contains);
                }
            }

            if (!_options.IncludeRoutines)
            {
                continue;
            }

            foreach (var routine in module.Routines)
            {
                var routineId = RoutineId(module, routine);
                _routineIndex.TryAdd((module.Id, routine.Name), routineId);
                AddNode(new GraphNode(
                    routineId,
                    GraphNodeKind.Routine,
                    routine.Name,
                    module.Path,
                    Tags: new Dictionary<string, string?>(StringComparer.Ordinal)
                    {
                        ["routineKind"] = routine.Kind.ToString(),
                        ["export"] = routine.IsExport ? "true" : "false",
                        ["lines"] = routine.LineCount.ToString(),
                        ["startLine"] = routine.StartLine.ToString(),
                        ["region"] = routine.Region,
                        ["directives"] = routine.Directives.Count == 0 ? null : string.Join(';', routine.Directives),
                    }));

                if (_options.IncludeContainment)
                {
                    AddEdge(module.Id, routineId, GraphEdgeKind.Defines);
                }
            }
        }
    }

    private void AddMetadataReferences(MdObjectModel metadata)
    {
        if (!_options.IncludeMetadataReferences)
        {
            return;
        }

        foreach (var child in metadata.Configuration.Children)
        {
            AddObjectReferences(child);
        }
    }

    /// <summary>
    /// Добавляет ссылки объекта. Если сам объект не попал в граф (например, реквизит),
    /// ссылки приписываются ближайшему включённому предку, чтобы типы не терялись.
    /// </summary>
    private void AddObjectReferences(MdObject obj)
    {
        var sourceId = _nodes.ContainsKey(obj.Id) ? obj.Id : FindIncludedAncestor(obj);
        if (sourceId is not null)
        {
            foreach (var reference in obj.References)
            {
                var targetId = ResolveReferenceTarget(reference.TargetId, reference.TargetKind, reference.TargetName);
                if (targetId is null)
                {
                    continue;
                }

                var detail = reference.Detail ?? reference.Kind.ToString();
                if (!string.Equals(sourceId, obj.Id, StringComparison.Ordinal))
                {
                    detail = $"{obj.CanonicalName}: {detail}";
                }

                AddEdge(sourceId, targetId, GraphEdgeKind.References, null, detail);
            }
        }

        foreach (var child in obj.Children)
        {
            AddObjectReferences(child);
        }
    }

    private string? FindIncludedAncestor(MdObject obj)
    {
        var current = obj.Parent;
        while (current is not null)
        {
            if (_nodes.ContainsKey(current.Id))
            {
                return current.Id;
            }

            current = current.Parent;
        }

        return null;
    }

    private void AddCodeLinks(IReadOnlyList<BslModuleInfo> modules)
    {
        foreach (var module in modules)
        {
            if (_options.IncludeMetadataAccess)
            {
                AddMetadataAccessEdges(module.Id, module.MetadataAccesses);
            }

            if (_options.IncludeCalls)
            {
                AddCallEdges(module, module.Id, module.Calls);
            }

            if (!_options.IncludeRoutines)
            {
                continue;
            }

            foreach (var routine in module.Routines)
            {
                if (!_routineIndex.TryGetValue((module.Id, routine.Name), out var routineId))
                {
                    continue;
                }

                if (_options.IncludeMetadataAccess)
                {
                    AddMetadataAccessEdges(routineId, routine.MetadataAccesses);
                }

                if (_options.IncludeCalls)
                {
                    AddCallEdges(module, routineId, routine.Calls);
                }
            }
        }
    }

    private void AddCallEdges(BslModuleInfo module, string sourceId, IReadOnlyList<BslCall> calls)
    {
        foreach (var call in calls)
        {
            var targetId = ResolveCall(module, call);
            if (targetId is null)
            {
                continue;
            }

            AddEdge(sourceId, targetId, GraphEdgeKind.Calls, call.Line, call.Callee);
        }
    }

    private string? ResolveCall(BslModuleInfo module, BslCall call)
    {
        if (call.IsLocal)
        {
            if (_routineIndex.TryGetValue((module.Id, call.Method), out var localTarget))
            {
                return localTarget;
            }

            // Глобальная функция платформы: СтрНайти, ЧислоВСтроку, ТипЗнч и т. п.
            if (_platform?.ContainsMember(call.Method) == true)
            {
                return AddPlatformNode(call.Method);
            }

            return _options.IncludeExternalNodes
                ? AddExternalCallNode($"{module.OwnerId ?? module.Path}.{call.Method}")
                : null;
        }

        var qualifier = call.Qualifier!;
        if (_moduleByOwner.TryGetValue(MdNaming.CreateId(MdKind.CommonModule, qualifier), out var commonModuleId) &&
            _routineIndex.TryGetValue((commonModuleId, call.Method), out var target))
        {
            return target;
        }

        if (_moduleByOwner.TryGetValue(qualifier, out var moduleId) &&
            _routineIndex.TryGetValue((moduleId, call.Method), out var ownerTarget))
        {
            return ownerTarget;
        }

        // Метод платформы: Массив.Добавить, ТаблицаЗначений.Свернуть и т. п.
        if (_platform?.ContainsMember(call.Callee) == true)
        {
            return AddPlatformNode(call.Callee);
        }

        if (_options.IncludeExternalNodes)
        {
            // Квалификатор может быть переменной (Объект.Метод), а может быть общим модулем
            // без такого метода. В обоих случаях фиксируем внешнюю цель по тексту вызова.
            return AddExternalCallNode(call.Callee);
        }

        return null;
    }

    /// <summary>Создаёт узел метода платформы, подтверждённый справкой установленной версии.</summary>
    private string AddPlatformNode(string callee)
    {
        var id = "platform:" + callee;
        var topic = _platform?.Find(callee);
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

    private string AddExternalCallNode(string callee)
    {
        var id = "call:" + callee;
        AddNode(new GraphNode(id, GraphNodeKind.External, callee, IsExternal: true));
        return id;
    }

    private void AddMetadataAccessEdges(string sourceId, IReadOnlyList<BslMetadataAccess> accesses)
    {
        foreach (var access in accesses)
        {
            var targetId = ResolveReferenceTarget(
                MdNaming.CreateId(access.Kind, access.ObjectName),
                access.Kind,
                access.ObjectName);

            if (targetId is null)
            {
                continue;
            }

            AddEdge(sourceId, targetId, GraphEdgeKind.UsesMetadata, access.Line, access.Text);
        }
    }

    private string? ResolveReferenceTarget(string targetId, MdKind kind, string name)
    {
        if (kind.IsUnknown)
        {
            return null;
        }

        if (_nodes.ContainsKey(targetId))
        {
            return targetId;
        }

        if (!_options.IncludeExternalNodes)
        {
            return null;
        }

        AddNode(new GraphNode(targetId, GraphNodeKind.External, name, IsExternal: true, MetadataKind: kind.Name));
        return targetId;
    }

    private void AddNode(GraphNode node)
    {
        if (!_nodes.ContainsKey(node.Id))
        {
            _nodes[node.Id] = node;
        }
    }

    private void AddEdge(string sourceId, string targetId, GraphEdgeKind kind, int? line = null, string? detail = null)
    {
        if (string.Equals(sourceId, targetId, StringComparison.Ordinal))
        {
            return;
        }

        var key = (sourceId, targetId, kind, line ?? -1, detail);
        if (!_edgeKeys.Add(key))
        {
            return;
        }

        _edges.Add(new GraphEdge(sourceId, targetId, kind, line, detail));
    }

    private static bool IsNestedBookkeepingKind(MdKind kind) =>
        kind == MdKind.Attribute || kind == MdKind.TabularSection;

    private static string RoutineId(BslModuleInfo module, BslRoutine routine) =>
        $"routine:{module.Id}#{routine.Name}";

    private static Dictionary<string, string?> BuildTags(MdObject obj) =>
        new(StringComparer.Ordinal)
        {
            ["synonym"] = obj.Synonym,
            ["uuid"] = obj.Uuid?.ToString(),
            ["path"] = obj.Directory,
            // Объект известен только по имени из ChildObjects родителя: его XML не прочитан
            // (например, разбор ограничен опцией Sections или файл был занят).
            ["nameOnly"] = obj.IsNameOnlyReference ? "true" : null,
        };
}
