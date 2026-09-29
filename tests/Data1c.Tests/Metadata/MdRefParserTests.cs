using Data1c.Core.Dump;
using Data1c.Core.Metadata;
using Xunit;

namespace Data1c.Tests.Metadata;

public sealed class MdRefParserTests
{
    [Theory]
    [InlineData("cfg:CatalogRef.Товары", "Catalog", "Товары", null)]
    [InlineData("CatalogObject.Товары", "Catalog", "Товары", null)]
    [InlineData("Catalog.Товары", "Catalog", "Товары", null)]
    [InlineData("CommonModule.ОбщегоНазначения", "CommonModule", "ОбщегоНазначения", null)]
    [InlineData("Catalog.Товары.StandardAttribute.Code", "Catalog", "Товары", "StandardAttribute.Code")]
    [InlineData("cfg:InformationRegisterRef.КурсыВалют", "InformationRegister", "КурсыВалют", null)]
    [InlineData("Document.ЗаказПокупателя.Attribute.Сумма", "Document", "ЗаказПокупателя", "Attribute.Сумма")]
    public void Разбирает_ссылки_на_объекты(string text, string kind, string name, string? rest)
    {
        var parsed = MdRefParser.TryParse(text, out var actualKind, out var actualName, out var actualRest);

        Assert.True(parsed);
        Assert.Equal(kind, actualKind.Name);
        Assert.Equal(name, actualName);
        Assert.Equal(rest, actualRest);
    }

    [Theory]
    [InlineData("xs:string")]
    [InlineData("v8:ValueStorage")]
    [InlineData("")]
    [InlineData("Просто имя")]
    [InlineData("НеизвестныйВид.Объект")]
    public void Не_считает_ссылкой_посторонний_текст(string text)
    {
        Assert.False(MdRefParser.TryParse(text, out _, out _, out _));
    }

    [Fact]
    public void Строит_канонический_идентификатор()
    {
        Assert.True(MdRefParser.TryParseObjectId("cfg:CatalogRef.Товары", out var id));
        Assert.Equal("Catalog.Товары", id);
    }
}

public sealed class MdNamingTests
{
    [Theory]
    [InlineData("Catalogs", "Catalog")]
    [InlineData("CommonModules", "CommonModule")]
    [InlineData("InformationRegisters", "InformationRegister")]
    public void Определяет_вид_по_каталогу_выгрузки(string folder, string expected)
    {
        Assert.True(MdNaming.TryKindFromSection(folder, out var kind));
        Assert.Equal(expected, kind.Name);
    }

    [Theory]
    [InlineData("Справочники", "Catalog")]
    [InlineData("Документы", "Document")]
    [InlineData("РегистрыСведений", "InformationRegister")]
    [InlineData("Перечисления", "Enum")]
    public void Определяет_вид_по_имени_коллекции_в_коде(string collection, string expected)
    {
        Assert.True(MdNaming.TryKindFromCollection(collection, out var kind));
        Assert.Equal(expected, kind.Name);
    }
}

public sealed class DumpPathTests
{
    [Theory]
    [InlineData("Catalogs\\Товары.xml", "Catalogs/Товары.xml")]
    [InlineData("./Catalogs/Товары.xml", "Catalogs/Товары.xml")]
    [InlineData("/Catalogs/Товары.xml", "Catalogs/Товары.xml")]
    public void Нормализует_пути(string input, string expected)
    {
        Assert.Equal(expected, DumpPath.Normalize(input));
    }

    [Fact]
    public void Разбирает_части_пути()
    {
        Assert.Equal("Catalogs/Товары/Ext", DumpPath.GetDirectory("Catalogs/Товары/Ext/ObjectModule.bsl"));
        Assert.Equal("ObjectModule.bsl", DumpPath.GetFileName("Catalogs/Товары/Ext/ObjectModule.bsl"));
        Assert.Equal(".bsl", DumpPath.GetExtension("Catalogs/Товары/Ext/ObjectModule.bsl"));
        Assert.Equal("Товары", DumpPath.GetFileNameWithoutExtension("Catalogs/Товары.xml"));
        Assert.Equal("Catalogs/Товары", DumpPath.TakeSegments("Catalogs/Товары/Ext/ObjectModule.bsl", 2));
    }
}
