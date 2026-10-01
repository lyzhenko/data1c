using Data1c.Core.Bsl;
using Data1c.Core.Metadata;
using Data1c.Core.Platform;

namespace Data1c.Core.Analysis;

/// <summary>
/// Проверка черновика модуля BSL своими силами, без запуска 1С: заведомые ошибки (неизвестный вызов,
/// неверное число аргументов, отсутствующий объект метаданных, функция без «Возврат», недостижимый код)
/// и подозрительные места (неиспользуемые переменные и параметры).
/// </summary>
/// <remarks>
/// <para>Структура модуля берётся из <see cref="BslModuleParser"/>, факты о конфигурации — из
/// <see cref="IDraftContext"/> (обычно поверх SQLite-индекса), методы платформы проверяются по
/// <see cref="PlatformHelpIndex"/>. Полный вывод типов не делается: обращения к реквизитам через
/// переменные («Объект.Артикул») и вызовы методов объектов не проверяются — для них тип неизвестен.</para>
/// <para>Если модель платформы не подключена, неизвестный локальный вызов может оказаться глобальной
/// функцией платформы («Сообщить»), поэтому такое замечание понижается до предупреждения.</para>
/// <para>Устроено так, чтобы платформенная проверка (1cv8 DESIGNER /CheckModules) добавлялась рядом:
/// её результат — те же <see cref="DraftProblem"/>, и список замечаний просто станет длиннее.</para>
/// </remarks>
public sealed class DraftCheck
{
    /// <summary>Имя модуля, когда черновик проверяется без пути: нужно для подписей в ответе.</summary>
    public const string DefaultModulePath = "черновик.bsl";

    /// <summary>Ключевые слова объявления переменных: русское и английское написание.</summary>
    private static readonly string[] VariableKeywords = ["Перем", "Var"];

    /// <summary>Слова, которыми закрывается блок: после «Возврат» они не считаются недостижимым кодом.</summary>
    private static readonly string[] BlockEndKeywords =
        ["КонецПроцедуры", "КонецФункции", "КонецЕсли", "КонецЦикла", "КонецПопытки", "Иначе", "ИначеЕсли", "Исключение",
         "EndProcedure", "EndFunction", "EndIf", "EndDo", "EndTry", "Else", "ElsIf", "Except"];

    private readonly IDraftContext? _context;
    private readonly PlatformHelpIndex? _platform;
    private readonly BslModuleParser _parser = new();
    private readonly Lock _gate = new();

    /// <summary>Создаёт проверку над фактами о конфигурации и (необязательно) справкой платформы.</summary>
    /// <param name="context">Факты о конфигурации: процедуры, объекты метаданных. Может отсутствовать.</param>
    /// <param name="platform">Модель платформы для проверки методов платформы. Может отсутствовать.</param>
    public DraftCheck(IDraftContext? context = null, PlatformHelpIndex? platform = null)
    {
        _context = context;
        _platform = platform;
    }

    /// <summary>Проверяет текст модуля или его черновик.</summary>
    /// <param name="text">Текст модуля BSL.</param>
    /// <param name="modulePath">Путь модуля внутри выгрузки; null — черновик без места.</param>
    public DraftCheckResult Check(string? text, string? modulePath = null)
    {
        var source = text ?? string.Empty;
        var path = string.IsNullOrWhiteSpace(modulePath) ? DefaultModulePath : modulePath;
        var module = Parse(source, path);
        var tokens = BslLexer.Tokenize(source);
        var problems = new List<DraftProblem>();
        var notes = new List<string>();

        var platform = _platform is { IsAvailable: true } ? _platform : null;
        if (platform is null)
        {
            notes.Add("Справка платформы не подключена: методы платформы не проверяются, "
                + "а неизвестный локальный вызов может оказаться глобальной функцией платформы.");
        }

        if (_context is null)
        {
            notes.Add("Индекс конфигурации недоступен: проверены только правила внутри самого модуля.");
        }

        var routines = new Dictionary<string, BslRoutine>(StringComparer.OrdinalIgnoreCase);
        foreach (var routine in module.Routines)
        {
            routines.TryAdd(routine.Name, routine);
        }

        CheckCalls(module, routines, platform, problems);
        CheckMetadataAccesses(module, problems);
        CheckDeclarations(module, tokens, problems);
        CheckRoutines(module, tokens, problems);

        return new DraftCheckResult(
            [.. problems.OrderBy(static problem => problem.Line).ThenBy(static problem => problem.Severity)],
            notes);
    }

    private BslModuleInfo Parse(string text, string path)
    {
        // Разборщик хранит состояние в полях: одновременные проверки ждут друг друга, но не портят результат.
        lock (_gate)
        {
            return _parser.Parse(new BslModuleSource(path, text, null, BslModuleKinds.FromPath(path)));
        }
    }

    /// <summary>Проверяет вызовы: разрешение имени, число аргументов, процедура вместо функции.</summary>
    private void CheckCalls(
        BslModuleInfo module,
        IReadOnlyDictionary<string, BslRoutine> routines,
        PlatformHelpIndex? platform,
        List<DraftProblem> problems)
    {
        foreach (var call in EnumerateCalls(module))
        {
            // «ВызватьИсключение "текст"» — оператор языка, а не вызов процедуры: некоторые пишут
            // его со скобками, и такой «вызов» не должен считаться неизвестной процедурой.
            if (IsLanguageOperator(call.Callee))
            {
                continue;
            }

            if (call.Qualifier is null)
            {
                if (routines.TryGetValue(call.Method, out var local))
                {
                    CheckCallTarget(
                        call,
                        local.Name,
                        local.Kind == BslRoutineKind.Function,
                        local.Parameters.Count,
                        problems);
                    continue;
                }

                var symbols = FindExact(call.Method);
                if (symbols.Count > 0)
                {
                    CheckCallTarget(call, symbols[0].Name, symbols[0].IsFunction, symbols[0].Parameters.Count, problems);
                    continue;
                }

                if (ResolvePlatform(platform, call) is { } localPlatform)
                {
                    CheckPlatformCall(call, localPlatform, problems);
                    continue;
                }

                problems.Add(new DraftProblem(
                    call.Line,
                    platform is null ? DraftProblemSeverity.Warning : DraftProblemSeverity.Error,
                    DraftProblemKind.UnknownProcedure,
                    $"Неизвестная процедура или функция «{call.Method}»: её нет ни в модуле, ни в конфигурации.",
                    SimilarSymbolsHint(call.Method)));
                continue;
            }

            // Вызов через объект или переменную: «Справочники.Товары.СоздатьЭлемент()», «Объект.Записать()».
            // Тип получателя не выводится, поэтому такие вызовы не судим.
            if (call.Qualifier.Contains('.', StringComparison.Ordinal))
            {
                continue;
            }

            if (ResolvePlatform(platform, call) is { } platformTopic)
            {
                CheckPlatformCall(call, platformTopic, problems);
                continue;
            }

            var ownerId = "CommonModule." + call.Qualifier;
            var owned = FindExact(call.Method)
                .Where(symbol => string.Equals(symbol.OwnerId, ownerId, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (owned.Count > 0)
            {
                CheckCallTarget(call, owned[0].Name, owned[0].IsFunction, owned[0].Parameters.Count, problems);
                continue;
            }

            // Замечание выдаём только тогда, когда общий модуль точно есть в конфигурации:
            // иначе «Имя.Метод()» — это обращение через переменную, а не вызов общего модуля.
            if (_context?.GetMetadataObject(ownerId) is { } owner && !string.IsNullOrEmpty(owner.Name))
            {
                problems.Add(new DraftProblem(
                    call.Line,
                    DraftProblemSeverity.Error,
                    DraftProblemKind.UnknownCommonModuleMethod,
                    $"В общем модуле «{call.Qualifier}» нет метода «{call.Method}».",
                    OwnedSymbolsHint(ownerId)));
            }
        }
    }

    /// <summary>
    /// Ищет вызов в справке платформы: сначала по полному имени («Массив.Добавить»), затем для
    /// вызова без получателя — среди глобальных функций («Сообщить» — это «Глобальный контекст.Сообщить»).
    /// </summary>
    private static PlatformTopic? ResolvePlatform(PlatformHelpIndex? platform, BslCall call)
    {
        if (platform is null)
        {
            return null;
        }

        if (platform.ContainsMember(call.Callee))
        {
            return platform.Find(call.Callee);
        }

        return call.Qualifier is null ? platform.FindGlobalFunction(call.Method) : null;
    }

    /// <summary>Проверяет число аргументов метода платформы, если его удалось узнать из справки.</summary>
    private static void CheckPlatformCall(BslCall call, PlatformTopic topic, List<DraftProblem> problems)
    {
        var passed = call.ArgumentCount;
        if (passed < 0 || !PlatformSyntax.TryGetParameterBounds(topic.Text, out var required, out var total))
        {
            return;
        }

        var expected = required == total
            ? $"ожидается параметров {total}"
            : $"ожидается параметров от {required} до {total}";

        if (passed > total)
        {
            problems.Add(new DraftProblem(
                call.Line,
                DraftProblemSeverity.Error,
                DraftProblemKind.WrongArgumentCount,
                $"«{call.Callee}»: {expected}, передано {passed}.",
                "Справка платформы: " + topic.Title));
        }
        else if (passed < required)
        {
            problems.Add(new DraftProblem(
                call.Line,
                DraftProblemSeverity.Error,
                DraftProblemKind.WrongArgumentCount,
                $"«{call.Callee}»: ожидается не меньше {required} параметров, передано {passed}.",
                "Справка платформы: " + topic.Title));
        }
    }

    /// <summary>Ключевые слова языка, которые иногда записывают со скобками как вызов.</summary>
    private static bool IsLanguageOperator(string callee) =>
        callee.Equals("ВызватьИсключение", StringComparison.OrdinalIgnoreCase) ||
        callee.Equals("Raise", StringComparison.OrdinalIgnoreCase);

    /// <summary>Проверки, которые нужны для разрешённого вызова: аргументы и вид цели.</summary>
    private static void CheckCallTarget(
        BslCall call,
        string targetName,
        bool isFunction,
        int parameterCount,
        List<DraftProblem> problems)
    {
        if (call.ArgumentCount >= 0 && parameterCount >= 0 && call.ArgumentCount != parameterCount)
        {
            if (call.ArgumentCount > parameterCount)
            {
                problems.Add(new DraftProblem(
                    call.Line,
                    DraftProblemSeverity.Error,
                    DraftProblemKind.WrongArgumentCount,
                    $"«{targetName}»: ожидается параметров {parameterCount}, передано {call.ArgumentCount}."));
            }
            else
            {
                problems.Add(new DraftProblem(
                    call.Line,
                    DraftProblemSeverity.Warning,
                    DraftProblemKind.WrongArgumentCount,
                    $"«{targetName}»: ожидается параметров {parameterCount}, передано {call.ArgumentCount}.",
                    "Часть параметров может быть необязательной — сверьтесь с заголовком процедуры."));
            }
        }

        if (!isFunction && call.ResultUsed == true)
        {
            problems.Add(new DraftProblem(
                call.Line,
                DraftProblemSeverity.Error,
                DraftProblemKind.ProcedureUsedAsFunction,
                $"Процедура «{targetName}» вызвана как функция: у процедуры нет результата."));
        }
        else if (isFunction && call.ResultUsed == false)
        {
            problems.Add(new DraftProblem(
                call.Line,
                DraftProblemSeverity.Info,
                DraftProblemKind.FunctionUsedAsProcedure,
                $"Результат функции «{targetName}» не используется."));
        }
    }

    /// <summary>Проверяет обращения вида «Справочники.Товары» по составу конфигурации.</summary>
    private void CheckMetadataAccesses(BslModuleInfo module, List<DraftProblem> problems)
    {
        if (_context is null)
        {
            return;
        }

        foreach (var access in EnumerateAccesses(module))
        {
            if (access.Kind.IsUnknown || access.Kind == MdKind.MetadataRoot)
            {
                continue;
            }

            var id = MdNaming.CreateId(access.Kind, access.ObjectName);
            if (_context.GetMetadataObject(id) is not null)
            {
                continue;
            }

            // Секция выгрузки могла не разбираться вовсе: тогда «объекта нет» ничего не значит.
            if (!_context.HasMetadataKind(access.Kind.Name))
            {
                continue;
            }

            var similar = _context.FindMetadataObjects(access.ObjectName, 5)
                .Where(candidate => !string.Equals(candidate.Id, id, StringComparison.Ordinal))
                .Select(static candidate => candidate.Id)
                .Distinct(StringComparer.Ordinal)
                .Take(3)
                .ToList();

            problems.Add(new DraftProblem(
                access.Line,
                DraftProblemSeverity.Error,
                DraftProblemKind.UnknownMetadataObject,
                $"Объект метаданных «{access.Text}» не найден в конфигурации.",
                similar.Count > 0 ? "Похожие объекты: " + string.Join(", ", similar) + "." : null));
        }
    }

    /// <summary>Ищет объявленные и нигде не использованные переменные и параметры.</summary>
    private static void CheckDeclarations(BslModuleInfo module, IReadOnlyList<BslToken> tokens, List<DraftProblem> problems)
    {
        var declarations = new List<Declaration>();
        CollectVariables(module, tokens, declarations);

        foreach (var routine in module.Routines)
        {
            foreach (var parameter in routine.Parameters)
            {
                declarations.Add(new Declaration(
                    parameter,
                    routine.StartLine,
                    routine.StartLine,
                    routine.EndLine,
                    DraftProblemKind.UnusedParameter));
            }
        }

        foreach (var declaration in declarations)
        {
            var occurrences = 0;
            foreach (var token in tokens)
            {
                if (token.Kind != BslTokenKind.Identifier ||
                    token.Line < declaration.ScopeStart ||
                    token.Line > declaration.ScopeEnd ||
                    !token.Span.Equals(declaration.Name, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                occurrences++;
            }

            if (occurrences > 1)
            {
                continue;
            }

            problems.Add(new DraftProblem(
                declaration.DeclarationLine,
                DraftProblemSeverity.Warning,
                declaration.Kind,
                declaration.Kind == DraftProblemKind.UnusedParameter
                    ? $"Параметр «{declaration.Name}» не используется."
                    : $"Переменная «{declaration.Name}» объявлена, но не используется."));
        }
    }

    /// <summary>Собирает объявления «Перем …;»: имя, строка объявления и область видимости.</summary>
    private static void CollectVariables(BslModuleInfo module, IReadOnlyList<BslToken> tokens, List<Declaration> declarations)
    {
        for (var index = 0; index < tokens.Count; index++)
        {
            var token = tokens[index];
            if (token.Kind != BslTokenKind.Keyword || !IsAny(token, VariableKeywords))
            {
                continue;
            }

            var (scopeStart, scopeEnd) = ScopeOf(module, token.Line);
            var cursor = index + 1;
            while (cursor < tokens.Count)
            {
                var current = tokens[cursor];
                if (current.Kind is BslTokenKind.NewLine or BslTokenKind.Comment or BslTokenKind.Directive)
                {
                    cursor++;
                    continue;
                }

                if (current.Kind == BslTokenKind.Operator && current.Span.SequenceEqual(";"))
                {
                    break;
                }

                if (current.Kind == BslTokenKind.Identifier)
                {
                    declarations.Add(new Declaration(
                        current.GetText(),
                        current.Line,
                        scopeStart,
                        scopeEnd,
                        DraftProblemKind.UnusedVariable));
                }

                cursor++;
            }

            index = cursor;
        }
    }

    /// <summary>Область видимости объявления: строки процедуры, внутри которой оно стоит, иначе — весь модуль.</summary>
    private static (int Start, int End) ScopeOf(BslModuleInfo module, int line)
    {
        foreach (var routine in module.Routines)
        {
            if (line >= routine.StartLine && line <= routine.EndLine)
            {
                return (routine.StartLine, routine.EndLine);
            }
        }

        return (1, Math.Max(1, module.LineCount));
    }

    /// <summary>Проверяет процедуры и функции: «Возврат» у функции и недостижимый код после «Возврат».</summary>
    private static void CheckRoutines(BslModuleInfo module, IReadOnlyList<BslToken> tokens, List<DraftProblem> problems)
    {
        foreach (var routine in module.Routines)
        {
            if (routine.Kind == BslRoutineKind.Function && !HasToken(tokens, routine, "Возврат", "Return"))
            {
                problems.Add(new DraftProblem(
                    routine.StartLine,
                    DraftProblemSeverity.Error,
                    DraftProblemKind.FunctionWithoutReturn,
                    $"Функция «{routine.Name}» не содержит оператора «Возврат»."));
            }

            foreach (var line in UnreachableLines(routine, tokens))
            {
                problems.Add(new DraftProblem(
                    line,
                    DraftProblemSeverity.Warning,
                    DraftProblemKind.CodeAfterReturn,
                    $"Код после «Возврат» в «{routine.Name}» не выполняется."));
            }
        }
    }

    /// <summary>Есть ли в строках процедуры такой токен-ключевое слово.</summary>
    private static bool HasToken(IReadOnlyList<BslToken> tokens, BslRoutine routine, params string[] names)
    {
        foreach (var token in tokens)
        {
            if (token.Line < routine.StartLine || token.Line > routine.EndLine)
            {
                continue;
            }

            if (token.Kind == BslTokenKind.Keyword && IsAny(token, names))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Строки недостижимого кода: оператор, который стоит после завершённого «Возврат …;».
    /// Закрывающие слова блоков («КонецЕсли», «Иначе») недостижимым кодом не считаются.
    /// </summary>
    private static IEnumerable<int> UnreachableLines(BslRoutine routine, IReadOnlyList<BslToken> tokens)
    {
        var reported = new HashSet<int>();
        for (var index = 0; index < tokens.Count; index++)
        {
            var token = tokens[index];
            if (token.Line < routine.StartLine || token.Line > routine.EndLine ||
                token.Kind != BslTokenKind.Keyword || !IsAny(token, "Возврат", "Return"))
            {
                continue;
            }

            var end = StatementEnd(tokens, index, routine.EndLine);
            if (end < 0)
            {
                continue;
            }

            var next = NextMeaningful(tokens, end + 1, routine.EndLine);
            if (next < 0)
            {
                continue;
            }

            var candidate = tokens[next];
            if (candidate.Kind == BslTokenKind.Keyword && IsAny(candidate, BlockEndKeywords))
            {
                continue;
            }

            if (reported.Add(candidate.Line))
            {
                yield return candidate.Line;
            }
        }
    }

    /// <summary>Индекс завершающей «;» оператора «Возврат»; −1 — оператор не завершён в границах процедуры.</summary>
    private static int StatementEnd(IReadOnlyList<BslToken> tokens, int start, int maxLine)
    {
        var depth = 0;
        for (var index = start + 1; index < tokens.Count; index++)
        {
            var token = tokens[index];
            if (token.Line > maxLine)
            {
                return -1;
            }

            if (token.Kind == BslTokenKind.Operator)
            {
                if (token.Span.SequenceEqual("(") || token.Span.SequenceEqual("["))
                {
                    depth++;
                }
                else if (token.Span.SequenceEqual(")") || token.Span.SequenceEqual("]"))
                {
                    depth--;
                }
                else if (depth == 0 && token.Span.SequenceEqual(";"))
                {
                    return index;
                }

                continue;
            }

            if (depth == 0 && token.Kind == BslTokenKind.Keyword && IsAny(token, "КонецПроцедуры", "КонецФункции"))
            {
                return -1;
            }
        }

        return -1;
    }

    /// <summary>Ближайший значимый токен процедуры, начиная с позиции <paramref name="start"/>; −1 — такого нет.</summary>
    private static int NextMeaningful(IReadOnlyList<BslToken> tokens, int start, int maxLine)
    {
        for (var index = start; index < tokens.Count; index++)
        {
            var token = tokens[index];
            if (token.Line > maxLine)
            {
                return -1;
            }

            if (token.Kind is BslTokenKind.NewLine or BslTokenKind.Comment or BslTokenKind.Directive)
            {
                continue;
            }

            return index;
        }

        return -1;
    }

    /// <summary>Все вызовы модуля: и в теле процедур, и в коде модуля.</summary>
    private static IEnumerable<BslCall> EnumerateCalls(BslModuleInfo module) =>
        module.Calls.Concat(module.Routines.SelectMany(static routine => routine.Calls));

    /// <summary>Все обращения к метаданным: и в теле процедур, и в коде модуля.</summary>
    private static IEnumerable<BslMetadataAccess> EnumerateAccesses(BslModuleInfo module) =>
        module.MetadataAccesses.Concat(module.Routines.SelectMany(static routine => routine.MetadataAccesses));

    /// <summary>Точные совпадения имени среди процедур конфигурации.</summary>
    private IReadOnlyList<DraftSymbol> FindExact(string name) =>
        _context?.FindSymbols(name, 10, exact: true) ?? [];

    /// <summary>Подсказка с похожими именами процедур: помогает при опечатке в вызове.</summary>
    private string? SimilarSymbolsHint(string name)
    {
        if (_context is null)
        {
            return null;
        }

        var similar = _context.FindSymbols(name, 5)
            .Select(static symbol => symbol.Name)
            .Where(candidate => !string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .ToList();

        return similar.Count > 0 ? "Похожие имена: " + string.Join(", ", similar) + "." : null;
    }

    /// <summary>Подсказка с методами общего модуля: «общего модуля/метода нет» без примеров бесполезно.</summary>
    private string? OwnedSymbolsHint(string ownerId)
    {
        if (_context is null)
        {
            return null;
        }

        var names = _context.FindSymbolsByOwner(ownerId, 50)
            .Select(static symbol => symbol.Name)
            .Distinct(StringComparer.Ordinal)
            .Take(5)
            .ToList();

        return names.Count > 0 ? "Известные методы модуля: " + string.Join(", ", names) + "." : null;
    }

    private static bool IsAny(BslToken token, params string[] names)
    {
        foreach (var name in names)
        {
            if (token.Span.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Объявленная переменная или параметр и область, в которой ищутся её употребления.</summary>
    private sealed record Declaration(
        string Name,
        int DeclarationLine,
        int ScopeStart,
        int ScopeEnd,
        DraftProblemKind Kind);
}
