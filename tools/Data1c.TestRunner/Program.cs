using System.Reflection;
using System.Text;
using Xunit;
using Xunit.Sdk;

namespace Data1c.TestRunner;

/// <summary>
/// Минимальный раннер тестов xunit: находит [Fact] и [Theory] по отражению и выполняет их.
/// Нужен потому, что стандартный <c>dotnet test</c> в этом окружении не может запустить testhost
/// (он не получает доступ к handle родительского процесса).
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            Console.OutputEncoding = new UTF8Encoding(false);
        }
        catch (IOException)
        {
            // Кодировку консоли сменить не удалось — не критично.
        }

        var filter = GetFilter(args);
        var assembly = Assembly.Load("Data1c.Tests");

        var passed = 0;
        var failed = 0;
        var skipped = 0;
        var failures = new List<string>();
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        foreach (var type in assembly.GetTypes().Where(static t => t is { IsClass: true, IsAbstract: false }).OrderBy(static t => t.FullName, StringComparer.Ordinal))
        {
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance).OrderBy(static m => m.Name, StringComparer.Ordinal))
            {
                var fact = method.GetCustomAttribute<FactAttribute>();
                if (fact is null)
                {
                    continue;
                }

                var name = $"{type.FullName}.{method.Name}";
                if (filter is not null && !name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (method.GetCustomAttribute<TheoryAttribute>() is not null)
                {
                    RunTheory(type, method, name, ref passed, ref failed, ref skipped, failures);
                }
                else
                {
                    RunFact(type, method, name, fact, ref passed, ref failed, ref skipped, failures);
                }
            }
        }

        stopwatch.Stop();
        Console.WriteLine();
        foreach (var failure in failures)
        {
            Console.WriteLine(failure);
            Console.WriteLine();
        }

        Console.WriteLine($"Пройдено: {passed}, не пройдено: {failed}, пропущено: {skipped} ({stopwatch.Elapsed:mm\\:ss\\.ff})");
        return failed == 0 ? 0 : 1;
    }

    private static string? GetFilter(string[] args)
    {
        foreach (var arg in args)
        {
            if (arg.StartsWith("--filter=", StringComparison.Ordinal))
            {
                return arg["--filter=".Length..];
            }
        }

        return null;
    }

    private static void RunFact(
        Type type,
        MethodInfo method,
        string name,
        FactAttribute fact,
        ref int passed,
        ref int failed,
        ref int skipped,
        List<string> failures)
    {
        if (fact.Skip is not null)
        {
            skipped++;
            return;
        }

        var error = Execute(type, method, []);
        if (error is null)
        {
            passed++;
            Console.WriteLine($"  ok   {name}");
        }
        else
        {
            failed++;
            failures.Add($"FAIL {name}{Environment.NewLine}     {error}");
            Console.WriteLine($"  FAIL {name}");
        }
    }

    private static void RunTheory(
        Type type,
        MethodInfo method,
        string name,
        ref int passed,
        ref int failed,
        ref int skipped,
        List<string> failures)
    {
        var dataAttributes = method.GetCustomAttributes<DataAttribute>().ToList();
        List<object?[]> cases;
        try
        {
            cases = [.. dataAttributes.SelectMany(a => a.GetData(method))];
        }
        catch (Exception ex)
        {
            failed++;
            failures.Add($"FAIL {name} (данные теории не получены){Environment.NewLine}     {ex.Message}");
            return;
        }

        if (cases.Count == 0)
        {
            skipped++;
            failures.Add($"SKIP {name}: у теории нет наборов данных");
            return;
        }

        for (var index = 0; index < cases.Count; index++)
        {
            var caseName = $"{name}({string.Join(", ", cases[index].Select(Format))})";
            var error = Execute(type, method, cases[index]);
            if (error is null)
            {
                passed++;
                Console.WriteLine($"  ok   {caseName}");
            }
            else
            {
                failed++;
                failures.Add($"FAIL {caseName}{Environment.NewLine}     {error}");
                Console.WriteLine($"  FAIL {caseName}");
            }
        }
    }

    private static string Format(object? value) => value switch
    {
        null => "null",
        string text => $"\"{text}\"",
        _ => value.ToString() ?? "null",
    };

    private static string? Execute(Type type, MethodInfo method, object?[] arguments)
    {
        object? instance = null;
        try
        {
            instance = Activator.CreateInstance(type);
            var result = method.Invoke(instance, arguments);
            if (result is Task task)
            {
                task.GetAwaiter().GetResult();
            }
            else if (result is ValueTask valueTask)
            {
                valueTask.GetAwaiter().GetResult();
            }

            return null;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            return Describe(ex.InnerException);
        }
        catch (Exception ex)
        {
            return Describe(ex);
        }
        finally
        {
            (instance as IDisposable)?.Dispose();
        }
    }

    private static string Describe(Exception exception)
    {
        var message = exception is XunitException
            ? exception.Message
            : $"{exception.GetType().Name}: {exception.Message}";

        var frames = exception.StackTrace?
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(static line => line.Contains("Data1c", StringComparison.Ordinal))
            .Take(4)
            .Select(static line => "     " + line.Trim());

        return frames is null
            ? message
            : message + Environment.NewLine + string.Join(Environment.NewLine, frames);
    }
}
