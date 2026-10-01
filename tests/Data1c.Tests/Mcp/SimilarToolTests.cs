using System.Text.Json;
using System.Text.Json.Nodes;
using Data1c.Core.Analysis;
using Data1c.Core.Dump;
using Data1c.Mcp;
using Data1c.Store;
using Xunit;

namespace Data1c.Tests.Mcp;

/// <summary>
/// Инструмент similar (Э2-5): по черновику и по уже существующей процедуре находит похожие
/// реализации и подсказывает открыть их код инструментом code.
/// </summary>
public sealed class SimilarToolTests
{
    private const string Initialize =
        """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","clientInfo":{"name":"тест","version":"1"}}}""";

    [Fact]
    public async Task Черновик_находит_похожую_реализацию()
    {
        var path = TempIndexPath();
        try
        {
            using var session = IndexSession(path, SimilarDump.Create());
            var response = await CallAsync(session, $"{{\"text\":{JsonSerializer.Serialize(SimilarDump.Draft)}}}");

            var payload = JsonNode.Parse(response)!.AsObject();
            var candidates = payload["candidates"]!.AsArray();
            var best = candidates[0]!;

            Assert.Equal(SimilarDump.TwinId, Text(best["id"]));
            Assert.Equal("ЗагрузитьТоварыПоЗаказу", Text(best["name"]));
            Assert.Equal(SimilarDump.TwinModulePath, Text(best["module"]));
            Assert.Equal("CommonModule.ОбработкаЗаказов", Text(best["owner"]));
            Assert.Equal("2-8", Text(best["lines"]));
            Assert.True(best["score"]!.GetValue<double>() > 0.5);

            var matches = best["matches"]!.AsArray().Select(match => Text(match!["signal"])).ToList();
            Assert.Contains("platformCalls", matches);
            Assert.Contains("metadataReferences", matches);
            Assert.Contains("из запроса", Text(best["why"]), StringComparison.Ordinal);

            // Ответ подсказывает, чем открыть код найденной процедуры.
            Assert.Contains("code", Text(payload["hint"]), StringComparison.Ordinal);

            // В признаках черновика видно обращение к справочнику из текста запроса.
            var draft = payload["draft"]!.AsObject();
            Assert.Contains("Catalog.Товары [query]", draft["metadataReferences"]!.AsArray().Select(Text));
        }
        finally
        {
            Remove(path);
        }
    }

    [Fact]
    public async Task Существующая_процедура_ищет_похожие_без_себя()
    {
        var path = TempIndexPath();
        try
        {
            using var session = IndexSession(path, SimilarDump.Create());
            var response = await CallAsync(session, $"{{\"id\":{JsonSerializer.Serialize(SimilarDump.TwinId)}}}");

            var payload = JsonNode.Parse(response)!.AsObject();
            var ids = payload["candidates"]!.AsArray().Select(candidate => Text(candidate!["id"])).ToList();

            Assert.DoesNotContain(SimilarDump.TwinId, ids);
            Assert.Equal(SimilarDump.ReaderId, ids[0]);
            Assert.Equal("id", Text(payload["source"]));
        }
        finally
        {
            Remove(path);
        }
    }

    [Fact]
    public async Task Процедура_по_пути_и_строке_находит_похожие()
    {
        var path = TempIndexPath();
        try
        {
            using var session = IndexSession(path, SimilarDump.Create());
            var response = await CallAsync(
                session,
                $"{{\"path\":{JsonSerializer.Serialize(SimilarDump.TwinModulePath)},\"line\":2,\"limit\":1}}");

            var payload = JsonNode.Parse(response)!.AsObject();
            var candidates = payload["candidates"]!.AsArray();

            Assert.Single(candidates);
            Assert.Equal(SimilarDump.ReaderId, Text(candidates[0]!["id"]));
            Assert.Equal("path", Text(payload["source"]));
        }
        finally
        {
            Remove(path);
        }
    }

    [Fact]
    public async Task Пустой_результат_отвечает_понятно()
    {
        var path = TempIndexPath();
        try
        {
            using var session = IndexSession(path, SimilarDump.Create());
            var response = await CallAsync(session, $"{{\"text\":{JsonSerializer.Serialize(SimilarDump.ForeignDraft)}}}");

            Assert.Contains("Похожих реализаций не найдено", response, StringComparison.Ordinal);
            Assert.Contains("не нашлось", response, StringComparison.Ordinal);
        }
        finally
        {
            Remove(path);
        }
    }

    [Fact]
    public async Task Ответ_показывает_текст_вызова_и_отдельно_слабый_сигнал()
    {
        var path = TempIndexPath();
        try
        {
            using var session = IndexSession(path, CallDump());
            var response = await CallAsync(session, $"{{\"text\":{JsonSerializer.Serialize(CallDraft)}}}");

            var payload = JsonNode.Parse(response)!.AsObject();
            var draft = payload["draft"]!.AsObject();

            // Признак вызова процедуры — идентификатор цели, но в ответе виден текст вызова.
            Assert.Contains("Общий.ОбработатьТовар", draft["routineCalls"]!.AsArray().Select(Text));

            // Вызов с неразрешённой целью показан отдельно: по нему совпадение только по имени метода.
            Assert.Contains("Справочники.Товары.НайтиПоНаименованию", draft["unresolvedCalls"]!.AsArray().Select(Text));

            var weak = payload["candidates"]!.AsArray()
                .SelectMany(static candidate => candidate!["matches"]!.AsArray())
                .Where(static match => Text(match!["signal"]) == "unresolvedCallsWeak")
                .ToList();
            Assert.NotEmpty(weak);
            Assert.Contains(weak, static match =>
                string.Join(" ", match!["values"]!.AsArray().Select(Text))
                    .Contains("НайтиПоНаименованию", StringComparison.Ordinal));
            Assert.Contains("слабый сигнал", Text(payload["candidates"]![0]!["why"]), StringComparison.Ordinal);
        }
        finally
        {
            Remove(path);
        }
    }

    /// <summary>Черновик: вызывает метод общего модуля с квалификатором и метод менеджера справочника.</summary>
    private const string CallDraft = """
        // Обрабатывает товары по заказу.
        Процедура ОбработатьТоварыПоЗаказу()
            Общий.ОбработатьТовар("Тест");
            Товар = Справочники.Товары.НайтиПоНаименованию("Тест");
        КонецПроцедуры
        """;

    /// <summary>Выгрузка: процедура вызывает тот же метод общего модуля и тот же метод менеджера.</summary>
    private static InMemoryDumpSource CallDump()
    {
        var source = new InMemoryDumpSource("выгрузка с вызовом общего модуля");
        source.AddText("Configuration.xml", """
            <?xml version="1.0" encoding="UTF-8"?>
            <MetaDataObject xmlns="http://v8.1c.ru/8.3/MDClasses" xmlns:v8="http://v8.1c.ru/8.1/data/core" version="2.20">
                <Configuration uuid="0f0f0f0f-0000-0000-0000-0000000000e5">
                    <Properties>
                        <Name>КонфигурацияСВызовомМодуля</Name>
                    </Properties>
                    <ChildObjects>
                        <CommonModule>Общий</CommonModule>
                    </ChildObjects>
                </Configuration>
            </MetaDataObject>
            """);
        source.AddText("CommonModules/Общий.xml", """
            <?xml version="1.0" encoding="UTF-8"?>
            <MetaDataObject xmlns="http://v8.1c.ru/8.3/MDClasses" xmlns:v8="http://v8.1c.ru/8.1/data/core" version="2.20">
                <CommonModule uuid="99999999-9999-9999-9999-999999999991">
                    <Properties>
                        <Name>Общий</Name>
                        <Server>true</Server>
                    </Properties>
                </CommonModule>
            </MetaDataObject>
            """);
        source.AddText("CommonModules/Общий/Ext/Module.bsl", """
            // Обрабатывает товар.
            Процедура ОбработатьТовар(Товар)
                Сообщить(Товар);
            КонецПроцедуры

            // Обрабатывает товары по заказу.
            Процедура ОбработатьТоварыПоЗаказу()
                Общий.ОбработатьТовар("Тест");
                Товар = Товары.НайтиПоНаименованию("Тест");
            КонецПроцедуры
            """);
        return source;
    }

    /// <summary>Сессия поверх готового индекса в файле: инструмент отвечает из индекса, без разбора в память.</summary>
    private static AnalysisSession IndexSession(string path, InMemoryDumpSource source)
    {
        var analyzed = new DumpAnalyzer().Analyze(source);
        using (var index = SqliteIndex.Open(path))
        {
            new IndexWriter(index).Write(source, analyzed);
        }

        return new AnalysisSession(new AnalysisRequest { IndexPath = path, UseIndex = true }, source);
    }

    private static async Task<string> CallAsync(AnalysisSession session, string arguments)
    {
        var call = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 2,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = "similar",
                ["arguments"] = JsonNode.Parse(arguments),
            },
        }.ToJsonString();

        var output = new StringWriter();
        var log = new StringWriter();
        var server = new McpServer(
            new ToolCatalog(session),
            new StringReader(Initialize + "\n" + call + "\n"),
            output,
            log);
        await server.RunAsync(CancellationToken.None);

        var line = output
            .ToString()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(static text => JsonNode.Parse(text)!.AsObject())
            .Single(item => Text(item["id"]) == "2");

        return Text(line["result"]!["content"]![0]!["text"]);
    }

    private static string TempIndexPath() =>
        Path.Combine(Path.GetTempPath(), "data1c-similar-" + Guid.NewGuid().ToString("N") + ".db");

    private static void Remove(string path)
    {
        var directory = Path.GetDirectoryName(path)!;
        foreach (var file in Directory.EnumerateFiles(directory, Path.GetFileName(path) + "*"))
        {
            File.Delete(file);
        }
    }

    private static string Text(JsonNode? node) => node switch
    {
        null => string.Empty,
        JsonValue value when value.TryGetValue<string>(out var text) => text,
        JsonValue value when value.TryGetValue<double>(out var number) => number.ToString(System.Globalization.CultureInfo.InvariantCulture),
        JsonValue value when value.TryGetValue<int>(out var integer) => integer.ToString(System.Globalization.CultureInfo.InvariantCulture),
        _ => node.ToJsonString(),
    };
}
