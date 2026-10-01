namespace Data1c.Core.Graph;

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

    /// <summary>Поиск узлов по идентификатору, имени, синониму или пути файла.</summary>
    IReadOnlyList<GraphSearchHit> Search(string? query, int limit = 30);

    /// <summary>Разрешение ссылки на узел: идентификатор, часть имени или путь.</summary>
    IReadOnlyList<GraphNode> Resolve(string? reference, int limit = 10);

    /// <summary>Окружение узла: узлы и связи в заданном радиусе.</summary>
    GraphNeighborhood GetNeighborhood(GraphNeighborhoodRequest request);

    /// <summary>Карточка узла: сам узел и связи в обе стороны.</summary>
    GraphNodeDetails? GetNode(string? id);
}
