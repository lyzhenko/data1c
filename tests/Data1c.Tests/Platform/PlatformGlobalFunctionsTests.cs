using Data1c.Core.Platform;
using Data1c.FileSystem;
using Xunit;

namespace Data1c.Tests.Platform;

/// <summary>
/// Самопроверка встроенного списка глобальных функций платформы (<see cref="PlatformGlobalFunctions"/>).
/// Список снят со справки 8.3.27, поэтому справка установленной платформы не должна знать имён,
/// которых в нём нет: иначе вызов такой функции агент увидит как ошибку «неизвестная процедура».
/// Если платформы на машине нет, проверка проходит вхолостую — как <see cref="RealPlatformTests"/>.
/// </summary>
public sealed class PlatformGlobalFunctionsTests
{
    [Fact]
    public void Справка_платформы_покрыта_встроенным_списком()
    {
        var source = new FileSystemPlatformSource();
        if (!source.EnumerateInstallations().Any())
        {
            Console.WriteLine("Платформа 1С не найдена — сверка встроенного списка со справкой пропущена.");
            return;
        }

        var index = new PlatformHelpIndex(source);
        var names = index.GlobalFunctionNames;
        var missing = names
            .Where(static name => !PlatformGlobalFunctions.IsKnown(name))
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToList();

        Console.WriteLine($"платформа: {index.Version} | глобальных функций в справке: {names.Count} "
            + $"| имён во встроенном списке: {PlatformGlobalFunctions.All.Count}");

        Assert.True(
            missing.Count == 0,
            $"Во встроенном списке PlatformGlobalFunctions нет {missing.Count} имён из справки {index.Version}: "
                + string.Join(", ", missing.Take(50))
                + (missing.Count > 50 ? " и другие." : "."));
    }

    [Fact]
    public void Встроенный_список_знает_главные_глобальные_функции()
    {
        // Проверка без платформы: список должен работать и там, где справки под рукой нет.
        foreach (var name in new[]
                 {
                     "Сообщить", "СтрНайти", "Формат", "НСтр", "ЗначениеЗаполнено", "ТипЗнч", "Число", "Строка",
                     "Дата", "Найти", "ТекущаяДата", "Мин", "Макс", "Тип", "ПустаяСтрока", "СокрЛП", "СтрШаблон",
                     "Message", "Format", "StrTemplate", "Sin",
                 })
        {
            Assert.True(PlatformGlobalFunctions.IsKnown(name), $"«{name}» должно быть во встроенном списке.");
        }

        Console.WriteLine($"имён во встроенном списке: {PlatformGlobalFunctions.All.Count}");
        Assert.True(
            PlatformGlobalFunctions.All.Count > 500,
            $"имён во встроенном списке: {PlatformGlobalFunctions.All.Count}");

        Assert.False(PlatformGlobalFunctions.IsKnown("НетТакойФункции"));
        Assert.False(PlatformGlobalFunctions.IsKnown(null));
        Assert.False(PlatformGlobalFunctions.IsKnown("   "));
        Assert.True(PlatformGlobalFunctions.IsKnown("  сообщить  "));
    }
}
