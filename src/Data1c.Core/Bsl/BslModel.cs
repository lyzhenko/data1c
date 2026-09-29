using Data1c.Core.Dump;
using Data1c.Core.Metadata;

namespace Data1c.Core.Bsl;

/// <summary>Вид модуля 1С, определяемый по имени файла и пути в выгрузке.</summary>
public enum BslModuleKind
{
    Unknown,
    CommonModule,
    ObjectModule,
    ManagerModule,
    RecordSetModule,
    ValueManagerModule,
    CommandModule,
    FormModule,
    ManagedApplicationModule,
    SessionModule,
    ExternalConnectionModule,
    OrdinaryApplicationModule,
    HTTPServiceModule,
    WebServiceModule,
    IntegrationServiceModule,
}

public static class BslModuleKinds
{
    /// <summary>Определяет вид модуля по пути файла внутри выгрузки.</summary>
    public static BslModuleKind FromPath(string relativePath)
    {
        var fileName = DumpPath.GetFileName(relativePath);
        var directory = DumpPath.GetDirectory(relativePath);

        switch (fileName)
        {
            case "ObjectModule.bsl":
                return BslModuleKind.ObjectModule;
            case "ManagerModule.bsl":
                return BslModuleKind.ManagerModule;
            case "RecordSetModule.bsl":
                return BslModuleKind.RecordSetModule;
            case "ValueManagerModule.bsl":
                return BslModuleKind.ValueManagerModule;
            case "CommandModule.bsl":
                return BslModuleKind.CommandModule;
            case "FormModule.bsl":
                return BslModuleKind.FormModule;
            case "ManagedApplicationModule.bsl":
                return BslModuleKind.ManagedApplicationModule;
            case "SessionModule.bsl":
                return BslModuleKind.SessionModule;
            case "ExternalConnectionModule.bsl":
                return BslModuleKind.ExternalConnectionModule;
            case "OrdinaryApplicationModule.bsl":
                return BslModuleKind.OrdinaryApplicationModule;
            case "Module.bsl":
                if (directory.EndsWith("/Ext/Form", StringComparison.OrdinalIgnoreCase))
                {
                    return BslModuleKind.FormModule;
                }

                var section = DumpPath.Segments(relativePath) is [var first, ..] ? first : string.Empty;
                return section switch
                {
                    "CommonModules" => BslModuleKind.CommonModule,
                    "HTTPServices" => BslModuleKind.HTTPServiceModule,
                    "WebServices" => BslModuleKind.WebServiceModule,
                    "IntegrationServices" => BslModuleKind.IntegrationServiceModule,
                    _ => BslModuleKind.Unknown,
                };
            default:
                return BslModuleKind.Unknown;
        }
    }
}

public enum BslRoutineKind
{
    Procedure,
    Function,
}

/// <summary>Область препроцессора: <c>#Область ... #КонецОбласти</c>.</summary>
public sealed record BslRegion(string Name, int StartLine, int EndLine, int Depth);

/// <summary>Вызов метода в коде BSL.</summary>
/// <param name="Callee">Полный текст вызова: «ОбщегоНазначения.ЗначениеРеквизитаОбъекта».</param>
/// <param name="Qualifier">Часть до последней точки: «ОбщегоНазначения». Для локального вызова — null.</param>
/// <param name="Method">Последняя часть: «ЗначениеРеквизитаОбъекта».</param>
/// <param name="Line">Номер строки (1-based).</param>
public sealed record BslCall(string Callee, string? Qualifier, string Method, int Line)
{
    /// <summary>Вызов процедуры/функции текущего модуля без квалификатора.</summary>
    public bool IsLocal => Qualifier is null;
}

/// <summary>Обращение к объекту метаданных из кода: «Справочники.Товары», «Метаданные.Документы.Заказ».</summary>
/// <param name="Kind">Вид объекта метаданных.</param>
/// <param name="ObjectName">Имя объекта.</param>
/// <param name="Collection">Имя коллекции, как записано в коде: «Справочники».</param>
/// <param name="Line">Номер строки (1-based).</param>
/// <param name="Text">Полный текст обращения.</param>
public sealed record BslMetadataAccess(MdKind Kind, string ObjectName, string Collection, int Line, string Text);

public enum BslDiagnosticKind
{
    UnclosedRoutine,
    UnclosedRegion,
    UnexpectedRoutineEnd,
    UnexpectedRegionEnd,
    UnexpectedToken,
    DuplicateRoutine,
    Other,
}

/// <summary>Замечание структурного разбора модуля (не ошибка компиляции, а признак неожиданной разметки).</summary>
public sealed record BslDiagnostic(BslDiagnosticKind Kind, string Message, int Line);

/// <summary>Процедура или функция модуля.</summary>
public sealed record BslRoutine(
    string Name,
    BslRoutineKind Kind,
    bool IsExport,
    IReadOnlyList<string> Parameters,
    int StartLine,
    int EndLine,
    int Depth,
    string? Region,
    IReadOnlyList<string> Directives,
    IReadOnlyList<BslCall> Calls,
    IReadOnlyList<BslMetadataAccess> MetadataAccesses)
{
    /// <summary>Число строк тела процедуры.</summary>
    public int LineCount => EndLine - StartLine + 1;

    public bool HasDirective(string name) =>
        Directives.Any(d => string.Equals(d, name, StringComparison.OrdinalIgnoreCase));

    public override string ToString() => $"{Kind} {Name} (строки {StartLine}-{EndLine})";
}

/// <summary>Результат разбора одного модуля BSL.</summary>
public sealed class BslModuleInfo
{
    public required string Path { get; init; }

    /// <summary>Идентификатор объекта метаданных-владельца, если модуль удалось привязать.</summary>
    public string? OwnerId { get; init; }

    public BslModuleKind Kind { get; init; } = BslModuleKind.Unknown;

    public IReadOnlyList<BslRoutine> Routines { get; init; } = [];

    /// <summary>Области модуля, включая вложенные.</summary>
    public IReadOnlyList<BslRegion> Regions { get; init; } = [];

    /// <summary>Вызовы вне процедур и функций (код модуля).</summary>
    public IReadOnlyList<BslCall> Calls { get; init; } = [];

    /// <summary>Обращения к метаданным вне процедур и функций.</summary>
    public IReadOnlyList<BslMetadataAccess> MetadataAccesses { get; init; } = [];

    public IReadOnlyList<BslDiagnostic> Diagnostics { get; init; } = [];

    public int LineCount { get; init; }

    /// <summary>Устойчивый идентификатор узла графа для модуля.</summary>
    public string Id => "module:" + Path;

    public bool IsEmpty =>
        Routines.Count == 0 && Calls.Count == 0 && MetadataAccesses.Count == 0;
}

/// <summary>Входные данные для разбора модуля.</summary>
/// <param name="Path">Путь модуля внутри выгрузки.</param>
/// <param name="Text">Текст модуля.</param>
/// <param name="OwnerId">Идентификатор объекта метаданных-владельца.</param>
/// <param name="Kind">Вид модуля.</param>
public sealed record BslModuleSource(string Path, string Text, string? OwnerId = null, BslModuleKind Kind = BslModuleKind.Unknown);

/// <summary>Разборщик модулей BSL.</summary>
public interface IBslModuleParser
{
    BslModuleInfo Parse(BslModuleSource source);
}
