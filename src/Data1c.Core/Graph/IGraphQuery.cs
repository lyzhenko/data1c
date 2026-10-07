using Data1c.Core.Metadata;

namespace Data1c.Core.Graph;

/// <summary>Найденный вложенный объект: реквизит, табличная часть, форма, команда, макет.</summary>
/// <param name="Id">Полный идентификатор вида <c>Catalog.Товары/Attribute.Артикул</c>.</param>
/// <param name="Kind">Вид вложенного объекта (Attribute, TabularSection, Form, …).</param>
/// <param name="Name">Имя.</param>
/// <param name="Synonym">Синоним, если есть.</param>
/// <param name="ObjectId">Идентификатор объекта-владельца верхнего уровня.</param>
/// <param name="ParentId">Идентификатор родителя, если вложенность глубже одного уровня.</param>
/// <param name="Types">Типы значения (для реквизитов) в виде идентификаторов объектов метаданных.</param>
public sealed record GraphNestedHit(
    string Id,
    string Kind,
    string Name,
    string? Synonym,
    string ObjectId,
    string? ParentId,
    IReadOnlyList<string> Types);

/// <summary>Ссылка объекта метаданных: тип реквизита, содержимое подсистемы, право роли, форма.</summary>
/// <param name="Kind">Вид связи (type, query, rights, form, …).</param>
/// <param name="Target">Идентификатор цели.</param>
/// <param name="Detail">Уточнение: имя реквизита, имя права, роль.</param>
public sealed record MetadataReferenceInfo(string Kind, string Target, string? Detail);

/// <summary>
/// Карточка объекта метаданных деревом: сам объект, его свойства, состав, типы, ссылки и модули.
/// Нужна, чтобы писать код по реальной структуре объекта, не поднимая весь разбор выгрузки.
/// </summary>
/// <param name="Children">Дети в пределах запрошенной глубины.</param>
/// <param name="ChildrenNotShown">Сколько детей не попало в ответ: глубина или предел.</param>
/// <param name="Form">Описание формы для объектов-форм: реквизиты, элементы, команды и обработчики.</param>
public sealed record MetadataCard(
    string Id,
    string Kind,
    string Name,
    string? Synonym,
    string? Comment,
    string? Uuid,
    bool IsTopLevel,
    string? ParentId,
    string? SourcePath,
    IReadOnlyList<string> Types,
    IReadOnlyDictionary<string, string> Properties,
    IReadOnlyList<MetadataReferenceInfo> References,
    IReadOnlyList<string> ModulePaths,
    IReadOnlyList<MetadataCard> Children,
    int ChildrenNotShown,
    FormModel? Form = null);

/// <summary>
/// Запросы к графу зависимостей независимо от того, где он лежит: собран в памяти
/// (<see cref="GraphQueryService"/>) или прочитан из SQLite-индекса.
/// </summary>
/// <remarks>
/// Потребители — просмотрщик и MCP-сервер — работают через этот интерфейс, поэтому индекс
/// и разбор в память взаимозаменяемы: сервер отвечает из базы, когда она есть, и падает
/// обратно на разбор, когда её нет.
/// </remarks>
public interface IGraphQuery
{
    /// <summary>Сводная статистика графа.</summary>
    GraphStatistics Statistics { get; }

    /// <summary>Поиск узлов по идентификатору, имени, синониму или пути файла; <paramref name="offset"/> пропускает первые результаты (добор страницы).</summary>
    IReadOnlyList<GraphSearchHit> Search(string? query, int limit = 30, int offset = 0);

    /// <summary>Разрешение ссылки на узел: идентификатор, часть имени или путь.</summary>
    IReadOnlyList<GraphNode> Resolve(string? reference, int limit = 10);

    /// <summary>Окружение узла: узлы и связи в заданном радиусе.</summary>
    GraphNeighborhood GetNeighborhood(GraphNeighborhoodRequest request);

    /// <summary>Карточка узла: сам узел и связи в обе стороны.</summary>
    GraphNodeDetails? GetNode(string? id);

    /// <summary>Узел по идентификатору без загрузки связей.</summary>
    GraphNode? FindNode(string? id);

    /// <summary>
    /// Поиск по вложенным объектам: реквизиты, табличные части, формы, команды. В графе их нет —
    /// они живут в модели метаданных (или в таблицах состава индекса), но находятся по имени,
    /// поэтому поиск вынесен в общий контракт.
    /// </summary>
    IReadOnlyList<GraphNestedHit> SearchNested(string? query, int limit = 20, IReadOnlyCollection<string>? metadataKinds = null);

    /// <summary>Карточка объекта метаданных деревом: свойства, состав, типы, ссылки, модули.</summary>
    /// <param name="id">Идентификатор объекта метаданных.</param>
    /// <param name="depth">Глубина дерева состава.</param>
    /// <param name="maxChildren">Сколько детей показывать у одного узла дерева.</param>
    /// <param name="sections">
    /// Виды разделов состава, которые остаются в дереве (имена видов из разбора выгрузки:
    /// <c>Attribute</c>, <c>TabularSection</c>, <c>Form</c> и так далее). <see langword="null"/> — все виды.
    /// Фильтр действует на каждом уровне: у табличной части остаются только запрошенные виды её реквизитов,
    /// а <see cref="MetadataCard.ChildrenNotShown"/> считает не показанные дети того же вида.
    /// </param>
    /// <param name="offset">
    /// Сколько первых детей пропустить в каждом узле дерева: вместе с <paramref name="maxChildren"/>
    /// даёт добор хвоста раздела. Считается после фильтра <paramref name="sections"/>.
    /// </param>
    MetadataCard? GetMetadata(
        string? id,
        int depth = 3,
        int maxChildren = 200,
        IReadOnlyCollection<string>? sections = null,
        int offset = 0);

    /// <summary>
    /// Кто и в каком контексте обращается к объекту метаданных: счётчики по контекстам и видам связи,
    /// топ модулей-читателей и примеры обращений. Нужна карточкам <c>metadata</c> и <c>node</c>,
    /// чтобы агент видел, где искать примеры работы с объектом.
    /// </summary>
    /// <param name="id">Идентификатор объекта метаданных.</param>
    /// <param name="limit">Предел числа примеров обращений; счётчики остаются полными.</param>
    /// <param name="context">
    /// Контекст из <see cref="MetadataRefContexts.All"/>: тогда в сводку попадают только такие
    /// обращения, а счётчики, читатели и примеры пересчитываются по ним. <see langword="null"/> —
    /// все контексты.
    /// </param>
    MetadataUsageSummary GetMetadataUsages(string? id, int limit = 20, string? context = null);
}
