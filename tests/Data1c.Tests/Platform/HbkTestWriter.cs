using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Data1c.Tests.Platform;

/// <summary>Настройки сборки синтетического контейнера .hbk для тестов.</summary>
internal sealed record HbkWriteOptions
{
    /// <summary>Не создавать запись FileStorage (файл не похож на .hbk).</summary>
    public bool OmitFileStorageRecord { get; init; }

    /// <summary>Испортить заголовок блока, чтобы разбор не смог его прочитать.</summary>
    public bool BreakBlockHeader { get; init; }

    /// <summary>Замкнуть цепочку блоков саму на себя.</summary>
    public bool CreateChainCycle { get; init; }

    /// <summary>Разбивать содержимое на блоки указанного размера (0 — один блок).</summary>
    public int BlockChunkSize { get; init; }

    /// <summary>Имя файла в записи (по умолчанию FileStorage).</summary>
    public string Name { get; init; } = "FileStorage";
}

/// <summary>
/// Собирает контейнер .hbk по тому же формату, который читает <c>HbkArchive</c>:
/// таблица записей со смещения 47, запись с именем FileStorage и цепочка блоков с ZIP-архивом тем.
/// </summary>
internal static class HbkTestWriter
{
    private const int TableOffset = 47;
    private const int TableEntries = 7;
    private const int EntrySize = 12;
    private const int BlockHeaderSize = 31;
    private const int NameOffsetInRecord = BlockHeaderSize + 20;

    public static byte[] Create(IReadOnlyList<(string Path, string Html)> topics, HbkWriteOptions? options = null)
    {
        options ??= new HbkWriteOptions();
        var zip = BuildZip(topics);
        var blocks = SplitIntoBlocks(zip, options.BlockChunkSize);

        var recordAddress = TableOffset + TableEntries * EntrySize;
        var nameBytes = Encoding.Unicode.GetBytes(options.Name);
        var firstBlockAddress = recordAddress + NameOffsetInRecord + nameBytes.Length + 2;

        var blockAddresses = new List<int>(blocks.Count);
        var cursor = firstBlockAddress;
        foreach (var block in blocks)
        {
            blockAddresses.Add(cursor);
            cursor += BlockHeaderSize + block.Length;
        }

        var data = new byte[cursor];

        if (!options.OmitFileStorageRecord)
        {
            // Запись таблицы — это пара Int32 в одном 12-байтовом элементе: адрес записи с именем
            // и адрес начала цепочки блоков.
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(TableOffset), recordAddress);
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(TableOffset + 4), firstBlockAddress);
        }

        Array.Copy(nameBytes, 0, data, recordAddress + NameOffsetInRecord, nameBytes.Length);

        for (var index = 0; index < blocks.Count; index++)
        {
            var address = blockAddresses[index];
            var next = index + 1 < blocks.Count ? blockAddresses[index + 1] : int.MaxValue;
            if (options.CreateChainCycle && index == blocks.Count - 1 && blocks.Count > 1)
            {
                next = blockAddresses[0];
            }

            // Поля заголовка как в реальных файлах: объявленный размер всей записи заполнен только
            // в первом блоке, размер данных блока — в каждом, дальше адрес следующего блока.
            var declaredSize = index == 0 ? zip.Length : 0;
            var header = options.BreakBlockHeader
                ? new string('Z', BlockHeaderSize)
                : BuildBlockHeader(declaredSize, blocks[index].Length, next);

            Encoding.ASCII.GetBytes(header, data.AsSpan(address, BlockHeaderSize));
            Array.Copy(blocks[index], 0, data, address + BlockHeaderSize, blocks[index].Length);
        }

        return data;
    }

    private static string BuildBlockHeader(int declaredSize, int blockSize, int nextAddress) =>
        "00"
        + declaredSize.ToString("X8")
        + "0"
        + blockSize.ToString("X8")
        + "0"
        + nextAddress.ToString("X8")
        + "000";

    private static List<byte[]> SplitIntoBlocks(byte[] payload, int chunkSize)
    {
        if (chunkSize <= 0 || payload.Length <= chunkSize)
        {
            return [payload];
        }

        var blocks = new List<byte[]>();
        for (var offset = 0; offset < payload.Length; offset += chunkSize)
        {
            var length = Math.Min(chunkSize, payload.Length - offset);
            var block = new byte[length];
            Array.Copy(payload, offset, block, 0, length);
            blocks.Add(block);
        }

        return blocks;
    }

    private static byte[] BuildZip(IReadOnlyList<(string Path, string Html)> topics)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, html) in topics)
            {
                var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
                using var stream = entry.Open();
                using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                writer.Write(html);
            }
        }

        return buffer.ToArray();
    }
}
