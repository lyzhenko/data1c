using Data1c.Core.Analysis;

namespace Data1c.Store;

/// <summary>
/// Факты о конфигурации для проверки черновика (Э2-2) поверх готового SQLite-индекса:
/// процедуры и функции, их параметры и объекты метаданных.
/// </summary>
/// <remarks>
/// Индекс — основной источник для <see cref="DraftCheck"/>: он отвечает без разбора всей выгрузки.
/// Каждый запрос идёт по индексу, поэтому проверка черновика стоит доли секунды.
/// </remarks>
public sealed class IndexDraftContext : IDraftContext
{
    private readonly IndexReader _reader;

    /// <summary>Создаёт контекст над читателем индекса.</summary>
    /// <param name="reader">Читатель готового индекса.</param>
    public IndexDraftContext(IndexReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        _reader = reader;
    }

    /// <inheritdoc/>
    public IReadOnlyList<DraftSymbol> FindSymbols(string name, int limit = 20, bool exact = false) =>
        [.. _reader.FindSymbols(name, limit, exact).Select(ToDraft)];

    /// <inheritdoc/>
    public IReadOnlyList<DraftSymbol> FindSymbolsByOwner(string ownerId, int limit = 50) =>
        [.. _reader.FindSymbolsByOwner(ownerId, limit).Select(ToDraft)];

    /// <inheritdoc/>
    public IReadOnlyList<DraftMetadataObject> FindMetadataObjects(string query, int limit = 20) =>
        [.. _reader.FindMetadataObjects(query, limit)
            .Select(static row => new DraftMetadataObject(row.Id, row.Kind, row.Name, row.Synonym))];

    /// <inheritdoc/>
    public DraftMetadataObject? GetMetadataObject(string id)
    {
        var row = _reader.GetMetadataObject(id);
        return row is null ? null : new DraftMetadataObject(row.Id, row.Kind, row.Name, row.Synonym);
    }

    /// <inheritdoc/>
    public bool HasMetadataKind(string kind) => _reader.HasMetadataKind(kind);

    private static DraftSymbol ToDraft(SymbolRow row) => new(
        row.Name,
        row.Kind,
        row.IsExport,
        row.ModulePath,
        row.OwnerId,
        ParseParameters(row.Parameters),
        row.StartLine,
        row.EndLine);

    /// <summary>
    /// Разбирает строку параметров из индекса: писатель складывает имена через запятую.
    /// </summary>
    private static IReadOnlyList<string> ParseParameters(string? parameters)
    {
        if (string.IsNullOrWhiteSpace(parameters))
        {
            return [];
        }

        return
        [
            .. parameters
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(static name => name.Length > 0)
        ];
    }
}
