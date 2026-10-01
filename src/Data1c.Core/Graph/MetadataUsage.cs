namespace Data1c.Core.Graph;

/// <summary>Одно обращение к объекту метаданных: кто обращается, в каком контексте и где именно.</summary>
/// <param name="SourceId">Идентификатор узла-читателя: модуль, процедура или объект метаданных.</param>
/// <param name="Name">Имя читателя, если он есть в графе или модели метаданных.</param>
/// <param name="File">Файл выгрузки читателя: по нему агент читает примеры работы с объектом.</param>
/// <param name="Context">Контекст обращения: <c>code</c>, <c>query</c>, <c>type</c>, <c>content</c> и другие.</param>
/// <param name="Line">Строка модуля, где встретилось обращение. У ссылок метаданных её нет.</param>
/// <param name="Detail">Фрагмент кода или текста запроса, имя реквизита, текст ссылки.</param>
public sealed record MetadataUsage(
    string SourceId,
    string? Name,
    string? File,
    string Context,
    int? Line = null,
    string? Detail = null);

/// <summary>Модуль-читатель: сколько раз он обращается к объекту и где это видно впервые.</summary>
/// <param name="SourceId">Идентификатор узла-читателя.</param>
/// <param name="Name">Имя читателя.</param>
/// <param name="File">Файл выгрузки читателя.</param>
/// <param name="Count">Сколько обращений у этого читателя. Счётчик полный, лимит его не режет.</param>
/// <param name="Context">Контекст первого обращения.</param>
/// <param name="Line">Строка первого обращения, если она известна.</param>
/// <param name="Detail">Фрагмент первого обращения.</param>
public sealed record MetadataUsageReader(
    string SourceId,
    string? Name,
    string? File,
    int Count,
    string Context,
    int? Line = null,
    string? Detail = null);

/// <summary>Сколько обращений у одного контекста или у одного вида связи.</summary>
/// <param name="Name">Значение: контекст (<c>code</c>) либо вид связи графа (<c>UsesMetadata</c>).</param>
/// <param name="Count">Число обращений; считается по всем строкам, даже когда списки обрезаны лимитом.</param>
public sealed record MetadataUsageCount(string Name, int Count);

/// <summary>
/// Обращения к объекту метаданных: полные счётчики по контекстам и видам связи, топ модулей-читателей
/// и примеры обращений. Нужна карточкам <c>metadata</c> и <c>node</c>, чтобы агент видел, где искать
/// примеры работы с объектом, а не только связи графа.
/// </summary>
/// <param name="Total">Всего обращений к объекту.</param>
/// <param name="ByContext">Разбивка по контекстам, по убыванию числа обращений.</param>
/// <param name="ByKind">
/// Разбивка по видам связи индекса: <c>UsesMetadata</c> — обращения из кода и текстов запросов,
/// <c>References</c> — перекрёстные ссылки метаданных (типы, состав, формы, права).
/// </param>
/// <param name="Readers">Топ модулей-читателей с числом обращений и первым местом.</param>
/// <param name="Items">Конкретные обращения: читатель, строка, контекст, фрагмент.</param>
/// <param name="Shown">Сколько обращений попало в <paramref name="Items"/>.</param>
public sealed record MetadataUsageSummary(
    int Total,
    IReadOnlyList<MetadataUsageCount> ByContext,
    IReadOnlyList<MetadataUsageCount> ByKind,
    IReadOnlyList<MetadataUsageReader> Readers,
    IReadOnlyList<MetadataUsage> Items,
    int Shown)
{
    /// <summary>Сколько модулей-читателей показывает карточка объекта.</summary>
    public const int ReaderLimit = 10;

    /// <summary>Объект никто не читает: пустые счётчики и списки.</summary>
    public static MetadataUsageSummary Empty { get; } = new(0, [], [], [], [], 0);

    /// <summary>
    /// Собирает сводку из полного списка обращений: счётчики считаются по всему списку,
    /// а читатели и примеры обрезаются лимитом.
    /// </summary>
    /// <param name="usages">Все обращения к объекту.</param>
    /// <param name="limit">Предел числа примеров обращений в <see cref="Items"/>.</param>
    public static MetadataUsageSummary From(IReadOnlyList<MetadataUsage> usages, int limit)
    {
        if (usages.Count == 0)
        {
            return Empty;
        }

        var byContext = usages
            .GroupBy(static usage => usage.Context, StringComparer.Ordinal)
            .Select(static group => new MetadataUsageCount(group.Key, group.Count()))
            .OrderByDescending(static group => group.Count)
            .ThenBy(static group => group.Name, StringComparer.Ordinal)
            .ToList();

        // Вид связи выводится из контекста: код и тексты запросов — это UsesMetadata,
        // всё остальное — перекрёстные ссылки метаданных (References).
        var byKind = byContext
            .GroupBy(static group => MetadataRefContexts.KindOf(group.Name), StringComparer.Ordinal)
            .Select(static group => new MetadataUsageCount(group.Key, group.Sum(static item => item.Count)))
            .OrderByDescending(static group => group.Count)
            .ThenBy(static group => group.Name, StringComparer.Ordinal)
            .ToList();

        var readers = usages
            .GroupBy(static usage => usage.SourceId, StringComparer.Ordinal)
            .Select(static group => (Id: group.Key, Count: group.Count(), First: group.OrderBy(static usage => usage.Line ?? int.MaxValue).First()))
            .OrderByDescending(static reader => reader.Count)
            .ThenBy(static reader => reader.Id, StringComparer.Ordinal)
            .Take(ReaderLimit)
            .Select(static reader => new MetadataUsageReader(
                reader.Id,
                reader.First.Name,
                reader.First.File,
                reader.Count,
                reader.First.Context,
                reader.First.Line,
                reader.First.Detail))
            .ToList();

        var items = usages
            .OrderBy(static usage => usage.Context, StringComparer.Ordinal)
            .ThenBy(static usage => usage.SourceId, StringComparer.Ordinal)
            .ThenBy(static usage => usage.Line ?? 0)
            .Take(Math.Clamp(limit, 1, 500))
            .ToList();

        return new MetadataUsageSummary(usages.Count, byContext, byKind, readers, items, items.Count);
    }
}
