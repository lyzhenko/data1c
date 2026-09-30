using System.IO.Compression;
using Data1c.Core.Platform;
using Xunit;

namespace Data1c.Tests.Platform;

public sealed class HbkArchiveTests
{
    private static readonly (string Path, string Html)[] TwoTopics =
    [
        ("Массив/Добавить.html", "<h1>Массив.Добавить</h1><p>Добавляет значение в конец массива.</p>"),
        ("Массив/Количество.html", "<h1>Массив.Количество</h1><p>Количество элементов.</p>"),
    ];

    [Fact]
    public void Достаёт_zip_с_темами_из_контейнера()
    {
        var container = HbkTestWriter.Create(TwoTopics);

        var payload = HbkArchive.TryReadFileStorage(new MemoryStream(container));

        Assert.NotNull(payload);
        using var zip = new ZipArchive(new MemoryStream(payload), ZipArchiveMode.Read);
        Assert.Equal(2, zip.Entries.Count);
    }

    [Fact]
    public void Собирает_содержимое_из_нескольких_блоков_по_размеру_данных_блока()
    {
        // Ловушка реального формата: первое поле заголовка — объявленный размер всей записи,
        // он больше первого блока (здесь — длина всего ZIP). Читать блок по нему нельзя:
        // соберётся мусор и ZIP не откроется.
        var container = HbkTestWriter.Create(TwoTopics, new HbkWriteOptions { BlockChunkSize = 64 });

        var payload = HbkArchive.ReadFileStorage(new MemoryStream(container));
        using var zip = new ZipArchive(new MemoryStream(payload), ZipArchiveMode.Read);

        Assert.Equal(2, zip.Entries.Count);
        Assert.True(payload.Length > 64, "содержимое должно занимать больше одного блока");

        var topics = ReadTopics(container);
        Assert.Equal(2, topics.Count);
        Assert.Contains(topics, static t => t.Name == "Массив.Добавить");
    }

    [Fact]
    public void Возвращает_null_если_записи_FileStorage_нет()
    {
        var container = HbkTestWriter.Create(TwoTopics, new HbkWriteOptions { OmitFileStorageRecord = true });

        Assert.Null(HbkArchive.TryReadFileStorage(new MemoryStream(container)));
    }

    [Fact]
    public void Возвращает_null_для_файла_неправильного_размера()
    {
        // Меньше минимального заголовка: 47 байт таблицы плюс 7 записей по 12 байт.
        Assert.Null(HbkArchive.TryReadFileStorage(new MemoryStream(new byte[10])));
        Assert.Null(HbkArchive.TryReadFileStorage(new MemoryStream(new byte[58])));
        Assert.Null(HbkArchive.TryReadFileStorage(new MemoryStream(new byte[130])));
    }

    [Fact]
    public void Возвращает_null_на_мусоре_вместо_контейнера()
    {
        var garbage = new byte[4096];
        Array.Fill(garbage, (byte)0xFF);

        Assert.Null(HbkArchive.TryReadFileStorage(new MemoryStream(garbage)));
    }

    [Fact]
    public void Устойчив_к_битому_заголовку_блока()
    {
        var container = HbkTestWriter.Create(TwoTopics, new HbkWriteOptions { BreakBlockHeader = true });

        Assert.Null(HbkArchive.TryReadFileStorage(new MemoryStream(container)));
    }

    [Fact]
    public void Обрывает_зацикленную_цепочку_блоков()
    {
        const int chunk = 64;
        var plain = HbkArchive.TryReadFileStorage(new MemoryStream(
            HbkTestWriter.Create(TwoTopics, new HbkWriteOptions { BlockChunkSize = chunk })));
        var container = HbkTestWriter.Create(
            TwoTopics,
            new HbkWriteOptions { BlockChunkSize = chunk, CreateChainCycle = true });

        var payload = HbkArchive.TryReadFileStorage(new MemoryStream(container));

        // На повторном адресе разбор останавливается, поэтому каждый блок читается ровно один раз:
        // содержимое совпадает с незацикленным вариантом, а не растёт бесконечно.
        Assert.NotNull(plain);
        Assert.NotNull(payload);
        Assert.Equal(plain.Length, payload.Length);
    }

    [Fact]
    public void ReadFileStorage_сообщает_о_непонятном_формате()
    {
        var exception = Assert.Throws<InvalidDataException>(() =>
            HbkArchive.ReadFileStorage(new MemoryStream(new byte[100])));

        Assert.Contains("FileStorage", exception.Message, StringComparison.Ordinal);
    }

    private static IReadOnlyList<HbkTopic> ReadTopics(byte[] container)
    {
        var payload = HbkArchive.ReadFileStorage(new MemoryStream(container));
        using var zip = new MemoryStream(payload, writable: false);
        return HbkTopicReader.ReadTopics(zip);
    }
}
