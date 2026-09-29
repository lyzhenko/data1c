using Data1c.Core.Bsl;
using Data1c.Core.Metadata;
using Xunit;

namespace Data1c.Tests.Metadata;

public sealed class MetadataDumpReaderTests
{
    private static MetadataReadResult ReadSample() => new MetadataDumpReader().Read(SampleDump.Create());

    [Fact]
    public void Читает_корень_конфигурации()
    {
        var result = ReadSample();

        Assert.Equal("ТестоваяКонфигурация", result.Model.Configuration.Name);
        Assert.Equal("Тестовая конфигурация", result.Model.Configuration.Synonym);
        Assert.Equal("ManagedApplication", result.Model.Configuration.GetProperty("DefaultRunMode"));
        Assert.Equal(0, result.FailedFiles);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Собирает_объекты_верхнего_уровня_без_дубликатов()
    {
        var result = ReadSample();

        Assert.Equal(4, result.Model.Configuration.Children.Count);
        Assert.DoesNotContain(result.Model.Configuration.Children, static c => c.IsNameOnlyReference);
        Assert.Equal(2, result.Model.TopLevelOfKind(MdKind.CommonModule).Count());
        Assert.NotNull(result.Model.Find("Catalog.Товары"));
        Assert.NotNull(result.Model.Find("Subsystem.Продажи"));
    }

    [Fact]
    public void Читает_свойства_и_реквизиты_объекта()
    {
        var result = ReadSample();
        var catalog = result.Model.Find("Catalog.Товары");

        Assert.NotNull(catalog);
        Assert.Equal("Номенклатура", catalog.Synonym);
        Assert.Equal(Guid.Parse("11111111-1111-1111-1111-111111111111"), catalog.Uuid);
        Assert.Equal("9", catalog.GetProperty("CodeLength"));

        var article = catalog.Descendants().First(static o => o.Name == "Артикул");
        Assert.Equal(MdKind.Attribute, article.Kind);
        Assert.Empty(article.References);
        Assert.Equal("Catalog.Товары/Attribute.Артикул", article.Id);
    }

    [Fact]
    public void Заменяет_ссылку_по_имени_полноценным_объектом()
    {
        var result = ReadSample();
        var catalog = result.Model.Find("Catalog.Товары");
        Assert.NotNull(catalog);

        var form = catalog.FindChild(MdKind.Form, "ФормаЭлемента");
        Assert.NotNull(form);
        Assert.False(form.IsNameOnlyReference);
        Assert.Equal("Catalog.Товары/Form.ФормаЭлемента", form.Id);
        Assert.Equal(Guid.Parse("55555555-5555-5555-5555-555555555555"), form.Uuid);
        Assert.Equal("Managed", form.GetProperty("FormType"));
    }

    [Fact]
    public void Собирает_ссылки_метаданных()
    {
        var result = ReadSample();
        var catalog = result.Model.Find("Catalog.Товары");
        Assert.NotNull(catalog);

        var unit = catalog.Descendants().First(static o => o.Name == "Единица");
        var typeReference = Assert.Single(unit.References);
        Assert.Equal("Catalog.ЕдиницыИзмерения", typeReference.TargetId);
        Assert.Equal(MdReferenceKind.Type, typeReference.Kind);
        Assert.Equal("cfg:CatalogRef.ЕдиницыИзмерения", typeReference.Raw);

        var subsystem = result.Model.Find("Subsystem.Продажи");
        Assert.NotNull(subsystem);
        Assert.Equal(2, subsystem.References.Count);
        Assert.Contains(subsystem.References, static r => r.TargetId == "Catalog.Товары" && r.Kind == MdReferenceKind.Content);
        Assert.Contains(subsystem.References, static r => r.TargetId == "CommonModule.ОбщегоНазначения");
    }

    [Fact]
    public void Привязывает_модули_к_объектам()
    {
        var result = ReadSample();

        Assert.Equal(3, result.ModuleFiles.Count);

        var commonModule = result.ModuleFiles.First(static m => m.File.RelativePath == SampleDump.CommonModuleBslPath);
        Assert.Equal("CommonModule.ОбщегоНазначения", commonModule.OwnerId);
        Assert.Equal(BslModuleKind.CommonModule, commonModule.Kind);

        var formModule = result.ModuleFiles.First(static m => m.File.RelativePath == SampleDump.FormModuleBslPath);
        Assert.Equal("Catalog.Товары/Form.ФормаЭлемента", formModule.OwnerId);
        Assert.Equal(BslModuleKind.FormModule, formModule.Kind);

        var catalog = result.Model.Find("CommonModule.ОбщегоНазначения");
        Assert.NotNull(catalog);
        Assert.Equal(SampleDump.CommonModuleBslPath, Assert.Single(catalog.Modules).RelativePath);
    }

    [Fact]
    public void Пропускает_файлы_без_корня_MetaDataObject()
    {
        var source = SampleDump.Create();
        source.AddText("Ext/CustomInterface.xml", """<?xml version="1.0" encoding="UTF-8"?><ClientInterface/>""");

        var result = new MetadataDumpReader().Read(source);

        Assert.Equal(0, result.FailedFiles);
        Assert.Equal(6, result.ParsedFiles);
        Assert.Equal(1, result.SkippedFiles);
    }

    [Fact]
    public void Не_падает_на_битом_xml_и_сообщает_о_нём()
    {
        var source = SampleDump.Create();
        source.AddText("Catalogs/Битый.xml", """<?xml version="1.0" encoding="UTF-8"?><MetaDataObject><Catalog>""");

        var result = new MetadataDumpReader().Read(source);

        Assert.Equal(1, result.FailedFiles);
        Assert.Equal(6, result.ParsedFiles);
        Assert.Contains(result.Warnings, static w => w.Contains("Битый.xml", StringComparison.Ordinal));
    }
}
