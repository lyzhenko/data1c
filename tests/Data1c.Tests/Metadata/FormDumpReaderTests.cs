using System.Diagnostics;
using System.Globalization;
using System.Text;
using Data1c.Core.Analysis;
using Data1c.Core.Dump;
using Data1c.Core.Metadata;
using Xunit;

namespace Data1c.Tests.Metadata;

/// <summary>
/// Разбор описания формы (<c>Ext/Form.xml</c>) и его связь с конфигурацией: реквизиты, элементы
/// с привязкой по DataPath, команды, обработчики событий и процедуры модуля формы. Синтетическая
/// выгрузка собирается в <see cref="FormSampleDump"/>, общий <see cref="SampleDump"/> не затрагивается.
/// </summary>
public sealed class FormDumpReaderTests
{
    [Fact]
    public void Читает_реквизиты_элементы_команды_и_обработчики()
    {
        var result = ReadForm();

        Assert.Empty(result.Warnings);

        var form = result.Form;
        Assert.Equal("ФормаЭлемента", form.Name);
        Assert.Equal(FormKind.Managed, form.Kind);
        Assert.Equal(FormSampleDump.FormPath, form.SourcePath);

        // Реквизиты: у основного — тип-ссылка и признак, у обычного — тип платформы.
        Assert.Equal(2, form.Attributes.Count);

        var main = form.FindAttribute("Объект");
        Assert.NotNull(main);
        Assert.True(main.IsMain);
        Assert.Equal(["Catalog.Товары"], main.Types);

        var comment = form.FindAttribute("Комментарий");
        Assert.NotNull(comment);
        Assert.False(comment.IsMain);
        Assert.Equal(["xs:string"], comment.Types);

        // Элементы: имя — атрибут, вид — имя узла, вложенность — через ChildItems.
        Assert.Contains(form.Elements, static element => element is { Name: "Артикул", Kind: "InputField" });
        Assert.Contains(form.Elements, static element => element is { Name: "Товары", Kind: "Table" });
        Assert.Contains(form.Elements, static element => element is { Name: "ТоварыЦена", DataPath: "Объект.Товары.Цена" });
        Assert.Contains(form.Elements, static element => element is { Name: "КнопкаПечать", Kind: "Button", CommandName: "Form.Command.Печать" });

        // Команда формы и её обработчик.
        var command = Assert.Single(form.Commands);
        Assert.Equal("Печать", command.Name);
        Assert.Equal("Печать", command.Handler);

        // Обработчики: событие формы и событие элемента.
        Assert.Equal(3, form.Handlers.Count);
        Assert.Contains(form.Handlers, static handler => handler is { Event: "OnOpen", Element: null, Procedure: "ПриОткрытии" });
        Assert.Contains(form.Handlers, static handler => handler is { Event: "OnChange", Element: "Артикул", Procedure: "АртикулПриИзменении" });
        Assert.Contains(form.Handlers, static handler => handler is { Event: "OnCreateAtServer", Procedure: "НетТакойПроцедуры" });
    }

    [Fact]
    public void Связывает_элемент_с_реквизитом_по_DataPath()
    {
        var form = ReadForm().Form;

        // «Объект.Артикул» и «Объект.Товары.Цена» ведут к основному реквизиту «Объект».
        Assert.Equal("Объект", form.Elements.Single(static element => element.Name == "Артикул").Attribute);
        Assert.Equal("Объект", form.Elements.Single(static element => element.Name == "Товары").Attribute);
        Assert.Equal("Объект", form.Elements.Single(static element => element.Name == "ТоварыЦена").Attribute);

        // У кнопки командной панели пути данных нет — привязки тоже.
        Assert.Null(form.Elements.Single(static element => element.Name == "КнопкаПечать").Attribute);
    }

    [Fact]
    public void Служебные_узлы_элементами_не_считаются()
    {
        var form = ReadForm().Form;
        var names = form.Elements.Select(static element => element.Name).ToList();

        Assert.DoesNotContain("АртикулКонтекстноеМеню", names);
        Assert.DoesNotContain("АртикулРасширеннаяПодсказка", names);
        Assert.DoesNotContain("ФормаКоманднаяПанель", names);
        Assert.DoesNotContain("OnOpen", names);
    }

    [Fact]
    public void Определяет_вид_формы_по_пространству_имён()
    {
        var ordinary = FormSampleDump.FormXml.Replace(
            "http://v8.1c.ru/8.3/xcf/logform",
            "http://v8.1c.ru/8.1/data/ui/form",
            StringComparison.Ordinal);

        Assert.Equal(FormKind.Ordinary, ReadForm(ordinary).Form.Kind);
        Assert.Equal(FormKind.Unknown, ReadForm("<Form xmlns=\"http://example.org/form\"/>").Form.Kind);
    }

    [Fact]
    public void Вид_формы_берётся_из_свойств_объекта_метаданных()
    {
        // Пространство имён ничего не говорит о виде формы: остаются свойства объекта метаданных.
        var source = FormSampleDump.Create();
        source.AddText(
            FormSampleDump.FormPath,
            FormSampleDump.FormXml.Replace("http://v8.1c.ru/8.3/xcf/logform", "http://example.org/form", StringComparison.Ordinal));

        var result = new MetadataDumpReader().Read(source);
        var form = result.Model.Find(FormSampleDump.FormId);

        Assert.NotNull(form);
        Assert.NotNull(form.Form);
        Assert.Equal(FormKind.Managed, form.Form.Kind);
    }

    [Fact]
    public void Форма_без_ожидаемых_узлов_не_роняет_разбор()
    {
        var result = ReadForm("""
            <?xml version="1.0" encoding="UTF-8"?>
            <Form xmlns="http://v8.1c.ru/8.3/xcf/logform" version="2.18">
            	<Name>ПустаяФорма</Name>
            </Form>
            """);

        Assert.Empty(result.Warnings);
        Assert.True(result.Form.IsEmpty);
        Assert.Equal("ПустаяФорма", result.Form.Name);
    }

    [Fact]
    public void Незнакомый_корневой_узел_даёт_предупреждение_а_не_исключение()
    {
        var result = ReadForm("""<?xml version="1.0" encoding="UTF-8"?><НеФорма><Items/></НеФорма>""");

        Assert.True(result.Form.IsEmpty);
        Assert.Contains(result.Warnings, static warning => warning.Contains("не похож на описание формы", StringComparison.Ordinal));
    }

    [Fact]
    public void Битый_xml_даёт_предупреждение_и_сохраняет_прочитанное()
    {
        // Файл обрывается на втором реквизите: разбор должен вернуть предупреждение,
        // а прочитанное до обрыва — остаться в модели.
        var result = ReadForm("""
            <?xml version="1.0" encoding="UTF-8"?>
            <Form xmlns="http://v8.1c.ru/8.3/xcf/logform" version="2.18">
            	<Attributes>
            		<Attribute name="Объект">
            			<Type>
            				<v8:Type xmlns:v8="http://v8.1c.ru/8.1/data/core">xs:string</v8:Type>
            			</Type>
            			<MainAttribute>true</MainAttribute>
            		</Attribute>
            		<Attribute name="Комментарий">
            			<Type>
            """);

        Assert.NotEmpty(result.Warnings);
        Assert.Contains(result.Warnings, static warning => warning.Contains(FormSampleDump.FormPath, StringComparison.Ordinal));

        var attribute = Assert.Single(result.Form.Attributes);
        Assert.Equal("Объект", attribute.Name);
        Assert.True(attribute.IsMain);
        Assert.Equal(["xs:string"], attribute.Types);
    }

    [Fact]
    public void Пустой_файл_даёт_предупреждение()
    {
        var result = ReadForm(string.Empty);

        Assert.True(result.Form.IsEmpty);
        Assert.NotEmpty(result.Warnings);
        Assert.Contains(result.Warnings, static warning => warning.Contains(FormSampleDump.FormPath, StringComparison.Ordinal));
    }

    [Fact]
    public void Разбор_выгрузки_привязывает_форму_к_объекту_и_обработчики_к_модулю()
    {
        var result = new DumpAnalyzer().Analyze(FormSampleDump.Create());

        Assert.Empty(result.Warnings);

        var owner = result.Metadata.Find(FormSampleDump.FormId);
        Assert.NotNull(owner);
        Assert.Equal(MdKind.Form, owner.Kind);
        Assert.Equal("Catalog.Товары", owner.Parent?.Id);

        // Модуль формы уже привязан к форме как модуль её объекта.
        Assert.Contains(owner.Modules, static module => module.RelativePath == FormSampleDump.FormModulePath);

        var form = owner.Form;
        Assert.NotNull(form);
        Assert.Equal("ФормаЭлемента", form.Name);
        Assert.Equal(3, form.Handlers.Count);

        var open = form.Handlers.Single(static handler => handler.Procedure == "ПриОткрытии");
        Assert.True(open.Resolved);
        Assert.Equal(2, open.Line);

        var changed = form.Handlers.Single(static handler => handler.Procedure == "АртикулПриИзменении");
        Assert.True(changed.Resolved);
        Assert.Equal(7, changed.Line);

        // Процедуры, которой в модуле нет, обработчик не находит: строка остаётся неизвестной.
        var missing = form.Handlers.Single(static handler => handler.Procedure == "НетТакойПроцедуры");
        Assert.False(missing.Resolved);
        Assert.Null(missing.Line);
    }

    [Fact]
    public void Без_модуля_формы_обработчики_остаются_несвязанными()
    {
        var result = new DumpAnalyzer().Analyze(FormSampleDump.Create(withFormModule: false));
        var form = result.Metadata.Find(FormSampleDump.FormId)?.Form;

        Assert.NotNull(form);
        Assert.Equal(3, form.Handlers.Count);
        Assert.Empty(form.ResolvedHandlers);
        Assert.All(form.Handlers, static handler => Assert.Null(handler.Line));
    }

    [Fact]
    public void Битый_файл_формы_не_роняет_разбор_выгрузки()
    {
        var source = FormSampleDump.Create();
        source.AddText(FormSampleDump.FormPath, FormSampleDump.FormXml[..(FormSampleDump.FormXml.Length / 2)]);

        var result = new MetadataDumpReader().Read(source);

        Assert.Equal(0, result.FailedFiles);
        Assert.NotNull(result.Model.Find(FormSampleDump.FormId));
        Assert.Contains(result.Warnings, static warning => warning.Contains("ФормаЭлемента/Ext/Form.xml", StringComparison.Ordinal));
    }

    [Fact]
    public void Форма_без_файла_описания_остаётся_объектом_без_деталей()
    {
        // Есть объект метаданных формы, но нет Ext/Form.xml: форма остаётся в модели без описания.
        var source = new InMemoryDumpSource("выгрузка без описания формы");
        source.AddText(FormSampleDump.ConfigurationPath, FormSampleDump.ConfigurationXml);
        source.AddText(FormSampleDump.CatalogPath, FormSampleDump.CatalogXml);
        source.AddText(FormSampleDump.FormMetadataPath, FormSampleDump.FormMetadataXml);

        var result = new MetadataDumpReader().Read(source);
        var form = result.Model.Find(FormSampleDump.FormId);

        Assert.NotNull(form);
        Assert.Null(form.Form);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Большая_форма_разбирается_целиком()
    {
        // В реальной выгрузке медиана Form.xml — 15 КБ, а максимум под 2 МБ, поэтому разбор
        // не должен ни падать на объёме, ни разъезжаться по счётчикам.
        var xml = BuildLargeForm(elements: 5_000, attributes: 500);

        var started = Stopwatch.StartNew();
        var result = new FormDumpReader().Read(
            new MemoryStream(Encoding.UTF8.GetBytes(xml)),
            FormSampleDump.FormPath);
        started.Stop();

        Assert.Empty(result.Warnings);
        Assert.Equal(5_000, result.Form.Elements.Count);
        Assert.Equal(500, result.Form.Attributes.Count);
        Assert.Equal(5_000, result.Form.Handlers.Count);
        Assert.Equal("Объект", result.Form.Elements[^1].Attribute);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(10), $"разбор занял {started.Elapsed}");
    }

    /// <summary>Собирает форму размером около мегабайта: элементы, реквизиты и обработчики.</summary>
    private static string BuildLargeForm(int elements, int attributes)
    {
        var builder = new StringBuilder();
        builder.Append(
            """<?xml version="1.0" encoding="UTF-8"?><Form xmlns="http://v8.1c.ru/8.3/xcf/logform" xmlns:v8="http://v8.1c.ru/8.1/data/core" version="2.18"><ChildItems>""");

        for (var index = 0; index < elements; index++)
        {
            builder.Append(CultureInfo.InvariantCulture, $"""
                <InputField name="Поле{index}" id="{index}">
                	<DataPath>Объект.Поле{index}</DataPath>
                	<Events>
                		<Event name="OnChange">Поле{index}ПриИзменении</Event>
                	</Events>
                </InputField>
                """);
        }

        builder.Append("</ChildItems><Attributes>");
        for (var index = 0; index < attributes; index++)
        {
            var name = index == 0 ? "Объект" : "Реквизит" + index.ToString(CultureInfo.InvariantCulture);
            builder.Append(CultureInfo.InvariantCulture, $"""
                <Attribute name="{name}" id="{index}">
                	<Type>
                		<v8:Type>xs:string</v8:Type>
                	</Type>
                </Attribute>
                """);
        }

        builder.Append("</Attributes></Form>");
        return builder.ToString();
    }

    private static FormReadResult ReadForm(string? xml = null) => new FormDumpReader().Read(
        new MemoryStream(Encoding.UTF8.GetBytes(xml ?? FormSampleDump.FormXml)),
        FormSampleDump.FormPath);
}
