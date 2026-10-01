using System.Collections.Frozen;

namespace Data1c.Core.Platform;

/// <summary>
/// Имена глобальных функций платформы 1С, которые библиотека знает без справки. Нужны там, где
/// установленной платформы под рукой нет: вызов без получателя («Сообщить("текст")») — это
/// глобальная функция платформы, а не неизвестная процедура.
/// </summary>
/// <remarks>
/// <para>Список снят с установленной платформы 1С 8.3.27.2214 из синтакс-помощника
/// (<c>bin\shcntx_ru.hbk</c>): взяты темы раздела «Глобальный контекст», подраздела «Методы», —
/// 500 функций. У каждой функции в заголовке темы два имени, русское и английское
/// («Глобальный контекст.Сообщить (Global context.Message)»), поэтому в списке 989 имён:
/// 500 русских и 489 английских (у 11 функций, например <c>Sin</c>, написания совпадают).
/// Свойства (101) и события (33) того же раздела справки в список не вошли: вызвать их нельзя,
/// функции платформы — только методы.</para>
/// <para>Список решает, ошибка это или предупреждение, поэтому он должен быть один и тот же при
/// любой загруженности справки; сама справка (<see cref="PlatformHelpIndex"/>) остаётся источником
/// подсказок и числа параметров у методов платформы.</para>
/// <para>Как обновлять после установки новой версии платформы. Справка не копируется в репозиторий,
/// список сверяется с ней тестом <c>PlatformGlobalFunctionsTests.Справка_платформы_покрыта_встроенным_списком</c>:
/// тест берёт имена, которые справка считает глобальными функциями
/// (<see cref="PlatformHelpIndex.GlobalFunctionNames"/>), и в сообщении об ошибке перечисляет те,
/// которых во встроенном списке нет, — их и нужно добавить сюда. Полный список имён справки
/// печатается так: <c>new PlatformHelpIndex(источник).GlobalFunctionNames</c>, где источник —
/// установленная платформа (<c>FileSystemPlatformSource</c> в Data1c.FileSystem или
/// <c>InMemoryPlatformSource</c> в тестах).</para>
/// </remarks>
public static class PlatformGlobalFunctions
{
    /// <summary>
    /// Глобальные функции платформы: русское и английское написание имён, как в заголовках тем справки.
    /// Сравнение — без учёта регистра.
    /// </summary>
    public static FrozenSet<string> All { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "ACos", "ASin", "ATan", "AccessParameters", "AccessRight", "ActiveWindow", "AddMonth", "ApplicationPresentation",
        "AttachAddIn", "AttachAddInAsync", "AttachComputerInformationExtensionAsync", "AttachCryptoExtension", "AttachCryptoExtensionAsync", "AttachFileSystemExtension", "AttachFileSystemExtensionAsync", "AttachIdleHandler",
        "AttachLicensingClientParametersRequestHandler", "AttachNotificationHandler", "Base64String", "Base64Value", "Base64Значение", "Base64Строка", "Beep", "BegOfDay",
        "BegOfHour", "BegOfMinute", "BegOfMonth", "BegOfQuarter", "BegOfWeek", "BegOfYear", "BeginAttachingAddIn", "BeginAttachingComputerInformationExtension",
        "BeginAttachingCryptoExtension", "BeginAttachingFileSystemExtension", "BeginCopyingFile", "BeginCreateBinaryDataFromFile", "BeginCreatingDirectory", "BeginDeletingFiles", "BeginFindingFiles", "BeginGetFileFromServer",
        "BeginGetFilesFromServer", "BeginGettingDocumentsDir", "BeginGettingFiles", "BeginGettingMobileDeviceLibraryDir", "BeginGettingNetworkAdaptersInformation", "BeginGettingTempFilesDir", "BeginGettingUserDataWorkDir", "BeginInstallAddIn",
        "BeginInstallCryptoExtension", "BeginInstallFileSystemExtension", "BeginInstallingComputerInformationExtension", "BeginMovingFile", "BeginPutFile", "BeginPutFileToServer", "BeginPutFilesToServer", "BeginPuttingFiles",
        "BeginRequestingUserPermission", "BeginRunningApplication", "BeginTransaction", "BinDir", "BitwiseAnd", "BitwiseAndNot", "BitwiseNot", "BitwiseOr",
        "BitwiseShiftLeft", "BitwiseShiftRight", "BitwiseXor", "Boolean", "BriefErrorDescription", "CallSleep", "CanReadXML", "CannotOpenForm",
        "Char", "CharCode", "CheckAddInAttachment", "CheckBit", "CheckByBitMask", "CheckPasswordCompromise", "CheckScriptCircularRefs", "CheckUserPasswordComplianceWithStoredValue",
        "ClearEventLog", "ClearMessages", "ClearUserSettings", "ClientApplicationInterfaceCurrentVariant", "CloseHelp", "CommitTransaction", "ComputerName", "ConcatBinaryData",
        "ConcatBinaryDataBuffers", "ConfigurationChanged", "ConnectExternalDataSource", "ConnectionStopRequest", "CopyEventLog", "CopyFile", "CopyFileAsync", "CopyFormData",
        "Cos", "CreateAddInObjectAsync", "CreateBinaryDataFromFileAsync", "CreateDirectory", "CreateDirectoryAsync", "CreateXDTOFactory", "CurrentDate", "CurrentLanguage",
        "CurrentLocaleCode", "CurrentRunMode", "CurrentSessionDate", "CurrentSessionIsTested", "CurrentSystemLanguage", "CurrentUniversalDate", "CurrentUniversalDateInMilliseconds", "DataBaseConfigurationChangedDynamically",
        "DataSeparationSafeMode", "Date", "Day", "DayOfYear", "DaylightTimeOffset", "DecodeString", "DeleteDisallowedXMLCharacters", "DeleteFiles",
        "DeleteFilesAsync", "DeleteFromTempStorage", "DeleteObjects", "DetachIdleHandler", "DetachLicensingClientParametersRequestHandler", "DetachNotificationHandler", "DetailErrorDescription", "DisconnectExternalDataSource",
        "DoMessageBox", "DoMessageBoxAsync", "DoQueryBox", "DoQueryBoxAsync", "DocumentIsPasswordProtected", "DocumentIsPasswordProtectedAsync", "DocumentsDir", "DocumentsDirAsync",
        "DynamicAddInInstallationSupported", "EncodeString", "EndOfDay", "EndOfHour", "EndOfMinute", "EndOfMonth", "EndOfQuarter", "EndOfWeek",
        "EndOfYear", "EraseInfoBaseData", "ErrorDescription", "ErrorInfo", "Eval", "EvaluateStoredUserPasswordValue", "EventLogEventPresentation", "ExclusiveMode",
        "Exit", "Exp", "FillPropertyValues", "Find", "FindByRef", "FindDisallowedXMLCharacters", "FindFiles", "FindFilesAsync",
        "FindMarkedForDeletion", "FindWindowByURL", "FormDataToValue", "Format", "FromXMLType", "GetActionOnUserPasswordRequirementsViolationOnAuthentication", "GetAdditionalIndexesUsage", "GetAddressByLocation",
        "GetAllFilesMask", "GetAppearanceTemplate", "GetAvailableLocaleCodes", "GetAvailableTimeZones", "GetBase64BinaryDataBufferFromBinaryDataBuffer", "GetBase64BinaryDataFromBinaryData", "GetBase64StringFromBinaryData", "GetBase64StringFromBinaryDataBuffer",
        "GetBinaryDataBufferFromBase64BinaryDataBuffer", "GetBinaryDataBufferFromBase64String", "GetBinaryDataBufferFromBinaryData", "GetBinaryDataBufferFromHexBinaryDataBuffer", "GetBinaryDataBufferFromHexString", "GetBinaryDataBufferFromString", "GetBinaryDataFromBase64BinaryData", "GetBinaryDataFromBase64String",
        "GetBinaryDataFromBinaryDataBuffer", "GetBinaryDataFromHexBinaryData", "GetBinaryDataFromHexString", "GetBinaryDataFromString", "GetCOMObject", "GetChoiceData", "GetClientAllFilesMask", "GetClientConnectionSpeed",
        "GetClientDisplaysInformation", "GetClientPathSeparator", "GetCommonForm", "GetCommonTemplate", "GetComputerSleepModeProhibition", "GetConfigurationID", "GetCurrentInfoBaseSession", "GetDBStorageStructureInfo",
        "GetDataBaseConfigurationUpdate", "GetDatabaseAndBinaryDataStorageDataSize", "GetDatabaseDataSize", "GetEventLogDataStorageSplitPeriod", "GetEventLogEventUse", "GetEventLogFilterValues", "GetEventLogPeriod", "GetEventLogUsing",
        "GetExclusiveModeParameters", "GetExternalResourcesMode", "GetExternalURL", "GetFile", "GetFileFromServerAsync", "GetFiles", "GetFilesFromServerAsync", "GetForm",
        "GetFromTempStorage", "GetFunctionalOption", "GetHexBinaryDataBufferFromBinaryDataBuffer", "GetHexBinaryDataFromBinaryData", "GetHexStringFromBinaryData", "GetHexStringFromBinaryDataBuffer", "GetHibernateSessionTerminateTime", "GetInactivityTimeForTerminateSession",
        "GetInactivityTimeForTerminateSessionNotification", "GetInfoBaseBeginningOfCentury", "GetInfoBaseConnections", "GetInfoBasePredefinedData", "GetInfoBaseRegionalSettings", "GetInfoBaseSessions", "GetInfoBaseTimeZone", "GetInfoBaseURL",
        "GetInterfaceFunctionalOption", "GetInterfaceFunctionalOptionParameters", "GetLicensingClientAdditionalParameter", "GetLicensingClientName", "GetLocationByAddress", "GetLockWaitTime", "GetMobileClientSignatureVerificationMethod", "GetNetworkAdaptersInformationAsync",
        "GetObjectAndFormAttributeConformity", "GetObjectAndFormConformity", "GetPassiveSessionHibernateTime", "GetPathSeparator", "GetPredefinedValueFullName", "GetPreferableMainServerUse", "GetRealTimeTimestamp", "GetSafeModeDisabled",
        "GetServerAllFilesMask", "GetServerPathSeparator", "GetSessionRegionalSettings", "GetSessionsLock", "GetStandardCommonSettingsStorage", "GetStandardDynamicListsUserSettingsStorage", "GetStandardFormDataSettingsStorage", "GetStandardODataInterfaceContent",
        "GetStandardReportsUserSettingsStorage", "GetStandardReportsVariantsStorage", "GetStandardURLExternalDataStorage", "GetStringDeclensions", "GetStringDeclensionsByNumber", "GetStringFromBinaryData", "GetStringFromBinaryDataBuffer", "GetTempFileName",
        "GetTotalRecalcJobCount", "GetURL", "GetURLsPresentations", "GetUUIDWithCompatibilitySupport", "GetUnsafeActionProtectionDisabled", "GetUsedServer", "GetUserMessages", "GetUserPasswordCompromiseCheck",
        "GetUserPasswordExpirationNotificationPeriod", "GetUserPasswordHashAlgorithmType", "GetUserPasswordMaxEffectivePeriod", "GetUserPasswordMinEffectivePeriod", "GetUserPasswordMinLength", "GetUserPasswordReuseLimit", "GetUserPasswordStrengthCheck", "GetWindows",
        "GetXMLType", "GotoURL", "Hour", "ImportXDTOModel", "InfoBaseConnectionNumber", "InfoBaseConnectionString", "InfoBaseLocaleCode", "InfoBaseSessionNumber",
        "InitializePredefinedData", "InputDate", "InputDateAsync", "InputNumber", "InputNumberAsync", "InputString", "InputStringAsync", "InputValue",
        "InputValueAsync", "InstallAddIn", "InstallAddInAsync", "InstallComputerInformationExtensionAsync", "InstallCryptoExtension", "InstallCryptoExtensionAsync", "InstallFileSystemExtension", "InstallFileSystemExtensionAsync",
        "Int", "IsBlankString", "IsInRole", "IsTempStorageURL", "Left", "LoadAddIn", "LocaleCodePresentation", "LockApplication",
        "LockDataForEdit", "Log", "Log10", "Lower", "MainServerAvailable", "MapRepresentationSupported", "Max", "MergeFiles",
        "Message", "Mid", "Min", "Minute", "MobileApplicationFunctionalitySupported", "MobileDeviceLibraryDir", "MobileDeviceLibraryDirAsync", "Month",
        "MoveFile", "MoveFileAsync", "NStr", "Notify", "NotifyChanged", "Number", "NumberFromBinaryString", "NumberFromHexString",
        "NumberInWords", "OSUsers", "OpenForm", "OpenFormModal", "OpenHelp", "OpenHelpContent", "OpenHelpIndex", "OpenValue",
        "OpenValueAsync", "PeriodPresentation", "Pow", "PredefinedValue", "PrivilegedMode", "ProceedWithCall", "ProcessJobs", "PutFile",
        "PutFileToServerAsync", "PutFiles", "PutFilesToServerAsync", "PutToTempStorage", "ReadJSON", "ReadJSONDate", "ReadJSONValue", "ReadXML",
        "RefreshInterface", "RefreshObjectsNumbering", "RefreshReusableValues", "RequestUserPermission", "RequestUserPermissionAsync", "RestoreValue", "Right", "RightPresentation",
        "RollbackTransaction", "Round", "RunApp", "RunAppAsync", "RunCallback", "RunSystem", "SafeMode", "SaveUserSettings",
        "SaveValue", "Second", "SessionBeginningOfCentury", "SessionTimeZone", "SetActionOnUserPasswordRequirementsViolationOnAuthentication", "SetAdditionalIndexesUsage", "SetBit", "SetComputerSleepModeProhibition",
        "SetDataSeparationSafeMode", "SetEventLogDataStorageSplitPeriod", "SetEventLogEventUse", "SetEventLogUsing", "SetExclusiveMode", "SetHibernateSessionTerminateTime", "SetInactivityTimeForTerminateSession", "SetInactivityTimeForTerminateSessionNotification",
        "SetInfoBaseBeginningOfCentury", "SetInfoBasePredefinedDataUpdate", "SetInfoBaseRegionalSettings", "SetInfoBaseTimeZone", "SetInterfaceFunctionalOptionParameters", "SetLicensingClientParameters", "SetLockWaitTime", "SetMobileClientSignatureVerificationMethod",
        "SetObjectAndFormAttributeConformity", "SetObjectAndFormConformity", "SetPassiveSessionHibernateTime", "SetPreferableMainServerUse", "SetPrivilegedMode", "SetSafeMode", "SetSafeModeDisabled", "SetSessionTimeZone",
        "SetSessionsLock", "SetStandardODataInterfaceContent", "SetTotalRecalcJobCount", "SetUnsafeActionProtectionDisabled", "SetUsedServer", "SetUserPasswordCompromiseCheck", "SetUserPasswordExpirationNotificationPeriod", "SetUserPasswordHashAlgorithmType",
        "SetUserPasswordMaxEffectivePeriod", "SetUserPasswordMinEffectivePeriod", "SetUserPasswordMinLength", "SetUserPasswordReuseLimit", "SetUserPasswordStrengthCheck", "ShowErrorInfo", "ShowInputAppRate", "ShowInputDate",
        "ShowInputNumber", "ShowInputString", "ShowInputValue", "ShowMessageBox", "ShowOnMap", "ShowQueryBox", "ShowUserNotification", "ShowValue",
        "Sin", "SplitBinaryData", "SplitFile", "Sqrt", "StandardTimeOffset", "Status", "StrCompare", "StrConcat",
        "StrEndsWith", "StrFind", "StrFindAllByRegularExpression", "StrFindAndHighlightByAppearance", "StrFindByRegularExpression", "StrGetLine", "StrLen", "StrLikeByRegularExpression",
        "StrLineCount", "StrOccurrenceCount", "StrReplace", "StrReplaceByRegularExpression", "StrSplit", "StrStartsWith", "StrTemplate", "String",
        "StringWithNumber", "System", "Tan", "TempFilesDir", "TempFilesDirAsync", "Terminate", "TimeZone", "TimeZonePresentation",
        "Title", "ToLocalTime", "ToUniversalTime", "TransactionActive", "TrimAll", "TrimL", "TrimR", "TruncateEventLog",
        "Type", "TypeOf", "UnloadEventLog", "UnlockDataForEdit", "Upper", "UserDataWorkDir", "UserDataWorkDirAsync", "UserFullName",
        "UserInterruptProcessing", "UserName", "ValueFromFile", "ValueFromStringInternal", "ValueIsFilled", "ValueToFile", "ValueToFormData", "ValueToStringInternal",
        "VerifyAccessRights", "WeekDay", "WeekOfYear", "WindowsUsers", "WriteJSON", "WriteJSONDate", "WriteJSONValue", "WriteLogEvent",
        "WriteXML", "XMLString", "XMLType", "XMLTypeOf", "XMLValue", "XMLЗначение", "XMLСтрока", "XMLТип",
        "XMLТипЗнч", "Year", "АктивноеОкно", "БезопасныйРежим", "БезопасныйРежимРазделенияДанных", "Булево", "ВРег", "ВвестиДату",
        "ВвестиДатуАсинх", "ВвестиЗначение", "ВвестиЗначениеАсинх", "ВвестиСтроку", "ВвестиСтрокуАсинх", "ВвестиЧисло", "ВвестиЧислоАсинх", "ВозможностьЧтенияXML",
        "Вопрос", "ВопросАсинх", "ВосстановитьЗначение", "ВыгрузитьЖурналРегистрации", "ВызватьПаузу", "ВыполнитьОбработкуЗаданий", "ВыполнитьОбработкуОповещения", "ВыполнитьПроверкуПравДоступа",
        "Вычислить", "ВычислитьСохраняемоеЗначениеПароляПользователя", "Год", "ДанныеФормыВЗначение", "Дата", "День", "ДеньГода", "ДеньНедели",
        "ДобавитьМесяц", "ДокументЗащищенПаролем", "ДокументЗащищенПаролемАсинх", "ЗаблокироватьДанныеДляРедактирования", "ЗаблокироватьРаботуПользователя", "ЗавершитьРаботуСистемы", "ЗагрузитьВнешнююКомпоненту", "ЗакрытьСправку",
        "ЗаписатьJSON", "ЗаписатьXML", "ЗаписатьДатуJSON", "ЗаписатьЗначениеJSON", "ЗаписьЖурналаРегистрации", "ЗаполнитьЗначенияСвойств", "ЗапрещеноОткрытиеФорм", "ЗапроситьРазрешениеПользователя",
        "ЗапроситьРазрешениеПользователяАсинх", "ЗапуститьПриложение", "ЗапуститьПриложениеАсинх", "ЗапуститьСистему", "ЗафиксироватьТранзакцию", "ЗначениеВДанныеФормы", "ЗначениеВСтрокуВнутр", "ЗначениеВФайл",
        "ЗначениеЗаполнено", "ЗначениеИзСтрокиВнутр", "ЗначениеИзФайла", "ИзXMLТипа", "ИмпортМоделиXDTO", "ИмяКомпьютера", "ИмяПользователя", "ИнициализироватьПредопределенныеДанные",
        "ИнформацияОбОшибке", "КаталогБиблиотекиМобильногоУстройства", "КаталогБиблиотекиМобильногоУстройстваАсинх", "КаталогВременныхФайлов", "КаталогВременныхФайловАсинх", "КаталогДокументов", "КаталогДокументовАсинх", "КаталогПрограммы",
        "КодЛокализацииИнформационнойБазы", "КодСимвола", "КодироватьСтроку", "КомандаСистемы", "КонецГода", "КонецДня", "КонецКвартала", "КонецМесяца",
        "КонецМинуты", "КонецНедели", "КонецЧаса", "КонфигурацияБазыДанныхИзмененаДинамически", "КонфигурацияИзменена", "КопироватьДанныеФормы", "КопироватьФайл", "КопироватьФайлАсинх",
        "КраткоеПредставлениеОшибки", "Лев", "Макс", "МестноеВремя", "Месяц", "Мин", "Минута", "МонопольныйРежим",
        "НРег", "НСтр", "Найти", "НайтиНедопустимыеСимволыXML", "НайтиОкноПоНавигационнойСсылке", "НайтиПоСсылкам", "НайтиПомеченныеНаУдаление", "НайтиФайлы",
        "НайтиФайлыАсинх", "НачалоГода", "НачалоДня", "НачалоКвартала", "НачалоМесяца", "НачалоМинуты", "НачалоНедели", "НачалоСтолетияСеанса",
        "НачалоЧаса", "НачатьЗапросРазрешенияПользователя", "НачатьЗапускПриложения", "НачатьКопированиеФайла", "НачатьПеремещениеФайла", "НачатьПодключениеВнешнейКомпоненты", "НачатьПодключениеРасширенияПолученияИнформацииОКомпьютере", "НачатьПодключениеРасширенияРаботыСКриптографией",
        "НачатьПодключениеРасширенияРаботыСФайлами", "НачатьПоискФайлов", "НачатьПолучениеИнформацииОСетевыхАдаптерах", "НачатьПолучениеКаталогаБиблиотекиМобильногоУстройства", "НачатьПолучениеКаталогаВременныхФайлов", "НачатьПолучениеКаталогаДокументов", "НачатьПолучениеРабочегоКаталогаДанныхПользователя", "НачатьПолучениеФайлаССервера",
        "НачатьПолучениеФайлов", "НачатьПолучениеФайловССервера", "НачатьПомещениеФайла", "НачатьПомещениеФайлаНаСервер", "НачатьПомещениеФайлов", "НачатьПомещениеФайловНаСервер", "НачатьСозданиеДвоичныхДанныхИзФайла", "НачатьСозданиеКаталога",
        "НачатьТранзакцию", "НачатьУдалениеФайлов", "НачатьУстановкуВнешнейКомпоненты", "НачатьУстановкуРасширенияПолученияИнформацииОКомпьютере", "НачатьУстановкуРасширенияРаботыСКриптографией", "НачатьУстановкуРасширенияРаботыСФайлами", "НеделяГода", "НеобходимостьЗавершенияСоединения",
        "НомерСеансаИнформационнойБазы", "НомерСоединенияИнформационнойБазы", "ОбновитьИнтерфейс", "ОбновитьНумерациюОбъектов", "ОбновитьПовторноИспользуемыеЗначения", "ОбработкаПрерыванияПользователя", "ОбъединитьФайлы", "Окр",
        "ОписаниеОшибки", "Оповестить", "ОповеститьОбИзменении", "ОсновнойСерверДоступен", "ОтключитьОбработчикЗапросаНастроекКлиентаЛицензирования", "ОтключитьОбработчикОжидания", "ОтключитьОбработчикОповещения", "ОткрытьЗначение",
        "ОткрытьЗначениеАсинх", "ОткрытьИндексСправки", "ОткрытьСодержаниеСправки", "ОткрытьСправку", "ОткрытьФорму", "ОткрытьФормуМодально", "ОтменитьТранзакцию", "ОчиститьЖурналРегистрации",
        "ОчиститьНастройкиПользователя", "ОчиститьСообщения", "ПараметрыДоступа", "ПерейтиПоНавигационнойСсылке", "ПереместитьФайл", "ПереместитьФайлАсинх", "ПобитовоеИ", "ПобитовоеИНе",
        "ПобитовоеИли", "ПобитовоеИсключительноеИли", "ПобитовоеНе", "ПобитовыйСдвигВлево", "ПобитовыйСдвигВправо", "ПоддерживаетсяДинамическаяУстановкаВнешнихКомпонент", "ПоддерживаетсяОтображениеКарты", "ПоддерживаетсяФункциональностьМобильногоПриложения",
        "ПодключитьВнешнююКомпоненту", "ПодключитьВнешнююКомпонентуАсинх", "ПодключитьОбработчикЗапросаНастроекКлиентаЛицензирования", "ПодключитьОбработчикОжидания", "ПодключитьОбработчикОповещения", "ПодключитьРасширениеПолученияИнформацииОКомпьютереАсинх", "ПодключитьРасширениеРаботыСКриптографией", "ПодключитьРасширениеРаботыСКриптографиейАсинх",
        "ПодключитьРасширениеРаботыСФайлами", "ПодключитьРасширениеРаботыСФайламиАсинх", "ПодробноеПредставлениеОшибки", "ПоказатьВводДаты", "ПоказатьВводЗначения", "ПоказатьВводОценкиПриложения", "ПоказатьВводСтроки", "ПоказатьВводЧисла",
        "ПоказатьВопрос", "ПоказатьЗначение", "ПоказатьИнформациюОбОшибке", "ПоказатьНаКарте", "ПоказатьОповещениеПользователя", "ПоказатьПредупреждение", "ПолноеИмяПользователя", "ПолучитьBase64БуферДвоичныхДанныхИзБуфераДвоичныхДанных",
        "ПолучитьBase64ДвоичныеДанныеИзДвоичныхДанных", "ПолучитьBase64СтрокуИзБуфераДвоичныхДанных", "ПолучитьBase64СтрокуИзДвоичныхДанных", "ПолучитьCOMОбъект", "ПолучитьHexБуферДвоичныхДанныхИзБуфераДвоичныхДанных", "ПолучитьHexДвоичныеДанныеИзДвоичныхДанных", "ПолучитьHexСтрокуИзБуфераДвоичныхДанных", "ПолучитьHexСтрокуИзДвоичныхДанных",
        "ПолучитьXMLТип", "ПолучитьАдресПоМестоположению", "ПолучитьБлокировкуСеансов", "ПолучитьБуферДвоичныхДанныхИзBase64БуфераДвоичныхДанных", "ПолучитьБуферДвоичныхДанныхИзBase64Строки", "ПолучитьБуферДвоичныхДанныхИзHexБуфераДвоичныхДанных", "ПолучитьБуферДвоичныхДанныхИзHexСтроки", "ПолучитьБуферДвоичныхДанныхИзДвоичныхДанных",
        "ПолучитьБуферДвоичныхДанныхИзСтроки", "ПолучитьВнешнююНавигационнуюСсылку", "ПолучитьВремяЗавершенияСеансаПриБездействии", "ПолучитьВремяЗавершенияСпящегоСеанса", "ПолучитьВремяЗасыпанияПассивногоСеанса", "ПолучитьВремяОжиданияБлокировкиДанных", "ПолучитьВремяПредупрежденияОЗавершенииСеансаПриБездействии", "ПолучитьДанныеВыбора",
        "ПолучитьДвоичныеДанныеИзBase64ДвоичныхДанных", "ПолучитьДвоичныеДанныеИзBase64Строки", "ПолучитьДвоичныеДанныеИзHexДвоичныхДанных", "ПолучитьДвоичныеДанныеИзHexСтроки", "ПолучитьДвоичныеДанныеИзБуфераДвоичныхДанных", "ПолучитьДвоичныеДанныеИзСтроки", "ПолучитьДействиеПриНесоответствииПаролейПользователейТребованиямПриАутентификации", "ПолучитьДополнительныйПараметрКлиентаЛицензирования",
        "ПолучитьДопустимыеКодыЛокализации", "ПолучитьДопустимыеЧасовыеПояса", "ПолучитьЗапретЗасыпанияКомпьютера", "ПолучитьЗначенияОтбораЖурналаРегистрации", "ПолучитьИдентификаторКонфигурации", "ПолучитьИзВременногоХранилища", "ПолучитьИмяВременногоФайла", "ПолучитьИмяКлиентаЛицензирования",
        "ПолучитьИнформациюОСетевыхАдаптерахАсинх", "ПолучитьИнформациюЭкрановКлиента", "ПолучитьИспользованиеДополнительныхИндексов", "ПолучитьИспользованиеЖурналаРегистрации", "ПолучитьИспользованиеСобытияЖурналаРегистрации", "ПолучитьИспользуемыйСервер", "ПолучитьКоличествоЗаданийПересчетаИтогов", "ПолучитьМакетОформления",
        "ПолучитьМаксимальныйСрокДействияПаролейПользователей", "ПолучитьМаскуВсеФайлы", "ПолучитьМаскуВсеФайлыКлиента", "ПолучитьМаскуВсеФайлыСервера", "ПолучитьМестоположениеПоАдресу", "ПолучитьМинимальнуюДлинуПаролейПользователей", "ПолучитьМинимальныйСрокДействияПаролейПользователей", "ПолучитьНавигационнуюСсылку",
        "ПолучитьНавигационнуюСсылкуИнформационнойБазы", "ПолучитьНачалоСтолетияИнформационнойБазы", "ПолучитьОбновлениеКонфигурацииБазыДанных", "ПолучитьОбновлениеПредопределенныхДанныхИнформационнойБазы", "ПолучитьОбщийМакет", "ПолучитьОбщуюФорму", "ПолучитьОграничениеПовторенияПаролейПользователейСредиПоследних", "ПолучитьОкна",
        "ПолучитьОперативнуюОтметкуВремени", "ПолучитьОтключениеБезопасногоРежима", "ПолучитьОтключениеЗащитыОтОпасныхДействий", "ПолучитьПараметрыМонопольногоРежима", "ПолучитьПараметрыФункциональныхОпцийИнтерфейса", "ПолучитьПериодЖурналаРегистрации", "ПолучитьПериодРазделенияХраненияДанныхЖурналаРегистрации", "ПолучитьПолноеИмяПредопределенногоЗначения",
        "ПолучитьПредставленияНавигационныхСсылок", "ПолучитьПреимущественноеИспользованиеОсновногоСервера", "ПолучитьПроверкуРаскрытияПаролейПользователей", "ПолучитьПроверкуСложностиПаролейПользователей", "ПолучитьРазделительПути", "ПолучитьРазделительПутиКлиента", "ПолучитьРазделительПутиСервера", "ПолучитьРазмерДанныхБазыДанных",
        "ПолучитьРазмерДанныхБазыДанныхИХранилищаДвоичныхДанных", "ПолучитьРегиональныеНастройкиИнформационнойБазы", "ПолучитьРегиональныеНастройкиСеанса", "ПолучитьРежимВнешнихРесурсов", "ПолучитьСеансыИнформационнойБазы", "ПолучитьСклоненияСтроки", "ПолучитьСклоненияСтрокиПоЧислу", "ПолучитьСкоростьКлиентскогоСоединения",
        "ПолучитьСоединенияИнформационнойБазы", "ПолучитьСообщенияПользователю", "ПолучитьСоответствиеОбъектаИРеквизитаФормы", "ПолучитьСоответствиеОбъектаИФормы", "ПолучитьСоставСтандартногоИнтерфейсаOData", "ПолучитьСпособПроверкиПодписиМобильногоКлиента", "ПолучитьСрокПредупрежденияОбИстеченииСрокаДействияПаролейПользователей", "ПолучитьСтандартноеХранилищеВариантовОтчетов",
        "ПолучитьСтандартноеХранилищеВнешнихДанныхНавигационныхСсылок", "ПолучитьСтандартноеХранилищеНастроекДанныхФорм", "ПолучитьСтандартноеХранилищеОбщихНастроек", "ПолучитьСтандартноеХранилищеПользовательскихНастроекДинамическихСписков", "ПолучитьСтандартноеХранилищеПользовательскихНастроекОтчетов", "ПолучитьСтрокуИзБуфераДвоичныхДанных", "ПолучитьСтрокуИзДвоичныхДанных", "ПолучитьСтруктуруХраненияБазыДанных",
        "ПолучитьТекущийСеансИнформационнойБазы", "ПолучитьТипАлгоритмаХешированияПаролейПользователей", "ПолучитьУникальныйИдентификаторСПоддержкойСовместимости", "ПолучитьФайл", "ПолучитьФайлССервераАсинх", "ПолучитьФайлы", "ПолучитьФайлыССервераАсинх", "ПолучитьФорму",
        "ПолучитьФункциональнуюОпцию", "ПолучитьФункциональнуюОпциюИнтерфейса", "ПолучитьЧасовойПоясИнформационнойБазы", "ПользователиWindows", "ПользователиОС", "ПоместитьВоВременноеХранилище", "ПоместитьФайл", "ПоместитьФайлНаСерверАсинх",
        "ПоместитьФайлы", "ПоместитьФайлыНаСерверАсинх", "Прав", "ПравоДоступа", "ПредопределенноеЗначение", "ПредставлениеКодаЛокализации", "ПредставлениеПериода", "ПредставлениеПрава",
        "ПредставлениеПриложения", "ПредставлениеСобытияЖурналаРегистрации", "ПредставлениеЧасовогоПояса", "Предупреждение", "ПредупреждениеАсинх", "ПрекратитьРаботуСистемы", "ПривилегированныйРежим", "ПроверитьБит",
        "ПроверитьПоБитовойМаске", "ПроверитьПодключениеВнешнейКомпоненты", "ПроверитьРаскрытиеПароля", "ПроверитьСоответствиеПароляПользователяСохраняемомуЗначению", "ПроверитьЦиклическиеСсылкиВстроенногоЯзыка", "ПродолжитьВызов", "ПрочитатьJSON", "ПрочитатьXML",
        "ПрочитатьДатуJSON", "ПрочитатьЗначениеJSON", "ПустаяСтрока", "РабочийКаталогДанныхПользователя", "РабочийКаталогДанныхПользователяАсинх", "РазблокироватьДанныеДляРедактирования", "РазделитьДвоичныеДанные", "РазделитьФайл",
        "РазорватьСоединениеСВнешнимИсточникомДанных", "РаскодироватьСтроку", "РольДоступна", "Секунда", "Сигнал", "Символ", "СкопироватьЖурналРегистрации", "СмещениеЛетнегоВремени",
        "СмещениеСтандартногоВремени", "СоединитьБуферыДвоичныхДанных", "СоединитьДвоичныеДанные", "СоздатьДвоичныеДанныеИзФайлаАсинх", "СоздатьКаталог", "СоздатьКаталогАсинх", "СоздатьОбъектВнешнейКомпонентыАсинх", "СоздатьФабрикуXDTO",
        "СокрЛ", "СокрЛП", "СокрП", "СократитьЖурналРегистрации", "Сообщить", "Состояние", "СохранитьЗначение", "СохранитьНастройкиПользователя",
        "Сред", "СтрДлина", "СтрЗаканчиваетсяНа", "СтрЗаменить", "СтрЗаменитьПоРегулярномуВыражению", "СтрНайти", "СтрНайтиВсеПоРегулярномуВыражению", "СтрНайтиИВыделитьОформлением",
        "СтрНайтиПоРегулярномуВыражению", "СтрНачинаетсяС", "СтрПодобнаПоРегулярномуВыражению", "СтрПолучитьСтроку", "СтрРазделить", "СтрСоединить", "СтрСравнить", "СтрЧислоВхождений",
        "СтрЧислоСтрок", "СтрШаблон", "Строка", "СтрокаСЧислом", "СтрокаСоединенияИнформационнойБазы", "ТРег", "ТекущаяДата", "ТекущаяДатаСеанса",
        "ТекущаяУниверсальнаяДата", "ТекущаяУниверсальнаяДатаВМиллисекундах", "ТекущийВариантИнтерфейсаКлиентскогоПриложения", "ТекущийКодЛокализации", "ТекущийРежимЗапуска", "ТекущийСеансТестируется", "ТекущийЯзык", "ТекущийЯзыкСистемы",
        "Тип", "ТипЗнч", "ТранзакцияАктивна", "УдалитьДанныеИнформационнойБазы", "УдалитьИзВременногоХранилища", "УдалитьНедопустимыеСимволыXML", "УдалитьОбъекты", "УдалитьФайлы",
        "УдалитьФайлыАсинх", "УниверсальноеВремя", "УстановитьБезопасныйРежим", "УстановитьБезопасныйРежимРазделенияДанных", "УстановитьБит", "УстановитьБлокировкуСеансов", "УстановитьВнешнююКомпоненту", "УстановитьВнешнююКомпонентуАсинх",
        "УстановитьВремяЗавершенияСеансаПриБездействии", "УстановитьВремяЗавершенияСпящегоСеанса", "УстановитьВремяЗасыпанияПассивногоСеанса", "УстановитьВремяОжиданияБлокировкиДанных", "УстановитьВремяПредупрежденияОЗавершенииСеансаПриБездействии", "УстановитьДействиеПриНесоответствииПаролейПользователейТребованиямПриАутентификации", "УстановитьЗапретЗасыпанияКомпьютера", "УстановитьИспользованиеДополнительныхИндексов",
        "УстановитьИспользованиеЖурналаРегистрации", "УстановитьИспользованиеСобытияЖурналаРегистрации", "УстановитьИспользуемыйСервер", "УстановитьКоличествоЗаданийПересчетаИтогов", "УстановитьМаксимальныйСрокДействияПаролейПользователей", "УстановитьМинимальнуюДлинуПаролейПользователей", "УстановитьМинимальныйСрокДействияПаролейПользователей", "УстановитьМонопольныйРежим",
        "УстановитьНастройкиКлиентаЛицензирования", "УстановитьНачалоСтолетияИнформационнойБазы", "УстановитьОбновлениеПредопределенныхДанныхИнформационнойБазы", "УстановитьОграничениеПовторенияПаролейПользователейСредиПоследних", "УстановитьОтключениеБезопасногоРежима", "УстановитьОтключениеЗащитыОтОпасныхДействий", "УстановитьПараметрыФункциональныхОпцийИнтерфейса", "УстановитьПериодРазделенияХраненияДанныхЖурналаРегистрации",
        "УстановитьПреимущественноеИспользованиеОсновногоСервера", "УстановитьПривилегированныйРежим", "УстановитьПроверкуРаскрытияПаролейПользователей", "УстановитьПроверкуСложностиПаролейПользователей", "УстановитьРасширениеПолученияИнформацииОКомпьютереАсинх", "УстановитьРасширениеРаботыСКриптографией", "УстановитьРасширениеРаботыСКриптографиейАсинх", "УстановитьРасширениеРаботыСФайлами",
        "УстановитьРасширениеРаботыСФайламиАсинх", "УстановитьРегиональныеНастройкиИнформационнойБазы", "УстановитьСоединениеСВнешнимИсточникомДанных", "УстановитьСоответствиеОбъектаИРеквизитаФормы", "УстановитьСоответствиеОбъектаИФормы", "УстановитьСоставСтандартногоИнтерфейсаOData", "УстановитьСпособПроверкиПодписиМобильногоКлиента", "УстановитьСрокПредупрежденияОбИстеченииСрокаДействияПаролейПользователей",
        "УстановитьТипАлгоритмаХешированияПаролейПользователей", "УстановитьЧасовойПоясИнформационнойБазы", "УстановитьЧасовойПоясСеанса", "Формат", "Цел", "Час", "ЧасовойПояс", "ЧасовойПоясСеанса",
        "Число", "ЧислоИзДвоичнойСтроки", "ЧислоИзШестнадцатеричнойСтроки", "ЧислоПрописью", "ЭтоАдресВременногоХранилища"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Глобальная функция платформы с таким именем?</summary>
    public static bool IsKnown(string? name) => !string.IsNullOrWhiteSpace(name) && All.Contains(name.Trim());
}
