using System.Collections.Concurrent;
using Data1c.Core.Dump;

namespace Data1c.Core.Metadata;

/// <summary>Права одной роли на один объект: строка связи «роль — объект».</summary>
/// <param name="RoleId">Идентификатор роли в модели, графе и индексе: «Role.Менеджер».</param>
/// <param name="RoleName">Имя роли.</param>
/// <param name="RoleFile">Путь файла прав внутри выгрузки.</param>
/// <param name="Rights">Права роли на объект в порядке файла.</param>
/// <param name="HasRestriction">У роли на этот объект включено ограничение доступа к данным (RLS).</param>
public sealed record ObjectRoleRights(
    string RoleId,
    string RoleName,
    string RoleFile,
    IReadOnlyList<RoleRightEntry> Rights,
    bool HasRestriction)
{
    /// <summary>Сколько прав выдано.</summary>
    public int GrantedCount => Rights.Count(static right => right.Value);

    /// <summary>Сколько прав снято.</summary>
    public int DeniedCount => Rights.Count(static right => !right.Value);
}

/// <summary>
/// Сводка прав ролей выгрузки, собранная по файлам <c>Roles/&lt;Имя&gt;/Ext/Rights.xml</c>:
/// какие роли и с какими правами упоминают объект и что может конкретная роль.
/// </summary>
/// <remarks>
/// Разбор потоковый (см. <see cref="RightsDumpReader"/>), файлы читаются один раз при создании сводки,
/// а текст условия RLS в неё не попадает: условия бывают длинными, поэтому они читаются из файла роли
/// по требованию (<see cref="RoleWithConditions"/>, <see cref="Condition"/>).
/// </remarks>
public sealed class RoleRightsCatalog
{
    /// <summary>Сколько замечаний разбора попадает в сводку: битая выгрузка иначе забьёт ответ.</summary>
    private const int MaxWarnings = 50;

    private readonly IDumpSource _source;
    private readonly RightsDumpReader _reader = new();

    /// <summary>Права ролей по имени роли.</summary>
    private readonly Dictionary<string, RoleRights> _roles;

    /// <summary>Роли, упомянувшие объект, по идентификатору объекта.</summary>
    private readonly Dictionary<string, List<ObjectRoleRights>> _objects;

    /// <summary>Путь файла прав по имени роли: нужен чтению условий по требованию.</summary>
    private readonly Dictionary<string, string> _files;

    /// <summary>Разобранные с условиями права ролей: условия читаются только у запрошенных ролей.</summary>
    private readonly ConcurrentDictionary<string, RoleRights> _withConditions = new(StringComparer.Ordinal);

    private RoleRightsCatalog(
        IDumpSource source,
        Dictionary<string, RoleRights> roles,
        Dictionary<string, List<ObjectRoleRights>> objects,
        Dictionary<string, string> files,
        List<string> warnings)
    {
        _source = source;
        _roles = roles;
        _objects = objects;
        _files = files;
        Warnings = warnings;
    }

    /// <summary>Замечания разбора: битые файлы прав и нечитаемые роли.</summary>
    public IReadOnlyList<string> Warnings { get; }

    /// <summary>Сколько ролей с файлами прав нашлось в выгрузке.</summary>
    public int RoleCount => _roles.Count;

    /// <summary>Сколько разных объектов упоминается в правах ролей.</summary>
    public int ObjectsCount => _objects.Count;

    /// <summary>Сколько прав ролей на объекты разобрано (выданных и снятых).</summary>
    public int RightsCount => _roles.Values.Sum(static role => role.Objects.Sum(static obj => obj.Rights.Count));

    /// <summary>Сколько прав выдано.</summary>
    public int GrantedCount => _roles.Values.Sum(static role => role.GrantedCount);

    /// <summary>Сколько прав снято.</summary>
    public int DeniedCount => _roles.Values.Sum(static role => role.DeniedCount);

    /// <summary>У скольких пар «роль — объект» включено ограничение RLS.</summary>
    public int RestrictionCount => _objects.Values.Sum(static list => list.Count(static row => row.HasRestriction));

    /// <summary>Имена ролей в порядке возрастания: нужны подсказке «похожие роли».</summary>
    public IReadOnlyList<string> RoleNames => [.. _roles.Keys.OrderBy(static name => name, StringComparer.Ordinal)];

    /// <summary>
    /// Собирает сводку прав по выгрузке. Файлы разбираются параллельно и потоково;
    /// битые файлы дают замечания, а не исключение.
    /// </summary>
    /// <param name="source">Источник выгрузки.</param>
    /// <param name="rightsPaths">Пути файлов прав, если они уже известны (например, из индекса); иначе обход выгрузки.</param>
    /// <param name="cancellationToken">Отмена разбора.</param>
    public static RoleRightsCatalog Build(
        IDumpSource source,
        IReadOnlyList<string>? rightsPaths = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        var paths = rightsPaths is { Count: > 0 }
            ? rightsPaths.Select(DumpPath.Normalize).Where(RightsDumpReader.IsRightsFile).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            : source.EnumerateFiles(cancellationToken).Select(static file => file.RelativePath).Where(RightsDumpReader.IsRightsFile).ToList();

        var reader = new RightsDumpReader();
        var parsed = new ConcurrentBag<RoleRights>();
        var warnings = new ConcurrentBag<string>();

        Parallel.ForEach(
            paths,
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = cancellationToken },
            path =>
            {
                try
                {
                    using var stream = source.OpenRead(new DumpFile(path, 0, DateTimeOffset.UnixEpoch));

                    // Условия RLS при сборке сводки не читаются: их текст запрашивается отдельно.
                    var result = reader.Read(stream, path, includeConditions: false);
                    parsed.Add(result.Rights);
                    foreach (var warning in result.Warnings)
                    {
                        warnings.Add(warning);
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    warnings.Add($"Права роли «{path}» не прочитаны: {exception.Message}");
                }
            });

        var roles = new Dictionary<string, RoleRights>(StringComparer.Ordinal);
        var objects = new Dictionary<string, List<ObjectRoleRights>>(StringComparer.Ordinal);
        var files = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var role in parsed)
        {
            // Одна и та же роль может прийти из базы и из расширения: в сводке остаётся одна запись.
            roles[role.Role] = role;
            files[role.Role] = role.SourcePath;
            foreach (var obj in role.Objects)
            {
                if (!obj.IsResolved)
                {
                    continue;
                }

                if (!objects.TryGetValue(obj.ObjectId, out var list))
                {
                    objects[obj.ObjectId] = list = [];
                }

                list.Add(new ObjectRoleRights(role.RoleId, role.Role, role.SourcePath, obj.Rights, obj.HasRestriction));
            }
        }

        return new RoleRightsCatalog(
            source,
            roles,
            objects,
            files,
            [.. warnings.Take(MaxWarnings)]);
    }

    /// <summary>Роли, у которых есть права на объект (пустой список — объект в правах не упомянут).</summary>
    /// <param name="objectId">Идентификатор объекта метаданных: «Catalog.Товары».</param>
    public IReadOnlyList<ObjectRoleRights> RolesOnObject(string objectId)
    {
        ArgumentNullException.ThrowIfNull(objectId);
        return _objects.TryGetValue(objectId.Trim(), out var list) ? list : [];
    }

    /// <summary>Права роли по имени, идентификатору («Role.Менеджер») или пути файла прав.</summary>
    /// <param name="roleIdOrName">Имя роли, её идентификатор или путь файла прав.</param>
    /// <param name="role">Найденные права роли без текстов условий RLS.</param>
    public bool TryGetRole(string roleIdOrName, out RoleRights role)
    {
        ArgumentNullException.ThrowIfNull(roleIdOrName);
        role = null!;
        var name = NormalizeRoleName(roleIdOrName);
        if (name.Length == 0)
        {
            return false;
        }

        if (_roles.TryGetValue(name, out var exact))
        {
            role = exact;
            return true;
        }

        // Имена ролей в конфигурации чувствительны к регистру, но в запросе агента регистр может отличаться.
        foreach (var (key, value) in _roles)
        {
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
            {
                role = value;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Права роли с текстами условий RLS: файл роли читается заново по требованию и запоминается.
    /// Если файл недоступен, возвращаются права из сводки — без условий.
    /// </summary>
    /// <param name="roleIdOrName">Имя роли, её идентификатор или путь файла прав.</param>
    public RoleRights? RoleWithConditions(string roleIdOrName)
    {
        if (!TryGetRole(roleIdOrName, out var role))
        {
            return null;
        }

        if (_withConditions.TryGetValue(role.Role, out var cached))
        {
            return cached;
        }

        if (!_files.TryGetValue(role.Role, out var path))
        {
            return role;
        }

        RoleRights detailed;
        try
        {
            using var stream = _source.OpenRead(new DumpFile(path, 0, DateTimeOffset.UnixEpoch));
            detailed = _reader.Read(stream, path, roleName: role.Role, includeConditions: true).Rights;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Условия недоступны: сводка прав всё равно остаётся пригодной.
            detailed = role;
        }

        _withConditions[role.Role] = detailed;
        return detailed;
    }

    /// <summary>
    /// Текст условия RLS роли на объект: читается из файла роли по требованию.
    /// Возвращает null, если условия нет или файл недоступен.
    /// </summary>
    /// <param name="roleIdOrName">Имя роли, её идентификатор или путь файла прав.</param>
    /// <param name="objectId">Идентификатор объекта метаданных.</param>
    public string? Condition(string roleIdOrName, string objectId)
    {
        var role = RoleWithConditions(roleIdOrName);
        if (role is null)
        {
            return null;
        }

        foreach (var obj in role.Objects)
        {
            if (obj.HasRestriction && string.Equals(obj.ObjectId, objectId, StringComparison.Ordinal))
            {
                return obj.Condition;
            }
        }

        return null;
    }

    /// <summary>Имя роли без префикса вида, пути файла прав и лишних пробелов.</summary>
    /// <param name="roleIdOrName">Имя роли, её идентификатор или путь файла прав.</param>
    public static string NormalizeRoleName(string roleIdOrName)
    {
        ArgumentNullException.ThrowIfNull(roleIdOrName);
        var text = roleIdOrName.Trim();
        if (text.EndsWith("Rights.xml", StringComparison.OrdinalIgnoreCase))
        {
            return RightsDumpReader.RoleNameFromPath(text);
        }

        const string prefix = "Role.";
        return text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? text[prefix.Length..].Trim() : text;
    }
}
