using Data1c.Core.Analysis;
using Data1c.Core.Dump;
using Data1c.Tests.Graph;
using Data1c.Store;
using Xunit;

namespace Data1c.Tests.Store;

/// <summary>
/// Проверяет индекс выгрузки: запись разбора в SQLite, поиск по подстроке (триграммы FTS5),
/// вызовы, обращения к метаданным и обход связей рекурсивным запросом.
/// </summary>
public sealed class SqliteIndexTests
{
    private const string FirstModule = DependencyGraphBuilderTests.FirstModulePath;
    private const string SecondModule = DependencyGraphBuilderTests.SecondModulePath;

    private static string Routine(string module, string name) => $"routine:module:{module}#{name}";

    [Fact]
    public void Пишет_и_читает_узлы_символы_и_связи()
    {
        using var fixture = new IndexFixture();
        var statistics = fixture.Reader.GetStatistics();

        Assert.True(statistics.Nodes > 10, $"узлов: {statistics.Nodes}");
        Assert.True(statistics.Edges > 10, $"связей: {statistics.Edges}");
        Assert.True(statistics.Symbols >= 3, $"символов: {statistics.Symbols}");
        Assert.True(statistics.MetadataObjects > 0);
        Assert.Equal(fixture.Write.Nodes, statistics.Nodes);
        Assert.Equal(fixture.Write.Symbols, statistics.Symbols);
        Assert.NotNull(statistics.DumpPath);
        Assert.NotNull(statistics.IndexedAt);

        var catalog = fixture.Reader.GetNode("Catalog.Товары");
        Assert.NotNull(catalog);
        Assert.Equal("MetadataObject", catalog.Kind);
        Assert.Equal("Товары", catalog.Name);
    }

    [Fact]
    public void Находит_узел_по_подстроке_имени()
    {
        using var fixture = new IndexFixture();

        // «овар» — подстрока внутри «Товары»: работает благодаря триграммному FTS5.
        var hits = fixture.Reader.Search("овар");

        Assert.Contains(hits, static n => n.Id == "Catalog.Товары");
    }

    [Fact]
    public void Находит_процедуры_и_определяет_процедуру_по_строке()
    {
        using var fixture = new IndexFixture();

        var symbols = fixture.Reader.FindSymbols("МояПроцедура", exact: true);
        var symbol = Assert.Single(symbols);
        Assert.Equal(FirstModule, symbol.ModulePath);
        Assert.True(symbol.IsExport);
        Assert.Equal("Procedure", symbol.Kind);

        var byLine = fixture.Reader.FindSymbolAt(FirstModule, 2);
        Assert.NotNull(byLine);
        Assert.Equal("МояПроцедура", byLine.Name);

        Assert.Equal(2, fixture.Reader.FindSymbolsInModule(FirstModule).Count);
    }

    [Fact]
    public void Показывает_вызовы_в_обе_стороны()
    {
        using var fixture = new IndexFixture();
        var caller = Routine(FirstModule, "МояПроцедура");
        var callee = Routine(SecondModule, "ЗагрузитьДанные");

        var callees = fixture.Reader.Callees(caller);
        Assert.Contains(callee, callees);
        Assert.Contains(Routine(FirstModule, "ДругаяПроцедура"), callees);

        var callers = fixture.Reader.Callers(callee);
        Assert.Contains(caller, callers);

        var calls = fixture.Reader.Outgoing(caller, "Calls");
        Assert.Contains(calls, static e => e.Detail == "РаботаСДанными.ЗагрузитьДанные");
        Assert.Contains(calls, static e => e.Line == 2);
    }

    [Fact]
    public void Показывает_обращения_к_объекту_метаданных()
    {
        using var fixture = new IndexFixture();

        var usages = fixture.Reader.Usages("Catalog.Товары", context: "code");

        Assert.NotEmpty(usages);
        Assert.Contains(usages, static u => u.SourceId.Contains("ДругаяПроцедура", StringComparison.Ordinal));
    }

    [Fact]
    public void Обходит_связи_на_глубину_рекурсивным_запросом()
    {
        using var fixture = new IndexFixture();
        var caller = Routine(FirstModule, "МояПроцедура");

        var reach = fixture.Reader.Reach(caller, depth: 1, maxNodes: 50, direction: "out");
        Assert.Contains(reach, static r => r.Id == Routine(FirstModule, "ДругаяПроцедура") && r.Depth == 1);
        Assert.Contains(reach, static r => r.Id == Routine(SecondModule, "ЗагрузитьДанные") && r.Depth == 1);

        var (nodes, edges) = fixture.Reader.Neighborhood(caller, depth: 1, maxNodes: 50);
        Assert.NotEmpty(nodes);
        Assert.NotEmpty(edges);

        // Каждая связь окружения соединяет только узлы, попавшие в окружение.
        var ids = nodes.Select(static n => n.Id).ToHashSet(StringComparer.Ordinal);
        Assert.All(edges, e =>
        {
            Assert.Contains(e.SourceId, ids);
            Assert.Contains(e.TargetId, ids);
        });
    }

    [Fact]
    public void Обход_не_зацикливается_и_уважает_лимиты()
    {
        using var fixture = new IndexFixture();
        var caller = Routine(FirstModule, "МояПроцедура");

        var deep = fixture.Reader.Reach(caller, depth: 4, maxNodes: 1000);
        Assert.Equal(deep.Count, deep.Select(static r => r.Id).Distinct(StringComparer.Ordinal).Count());

        var limited = fixture.Reader.Reach(caller, depth: 4, maxNodes: 2);
        Assert.True(limited.Count <= 2, $"узлов: {limited.Count}");
    }

    [Fact]
    public void Ищет_объекты_метаданных_по_имени_и_синониму()
    {
        using var fixture = new IndexFixture();

        var objects = fixture.Reader.FindMetadataObjects("Товар");

        Assert.Contains(objects, static o => o.Id == "Catalog.Товары");
    }

    [Fact]
    public void Отдаёт_состав_объекта_метаданных()
    {
        using var fixture = new IndexFixture();

        var items = fixture.Reader.GetMetadataItems("Catalog.Товары");

        Assert.Contains(items, static i => i.Name == "Артикул");
    }

    [Fact]
    public void Повторная_запись_заменяет_данные_а_не_дублирует()
    {
        using var fixture = new IndexFixture();
        var before = fixture.Reader.GetStatistics();

        fixture.WriteAgain();

        var after = fixture.Reader.GetStatistics();
        Assert.Equal(before.Nodes, after.Nodes);
        Assert.Equal(before.Edges, after.Edges);
        Assert.Equal(before.Symbols, after.Symbols);
    }

    [Fact]
    public void Схема_создаётся_идемпотентно_и_версия_проверяется()
    {
        var path = Path.Combine(Path.GetTempPath(), "data1c-index-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using (var index = SqliteIndex.Open(path))
            {
                Assert.Equal(IndexSchemaVersion, index.SchemaVersion);
                index.SetMeta("indexed_at", DateTimeOffset.UtcNow.ToString("O"));
            }

            Assert.True(SqliteIndex.LooksLikeIndex(path));

            using (var index = SqliteIndex.OpenReadOnly(path))
            {
                Assert.Equal(IndexSchemaVersion, index.SchemaVersion);
                Assert.True(index.IsReadOnly);
            }

            using (var index = SqliteIndex.Open(path))
            {
                index.SetMeta("schema_version", "99");
            }

            Assert.False(SqliteIndex.LooksLikeIndex(path));
            Assert.Throws<InvalidOperationException>(() => SqliteIndex.OpenReadOnly(path));
        }
        finally
        {
            foreach (var file in Directory.EnumerateFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + "*"))
            {
                File.Delete(file);
            }
        }
    }

    [Fact]
    public void Находит_вызывающих_по_имени_вызова()
    {
        using var fixture = new IndexFixture();

        var callers = fixture.Reader.CallersByName("РаботаСДанными.ЗагрузитьДанные");

        Assert.Contains(callers, e => e.SourceId == Routine(FirstModule, "МояПроцедура"));
    }

    [Fact]
    public void Обход_связей_работает_на_индексе_только_для_чтения()
    {
        var path = Path.Combine(Path.GetTempPath(), "data1c-readonly-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            var source = SampleDump.Create();
            var result = new DumpAnalyzer().Analyze(source);
            using (var index = SqliteIndex.Open(path))
            {
                new IndexWriter(index) { IncludeComments = false }.Write(source, result);
            }

            // Индекс открыт только на чтение: временные таблицы в этом режиме запрещены,
            // поэтому обход обязан обходиться рекурсивным запросом.
            using var readOnly = SqliteIndex.OpenReadOnly(path);
            var reader = new IndexReader(readOnly);
            var caller = Routine(FirstModule, "МояПроцедура");

            var reach = reader.Reach(caller, depth: 1, direction: "out");
            Assert.Contains(reach, static r => r.Id == Routine(SecondModule, "ЗагрузитьДанные"));

            var (nodes, edges) = reader.Neighborhood(caller, depth: 1, maxNodes: 30);
            Assert.NotEmpty(nodes);
            Assert.NotEmpty(edges);
        }
        finally
        {
            foreach (var file in Directory.EnumerateFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + "*"))
            {
                File.Delete(file);
            }
        }
    }

    [Fact]
    public void Число_и_имена_параметров_доезжают_до_индекса()
    {
        using var fixture = new IndexFixture();

        // Схема v7 хранит и имена параметров, и их число: проверке черновика нужны оба.
        var withParameter = Assert.Single(fixture.Reader.FindSymbols("ПриОткрытии", exact: true));
        Assert.Equal(1, withParameter.ParametersCount);
        Assert.Equal("Отказ", withParameter.Parameters);

        var withoutParameters = Assert.Single(fixture.Reader.FindSymbols("ДругаяПроцедура", exact: true));
        Assert.Equal(0, withoutParameters.ParametersCount);
        Assert.Null(withoutParameters.Parameters);

        // Те же данные видны проверке черновика через контекст индекса.
        var context = new IndexDraftContext(fixture.Reader);
        var symbol = Assert.Single(context.FindSymbols("ПриОткрытии", exact: true));
        Assert.Equal(["Отказ"], symbol.Parameters);
    }

    [Fact]
    public void Число_обязательных_параметров_доезжает_до_индекса()
    {
        // Параметры со значением по умолчанию необязательны: индекс хранит и общее число, и обязательное.
        var source = SampleDump.Create();
        source.AddText(SampleDump.SecondCommonModuleBslPath, """
            Процедура ЗагрузитьДанные(Ссылка, Режим = Неопределено, Дата = Неопределено) Экспорт
                Сообщить(Ссылка);
            КонецПроцедуры
            """);
        var analysis = new DumpAnalyzer().Analyze(source);
        using var index = SqliteIndex.OpenInMemory();
        new IndexWriter(index) { IncludeComments = false }.Write(source, analysis);
        var reader = new IndexReader(index);

        var symbol = Assert.Single(reader.FindSymbols("ЗагрузитьДанные", exact: true));
        Assert.Equal(3, symbol.ParametersCount);
        Assert.Equal(1, symbol.RequiredCount);

        var context = new IndexDraftContext(reader);
        var draft = Assert.Single(context.FindSymbols("ЗагрузитьДанные", exact: true));
        Assert.Equal(["Ссылка", "Режим", "Дата"], draft.Parameters);
        Assert.Equal(1, draft.RequiredCount);
    }

    [Fact]
    public void Обработчики_формы_находятся_по_пути_модуля()
    {
        var source = FormSampleDump.Create();
        var result = new DumpAnalyzer().Analyze(source);
        using var index = SqliteIndex.OpenInMemory();
        new IndexWriter(index) { IncludeComments = false }.Write(source, result);
        var reader = new IndexReader(index);

        // Обработчик события формы, обработчик изменения элемента и команда формы: все три
        // зарегистрированы в описании формы, и модуль формы связан с ней путём модуля.
        Assert.True(reader.IsEventHandler(FormSampleDump.FormModulePath, "ПриОткрытии"));
        Assert.True(reader.IsEventHandler(FormSampleDump.FormModulePath, "АртикулПриИзменении"));
        Assert.True(reader.IsEventHandler(FormSampleDump.FormModulePath, "Печать"));

        // Регистр в BSL не важен, а чужие имена и пути обработчиками не считаются.
        Assert.True(reader.IsEventHandler(FormSampleDump.FormModulePath, "приоткрытии"));
        Assert.False(reader.IsEventHandler(FormSampleDump.FormModulePath, "Сообщить"));
        Assert.False(reader.IsEventHandler(SampleDump.CommonModuleBslPath, "ПриОткрытии"));
    }

    private static int IndexSchemaVersion => SqliteIndex.SupportedSchemaVersion;

    /// <summary>Индекс, собранный из тестовой выгрузки через полный разбор.</summary>
    private sealed class IndexFixture : IDisposable
    {
        private readonly IDumpSource _source;
        private readonly AnalysisResult _result;

        internal IndexFixture()
        {
            _source = SampleDump.Create();
            _result = new DumpAnalyzer().Analyze(_source);
            Index = SqliteIndex.OpenInMemory();
            Write = new IndexWriter(Index) { IncludeComments = true }.Write(_source, _result);
            Reader = new IndexReader(Index);
        }

        internal SqliteIndex Index { get; }

        internal IndexReader Reader { get; }

        internal IndexWriteResult Write { get; private set; }

        internal void WriteAgain() => Write = new IndexWriter(Index) { IncludeComments = false }.Write(_source, _result);

        public void Dispose() => Index.Dispose();
    }
}
