using Data1c.Core.Analysis;
using Data1c.Core.Graph;
using Data1c.Store;
using Xunit;

namespace Data1c.Tests.Store;

/// <summary>
/// Проверки хранения прав ролей в индексе: права лежат в <c>metadata_refs</c> с контекстом
/// <c>right</c>, источник — роль, цель — объект, а в <c>detail</c> — сжатый перечень прав
/// («Read=true;Insert=false») и метка RLS.
/// </summary>
public sealed class RightsIndexTests
{
    [Fact]
    public void Права_ролей_попадают_в_metadata_refs_с_контекстом_right()
    {
        var path = Path.Combine(Path.GetTempPath(), "data1c-rights-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            var source = RightsSampleDump.Create();
            var analyzed = new DumpAnalyzer().Analyze(source);
            using (var writeIndex = SqliteIndex.Open(path))
            {
                var written = new IndexWriter(writeIndex) { IncludeComments = false }.Write(source, analyzed);

                // Права записаны строками metadata_refs вместе с остальными обращениями к метаданным.
                Assert.True(written.MetadataRefs > 0);
            }

            using var index = SqliteIndex.OpenReadOnly(path);
            var reader = new IndexReader(index);

            var onCatalog = reader.Usages("Catalog.Товары", 50, MetadataRefContexts.Right);
            Assert.Equal(2, onCatalog.Count);

            // Источник и цель прав — настоящие узлы выгрузки: роль есть в модели, объект существует.
            Assert.NotNull(reader.GetNode("Role.Менеджер"));
            Assert.NotNull(reader.GetMetadataObject("Catalog.Товары"));

            var manager = Assert.Single(onCatalog, static row => row.SourceId == "Role.Менеджер");
            Assert.Equal("Catalog.Товары", manager.TargetId);
            Assert.Equal("Read=true;Insert=true;Delete=false;RLS", manager.Detail);
            Assert.Null(manager.Line);

            var full = Assert.Single(onCatalog, static row => row.SourceId == "Role.ПолныеПрава");
            Assert.Equal("Read=true", full.Detail);

            // Обратная сторона: что может роль. У «Менеджера» два объекта, у второго есть снятое право.
            var role = reader.ReferencesOf("Role.Менеджер", 50);
            var withRights = role.Where(static row => row.Context == MetadataRefContexts.Right).ToList();
            Assert.Equal(2, withRights.Count);

            var order = Assert.Single(withRights, static row => row.TargetId == "Document.Заказ");
            Assert.Equal("Read=true;InteractiveInsert=false", order.Detail);

            // Права на конфигурацию целиком не разрешаются в вид с точкой и хранятся как «Configuration».
            Assert.Contains(
                reader.ReferencesOf("Role.ПолныеПрава", 50),
                static row => row.TargetId == "Configuration" && row.Context == MetadataRefContexts.Right);

            // Карточка роли показывает её права через существующий инструмент metadata.
            var card = new IndexGraphQuery(reader).GetMetadata("Role.Менеджер");
            Assert.NotNull(card);
            Assert.Contains(
                card.References,
                static reference => reference.Kind == MetadataRefContexts.Right
                    && reference.Target == "Catalog.Товары"
                    && reference.Detail == "Read=true;Insert=true;Delete=false;RLS");
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
    public void Роль_без_прав_не_попадает_в_metadata_refs()
    {
        var path = Path.Combine(Path.GetTempPath(), "data1c-rights-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            var source = RightsSampleDump.Create();
            var analyzed = new DumpAnalyzer().Analyze(source);
            using (var writeIndex = SqliteIndex.Open(path))
            {
                new IndexWriter(writeIndex) { IncludeComments = false, IncludeRights = false }.Write(source, analyzed);
            }

            using var index = SqliteIndex.OpenReadOnly(path);
            var reader = new IndexReader(index);

            // Права отключены: строк с контекстом right нет вовсе.
            Assert.Empty(reader.Usages("Catalog.Товары", 50, MetadataRefContexts.Right));
        }
        finally
        {
            foreach (var file in Directory.EnumerateFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + "*"))
            {
                File.Delete(file);
            }
        }
    }

}
