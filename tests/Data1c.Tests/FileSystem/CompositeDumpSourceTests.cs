using System.Text;
using Data1c.Core.Dump;
using Data1c.FileSystem;
using Xunit;

namespace Data1c.Tests.FileSystem;

/// <summary>
/// Составная выгрузка: база конфигурации и расширение, у которых часть модулей лежит по одним
/// и тем же относительным путям.
/// </summary>
public sealed class CompositeDumpSourceTests
{
    private const string ModulePath = "CommonModules/Общий/Ext/Module.bsl";

    [Fact]
    public void Перекрытый_файл_читается_из_последнего_источника()
    {
        var composite = Create();

        Assert.Equal("модуль расширения", Read(composite, ModulePath));
        Assert.True(composite.IsOverridden(ModulePath));
        Assert.Equal("расширение", composite.SourceOf(ModulePath)!.DisplayName);
    }

    [Fact]
    public void Корень_конфигурации_не_перекрывается_расширением()
    {
        var composite = Create();

        // Иначе расширение подменило бы корень конфигурации и модель метаданных описала бы только его.
        Assert.Equal("базовый корень", Read(composite, "Configuration.xml"));
        Assert.Equal("база", composite.SourceOf("Configuration.xml")!.DisplayName);
        Assert.True(composite.IsOverridden("Configuration.xml"));
    }

    [Fact]
    public void Перечисление_не_дублирует_пути_а_EnumerateAll_отдаёт_обе_версии()
    {
        var composite = Create();

        var paths = composite.EnumerateFiles().Select(static file => file.RelativePath).ToList();
        Assert.Equal(4, paths.Count);
        Assert.Contains(ModulePath, paths);
        Assert.Contains("Catalogs/Товары.xml", paths);
        Assert.Contains("Catalogs/Новый.xml", paths);

        var all = composite.EnumerateAll().ToList();
        Assert.Equal(6, all.Count);
        Assert.Equal(2, all.Count(entry => entry.File.RelativePath == ModulePath));
        Assert.Equal(
            new[] { 0, 1 },
            all.Where(entry => entry.File.RelativePath == ModulePath).Select(static entry => entry.SourceIndex).Order().ToList());
    }

    [Fact]
    public void Один_источник_ведёт_себя_как_обычная_выгрузка()
    {
        var composite = new CompositeDumpSource([CreateBase()]);

        Assert.False(composite.IsOverridden(ModulePath));
        Assert.Equal("модуль базы", Read(composite, ModulePath));
        Assert.Equal("база", composite.DisplayName);
    }

    [Fact]
    public void Пустой_список_источников_отвергается()
    {
        Assert.Throws<ArgumentException>(() => new CompositeDumpSource([]));
    }

    private static CompositeDumpSource Create() => new([CreateBase(), CreateExtension()]);

    private static InMemoryDumpSource CreateBase()
    {
        var source = new InMemoryDumpSource("база");
        source.AddText("Configuration.xml", "базовый корень");
        source.AddText(ModulePath, "модуль базы");
        source.AddText("Catalogs/Товары.xml", "объект базы");
        return source;
    }

    private static InMemoryDumpSource CreateExtension()
    {
        var source = new InMemoryDumpSource("расширение");
        source.AddText("Configuration.xml", "корень расширения");
        source.AddText(ModulePath, "модуль расширения");
        source.AddText("Catalogs/Новый.xml", "объект расширения");
        return source;
    }

    private static string Read(IDumpSource source, string relativePath)
    {
        using var stream = source.OpenRead(new DumpFile(relativePath, 0, DateTimeOffset.UnixEpoch));
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
