namespace Data1c.Core.Graph;

/// <summary>
/// Разделы состава объекта метаданных для карточки <c>metadata</c>. Список собран по видам, которые
/// реально встречаются в выгрузках ERP: реквизиты, табличные части, формы, команды, макеты, измерения,
/// ресурсы, значения перечисления и параметры. Вида <c>Module</c> здесь нет намеренно: модули лежат
/// не в составе объекта, а в его каталоге <c>Ext</c> и отдаются отдельным полем карточки.
/// </summary>
public static class MetadataSections
{
    /// <summary>Имена видов разделов состава: попадают в схему аргумента <c>sections</c> и в текст ошибки.</summary>
    public static readonly IReadOnlyList<string> All =
    [
        "Attribute",
        "TabularSection",
        "Form",
        "Command",
        "Template",
        "Dimension",
        "Resource",
        "EnumValue",
        "Parameter",
    ];

    /// <summary>
    /// Остаётся ли раздел при фильтре: <see langword="null"/> или пустой набор означают «все виды».
    /// Сравнение без учёта регистра, чтобы прямой вызов контракта не зависел от записи имени вида.
    /// </summary>
    public static bool Matches(string kind, IReadOnlyCollection<string>? sections) =>
        sections is null
        || sections.Count == 0
        || sections.Any(section => string.Equals(section, kind, StringComparison.OrdinalIgnoreCase));

    /// <summary>Известное имя вида раздела по значению аргумента или <see langword="null"/>, если такого вида нет.</summary>
    public static string? Known(string? value) =>
        value is null
            ? null
            : All.FirstOrDefault(section => string.Equals(section, value, StringComparison.OrdinalIgnoreCase));
}
