using Guardrails.Cli;
using Guardrails.Core.Breakdown;

namespace Guardrails.Integration.Tests;

/// <summary>
/// Drives <c>guardrails lock</c> through the REAL composition root
/// (<see cref="CommandFactory.BuildRootCommand"/>) to prove the GitGuardian baseline-exclusion
/// SUGGESTION (issue #67) is actually wired into the <c>lock</c>→suggest path — the integration
/// gap the review flagged (the wiring was never exercised end-to-end). The suggestion is read-only
/// and advisory: it DETECTS whether the enclosing repo's <c>.gitguardian.yaml</c> excludes baseline
/// files and PRINTS a copy-pasteable line; it never edits or creates the user's scanner config and
/// never affects the exit code. Output is captured with <see cref="StringConsoleIo"/> (no
/// process-global console) so it stays parallel-safe.
/// </summary>
public sealed class GitGuardianSuggestionCliTests
{
    private static async Task<(int ExitCode, string Output)> InvokeAsync(StringConsoleIo io, params string[] args)
    {
        var root = CommandFactory.BuildRootCommand(io);
        int exit = await root.Parse(args).InvokeAsync();
        return (exit, io.OutText);
    }

    [Fact]
    public async Task Lock_InGitRepo_WithoutExclusion_PrintsGitGuardianSuggestion()
    {
        // A real, runnable plan with a .git at the plan root → the plan root IS the git repo root.
        using var plan = new ScriptPlanBuilder().AddTask("01-first");
        Directory.CreateDirectory(Path.Combine(plan.PlanDir, ".git"));
        // No .gitguardian.yaml exists → lock should suggest creating one excluding the baseline.

        var io = new StringConsoleIo();
        (int exit, string output) = await InvokeAsync(io, "lock", plan.PlanDir);

        Assert.Equal(ExitCodes.Success, exit);
        Assert.True(File.Exists(Path.Combine(plan.PlanDir, BreakdownManifest.FileName)),
            "lock must still write the baseline");
        // The wiring is proven: lock → suggest printed the copy-pasteable exclusion glob.
        Assert.Contains(GitGuardianConfig.BaselineGlob, output, StringComparison.Ordinal);
        // Advisory only: it must NOT have created the scanner config.
        Assert.False(File.Exists(Path.Combine(plan.PlanDir, ".gitguardian.yaml")),
            "suggestion is read-only — lock must not create .gitguardian.yaml");
    }

    [Fact]
    public async Task Lock_InGitRepo_AlreadyExcluded_IsQuietAboutGitGuardian()
    {
        using var plan = new ScriptPlanBuilder().AddTask("01-first");
        Directory.CreateDirectory(Path.Combine(plan.PlanDir, ".git"));
        await File.WriteAllTextAsync(Path.Combine(plan.PlanDir, ".gitguardian.yaml"),
            """
            version: 2
            secret:
              ignored-paths:
                - "**/guardrails.baseline"
            """,
            TestContext.Current.CancellationToken);

        var io = new StringConsoleIo();
        (int exit, string output) = await InvokeAsync(io, "lock", plan.PlanDir);

        Assert.Equal(ExitCodes.Success, exit);
        // Already excluded → no suggestion line (the only baseline-glob mention would be a suggestion).
        Assert.DoesNotContain("Suggestion", output, StringComparison.Ordinal);
    }
}
