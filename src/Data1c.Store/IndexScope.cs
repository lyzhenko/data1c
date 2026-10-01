namespace Data1c.Store;

/// <summary>
/// Что перезаписать при частичной переиндексации: файлы модулей, содержимое которых изменилось,
/// и файлы, которых в выгрузке больше нет.
/// </summary>
/// <param name="ModuleFiles">Пути модулей BSL, которые нужно разобрать и записать заново.</param>
/// <param name="RemovedFiles">Пути исчезнувших файлов: их строки удаляются из индекса.</param>
public sealed record IndexScope(
    IReadOnlyList<string> ModuleFiles,
    IReadOnlyList<string>? RemovedFiles = null)
{
    /// <summary>Работать не над чем.</summary>
    public bool IsEmpty => ModuleFiles.Count == 0 && (RemovedFiles is null || RemovedFiles.Count == 0);
}
