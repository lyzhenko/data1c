using System.Collections.Frozen;
using System.Text;
using Data1c.Core.Metadata;

namespace Data1c.Core.Bsl;

/// <summary>Ссылка на таблицу метаданных, найденную в тексте запроса 1С.</summary>
/// <param name="Kind">Вид объекта метаданных.</param>
/// <param name="ObjectName">Имя объекта («Товары»).</param>
/// <param name="Collection">Имя коллекции так, как записано в запросе: «Справочник» или «Справочники».</param>
/// <param name="Line">Номер строки модуля (1-based); для отдельного текста запроса — номер строки внутри него.</param>
/// <param name="Text">Фрагмент запроса: ключевое слово и имя таблицы, например «ИЗ Справочник.Товары КАК Т».</param>
public sealed record BslQueryReference(MdKind Kind, string ObjectName, string Collection, int Line, string Text);

/// <summary>
/// Разбор текстов запросов на языке 1С: находит обращения к таблицам метаданных после ключевых слов
/// «ИЗ», «СОЕДИНЕНИЕ» (включая «ЛЕВОЕ», «ПРАВОЕ», «ВНУТРЕННЕЕ», «ПОЛНОЕ» и «ВНЕШНЕЕ»), «ПОМЕСТИТЬ»
/// и «ОБЪЕДИНИТЬ».
/// </summary>
/// <remarks>
/// <para>Разбор намеренно поверхностный: список полей, условия и группировки не нужны, важно только
/// найти таблицы-источники. Поэтому текст запроса просматривается одним проходом по словам, без
/// построения дерева разбора, и разборщик никогда не бросает исключений на некорректном тексте.</para>
/// <para>Что учитывается: строки модуля с продолжением через «|» (лексер отдаёт такую строку одним
/// токеном вместе с номером строки открывающей кавычки); псевдонимы «КАК»; временные таблицы
/// «ПОМЕСТИТЬ ВТ_…» (имя запоминается и не считается объектом метаданных); виртуальные таблицы
/// «РегистрСведений.Курсы.СрезПоследних(…)» (берётся базовое имя таблицы); единственное и
/// множественное число коллекции. Соответствие «коллекция → <see cref="MdKind"/>» берётся из
/// <see cref="MdNaming"/>, своей таблицы видов здесь нет: собственная таблица только у имён
/// коллекций в единственном числе, которых в <see cref="MdNaming"/> нет по определению.</para>
/// <para>Ограничения: строка-шаблон запроса, собранная из нескольких литералов через
/// «СтрШаблон» или сложение, не склеивается — находится только то, что видно в одном литерале;
/// таблица, переданная параметром («ИЗ &amp;ИмяТаблицы»), не разрешается.</para>
/// </remarks>
public static class QueryParser
{
    /// <summary>Ключевое слово «ИЗ».</summary>
    private const string From = "ИЗ";

    /// <summary>Ключевое слово «СОЕДИНЕНИЕ».</summary>
    private const string Join = "СОЕДИНЕНИЕ";

    /// <summary>Ключевое слово «ПОМЕСТИТЬ»: после него стоит имя временной таблицы, а не объект метаданных.</summary>
    private const string Into = "ПОМЕСТИТЬ";

    /// <summary>Ключевое слово «ОБЪЕДИНИТЬ».</summary>
    private const string Union = "ОБЪЕДИНИТЬ";

    /// <summary>Ключевое слово «КАК» (псевдоним таблицы).</summary>
    private const string As = "КАК";

    /// <summary>Слово «ВСЕ» в конструкции «ОБЪЕДИНИТЬ ВСЕ».</summary>
    private const string All = "ВСЕ";

    /// <summary>Слова, стоящие перед «СОЕДИНЕНИЕ»; используются только для текста фрагмента.</summary>
    private static readonly string[] JoinModifiers = ["ЛЕВОЕ", "ПРАВОЕ", "ВНУТРЕННЕЕ", "ПОЛНОЕ", "ВНЕШНЕЕ"];

    /// <summary>
    /// Имена коллекций, которые встречаются в запросах, в единственном числе → имя коллекции
    /// в <see cref="MdNaming"/> (множественное число). Вид объекта берётся из MdNaming.
    /// </summary>
    private static readonly (string Singular, string Plural)[] QueryCollectionNames =
    [
        ("Справочник", "Справочники"),
        ("Документ", "Документы"),
        ("ЖурналДокументов", "ЖурналыДокументов"),
        ("Перечисление", "Перечисления"),
        ("Константа", "Константы"),
        ("РегистрСведений", "РегистрыСведений"),
        ("РегистрНакопления", "РегистрыНакопления"),
        ("РегистрБухгалтерии", "РегистрыБухгалтерии"),
        ("РегистрРасчета", "РегистрыРасчета"),
        ("БизнесПроцесс", "БизнесПроцессы"),
        ("Задача", "Задачи"),
        ("ПланСчетов", "ПланыСчетов"),
        ("ПланВидовХарактеристик", "ПланыВидовХарактеристик"),
        ("ПланВидовРасчета", "ПланыВидовРасчета"),
        ("ПланОбмена", "ПланыОбмена"),
        ("Последовательность", "Последовательности"),
        ("Обработка", "Обработки"),
        ("Отчет", "Отчеты"),
        ("ВнешнийИсточникДанных", "ВнешниеИсточникиДанных"),
    ];

    /// <summary>Имя коллекции в запросе («Справочник», «Справочники») → вид объекта метаданных.</summary>
    private static readonly FrozenDictionary<string, MdKind> Collections = BuildCollections();

    /// <summary>Коллекции <see cref="MdNaming"/>, для которых не задано имя в единственном числе.</summary>
    private static readonly FrozenSet<string> Unmapped = BuildUnmapped();

    /// <summary>
    /// Коллекции <see cref="MdNaming"/>, для которых в разборщике нет имени в единственном числе:
    /// таблицы таких коллекций в тексте запроса распознаются только по множественному числу.
    /// Список нужен, чтобы ограничение было видно, а не терялось молча.
    /// </summary>
    public static IReadOnlyCollection<string> UnmappedCollections => Unmapped;

    /// <summary>Находит таблицы метаданных в отдельном тексте запроса.</summary>
    /// <param name="queryText">
    /// Текст запроса. Обрамляющие кавычки можно не убирать: строковый литерал BSL разбирается правильно,
    /// а служебный символ «|» продолжения строки отбрасывается.
    /// </param>
    /// <param name="startLine">Номер строки, с которой начинается текст: для запроса из модуля — строка открывающей кавычки.</param>
    /// <returns>Найденные таблицы в порядке появления; для текста без таблиц — пустой список.</returns>
    public static IReadOnlyList<BslQueryReference> ParseQuery(string queryText, int startLine = 1)
    {
        if (string.IsNullOrWhiteSpace(queryText))
        {
            return [];
        }

        // Текст с обрамляющей кавычкой — литерал BSL: его нужно очистить так же, как строку модуля.
        var trimmed = queryText.AsSpan().TrimStart();
        var result = new List<BslQueryReference>();
        Scan(ReadContent(queryText.AsSpan(), literal: trimmed.Length > 0 && trimmed[0] == '"'), startLine, result);
        return result;
    }

    /// <summary>Находит таблицы метаданных в тексте всего модуля BSL.</summary>
    /// <param name="moduleText">Текст модуля.</param>
    /// <returns>Найденные таблицы с номерами строк модуля.</returns>
    /// <remarks>
    /// Строки модуля разбираются тем же лексером, что и структурный разбор модуля, поэтому номера строк
    /// и многострочные литералы с «|» трактуются одинаково.
    /// </remarks>
    public static IReadOnlyList<BslQueryReference> ParseModule(string moduleText)
    {
        var result = new List<BslQueryReference>();
        CollectFromTokens(BslLexer.Tokenize(moduleText), result);
        return result;
    }

    /// <summary>
    /// Добавляет в <paramref name="result"/> таблицы из строковых литералов готового списка токенов
    /// модуля: отдельная лексическая разметка не нужна, поэтому разбор не удорожает проход.
    /// </summary>
    /// <param name="tokens">Токены модуля, полученные от <see cref="BslLexer"/>.</param>
    /// <param name="result">Список, в который добавляются найденные ссылки в порядке следования строк.</param>
    internal static void CollectFromTokens(IReadOnlyList<BslToken> tokens, List<BslQueryReference> result)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(result);

        foreach (var token in tokens)
        {
            // Дешёвая проверка по сырому тексту отсекает обычные строки: разбор идёт только по запросам.
            if (token.Kind != BslTokenKind.String || !LooksLikeQuery(token.Span))
            {
                continue;
            }

            Scan(ReadContent(token.Span, literal: true), token.Line, result);
        }
    }

    /// <summary>Ищет таблицы в уже очищенном тексте запроса; временные таблицы видны только внутри одного запроса.</summary>
    private static void Scan(string content, int startLine, List<BslQueryReference> result)
    {
        var tokens = Tokenize(content, startLine);
        var temporary = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < tokens.Count; i++)
        {
            var text = tokens[i].Text;

            if (IsKeyword(text, Into))
            {
                // «ПОМЕСТИТЬ ВТ_Товары»: имя временной таблицы — не объект метаданных, но его нужно
                // запомнить, чтобы позже не принять «ИЗ ВТ_Товары» за таблицу метаданных.
                if (i + 1 < tokens.Count && IsIdentifier(tokens[i + 1].Text))
                {
                    temporary.Add(tokens[i + 1].Text);
                }

                continue;
            }

            if (!IsKeyword(text, From) && !IsKeyword(text, Join) && !IsKeyword(text, Union))
            {
                continue;
            }

            var keyword = TableKeyword(tokens, i);
            var next = i + 1;
            if (IsKeyword(text, Union) && next < tokens.Count && IsKeyword(tokens[next].Text, All))
            {
                next++;
            }

            if (!TryReadTable(tokens, next, temporary, out var collection, out var objectName, out var kind, out var end))
            {
                continue;
            }

            var alias = ReadAlias(tokens, end + 1);
            var fragment = alias.Length == 0
                ? $"{keyword} {collection}.{objectName}"
                : $"{keyword} {collection}.{objectName} {As} {alias}";

            result.Add(new BslQueryReference(kind, objectName, collection, tokens[next].Line, fragment));
            i = end;
        }
    }

    /// <summary>
    /// Читает имя таблицы с позиции <paramref name="index"/>: «Коллекция.Объект». Виртуальная таблица
    /// («РегистрСведений.Курсы.СрезПоследних») ограничивается первыми двумя элементами цепочки.
    /// </summary>
    private static bool TryReadTable(
        IReadOnlyList<QueryToken> tokens,
        int index,
        HashSet<string> temporary,
        out string collection,
        out string objectName,
        out MdKind kind,
        out int end)
    {
        collection = string.Empty;
        objectName = string.Empty;
        kind = MdKind.Unknown;
        end = index;

        if (index + 1 >= tokens.Count || !IsIdentifier(tokens[index].Text))
        {
            return false;
        }

        // Имя без точки — это временная таблица, параметр или поле, но не таблица метаданных.
        if (tokens[index + 1].Text != "." || index + 2 >= tokens.Count || !IsIdentifier(tokens[index + 2].Text))
        {
            return false;
        }

        var name = tokens[index].Text;
        if (temporary.Contains(name) || !Collections.TryGetValue(name, out var resolved) || resolved == MdKind.MetadataRoot)
        {
            return false;
        }

        collection = name;
        objectName = tokens[index + 2].Text;
        kind = resolved;
        end = index + 2;
        return true;
    }

    /// <summary>Псевдоним таблицы после «КАК», если он есть.</summary>
    private static string ReadAlias(IReadOnlyList<QueryToken> tokens, int index) =>
        index + 1 < tokens.Count && IsKeyword(tokens[index].Text, As) && IsIdentifier(tokens[index + 1].Text)
            ? tokens[index + 1].Text
            : string.Empty;

    /// <summary>Ключевое слово таблицы вместе с уточнением соединения: «ИЗ», «ЛЕВОЕ ВНЕШНЕЕ СОЕДИНЕНИЕ».</summary>
    private static string TableKeyword(IReadOnlyList<QueryToken> tokens, int index)
    {
        var parts = new List<string>(3) { tokens[index].Text };
        var i = index - 1;
        while (i >= 0 && parts.Count < 3 && JoinModifiers.Contains(tokens[i].Text, StringComparer.OrdinalIgnoreCase))
        {
            parts.Insert(0, tokens[i].Text);
            i--;
        }

        return string.Join(' ', parts);
    }

    /// <summary>
    /// Разбирает текст запроса на слова, точки и параметры. Строки и комментарии запроса пропускаются
    /// целиком (слово «ИЗ» внутри строки таблицей не считается), а переводы строк сдвигают нумерацию.
    /// </summary>
    private static List<QueryToken> Tokenize(string text, int startLine)
    {
        var tokens = new List<QueryToken>(Math.Max(8, text.Length / 8));
        var line = startLine;
        var i = 0;

        while (i < text.Length)
        {
            var c = text[i];
            if (c == '\n')
            {
                line++;
                i++;
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            if (c == '"')
            {
                i++;
                while (i < text.Length)
                {
                    if (text[i] == '\n')
                    {
                        line++;
                    }

                    if (text[i] == '"')
                    {
                        if (i + 1 < text.Length && text[i + 1] == '"')
                        {
                            i += 2;
                            continue;
                        }

                        i++;
                        break;
                    }

                    i++;
                }

                continue;
            }

            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                while (i < text.Length && text[i] != '\n')
                {
                    i++;
                }

                continue;
            }

            if (c == '.')
            {
                tokens.Add(new QueryToken(".", line));
                i++;
                continue;
            }

            if (c == '&')
            {
                // Параметр запроса целиком: «&ИмяТаблицы» — не имя коллекции.
                var start = i;
                i++;
                while (i < text.Length && BslLexer.IsIdentifierPart(text[i]))
                {
                    i++;
                }

                tokens.Add(new QueryToken(text[start..i], line));
                continue;
            }

            if (char.IsLetter(c) || c == '_')
            {
                var start = i;
                while (i < text.Length && BslLexer.IsIdentifierPart(text[i]))
                {
                    i++;
                }

                tokens.Add(new QueryToken(text[start..i], line));
                continue;
            }

            if (char.IsAsciiDigit(c))
            {
                while (i < text.Length && (char.IsAsciiDigit(text[i]) || text[i] == ','))
                {
                    i++;
                }

                continue;
            }

            // Скобки, запятые, знаки операций и прочие символы для поиска таблиц не нужны.
            i++;
        }

        return tokens;
    }

    /// <summary>
    /// Приводит текст к виду, пригодному для разбора: снимает обрамляющие кавычки литерала,
    /// раскрывает удвоенные кавычки, отбрасывает «|» продолжения строки и оставляет по одному
    /// переводу строки на каждую физическую строку — по ним считается номер строки.
    /// </summary>
    private static string ReadContent(ReadOnlySpan<char> text, bool literal)
    {
        var start = literal && text.Length > 0 && text[0] == '"' ? 1 : 0;
        var end = text.Length;
        if (literal && end > start && text[end - 1] == '"')
        {
            end--;
        }

        var builder = new StringBuilder(end - start);
        var afterNewLine = false;
        for (var i = start; i < end; i++)
        {
            var c = text[i];
            if (literal && c == '"' && i + 1 < end && text[i + 1] == '"')
            {
                builder.Append('"');
                afterNewLine = false;
                i++;
                continue;
            }

            if (c == '\r' || c == '\n')
            {
                if (!afterNewLine)
                {
                    builder.Append('\n');
                    afterNewLine = true;
                }

                continue;
            }

            if (c == '|')
            {
                continue;
            }

            builder.Append(c);
            afterNewLine = false;
        }

        return builder.ToString();
    }

    /// <summary>
    /// Быстрая проверка литерала перед полным разбором: есть ли в нём ключевое слово запроса
    /// отдельным словом. Отсекает обычные строки модуля, которых большинство.
    /// </summary>
    private static bool LooksLikeQuery(ReadOnlySpan<char> text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (!char.IsLetter(text[i]))
            {
                continue;
            }

            var start = i;
            while (i < text.Length && BslLexer.IsIdentifierPart(text[i]))
            {
                i++;
            }

            var word = text[start..i];
            if (word.Equals(From, StringComparison.OrdinalIgnoreCase) ||
                word.Equals(Join, StringComparison.OrdinalIgnoreCase) ||
                word.Equals(Into, StringComparison.OrdinalIgnoreCase) ||
                word.Equals(Union, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsKeyword(string text, string keyword) => text.Equals(keyword, StringComparison.OrdinalIgnoreCase);

    /// <summary>Слово языка запросов: с буквы или «_»; параметры («&amp;Имя») словами не считаются.</summary>
    private static bool IsIdentifier(string text) =>
        text.Length > 0 && (char.IsLetter(text[0]) || text[0] == '_');

    private static FrozenDictionary<string, MdKind> BuildCollections()
    {
        var map = new Dictionary<string, MdKind>(StringComparer.OrdinalIgnoreCase);
        foreach (var (singular, plural) in QueryCollectionNames)
        {
            if (MdNaming.TryKindFromCollection(plural, out var kind) && kind != MdKind.MetadataRoot)
            {
                map[singular] = kind;
            }
        }

        // Множественное число принимается так же, как в коде: вид берётся прямо из MdNaming.
        foreach (var collection in MdNaming.KnownCollections)
        {
            if (MdNaming.TryKindFromCollection(collection, out var kind) && kind != MdKind.MetadataRoot)
            {
                map[collection] = kind;
            }
        }

        return map.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    private static FrozenSet<string> BuildUnmapped()
    {
        var mapped = QueryCollectionNames
            .Where(static pair => Collections.ContainsKey(pair.Singular))
            .Select(static pair => pair.Plural)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return MdNaming.KnownCollections
            .Where(collection => !mapped.Contains(collection) &&
                                 MdNaming.TryKindFromCollection(collection, out var kind) &&
                                 kind != MdKind.MetadataRoot)
            .OrderBy(static collection => collection, StringComparer.Ordinal)
            .ToFrozenSet(StringComparer.Ordinal);
    }

    /// <summary>Слово запроса или точка; номер строки считается от начала текста запроса.</summary>
    private readonly record struct QueryToken(string Text, int Line);
}
