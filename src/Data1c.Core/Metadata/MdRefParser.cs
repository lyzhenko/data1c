namespace Data1c.Core.Metadata;

/// <summary>
/// Разбор строковых ссылок на объекты метаданных, встречающихся в выгрузке 1С:
/// <c>cfg:CatalogRef.Товары</c>, <c>CatalogObject.Товары</c>, <c>Catalog.Товары.StandardAttribute.Code</c>,
/// <c>CommonModule.ОбщегоНазначения</c>.
/// </summary>
public static class MdRefParser
{
    /// <summary>
    /// Пытается разобрать ссылку. Возвращает вид и имя объекта, а остаток пути — в <paramref name="rest"/>.
    /// </summary>
    /// <param name="text">Текст ссылки.</param>
    /// <param name="kind">Вид объекта-цели.</param>
    /// <param name="name">Имя объекта-цели.</param>
    /// <param name="rest">Остаток ссылки после вида и имени (например, «StandardAttribute.Code»).</param>
    public static bool TryParse(string text, out MdKind kind, out string name, out string? rest)
    {
        kind = MdKind.Unknown;
        name = string.Empty;
        rest = null;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var value = text.Trim();

        var colon = value.IndexOf(':');
        if (colon > 0)
        {
            var prefix = value[..colon];
            var tail = value[(colon + 1)..];
            if (MdNaming.TryKindFromTypePrefix(prefix, out var prefixedKind))
            {
                return TrySplitTail(prefixedKind, tail, out kind, out name, out rest);
            }

            // Пространство имён XML (cfg, xr, ent) — разбираем то, что после двоеточия.
            if (prefix is "cfg" or "xr" or "ent" or "app" or "cmi" or "lf" or "style" or "sys" or "v8" or "v8ui" or "web" or "win" or "xen" or "xpr" or "xs" or "xsi")
            {
                value = tail;
            }
            else
            {
                return false;
            }
        }

        var separator = value.IndexOf('.');
        if (separator <= 0 || separator == value.Length - 1)
        {
            return false;
        }

        var head = value[..separator];
        var remainder = value[(separator + 1)..];
        if (!MdNaming.TryKindFromTypePrefix(head, out var headKind))
        {
            return false;
        }

        return TrySplitTail(headKind, remainder, out kind, out name, out rest);
    }

    /// <summary>Пытается получить канонический идентификатор «Catalog.Товары».</summary>
    public static bool TryParseObjectId(string text, out string id)
    {
        id = string.Empty;
        if (!TryParse(text, out var kind, out var name, out _))
        {
            return false;
        }

        id = MdNaming.CreateId(kind, name);
        return true;
    }

    private static bool TrySplitTail(MdKind headKind, string tail, out MdKind kind, out string name, out string? rest)
    {
        kind = headKind;
        rest = null;

        var separator = tail.IndexOf('.');
        if (separator < 0)
        {
            name = tail;
            return name.Length > 0;
        }

        name = tail[..separator];
        rest = tail[(separator + 1)..];
        return name.Length > 0;
    }
}
