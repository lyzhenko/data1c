using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Data1c.Core.Platform;

/// <summary>Тема, извлечённая из контейнера .hbk.</summary>
/// <param name="Name">Нормализованное имя темы (точечный путь).</param>
/// <param name="Path">Исходный путь внутри контейнера.</param>
/// <param name="Title">Заголовок.</param>
/// <param name="Text">Текст без разметки.</param>
public sealed record HbkTopic(string Name, string Path, string Title, string Text);

/// <summary>
/// Чтение контейнера <c>.hbk</c> — недокументированного формата справочной системы 1С
/// (синтакс-помощник, описание языка, описание языка запросов).
/// </summary>
/// <remarks>
/// <para>Устройство файла:</para>
/// <list type="number">
/// <item>со смещения <see cref="TableOffset"/> идут <see cref="TableEntries"/> записей по
/// <see cref="EntrySize"/> байт: два <c>Int32</c> — адрес записи с именем файла и адрес начала
/// цепочки блоков с его содержимым;</item>
/// <item>имя файла записано в UTF-16LE по адресу <c>recordAddress + <see cref="NameOffsetInRecord"/></c>
/// до нулевого символа; ищется запись с именем <c>FileStorage</c>;</item>
/// <item>содержимое — цепочка блоков: заголовок блока занимает <see cref="BlockHeaderSize"/> байт ASCII,
/// где поля [2..10), [11..19) и [20..28) — шестнадцатеричные числа: объявленный размер всей записи
/// (заполнен только в первом блоке), размер данных этого блока и адрес следующего блока;</item>
/// <item>данные блока читаются по <b>второму</b> полю. Первое поле в многофайловых контейнерах больше
/// размера блока (это размер всей записи), поэтому чтение «по первому полю» собирает мусор:
/// так, в 8.3.27 у <c>shlang_ru.hbk</c> первое поле 68 634 при размере блока 42 549. Признак конца
/// цепочки — нулевой адрес или <c>7FFFFFFF</c>;</item>
/// <item>склеенная полезная нагрузка — ZIP-архив с HTML-темами.</item>
/// </list>
/// <para>Разбор устойчив к мусору: битые заголовки, выход за границы и циклы в цепочке
/// прекращают чтение, а не приводят к исключению.</para>
/// </remarks>
public static class HbkArchive
{
    /// <summary>Смещение таблицы записей в начале файла.</summary>
    internal const int TableOffset = 47;

    /// <summary>Число записей в таблице.</summary>
    internal const int TableEntries = 7;

    /// <summary>Размер одной записи таблицы.</summary>
    internal const int EntrySize = 12;

    /// <summary>Размер заголовка блока в цепочке.</summary>
    internal const int BlockHeaderSize = 31;

    /// <summary>Смещение имени файла внутри записи, на которую ссылается таблица.</summary>
    internal const int NameOffsetInRecord = BlockHeaderSize + 20;

    /// <summary>Ограничение длины имени файла в записи.</summary>
    internal const int MaxFileNameLength = 200;

    /// <summary>Защита от бесконечной цепочки блоков.</summary>
    internal const int MaxBlocks = 100_000;

    /// <summary>Имя записи, в которой лежит содержимое справочника.</summary>
    internal const string FileStorageName = "FileStorage";

    /// <summary>
    /// Достаёт из контейнера вложенный ZIP-архив с темами.
    /// </summary>
    /// <returns>Байты ZIP-архива или <see langword="null"/>, если это не .hbk или запись FileStorage не найдена.</returns>
    public static byte[]? TryReadFileStorage(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var data = ReadAll(stream);
        if (data.Length < TableOffset + TableEntries * EntrySize)
        {
            return null;
        }

        for (var index = 0; index < TableEntries; index++)
        {
            var entryOffset = TableOffset + index * EntrySize;
            var recordAddress = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(entryOffset));
            var dataAddress = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(entryOffset + 4));
            if (recordAddress <= 0 || recordAddress >= data.Length || dataAddress <= 0 || dataAddress >= data.Length)
            {
                continue;
            }

            var name = ReadRecordName(data, recordAddress);
            if (!string.Equals(name, FileStorageName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var payload = ReadBlockChain(data, dataAddress);
            return payload.Length > 0 ? payload : null;
        }

        return null;
    }

    /// <summary>Достаёт вложенный ZIP-архив или сообщает, что формат не распознан.</summary>
    public static byte[] ReadFileStorage(Stream stream) =>
        TryReadFileStorage(stream)
        ?? throw new InvalidDataException("В файле нет записи FileStorage: это не .hbk или формат изменился.");

    private static byte[] ReadAll(Stream stream)
    {
        if (stream is MemoryStream memory && memory.Position == 0 && memory.TryGetBuffer(out var segment) && segment.Offset == 0)
        {
            return segment.Array!.Length == segment.Count ? segment.Array! : memory.ToArray();
        }

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static string? ReadRecordName(byte[] data, int recordAddress)
    {
        var nameAddress = recordAddress + NameOffsetInRecord;
        if (nameAddress <= 0 || nameAddress + 2 > data.Length)
        {
            return null;
        }

        var name = new StringBuilder(64);
        for (var offset = nameAddress; offset + 1 < data.Length && name.Length < MaxFileNameLength; offset += 2)
        {
            var code = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset));
            if (code == 0)
            {
                break;
            }

            name.Append((char)code);
        }

        return name.Length == 0 ? null : name.ToString();
    }

    private static byte[] ReadBlockChain(byte[] data, int startAddress)
    {
        using var payload = new MemoryStream();
        var address = startAddress;
        var visited = new HashSet<int>();

        for (var step = 0; step < MaxBlocks; step++)
        {
            if (address <= 0 || address + BlockHeaderSize > data.Length || !visited.Add(address))
            {
                break;
            }

            if (!TryParseBlockHeader(data, address, out _, out var blockSize, out var nextAddress) || blockSize < 0)
            {
                break;
            }

            var contentAddress = address + BlockHeaderSize;
            var available = data.Length - contentAddress;
            var take = Math.Min(blockSize, available);
            if (take > 0)
            {
                payload.Write(data, contentAddress, take);
            }

            if (nextAddress == int.MaxValue || nextAddress <= 0)
            {
                break;
            }

            address = nextAddress;
        }

        return payload.ToArray();
    }

    private static bool TryParseBlockHeader(byte[] data, int address, out int declaredSize, out int blockSize, out int nextAddress)
    {
        declaredSize = 0;
        blockSize = 0;
        nextAddress = 0;

        var header = Encoding.ASCII.GetString(data, address, BlockHeaderSize);
        return TryParseHex(header.AsSpan(2, 8), out declaredSize)
            && TryParseHex(header.AsSpan(11, 8), out blockSize)
            && TryParseHex(header.AsSpan(20, 8), out nextAddress);
    }

    private static bool TryParseHex(ReadOnlySpan<char> value, out int result)
    {
        result = 0;
        var text = value.Trim();
        return text.Length > 0
            && int.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out result);
    }
}
