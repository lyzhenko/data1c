namespace Data1c.Core.Bsl;

/// <summary>
/// Мелкие операции над потоком токенов BSL, общие для таблицы символов и вывода типов:
/// пропуск комментариев и переводов строк, распознавание ключевых слов, определение начала
/// оператора и разбор списка аргументов вызова.
/// </summary>
/// <remarks>
/// Дерево выражений не строится: разбор идёт по токенам и сознательно ограничен. Из-за этого
/// результат консервативен — нераспознанное выражение считается неизвестным, а не угадывается.
/// </remarks>
internal static class BslTokenScan
{
    /// <summary>Токен не влияет на разбор: комментарий или перевод строки.</summary>
    public static bool IsTrivia(BslToken token) =>
        token.Kind is BslTokenKind.Comment or BslTokenKind.NewLine;

    /// <summary>Индекс следующего значимого токена или -1, если значимых токенов больше нет.</summary>
    public static int NextMeaningful(IReadOnlyList<BslToken> tokens, int index)
    {
        for (var i = Math.Max(0, index); i < tokens.Count; i++)
        {
            if (!IsTrivia(tokens[i]))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Текст токена равен ключевому слову без учёта регистра.</summary>
    public static bool IsKeyword(BslToken token, string keyword) =>
        token.Kind is BslTokenKind.Keyword or BslTokenKind.Identifier &&
        token.Span.Equals(keyword, StringComparison.OrdinalIgnoreCase);

    /// <summary>Токен — оператор с указанным текстом.</summary>
    public static bool IsOperator(BslToken token, string text) =>
        token.Kind == BslTokenKind.Operator && token.Span.Equals(text, StringComparison.Ordinal);

    /// <summary>Открывающая скобка любого вида.</summary>
    public static bool IsOpening(BslToken token) =>
        IsOperator(token, "(") || IsOperator(token, "[") || IsOperator(token, "{");

    /// <summary>Закрывающая скобка любого вида.</summary>
    public static bool IsClosing(BslToken token) =>
        IsOperator(token, ")") || IsOperator(token, "]") || IsOperator(token, "}");

    /// <summary>
    /// Токен начинает самостоятельный оператор. Имя переменной в начале оператора — это левая часть
    /// присваивания, а имя внутри выражения (аргумент, условие, элемент списка) — нет.
    /// </summary>
    public static bool IsStatementStart(IReadOnlyList<BslToken> tokens, int index)
    {
        var previous = index - 1;
        while (previous >= 0 && tokens[previous].Kind == BslTokenKind.Comment)
        {
            previous--;
        }

        if (previous < 0)
        {
            return true;
        }

        var token = tokens[previous];
        if (token.Kind == BslTokenKind.NewLine)
        {
            return true;
        }

        if (IsOperator(token, ";"))
        {
            return true;
        }

        // Оператор может начинаться сразу за словом «Тогда», «Иначе», «Цикл», «Попытка», «Исключение» —
        // например, в однострочной записи «Если А Тогда Б = 1; КонецЕсли».
        return token.Kind == BslTokenKind.Keyword &&
               (IsKeyword(token, "Тогда") || IsKeyword(token, "Иначе") || IsKeyword(token, "Цикл") ||
                IsKeyword(token, "Попытка") || IsKeyword(token, "Исключение"));
    }

    /// <summary>Индекс токена сразу за парной закрывающей скобкой или -1, если пара не найдена.</summary>
    public static int AfterMatchingBracket(IReadOnlyList<BslToken> tokens, int openIndex)
    {
        var depth = 0;
        for (var i = openIndex; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (token.Kind != BslTokenKind.Operator)
            {
                continue;
            }

            if (IsOpening(token))
            {
                depth++;
            }
            else if (IsClosing(token))
            {
                depth--;
                if (depth <= 0)
                {
                    return i + 1;
                }
            }
        }

        return -1;
    }

    /// <summary>
    /// Разбирает список аргументов вызова от открывающей скобки. Для каждого аргумента возвращает
    /// имя переменной, если аргумент — ровно один идентификатор, иначе <see langword="null"/>:
    /// выражения не разбираются, поэтому передать их тип нельзя.
    /// </summary>
    /// <param name="tokens">Поток токенов модуля.</param>
    /// <param name="openIndex">Индекс открывающей скобки вызова.</param>
    /// <returns>Имена аргументов и индекс токена за закрывающей скобкой.</returns>
    public static (List<string?> Arguments, int Next) ParseArguments(IReadOnlyList<BslToken> tokens, int openIndex)
    {
        var arguments = new List<string?>();
        var current = new List<BslToken>();
        var depth = 0;

        for (var i = openIndex; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (token.Kind == BslTokenKind.Operator)
            {
                if (IsOpening(token))
                {
                    depth++;
                    if (depth > 1)
                    {
                        current.Add(token);
                    }

                    continue;
                }

                if (IsClosing(token))
                {
                    depth--;
                    if (depth == 0)
                    {
                        if (current.Count > 0 || arguments.Count > 0)
                        {
                            AddArgument(arguments, current);
                        }

                        return (arguments, i + 1);
                    }

                    current.Add(token);
                    continue;
                }

                if (depth == 1 && IsOperator(token, ","))
                {
                    AddArgument(arguments, current);
                    current.Clear();
                    continue;
                }
            }

            if (depth >= 1 && !IsTrivia(token))
            {
                current.Add(token);
            }
        }

        AddArgument(arguments, current);
        return (arguments, tokens.Count);
    }

    /// <summary>Аргумент — имя переменной, только если он состоит из одного идентификатора.</summary>
    private static void AddArgument(List<string?> arguments, List<BslToken> tokens) =>
        arguments.Add(tokens is [{ Kind: BslTokenKind.Identifier } single] ? single.GetText() : null);
}
