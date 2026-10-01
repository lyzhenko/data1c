using System.Text;
using Data1c.Core.Metadata;
using Xunit;

namespace Data1c.Tests.Metadata;

/// <summary>
/// Проверки потокового разбора прав ролей (<c>Roles/&lt;Имя&gt;/Ext/Rights.xml</c>): объекты, права
/// со значениями true/false, признак и текст условия RLS, устойчивость к незнакомым узлам и битому XML.
/// </summary>
public sealed class RightsDumpReaderTests
{
    [Fact]
    public void Разбор_читает_признаки_объекты_права_и_RLS()
    {
        var result = Read(RightsSampleDump.RightsXml, RightsSampleDump.RightsPath);

        Assert.Empty(result.Warnings);
        Assert.Equal("Менеджер", result.Rights.Role);
        Assert.Equal("Role.Менеджер", result.Rights.RoleId);
        Assert.False(result.Rights.SetForNewObjects);
        Assert.True(result.Rights.SetForAttributesByDefault);
        Assert.False(result.Rights.IndependentRightsOfChildObjects);
        Assert.Equal(2, result.Rights.Objects.Count);

        var goods = result.Rights.Objects[0];
        Assert.Equal("Catalog.Товары", goods.Name);
        Assert.Equal("Catalog.Товары", goods.ObjectId);
        Assert.Equal(MdKind.Catalog, goods.Kind);
        Assert.True(goods.IsResolved);
        Assert.Equal(new[] { "Read", "Insert", "Delete" }, goods.Rights.Select(static right => right.Name));
        Assert.True(goods.Rights.Single(static right => right.Name == "Read").Value);
        Assert.False(goods.Rights.Single(static right => right.Name == "Delete").Value);
        Assert.Equal(2, goods.GrantedCount);
        Assert.Equal(1, goods.DeniedCount);

        // Условие RLS по умолчанию только отмечается: его текст читается по требованию.
        Assert.True(goods.HasRestriction);
        Assert.Null(goods.Condition);

        var order = result.Rights.Objects[1];
        Assert.Equal("Document.Заказ", order.ObjectId);
        Assert.True(order.Rights.Single(static right => right.Name == "Read").Value);
        Assert.False(order.Rights.Single(static right => right.Name == "InteractiveInsert").Value);
        Assert.False(order.HasRestriction);

        Assert.Equal(3, result.Rights.GrantedCount);
        Assert.Equal(2, result.Rights.DeniedCount);
        Assert.Equal(1, result.Rights.RestrictionCount);
    }

    [Fact]
    public void Текст_условия_RLS_читается_по_требованию()
    {
        var without = Read(RightsSampleDump.RightsXml, RightsSampleDump.RightsPath);
        Assert.Null(without.Rights.Objects[0].Condition);

        var with = Read(RightsSampleDump.RightsXml, RightsSampleDump.RightsPath, includeConditions: true);

        Assert.Equal(RightsSampleDump.Condition, with.Rights.Objects[0].Condition);
        Assert.Null(with.Rights.Objects[1].Condition);
    }

    [Fact]
    public void Незнакомые_узлы_пропускаются()
    {
        var result = Read(RightsSampleDump.RightsXml, RightsSampleDump.RightsPath);

        // Объект из незнакомого раздела не разбирается: поддерево пропускается целиком.
        Assert.DoesNotContain(result.Rights.Objects, static obj => obj.Name.Contains("Скрытый", StringComparison.Ordinal));
        Assert.Equal(2, result.Rights.Objects.Count);
    }

    [Fact]
    public void Битый_XML_даёт_предупреждение_и_разобранную_часть()
    {
        const string broken = """
            <?xml version="1.0" encoding="UTF-8"?>
            <Rights>
                <setForNewObjects>true</setForNewObjects>
                <object>
                    <name>Catalog.Товары</name>
                    <right>
                        <name>Read</name>
                        <value>true</value>
                    </right>
                </object>
            """;

        var result = Read(broken, RightsSampleDump.RightsPath);

        Assert.NotEmpty(result.Warnings);
        Assert.Contains(result.Warnings, static warning => warning.Contains("частично", StringComparison.Ordinal));
        Assert.True(result.Rights.SetForNewObjects);

        var goods = Assert.Single(result.Rights.Objects);
        Assert.Equal("Catalog.Товары", goods.ObjectId);
        Assert.True(goods.Rights.Single(static right => right.Name == "Read").Value);
    }

    [Fact]
    public void Пустой_файл_даёт_предупреждение_без_исключения()
    {
        var result = Read(string.Empty, RightsSampleDump.RightsPath);

        Assert.Empty(result.Rights.Objects);
        Assert.NotEmpty(result.Warnings);
    }

    [Fact]
    public void Имя_роли_и_признак_файла_прав_берутся_из_пути()
    {
        Assert.True(RightsDumpReader.IsRightsFile("Roles/Менеджер/Ext/Rights.xml"));
        Assert.False(RightsDumpReader.IsRightsFile("Roles/Менеджер.xml"));
        Assert.False(RightsDumpReader.IsRightsFile("Catalogs/Товары/Ext/Rights.xml"));

        Assert.Equal("Менеджер", RightsDumpReader.RoleNameFromPath("Roles/Менеджер/Ext/Rights.xml"));
        Assert.Equal("Менеджер", RightsDumpReader.RoleNameFromPath("Roles/Менеджер/Ext/Rights.xml".Replace('/', '\\')));
    }

    [Theory]
    [InlineData("Catalog.Товары", "Catalog.Товары")]
    [InlineData("Configuration", "Configuration")]
    [InlineData("Subsystem.Продажи.Subsystem.Розница", "Subsystem.Продажи/Subsystem.Розница")]
    [InlineData("AccumulationRegister.ОстаткиТоваров", "AccumulationRegister.ОстаткиТоваров")]
    [InlineData("Document.Заказ.TabularSection.Строки", "Document.Заказ/TabularSection.Строки")]
    public void Имя_объекта_в_правах_разрешается_в_идентификатор(string name, string expected)
    {
        Assert.True(RightsTargetResolver.TryResolve(name, out var id, out var kind));

        Assert.Equal(expected, id);
        Assert.False(kind.IsUnknown);
    }

    [Fact]
    public void Неразобранное_имя_объекта_не_разрешается()
    {
        Assert.False(RightsTargetResolver.TryResolve("Товары", out var id, out _));
        Assert.Empty(id);
        Assert.False(RightsTargetResolver.TryResolve("   ", out _, out _));
    }

    [Fact]
    public void Пустая_заглушка_ограничения_не_считается_RLS()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <Rights xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
                <object>
                    <name>Catalog.Товары</name>
                    <right>
                        <name>Read</name>
                        <value>true</value>
                    </right>
                    <restrictionByCondition xsi:nil="true"/>
                </object>
            </Rights>
            """;

        var result = Read(xml, RightsSampleDump.RightsPath, includeConditions: true);

        var goods = Assert.Single(result.Rights.Objects);
        Assert.False(goods.HasRestriction);
        Assert.Null(goods.Condition);
    }

    [Fact]
    public void Условие_внутри_права_тоже_помечает_RLS()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <Rights>
                <object>
                    <name>Catalog.Товары</name>
                    <right>
                        <name>Read</name>
                        <value>true</value>
                        <restrictionByCondition>
                            <condition>ГДЕ Организация = &amp;Организация</condition>
                        </restrictionByCondition>
                    </right>
                </object>
            </Rights>
            """;

        var result = Read(xml, RightsSampleDump.RightsPath, includeConditions: true);

        var goods = Assert.Single(result.Rights.Objects);
        Assert.True(goods.HasRestriction);
        Assert.Equal(RightsSampleDump.Condition, goods.Condition);
    }

    [Fact]
    public void Сжатая_запись_прав_разбирается_обратно_в_значения()
    {
        var rights = new[] { new RoleRightEntry("Read", true), new RoleRightEntry("Insert", false) };
        var detail = RightsDetail.Format(rights, hasRestriction: true);
        Assert.Equal("Read=true;Insert=false;RLS", detail);

        var parsed = RightsDetail.Parse(detail);
        Assert.Equal(new[] { "Read", "Insert" }, parsed.Select(static right => right.Name));
        Assert.True(parsed[0].Value);
        Assert.False(parsed[1].Value);

        // Без ограничения метки в detail нет, а пустая запись разбирается в пустой список.
        Assert.Equal("Read=true", RightsDetail.Format([new RoleRightEntry("Read", true)], hasRestriction: false));
        Assert.Empty(RightsDetail.Parse("RLS"));
        Assert.Empty(RightsDetail.Parse(string.Empty));
    }

    private static RightsReadResult Read(string xml, string path, bool includeConditions = false)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        return new RightsDumpReader().Read(stream, path, includeConditions: includeConditions);
    }
}
