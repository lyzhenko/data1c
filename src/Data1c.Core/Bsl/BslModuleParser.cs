using Data1c.Core.Metadata;

namespace Data1c.Core.Bsl;

/// <summary>
/// Структурный разбор модуля 1С (BSL) поверх <see cref="BslLexer"/>: процедуры и функции, области,
/// директивы компиляции, вызовы методов и обращения к объектам метаданных.
/// </summary>
/// <remarks>
/// <para>Разборщик сознательно не строит AST и работает за один проход по потоку токенов. Он никогда
/// не бросает исключений на некорректном тексте: все неожиданности попадают в
/// <see cref="BslModuleInfo.Diagnostics"/>.</para>
/// <para>Осознанные упрощения: код внутри строковых литералов не разбирается (строки просматриваются
/// только как тексты запросов — см. <see cref="QueryParser"/>); аргументы вызовов
/// пропускаются целиком, поэтому вызовы и обращения к метаданным внутри аргументов не учитываются;
/// вложенные процедуры (запрещённые в 1С) закрывают предыдущую процедуру и дают диагностику.</para>
/// </remarks>
public sealed class BslModuleParser : IBslModuleParser
{
    /// <summary>Верхняя граница просмотра заголовка: защита от «зависшего» разбора неполного файла.</summary>
    private const int MaxHeaderTokens = 2000;

    /// <summary>Имя корня метаданных в коде: «Метаданные.Справочники.Товары».</summary>
    private const string MetadataRootName = "Метаданные";

    /// <summary>Защита состояния разбора от одновременных вызовов <see cref="Parse"/>.</summary>
    private readonly object _gate = new();

    private readonly List<BslRoutine> _routines = [];
    private readonly List<BslRegion> _regions = [];
    private readonly List<BslDiagnostic> _diagnostics = [];
    private readonly List<BslCall> _moduleCalls = [];
    private readonly List<BslMetadataAccess> _moduleMetadata = [];
    private readonly List<BslQueryReference> _queries = [];
    private readonly List<BslQueryReference> _moduleQueries = [];
    private readonly List<string> _pendingDirectives = [];
    private readonly Stack<RegionFrame> _regionStack = new();
    private readonly Dictionary<string, int> _routineLines = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<RouteResult> _finished = [];

    private IReadOnlyList<BslToken> _tokens = [];
    private int _index;
    private int _lineCount;
    private int _conditionalDepth;
    private RoutineFrame? _routine;

    /// <summary>Разбирает текст модуля BSL в структурированное описание.</summary>
    /// <param name="source">Путь, текст и метаданные расположения модуля.</param>
    /// <returns>Описание модуля; для пустого текста — пустое описание с нулевым числом строк.</returns>
    /// <exception cref="ArgumentNullException">Если <paramref name="source"/> равен <see langword="null"/>.</exception>
    /// <remarks>
    /// Состояние разбора хранится в полях экземпляра, поэтому вызовы сериализуются: один экземпляр
    /// разборщика можно безопасно использовать из нескольких потоков (они будут ждать друг друга).
    /// Чтобы разбирать модули параллельно, создавайте по экземпляру на поток — так делает
    /// <see cref="Analysis.DumpAnalyzer"/>.
    /// </remarks>
    public BslModuleInfo Parse(BslModuleSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        lock (_gate)
        {
            return ParseCore(source);
        }
    }

    private BslModuleInfo ParseCore(BslModuleSource source)
    {
        var text = source.Text ?? string.Empty;
        Reset(text);
        _tokens = BslLexer.Tokenize(text);

        // Строки запросов ищутся по готовым токенам: своей лексической разметки не нужно, а строковые
        // литералы вместе с номером строки открывающей кавычки уже собраны. Ссылки распределяются по
        // процедурам в конце разбора, когда границы процедур известны.
        QueryParser.CollectFromTokens(_tokens, _queries);

        while (_index < _tokens.Count)
        {
            var token = _tokens[_index];
            switch (token.Kind)
            {
                case BslTokenKind.EndOfFile:
                    _index++;
                    break;

                case BslTokenKind.Comment:
                case BslTokenKind.Unexpected:
                case BslTokenKind.NewLine:
                    _index++;
                    break;

                case BslTokenKind.Annotation:
                    _pendingDirectives.Add(token.GetText());
                    _index++;
                    break;

                case BslTokenKind.Directive:
                    HandleDirective(token);
                    break;

                case BslTokenKind.Operator:
                    // Скобки и разделители сами по себе структуры не образуют — их пропускаем.
                    _index++;
                    break;

                case BslTokenKind.Identifier when IsRoutineKeyword(token):
                case BslTokenKind.Keyword when IsRoutineKeyword(token) && _conditionalDepth == 0:
                    HandleRoutineHeader();
                    break;

                case BslTokenKind.Keyword when IsKeywordAt(_index, "КонецПроцедуры") || IsKeywordAt(_index, "КонецФункции"):
                    HandleRoutineEnd(token);
                    break;

                case BslTokenKind.Identifier:
                    HandleChain();
                    break;

                default:
                    _index++;
                    break;
            }
        }

        // Незакрытые процедуры и области завершаются концом модуля; о них сообщаем диагностикой.
        if (_routine is not null)
        {
            var frame = _routine;
            _routine = null;
            _finished.Add(new RouteResult(frame, Math.Max(frame.StartLine, _lineCount)));
            _diagnostics.Add(new BslDiagnostic(
                BslDiagnosticKind.UnclosedRoutine,
                $"Процедура или функция «{frame.Name}» не закрыта.",
                frame.StartLine));
        }

        while (_regionStack.Count > 0)
        {
            var open = _regionStack.Pop();
            _regions.Add(new BslRegion(open.Name, open.StartLine, _lineCount, open.Depth));
            _diagnostics.Add(new BslDiagnostic(
                BslDiagnosticKind.UnclosedRegion,
                $"Область «{open.Name}» не закрыта директивой #КонецОбласти.",
                open.StartLine));
        }

        // Ссылки из запросов приписываются процедуре, которая содержит эту строку; запросы кода модуля
        // (вне процедур) остаются на самом модуле — так же, как обращения к метаданным из кода.
        foreach (var reference in _queries)
        {
            var frame = FindRoutineAt(reference.Line);
            if (frame is null)
            {
                _moduleQueries.Add(reference);
            }
            else
            {
                frame.Queries.Add(reference);
            }
        }

        foreach (var finished in _finished)
        {
            _routines.Add(finished.Frame.ToRoutine(finished.EndLine));
        }

        // Списки копируются: иначе следующий разбор очистил бы уже выданный результат.
        return new BslModuleInfo
        {
            Path = source.Path,
            OwnerId = source.OwnerId,
            Kind = source.Kind,
            Routines = [.. _routines],
            Regions = [.. _regions],
            Calls = [.. _moduleCalls],
            MetadataAccesses = [.. _moduleMetadata],
            QueryReferences = [.. _moduleQueries],
            Diagnostics = [.. _diagnostics],
            LineCount = _lineCount,
        };
    }

    private void Reset(string text)
    {
        _routines.Clear();
        _regions.Clear();
        _diagnostics.Clear();
        _moduleCalls.Clear();
        _moduleMetadata.Clear();
        _queries.Clear();
        _moduleQueries.Clear();
        _pendingDirectives.Clear();
        _regionStack.Clear();
        _routineLines.Clear();
        _finished.Clear();
        _index = 0;
        _conditionalDepth = 0;
        _routine = null;
        _lineCount = CountLines(text);
    }

    /// <summary>Число строк модуля: число переводов строк плюс один; 0 для пустого текста.</summary>
    private static int CountLines(string text)
    {
        if (text.Length == 0)
        {
            return 0;
        }

        var lines = 1;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                lines++;
            }
            else if (text[i] == '\r' && (i + 1 >= text.Length || text[i + 1] != '\n'))
            {
                lines++;
            }
        }

        return lines;
    }

    /// <summary>Токен — ключевое слово «Процедура» или «Функция» (регистр не важен).</summary>
    private static bool IsRoutineKeyword(BslToken token) =>
        token.Span.Equals("Процедура", StringComparison.OrdinalIgnoreCase) ||
        token.Span.Equals("Функция", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Процедура, в тело которой попадает строка. Если границы процедур пересекаются (вложенные
    /// процедуры в 1С запрещены, но в некорректном тексте встречаются), выбирается самая узкая.
    /// </summary>
    private RoutineFrame? FindRoutineAt(int line)
    {
        RoutineFrame? found = null;
        var narrowest = int.MaxValue;
        foreach (var finished in _finished)
        {
            if (line < finished.Frame.StartLine || line > finished.EndLine)
            {
                continue;
            }

            var span = finished.EndLine - finished.Frame.StartLine;
            if (span < narrowest)
            {
                narrowest = span;
                found = finished.Frame;
            }
        }

        return found;
    }

    /// <summary>Обрабатывает директиву препроцессора «#…»: области и условия компиляции.</summary>
    private void HandleDirective(BslToken token)
    {
        // DirectiveKeyWord возвращает слово в нижнем регистре — сравнение идёт с нижним регистром.
        switch (DirectiveKeyWord(token))
        {
            case "область":
                {
                    var frame = new RegionFrame(ExtractDirectiveArgument(token, "Область"), token.Line, _regionStack.Count);
                    _regionStack.Push(frame);
                    _index++;
                    return;
                }

            case "конецобласти":
                {
                    if (_regionStack.Count == 0)
                    {
                        _diagnostics.Add(new BslDiagnostic(
                            BslDiagnosticKind.UnexpectedRegionEnd,
                            "#КонецОбласти без открывающей директивы #Область.",
                            token.Line));
                    }
                    else
                    {
                        var open = _regionStack.Pop();
                        _regions.Add(new BslRegion(open.Name, open.StartLine, token.Line, open.Depth));
                    }

                    _index++;
                    return;
                }

            case "если":
                _conditionalDepth++;
                _index++;
                return;

            case "конецесли":
                if (_conditionalDepth > 0)
                {
                    _conditionalDepth--;
                }

                _index++;
                return;

            default:
                // #Иначе, #Тогда, #Вставка, #Удаление, #Использовать и прочие на структуру не влияют.
                _index++;
                return;
        }
    }

    /// <summary>Первое слово директивы без «#» («Область», «КонецОбласти», «Если» …) в нижнем регистре.</summary>
    private static string DirectiveKeyWord(BslToken token)
    {
        var span = token.Span;
        var start = span.Length > 0 && span[0] == '#' ? 1 : 0;
        var end = start;
        while (end < span.Length && BslLexer.IsIdentifierPart(span[end]))
        {
            end++;
        }

        return end > start ? BslLexer.FoldToLower(token.GetText(), start, end - start) : string.Empty;
    }

    /// <summary>Остаток строки директивы после ключевого слова: имя области в исходном регистре.</summary>
    private static string ExtractDirectiveArgument(BslToken token, string keyword)
    {
        var span = token.Span;
        var start = span.Length > 0 && span[0] == '#' ? 1 : 0;
        start = Math.Min(start + keyword.Length, span.Length);
        return span[start..].Trim().ToString();
    }

    /// <summary>Заголовок процедуры или функции.</summary>
    private void HandleRoutineHeader()
    {
        var keywordLine = _tokens[_index].Line;
        if (_routine is not null)
        {
            // Вложенные процедуры в 1С запрещены: закрываем предыдущую, чтобы разбор не «съел» следующую.
            var previous = _routine;
            _routine = null;
            _finished.Add(new RouteResult(previous, Math.Max(previous.StartLine, keywordLine)));
            _diagnostics.Add(new BslDiagnostic(
                BslDiagnosticKind.UnclosedRoutine,
                $"Процедура или функция «{previous.Name}» не закрыта до начала следующей процедуры или функции.",
                keywordLine));
        }

        var directives = TakePendingDirectives();
        if (!TryParseRoutineHeader(keywordLine, directives, out var frame))
        {
            _diagnostics.Add(new BslDiagnostic(
                BslDiagnosticKind.UnexpectedToken,
                "Не удалось разобрать заголовок процедуры или функции.",
                keywordLine));
            _index++;
            return;
        }

        if (!_routineLines.TryAdd(frame.Name, frame.StartLine))
        {
            _diagnostics.Add(new BslDiagnostic(
                BslDiagnosticKind.DuplicateRoutine,
                $"Имя «{frame.Name}» уже использовано процедурой или функцией (строка {_routineLines[frame.Name]}).",
                frame.StartLine));
        }

        _routine = frame;
        _index = frame.Next;
    }

    /// <summary>Скопированные директивы компиляции, накопившиеся перед заголовком; список очищается.</summary>
    private List<string> TakePendingDirectives()
    {
        var directives = new List<string>(_pendingDirectives);
        _pendingDirectives.Clear();
        return directives;
    }

    private bool TryParseRoutineHeader(int keywordLine, List<string> directives, out RoutineFrame frame)
    {
        frame = null!;
        var count = _tokens.Count;
        var i = SkipTrivia(_index);

        if (i >= count)
        {
            return false;
        }

        var kind = IsKeywordAt(i, "Процедура") ? BslRoutineKind.Procedure : BslRoutineKind.Function;
        i++;

        // Между ключевым словом и именем допустимы переводы строк и комментарии.
        i = SkipTrivia(i);
        if (i >= count || _tokens[i].Kind != BslTokenKind.Identifier)
        {
            return false;
        }

        var nameToken = _tokens[i];
        var name = nameToken.GetText();
        i++;

        var parameters = new List<string>();
        var next = i;
        var open = SkipTrivia(i);
        var hasParameterList = open < count && IsOperator(_tokens[open], "(");
        if (hasParameterList)
        {
            var limit = Math.Min(count, open + 1 + MaxHeaderTokens);
            var close = -1;
            var depth = 0;
            for (var j = open; j < limit; j++)
            {
                var candidate = _tokens[j];
                if (candidate.Kind != BslTokenKind.Operator)
                {
                    continue;
                }

                if (IsOperator(candidate, "("))
                {
                    depth++;
                }
                else if (IsOperator(candidate, ")"))
                {
                    depth--;
                    if (depth == 0)
                    {
                        close = j;
                        break;
                    }
                }
            }

            if (close < 0)
            {
                _diagnostics.Add(new BslDiagnostic(
                    BslDiagnosticKind.UnexpectedToken,
                    $"Список параметров процедуры или функции «{name}» не закрыт скобкой.",
                    nameToken.Line));
                return false;
            }

            parameters = ParseParameters(open + 1, close, nameToken.Line);
            next = close + 1;
        }

        // Слово «Экспорт» может стоять после закрывающей скобки, в том числе через перевод строки,
        // а при отсутствии списка параметров — сразу после имени процедуры или функции.
        var scan = SkipTrivia(next);
        var isExport = false;
        if (scan < count &&
            (!hasParameterList || scan > open) &&
            IsKeywordAt(scan, "Экспорт"))
        {
            isExport = true;
            next = scan + 1;
        }

        // Внимание: next передаётся в конструктор уже окончательным — тело процедуры начинается сразу за заголовком.
        frame = new RoutineFrame(name, kind, isExport, parameters, nameToken.Line, next, directives)
        {
            Region = _regionStack.Count > 0 ? _regionStack.Peek().Name : null,
            Depth = _regionStack.Count,
        };
        return true;
    }

    /// <summary>Собирает имена параметров между скобками; значения по умолчанию пропускает.</summary>
    private List<string> ParseParameters(int start, int end, int line)
    {
        var parameters = new List<string>();
        var i = start;
        while (i < end)
        {
            var token = _tokens[i];
            if (token.Kind == BslTokenKind.Identifier && !IsKeywordAt(i, "Знач"))
            {
                var name = token.GetText();
                if (!parameters.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    parameters.Add(name);
                }

                i++;
                continue;
            }

            if (token.Kind == BslTokenKind.Keyword && IsKeywordAt(i, "Знач"))
            {
                i++;
                continue;
            }

            if (token.Kind == BslTokenKind.Operator && IsOperator(token, "="))
            {
                // Значение по умолчанию пропускаем до запятой или закрывающей скобки,
                // чтобы его содержимое не попало в список параметров.
                var after = i + 1;
                if (after < end && _tokens[after].Kind == BslTokenKind.Identifier)
                {
                    _diagnostics.Add(new BslDiagnostic(
                        BslDiagnosticKind.UnexpectedToken,
                        $"Параметр «{_tokens[after].GetText()}» объявлен без имени.",
                        line));
                }

                i = SkipDefaultValue(after, end);
                continue;
            }

            i++;
        }

        return parameters;
    }

    /// <summary>
    /// Пропускает значение параметра по умолчанию: до запятой на верхнем уровне или до конца списка.
    /// Вложенные скобки и вызовы внутри значения пропускаются целиком.
    /// </summary>
    private int SkipDefaultValue(int index, int end)
    {
        var depth = 0;
        var i = index;
        while (i < end)
        {
            var token = _tokens[i];
            if (token.Kind == BslTokenKind.Operator)
            {
                if (IsOpening(token))
                {
                    depth++;
                }
                else if (IsClosing(token))
                {
                    if (depth == 0)
                    {
                        // Закрывающая скобка списка параметров — значение закончилось.
                        return i;
                    }

                    depth--;
                }
                else if (depth == 0 && IsOperator(token, ","))
                {
                    return i;
                }
            }

            i++;
        }

        return end;
    }

    /// <summary>
    /// Разбирает цепочку идентификаторов с текущей позиции: вызовы и обращения к метаданным попадают
    /// либо в текущую процедуру или функцию, либо, если разбирается код модуля, в описание модуля.
    /// </summary>
    private void HandleChain()
    {
        var accesses = new List<BslMetadataAccess>();
        var records = new List<BslCall>();
        var next = ParseChain(_index, accesses, records, out _);

        if (_routine is null)
        {
            if (accesses.Count > 0)
            {
                _moduleMetadata.AddRange(accesses);
            }

            if (records.Count > 0)
            {
                _moduleCalls.AddRange(records);
            }
        }
        else
        {
            if (accesses.Count > 0)
            {
                _routine.Metadata.AddRange(accesses);
            }

            if (records.Count > 0)
            {
                _routine.Calls.AddRange(records);
            }
        }

        if (records.Count > 0)
        {
            // Аргументы вызова пропускаем целиком, чтобы не разбирать выражения внутри них.
            next = SkipArguments(next);
        }

        _index = next > _index ? next : _index + 1;
    }

    /// <summary>
    /// Разбирает цепочку «Идентификатор[.Идентификатор]*» начиная с позиции <paramref name="start"/>
    /// и, если за цепочкой следует «(», фиксирует вызов.
    /// </summary>
    /// <param name="start">Индекс первого идентификатора цепочки.</param>
    /// <param name="accesses">Найденные в цепочке обращения к метаданным.</param>
    /// <param name="records">Вызовы методов, найденные в цепочке.</param>
    /// <param name="lastElement">Индекс последнего элемента цепочки.</param>
    /// <returns>Индекс открывающей «(», если это вызов, иначе — индекс первого токена после цепочки.</returns>
    private int ParseChain(int start, List<BslMetadataAccess> accesses, List<BslCall> records, out int lastElement)
    {
        var segments = new List<int>();
        var i = start;
        var chainLine = _tokens[start].Line;
        var endedOnElement = start;
        while (true)
        {
            if (i >= _tokens.Count || _tokens[i].Kind is not (BslTokenKind.Identifier or BslTokenKind.Keyword))
            {
                break;
            }

            endedOnElement = i;
            segments.Add(i);
            i++;

            // Индексация («Значения[0]») элемент цепочки не завершает.
            while (i < _tokens.Count && _tokens[i].Kind == BslTokenKind.Operator && IsOperator(_tokens[i], "["))
            {
                i = SkipBalanced(i, _tokens.Count);
            }

            if (i >= _tokens.Count || _tokens[i].Kind != BslTokenKind.Operator || !IsOperator(_tokens[i], "."))
            {
                break;
            }

            var afterDot = SkipTrivia(i + 1);
            if (afterDot >= _tokens.Count ||
                _tokens[afterDot].Kind is not (BslTokenKind.Identifier or BslTokenKind.Keyword))
            {
                break;
            }

            // Между точкой и элементом допускаются переводы строк и комментарии.
            i = afterDot;
        }

        lastElement = endedOnElement;
        if (segments.Count == 0)
        {
            return start + 1;
        }

        var isCall = i < _tokens.Count &&
                     _tokens[i].Kind == BslTokenKind.Operator &&
                     IsOperator(_tokens[i], "(");

        // «Новый Структура(...)» — конструктор, а не вызов метода; «Новый» стоит непосредственно перед именем типа.
        var isConstructor = start > 0 && IsKeywordAt(start - 1, "Новый");

        foreach (var access in InferMetadataAccesses(segments, isCall, chainLine))
        {
            accesses.Add(access);
        }

        if (isCall && !isConstructor)
        {
            var callee = BuildText(segments[0], endedOnElement);
            var method = _tokens[endedOnElement].GetText();
            var dot = callee.LastIndexOf('.');
            var qualifier = dot > 0 ? callee[..dot] : null;
            records.Add(new BslCall(callee, qualifier, method, chainLine));
        }

        return i;
    }

    /// <summary>
    /// Определяет обращения к метаданным в цепочке:
    /// «Справочники.Товары», «Справочники.Товары.СоздатьЭлемент()», «Метаданные.Справочники.Товары».
    /// </summary>
    private IEnumerable<BslMetadataAccess> InferMetadataAccesses(List<int> segments, bool isCall, int line)
    {
        var first = _tokens[segments[0]].GetText();

        if (string.Equals(first, MetadataRootName, StringComparison.OrdinalIgnoreCase))
        {
            // «Метаданные.<Коллекция>.<Объект>…» — вид берём из коллекции, текст ограничиваем тремя элементами.
            if (segments.Count >= 3 &&
                MdNaming.TryKindFromCollection(_tokens[segments[1]].GetText(), out var rootKind))
            {
                yield return new BslMetadataAccess(
                    rootKind,
                    _tokens[segments[2]].GetText(),
                    _tokens[segments[1]].GetText(),
                    line,
                    BuildText(segments[0], segments[2]));
                yield break;
            }

            // «Метаданные.Что-То…» — вид объекта по имени коллекции определить нельзя.
            yield return new BslMetadataAccess(
                MdKind.MetadataRoot,
                _tokens[segments[^1]].GetText(),
                MetadataRootName,
                line,
                BuildText(segments[0], segments[^1]));
            yield break;
        }

        if (!MdNaming.TryKindFromCollection(first, out var firstKind) || firstKind == MdKind.MetadataRoot)
        {
            yield break;
        }

        if (segments.Count >= 2 && !isCall)
        {
            // «Справочники.Товары» без вызова, в том числе с обращением к свойствам объекта, —
            // это обращение к объекту метаданных.
            yield return new BslMetadataAccess(
                firstKind,
                _tokens[segments[1]].GetText(),
                first,
                line,
                BuildText(segments[0], segments[1]));
            yield break;
        }

        if (segments.Count >= 3)
        {
            // «Справочники.Товары.СоздатьЭлемент()» — обращение к «Справочники.Товары»:
            // суффикс после метода объекта (например «.Провести») в текст не входит.
            yield return new BslMetadataAccess(
                firstKind,
                _tokens[segments[1]].GetText(),
                first,
                line,
                BuildText(segments[0], segments[1]));
        }

        // «Справочники.Товары(…)» — вызов метода менеджера; одиночная коллекция без имени объекта
        // тоже не даёт имени объекта, поэтому обращения не фиксируем.
    }

    /// <summary>Пропускает аргументы вызова: от «(» до парной «)»; возвращает позицию за скобкой.</summary>
    private int SkipArguments(int index)
    {
        if (index >= _tokens.Count ||
            _tokens[index].Kind != BslTokenKind.Operator ||
            !IsOperator(_tokens[index], "("))
        {
            return index;
        }

        return SkipBalanced(index, _tokens.Count);
    }

    /// <summary>Пропускает сбалансированную группу скобок; возвращает позицию за закрывающей скобкой.</summary>
    private int SkipBalanced(int index, int end)
    {
        var depth = 0;
        var i = index;
        while (i < end)
        {
            var token = _tokens[i];
            if (token.Kind == BslTokenKind.Operator)
            {
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

            i++;
        }

        return end;
    }

    private static bool IsOpening(BslToken token) =>
        IsOperator(token, "(") || IsOperator(token, "[") || IsOperator(token, "{");

    private static bool IsClosing(BslToken token) =>
        IsOperator(token, ")") || IsOperator(token, "]") || IsOperator(token, "}");

    /// <summary>Фиксирует завершение текущей процедуры или функции.</summary>
    private void CloseRoutine(int endLine)
    {
        if (_routine is null)
        {
            return;
        }

        var finished = _routine;
        _routine = null;
        _finished.Add(new RouteResult(finished, Math.Max(finished.StartLine, endLine)));
    }

    /// <summary><c>КонецПроцедуры</c>/<c>КонецФункции</c>: закрывает процедуру либо даёт диагностику.</summary>
    private void HandleRoutineEnd(BslToken token)
    {
        if (_routine is null)
        {
            _diagnostics.Add(new BslDiagnostic(
                BslDiagnosticKind.UnexpectedRoutineEnd,
                $"«{token.GetText()}» без открывающей процедуры или функции.",
                token.Line));
        }
        else
        {
            CloseRoutine(token.Line);
        }

        // До конца строки может остаться комментарий — пропускаем его вместе с закрывающим словом.
        _index++;
        while (_index < _tokens.Count &&
               _tokens[_index].Kind is BslTokenKind.Comment or BslTokenKind.Operator)
        {
            _index++;
        }
    }

    /// <summary>Текст от первого токена до последнего включительно, склеенный без пробелов (цепочка идентификаторов).</summary>
    private string BuildText(int from, int to)
    {
        var length = 0;
        for (var i = from; i <= to; i++)
        {
            length += _tokens[i].Text.Length;
        }

        return string.Create(length, (Tokens: _tokens, From: from, To: to), static (destination, state) =>
        {
            var position = 0;
            for (var i = state.From; i <= state.To; i++)
            {
                var span = state.Tokens[i].Span;
                span.CopyTo(destination[position..]);
                position += span.Length;
            }
        });
    }

    /// <summary>Пропускает переводы строк и комментарии (для заголовков процедур).</summary>
    private int SkipTrivia(int index)
    {
        while (index < _tokens.Count && _tokens[index].Kind is BslTokenKind.NewLine or BslTokenKind.Comment)
        {
            index++;
        }

        return index;
    }

    /// <summary>Текст токена равен ключевому слову без учёта регистра.</summary>
    private bool IsKeywordAt(int index, string keyword)
    {
        if (index < 0 || index >= _tokens.Count)
        {
            return false;
        }

        var token = _tokens[index];
        return token.Kind is BslTokenKind.Identifier or BslTokenKind.Keyword &&
               token.Span.Equals(keyword, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsOperator(BslToken token, string text) =>
        token.Kind == BslTokenKind.Operator && token.Span.Equals(text, StringComparison.Ordinal);

    /// <summary>Открытая область (имя, строка начала, глубина вложенности).</summary>
    private sealed record RegionFrame(string Name, int StartLine, int Depth);

    /// <summary>Разбираемая процедура или функция: заголовок и накопленные ссылки.</summary>
    private sealed class RoutineFrame(
        string name,
        BslRoutineKind kind,
        bool isExport,
        List<string> parameters,
        int startLine,
        int next,
        List<string> directives)
    {
        public string Name { get; } = name;

        public BslRoutineKind Kind { get; } = kind;

        public bool IsExport { get; } = isExport;

        public List<string> Parameters { get; } = parameters;

        public int StartLine { get; } = startLine;

        /// <summary>Индекс токена, с которого начинается тело процедуры, сразу после заголовка.</summary>
        public int Next { get; } = next;

        public List<string> Directives { get; } = directives;

        public string? Region { get; init; }

        public int Depth { get; init; }

        public List<BslCall> Calls { get; } = [];

        public List<BslMetadataAccess> Metadata { get; } = [];

        /// <summary>Таблицы метаданных из текстов запросов процедуры.</summary>
        public List<BslQueryReference> Queries { get; } = [];

        public BslRoutine ToRoutine(int endLine) => new(
            Name,
            Kind,
            IsExport,
            Parameters,
            StartLine,
            endLine,
            Depth,
            Region,
            Directives,
            Calls,
            Metadata)
        {
            QueryReferences = Queries,
        };
    }

    /// <summary>Готовая процедура вместе со строкой её завершения.</summary>
    private sealed record RouteResult(RoutineFrame Frame, int EndLine);
}
