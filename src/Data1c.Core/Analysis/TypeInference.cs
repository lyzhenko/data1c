using Data1c.Core.Bsl;
using Data1c.Core.Platform;

namespace Data1c.Core.Analysis;

/// <summary>Как получен выведенный тип переменной.</summary>
public enum InferredTypeSource
{
    /// <summary><c>Новый Тип(…)</c>.</summary>
    Constructor,

    /// <summary><c>Новый("Тип", …)</c> — тип задан строкой.</summary>
    ConstructorByName,

    /// <summary><c>ОписаниеТипов(…)</c>.</summary>
    TypeDescription,

    /// <summary><c>X.Создать()</c> или <c>X.Скопировать()</c>, где тип <c>X</c> уже известен.</summary>
    CreateOrCopy,

    /// <summary>Присваивание переменной известного типа: <c>Y = X</c>.</summary>
    Assignment,

    /// <summary>Тип параметра по аргументу вызова, тип которого известен.</summary>
    Parameter,
}

/// <summary>Место, где тип выведен: имя, тип, источник и позиция.</summary>
/// <param name="Name">Имя переменной или параметра.</param>
/// <param name="TypeName">Имя типа.</param>
/// <param name="Source">Как тип получен.</param>
/// <param name="Line">Строка (1-based).</param>
/// <param name="Column">Позиция в строке (1-based).</param>
/// <param name="Routine">Процедура или функция; <see langword="null"/> для модульного кода.</param>
public sealed record InferredType(string Name, string TypeName, InferredTypeSource Source, int Line, int Column, string? Routine);

/// <summary>Место, где тип вывести не удалось.</summary>
/// <param name="Name">Имя переменной.</param>
/// <param name="Reason">Почему тип неизвестен.</param>
/// <param name="Line">Строка (1-based).</param>
/// <param name="Column">Позиция в строке (1-based).</param>
/// <param name="Routine">Процедура или функция; <see langword="null"/> для модульного кода.</param>
public sealed record UnresolvedType(string Name, string Reason, int Line, int Column, string? Routine);

/// <summary>Вызов метода через переменную известного типа: разрешён как метод платформы.</summary>
/// <param name="Call">Исходный вызов, как он записан в коде.</param>
/// <param name="TypeName">Выведенный тип переменной-квалификатора.</param>
/// <param name="Callee">Имя вызова с типом вместо переменной: «ТаблицаЗначений.Свернуть».</param>
/// <param name="Routine">Процедура или функция, в которой стоит вызов.</param>
public sealed record ResolvedPlatformCall(BslCall Call, string TypeName, string Callee, string? Routine)
{
    /// <summary>Идентификатор узла графа, которым становится такой вызов.</summary>
    public string NodeId => "platform:" + Callee;
}

/// <summary>Вызов метода через переменную, тип которой вывести не удалось.</summary>
/// <param name="Call">Исходный вызов.</param>
/// <param name="Qualifier">Переменная-квалификатор.</param>
/// <param name="Reason">Почему вызов не разрешён.</param>
/// <param name="Routine">Процедура или функция, в которой стоит вызов.</param>
public sealed record UnresolvedCall(BslCall Call, string Qualifier, string Reason, string? Routine);

/// <summary>
/// Результат вывода типов модуля: словарь «имя → имя типа» (для модуля целиком и для позиции),
/// места, где тип вывести не удалось, и вызовы, которые удалось разрешить по известному типу.
/// </summary>
public sealed class TypeInferenceResult
{
    private readonly IReadOnlyList<BslScopeInfo> _scopes;
    private readonly Dictionary<string, Dictionary<string, TypeEntry>> _scopeTypes;
    private readonly Dictionary<int, List<ResolvedPlatformCall>> _platformByLine = [];
    private readonly PlatformHelpIndex? _platform;

    internal TypeInferenceResult(
        string path,
        Dictionary<string, Dictionary<string, TypeEntry>> scopeTypes,
        IReadOnlyDictionary<string, string> types,
        IReadOnlyList<InferredType> inferred,
        IReadOnlyList<UnresolvedType> unresolved,
        IReadOnlyList<ResolvedPlatformCall> platformCalls,
        IReadOnlyList<UnresolvedCall> unresolvedCalls,
        IReadOnlyList<BslScopeInfo> scopes,
        PlatformHelpIndex? platform)
    {
        Path = path;
        _scopeTypes = scopeTypes;
        Types = types;
        Inferred = inferred;
        Unresolved = unresolved;
        PlatformCalls = platformCalls;
        UnresolvedCalls = unresolvedCalls;
        _scopes = scopes;
        _platform = platform;

        foreach (var call in platformCalls)
        {
            if (!_platformByLine.TryGetValue(call.Call.Line, out var list))
            {
                list = [];
                _platformByLine[call.Call.Line] = list;
            }

            list.Add(call);
        }
    }

    /// <summary>Путь модуля, для которого считался вывод типов.</summary>
    public string Path { get; }

    /// <summary>Словарь «имя → имя типа» по модулю целиком: удобная сводка.</summary>
    /// <remarks>
    /// Имена локальных переменных разных процедур могут совпадать; в сводке остаётся первое
    /// известное значение. Для точного ответа на позицию используйте <see cref="TypesAt"/>.
    /// </remarks>
    public IReadOnlyDictionary<string, string> Types { get; }

    /// <summary>Все места, где тип выведен.</summary>
    public IReadOnlyList<InferredType> Inferred { get; }

    /// <summary>Все места, где тип вывести не удалось.</summary>
    public IReadOnlyList<UnresolvedType> Unresolved { get; }

    /// <summary>Вызовы, разрешённые как методы платформы по выведенному типу переменной.</summary>
    public IReadOnlyList<ResolvedPlatformCall> PlatformCalls { get; }

    /// <summary>Вызовы через переменную, тип которой не выведен.</summary>
    public IReadOnlyList<UnresolvedCall> UnresolvedCalls { get; }

    /// <summary>Области видимости модуля.</summary>
    public IReadOnlyList<BslScopeInfo> Scopes => _scopes;

    /// <summary>Сколько вызовов разрешилось благодаря выводу типов.</summary>
    public int ResolvedCallCount => PlatformCalls.Count;

    /// <summary>Сколько вызовов через переменную осталось неразрешёнными.</summary>
    public int UnresolvedCallCount => UnresolvedCalls.Count;

    /// <summary>Словарь «имя → имя типа» для указанной строки модуля.</summary>
    /// <param name="line">Номер строки (1-based).</param>
    public IReadOnlyDictionary<string, string> TypesAt(int line)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var routine = RoutineAt(line);

        if (routine is not null && _scopeTypes.TryGetValue(routine, out var local))
        {
            foreach (var (name, entry) in local)
            {
                if (entry.Line <= line)
                {
                    result[name] = entry.TypeName;
                }
            }
        }

        if (_scopeTypes.TryGetValue(string.Empty, out var module))
        {
            foreach (var (name, entry) in module)
            {
                if (entry.Line <= line)
                {
                    result.TryAdd(name, entry.TypeName);
                }
            }
        }

        return result;
    }

    /// <summary>Тип имени, действующий в указанной строке.</summary>
    public bool TryGetType(string name, int line, out string type)
    {
        type = string.Empty;
        if (string.IsNullOrEmpty(name) || !TypesAt(line).TryGetValue(name, out var found) || string.IsNullOrEmpty(found))
        {
            return false;
        }

        type = found;
        return true;
    }

    /// <summary>Отнесён ли тип к платформенным: по встроенному списку или по справке платформы.</summary>
    public bool IsPlatformType(string? typeName) =>
        PlatformTypeNames.IsKnown(typeName) || _platform?.IsKnownType(typeName) == true;

    /// <summary>
    /// Имя вызова платформы для вызова через переменную с известным типом:
    /// «Таблица.Свернуть» при типе «ТаблицаЗначений» даёт «ТаблицаЗначений.Свернуть».
    /// </summary>
    /// <param name="call">Вызов из разобранного модуля.</param>
    /// <param name="callee">Имя вызова с типом вместо переменной.</param>
    public bool TryGetPlatformCallee(BslCall call, out string callee)
    {
        callee = string.Empty;
        if (!_platformByLine.TryGetValue(call.Line, out var list))
        {
            return false;
        }

        foreach (var resolved in list)
        {
            if (resolved.Call.Callee.Equals(call.Callee, StringComparison.OrdinalIgnoreCase))
            {
                callee = resolved.Callee;
                return true;
            }
        }

        return false;
    }

    /// <summary>Процедура или функция, в которую попадает строка.</summary>
    private string? RoutineAt(int line) =>
        _scopes.FirstOrDefault(scope => scope.Kind == BslScopeKind.Routine && scope.Contains(line))?.Routine;
}

/// <summary>Тип имени в области видимости: имя типа, как он получен и где объявлен.</summary>
/// <param name="TypeName">Имя типа.</param>
/// <param name="Source">Как тип получен.</param>
/// <param name="Line">Строка, с которой тип действует.</param>
/// <param name="Column">Позиция в строке.</param>
internal sealed record TypeEntry(string TypeName, InferredTypeSource Source, int Line, int Column);

/// <summary>
/// Консервативный вывод типов по тексту модуля BSL: <c>Новый X</c>, <c>Новый("X")</c>,
/// <c>ОписаниеТипов(…)</c>, <c>X.Создать()</c>, присваивание переменной известного типа и типы
/// параметров по аргументам вызовов этого же модуля.
/// </summary>
/// <remarks>
/// <para>Поток данных не считается: ветвления, циклы и порядок присваиваний не анализируются,
/// побеждает последнее присваивание в тексте. Значения, полученные из выражений (литералы,
/// функции, методы с неизвестным типом), остаются неизвестными — это осознанно консервативно.</para>
/// <para>Если тип переменной отнесён к платформенным (<see cref="PlatformTypeNames"/> или справка
/// платформы), вызов «Переменная.Метод» считается методом платформы и получает узел
/// <c>platform:Тип.Метод</c> в графе зависимостей вместо внешней заглушки.</para>
/// </remarks>
public static class TypeInference
{
    /// <summary>Сколько кругов уточнения выполняется до остановки.</summary>
    private const int MaxRounds = 3;

    /// <summary>Выводит типы по тексту модуля (разбор выполняется заново).</summary>
    /// <param name="source">Путь и текст модуля.</param>
    public static TypeInferenceResult Infer(BslModuleSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var module = new BslModuleParser().Parse(source);
        if (module.Types is not null)
        {
            return module.Types;
        }

        var tokens = BslLexer.Tokenize(source.Text);
        var symbols = module.Symbols ?? SymbolTable.Build(module.Routines, tokens, module.LineCount);
        return Infer(module, symbols, tokens);
    }

    /// <summary>Выводит типы по разобранному модулю, его таблице символов и токенам.</summary>
    /// <param name="module">Разобранный модуль: из него берутся процедуры и вызовы.</param>
    /// <param name="symbols">Таблица символов этого же модуля.</param>
    /// <param name="tokens">Токены модуля.</param>
    /// <param name="platform">Справка платформы, если она подключена: уточняет платформенные типы.</param>
    public static TypeInferenceResult Infer(
        BslModuleInfo module,
        SymbolTable symbols,
        IReadOnlyList<BslToken> tokens,
        PlatformHelpIndex? platform = null)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(symbols);
        ArgumentNullException.ThrowIfNull(tokens);
        return new InferencePass(module, symbols, tokens, platform).Run();
    }

    /// <summary>
    /// Разрешает вызов по выведенному типу переменной: если квалификатор — переменная известного
    /// платформенного типа, возвращается имя вызова платформы («ТаблицаЗначений.Свернуть»).
    /// </summary>
    /// <param name="module">Разобранный модуль с готовым выводом типов.</param>
    /// <param name="call">Вызов из этого модуля.</param>
    /// <param name="callee">Имя вызова платформы.</param>
    public static bool TryResolvePlatformCallee(BslModuleInfo module, BslCall call, out string callee)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(call);
        callee = string.Empty;
        return module.Types is not null && module.Types.TryGetPlatformCallee(call, out callee);
    }

    /// <summary>Один проход вывода типов по модулю.</summary>
    private sealed class InferencePass(
        BslModuleInfo module,
        SymbolTable symbols,
        IReadOnlyList<BslToken> tokens,
        PlatformHelpIndex? platform)
    {
        /// <summary>Вид правой части присваивания: от него зависит, выводится ли тип.</summary>
        private enum ExpressionKind
        {
            Constructor,
            ConstructorByName,
            TypeDescription,
            CreateOrCopy,
            Alias,
            Unknown,
        }

        private sealed record Assignment(string? Routine, string Name, int Line, int Column, ExpressionKind Kind, string? Detail, string Reason);

        private sealed record CallSite(string? Routine, string Callee, int Line, List<string?> Arguments);

        private sealed record ParameterSlot(string Routine, string Name, int Line, int Column);

        private readonly BslModuleInfo _module = module;
        private readonly SymbolTable _symbols = symbols;
        private readonly IReadOnlyList<BslToken> _tokens = tokens;
        private readonly PlatformHelpIndex? _platform = platform;
        private readonly Dictionary<string, Dictionary<string, TypeEntry>> _scopeTypes = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<ParameterSlot>> _parameters = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<Assignment> _assignments = [];
        private readonly List<CallSite> _callSites = [];

        public TypeInferenceResult Run()
        {
            CollectParameters();
            Scan();

            for (var round = 0; round < MaxRounds; round++)
            {
                var changed = ResolveAssignments();
                changed |= PropagateParameters();
                if (!changed)
                {
                    break;
                }
            }

            var inferred = new List<InferredType>();
            var unresolved = new List<UnresolvedType>();
            Report(inferred, unresolved);
            var (platformCalls, unresolvedCalls) = ClassifyCalls();

            return new TypeInferenceResult(
                _module.Path,
                _scopeTypes,
                FlatTypes(),
                inferred,
                unresolved,
                platformCalls,
                unresolvedCalls,
                _symbols.Scopes,
                _platform);
        }

        /// <summary>Параметры процедур и функций: нужны для переноса типов из аргументов вызовов.</summary>
        private void CollectParameters()
        {
            foreach (var symbol in _symbols.Symbols)
            {
                if (symbol.Kind != BslSymbolKind.Parameter || symbol.Routine is not { Length: > 0 } routine)
                {
                    continue;
                }

                if (!_parameters.TryGetValue(routine, out var list))
                {
                    list = [];
                    _parameters[routine] = list;
                }

                list.Add(new ParameterSlot(routine, symbol.Name, symbol.Line, symbol.Column));
            }

            foreach (var list in _parameters.Values)
            {
                list.Sort(static (left, right) =>
                {
                    var byLine = left.Line.CompareTo(right.Line);
                    return byLine != 0 ? byLine : left.Column.CompareTo(right.Column);
                });
            }
        }

        /// <summary>Один проход по токенам: присваивания и вызовы процедур этого же модуля.</summary>
        private void Scan()
        {
            var routineNames = new HashSet<string>(_module.Routines.Select(static routine => routine.Name), StringComparer.OrdinalIgnoreCase);

            for (var i = 0; i < _tokens.Count; i++)
            {
                var token = _tokens[i];
                if (token.Kind != BslTokenKind.Identifier)
                {
                    continue;
                }

                var next = BslTokenScan.NextMeaningful(_tokens, i + 1);
                var isCall = next >= 0 && BslTokenScan.IsOperator(_tokens[next], "(") && routineNames.Contains(token.GetText());
                var isAssignment = next >= 0 &&
                                   BslTokenScan.IsOperator(_tokens[next], "=") &&
                                   BslTokenScan.IsStatementStart(_tokens, i);

                if (!isCall && !isAssignment)
                {
                    // Область видимости нужна только для найденных фактов: иначе поиск области
                    // выполнялся бы на каждом идентификаторе модуля.
                    continue;
                }

                var routine = _symbols.ScopeAt(token.Line).Routine;

                if (isCall)
                {
                    var (arguments, _) = BslTokenScan.ParseArguments(_tokens, next);
                    _callSites.Add(new CallSite(routine, token.GetText(), token.Line, arguments));
                }

                if (!isAssignment)
                {
                    continue;
                }

                var expression = BslTokenScan.NextMeaningful(_tokens, next + 1);
                if (expression < 0)
                {
                    continue;
                }

                var (kind, detail, reason) = Classify(expression);
                _assignments.Add(new Assignment(routine, token.GetText(), token.Line, token.Column, kind, detail, reason));
            }
        }

        /// <summary>Разбирает правую часть присваивания: тип выводится только у известных конструкций.</summary>
        private (ExpressionKind Kind, string? Detail, string Reason) Classify(int index)
        {
            var token = _tokens[index];
            if (BslTokenScan.IsKeyword(token, "Новый"))
            {
                var next = BslTokenScan.NextMeaningful(_tokens, index + 1);
                if (next < 0)
                {
                    return (ExpressionKind.Unknown, null, "после «Новый» нет имени типа");
                }

                if (_tokens[next].Kind == BslTokenKind.Identifier)
                {
                    return (ExpressionKind.Constructor, _tokens[next].GetText(), string.Empty);
                }

                if (BslTokenScan.IsOperator(_tokens[next], "("))
                {
                    var literal = BslTokenScan.NextMeaningful(_tokens, next + 1);
                    if (literal >= 0 && _tokens[literal].Kind == BslTokenKind.String)
                    {
                        var name = BslLexer.UnescapeString(_tokens[literal].Span).Trim();
                        return PlatformTypeNames.LooksLikeTypeName(name)
                            ? (ExpressionKind.ConstructorByName, name, string.Empty)
                            : (ExpressionKind.Unknown, null, $"имя типа из строки «{name}» не распознано");
                    }

                    return (ExpressionKind.Unknown, null, "«Новый(…)» без строкового имени типа");
                }

                return (ExpressionKind.Unknown, null, "после «Новый» нет имени типа");
            }

            if (token.Kind != BslTokenKind.Identifier)
            {
                return (ExpressionKind.Unknown, null, "выражение: тип не выводится (литерал или операция)");
            }

            return ClassifyChain(index);
        }

        /// <summary>Разбирает цепочку «Имя[.Имя]*»: тип выводится для «X.Создать()» и «X.Скопировать()».</summary>
        private (ExpressionKind Kind, string? Detail, string Reason) ClassifyChain(int index)
        {
            var last = index;
            var cursor = BslTokenScan.NextMeaningful(_tokens, index + 1);
            while (cursor >= 0 && BslTokenScan.IsOperator(_tokens[cursor], "."))
            {
                var segment = BslTokenScan.NextMeaningful(_tokens, cursor + 1);
                if (segment < 0 || _tokens[segment].Kind != BslTokenKind.Identifier)
                {
                    break;
                }

                last = segment;
                cursor = BslTokenScan.NextMeaningful(_tokens, segment + 1);
            }

            var isCall = cursor >= 0 && BslTokenScan.IsOperator(_tokens[cursor], "(");
            var method = _tokens[last].GetText();

            if (last == index)
            {
                if (!isCall)
                {
                    return (ExpressionKind.Alias, _tokens[index].GetText(), string.Empty);
                }

                return BslTokenScan.IsKeyword(_tokens[index], "ОписаниеТипов")
                    ? (ExpressionKind.TypeDescription, "ОписаниеТипов", string.Empty)
                    : (ExpressionKind.Unknown, null, $"функция «{method}»: возвращаемый тип неизвестен");
            }

            var variable = _tokens[index].GetText();
            if (isCall)
            {
                if (IsCreateOrCopy(method) && IsSimpleQualifier(index, last))
                {
                    return (ExpressionKind.CreateOrCopy, variable, string.Empty);
                }

                return (ExpressionKind.Unknown, null, $"метод «{variable}.{method}»: возвращаемый тип неизвестен");
            }

            return (ExpressionKind.Unknown, null, $"обращение к свойству «{variable}.{method}»: тип неизвестен");
        }

        /// <summary>Цепочка состоит ровно из двух элементов: «Переменная.Метод».</summary>
        private bool IsSimpleQualifier(int start, int last) =>
            BslTokenScan.NextMeaningful(_tokens, start + 1) is var dot &&
            dot >= 0 && BslTokenScan.IsOperator(_tokens[dot], ".") &&
            BslTokenScan.NextMeaningful(_tokens, dot + 1) == last;

        private static bool IsCreateOrCopy(string method) =>
            method.Equals("Создать", StringComparison.OrdinalIgnoreCase) ||
            method.Equals("Скопировать", StringComparison.OrdinalIgnoreCase);

        /// <summary>Один круг вывода по присваиваниям; возвращает true, если что-то уточнилось.</summary>
        private bool ResolveAssignments()
        {
            var changed = false;
            foreach (var assignment in _assignments)
            {
                if (TryResolveAssignment(assignment, out var type, out var source))
                {
                    changed |= SetType(assignment.Routine, assignment.Name, type, source, assignment.Line, assignment.Column);
                }
            }

            return changed;
        }

        /// <summary>Переносит типы аргументов в параметры процедур этого же модуля.</summary>
        private bool PropagateParameters()
        {
            var changed = false;
            foreach (var site in _callSites)
            {
                if (!_parameters.TryGetValue(site.Callee, out var parameters))
                {
                    continue;
                }

                for (var i = 0; i < site.Arguments.Count && i < parameters.Count; i++)
                {
                    if (site.Arguments[i] is not { } argument ||
                        !TryGetType(argument, site.Routine, site.Line, out var type))
                    {
                        continue;
                    }

                    var slot = parameters[i];
                    changed |= SetType(slot.Routine, slot.Name, type, InferredTypeSource.Parameter, slot.Line, slot.Column);
                }
            }

            return changed;
        }

        /// <summary>Собирает отчёт: где тип выведен и где не вышел.</summary>
        private void Report(List<InferredType> inferred, List<UnresolvedType> unresolved)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var assignment in _assignments)
            {
                if (TryResolveAssignment(assignment, out var type, out var source))
                {
                    var key = $"{assignment.Routine}\u0000{assignment.Name}\u0000{assignment.Line}";
                    if (seen.Add(key))
                    {
                        inferred.Add(new InferredType(assignment.Name, type, source, assignment.Line, assignment.Column, assignment.Routine));
                    }
                }
                else
                {
                    unresolved.Add(new UnresolvedType(assignment.Name, assignment.Reason, assignment.Line, assignment.Column, assignment.Routine));
                }
            }

            // Типы параметров приходят из вызовов, а не из присваиваний: их нужно добавить отдельно.
            foreach (var (routine, entries) in _scopeTypes)
            {
                foreach (var (name, entry) in entries)
                {
                    if (entry.Source == InferredTypeSource.Parameter)
                    {
                        inferred.Add(new InferredType(name, entry.TypeName, entry.Source, entry.Line, entry.Column, routine.Length == 0 ? null : routine));
                    }
                }
            }

            inferred.Sort(static (left, right) => left.Line.CompareTo(right.Line));
            unresolved.Sort(static (left, right) => left.Line.CompareTo(right.Line));
        }

        /// <summary>Делит вызовы через переменные на разрешённые по типу и оставшиеся неразрешёнными.</summary>
        private (List<ResolvedPlatformCall> Resolved, List<UnresolvedCall> Unresolved) ClassifyCalls()
        {
            var resolved = new List<ResolvedPlatformCall>();
            var unresolved = new List<UnresolvedCall>();

            foreach (var (routine, call) in EnumerateCalls())
            {
                if (call.Qualifier is not { Length: > 0 } qualifier)
                {
                    continue;
                }

                // Квалификатор — переменная модуля, только если у неё есть объявление.
                // Имена общих модулей, коллекций метаданных и свойств разрешает граф, а не вывод типов.
                if (_symbols.Resolve(qualifier, call.Line) is null)
                {
                    continue;
                }

                if (!TryGetType(qualifier, routine, call.Line, out var type))
                {
                    unresolved.Add(new UnresolvedCall(call, qualifier, $"тип переменной «{qualifier}» не выведен", routine));
                    continue;
                }

                if (PlatformTypeNames.IsKnown(type) || _platform?.IsKnownType(type) == true)
                {
                    resolved.Add(new ResolvedPlatformCall(call, type, type + "." + call.Method, routine));
                }
                else
                {
                    unresolved.Add(new UnresolvedCall(call, qualifier, $"тип «{type}» не отнесён к типам платформы", routine));
                }
            }

            return (resolved, unresolved);
        }

        private IEnumerable<(string? Routine, BslCall Call)> EnumerateCalls()
        {
            foreach (var call in _module.Calls)
            {
                yield return (null, call);
            }

            foreach (var routine in _module.Routines)
            {
                foreach (var call in routine.Calls)
                {
                    yield return (routine.Name, call);
                }
            }
        }

        /// <summary>Разрешает присваивание в имя типа.</summary>
        private bool TryResolveAssignment(Assignment assignment, out string type, out InferredTypeSource source)
        {
            source = InferredTypeSource.Assignment;
            switch (assignment.Kind)
            {
                case ExpressionKind.Constructor:
                    type = assignment.Detail!;
                    source = InferredTypeSource.Constructor;
                    return true;

                case ExpressionKind.ConstructorByName:
                    type = assignment.Detail!;
                    source = InferredTypeSource.ConstructorByName;
                    return true;

                case ExpressionKind.TypeDescription:
                    type = "ОписаниеТипов";
                    source = InferredTypeSource.TypeDescription;
                    return true;

                case ExpressionKind.CreateOrCopy:
                    if (TryGetType(assignment.Detail!, assignment.Routine, assignment.Line, out type))
                    {
                        source = InferredTypeSource.CreateOrCopy;
                        return true;
                    }

                    return false;

                case ExpressionKind.Alias:
                    if (TryGetType(assignment.Detail!, assignment.Routine, assignment.Line, out type))
                    {
                        source = InferredTypeSource.Assignment;
                        return true;
                    }

                    return false;

                default:
                    type = string.Empty;
                    return false;
            }
        }

        /// <summary>Тип имени в области процедуры, затем в области модуля.</summary>
        private bool TryGetType(string name, string? routine, int line, out string type)
        {
            if (routine is not null && Lookup(routine, name, line, out type))
            {
                return true;
            }

            return Lookup(string.Empty, name, line, out type);
        }

        private bool Lookup(string scope, string name, int line, out string type)
        {
            type = string.Empty;
            if (!_scopeTypes.TryGetValue(scope, out var entries) ||
                !entries.TryGetValue(name, out var entry) ||
                entry.Line > line)
            {
                return false;
            }

            type = entry.TypeName;
            return true;
        }

        /// <summary>
        /// Записывает тип имени. Присваивания перекрывают прежний тип (побеждает более позднее),
        /// типы параметров заполняют пустое место только один раз: повторный вызов с другим типом
        /// аргумента тип параметра не меняет.
        /// </summary>
        private bool SetType(string? scope, string name, string typeName, InferredTypeSource source, int line, int column)
        {
            var key = scope ?? string.Empty;
            if (!_scopeTypes.TryGetValue(key, out var entries))
            {
                entries = new Dictionary<string, TypeEntry>(StringComparer.OrdinalIgnoreCase);
                _scopeTypes[key] = entries;
            }

            if (entries.TryGetValue(name, out var existing))
            {
                if (existing.TypeName.Equals(typeName, StringComparison.OrdinalIgnoreCase) ||
                    source == InferredTypeSource.Parameter ||
                    existing.Line > line)
                {
                    return false;
                }
            }

            entries[name] = new TypeEntry(typeName, source, line, column);
            return true;
        }

        /// <summary>Сводка «имя → тип» по модулю: сначала переменные модуля, затем процедуры по порядку.</summary>
        private Dictionary<string, string> FlatTypes()
        {
            var flat = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (_scopeTypes.TryGetValue(string.Empty, out var module))
            {
                foreach (var (name, entry) in module)
                {
                    flat[name] = entry.TypeName;
                }
            }

            foreach (var scope in _symbols.Scopes)
            {
                if (scope.Routine is not { Length: > 0 } routine || !_scopeTypes.TryGetValue(routine, out var entries))
                {
                    continue;
                }

                foreach (var (name, entry) in entries)
                {
                    flat.TryAdd(name, entry.TypeName);
                }
            }

            return flat;
        }
    }
}
