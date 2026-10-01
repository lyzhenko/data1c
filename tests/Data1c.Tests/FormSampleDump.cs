using Data1c.Core.Dump;

namespace Data1c.Tests;

/// <summary>
/// Синтетическая выгрузка для проверок форм: справочник с формой, у которой два реквизита
/// (один основной), элементы с DataPath, команда и обработчики событий, и модуль формы.
/// Собирается отдельно от <see cref="SampleDump"/>: на общей выгрузке висят точные проверки
/// содержимого индекса, и менять её нельзя.
/// </summary>
internal static class FormSampleDump
{
    internal const string ConfigurationPath = "Configuration.xml";
    internal const string CatalogPath = "Catalogs/Товары.xml";
    internal const string FormMetadataPath = "Catalogs/Товары/Forms/ФормаЭлемента.xml";
    internal const string FormPath = "Catalogs/Товары/Forms/ФормаЭлемента/Ext/Form.xml";
    internal const string FormModulePath = "Catalogs/Товары/Forms/ФормаЭлемента/Ext/Form/Module.bsl";
    internal const string FormId = "Catalog.Товары/Form.ФормаЭлемента";

    internal const string FormModuleBsl = """
        &НаКлиенте
        Процедура ПриОткрытии(Отказ)
        	Сообщить("Открытие");
        КонецПроцедуры

        &НаКлиенте
        Процедура АртикулПриИзменении(Элемент)
        	Сообщить("Изменение");
        КонецПроцедуры

        &НаКлиенте
        Процедура Печать(Команда)
        	Сообщить("Печать");
        КонецПроцедуры
        """;

    /// <summary>Описание управляемой формы: структура узлов взята с реальной выгрузки 1С.</summary>
    internal const string FormXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <Form xmlns="http://v8.1c.ru/8.3/xcf/logform" xmlns:cfg="http://v8.1c.ru/8.1/data/enterprise/current-config" xmlns:v8="http://v8.1c.ru/8.1/data/core" version="2.18">
        	<AutoCommandBar name="ФормаКоманднаяПанель" id="-1">
        		<ChildItems>
        			<Button name="КнопкаПечать" id="10">
        				<Type>CommandBarButton</Type>
        				<CommandName>Form.Command.Печать</CommandName>
        			</Button>
        		</ChildItems>
        	</AutoCommandBar>
        	<Events>
        		<Event name="OnOpen">ПриОткрытии</Event>
        		<Event name="OnCreateAtServer">НетТакойПроцедуры</Event>
        	</Events>
        	<ChildItems>
        		<InputField name="Артикул" id="1">
        			<DataPath>Объект.Артикул</DataPath>
        			<Events>
        				<Event name="OnChange">АртикулПриИзменении</Event>
        			</Events>
        			<ContextMenu name="АртикулКонтекстноеМеню" id="2"/>
        			<ExtendedTooltip name="АртикулРасширеннаяПодсказка" id="3"/>
        		</InputField>
        		<Table name="Товары" id="4">
        			<DataPath>Объект.Товары</DataPath>
        			<ChildItems>
        				<InputField name="ТоварыЦена" id="5">
        					<DataPath>Объект.Товары.Цена</DataPath>
        				</InputField>
        			</ChildItems>
        		</Table>
        	</ChildItems>
        	<Attributes>
        		<Attribute name="Объект" id="1">
        			<Type>
        				<v8:Type>cfg:CatalogObject.Товары</v8:Type>
        			</Type>
        			<MainAttribute>true</MainAttribute>
        			<SavedData>true</SavedData>
        		</Attribute>
        		<Attribute name="Комментарий" id="2">
        			<Type>
        				<v8:Type>xs:string</v8:Type>
        				<v8:StringQualifiers>
        					<v8:Length>0</v8:Length>
        				</v8:StringQualifiers>
        			</Type>
        		</Attribute>
        	</Attributes>
        	<Commands>
        		<Command name="Печать" id="1">
        			<Action>Печать</Action>
        		</Command>
        	</Commands>
        </Form>
        """;

    /// <summary>Собирает выгрузку в памяти. Без модуля формы обработчики остаются несвязанными.</summary>
    internal static InMemoryDumpSource Create(bool withFormModule = true)
    {
        var source = new InMemoryDumpSource("выгрузка с формой");
        source.AddText(ConfigurationPath, ConfigurationXml);
        source.AddText(CatalogPath, CatalogXml);
        source.AddText(FormMetadataPath, FormMetadataXml);
        source.AddText(FormPath, FormXml);
        if (withFormModule)
        {
            source.AddText(FormModulePath, FormModuleBsl);
        }

        return source;
    }

    /// <summary>Раскладывает выгрузку по диску: нужна проверкам режима индекса.</summary>
    internal static string Materialize()
    {
        var root = Path.Combine(Path.GetTempPath(), "data1c-form-dump-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var source = Create();
        foreach (var file in source.EnumerateFiles())
        {
            var path = Path.Combine(root, file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var content = source.OpenRead(file);
            using var target = File.Create(path);
            content.CopyTo(target);
        }

        return root;
    }

    /// <summary>Убирает временный каталог; неудача очистки проверку не ломает.</summary>
    internal static void Remove(string root)
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Каталог останется — на результат проверки это не влияет.
        }
    }

    /// <summary>Корневой файл конфигурации: используется и проверками, собирающими выгрузку вручную.</summary>
    internal const string ConfigurationXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <MetaDataObject xmlns="http://v8.1c.ru/8.3/MDClasses" xmlns:v8="http://v8.1c.ru/8.1/data/core" version="2.20">
        	<Configuration uuid="1f0f0f0f-0000-0000-0000-000000000001">
        		<Properties>
        			<Name>КонфигурацияСФормой</Name>
        		</Properties>
        		<ChildObjects>
        			<Catalog>Товары</Catalog>
        		</ChildObjects>
        	</Configuration>
        </MetaDataObject>
        """;

    /// <summary>Описание справочника: реквизит и форма.</summary>
    internal const string CatalogXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <MetaDataObject xmlns="http://v8.1c.ru/8.3/MDClasses" xmlns:v8="http://v8.1c.ru/8.1/data/core" xmlns:cfg="http://v8.1c.ru/8.1/data/enterprise/current-config" version="2.20">
        	<Catalog uuid="11111111-1111-1111-1111-111111111111">
        		<Properties>
        			<Name>Товары</Name>
        		</Properties>
        		<ChildObjects>
        			<Attribute uuid="22222222-2222-2222-2222-222222222222">
        				<Properties>
        					<Name>Артикул</Name>
        					<Type>
        						<v8:Type>xs:string</v8:Type>
        					</Type>
        				</Properties>
        			</Attribute>
        			<Form>ФормаЭлемента</Form>
        		</ChildObjects>
        	</Catalog>
        </MetaDataObject>
        """;

    /// <summary>Объект метаданных формы: из его свойств берётся имя и вид формы.</summary>
    internal const string FormMetadataXml = """
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
}
