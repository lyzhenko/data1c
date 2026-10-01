namespace Data1c.Core.Platform;

/// <summary>
/// Перевод мест, как их называет платформа («ОбщийМодуль.Настройки.Модуль(3,9)»), в пути файлов
/// выгрузки. Нужен, чтобы замечание штатной проверки можно было открыть рядом с кодом.
/// Формат снят с реального журнала <c>/CheckConfig</c> на выгрузке 2,9 ГБ.
/// </summary>
public static class PlatformLocation
{
    /// <summary>Разделы выгрузки по видам объектов платформы.</summary>
    private static readonly Dictionary<string, string> SectionByKind = new(StringComparer.Ordinal)
    {
        ["ОбщийМодуль"] = "CommonModules",
        ["Справочник"] = "Catalogs",
        ["Документ"] = "Documents",
        ["Обработка"] = "DataProcessors",
        ["Отчет"] = "Reports",
        ["РегистрСведений"] = "InformationRegisters",
        ["РегистрНакопления"] = "AccumulationRegisters",
        ["РегистрБухгалтерии"] = "AccountingRegisters",
        ["РегистрРасчета"] = "CalculationRegisters",
        ["ПланВидовХарактеристик"] = "ChartsOfCharacteristicTypes",
        ["ПланСчетов"] = "ChartsOfAccounts",
        ["ПланВидовРасчета"] = "ChartsOfCalculationTypes",
        ["ПланОбмена"] = "ExchangePlans",
        ["БизнесПроцесс"] = "BusinessProcesses",
        ["Задача"] = "Tasks",
        ["Перечисление"] = "Enums",
        ["Константа"] = "Constants",
        ["ОбщаяФорма"] = "CommonForms",
        ["ЖурналДокументов"] = "DocumentJouRNals",
        ["КритерийОтбора"] = "FilterCriteria",
        ["ОбщийМакет"] = "CommonTemplates",
    };

    /// <summary>Файлы модулей по последнему слову в записи платформы.</summary>
    private static readonly Dictionary<string, string> ModuleFileByPart = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Модуль"] = "Ext/Module.bsl",
        ["МодульОбъекта"] = "Ext/ObjectModule.bsl",
        ["МодульМенеджера"] = "Ext/ManagerModule.bsl",
        ["МодульНабораЗаписей"] = "Ext/RecordSetModule.bsl",
        ["МодульКоманды"] = "Ext/CommandModule.bsl",
        ["МодульЗначенияПеречисления"] = "Ext/ManagerModule.bsl",
    };

    /// <summary>Номер строки из записи вида «ОбщийМодуль.Имя.Модуль(3,9)».</summary>
    public static int? LineOf(string? place)
    {
        if (string.IsNullOrWhiteSpace(place))
        {
            return null;
        }

        var close = place.LastIndexOf(')');
        var open = place.LastIndexOf('(');
        if (open < 0 || close < open)
        {
            return null;
        }

        var coordinates = place[(open + 1)..close].Split(',');
        return coordinates.Length > 0 && int.TryParse(coordinates[0].Trim(), out var line) ? line : null;
    }

    /// <summary>Путь файла выгрузки для записи платформы; <see langword="null"/>, если сопоставить не удалось.</summary>
    public static string? ResolveFilePath(string? place)
    {
        if (string.IsNullOrWhiteSpace(place))
        {
            return null;
        }

        var open = place.LastIndexOf('(');
        var bare = (open > 0 ? place[..open] : place).Trim();
        var parts = bare.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3 || !SectionByKind.TryGetValue(parts[0], out var section))
        {
            return null;
        }

        var name = parts[1];
        var head = section + "/" + name;

        // Форма: «Справочник.Имя.Форма.ИмяФормы.Модуль» или «…Форма.ИмяФормы.Форма».
        var form = Array.IndexOf(parts, "Форма");
        if (form > 1 && form + 1 < parts.Length)
        {
            var formName = parts[form + 1];
            var tail = parts[^1];
            if (string.Equals(tail, "Форма", StringComparison.OrdinalIgnoreCase))
            {
                return $"{head}/Forms/{formName}/Ext/Form.xml";
            }

            return string.Equals(tail, "Модуль", StringComparison.OrdinalIgnoreCase)
                ? $"{head}/Forms/{formName}/Ext/Form/Module.bsl"
                : null;
        }

        return ModuleFileByPart.TryGetValue(parts[^1], out var module)
            ? $"{head}/{module}"
            : null;
    }
}
