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
