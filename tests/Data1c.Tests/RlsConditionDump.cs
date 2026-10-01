using System.Security;
using Data1c.Core.Dump;

namespace Data1c.Tests;

/// <summary>
/// Синтетическая выгрузка с текстами условий RLS: три роли, у двух есть ограничения доступа
/// к данным, а у третьей — нет. Одно из условий намеренно длинное (больше предела ответа
/// инструмента), чтобы проверять, что в индекс текст пишется целиком.
/// </summary>
/// <remarks>
/// Общий <see cref="SampleDump"/> и <see cref="RightsSampleDump"/> не меняются: этим проверкам нужен
/// свой набор условий, а правка готовых образцов сдвинула бы счётчики их собственных проверок.
/// </remarks>
internal static class RlsConditionDump
{
    public const string RightsPath = "Roles/Менеджер/Ext/Rights.xml";
    public const string ObserverRightsPath = "Roles/Наблюдатель/Ext/Rights.xml";
    public const string FullRightsPath = "Roles/ПолныеПрава/Ext/Rights.xml";

    /// <summary>Условие RLS роли «Менеджер» на справочник «Товары».</summary>
    public const string GoodsCondition = "ГДЕ Организация = &Организация";

    /// <summary>Условие RLS роли «Наблюдатель» на документ «Заказ».</summary>
    public const string OrderCondition = "ГДЕ Ответственный = &ТекущийПользователь";

    /// <summary>
    /// Имя объекта прав, которое не разрешается в идентификатор метаданных: такие строки в индекс
    /// не пишутся, и условие для них инструмент читает из файла роли.
    /// </summary>
    public const string UnresolvedName = "НеизвестныйВид.СекретныйОбъект";

    /// <summary>Условие RLS неразрешённого объекта: в индекс не попадает.</summary>
    public const string UnresolvedCondition = "ГДЕ Секрет = &Секрет";

    /// <summary>
    /// Длинное условие RLS роли «Менеджер» на справочник «Склады»: содержит то же слово «Организация»,
    /// поэтому поиск по подстроке находит оба объекта роли.
    /// </summary>
    public static readonly string LongCondition = BuildLongCondition();

    public static InMemoryDumpSource Create()
    {
        var source = new InMemoryDumpSource("выгрузка с условиями RLS");
        source.AddText("Configuration.xml", ConfigurationXml);
        source.AddText("Catalogs/Товары.xml", ObjectXml("Catalog", "Товары", "11111111-2222-3333-4444-555555555555"));
        source.AddText("Catalogs/Склады.xml", ObjectXml("Catalog", "Склады", "22222222-2222-3333-4444-555555555555"));
        source.AddText("Documents/Заказ.xml", ObjectXml("Document", "Заказ", "33333333-2222-3333-4444-555555555555"));
        source.AddText("Roles/Менеджер.xml", RoleXml("Менеджер", "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"));
        source.AddText("Roles/Наблюдатель.xml", RoleXml("Наблюдатель", "dddddddd-dddd-dddd-dddd-dddddddddddd"));
        source.AddText("Roles/ПолныеПрава.xml", RoleXml("ПолныеПрава", "cccccccc-cccc-cccc-cccc-cccccccccccc"));
        source.AddText(RightsPath, ManagerRightsXml);
        source.AddText(ObserverRightsPath, ObserverRightsXml);
        source.AddText(FullRightsPath, FullRightsXml);
        return source;
    }

    /// <summary>Права «Менеджера»: два объекта с условиями, длинное условие — у второго.</summary>
    public static string ManagerRightsXml => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <Rights xmlns="http://v8.1c.ru/8.2/roles">
            <setForNewObjects>false</setForNewObjects>
            <setForAttributesByDefault>true</setForAttributesByDefault>
            <independentRightsOfChildObjects>false</independentRightsOfChildObjects>
            <object>
                <name>Catalog.Товары</name>
                <right>
                    <name>Read</name>
                    <value>true</value>
                </right>
                <right>
                    <name>Delete</name>
                    <value>false</value>
                </right>
                <restrictionByCondition>
                    <condition>{Escape(GoodsCondition)}</condition>
                </restrictionByCondition>
            </object>
            <object>
                <name>Catalog.Склады</name>
                <right>
                    <name>Read</name>
                    <value>true</value>
                </right>
                <restrictionByCondition>
                    <condition>{Escape(LongCondition)}</condition>
                </restrictionByCondition>
            </object>
            <object>
                <name>Document.Заказ</name>
                <right>
                    <name>Read</name>
                    <value>true</value>
                </right>
            </object>
            <object>
                <name>{UnresolvedName}</name>
                <right>
                    <name>Read</name>
                    <value>true</value>
                </right>
                <restrictionByCondition>
                    <condition>{Escape(UnresolvedCondition)}</condition>
                </restrictionByCondition>
            </object>
        </Rights>
        """;

    /// <summary>Права «Наблюдателя»: одно условие на документ.</summary>
    public static string ObserverRightsXml => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <Rights xmlns="http://v8.1c.ru/8.2/roles">
            <object>
                <name>Document.Заказ</name>
                <right>
                    <name>Read</name>
                    <value>true</value>
                </right>
                <restrictionByCondition>
                    <condition>{Escape(OrderCondition)}</condition>
                </restrictionByCondition>
            </object>
        </Rights>
        """;

    /// <summary>Права «ПолныхПрав»: права на объект без ограничения доступа к данным.</summary>
    public static string FullRightsXml => """
        <?xml version="1.0" encoding="UTF-8"?>
        <Rights xmlns="http://v8.1c.ru/8.2/roles">
            <object>
                <name>Catalog.Товары</name>
                <right>
                    <name>Read</name>
                    <value>true</value>
                </right>
            </object>
        </Rights>
        """;

    /// <summary>Условие длиной больше 4 000 символов: предел в ответе инструмента, но не в индексе.</summary>
    private static string BuildLongCondition()
    {
        var text = "ГДЕ Организация = &Организация И ";
        for (var index = 0; index < 200; index++)
        {
            text += "Склад.Организация = &Организация И ";
        }

        return text + "ИСТИНА";
    }

    /// <summary>Текст условия попадает в XML как есть: амперсанды экранируются.</summary>
    private static string Escape(string condition) => SecurityElement.Escape(condition) ?? condition;

    private const string ConfigurationXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <MetaDataObject xmlns="http://v8.1c.ru/8.3/MDClasses" version="2.20">
            <Configuration uuid="0f0f0f0f-0000-0000-0000-000000000003">
                <Properties>
                    <Name>УсловияRLS</Name>
                </Properties>
                <ChildObjects>
                    <Catalog>Товары</Catalog>
                    <Catalog>Склады</Catalog>
                    <Document>Заказ</Document>
                    <Role>Менеджер</Role>
                    <Role>Наблюдатель</Role>
                    <Role>ПолныеПрава</Role>
                </ChildObjects>
            </Configuration>
        </MetaDataObject>
        """;

    private static string ObjectXml(string kind, string name, string uuid) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <MetaDataObject xmlns="http://v8.1c.ru/8.3/MDClasses" version="2.20">
            <{kind} uuid="{uuid}">
                <Properties>
                    <Name>{name}</Name>
                </Properties>
            </{kind}>
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
