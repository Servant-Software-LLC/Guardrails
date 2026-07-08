using Guardrails.Core.Execution;

namespace Guardrails.Core.Tests;

/// <summary>
/// Pins <see cref="WindowsBashPaths.ToForwardSlashForm"/> — the #263 conversion that protects a
/// bash-invoked script's <c>GUARDRAILS_*</c> path env vars from backslash-escape corruption when a
/// guardrail interpolates one into an escape-sensitive context (a <c>node -e</c> JS string literal, a
/// regex, <c>sed</c>/<c>awk</c>).
/// <para>
/// This type deliberately contains NO <c>OperatingSystem.IsWindows()</c> branch (the Windows +
/// bash-interpreter gating lives in <see cref="ScriptUnitRunner"/>) — so every test here is a pure
/// function test that proves the CONVERSION LOGIC itself on any host OS, independent of whether the
/// machine running the suite is actually Windows.
/// </para>
/// </summary>
public sealed class WindowsBashPathsTests
{
    [Fact]
    public void ConvertsBackslashesInGuardrailsPrefixedValues()
    {
        var env = new Dictionary<string, string>
        {
            ["GUARDRAILS_WORKSPACE"] = @"C:\Users\dev\guardrails-worktrees\seg-1",
            ["GUARDRAILS_PLAN_DIR"] = @"C:\repo\plan",
        };

        IReadOnlyDictionary<string, string> result = WindowsBashPaths.ToForwardSlashForm(env);

        Assert.Equal("C:/Users/dev/guardrails-worktrees/seg-1", result["GUARDRAILS_WORKSPACE"]);
        Assert.Equal("C:/repo/plan", result["GUARDRAILS_PLAN_DIR"]);
    }

    [Fact]
    public void LeavesNonGuardrailsKeysUntouched()
    {
        // A task/guardrail's own declared action.env value must never be second-guessed, even when it
        // is itself a Windows-shaped path — only the reserved GUARDRAILS_ prefix is in scope.
        var env = new Dictionary<string, string>
        {
            ["GUARDRAILS_WORKSPACE"] = @"C:\Users\dev\ws",
            ["MY_TOOL_PATH"] = @"C:\tools\mytool.exe",
        };

        IReadOnlyDictionary<string, string> result = WindowsBashPaths.ToForwardSlashForm(env);

        Assert.Equal("C:/Users/dev/ws", result["GUARDRAILS_WORKSPACE"]);
        Assert.Equal(@"C:\tools\mytool.exe", result["MY_TOOL_PATH"]);
    }

    [Fact]
    public void LeavesValuesWithoutBackslashesUntouched()
    {
        var env = new Dictionary<string, string>
        {
            ["GUARDRAILS_TASK_ID"] = "01-first",
            ["GUARDRAILS_ATTEMPT"] = "1",
        };

        IReadOnlyDictionary<string, string> result = WindowsBashPaths.ToForwardSlashForm(env);

        Assert.Equal("01-first", result["GUARDRAILS_TASK_ID"]);
        Assert.Equal("1", result["GUARDRAILS_ATTEMPT"]);
    }

    [Fact]
    public void ReturnsSameInstance_WhenNothingNeedsConversion()
    {
        // No allocation on the common no-op path (already forward-slash / non-Windows values, or a
        // Linux/macOS-shaped env with no backslashes at all).
        IReadOnlyDictionary<string, string> env = new Dictionary<string, string>
        {
            ["GUARDRAILS_WORKSPACE"] = "/home/dev/guardrails-worktrees/seg-1",
            ["GUARDRAILS_TASK_ID"] = "01-first",
        };

        IReadOnlyDictionary<string, string> result = WindowsBashPaths.ToForwardSlashForm(env);

        Assert.Same(env, result);
    }

    [Fact]
    public void ConvertsOnlyTheAffectedKeys_LeavingSiblingsAsIndependentValues()
    {
        var env = new Dictionary<string, string>
        {
            ["GUARDRAILS_WORKSPACE"] = @"C:\a\b",
            ["GUARDRAILS_LOG_DIR"] = @"C:\a\b\logs",
            ["GUARDRAILS_TASK_ID"] = "01-first",
        };

        IReadOnlyDictionary<string, string> result = WindowsBashPaths.ToForwardSlashForm(env);

        Assert.Equal("C:/a/b", result["GUARDRAILS_WORKSPACE"]);
        Assert.Equal("C:/a/b/logs", result["GUARDRAILS_LOG_DIR"]);
        Assert.Equal("01-first", result["GUARDRAILS_TASK_ID"]);
        // The original dictionary passed in must never be mutated in place.
        Assert.Equal(@"C:\a\b", env["GUARDRAILS_WORKSPACE"]);
    }
}
