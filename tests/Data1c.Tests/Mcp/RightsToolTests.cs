using System.Text.Json.Nodes;
using Data1c.Core.Analysis;
using Data1c.Core.Dump;
using Data1c.FileSystem;
using Data1c.Mcp;
using Data1c.Store;
using Xunit;

namespace Data1c.Tests.Mcp;

/// <summary>
/// Проверки инструмента <c>rights</c>: права ролей на объект, состав прав роли, признак и текст
/// условия RLS, а также понятные ответы для объекта без прав и неизвестной роли.
/// </summary>
public sealed class RightsToolTests
{
    [Fact]
    public async Task Объект_показывает_роли_права_и_текст_условия_RLS()
    {
        var (catalog, session) = await OpenAsync();
        await session.GetAsync(CancellationToken.None);

        var response = await CallAsync(catalog, new JsonObject { ["metadata"] = "Catalog.Товары" });
        var metadata = response["metadata"]!;

        Assert.Equal("Catalog.Товары", Text(metadata["object"]));
        Assert.Equal("Catalog", Text(metadata["kind"]));
        Assert.True(metadata["found"]!.GetValue<bool>());
        Assert.Equal(2, metadata["rolesTotal"]!.GetValue<int>());
        Assert.Equal(2, metadata["rolesWithRights"]!.GetValue<int>());
        Assert.Equal(3, metadata["rightsGranted"]!.GetValue<int>());
        Assert.Equal(1, metadata["rightsDenied"]!.GetValue<int>());
        Assert.Equal(1, metadata["rlsRoles"]!.GetValue<int>());

        var roles = metadata["roles"]!.AsArray();
        Assert.Equal(2, roles.Count);

        // Роль с ограничением RLS идёт первой, её условие прочитано из файла роли.
        var manager = roles[0]!;
        Assert.Equal("Role.Менеджер", Text(manager["role"]));
        Assert.Equal("Roles/Менеджер/Ext/Rights.xml", Text(manager["file"]));
        Assert.True(manager["rls"]!.GetValue<bool>());
        Assert.True(manager["rights"]!["Read"]!.GetValue<bool>());
        Assert.False(manager["rights"]!["Delete"]!.GetValue<bool>());
        Assert.Contains("Организация", Text(manager["condition"]));

        var full = roles[1]!;
        Assert.Equal("Role.ПолныеПрава", Text(full["role"]));
        Assert.False(full["rls"]!.GetValue<bool>());
        Assert.Null(full["condition"]);

        // Оговорка про назначение ролей пользователям обязательна: в файлах выгрузки его нет.
        Assert.Contains("пользовател", Text(response["note"]), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Роль_показывает_свои_объекты_права_и_RLS()
    {
        var (catalog, _) = await OpenAsync();

        var response = await CallAsync(catalog, new JsonObject { ["role"] = "Менеджер" });
        var role = response["role"]!;

        Assert.Equal("Role.Менеджер", Text(role["role"]));
        Assert.Equal("Менеджер", Text(role["name"]));
        Assert.False(role["setForNewObjects"]!.GetValue<bool>());
        Assert.True(role["setForAttributesByDefault"]!.GetValue<bool>());
        Assert.False(role["independentRightsOfChildObjects"]!.GetValue<bool>());
        Assert.Equal(2, role["objectsTotal"]!.GetValue<int>());
        Assert.Equal(2, role["objectsResolved"]!.GetValue<int>());
        Assert.Equal(3, role["rightsGranted"]!.GetValue<int>());
        Assert.Equal(2, role["rightsDenied"]!.GetValue<int>());
        Assert.Equal(1, role["rlsObjects"]!.GetValue<int>());

        var objects = role["objects"]!.AsArray();
        Assert.Equal(2, objects.Count);
        Assert.Equal("Catalog.Товары", Text(objects[0]!["object"]));
        Assert.True(objects[0]!["rights"]!["Insert"]!.GetValue<bool>());
        Assert.True(objects[0]!["rls"]!.GetValue<bool>());
        Assert.Contains("Организация", Text(objects[0]!["condition"]));

        var order = objects[1]!;
        Assert.Equal("Document.Заказ", Text(order["object"]));
        Assert.False(order["rights"]!["InteractiveInsert"]!.GetValue<bool>());
        Assert.False(order["rls"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Роль_принимает_идентификатор_и_путь_файла()
    {
        var (catalog, _) = await OpenAsync();

        var byId = await CallAsync(catalog, new JsonObject { ["role"] = "Role.Менеджер" });
        Assert.Equal("Role.Менеджер", Text(byId["role"]!["role"]));

        var byPath = await CallAsync(catalog, new JsonObject { ["role"] = "Roles/Менеджер/Ext/Rights.xml" });
        Assert.Equal("Role.Менеджер", Text(byPath["role"]!["role"]));
    }

    [Fact]
    public async Task Объект_без_прав_отвечает_понятно()
    {
        var (catalog, session) = await OpenAsync();
        await session.GetAsync(CancellationToken.None);

        var response = await CallAsync(catalog, new JsonObject { ["metadata"] = "Catalog.ЕдиницыИзмерения" });
        var metadata = response["metadata"]!;

        Assert.True(metadata["found"]!.GetValue<bool>());
        Assert.Equal(0, metadata["rolesWithRights"]!.GetValue<int>());
        Assert.Equal(2, metadata["rolesTotal"]!.GetValue<int>());
        Assert.Empty(metadata["roles"]!.AsArray());
        Assert.Contains("не даёт прав", Text(metadata["note"]));
    }

    [Fact]
    public async Task Неизвестный_объект_отвечает_подсказкой_про_идентификатор()
    {
        var (catalog, session) = await OpenAsync();
        await session.GetAsync(CancellationToken.None);

        var response = await CallAsync(catalog, new JsonObject { ["metadata"] = "Catalog.НетТакого" });
        var metadata = response["metadata"]!;

        Assert.False(metadata["found"]!.GetValue<bool>());
        Assert.Contains("не найден", Text(metadata["note"]));
    }

    [Fact]
    public async Task Неразобранный_идентификатор_ищется_по_строке_как_есть()
    {
        var (catalog, session) = await OpenAsync();
        await session.GetAsync(CancellationToken.None);

        // «Товары» — не идентификатор объекта: имя не разрешается, права ищутся по строке как есть.
        var response = await CallAsync(catalog, new JsonObject { ["metadata"] = "Товары" });
        var metadata = response["metadata"]!;

        Assert.Equal("Товары", Text(metadata["object"]));
        Assert.Null(metadata["kind"]);
        Assert.False(metadata["found"]!.GetValue<bool>());
        Assert.Equal(0, metadata["rolesWithRights"]!.GetValue<int>());
        Assert.Contains("не найден", Text(metadata["note"]));
    }

    [Fact]
    public async Task Неизвестная_роль_перечисляет_похожие_имена()
    {
        var (catalog, _) = await OpenAsync();
        var tool = Tool(catalog);

        var error = await Assert.ThrowsAsync<ToolException>(() =>
            tool.Execute(Arguments(new JsonObject { ["role"] = "Менедж" }), CancellationToken.None));

        Assert.Contains("Похожие роли", error.Message);
        Assert.Contains("Менеджер", error.Message);
    }

    [Fact]
    public async Task Без_аргументов_инструмент_объясняет_что_указать()
    {
        var (catalog, _) = await OpenAsync();
        var tool = Tool(catalog);

        var error = await Assert.ThrowsAsync<ToolException>(() =>
            tool.Execute(Arguments(new JsonObject()), CancellationToken.None));

        Assert.Contains("metadata", error.Message);
        Assert.Contains("role", error.Message);
    }

    [Fact]
    public async Task Ограничение_ответа_считает_счётчики_полностью()
    {
        var (catalog, _) = await OpenAsync();

        var response = await CallAsync(catalog, new JsonObject { ["metadata"] = "Catalog.Товары", ["limit"] = 1 });
        var metadata = response["metadata"]!;

        Assert.Equal(1, metadata["rolesShown"]!.GetValue<int>());
        Assert.Equal(2, metadata["rolesWithRights"]!.GetValue<int>());
        Assert.Equal(3, metadata["rightsGranted"]!.GetValue<int>());
        Assert.Single(metadata["roles"]!.AsArray());
    }

    [Fact]
    public async Task Права_читаются_и_без_индекса_на_выгрузке_в_памяти()
    {
        // Сводка прав собирается по файлам выгрузки: индекс для неё не нужен.
        using var session = new AnalysisSession(
            new AnalysisRequest { UseIndex = false },
            RightsSampleDump.Create());
        var catalog = new ToolCatalog(session);

        var response = await CallAsync(catalog, new JsonObject { ["role"] = "ПолныеПрава" });

        Assert.Null(session.Result);
        Assert.Equal("Role.ПолныеПрава", Text(response["role"]!["role"]));
        Assert.Equal(2, response["role"]!["objectsTotal"]!.GetValue<int>());
    }

    [Fact]
    public async Task В_режиме_индекса_список_файлов_прав_и_объект_берутся_из_индекса()
    {
        var root = Path.Combine(Path.GetTempPath(), "data1c-rights-dump-" + Guid.NewGuid().ToString("N"));
        var indexPath = Path.Combine(Path.GetTempPath(), "data1c-rights-index-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            Materialize(RightsSampleDump.Create(), root);
            var source = new FileSystemDumpSource(root);
            using (var index = SqliteIndex.Open(indexPath))
            {
                new IndexWriter(index) { IncludeComments = false }.Write(source, new DumpAnalyzer().Analyze(source));
            }

            using var session = new AnalysisSession(
                new AnalysisRequest { UseIndex = true, IndexPath = indexPath },
                source);
            await session.QueryAsync(CancellationToken.None);
            Assert.True(session.IsIndexReady);

            var response = await CallAsync(new ToolCatalog(session), new JsonObject { ["metadata"] = "Catalog.Товары" });
            var metadata = response["metadata"]!;

            // Существование объекта проверено по таблицам индекса, а не по разбору в память.
            Assert.Null(session.Result);
            Assert.True(metadata["found"]!.GetValue<bool>());
            Assert.Equal(2, metadata["rolesWithRights"]!.GetValue<int>());

            // Условие RLS прочитано из файла роли: в индексе лежит только признак.
            var manager = metadata["roles"]!.AsArray()[0]!;
            Assert.Equal("Role.Менеджер", Text(manager["role"]));
            Assert.Contains("Организация", Text(manager["condition"]));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }

            foreach (var file in Directory.EnumerateFiles(Path.GetDirectoryName(indexPath)!, Path.GetFileName(indexPath) + "*"))
            {
                File.Delete(file);
            }
        }
    }

    private static void Materialize(InMemoryDumpSource source, string root)
    {
        foreach (var file in source.EnumerateFiles())
        {
            var path = Path.Combine(root, file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var from = source.OpenRead(file);
            using var to = File.Create(path);
            from.CopyTo(to);
        }
    }

    private static async Task<(ToolCatalog Catalog, AnalysisSession Session)> OpenAsync()
    {
        var session = new AnalysisSession(new AnalysisRequest { UseIndex = false }, RightsSampleDump.Create());
        return (new ToolCatalog(session), session);
    }

    private static ToolSpec Tool(ToolCatalog catalog)
    {
        var tool = catalog.Find("rights");
        Assert.NotNull(tool);
        return tool;
    }

    private static ToolArguments Arguments(JsonObject arguments) => ToolArguments.From(arguments);

    private static async Task<JsonObject> CallAsync(ToolCatalog catalog, JsonObject arguments)
    {
        var text = await Tool(catalog).Execute(Arguments(arguments), CancellationToken.None);
        return JsonNode.Parse(text)!.AsObject();
    }

    private static string Text(JsonNode? node) => node?.GetValue<string>() ?? string.Empty;
}
