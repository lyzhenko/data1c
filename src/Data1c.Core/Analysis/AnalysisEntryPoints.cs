using Data1c.Core.Bsl;
using Data1c.Core.Metadata;

namespace Data1c.Core.Analysis;

/// <summary>
/// Точки входа из разбора выгрузки в памяти: тот же набор, что даёт индекс, но без SQLite.
/// Нужен, когда сервер запущен без индекса (<c>--no-index</c> или выгрузка в памяти).
/// </summary>
/// <remarks>
/// Сведения берутся из той же модели, что и в индекс: подписки и задания — объекты метаданных
/// (<see cref="MdKind.EventSubscription"/>, <see cref="MdKind.ScheduledJob"/>) со свойствами и
/// ссылками, обработчики форм — <see cref="FormModel.Handlers"/> разобранных форм. Обработчик
/// ищется по ссылке на общий модуль (имя процедуры — в уточнении ссылки), а строка процедуры —
/// в разобранном модуле этого объекта.
/// </remarks>
public sealed class AnalysisEntryPoints : IEntryPointFacts
{
    private readonly AnalysisResult _result;
    private readonly Dictionary<string, BslModuleInfo> _moduleByOwner;
    private readonly Dictionary<string, IReadOnlyList<BslRoutine>> _routinesByModule = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string?> _modulePathByOwner = new(StringComparer.Ordinal);

    /// <summary>Создаёт источник точек входа поверх готового разбора выгрузки.</summary>
    /// <param name="result">Результат разбора выгрузки.</param>
    public AnalysisEntryPoints(AnalysisResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        _result = result;
        _moduleByOwner = new Dictionary<string, BslModuleInfo>(StringComparer.Ordinal);
        foreach (var module in result.Modules)
        {
            if (module.OwnerId is { Length: > 0 } owner)
            {
                _moduleByOwner.TryAdd(owner, module);
            }
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<EntryPointFact> Read(EntryPointFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var limit = Math.Clamp(filter.Limit, 1, EntryPoints.MaxLimit + 1);
        var facts = new List<EntryPointFact>(limit * 3);

        if (filter.Kind is null or EntryPointKind.Subscription)
        {
            facts.AddRange(Subscriptions(filter.Metadata, limit));
        }

        if (filter.Kind is null or EntryPointKind.Job)
        {
            facts.AddRange(Jobs(filter.Metadata, limit));
        }

        if (filter.Kind is null or EntryPointKind.Form)
        {
            facts.AddRange(FormHandlers(filter.Metadata, limit));
        }

        return facts;
    }

    /// <summary>Подписки на события: источник — объект метаданных, обработчик — процедура общего модуля.</summary>
    private List<EntryPointFact> Subscriptions(string? metadata, int limit)
    {
        var result = new List<EntryPointFact>(limit);
        foreach (var obj in _result.Metadata.Objects)
        {
            if (obj.Kind != MdKind.EventSubscription)
            {
                continue;
            }

            var handler = HandlerOf(obj);
            var sources = SourceIds(obj);
            var fact = new EntryPointFact(
                EntryPointKind.Subscription,
                obj.Id,
                obj.Name,
                obj.Id,
                obj.Kind.Name,
                RelatedIds: Related(obj.Id, sources, handler.OwnerId),
                Event: obj.GetProperty("Event"),
                Sources: sources,
                HandlerOwnerId: handler.OwnerId,
                ModulePath: handler.ModulePath,
                Procedure: handler.Procedure,
                Line: handler.Line,
                Resolved: handler.Resolved,
                Properties: Properties(obj),
                SourcePath: obj.SourcePath);

            if (!EntryPoints.Matches(fact, metadata))
            {
                continue;
            }

            result.Add(fact);
            if (result.Count >= limit)
            {
                break;
            }
        }

        return result;
    }

    /// <summary>Регламентные задания: обработчик — процедура общего модуля, расписание в модели не хранится.</summary>
    private List<EntryPointFact> Jobs(string? metadata, int limit)
    {
        var result = new List<EntryPointFact>(limit);
        foreach (var obj in _result.Metadata.Objects)
        {
            if (obj.Kind != MdKind.ScheduledJob)
            {
                continue;
            }

            var handler = HandlerOf(obj);
            var fact = new EntryPointFact(
                EntryPointKind.Job,
                obj.Id,
                obj.Name,
                obj.Id,
                obj.Kind.Name,
                RelatedIds: Related(obj.Id, [], handler.OwnerId),
                Event: null,
                Sources: null,
                HandlerOwnerId: handler.OwnerId,
                ModulePath: handler.ModulePath,
                Procedure: handler.Procedure,
                Line: handler.Line,
                Resolved: handler.Resolved,
                Properties: Properties(obj),
                SourcePath: obj.SourcePath);

            if (!EntryPoints.Matches(fact, metadata))
            {
                continue;
            }

            result.Add(fact);
            if (result.Count >= limit)
            {
                break;
            }
        }

        return result;
    }

    /// <summary>Обработчики событий форм: процедуры модуля формы, найденные при разборе описания формы.</summary>
    private List<EntryPointFact> FormHandlers(string? metadata, int limit)
    {
        var result = new List<EntryPointFact>(limit);
        foreach (var obj in _result.Metadata.Objects)
        {
            if (obj.Form is not { Handlers.Count: > 0 } form)
            {
                continue;
            }

            var modulePath = ModulePathOf(obj.Id);
            var ownerId = obj.Parent is null || obj.Parent.Kind == MdKind.Configuration ? null : obj.Parent.Id;
            foreach (var handler in form.Handlers)
            {
                var fact = new EntryPointFact(
                    EntryPointKind.Form,
                    FormEntryPointId(obj.Id, handler),
                    form.Name,
                    obj.Id,
                    obj.Kind.Name,
                    RelatedIds: Related(obj.Id, ownerId is null ? [] : [ownerId], handlerOwnerId: null),
                    Event: handler.Event,
                    Element: handler.Element,
                    HandlerOwnerId: obj.Id,
                    ModulePath: modulePath,
                    Procedure: handler.Procedure,
                    Line: handler.Line,
                    Resolved: handler.Resolved,
                    SourcePath: obj.SourcePath);

                if (!EntryPoints.Matches(fact, metadata))
                {
                    continue;
                }

                result.Add(fact);
                if (result.Count >= limit)
                {
                    return result;
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Обработчик объекта: ссылка на общий модуль с именем процедуры в уточнении, а если ссылки нет —
    /// строковое свойство <c>Handler</c> (подписка) или <c>MethodName</c> (регламентное задание).
    /// </summary>
    private HandlerInfo HandlerOf(MdObject obj)
    {
        foreach (var reference in obj.References)
        {
            if (reference.TargetKind == MdKind.CommonModule)
            {
                return Resolve(reference.TargetId, reference.Detail);
            }
        }

        var text = obj.GetProperty("Handler") ?? obj.GetProperty("MethodName");
        return EntryPoints.TrySplitHandler(text, out var moduleId, out var procedure) && moduleId is not null
            ? Resolve(moduleId, procedure)
            : default;
    }

    /// <summary>Модуль-владелец и процедура-обработчик: строка берётся из разобранного модуля.</summary>
    private HandlerInfo Resolve(string ownerId, string? procedure)
    {
        var modulePath = ModulePathOf(ownerId);
        if (modulePath is null || string.IsNullOrWhiteSpace(procedure))
        {
            return new HandlerInfo(ownerId, modulePath, procedure, null, false);
        }

        var routines = _routinesByModule.TryGetValue(modulePath, out var cached)
            ? cached
            : _moduleByOwner.TryGetValue(ownerId, out var module)
                ? module.Routines
                : [];

        _routinesByModule[modulePath] = routines;

        var routine = routines.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, procedure, StringComparison.OrdinalIgnoreCase));

        return routine is null
            ? new HandlerInfo(ownerId, modulePath, procedure, null, false)
            : new HandlerInfo(ownerId, modulePath, routine.Name, routine.StartLine, true);
    }

    /// <summary>Путь модуля объекта метаданных: у формы — модуль формы, у общего модуля — его модуль.</summary>
    private string? ModulePathOf(string ownerId) =>
        _modulePathByOwner.TryGetValue(ownerId, out var cached)
            ? cached
            : _modulePathByOwner[ownerId] = _moduleByOwner.TryGetValue(ownerId, out var module) ? module.Path : null;

    /// <summary>Источники подписки: ссылки на объекты, чьи события она слушает.</summary>
    private static IReadOnlyList<string> SourceIds(MdObject obj)
    {
        var sources = new List<string>();
        foreach (var reference in obj.References)
        {
            if (reference.TargetKind == MdKind.CommonModule)
            {
                continue;
            }

            if (reference.Kind is MdReferenceKind.Type or MdReferenceKind.EventSource
                && !sources.Contains(reference.TargetId, StringComparer.Ordinal))
            {
                sources.Add(reference.TargetId);
            }
        }

        return sources;
    }

    /// <summary>Объекты, по которым точка входа находится фильтром: она сама, источники и модуль-обработчик.</summary>
    private static IReadOnlyList<string> Related(string id, IReadOnlyList<string> sources, string? handlerOwnerId)
    {
        var related = new List<string> { id };
        related.AddRange(sources);
        if (handlerOwnerId is { Length: > 0 } owner && !related.Contains(owner, StringComparer.Ordinal))
        {
            related.Add(owner);
        }

        return related;
    }

    /// <summary>Идентификатор обработчика формы: форма, элемент и событие.</summary>
    private static string FormEntryPointId(string formId, FormEventHandler handler) =>
        handler.Element is { Length: > 0 } element ? $"{formId}#{element}.{handler.Event}" : $"{formId}#{handler.Event}";

    /// <summary>Скалярные свойства объекта: у подписки это событие и обработчик, у задания — использование и ключ.</summary>
    private static IReadOnlyDictionary<string, string> Properties(MdObject obj)
    {
        var properties = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in obj.Properties)
        {
            if (!string.IsNullOrWhiteSpace(property.Value) && properties.Count < 16)
            {
                properties[property.Key] = property.Value;
            }
        }

        return properties;
    }

    /// <summary>Сведения об обработчике: модуль-владелец, путь модуля, процедура и её строка.</summary>
    private readonly record struct HandlerInfo(
        string? OwnerId,
        string? ModulePath,
        string? Procedure,
        int? Line,
        bool Resolved);
}
