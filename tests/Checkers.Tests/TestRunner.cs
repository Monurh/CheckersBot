using System.Diagnostics;

namespace Checkers.Tests;

/// <summary>A minimal test runner (no NuGet packages needed): dotnet run --project tests/Checkers.Tests</summary>
public static class TestRunner
{
    static readonly List<(string Group, string Name, Func<Task> Body)> Tests = new();

    public static void Add(string group, string name, Action body) => Tests.Add((group, name, () => { body(); return Task.CompletedTask; }));
    public static void Add(string group, string name, Func<Task> body) => Tests.Add((group, name, body));

    public static async Task<int> RunAsync(IEnumerable<string> groups)
    {
        var wanted = new HashSet<string>(groups, StringComparer.OrdinalIgnoreCase);
        int passed = 0, failed = 0, skipped = 0;
        foreach (var (group, name, body) in Tests)
        {
            if (wanted.Count > 0 && !wanted.Contains(group)) continue;
            var sw = Stopwatch.StartNew();
            try
            {
                await body();
                passed++;
                Console.WriteLine($"  ok    [{group}] {name}  ({sw.ElapsedMilliseconds} ms)");
            }
            catch (SkipException ex)
            {
                skipped++;
                Console.WriteLine($"  skip  [{group}] {name}  ({ex.Message})");
            }
            catch (Exception ex)
            {
                failed++;
                Console.WriteLine($"  FAIL  [{group}] {name}  ({sw.ElapsedMilliseconds} ms)");
                Console.WriteLine("        " + ex.Message.Replace("\n", "\n        "));
            }
        }
        Console.WriteLine();
        string skipText = skipped > 0 ? $", {skipped} skipped" : "";
        Console.WriteLine(failed == 0 ? $"All {passed} tests passed{skipText}." : $"{failed} failed, {passed} passed{skipText}.");
        return failed == 0 ? 0 : 1;
    }
}

public static class Assert
{
    public static void True(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    public static void Equal<T>(T expected, T actual, string what)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception($"{what}: expected {expected}, got {actual}");
    }

    public static void SetEqual(IEnumerable<string> expected, IEnumerable<string> actual, string what)
    {
        var e = expected.OrderBy(x => x).ToList();
        var a = actual.OrderBy(x => x).ToList();
        if (!e.SequenceEqual(a))
            throw new Exception($"{what}: expected [{string.Join(", ", e)}], got [{string.Join(", ", a)}]");
    }

    public static T Throws<T>(Action action, string what) where T : Exception
    {
        try { action(); }
        catch (T ex) { return ex; }
        catch (Exception ex) { throw new Exception($"{what}: expected {typeof(T).Name}, got {ex.GetType().Name}: {ex.Message}"); }
        throw new Exception($"{what}: expected {typeof(T).Name}, nothing was thrown");
    }
}
