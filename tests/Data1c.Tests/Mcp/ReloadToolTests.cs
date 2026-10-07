using System.Text.Json.Nodes;
using Data1c.Core.Analysis;
using Data1c.Core.Dump;
using Data1c.Mcp;
using Xunit;

namespace Data1c.Tests.Mcp;

/// <summary>
/// Инструмент reload с аргументом paths: переиндексируются только указанные модули, полная
/// перезагрузка всей выгрузки не запускается. Проверяется на настоящих файлах выгрузки во
/// временном каталоге — вместе с индексом, графом и кешем исходных текстов.
/// </summary>
public sealed class ReloadToolTests
{
    [Fact]
    public async Task Частичная_переиндексация_обновляет_граф_только_указанного_модуля()
    {
        var root = TestDump.Materialize();
        try
        {
            using var session = await OpenIndexedAsync(root);
            var untouchedBefore = FileState(session, SampleDump.SecondCommonModuleBslPath);
            var symbolsBefore = session.GetIndexReader()!.FindSymbolsInModule(SampleDump.SecondCommonModuleBslPath).Count;

            AppendToModule(root, SampleDump.CommonModuleBslPath);

            var answer = await CallAsync(session, new JsonObject { ["paths"] = new JsonArray(SampleDump.CommonModuleBslPath) });

            Assert.Equal("partial", Text(answer["mode"]));
            Assert.True(answer["reindexed"]!.GetValue<bool>());
            Assert.Equal([SampleDump.CommonModuleBslPath], Strings(answer["modules"]));
            Assert.True(answer["counts"]!["nodes"]!.GetValue<int>() > 0);
            Assert.Equal(1, answer["counts"]!["files"]!.GetValue<int>());
            Assert.True(answer["seconds"]!.GetValue<double>() >= 0);
            Assert.True(answer["compareSeconds"]!.GetValue<double>() >= 0);
            Assert.Equal(1, answer["dumpChange"]!["changed"]!.GetValue<int>());
            Assert.True(answer["dumpFreshAfter"]!.GetValue<bool>());

            // Граф обновился именно для этого модуля: появилась процедура и её вызов другого модуля.
            var reader = session.GetIndexReader()!;
            var routine = "routine:module:" + SampleDump.CommonModuleBslPath + "#НоваяПроцедураПослеПравки";
            Assert.NotNull(reader.GetNode(routine));
            Assert.Contains(
                "routine:module:" + SampleDump.SecondCommonModuleBslPath + "#ЗагрузитьДанные",
                reader.Callees(routine, 50));

            // Соседний модуль не тронут: ни его символы, ни отметка файла в индексе.
            Assert.Equal(symbolsBefore, reader.FindSymbolsInModule(SampleDump.SecondCommonModuleBslPath).Count);
            Assert.Equal(untouchedBefore, FileState(session, SampleDump.SecondCommonModuleBslPath));
        }
        finally
        {
            TestDump.Remove(root);
        }
    }

    [Fact]
    public async Task После_частичной_переиндексации_читается_новый_текст_модуля()
    {
        var root = TestDump.Materialize();
        try
        {
            using var session = await OpenIndexedAsync(root);
            var path = SampleDump.CommonModuleBslPath;

            // Текст читается заранее: он попадает в кеш читателя исходников.
            Assert.DoesNotContain("НоваяПроцедураПослеПравки", Text(session.Code.Read(path)));

            AppendToModule(root, path);
            await CallAsync(session, new JsonObject { ["paths"] = new JsonArray(path) });

            Assert.Contains("НоваяПроцедураПослеПравки", Text(session.Code.Read(path)));
        }
        finally
        {
            TestDump.Remove(root);
        }
    }

    [Fact]
    public async Task Неизвестный_индексу_путь_сообщает_причину_а_не_тихий_успех()
    {
        const string absent = "CommonModules/НетТакогоМодуля/Ext/Module.bsl";
        var root = TestDump.Materialize();
        try
        {
            using var session = await OpenIndexedAsync(root);
            var before = session.GetIndexReader()!.ReadFileStates();

            var answer = await CallAsync(session, new JsonObject { ["paths"] = new JsonArray(absent) });

            Assert.Equal("partial", Text(answer["mode"]));
            Assert.False(answer["reindexed"]!.GetValue<bool>());
            Assert.Equal([absent], Strings(answer["unknown"]));
            Assert.Contains("неизвестен индексу", Text(answer["reason"]), StringComparison.Ordinal);
            Assert.Contains("полная сборка", Text(answer["hint"]), StringComparison.Ordinal);
            Assert.False(answer["fullReloadStarted"]!.GetValue<bool>());

            // Индекс не изменился: ни файлов, ни разбора.
            Assert.Equal(before.Count, session.GetIndexReader()!.ReadFileStates().Count);
        }
        finally
        {
            TestDump.Remove(root);
        }
    }

    [Fact]
    public async Task Пустой_paths_не_делает_вид_что_всё_обновилось()
    {
        var root = TestDump.Materialize();
        try
        {
            using var session = await OpenIndexedAsync(root);

            var answer = await CallAsync(session, new JsonObject { ["paths"] = new JsonArray() });

            Assert.Equal("partial", Text(answer["mode"]));
            Assert.False(answer["reindexed"]!.GetValue<bool>());
            Assert.Contains("paths пуст", Text(answer["reason"]), StringComparison.Ordinal);
            Assert.False(answer["fullReloadStarted"]!.GetValue<bool>());

            // Полная перезагрузка не запускалась: индекс остался открытым.
            Assert.True(session.IsIndexReady);
        }
        finally
        {
            TestDump.Remove(root);
        }
    }

    [Fact]
    public async Task Несколько_путей_переиндексируются_за_один_вызов()
    {
        var root = TestDump.Materialize();
        try
        {
            using var session = await OpenIndexedAsync(root);
            AppendToModule(root, SampleDump.CommonModuleBslPath, "НоваяПроцедураПервогоМодуля");
            AppendToModule(root, SampleDump.SecondCommonModuleBslPath, "НоваяПроцедураВторогоМодуля");

            var answer = await CallAsync(
                session,
                new JsonObject
                {
                    ["paths"] = new JsonArray(SampleDump.CommonModuleBslPath, SampleDump.SecondCommonModuleBslPath),
                });

            Assert.True(answer["reindexed"]!.GetValue<bool>());
            Assert.Equal([SampleDump.CommonModuleBslPath, SampleDump.SecondCommonModuleBslPath], Strings(answer["modules"]));
            Assert.Equal(2, answer["counts"]!["files"]!.GetValue<int>());

            var reader = session.GetIndexReader()!;
            Assert.NotNull(reader.GetNode("routine:module:" + SampleDump.CommonModuleBslPath + "#НоваяПроцедураПервогоМодуля"));
            Assert.NotNull(reader.GetNode("routine:module:" + SampleDump.SecondCommonModuleBslPath + "#НоваяПроцедураВторогоМодуля"));
        }
        finally
        {
            TestDump.Remove(root);
        }
    }

    [Fact]
    public async Task Без_индекса_частичная_переиндексация_невозможна_и_запускается_полная()
    {
        using var session = new AnalysisSession(
            new AnalysisRequest { UseIndex = false },
            SampleDump.Create());
        var codeBefore = session.Code;

        var answer = await CallAsync(session, new JsonObject { ["paths"] = new JsonArray(SampleDump.CommonModuleBslPath) });

        Assert.Equal("full", Text(answer["mode"]));
        Assert.False(answer["reindexed"]!.GetValue<bool>());
        Assert.True(answer["fullReloadStarted"]!.GetValue<bool>());
        Assert.Contains("индекс не используется", Text(answer["reason"]), StringComparison.Ordinal);
        Assert.Contains("полная перезагрузка", Text(answer["hint"]), StringComparison.Ordinal);

        // Полная перезагрузка действительно запущена: читатель исходников заменён.
        Assert.False(ReferenceEquals(codeBefore, session.Code));
    }

    [Fact]
    public async Task Без_paths_полная_перезагрузка_отдаёт_сводку_изменений()
    {
        var root = TestDump.Materialize();
        try
        {
            using var session = await OpenIndexedAsync(root);
            AppendToModule(root, SampleDump.CommonModuleBslPath);

            var answer = await CallAsync(session, new JsonObject());

            Assert.Equal("full", Text(answer["mode"]));
            Assert.True(answer["fullReloadStarted"]!.GetValue<bool>());
            Assert.Equal(1, answer["dumpChange"]!["changed"]!.GetValue<int>());
            Assert.Equal(0, answer["dumpChange"]!["added"]!.GetValue<int>());
            Assert.False(answer["dumpChange"]!["fresh"]!.GetValue<bool>());

            // Прежнее поведение: разбор выгрузки запускается заново, индекс будет пересобран.
            Assert.False(session.IsIndexReady);

            var query = await session.QueryAsync(CancellationToken.None);
            Assert.Contains(
                query.Search("НоваяПроцедураПослеПравки", 5),
                static hit => hit.Name == "НоваяПроцедураПослеПравки");
        }
        finally
        {
            TestDump.Remove(root);
        }
    }

    /// <summary>Открывает выгрузку на диске и собирает индекс: частичная переиндексация работает по нему.</summary>
    private static async Task<AnalysisSession> OpenIndexedAsync(string root)
    {
        var session = new AnalysisSession(new AnalysisRequest { DumpPaths = [root] });
        await session.QueryAsync(CancellationToken.None);
        Assert.True(session.IsIndexReady);
        return session;
    }

    /// <summary>Дописывает в модуль новую процедуру: размер файла меняется, и правку видно без времени.</summary>
    private static void AppendToModule(string root, string modulePath, string procedure = "НоваяПроцедураПослеПравки")
    {
        var file = Path.Combine(root, modulePath.Replace('/', Path.DirectorySeparatorChar));
        File.AppendAllText(file, "\nПроцедура " + procedure + "() Экспорт\n\tРаботаСДанными.ЗагрузитьДанные();\nКонецПроцедуры\n");
    }

    private static (long Size, long Mtime) FileState(AnalysisSession session, string modulePath) =>
        session.GetIndexReader()!.ReadFileStates()[modulePath];

    private static Task<JsonObject> CallAsync(AnalysisSession session, JsonObject arguments)
    {
        var tool = new ToolCatalog(session).Find("reload") ?? throw new InvalidOperationException("Нет инструмента «reload».");
        return ExecuteAsync(tool, arguments);
    }

    private static async Task<JsonObject> ExecuteAsync(ToolSpec tool, JsonObject arguments)
    {
        var text = await tool.Execute(ToolArguments.From(arguments), CancellationToken.None);
        return JsonNode.Parse(text)!.AsObject();
    }

    private static string Text(JsonNode? node) => node?.GetValue<string>() ?? string.Empty;

    private static string Text(CodeFragment? fragment) =>
        fragment is null ? string.Empty : string.Join('\n', fragment.Lines);

    private static string[] Strings(JsonNode? node) =>
        node is null ? [] : [.. node.AsArray().Select(static item => item!.GetValue<string>())];
}
