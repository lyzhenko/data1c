using System.Text;

namespace Data1c.Core.Dump;

/// <summary>
/// Устойчивое чтение текстовых файлов выгрузки 1С: UTF-8 с BOM и без BOM, а при
/// нарушении UTF-8 — запасная однобайтовая кодировка CP1251.
/// </summary>
/// <remarks>
/// Большинство выгрузок «Выгрузить конфигурацию в файлы» пишет модули и XML в UTF-8,
/// но часть инструментов и старые выгрузки используют CP1251. Жёсткое чтение UTF-8
/// превращает такие модули в мусор (или роняет разбор исключением), поэтому байты
/// сначала проверяются на корректность UTF-8, и только потом выбирается кодировка:
/// <list type="number">
///   <item>BOM (UTF-8, UTF-16 LE/BE, UTF-32) — кодировка берётся из метки;</item>
///   <item>строгий UTF-8 — если байты полностью корректны, текст читается как UTF-8;</item>
///   <item>иначе — CP1251: в однобайтовой кодировке декодируется любой набор байтов.</item>
/// </list>
/// Включение CP1251 требует регистрации <see cref="CodePagesEncodingProvider"/>.
/// </remarks>
public static class DumpTextReader
{
    /// <summary>Кодовая страница Windows-1251 (кириллица).</summary>
    private const int Windows1251 = 1251;

    private static readonly Lazy<Encoding> FallbackEncoding = new(CreateFallbackEncoding, isThreadSafe: true);

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// Запасная кодировка для текстов выгрузки, не являющихся корректным UTF-8 (CP1251).
    /// </summary>
    public static Encoding Fallback => FallbackEncoding.Value;

    /// <summary>Читает текст из потока целиком, определяя кодировку по содержимому и BOM.</summary>
    /// <param name="stream">Поток с текстом файла выгрузки. Поток не закрывается.</param>
    /// <returns>Содержимое файла как строка.</returns>
    public static string ReadAllText(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return ReadAllText(ReadAllBytes(stream));
    }

    /// <summary>Читает текст из массива байтов, определяя кодировку по содержимому и BOM.</summary>
    /// <param name="bytes">Байты файла выгрузки.</param>
    /// <returns>Содержимое файла как строка.</returns>
    public static string ReadAllText(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        return ReadAllText(new ReadOnlySpan<byte>(bytes));
    }

    /// <summary>Читает строки файла, определяя кодировку по содержимому и BOM.</summary>
    /// <param name="bytes">Байты файла выгрузки.</param>
    /// <returns>Строки файла без переводов строк.</returns>
    public static string[] ReadLines(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        return SplitLines(ReadAllText(bytes));
    }

    /// <summary>Читает строки из потока, определяя кодировку по содержимому и BOM.</summary>
    /// <param name="stream">Поток с текстом файла выгрузки. Поток не закрывается.</param>
    /// <returns>Строки файла без переводов строк.</returns>
    public static string[] ReadLines(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return SplitLines(ReadAllText(ReadAllBytes(stream)));
    }

    /// <summary>
    /// Создаёт <see cref="TextReader"/> над потоком выгрузки с тем же выбором кодировки:
    /// BOM, затем строгий UTF-8, затем запасная CP1251. Нужен там, где текст разбирается
    /// потоково, а не целиком (например, <see cref="System.Xml.XmlReader"/>).
    /// </summary>
    /// <param name="stream">Поток с текстом файла выгрузки. Поток не закрывается.</param>
    /// <returns>Читатель текста с уже определённой кодировкой.</returns>
    /// <remarks>
    /// Понадобился потому, что <see cref="StreamReader"/> не умеет переключаться на запасную
    /// кодировку: переданная кодировка используется, только если есть BOM, иначе всегда
    /// читается UTF-8. Поэтому кодировка определяется заранее по уже прочитанным байтам
    /// (текстовые файлы выгрузки невелики), а читатель настраивается под неё.
    /// </remarks>
    public static TextReader CreateTextReader(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var bytes = ReadAllBytes(stream);
        var bom = BomSize(bytes);
        var encoding = GetEncoding(bytes, bom);

        // Поток не закрывается: закрывать его — обязанность вызывающего.
        return new StreamReader(new MemoryStream(bytes, writable: false), encoding, detectEncodingFromByteOrderMarks: true);
    }

    /// <summary>Читает текст из буфера байтов, определяя кодировку по содержимому и BOM.</summary>
    /// <param name="bytes">Байты файла выгрузки.</param>
    /// <returns>Содержимое файла как строка.</returns>
    internal static string ReadAllText(ReadOnlySpan<byte> bytes)
    {
        var bom = BomSize(bytes);
        return GetEncoding(bytes, bom).GetString(bytes[bom..]);
    }

    /// <summary>Читает все байты потока (поток не закрывается и не сбрасывается).</summary>
    private static byte[] ReadAllBytes(Stream stream)
    {
        if (stream is MemoryStream memory && memory.Position == 0)
        {
            return memory.ToArray();
        }

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    /// <summary>Разбивает текст на строки, приводя любые переводы строк к «\n».</summary>
    private static string[] SplitLines(string text) =>
        text.Length == 0 ? [] : text.ReplaceLineEndings("\n").Split('\n');

    /// <summary>
    /// Определяет кодировку по BOM и содержимому: BOM имеет приоритет, затем строгий UTF-8,
    /// затем запасная CP1251.
    /// </summary>
    private static Encoding GetEncoding(ReadOnlySpan<byte> bytes, int bom)
    {
        if (bom > 0)
        {
            return DetectByBom(bytes, bom);
        }

        return IsValidUtf8(bytes) ? StrictUtf8 : Fallback;
    }

    /// <summary>Длина BOM в начале буфера (0, если метки нет).</summary>
    private static int BomSize(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return 3;
        }

        if (bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xFE && bytes[2] == 0x00 && bytes[3] == 0x00)
        {
            return 4;
        }

        if (bytes.Length >= 4 && bytes[0] == 0x00 && bytes[1] == 0x00 && bytes[2] == 0xFE && bytes[3] == 0xFF)
        {
            return 4;
        }

        if (bytes.Length >= 2 && ((bytes[0] == 0xFF && bytes[1] == 0xFE) || (bytes[0] == 0xFE && bytes[1] == 0xFF)))
        {
            return 2;
        }

        return 0;
    }

    private static Encoding DetectByBom(ReadOnlySpan<byte> bytes, int bom) => bom switch
    {
        4 when bytes[0] == 0xFF => new UTF32Encoding(bigEndian: false, byteOrderMark: true),
        4 => new UTF32Encoding(bigEndian: true, byteOrderMark: true),
        3 => new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
        2 when bytes[0] == 0xFF => Encoding.Unicode,
        2 => Encoding.BigEndianUnicode,
        _ => Fallback,
    };

    /// <summary>
    /// Строгая проверка, что байты — корректная последовательность UTF-8.
    /// Некорректные последовательности штатный декодер заменяет символом-заменителем без
    /// исключения, поэтому проверка выполняется вручную. Открыта для тестов.
    /// </summary>
    internal static bool IsValidUtf8(ReadOnlySpan<byte> bytes)
    {
        var index = 0;
        while (index < bytes.Length)
        {
            var b = bytes[index];
            if (b < 0x80)
            {
                index++;
                continue;
            }

            int extra;
            int min;
            int code;
            if ((b & 0xE0) == 0xC0)
            {
                extra = 1;
                min = 0x80;
                code = b & 0x1F;
            }
            else if ((b & 0xF0) == 0xE0)
            {
                extra = 2;
                min = 0x800;
                code = b & 0x0F;
            }
            else if ((b & 0xF8) == 0xF0)
            {
                extra = 3;
                min = 0x10000;
                code = b & 0x07;
            }
            else
            {
                return false;
            }

            if (index + extra >= bytes.Length)
            {
                // Последовательность обрывается: считать такие байты UTF-8 нельзя.
                return false;
            }

            for (var i = 1; i <= extra; i++)
            {
                var next = bytes[index + i];
                if ((next & 0xC0) != 0x80)
                {
                    return false;
                }

                code = (code << 6) | (next & 0x3F);
            }

            // Перегруженные последовательности и суррогаты штатный декодер тоже считает
            // ошибкой (для него включён throwOnInvalidBytes), значит нужна запасная кодировка.
            if (code < min || code > 0x10FFFF || (code >= 0xD800 && code <= 0xDFFF))
            {
                return false;
            }

            index += extra + 1;
        }

        return true;
    }

    private static Encoding CreateFallbackEncoding()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(Windows1251);
        }
        catch (Exception exception) when (exception is NotSupportedException or ArgumentException)
        {
            // Провайдер кодовых страниц недоступен (усечённый рантайм): читаем байты
            // как Latin-1, чтобы разбор не падал. Текст при этом будет нечитаемым.
            return Encoding.Latin1;
        }
    }
}
