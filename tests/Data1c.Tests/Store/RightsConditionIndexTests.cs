using Data1c.Core.Analysis;
using Data1c.Core.Graph;
using Data1c.Core.Metadata;
using Data1c.Store;
using Xunit;

namespace Data1c.Tests.Store;

/// <summary>
/// Проверки хранения текста условий RLS в индексе (колонка появилась в схеме v9): условие лежит в
/// <c>metadata_refs.condition</c> рядом со строкой прав, пишется целиком и ищется по подстроке.
/// </summary>
public sealed class RightsConditionIndexTests
{
    [Fact]
    public void Условие_RLS_доезжает_до_индекса_целиком()
    {
        using var fixture = new ConditionFixture();
        var reader = fixture.Reader;

        // Условия роли читаются из индекса по объектам.
        var manager = reader.RightsConditionsOfRole("Role.Менеджер");
        Assert.Equal(2, manager.Count);
        Assert.Equal(RlsConditionDump.GoodsCondition, manager["Catalog.Товары"]);

        // Длинное условие сохраняется целиком: предел есть только у ответа инструмента.
        Assert.True(RlsConditionDump.LongCondition.Length > 4000);
        Assert.Equal(RlsConditionDump.LongCondition, manager["Catalog.Склады"]);

        var onGoods = reader.RightsConditionsOnObject("Catalog.Товары");
        var stored = Assert.Single(onGoods);
        Assert.Equal("Role.Менеджер", stored.Key);
        Assert.Equal(RlsConditionDump.GoodsCondition, stored.Value);

        // Строка прав отдаёт условие вместе с правами, а у объекта без RLS условие пустое.
        var rows = reader.Usages("Catalog.Товары", 50, MetadataRefContexts.Right);
        Assert.Equal(2, rows.Count);

        var withRls = Assert.Single(rows, static row => row.SourceId == "Role.Менеджер");
        Assert.Equal("Read=true;Delete=false;RLS", withRls.Detail);
        Assert.Equal(RlsConditionDump.GoodsCondition, withRls.Condition);

        var withoutRls = Assert.Single(rows, static row => row.SourceId == "Role.ПолныеПрава");
        Assert.Equal("Read=true", withoutRls.Detail);
        Assert.Null(withoutRls.Condition);

        // Обратная сторона: у объекта роли без ограничения условия нет.
        var order = Assert.Single(
            reader.ReferencesOf("Role.Менеджер", 50),
            static row => row.TargetId == "Document.Заказ");
        Assert.Equal(MetadataRefContexts.Right, order.Context);
        Assert.Null(order.Condition);

        // У роли без RLS условий нет вовсе.
        Assert.Empty(reader.RightsConditionsOfRole("Role.ПолныеПрава"));
    }

    [Fact]
    public void Поиск_по_тексту_условия_находит_роль_и_объект()
    {
        using var fixture = new ConditionFixture();
        var reader = fixture.Reader;

        // «Организация» встречается в условиях двух объектов одной роли.
        var found = reader.FindRightsByCondition("Организация");
        Assert.Equal(2, found.Matches);
        Assert.Equal(1, found.Roles);
        Assert.Equal(2, found.Objects);
        Assert.Equal(2, found.Rows.Count);
        Assert.Contains(found.Rows, static row => row.TargetId == "Catalog.Товары" && row.SourceId == "Role.Менеджер");
        Assert.Contains(found.Rows, static row => row.TargetId == "Catalog.Склады");

        // Регистр не важен: SQLite кириллицу не сворачивает, подстрока сравнивается в памяти.
        Assert.Equal(2, reader.FindRightsByCondition("организация").Matches);

        // Лимит обрезает строки, но не счётчики.
        var page = reader.FindRightsByCondition("Организация", limit: 1);
        Assert.Equal(2, page.Matches);
        Assert.Single(page.Rows);

        // Другое условие находит другую роль и другой объект.
        var observer = reader.FindRightsByCondition("ТекущийПользователь");
        Assert.Equal(1, observer.Matches);
        Assert.Equal(1, observer.Roles);
        Assert.Equal(1, observer.Objects);
        var row = Assert.Single(observer.Rows);
        Assert.Equal("Role.Наблюдатель", row.SourceId);
        Assert.Equal("Document.Заказ", row.TargetId);
        Assert.Equal(RlsConditionDump.OrderCondition, row.Condition);

        // Совпадений нет — счётчики нулевые, а пустая подстрока не совпадает ни с чем.
        Assert.Equal(0, reader.FindRightsByCondition("НетТакогоУсловия").Matches);
        Assert.Empty(reader.FindRightsByCondition("НетТакогоУсловия", limit: 500).Rows);
        Assert.Equal(0, reader.FindRightsByCondition("   ").Matches);
    }

    [Fact]
    public void Схема_создаётся_идемпотентно_и_считает_условия()
    {
        var path = Path.Combine(Path.GetTempPath(), "data1c-rls-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            // Повторное открытие той же базы схему не ломает: колонка условия одна и та же.
            using (var index = SqliteIndex.Open(path))
            {
                Assert.Equal(SqliteIndex.SupportedSchemaVersion, index.SchemaVersion);
            }

            using (var index = SqliteIndex.Open(path))
            {
                Assert.Equal(SqliteIndex.SupportedSchemaVersion, index.SchemaVersion);
            }

            var source = RlsConditionDump.Create();
            using (var index = SqliteIndex.Open(path))
            {
                new IndexWriter(index) { IncludeComments = false }.Write(source, new DumpAnalyzer().Analyze(source));

                // Условия есть у трёх пар «роль — объект»: два у «Менеджера» и одно у «Наблюдателя».
                Assert.Equal("3", index.GetMeta("cnt_rights_conditions"));
            }

            using (var index = SqliteIndex.OpenReadOnly(path))
            {
                Assert.Equal(3, new IndexReader(index).GetStatistics().RightsConditions);
                Assert.Equal(3, new IndexReader(index).CountRightsConditions());
            }

            // Индекс предыдущей схемы несовместим: чтение требует пересборки.
            using (var index = SqliteIndex.Open(path))
            {
                index.SetMeta("schema_version", "8");
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

    /// <summary>Индекс по синтетической выгрузке с условиями: собирается один раз на проверку.</summary>
    private sealed class ConditionFixture : IDisposable
    {
        private readonly string _path;

        public ConditionFixture()
        {
            _path = Path.Combine(Path.GetTempPath(), "data1c-rls-" + Guid.NewGuid().ToString("N") + ".db");
            var source = RlsConditionDump.Create();
            using (var index = SqliteIndex.Open(_path))
            {
                new IndexWriter(index) { IncludeComments = false }.Write(source, new DumpAnalyzer().Analyze(source));
            }

            Index = SqliteIndex.OpenReadOnly(_path);
            Reader = new IndexReader(Index);
        }

        public SqliteIndex Index { get; }

        public IndexReader Reader { get; }

        public void Dispose()
        {
            Index.Dispose();
            foreach (var file in Directory.EnumerateFiles(Path.GetDirectoryName(_path)!, Path.GetFileName(_path) + "*"))
            {
                File.Delete(file);
            }
        }
    }
}
