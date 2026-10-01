using Data1c.Core.Bsl;
using Data1c.Core.Metadata;

namespace Data1c.Core.Analysis;

/// <summary>
/// Факты о конфигурации поверх готового разбора в памяти: нужны проверке черновика там, где
/// SQLite-индекс ещё не собран (маленькая выгрузка или работа без индекса).
/// </summary>
/// <remarks>
/// Индекс — основной путь: он не требует держать весь разбор в памяти. Этот источник отвечает
/// по уже посчитанному <see cref="AnalysisResult"/> и перебирает объекты линейно, поэтому
/// предназначен для небольших конфигураций и тестов.
/// </remarks>
public sealed class AnalysisDraftContext : IDraftContext
{
    private readonly AnalysisResult _result;
    private readonly IReadOnlyList<BslModuleInfo> _modules;

    /// <summary>Создаёт контекст над готовым разбором выгрузки.</summary>
    /// <param name="result">Результат <see cref="DumpAnalyzer"/>.</param>
    public AnalysisDraftContext(AnalysisResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        _result = result;
        _modules = result.Modules;
    }

    /// <inheritdoc/>
    public IReadOnlyList<DraftSymbol> FindSymbols(string name, int limit = 20, bool exact = false)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return [];
        }

        var text = name.Trim();
        var bounded = Math.Max(1, limit);
        var found = new List<(int Rank, DraftSymbol Symbol)>();
        foreach (var module in _modules)
        {
            foreach (var routine in module.Routines)
            {
                var rank = Rank(routine.Name, text, exact);
                if (rank >= 0)
                {
                    found.Add((rank, ToDraft(module, routine)));
                }
            }
        }

        return
        [
            .. found
                .OrderBy(static item => item.Rank)
                .ThenBy(static item => item.Symbol.Name.Length)
                .ThenBy(static item => item.Symbol.Name, StringComparer.Ordinal)
                .Take(bounded)
                .Select(static item => item.Symbol)
        ];
    }

    /// <inheritdoc/>
    public IReadOnlyList<DraftSymbol> FindSymbolsByOwner(string ownerId, int limit = 50)
    {
        if (string.IsNullOrWhiteSpace(ownerId))
        {
            return [];
        }

        var bounded = Math.Max(1, limit);
        var found = new List<DraftSymbol>();
        foreach (var module in _modules)
        {
            if (!string.Equals(module.OwnerId, ownerId.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var routine in module.Routines)
            {
                found.Add(ToDraft(module, routine));
            }
        }

        return [.. found.Take(bounded)];
    }

    /// <inheritdoc/>
    public IReadOnlyList<DraftMetadataObject> FindMetadataObjects(string query, int limit = 20)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        var text = query.Trim();
        var bounded = Math.Max(1, limit);
        var found = new List<DraftMetadataObject>();
        foreach (var item in _result.Metadata.Objects)
        {
            if (item.Kind == MdKind.Configuration)
            {
                continue;
            }

            var nameHit = item.Name.Contains(text, StringComparison.OrdinalIgnoreCase);
            var synonymHit = item.Synonym?.Contains(text, StringComparison.OrdinalIgnoreCase) == true;
            if (nameHit || synonymHit)
            {
                found.Add(ToDraft(item));
            }
        }

        return
        [
            .. found
                .OrderBy(static item => item.Name.Length)
                .ThenBy(static item => item.Name, StringComparer.Ordinal)
                .Take(bounded)
        ];
    }

    /// <inheritdoc/>
    public DraftMetadataObject? GetMetadataObject(string id)
    {
        var item = string.IsNullOrWhiteSpace(id) ? null : _result.Metadata.Find(id.Trim());
        return item is null ? null : ToDraft(item);
    }

    /// <inheritdoc/>
    public bool HasMetadataKind(string kind) =>
        !string.IsNullOrWhiteSpace(kind) &&
        _result.Metadata.Objects.Any(item => string.Equals(item.Kind.Name, kind, StringComparison.OrdinalIgnoreCase));

    private static DraftSymbol ToDraft(BslModuleInfo module, BslRoutine routine) => new(
        routine.Name,
        routine.Kind.ToString(),
        routine.IsExport,
        module.Path,
        module.OwnerId,
        routine.Parameters,
        routine.StartLine,
        routine.EndLine);

    private static DraftMetadataObject ToDraft(MdObject item) =>
        new(item.Id, item.Kind.Name, item.Name, item.Synonym);

    /// <summary>Ступени совпадения: точное имя, префикс, подстрока. −1 — не подходит.</summary>
    private static int Rank(string name, string query, bool exact)
    {
        if (string.Equals(name, query, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (exact)
        {
            return -1;
        }

        if (name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        return name.Contains(query, StringComparison.OrdinalIgnoreCase) ? 2 : -1;
    }
}
