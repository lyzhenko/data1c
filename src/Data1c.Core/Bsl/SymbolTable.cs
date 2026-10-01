namespace Data1c.Core.Bsl;

/// <summary>Вид объявленного имени в модуле.</summary>
public enum BslSymbolKind
{
    /// <summary>Переменная модуля: <c>Перем X;</c> вне процедур и функций.</summary>
    ModuleVariable,

    /// <summary>Параметр процедуры или функции.</summary>
    Parameter,

    /// <summary>Локальная переменная процедуры или функции.</summary>
    LocalVariable,
}

/// <summary>Область видимости имени.</summary>
public enum BslScopeKind
{
    /// <summary>Весь модуль.</summary>
    Module,

    /// <summary>Процедура или функция.</summary>
    Routine,
}

/// <summary>Объявленное имя модуля: что объявлено, где и в какой области видимости.</summary>
/// <param name="Name">Имя, как записано в исходном тексте.</param>
/// <param name="Kind">Вид имени.</param>
/// <param name="Routine">Процедура или функция-владелец; <see langword="null"/> для переменной модуля.</param>
/// <param name="Line">Строка объявления (1-based).</param>
/// <param name="Column">Позиция в строке (1-based).</param>
/// <param name="IsExport">Признак <c>Экспорт</c> (у переменной модуля).</param>
/// <param name="IsByValue">Параметр объявлен со словом <c>Знач</c>.</param>
/// <param name="IsImplicit">Имя не объявлено словом <c>Перем</c>, а появилось из присваивания или переменной цикла.</param>
public sealed record BslSymbol(
    string Name,
    BslSymbolKind Kind,
    string? Routine,
    int Line,
    int Column,
    bool IsExport = false,
    bool IsByValue = false,
    bool IsImplicit = false)
{
    /// <summary>Имя объявлено внутри процедуры или функции.</summary>
    public bool IsLocal => Kind != BslSymbolKind.ModuleVariable;

    /// <summary>Область видимости имени.</summary>
    public BslScopeKind Scope => Kind == BslSymbolKind.ModuleVariable ? BslScopeKind.Module : BslScopeKind.Routine;

    public override string ToString() => $"{Kind} {Name} ({Line}:{Column})";
}

/// <summary>Область видимости модуля: сам модуль или процедура (функция) с границами по строкам.</summary>
/// <param name="Routine">Имя процедуры или функции; <see langword="null"/> для области модуля.</param>
/// <param name="Kind">Вид области.</param>
/// <param name="StartLine">Первая строка области (включая заголовок).</param>
/// <param name="EndLine">Последняя строка области (включая <c>КонецПроцедуры</c>).</param>
/// <param name="Parameters">Имена параметров в порядке объявления.</param>
public sealed record BslScopeInfo(
    string? Routine,
    BslScopeKind Kind,
    int StartLine,
    int EndLine,
    IReadOnlyList<string> Parameters)
{
    /// <summary>Строка входит в область.</summary>
    public bool Contains(int line) => line >= StartLine && line <= EndLine;
}

/// <summary>
/// Таблица символов модуля BSL: переменные модуля, параметры и локальные переменные процедур
/// и функций с позициями и областями видимости.
/// </summary>
/// <remarks>
/// <para>Правила видимости:</para>
/// <list type="bullet">
/// <item>переменная модуля видна на любой строке модуля;</item>
/// <item>параметр виден в своей процедуре (функции) целиком;</item>
/// <item>локальная переменная видна со строки объявления до конца своей процедуры (функции)
/// и не видна в других процедурах.</item>
/// </list>
/// <para>Имена делятся на объявленные словом <c>Перем</c> (и параметры) и неявные
/// (<see cref="BslSymbol.IsImplicit"/>): к неявным относятся переменные, которым присвоено значение
/// без объявления, и переменные циклов <c>Для</c>. Так таблица остаётся полезной и на коде,
/// где объявления пропущены, но вызывающий код всегда видит признак неявности.</para>
/// </remarks>
public sealed class SymbolTable
{
    /// <summary>
    /// Сколько областей просматривается назад, если строка не попала в найденную область.
    /// Перекрытие областей бывает только на недопустимом коде (вложенные процедуры), поэтому
    /// просмотр ограничен: иначе поиск области перестал бы быть логарифмическим.
    /// </summary>
    private const int MaxScopeBacktrack = 8;

    private readonly List<BslSymbol> _symbols;
    private readonly List<BslScopeInfo> _scopes;
    private readonly BslScopeInfo _moduleScope;
    private readonly Dictionary<string, List<BslSymbol>> _byName = new(StringComparer.OrdinalIgnoreCase);

    private SymbolTable(List<BslSymbol> symbols, List<BslScopeInfo> scopes, int lineCount)
    {
        _symbols = symbols;
        _scopes = scopes;
        _moduleScope = new BslScopeInfo(null, BslScopeKind.Module, 1, Math.Max(1, lineCount), []);

        foreach (var symbol in symbols)
        {
            if (!_byName.TryGetValue(symbol.Name, out var list))
            {
                list = [];
                _byName[symbol.Name] = list;
            }

            list.Add(symbol);
        }
    }

    /// <summary>Все найденные имена в порядке появления в тексте.</summary>
    public IReadOnlyList<BslSymbol> Symbols => _symbols;

    /// <summary>Области процедур и функций.</summary>
    public IReadOnlyList<BslScopeInfo> Scopes => _scopes;

    /// <summary>Область модуля целиком.</summary>
    public BslScopeInfo ModuleScope => _moduleScope;

    /// <summary>Переменные модуля.</summary>
    public IReadOnlyList<BslSymbol> ModuleVariables =>
        [.. _symbols.Where(static symbol => symbol.Kind == BslSymbolKind.ModuleVariable)];

    /// <summary>
    /// Собирает таблицу символов по тексту модуля. Разбор модуля выполняется заново,
    /// поэтому для уже разобранного модуля используйте <see cref="BslModuleInfo.Symbols"/>.
    /// </summary>
    public static SymbolTable Build(string? text)
    {
        var module = new BslModuleParser().Parse(new BslModuleSource(string.Empty, text ?? string.Empty));
        return module.Symbols ?? Build(module.Routines, BslLexer.Tokenize(text ?? string.Empty), module.LineCount);
    }

    /// <summary>Собирает таблицу символов по готовому списку процедур и токенам модуля.</summary>
    /// <param name="routines">Процедуры и функции модуля: задают области видимости и параметры.</param>
    /// <param name="tokens">Токены модуля.</param>
    /// <param name="lineCount">Число строк модуля (0 — определить по токенам).</param>
    public static SymbolTable Build(IReadOnlyList<BslRoutine> routines, IReadOnlyList<BslToken> tokens, int lineCount = 0)
    {
        ArgumentNullException.ThrowIfNull(routines);
        ArgumentNullException.ThrowIfNull(tokens);

        var symbols = new List<BslSymbol>();
        var scopes = BuildScopes(routines);
        CollectParameters(routines, tokens, symbols);
        CollectVariables(scopes, tokens, symbols);
        symbols.Sort(static (left, right) =>
        {
            var byLine = left.Line.CompareTo(right.Line);
            return byLine != 0 ? byLine : left.Column.CompareTo(right.Column);
        });

        return new SymbolTable(symbols, scopes, lineCount > 0 ? lineCount : LastLine(tokens));
    }

    /// <summary>Область, в которую попадает строка: процедура (функция) или модуль.</summary>
    public BslScopeInfo ScopeAt(int line) => FindScope(_scopes, line) ?? _moduleScope;

    /// <summary>Какие имена видимы в указанной строке.</summary>
    /// <param name="line">Номер строки (1-based).</param>
    public IReadOnlyList<BslSymbol> VisibleAt(int line)
    {
        var result = new List<BslSymbol>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var scope = ScopeAt(line);

        if (scope.Kind == BslScopeKind.Routine)
        {
            foreach (var symbol in _symbols)
            {
                if (!InScope(symbol, scope))
                {
                    continue;
                }

                if (symbol.Kind == BslSymbolKind.LocalVariable && symbol.Line > line)
                {
                    // Локальная переменная видна со строки объявления: раньше её ещё нет.
                    continue;
                }

                if (seen.Add(symbol.Name))
                {
                    result.Add(symbol);
                }
            }
        }

        foreach (var symbol in _symbols)
        {
            if (symbol.Kind == BslSymbolKind.ModuleVariable && seen.Add(symbol.Name))
            {
                result.Add(symbol);
            }
        }

        return result;
    }

    /// <summary>Все объявления имени в модуле (регистр не важен) — «где объявлено имя X».</summary>
    public IReadOnlyList<BslSymbol> FindDeclarations(string name)
    {
        if (string.IsNullOrEmpty(name) || !_byName.TryGetValue(name, out var symbols))
        {
            return [];
        }

        return symbols;
    }

    /// <summary>
    /// Какое объявление имени действует в указанной строке: сначала ищется объявление внутри
    /// процедуры (функции) этой строки, затем переменная модуля. Если объявления до строки нет,
    /// возвращается ближайшее объявление после неё — так видно имя, использованное раньше объявления.
    /// </summary>
    /// <param name="name">Имя (регистр не важен).</param>
    /// <param name="line">Номер строки (1-based).</param>
    /// <returns>Объявление или <see langword="null"/>, если имени в модуле нет.</returns>
    public BslSymbol? Resolve(string name, int line)
    {
        if (string.IsNullOrEmpty(name) || !_byName.TryGetValue(name, out var symbols))
        {
            return null;
        }

        var scope = ScopeAt(line);
        BslSymbol? before = null;
        BslSymbol? after = null;

        foreach (var symbol in symbols)
        {
            // Имена других процедур в этой строке не видимы.
            if (!InScope(symbol, scope))
            {
                continue;
            }

            if (symbol.Line <= line)
            {
                if (before is null || IsCloser(symbol, before))
                {
                    before = symbol;
                }
            }
            else if (after is null || symbol.Line < after.Line)
            {
                after = symbol;
            }
        }

        return before ?? after;
    }

    /// <summary>Ближайшее к строке объявление: чем позже объявлено и чем правее, тем оно «сильнее».</summary>
    private static bool IsCloser(BslSymbol candidate, BslSymbol current) =>
        candidate.Line > current.Line ||
        (candidate.Line == current.Line && candidate.Column > current.Column);

    /// <summary>Имя принадлежит той же области, что и строка: переменная модуля — любой строке, остальное — своей процедуре.</summary>
    private static bool InScope(BslSymbol symbol, BslScopeInfo scope) =>
        !symbol.IsLocal ||
        string.Equals(symbol.Routine, scope.Routine, StringComparison.OrdinalIgnoreCase);

    private static List<BslScopeInfo> BuildScopes(IReadOnlyList<BslRoutine> routines)
    {
        var scopes = new List<BslScopeInfo>(routines.Count);
        foreach (var routine in routines)
        {
            scopes.Add(new BslScopeInfo(
                routine.Name,
                BslScopeKind.Routine,
                routine.StartLine,
                routine.EndLine,
                routine.Parameters));
        }

        // Вложенные процедуры в 1С запрещены; если разбор встретил перекрытие, побеждает более узкая область.
        scopes.Sort(static (left, right) =>
        {
            var byStart = left.StartLine.CompareTo(right.StartLine);
            return byStart != 0 ? byStart : left.EndLine.CompareTo(right.EndLine);
        });

        return scopes;
    }

    private static int LastLine(IReadOnlyList<BslToken> tokens) => tokens.Count == 0 ? 0 : tokens[^1].Line;

    /// <summary>
    /// Область, в которую попадает строка. Области отсортированы по началу, поэтому нужная
    /// находится двоичным поиском; назад просматриваются только перекрывающиеся (недопустимые в 1С).
    /// </summary>
    private static BslScopeInfo? FindScope(IReadOnlyList<BslScopeInfo> scopes, int line)
    {
        var low = 0;
        var high = scopes.Count - 1;
        var candidate = -1;

        while (low <= high)
        {
            var middle = (low + high) / 2;
            if (scopes[middle].StartLine <= line)
            {
                candidate = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        for (var i = candidate; i >= 0 && candidate - i < MaxScopeBacktrack; i--)
        {
            if (scopes[i].Contains(line))
            {
                return scopes[i];
            }
        }

        return null;
    }

    /// <summary>Ищет процедуру по имени, начиная с позиции <paramref name="cursor"/>: заголовки идут по порядку.</summary>
    private static BslRoutine? TakeRoutine(IReadOnlyList<BslRoutine> routines, string name, ref int cursor)
    {
        for (var i = cursor; i < routines.Count; i++)
        {
            if (routines[i].Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                cursor = i + 1;
                return routines[i];
            }
        }

        return null;
    }

    /// <summary>Собирает параметры процедур и функций по заголовкам в тексте модуля.</summary>
    private static void CollectParameters(IReadOnlyList<BslRoutine> routines, IReadOnlyList<BslToken> tokens, List<BslSymbol> symbols)
    {
        if (routines.Count == 0)
        {
            return;
        }

        var cursor = 0;
        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (token.Kind is not (BslTokenKind.Keyword or BslTokenKind.Identifier) ||
                (!BslTokenScan.IsKeyword(token, "Процедура") && !BslTokenScan.IsKeyword(token, "Функция")))
            {
                continue;
            }

            var nameIndex = BslTokenScan.NextMeaningful(tokens, i + 1);
            if (nameIndex < 0 || tokens[nameIndex].Kind != BslTokenKind.Identifier)
            {
                continue;
            }

            var routine = TakeRoutine(routines, tokens[nameIndex].GetText(), ref cursor);
            if (routine is null)
            {
                continue;
            }

            var open = BslTokenScan.NextMeaningful(tokens, nameIndex + 1);
            if (open < 0 || !BslTokenScan.IsOperator(tokens[open], "("))
            {
                continue;
            }

            ParseParameterList(tokens, open, routine, symbols);
        }
    }

    /// <summary>Разбирает список параметров: имена, <c>Знач</c> и позиции; значения по умолчанию пропускает.</summary>
    private static void ParseParameterList(IReadOnlyList<BslToken> tokens, int openIndex, BslRoutine routine, List<BslSymbol> symbols)
    {
        var depth = 0;
        var byValue = false;

        for (var i = openIndex; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (token.Kind == BslTokenKind.Operator)
            {
                if (BslTokenScan.IsOpening(token))
                {
                    depth++;
                    continue;
                }

                if (BslTokenScan.IsClosing(token))
                {
                    depth--;
                    if (depth <= 0)
                    {
                        return;
                    }

                    continue;
                }

                if (depth == 1 && BslTokenScan.IsOperator(token, "="))
                {
                    // Значение по умолчанию пропускаем до запятой или закрывающей скобки списка.
                    i = SkipDefaultValue(tokens, i + 1);
                    byValue = false;
                    continue;
                }

                continue;
            }

            if (depth != 1)
            {
                continue;
            }

            if (token.Kind == BslTokenKind.Keyword && BslTokenScan.IsKeyword(token, "Знач"))
            {
                byValue = true;
                continue;
            }

            if (token.Kind != BslTokenKind.Identifier)
            {
                continue;
            }

            symbols.Add(new BslSymbol(
                token.GetText(),
                BslSymbolKind.Parameter,
                routine.Name,
                token.Line,
                token.Column,
                IsByValue: byValue));
            byValue = false;
        }
    }

    /// <summary>Индекс токена, с которого продолжается список параметров (запятая или закрывающая скобка).</summary>
    private static int SkipDefaultValue(IReadOnlyList<BslToken> tokens, int index)
    {
        var depth = 0;
        for (var i = index; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (token.Kind != BslTokenKind.Operator)
            {
                continue;
            }

            if (BslTokenScan.IsOpening(token))
            {
                depth++;
                continue;
            }

            if (BslTokenScan.IsClosing(token))
            {
                if (depth == 0)
                {
                    return i - 1;
                }

                depth--;
                continue;
            }

            if (depth == 0 && BslTokenScan.IsOperator(token, ","))
            {
                return i - 1;
            }
        }

        return tokens.Count;
    }

    /// <summary>
    /// Собирает переменные: объявленные словом <c>Перем</c>, переменные циклов и неявные переменные
    /// присваивания. Приоритет у объявления <c>Перем</c> — неявное имя с тем же именем не добавляется.
    /// </summary>
    private static void CollectVariables(IReadOnlyList<BslScopeInfo> scopes, IReadOnlyList<BslToken> tokens, List<BslSymbol> symbols)
    {
        var declared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var symbol in symbols)
        {
            declared.Add(ScopeKey(symbol.Routine, symbol.Name));
        }

        // Присваивание внутри процедуры переменной модуля не создаёт локальную переменную:
        // запись идёт в саму переменную модуля, поэтому неявное имя с таким именем не добавляется.
        var moduleNames = new HashSet<string>(
            symbols.Where(static symbol => symbol.Kind == BslSymbolKind.ModuleVariable).Select(static symbol => symbol.Name),
            StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];

            // Область строки ищется только там, где она действительно нужна: поиск области —
            // самая частая операция, а модули бывают на тысячи процедур.
            if (token.Kind == BslTokenKind.Keyword && BslTokenScan.IsKeyword(token, "Перем"))
            {
                var routine = FindScope(scopes, token.Line)?.Routine;
                i = CollectDeclaredList(scopes, tokens, i, routine, symbols, declared, moduleNames);
                continue;
            }

            if (token.Kind == BslTokenKind.Keyword && BslTokenScan.IsKeyword(token, "Для"))
            {
                CollectLoopVariable(tokens, i, FindScope(scopes, token.Line)?.Routine, symbols, declared, moduleNames);
                continue;
            }

            if (token.Kind == BslTokenKind.Identifier && BslTokenScan.IsStatementStart(tokens, i))
            {
                var equals = BslTokenScan.NextMeaningful(tokens, i + 1);
                if (equals >= 0 && BslTokenScan.IsOperator(tokens[equals], "="))
                {
                    AddImplicit(
                        symbols,
                        declared,
                        moduleNames,
                        token.GetText(),
                        FindScope(scopes, token.Line)?.Routine,
                        token.Line,
                        token.Column);
                }
            }
        }
    }

    /// <summary>Разбирает список имён после <c>Перем</c>; возвращает индекс последнего токена объявления.</summary>
    private static int CollectDeclaredList(
        IReadOnlyList<BslScopeInfo> scopes,
        IReadOnlyList<BslToken> tokens,
        int keywordIndex,
        string? routine,
        List<BslSymbol> symbols,
        HashSet<string> declared,
        HashSet<string> moduleNames)
    {
        var isExport = false;
        var last = keywordIndex;
        var firstAdded = symbols.Count;

        for (var i = keywordIndex + 1; i < tokens.Count; i++)
        {
            var token = tokens[i];
            last = i;

            if (token.Kind is BslTokenKind.Comment or BslTokenKind.NewLine)
            {
                continue;
            }

            if (token.Kind == BslTokenKind.Operator && BslTokenScan.IsOperator(token, ","))
            {
                continue;
            }

            if (token.Kind == BslTokenKind.Operator && BslTokenScan.IsOperator(token, ";"))
            {
                break;
            }

            if (token.Kind == BslTokenKind.Keyword && BslTokenScan.IsKeyword(token, "Экспорт"))
            {
                isExport = true;
                continue;
            }

            if (token.Kind != BslTokenKind.Identifier)
            {
                // Неожиданный токен: объявление закончилось (например, без точки с запятой).
                last = i - 1;
                break;
            }

            // Объявление относится к области по строке самого объявления, а не по слову «Перем».
            var scopeRoutine = FindScope(scopes, token.Line)?.Routine ?? routine;
            var key = ScopeKey(scopeRoutine, token.GetText());
            if (declared.Add(key))
            {
                symbols.Add(new BslSymbol(
                    token.GetText(),
                    scopeRoutine is null ? BslSymbolKind.ModuleVariable : BslSymbolKind.LocalVariable,
                    scopeRoutine,
                    token.Line,
                    token.Column,
                    IsExport: isExport && scopeRoutine is null));

                if (scopeRoutine is null)
                {
                    moduleNames.Add(token.GetText());
                }
            }
        }

        // «Экспорт» относится ко всему объявлению, поэтому проставляется после разбора списка
        // (слово может стоять и в конце: «Перем А, Б Экспорт;»).
        if (isExport)
        {
            for (var i = firstAdded; i < symbols.Count; i++)
            {
                if (symbols[i].Kind == BslSymbolKind.ModuleVariable)
                {
                    symbols[i] = symbols[i] with { IsExport = true };
                }
            }
        }

        return last;
    }

    /// <summary>Переменная цикла <c>Для Каждого X Из …</c> или счётчик <c>Для X = … По …</c>.</summary>
    private static void CollectLoopVariable(
        IReadOnlyList<BslToken> tokens,
        int keywordIndex,
        string? routine,
        List<BslSymbol> symbols,
        HashSet<string> declared,
        HashSet<string> moduleNames)
    {
        var next = BslTokenScan.NextMeaningful(tokens, keywordIndex + 1);
        if (next < 0)
        {
            return;
        }

        if (tokens[next].Kind == BslTokenKind.Keyword && BslTokenScan.IsKeyword(tokens[next], "Каждого"))
        {
            next = BslTokenScan.NextMeaningful(tokens, next + 1);
        }

        if (next < 0 || tokens[next].Kind != BslTokenKind.Identifier)
        {
            return;
        }

        AddImplicit(symbols, declared, moduleNames, tokens[next].GetText(), routine, tokens[next].Line, tokens[next].Column);
    }

    /// <summary>Добавляет неявное имя, если такого имени в этой области ещё нет.</summary>
    private static void AddImplicit(
        List<BslSymbol> symbols,
        HashSet<string> declared,
        HashSet<string> moduleNames,
        string name,
        string? routine,
        int line,
        int column)
    {
        // Имя уже объявлено переменной модуля: присваивание меняет её, а не создаёт локальную.
        if (routine is not null && moduleNames.Contains(name))
        {
            return;
        }

        if (!declared.Add(ScopeKey(routine, name)))
        {
            return;
        }

        symbols.Add(new BslSymbol(
            name,
            routine is null ? BslSymbolKind.ModuleVariable : BslSymbolKind.LocalVariable,
            routine,
            line,
            column,
            IsImplicit: true));
    }

    private static string ScopeKey(string? routine, string name) => (routine ?? string.Empty) + "\u0000" + name;
}
