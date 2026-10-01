using Data1c.Core.Bsl;

namespace Data1c.Core.Metadata;

/// <summary>
/// Связывает обработчики формы с процедурами её модуля. Описание формы (XML) и модуль (BSL)
/// читаются разными проходами: сначала метаданные, потом модули, поэтому связь проставляется
/// отдельным шагом, когда оба списка уже есть.
/// </summary>
public static class FormModelLinker
{
    /// <summary>
    /// Проставляет обработчикам формы строки процедур модуля формы. Модуль ищется по владельцу
    /// (идентификатору формы) и виду модуля <see cref="BslModuleKind.FormModule"/>.
    /// </summary>
    /// <returns>Сколько обработчиков нашло свою процедуру в модуле.</returns>
    public static int Link(MdObjectModel model, IReadOnlyList<BslModuleInfo> modules)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(modules);

        var byOwner = new Dictionary<string, BslModuleInfo>(StringComparer.Ordinal);
        foreach (var module in modules)
        {
            if (module.OwnerId is not { Length: > 0 } owner || module.Kind != BslModuleKind.FormModule)
            {
                continue;
            }

            // У формы один модуль, но при наложении источников их может оказаться два: берём первый.
            byOwner.TryAdd(owner, module);
        }

        if (byOwner.Count == 0)
        {
            return 0;
        }

        var linked = 0;
        foreach (var obj in model.Objects)
        {
            if (obj.Form is not { Handlers.Count: > 0 } form || !byOwner.TryGetValue(obj.Id, out var module))
            {
                continue;
            }

            var routines = new Dictionary<string, BslRoutine>(StringComparer.Ordinal);
            foreach (var routine in module.Routines)
            {
                routines.TryAdd(routine.Name, routine);
            }

            var handlers = new List<FormEventHandler>(form.Handlers.Count);
            foreach (var handler in form.Handlers)
            {
                if (routines.TryGetValue(handler.Procedure, out var routine))
                {
                    handlers.Add(handler with { Line = routine.StartLine, Resolved = true });
                    linked++;
                }
                else
                {
                    handlers.Add(handler);
                }
            }

            obj.Form = form.WithHandlers(handlers);
        }

        return linked;
    }
}
