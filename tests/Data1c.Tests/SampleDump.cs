using Data1c.Core.Dump;

namespace Data1c.Tests;

/// <summary>Небольшая синтетическая выгрузка 1С для тестов: два общих модуля, справочник с реквизитами и формой, подсистема.</summary>
internal static class SampleDump
{
    public const string ConfigurationPath = "Configuration.xml";
    public const string CatalogPath = "Catalogs/Товары.xml";
    public const string FormPath = "Catalogs/Товары/Forms/ФормаЭлемента.xml";
    public const string CommonModulePath = "CommonModules/ОбщегоНазначения.xml";
    public const string SecondCommonModulePath = "CommonModules/РаботаСДанными.xml";
    public const string SubsystemPath = "Subsystems/Продажи.xml";
    public const string CommonModuleBslPath = "CommonModules/ОбщегоНазначения/Ext/Module.bsl";
    public const string SecondCommonModuleBslPath = "CommonModules/РаботаСДанными/Ext/Module.bsl";
    public const string FormModuleBslPath = "Catalogs/Товары/Forms/ФормаЭлемента/Ext/Form/Module.bsl";

    public const string CommonModuleBsl = """
        Процедура МояПроцедура() Экспорт
            РаботаСДанными.ЗагрузитьДанные();
            ДругаяПроцедура();
        КонецПроцедуры

        Процедура ДругаяПроцедура()
            Справочники.Товары.НайтиПоНаименованию("Тест");
        КонецПроцедуры
        """;

    public const string SecondCommonModuleBsl = """
        Процедура ЗагрузитьДанные() Экспорт
            Сообщить("Загрузка");
        КонецПроцедуры
        """;

    public static InMemoryDumpSource Create()
    {
        var source = new InMemoryDumpSource("тестовая выгрузка");
        source.AddText(ConfigurationPath, ConfigurationXml);
        source.AddText(CatalogPath, CatalogXml);
        source.AddText(FormPath, FormXml);
        source.AddText(CommonModulePath, CommonModuleXml);
        source.AddText(SecondCommonModulePath, SecondCommonModuleXml);
        source.AddText(SubsystemPath, SubsystemXml);
        source.AddText(CommonModuleBslPath, CommonModuleBsl);
        source.AddText(SecondCommonModuleBslPath, SecondCommonModuleBsl);
        source.AddText(FormModuleBslPath, FormModuleBsl);
        return source;
    }

    private const string FormModuleBsl = """
        &НаКлиенте
        Процедура ПриОткрытии(Отказ)
            Сообщить("Открытие");
        КонецПроцедуры
        """;

    private const string ConfigurationXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <MetaDataObject xmlns="http://v8.1c.ru/8.3/MDClasses" xmlns:v8="http://v8.1c.ru/8.1/data/core" xmlns:cfg="http://v8.1c.ru/8.1/data/enterprise/current-config" xmlns:xr="http://v8.1c.ru/8.3/xcf/readable" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" version="2.20">
            <Configuration uuid="0f0f0f0f-0000-0000-0000-000000000001">
                <Properties>
                    <Name>ТестоваяКонфигурация</Name>
                    <Synonym>
                        <v8:item>
                            <v8:lang>ru</v8:lang>
                            <v8:content>Тестовая конфигурация</v8:content>
                        </v8:item>
                    </Synonym>
                    <Comment/>
                    <DefaultRunMode>ManagedApplication</DefaultRunMode>
                </Properties>
                <ChildObjects>
                    <Catalog>Товары</Catalog>
                    <CommonModule>ОбщегоНазначения</CommonModule>
                    <CommonModule>РаботаСДанными</CommonModule>
                    <Subsystem>Продажи</Subsystem>
                </ChildObjects>
            </Configuration>
        </MetaDataObject>
        """;

    private const string CatalogXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <MetaDataObject xmlns="http://v8.1c.ru/8.3/MDClasses" xmlns:v8="http://v8.1c.ru/8.1/data/core" xmlns:cfg="http://v8.1c.ru/8.1/data/enterprise/current-config" xmlns:xr="http://v8.1c.ru/8.3/xcf/readable" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" version="2.20">
            <Catalog uuid="11111111-1111-1111-1111-111111111111">
                <Properties>
                    <Name>Товары</Name>
                    <Synonym>
                        <v8:item>
                            <v8:lang>ru</v8:lang>
                            <v8:content>Номенклатура</v8:content>
                        </v8:item>
                    </Synonym>
                    <CodeLength>9</CodeLength>
                    <DefaultObjectForm>Catalog.Товары.Form.ФормаЭлемента</DefaultObjectForm>
                </Properties>
                <ChildObjects>
                    <Attribute uuid="22222222-2222-2222-2222-222222222222">
                        <Properties>
                            <Name>Артикул</Name>
                            <Type>
                                <v8:Type>xs:string</v8:Type>
                                <v8:StringQualifiers>
                                    <v8:Length>25</v8:Length>
                                </v8:StringQualifiers>
                            </Type>
                        </Properties>
                    </Attribute>
                    <Attribute uuid="33333333-3333-3333-3333-333333333333">
                        <Properties>
                            <Name>Единица</Name>
                            <Type>
                                <v8:Type>cfg:CatalogRef.ЕдиницыИзмерения</v8:Type>
                            </Type>
                        </Properties>
                    </Attribute>
                    <Form>ФормаЭлемента</Form>
                    <Command uuid="44444444-4444-4444-4444-444444444444">
                        <Properties>
                            <Name>Печать</Name>
                        </Properties>
                    </Command>
                </ChildObjects>
            </Catalog>
        </MetaDataObject>
        """;

    private const string FormXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <MetaDataObject xmlns="http://v8.1c.ru/8.3/MDClasses" xmlns:v8="http://v8.1c.ru/8.1/data/core" version="2.20">
            <Form uuid="55555555-5555-5555-5555-555555555555">
                <Properties>
                    <Name>ФормаЭлемента</Name>
                    <FormType>Managed</FormType>
                </Properties>
            </Form>
        </MetaDataObject>
        """;

    private const string CommonModuleXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <MetaDataObject xmlns="http://v8.1c.ru/8.3/MDClasses" xmlns:v8="http://v8.1c.ru/8.1/data/core" version="2.20">
            <CommonModule uuid="66666666-6666-6666-6666-666666666666">
                <Properties>
                    <Name>ОбщегоНазначения</Name>
                    <Global>false</Global>
                    <Server>true</Server>
                    <ClientManagedApplication>true</ClientManagedApplication>
                </Properties>
            </CommonModule>
        </MetaDataObject>
        """;

    private const string SecondCommonModuleXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <MetaDataObject xmlns="http://v8.1c.ru/8.3/MDClasses" xmlns:v8="http://v8.1c.ru/8.1/data/core" version="2.20">
            <CommonModule uuid="77777777-7777-7777-7777-777777777777">
                <Properties>
                    <Name>РаботаСДанными</Name>
                    <Server>true</Server>
                </Properties>
            </CommonModule>
        </MetaDataObject>
        """;

    private const string SubsystemXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <MetaDataObject xmlns="http://v8.1c.ru/8.3/MDClasses" xmlns:v8="http://v8.1c.ru/8.1/data/core" xmlns:xr="http://v8.1c.ru/8.3/xcf/readable" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" version="2.20">
            <Subsystem uuid="88888888-8888-8888-8888-888888888888">
                <Properties>
                    <Name>Продажи</Name>
                    <IncludeInCommandInterface>true</IncludeInCommandInterface>
                    <Content>
                        <xr:Item xsi:type="xr:MDObjectRef">Catalog.Товары</xr:Item>
                        <xr:Item xsi:type="xr:MDObjectRef">CommonModule.ОбщегоНазначения</xr:Item>
                    </Content>
                </Properties>
            </Subsystem>
        </MetaDataObject>
        """;
}
