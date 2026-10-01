using Data1c.Core.Bsl;
using Data1c.Core.Dump;
using Data1c.Core.Metadata;
using Data1c.Core.Platform;

namespace Data1c.Core.Analysis;

/// <summary>
/// Проверка черновика модуля BSL своими силами, без запуска 1С: заведомые ошибки (неизвестный вызов,
/// неверное число аргументов, вызов неэкспортного метода чужого модуля, отсутствующий объект метаданных,
/// функция без «Возврат», недостижимый код) и подозрительные места (неиспользуемые переменные и параметры).
/// </summary>
/// <remarks>
/// <para>Структура модуля берётся из <see cref="BslModuleParser"/>, факты о конфигурации — из
/// <see cref="IDraftContext"/> (обычно поверх SQLite-индекса), методы платформы проверяются по
/// <see cref="PlatformHelpIndex"/>. Полный вывод типов не делается: обращения к реквизитам через
/// переменные («Объект.Артикул») и вызовы методов объектов не проверяются — для них тип неизвестен.</para>
/// <para>Если модель платформы не подключена, неизвестный локальный вызов может оказаться глобальной
/// функцией платформы («Сообщить»), поэтому такое замечание понижается до предупреждения.</para>
/// <para>Число аргументов проверяется только там, где цель вызова известна точно: у процедуры самого
/// модуля и у метода общего модуля, названного с квалификатором. Вызов без квалификатора, имя которого
/// нашлось лишь в другом модуле, не судится: одноимённые процедуры разных модулей объявлены по-разному.</para>
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

    /// <summary>Имена параметров, которые платформа передаёт обработчикам событий и команд формы.</summary>
    private static readonly string[] StandardHandlerParameters =
        ["Отказ", "СтандартнаяОбработка", "Параметры", "Элемент", "Команда", "ИмяСобытия"];

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

        CheckCalls(module, routines, platform, path, problems);
        CheckMetadataAccesses(module, problems);
        CheckDeclarations(module, tokens, path, problems);
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

    /// <summary>Проверяет вызовы: разрешение имени, число аргументов, вид цели, доступность из другого модуля.</summary>
    /// <param name="module">Разобранный черновик.</param>
    /// <param name="routines">Процедуры самого черновика по имени.</param>
    /// <param name="platform">Справка платформы, если подключена.</param>
    /// <param name="path">Путь проверяемого модуля: по нему видно, «свой» это модуль или чужой.</param>
    /// <param name="problems">Список замечаний, который дополняется.</param>
    private void CheckCalls(
        BslModuleInfo module,
        IReadOnlyDictionary<string, BslRoutine> routines,
        PlatformHelpIndex? platform,
        string path,
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
                        local.Parameters,
                        local.RequiredCount,
                        problems);
                    continue;
                }

                // Имя найдено в другом модуле: без квалификатора это скорее всего глобальный метод
                // общего модуля, но одноимённые процедуры разных модулей объявлены по-разному.
                // Судить по первому совпадению нельзя — замечание было бы ложным, поэтому вызов
                // лишь перестаёт считаться неизвестным.
                if (FindExact(call.Method).Count > 0)
                {
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
                CheckCallTarget(
                    call,
                    owned[0].Name,
                    owned[0].IsFunction,
                    owned[0].Parameters,
                    owned[0].RequiredCount,
                    problems);
                CheckModuleExport(call, owned[0], path, problems);
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
    /// <param name="call">Вызов в черновике.</param>
    /// <param name="targetName">Имя вызываемой процедуры или функции.</param>
    /// <param name="isFunction">Цель — функция, а не процедура.</param>
    /// <param name="parameters">Объявленные параметры цели в порядке объявления.</param>
    /// <param name="required">Сколько параметров обязательно передать.</param>
    /// <param name="problems">Список замечаний, который дополняется.</param>
    private static void CheckCallTarget(
        BslCall call,
        string targetName,
        bool isFunction,
        IReadOnlyList<string> parameters,
        int required,
        List<DraftProblem> problems)
    {
        if (call.ArgumentCount >= 0)
        {
            // Обязательных параметров может быть меньше объявленных: у остальных есть значение
            // по умолчанию («Режим = Неопределено»), и передавать их не нужно. А вот лишний
            // аргумент и недостача обязательных — ошибка компиляции.
            var mandatory = Math.Clamp(required, 0, parameters.Count);
            if (call.ArgumentCount > parameters.Count)
            {
                problems.Add(new DraftProblem(
                    call.Line,
                    DraftProblemSeverity.Error,
                    DraftProblemKind.WrongArgumentCount,
                    $"«{targetName}»: ожидается параметров {parameters.Count}, передано {call.ArgumentCount}.",
                    parameters.Count == 0 ? null : "Объявленные параметры: " + ParameterNames(parameters) + "."));
            }
            else if (call.ArgumentCount < mandatory)
            {
                var names = parameters.Take(mandatory).ToList();
                problems.Add(new DraftProblem(
                    call.Line,
                    DraftProblemSeverity.Error,
                    DraftProblemKind.WrongArgumentCount,
                    $"«{targetName}»: ожидается {mandatory} ({ParameterNames(names)}), "
                        + $"передано {call.ArgumentCount}.",
                    DefaultsHint(parameters, mandatory)));
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

    /// <summary>
    /// Подсказка про параметры со значениями по умолчанию: без неё сообщение о недостаче выглядит
    /// так, будто передать нужно все объявленные параметры.
    /// </summary>
    private static string? DefaultsHint(IReadOnlyList<string> parameters, int required)
    {
        if (parameters.Count == 0)
        {
            return null;
        }

        return required >= parameters.Count
            ? "Объявленные параметры: " + ParameterNames(parameters) + ". Значений по умолчанию у них нет."
            : $"Параметры со значениями по умолчанию: {ParameterNames([.. parameters.Skip(required)])}.";
    }

    /// <summary>
    /// Проверяет доступность метода общего модуля из проверяемого модуля: без ключевого слова
    /// «Экспорт» метод виден только внутри своего модуля. Вызов из своего же модуля законен —
    /// на реальной выгрузке таких вызовов сотни тысяч, и замечание на них было бы ложным.
    /// </summary>
    /// <param name="call">Вызов вида «ОбщийМодуль.Метод()».</param>
    /// <param name="target">Найденное объявление метода.</param>
    /// <param name="path">Путь проверяемого модуля.</param>
    /// <param name="problems">Список замечаний, который дополняется.</param>
    private static void CheckModuleExport(
        BslCall call,
        DraftSymbol target,
        string path,
        List<DraftProblem> problems)
    {
        if (target.IsExport || IsSameModule(path, target.ModulePath))
        {
            return;
        }

        problems.Add(new DraftProblem(
            call.Line,
            DraftProblemSeverity.Error,
            DraftProblemKind.MethodNotExported,
            $"Метод «{call.Qualifier}.{target.Name}» не экспортирован: без «Экспорт» он доступен "
                + "только внутри своего модуля.",
            $"Объявление: {target.ModulePath}, строка {target.StartLine}."));
    }

    /// <summary>Проверяемый модуль и модуль объявления — один и тот же файл.</summary>
    private static bool IsSameModule(string path, string modulePath) =>
        string.Equals(DumpPath.Normalize(path), DumpPath.Normalize(modulePath), StringComparison.OrdinalIgnoreCase);

    /// <summary>Имена параметров для сообщения: длинный список обрезается, чтобы не раздувать ответ.</summary>
    private static string ParameterNames(IReadOnlyList<string> parameters)
    {
        const int limit = 8;
        return parameters.Count <= limit
            ? string.Join(", ", parameters)
            : string.Join(", ", parameters.Take(limit)) + ", …";
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
    /// <param name="module">Разобранный черновик.</param>
    /// <param name="tokens">Токены черновика: по ним считаются употребления.</param>
    /// <param name="path">Путь проверяемого модуля: по нему ищутся зарегистрированные обработчики формы.</param>
    /// <param name="problems">Список замечаний, который дополняется.</param>
    private void CheckDeclarations(
        BslModuleInfo module,
        IReadOnlyList<BslToken> tokens,
        string path,
        List<DraftProblem> problems)
    {
        var declarations = new List<Declaration>();
        CollectVariables(module, tokens, declarations);

        foreach (var routine in module.Routines)
        {
            foreach (var parameter in routine.Parameters)
            {
                if (CountOccurrences(tokens, parameter, routine.StartLine, routine.EndLine) > 1)
                {
                    continue;
                }

                // Обработчики вызывает платформа, а не код: её параметры (например, «Отказ») в теле
                // процедуры могут не употребляться вовсе, и это не оплошность автора.
                if (IsEventHandler(routine, path))
                {
                    continue;
                }

                problems.Add(new DraftProblem(
                    routine.StartLine,
                    DraftProblemSeverity.Warning,
                    DraftProblemKind.UnusedParameter,
                    $"Параметр «{parameter}» не используется."));
            }
        }

        foreach (var declaration in declarations)
        {
            if (CountOccurrences(tokens, declaration.Name, declaration.ScopeStart, declaration.ScopeEnd) > 1)
            {
                continue;
            }

            problems.Add(new DraftProblem(
                declaration.DeclarationLine,
                DraftProblemSeverity.Warning,
                declaration.Kind,
                $"Переменная «{declaration.Name}» объявлена, но не используется."));
        }
    }

    /// <summary>Сколько раз имя встречается в токенах указанного диапазона строк.</summary>
    private static int CountOccurrences(IReadOnlyList<BslToken> tokens, string name, int startLine, int endLine)
    {
        var occurrences = 0;
        foreach (var token in tokens)
        {
            if (token.Kind == BslTokenKind.Identifier &&
                token.Line >= startLine &&
                token.Line <= endLine &&
                token.Span.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                occurrences++;
            }
        }

        return occurrences;
    }

    /// <summary>
    /// Считается ли процедура обработчиком, параметры которого задаёт платформа. Признак берётся
    /// из данных: обработчики формы и её команд лежат в индексе с путём модуля формы. Если данных
    /// нет (черновик без места или форма ещё не описана), остаётся проверка по штатным именам
    /// параметров: у обработчика события они всегда платформенные.
    /// </summary>
    /// <param name="routine">Процедура или функция черновика.</param>
    /// <param name="path">Путь проверяемого модуля.</param>
    private bool IsEventHandler(BslRoutine routine, string path)
    {
        if (_context?.IsEventHandler(path, routine.Name) == true)
        {
            return true;
        }

        return routine.Parameters.Count > 0 && routine.Parameters.All(IsStandardHandlerParameter);
    }

    /// <summary>Имя параметра — штатный параметр обработчика.</summary>
    private static bool IsStandardHandlerParameter(string name) =>
        StandardHandlerParameters.Contains(name, StringComparer.OrdinalIgnoreCase);

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
