using Data1c.Core.Dump;

namespace Data1c.Tests;

/// <summary>
/// Синтетическая выгрузка с правами ролей: две роли, объект с правами true/false и условием RLS,
/// второй объект без ограничения и объект, который в правах не упомянут вовсе.
/// </summary>
/// <remarks>
/// Общий <see cref="SampleDump"/> намеренно не меняется: правам нужен собственный набор файлов
/// <c>Roles/&lt;Имя&gt;/Ext/Rights.xml</c>, а проверки хотят видеть RLS и роль с одним объектом.
/// </remarks>
internal static class RightsSampleDump
{
    public const string ConfigurationPath = "Configuration.xml";
    public const string CatalogPath = "Catalogs/Товары.xml";
    public const string UnusedCatalogPath = "Catalogs/ЕдиницыИзмерения.xml";
    public const string DocumentPath = "Documents/Заказ.xml";
    public const string RolePath = "Roles/Менеджер.xml";
    public const string FullRightsRolePath = "Roles/ПолныеПрава.xml";
    public const string RightsPath = "Roles/Менеджер/Ext/Rights.xml";
    public const string FullRightsPath = "Roles/ПолныеПрава/Ext/Rights.xml";

    /// <summary>Текст условия RLS роли «Менеджер» на справочник «Товары».</summary>
    public const string Condition = "ГДЕ Организация = &Организация";

    public static InMemoryDumpSource Create()
    {
        var source = new InMemoryDumpSource("выгрузка с правами ролей");
        source.AddText(ConfigurationPath, ConfigurationXml);
        source.AddText(CatalogPath, CatalogXml);
        source.AddText(UnusedCatalogPath, "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n"
            + "<MetaDataObject><Catalog uuid=\"99999999-9999-9999-9999-999999999999\">"
            + "<Properties><Name>ЕдиницыИзмерения</Name></Properties></Catalog></MetaDataObject>\n");
        source.AddText(DocumentPath, "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n"
            + "<MetaDataObject><Document uuid=\"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa\">"
            + "<Properties><Name>Заказ</Name></Properties></Document></MetaDataObject>\n");
        source.AddText(RolePath, RoleXml("Менеджер", "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"));
        source.AddText(FullRightsRolePath, RoleXml("ПолныеПрава", "cccccccc-cccc-cccc-cccc-cccccccccccc"));
        source.AddText(RightsPath, RightsXml);
        source.AddText(FullRightsPath, FullRightsXml);
        return source;
    }

    /// <summary>Права роли «Менеджер»: два объекта, права true/false, условие RLS и незнакомые узлы.</summary>
    public const string RightsXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <Rights xmlns="http://v8.1c.ru/8.2/roles">
            <setForNewObjects>false</setForNewObjects>
            <setForAttributesByDefault>true</setForAttributesByDefault>
            <independentRightsOfChildObjects>false</independentRightsOfChildObjects>
            <unknownSection>
                <object>
                    <name>Catalog.Скрытый</name>
                    <right>
                        <name>Read</name>
                        <value>true</value>
                    </right>
                </object>
            </unknownSection>
            <object>
                <name>Catalog.Товары</name>
                <right>
                    <name>Read</name>
                    <value>true</value>
                </right>
                <right>
                    <name>Insert</name>
                    <value>true</value>
                    <comment>незнакомый узел внутри права</comment>
                </right>
                <right>
                    <name>Delete</name>
                    <value>false</value>
                </right>
                <restrictionByCondition>
                    <condition>ГДЕ Организация = &amp;Организация</condition>
                </restrictionByCondition>
            </object>
            <object>
                <name>Document.Заказ</name>
                <right>
                    <name>Read</name>
                    <value>true</value>
                </right>
                <right>
                    <name>InteractiveInsert</name>
                    <value>false</value>
                </right>
            </object>
        </Rights>
        """;

    /// <summary>Права роли «ПолныеПрава»: объект «Товары» и права на конфигурацию целиком.</summary>
    public const string FullRightsXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <Rights xmlns="http://v8.1c.ru/8.2/roles">
            <setForNewObjects>true</setForNewObjects>
            <setForAttributesByDefault>true</setForAttributesByDefault>
            <independentRightsOfChildObjects>true</independentRightsOfChildObjects>
            <object>
                <name>Catalog.Товары</name>
                <right>
                    <name>Read</name>
                    <value>true</value>
                </right>
            </object>
            <object>
                <name>Configuration</name>
                <right>
                    <name>Administration</name>
                    <value>true</value>
                </right>
            </object>
        </Rights>
        """;

    private const string ConfigurationXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <MetaDataObject xmlns="http://v8.1c.ru/8.3/MDClasses" version="2.20">
            <Configuration uuid="0f0f0f0f-0000-0000-0000-000000000002">
                <Properties>
                    <Name>ПраваТест</Name>
                </Properties>
                <ChildObjects>
                    <Catalog>Товары</Catalog>
                    <Catalog>ЕдиницыИзмерения</Catalog>
                    <Document>Заказ</Document>
                    <Role>Менеджер</Role>
                    <Role>ПолныеПрава</Role>
                </ChildObjects>
            </Configuration>
        </MetaDataObject>
        """;

    private const string CatalogXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <MetaDataObject xmlns="http://v8.1c.ru/8.3/MDClasses" version="2.20">
            <Catalog uuid="11111111-2222-3333-4444-555555555555">
                <Properties>
                    <Name>Товары</Name>
                    <Synonym>
                        <v8:item xmlns:v8="http://v8.1c.ru/8.1/data/core">
                            <v8:lang>ru</v8:lang>
                            <v8:content>Номенклатура</v8:content>
                        </v8:item>
                    </Synonym>
                </Properties>
            </Catalog>
        </MetaDataObject>
        """;

    private static string RoleXml(string name, string uuid) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <MetaDataObject xmlns="http://v8.1c.ru/8.3/MDClasses" version="2.20">
            <Role uuid="{uuid}">
                <Properties>
                    <Name>{name}</Name>
                </Properties>
            </Role>
        </MetaDataObject>
        """;
}
