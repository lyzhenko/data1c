using System.Collections.Frozen;

namespace Data1c.Core.Metadata;

/// <summary>
/// Вид объекта метаданных 1С. Открытый набор: неизвестные виды не ломают разбор выгрузки,
/// а сохраняются под своим именем из XML или из имени каталога выгрузки.
/// </summary>
public readonly record struct MdKind(string Name)
{
    public static readonly MdKind Unknown = new("Unknown");
    public static readonly MdKind Configuration = new("Configuration");
    public static readonly MdKind Catalog = new("Catalog");
    public static readonly MdKind Document = new("Document");
    public static readonly MdKind DocumentJournal = new("DocumentJournal");
    public static readonly MdKind Enum = new("Enum");
    public static readonly MdKind Constant = new("Constant");
    public static readonly MdKind InformationRegister = new("InformationRegister");
    public static readonly MdKind AccumulationRegister = new("AccumulationRegister");
    public static readonly MdKind AccountingRegister = new("AccountingRegister");
    public static readonly MdKind CalculationRegister = new("CalculationRegister");
    public static readonly MdKind BusinessProcess = new("BusinessProcess");
    public static readonly MdKind Task = new("Task");
    public static readonly MdKind ChartOfAccounts = new("ChartOfAccounts");
    public static readonly MdKind ChartOfCharacteristicTypes = new("ChartOfCharacteristicTypes");
    public static readonly MdKind ChartOfCalculationTypes = new("ChartOfCalculationTypes");
    public static readonly MdKind ExchangePlan = new("ExchangePlan");
    public static readonly MdKind Sequence = new("Sequence");
    public static readonly MdKind DataProcessor = new("DataProcessor");
    public static readonly MdKind Report = new("Report");
    public static readonly MdKind CommonModule = new("CommonModule");
    public static readonly MdKind CommonForm = new("CommonForm");
    public static readonly MdKind CommonCommand = new("CommonCommand");
    public static readonly MdKind CommonTemplate = new("CommonTemplate");
    public static readonly MdKind CommonPicture = new("CommonPicture");
    public static readonly MdKind CommonAttribute = new("CommonAttribute");
    public static readonly MdKind CommandGroup = new("CommandGroup");
    public static readonly MdKind Subsystem = new("Subsystem");
    public static readonly MdKind Role = new("Role");
    public static readonly MdKind Language = new("Language");
    public static readonly MdKind Style = new("Style");
    public static readonly MdKind StyleItem = new("StyleItem");
    public static readonly MdKind FilterCriterion = new("FilterCriterion");
    public static readonly MdKind DefinedType = new("DefinedType");
    public static readonly MdKind FunctionalOption = new("FunctionalOption");
    public static readonly MdKind FunctionalOptionsParameter = new("FunctionalOptionsParameter");
    public static readonly MdKind EventSubscription = new("EventSubscription");
    public static readonly MdKind ScheduledJob = new("ScheduledJob");
    public static readonly MdKind SessionParameter = new("SessionParameter");
    public static readonly MdKind SettingsStorage = new("SettingsStorage");
    public static readonly MdKind HTTPService = new("HTTPService");
    public static readonly MdKind WebService = new("WebService");
    public static readonly MdKind WSReference = new("WSReference");
    public static readonly MdKind XDTOPackage = new("XDTOPackage");
    public static readonly MdKind ExternalDataSource = new("ExternalDataSource");
    public static readonly MdKind IntegrationService = new("IntegrationService");

    /// <summary>Вложенный объект: реквизит, табличная часть, форма, макет, команда.</summary>
    public static readonly MdKind Attribute = new("Attribute");
    public static readonly MdKind TabularSection = new("TabularSection");
    public static readonly MdKind Form = new("Form");
    public static readonly MdKind Template = new("Template");
    public static readonly MdKind Command = new("Command");

    /// <summary>Служебный вид для корня «Метаданные.&lt;Коллекция&gt;» в коде BSL.</summary>
    public static readonly MdKind MetadataRoot = new("Metadata");

    public bool IsUnknown => Name.Length == 0 || Name == Unknown.Name;

    public override string ToString() => Name;
}

/// <summary>
/// Соответствия между именами 1С: каталогами выгрузки, префиксами типов (<c>cfg:CatalogRef</c>)
/// и русскими именами коллекций в коде BSL («Справочники»).
/// </summary>
public static class MdNaming
{
    private static readonly FrozenDictionary<string, MdKind> Sections = new Dictionary<string, MdKind>(StringComparer.OrdinalIgnoreCase)
    {
        ["Catalogs"] = MdKind.Catalog,
        ["Documents"] = MdKind.Document,
        ["DocumentJournals"] = MdKind.DocumentJournal,
        ["Enums"] = MdKind.Enum,
        ["Constants"] = MdKind.Constant,
        ["InformationRegisters"] = MdKind.InformationRegister,
        ["AccumulationRegisters"] = MdKind.AccumulationRegister,
        ["AccountingRegisters"] = MdKind.AccountingRegister,
        ["CalculationRegisters"] = MdKind.CalculationRegister,
        ["BusinessProcesses"] = MdKind.BusinessProcess,
        ["Tasks"] = MdKind.Task,
        ["ChartsOfAccounts"] = MdKind.ChartOfAccounts,
        ["ChartsOfCharacteristicTypes"] = MdKind.ChartOfCharacteristicTypes,
        ["ChartsOfCalculationTypes"] = MdKind.ChartOfCalculationTypes,
        ["ExchangePlans"] = MdKind.ExchangePlan,
        ["Sequences"] = MdKind.Sequence,
        ["DataProcessors"] = MdKind.DataProcessor,
        ["Reports"] = MdKind.Report,
        ["CommonModules"] = MdKind.CommonModule,
        ["CommonForms"] = MdKind.CommonForm,
        ["CommonCommands"] = MdKind.CommonCommand,
        ["CommonTemplates"] = MdKind.CommonTemplate,
        ["CommonPictures"] = MdKind.CommonPicture,
        ["CommonAttributes"] = MdKind.CommonAttribute,
        ["CommandGroups"] = MdKind.CommandGroup,
        ["Subsystems"] = MdKind.Subsystem,
        ["Roles"] = MdKind.Role,
        ["Languages"] = MdKind.Language,
        ["Styles"] = MdKind.Style,
        ["StyleItems"] = MdKind.StyleItem,
        ["FilterCriteria"] = MdKind.FilterCriterion,
        ["DefinedTypes"] = MdKind.DefinedType,
        ["FunctionalOptions"] = MdKind.FunctionalOption,
        ["FunctionalOptionsParameters"] = MdKind.FunctionalOptionsParameter,
        ["EventSubscriptions"] = MdKind.EventSubscription,
        ["ScheduledJobs"] = MdKind.ScheduledJob,
        ["SessionParameters"] = MdKind.SessionParameter,
        ["SettingsStorages"] = MdKind.SettingsStorage,
        ["HTTPServices"] = MdKind.HTTPService,
        ["WebServices"] = MdKind.WebService,
        ["WSReferences"] = MdKind.WSReference,
        ["XDTOPackages"] = MdKind.XDTOPackage,
        ["ExternalDataSources"] = MdKind.ExternalDataSource,
        ["IntegrationServices"] = MdKind.IntegrationService,
        ["Forms"] = MdKind.Form,
        ["Templates"] = MdKind.Template,
        ["Commands"] = MdKind.Command,
        ["Attributes"] = MdKind.Attribute,
        ["TabularSections"] = MdKind.TabularSection,
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenDictionary<string, MdKind> TypePrefixes = new Dictionary<string, MdKind>(StringComparer.OrdinalIgnoreCase)
    {
        ["Catalog"] = MdKind.Catalog,
        ["CatalogRef"] = MdKind.Catalog,
        ["CatalogObject"] = MdKind.Catalog,
        ["CatalogSelection"] = MdKind.Catalog,
        ["CatalogList"] = MdKind.Catalog,
        ["CatalogManager"] = MdKind.Catalog,
        ["Document"] = MdKind.Document,
        ["DocumentRef"] = MdKind.Document,
        ["DocumentObject"] = MdKind.Document,
        ["DocumentSelection"] = MdKind.Document,
        ["DocumentList"] = MdKind.Document,
        ["DocumentManager"] = MdKind.Document,
        ["DocumentJournal"] = MdKind.DocumentJournal,
        ["DocumentJournalRef"] = MdKind.DocumentJournal,
        ["DocumentJournalSelection"] = MdKind.DocumentJournal,
        ["Enum"] = MdKind.Enum,
        ["EnumRef"] = MdKind.Enum,
        ["EnumManager"] = MdKind.Enum,
        ["Constant"] = MdKind.Constant,
        ["ConstantRef"] = MdKind.Constant,
        ["ConstantValueManager"] = MdKind.Constant,
        ["ConstantValueKey"] = MdKind.Constant,
        ["InformationRegister"] = MdKind.InformationRegister,
        ["InformationRegisterRef"] = MdKind.InformationRegister,
        ["InformationRegisterRecordSet"] = MdKind.InformationRegister,
        ["InformationRegisterRecordKey"] = MdKind.InformationRegister,
        ["InformationRegisterRecordManager"] = MdKind.InformationRegister,
        ["InformationRegisterSelection"] = MdKind.InformationRegister,
        ["InformationRegisterList"] = MdKind.InformationRegister,
        ["InformationRegisterManager"] = MdKind.InformationRegister,
        ["AccumulationRegister"] = MdKind.AccumulationRegister,
        ["AccumulationRegisterRef"] = MdKind.AccumulationRegister,
        ["AccumulationRegisterRecordSet"] = MdKind.AccumulationRegister,
        ["AccumulationRegisterRecordKey"] = MdKind.AccumulationRegister,
        ["AccumulationRegisterSelection"] = MdKind.AccumulationRegister,
        ["AccumulationRegisterList"] = MdKind.AccumulationRegister,
        ["AccumulationRegisterManager"] = MdKind.AccumulationRegister,
        ["AccountingRegister"] = MdKind.AccountingRegister,
        ["AccountingRegisterRef"] = MdKind.AccountingRegister,
        ["AccountingRegisterRecordSet"] = MdKind.AccountingRegister,
        ["AccountingRegisterRecordKey"] = MdKind.AccountingRegister,
        ["AccountingRegisterSelection"] = MdKind.AccountingRegister,
        ["AccountingRegisterList"] = MdKind.AccountingRegister,
        ["AccountingRegisterManager"] = MdKind.AccountingRegister,
        ["CalculationRegister"] = MdKind.CalculationRegister,
        ["CalculationRegisterRef"] = MdKind.CalculationRegister,
        ["CalculationRegisterRecordSet"] = MdKind.CalculationRegister,
        ["CalculationRegisterRecordKey"] = MdKind.CalculationRegister,
        ["CalculationRegisterSelection"] = MdKind.CalculationRegister,
        ["CalculationRegisterList"] = MdKind.CalculationRegister,
        ["CalculationRegisterManager"] = MdKind.CalculationRegister,
        ["BusinessProcess"] = MdKind.BusinessProcess,
        ["BusinessProcessRef"] = MdKind.BusinessProcess,
        ["BusinessProcessObject"] = MdKind.BusinessProcess,
        ["Task"] = MdKind.Task,
        ["TaskRef"] = MdKind.Task,
        ["TaskObject"] = MdKind.Task,
        ["ChartOfAccounts"] = MdKind.ChartOfAccounts,
        ["ChartOfAccountsRef"] = MdKind.ChartOfAccounts,
        ["ChartOfAccountsObject"] = MdKind.ChartOfAccounts,
        ["ChartOfCharacteristicTypes"] = MdKind.ChartOfCharacteristicTypes,
        ["ChartOfCharacteristicTypesRef"] = MdKind.ChartOfCharacteristicTypes,
        ["ChartOfCharacteristicTypesObject"] = MdKind.ChartOfCharacteristicTypes,
        ["ChartOfCalculationTypes"] = MdKind.ChartOfCalculationTypes,
        ["ChartOfCalculationTypesRef"] = MdKind.ChartOfCalculationTypes,
        ["ChartOfCalculationTypesObject"] = MdKind.ChartOfCalculationTypes,
        ["ExchangePlan"] = MdKind.ExchangePlan,
        ["ExchangePlanRef"] = MdKind.ExchangePlan,
        ["ExchangePlanObject"] = MdKind.ExchangePlan,
        ["Sequence"] = MdKind.Sequence,
        ["SequenceRef"] = MdKind.Sequence,
        ["DataProcessor"] = MdKind.DataProcessor,
        ["DataProcessorRef"] = MdKind.DataProcessor,
        ["DataProcessorObject"] = MdKind.DataProcessor,
        ["Report"] = MdKind.Report,
        ["ReportRef"] = MdKind.Report,
        ["ReportObject"] = MdKind.Report,
        ["CommonModule"] = MdKind.CommonModule,
        ["CommonModuleRef"] = MdKind.CommonModule,
        ["CommonForm"] = MdKind.CommonForm,
        ["CommonCommand"] = MdKind.CommonCommand,
        ["CommonTemplate"] = MdKind.CommonTemplate,
        ["CommonPicture"] = MdKind.CommonPicture,
        ["CommonAttribute"] = MdKind.CommonAttribute,
        ["CommandGroup"] = MdKind.CommandGroup,
        ["CommandGroupRef"] = MdKind.CommandGroup,
        ["Subsystem"] = MdKind.Subsystem,
        ["SubsystemRef"] = MdKind.Subsystem,
        ["Role"] = MdKind.Role,
        ["Language"] = MdKind.Language,
        ["LanguageRef"] = MdKind.Language,
        ["Style"] = MdKind.Style,
        ["StyleItem"] = MdKind.StyleItem,
        ["StyleItemRef"] = MdKind.StyleItem,
        ["FilterCriterion"] = MdKind.FilterCriterion,
        ["FilterCriterionRef"] = MdKind.FilterCriterion,
        ["DefinedType"] = MdKind.DefinedType,
        ["FunctionalOption"] = MdKind.FunctionalOption,
        ["FunctionalOptionsParameter"] = MdKind.FunctionalOptionsParameter,
        ["EventSubscription"] = MdKind.EventSubscription,
        ["ScheduledJob"] = MdKind.ScheduledJob,
        ["SessionParameter"] = MdKind.SessionParameter,
        ["SettingsStorage"] = MdKind.SettingsStorage,
        ["HTTPService"] = MdKind.HTTPService,
        ["WebService"] = MdKind.WebService,
        ["WSReference"] = MdKind.WSReference,
        ["XDTOPackage"] = MdKind.XDTOPackage,
        ["ExternalDataSource"] = MdKind.ExternalDataSource,
        ["ExternalDataSourceRef"] = MdKind.ExternalDataSource,
        ["IntegrationService"] = MdKind.IntegrationService,
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenDictionary<string, MdKind> Collections = new Dictionary<string, MdKind>(StringComparer.Ordinal)
    {
        ["Справочники"] = MdKind.Catalog,
        ["Документы"] = MdKind.Document,
        ["ЖурналыДокументов"] = MdKind.DocumentJournal,
        ["Перечисления"] = MdKind.Enum,
        ["Константы"] = MdKind.Constant,
        ["РегистрыСведений"] = MdKind.InformationRegister,
        ["РегистрыНакопления"] = MdKind.AccumulationRegister,
        ["РегистрыБухгалтерии"] = MdKind.AccountingRegister,
        ["РегистрыРасчета"] = MdKind.CalculationRegister,
        ["БизнесПроцессы"] = MdKind.BusinessProcess,
        ["Задачи"] = MdKind.Task,
        ["ПланыСчетов"] = MdKind.ChartOfAccounts,
        ["ПланыВидовХарактеристик"] = MdKind.ChartOfCharacteristicTypes,
        ["ПланыВидовРасчета"] = MdKind.ChartOfCalculationTypes,
        ["ПланыОбмена"] = MdKind.ExchangePlan,
        ["Последовательности"] = MdKind.Sequence,
        ["Обработки"] = MdKind.DataProcessor,
        ["Отчеты"] = MdKind.Report,
        ["ОбщиеМодули"] = MdKind.CommonModule,
        ["ОбщиеФормы"] = MdKind.CommonForm,
        ["ОбщиеКоманды"] = MdKind.CommonCommand,
        ["ОбщиеМакеты"] = MdKind.CommonTemplate,
        ["ОбщиеКартинки"] = MdKind.CommonPicture,
        ["ОбщиеРеквизиты"] = MdKind.CommonAttribute,
        ["ГруппыКоманд"] = MdKind.CommandGroup,
        ["Подсистемы"] = MdKind.Subsystem,
        ["Роли"] = MdKind.Role,
        ["Языки"] = MdKind.Language,
        ["Стили"] = MdKind.Style,
        ["ЭлементыСтиля"] = MdKind.StyleItem,
        ["КритерииОтбора"] = MdKind.FilterCriterion,
        ["ОпределяемыеТипы"] = MdKind.DefinedType,
        ["ФункциональныеОпции"] = MdKind.FunctionalOption,
        ["ПараметрыФункциональныхОпций"] = MdKind.FunctionalOptionsParameter,
        ["ПодпискиНаСобытия"] = MdKind.EventSubscription,
        ["РегламентныеЗадания"] = MdKind.ScheduledJob,
        ["ПараметрыСеанса"] = MdKind.SessionParameter,
        ["ХранилищаНастроек"] = MdKind.SettingsStorage,
        ["HTTPСервисы"] = MdKind.HTTPService,
        ["ВебСервисы"] = MdKind.WebService,
        ["WSСсылки"] = MdKind.WSReference,
        ["ПакетыXDTO"] = MdKind.XDTOPackage,
        ["ВнешниеИсточникиДанных"] = MdKind.ExternalDataSource,
        ["СервисыИнтеграции"] = MdKind.IntegrationService,
        ["Формы"] = MdKind.Form,
        ["Макеты"] = MdKind.Template,
        ["Команды"] = MdKind.Command,
        ["Метаданные"] = MdKind.MetadataRoot,
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>Каталог выгрузки («Catalogs») → вид объекта.</summary>
    public static bool TryKindFromSection(string folder, out MdKind kind) => Sections.TryGetValue(folder, out kind);

    /// <summary>Префикс типа («CatalogRef») → вид объекта.</summary>
    public static bool TryKindFromTypePrefix(string prefix, out MdKind kind) => TypePrefixes.TryGetValue(prefix, out kind);

    /// <summary>Имя коллекции в коде BSL («Справочники») → вид объекта.</summary>
    public static bool TryKindFromCollection(string identifier, out MdKind kind) => Collections.TryGetValue(identifier, out kind);

    /// <summary>Известные каталоги выгрузки.</summary>
    public static IEnumerable<string> KnownSections => Sections.Keys;

    /// <summary>Канонический идентификатор объекта: «Catalog.Товары».</summary>
    public static string CreateId(MdKind kind, string name) => $"{kind.Name}.{name}";

    /// <summary>Разбирает идентификатор вида «Catalog.Товары».</summary>
    public static bool TrySplitId(string id, out MdKind kind, out string name)
    {
        kind = MdKind.Unknown;
        name = string.Empty;
        if (string.IsNullOrEmpty(id))
        {
            return false;
        }

        var separator = id.IndexOf('.');
        if (separator <= 0 || separator == id.Length - 1)
        {
            return false;
        }

        kind = new MdKind(id[..separator]);
        name = id[(separator + 1)..];
        return true;
    }
}
