using System.Text.Json;
using Data1c.Core.Analysis;

namespace Data1c.Store;

/// <summary>
/// Точки входа из SQLite-индекса (Э3-2): подписки на события, регламентные задания и обработчики
/// событий форм.
/// </summary>
/// <remarks>
/// Схема индекса для точек входа не менялась — берётся то, что уже записано:
/// <list type="bullet">
/// <item>подписки и задания — строки <c>metadata_objects</c> своих видов со свойствами в JSON
/// (<c>Event</c>, <c>Handler</c>, <c>MethodName</c>, <c>Use</c>, <c>Predefined</c>, …);</item>
/// <item>обработчик и источники подписки — строки <c>metadata_refs</c>: ссылка на общий модуль
/// хранит имя процедуры в уточнении, ссылки контекста <c>type</c> и <c>event</c> — объекты,
/// чьи события слушает подписка;</item>
/// <item>обработчики форм — строки <c>form_items</c> с видом <c>Handler</c> вместе с их формой
/// из <c>form_models</c>.</item>
/// </list>
/// Строка процедуры-обработчика ищется в <c>symbols</c> модуля-владельца: только найденная
/// процедура получает узел «routine:module:путь#Имя», который принимает инструмент code.
/// <para>
/// Фильтр по объекту метаданных применяется в SQL до предела выдачи, поэтому подходящие точки
/// входа не теряются из-за лимита.
/// </para>
/// </remarks>
public sealed class IndexEntryPoints : IEntryPointFacts
{
    /// <summary>Предел числа ссылок объекта: у подписки на «регистрацию удаления» источников сотни.</summary>
    private const int ReferenceLimit = 1000;

    private readonly IndexReader _reader;
    private readonly Dictionary<string, string?> _modulePathByOwner = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<SymbolRow>> _symbolsByModule = new(StringComparer.Ordinal);

    /// <summary>Создаёт источник точек входа поверх читателя индекса.</summary>
    /// <param name="reader">Читатель готового индекса.</param>
    public IndexEntryPoints(IndexReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        _reader = reader;
    }

    /// <inheritdoc/>
    public IReadOnlyList<EntryPointFact> Read(EntryPointFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var limit = Math.Clamp(filter.Limit, 1, EntryPoints.MaxLimit + 1);
        var facts = new List<EntryPointFact>(limit * 3);

        if (filter.Kind is null or EntryPointKind.Subscription)
        {
            facts.AddRange(ObjectEntryPoints(
                "EventSubscription",
                EntryPointKind.Subscription,
                filter.Metadata,
                limit));
        }

        if (filter.Kind is null or EntryPointKind.Job)
        {
            facts.AddRange(ObjectEntryPoints(
                "ScheduledJob",
                EntryPointKind.Job,
                filter.Metadata,
                limit));
        }

        if (filter.Kind is null or EntryPointKind.Form)
        {
            facts.AddRange(FormHandlers(filter.Metadata, limit));
        }

        return facts;
    }

    /// <summary>
    /// Подписки или регламентные задания: обработчик берётся из ссылки на общий модуль, источники
    /// подписки — из остальных ссылок объекта.
    /// </summary>
    private List<EntryPointFact> ObjectEntryPoints(string kind, EntryPointKind entryPointKind, string? metadata, int limit)
    {
        var result = new List<EntryPointFact>(limit);
        foreach (var row in _reader.FindEntryPointObjects([kind], metadata, limit))
        {
            var references = _reader.ReferencesOf(row.Id, ReferenceLimit);
            var properties = ParseProperties(row.Properties);
            var handler = HandlerOf(properties, references);
            var sources = entryPointKind == EntryPointKind.Subscription ? SourceIds(references) : [];
            var related = new List<string>(sources.Count + 2) { row.Id };
            related.AddRange(sources);
            if (handler.OwnerId is { Length: > 0 } owner && !related.Contains(owner, StringComparer.Ordinal))
            {
                related.Add(owner);
            }

            result.Add(new EntryPointFact(
                entryPointKind,
                row.Id,
                row.Name,
                row.Id,
                row.Kind,
                RelatedIds: related,
                Event: properties.TryGetValue("Event", out var @event) ? @event : null,
                Sources: sources,
                HandlerOwnerId: handler.OwnerId,
                ModulePath: handler.ModulePath,
                Procedure: handler.Procedure,
                Line: handler.Line,
                Resolved: handler.Resolved,
                Properties: properties,
                SourcePath: row.SourcePath));
        }

        return result;
    }

    /// <summary>Обработчики событий форм: процедура, её строка и модуль формы.</summary>
    private List<EntryPointFact> FormHandlers(string? metadata, int limit)
    {
        var result = new List<EntryPointFact>(limit);
        foreach (var row in _reader.FormHandlers(metadata, limit))
        {
            var related = new List<string>(2) { row.FormId };
            if (row.ObjectId is { Length: > 0 } owner && !related.Contains(owner, StringComparer.Ordinal))
            {
                related.Add(owner);
            }

            result.Add(new EntryPointFact(
                EntryPointKind.Form,
                row.Element is { Length: > 0 } element ? $"{row.FormId}#{element}.{row.Event}" : $"{row.FormId}#{row.Event}",
                row.FormName,
                row.FormId,
                "Form",
                RelatedIds: related,
                Event: row.Event,
                Element: row.Element,
                HandlerOwnerId: row.FormId,
                ModulePath: ModulePathOf(row.FormId),
                Procedure: row.Procedure,
                Line: row.Line,
                Resolved: row.Resolved,
                SourcePath: row.SourcePath));
        }

        return result;
    }

    /// <summary>
    /// Обработчик объекта: ссылка на общий модуль (имя процедуры — в уточнении ссылки), а если её
    /// нет — строковое свойство <c>Handler</c> (подписка) или <c>MethodName</c> (регламентное задание).
    /// </summary>
    private HandlerInfo HandlerOf(IReadOnlyDictionary<string, string> properties, IReadOnlyList<MetadataRefRow> references)
    {
        foreach (var reference in references)
        {
            if (reference.TargetId.StartsWith("CommonModule.", StringComparison.Ordinal))
            {
                return Resolve(reference.TargetId, reference.Detail);
            }
        }

        var text = properties.TryGetValue("Handler", out var handler)
            ? handler
            : properties.TryGetValue("MethodName", out var method) ? method : null;

        return EntryPoints.TrySplitHandler(text, out var moduleId, out var procedure) && moduleId is not null
            ? Resolve(moduleId, procedure)
            : default;
    }

    /// <summary>Модуль-владелец и процедура-обработчик: строка берётся из символов модуля.</summary>
    private HandlerInfo Resolve(string ownerId, string? procedure)
    {
        var modulePath = ModulePathOf(ownerId);
        if (modulePath is null || string.IsNullOrWhiteSpace(procedure))
        {
            return new HandlerInfo(ownerId, modulePath, procedure, null, false);
        }

        var symbol = SymbolsOf(modulePath).FirstOrDefault(candidate =>
            string.Equals(candidate.Name, procedure, StringComparison.OrdinalIgnoreCase));

        return symbol is null
            ? new HandlerInfo(ownerId, modulePath, procedure, null, false)
            : new HandlerInfo(ownerId, modulePath, symbol.Name, symbol.StartLine, true);
    }

    /// <summary>Путь модуля объекта метаданных: у общего модуля — его модуль, у формы — модуль формы.</summary>
    private string? ModulePathOf(string ownerId) =>
        _modulePathByOwner.TryGetValue(ownerId, out var cached)
            ? cached
            : _modulePathByOwner[ownerId] = _reader.ModulePaths(ownerId, 1).FirstOrDefault();

    /// <summary>Процедуры модуля: по ним проверяется, что обработчик существует, и берётся строка.</summary>
    private IReadOnlyList<SymbolRow> SymbolsOf(string modulePath) =>
        _symbolsByModule.TryGetValue(modulePath, out var cached)
            ? cached
            : _symbolsByModule[modulePath] = _reader.FindSymbolsInModule(modulePath);

    /// <summary>Источники подписки: ссылки на объекты, чьи события она слушает.</summary>
    private static IReadOnlyList<string> SourceIds(IReadOnlyList<MetadataRefRow> references)
    {
        var sources = new List<string>();
        foreach (var reference in references)
        {
            if (reference.TargetId.StartsWith("CommonModule.", StringComparison.Ordinal))
            {
                continue;
            }

            if (reference.Context is "type" or "event"
                && !sources.Contains(reference.TargetId, StringComparer.Ordinal))
            {
                sources.Add(reference.TargetId);
            }
        }

        return sources;
    }

    /// <summary>Свойства объекта из JSON-строки индекса: писатель складывает их одним объектом.</summary>
    private static IReadOnlyDictionary<string, string> ParseProperties(string? properties)
    {
        if (string.IsNullOrWhiteSpace(properties))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, string?>>(properties);
            if (parsed is null)
            {
                return new Dictionary<string, string>(StringComparer.Ordinal);
            }

            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var property in parsed)
            {
                if (!string.IsNullOrWhiteSpace(property.Value))
                {
                    result[property.Key] = property.Value;
                }
            }

            return result;
        }
        catch (JsonException)
        {
            // Свойства пишет сам индекс: нечитаемая строка не должна ломать точки входа.
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    /// <summary>Сведения об обработчике: модуль-владелец, путь модуля, процедура и её строка.</summary>
    private readonly record struct HandlerInfo(
        string? OwnerId,
        string? ModulePath,
        string? Procedure,
        int? Line,
        bool Resolved);
}
