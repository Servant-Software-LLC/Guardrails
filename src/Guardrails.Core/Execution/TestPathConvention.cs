namespace Guardrails.Core.Execution;

/// <summary>
/// The one conservative "this is a test file" reading of a workspace path, shared by every rule that must not treat
/// a test like the rest of the code. Keyed on the conventions this repo and the plans it generates actually use — a
/// path containing "test" whose name ends <c>Tests.cs</c>, <c>Test.cs</c>, <c>_test.py</c>, <c>.test.ts</c>,
/// <c>.test.js</c> or <c>.spec.ts</c> — so a miss is silence rather than a false claim.
/// <list type="bullet">
///   <item>GR2075 (a task grades a test it authored itself) reads <c>writeScope</c> entries with it.</item>
///   <item>#707's first-occurrence write-scope rule refuses a test path with it: in a correctly split TDD plan the
///     only upstream-authored file an implementing task can still hit outside its scope is a test, and telling a
///     human to widen the scope over a protected test is the wrong advice.</item>
/// </list>
/// Hoisted unchanged out of <c>PlanValidator</c> so the two rules read ONE convention and cannot drift apart.
/// </summary>
internal static class TestPathConvention
{
    /// <summary>True when <paramref name="path"/> reads as a test file under the conventions above.</summary>
    internal static bool LooksLikeTestPath(string path)
    {
        string normalized = path.Trim().Replace('\\', '/');
        return normalized.Contains("test", StringComparison.OrdinalIgnoreCase)
               && (normalized.EndsWith("tests.cs", StringComparison.OrdinalIgnoreCase)
                   || normalized.EndsWith("test.cs", StringComparison.OrdinalIgnoreCase)
                   || normalized.EndsWith("_test.py", StringComparison.OrdinalIgnoreCase)
                   || normalized.EndsWith(".test.ts", StringComparison.OrdinalIgnoreCase)
                   || normalized.EndsWith(".test.js", StringComparison.OrdinalIgnoreCase)
                   || normalized.EndsWith(".spec.ts", StringComparison.OrdinalIgnoreCase));
    }
}
