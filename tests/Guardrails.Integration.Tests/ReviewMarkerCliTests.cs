using System.CommandLine;
using Guardrails.Cli;
using Guardrails.Cli.Commands;
using Guardrails.Core.Loading;
using Guardrails.Core.Review;

namespace Guardrails.Integration.Tests;

/// <summary>
/// The review-marker warn-don't-block surfacing through the real CLI (SSOT §13, issue #79):
/// <c>validate</c> appends GR2025 (WARNING, exit 0) when the marker is missing/stale; a fresh marker
/// stays quiet; <c>run</c> prints the same nudge unless <c>--skip-review-check</c>. The marker is a
/// warning, so it NEVER changes an exit code.
/// </summary>
public sealed class ReviewMarkerCliTests
{
    private static async Task<(int ExitCode, string Output)> InvokeAsync(params string[] args)
    {
        var io = new StringConsoleIo();
        var root = new RootCommand("test root");
        root.Add(RunCommand.Create(io));
        root.Add(ValidateCommand.Create(io));
        root.Add(MarkReviewedCommand.Create(io));
        int exit = await root.Parse(args).InvokeAsync();
        return (exit, io.OutText);
    }

    private static void WriteFreshMarker(string planDir)
    {
        PlanLoadResult load = new PlanLoader().Load(planDir);
        Assert.False(load.HasErrors, string.Join("\n", load.Diagnostics));
        ReviewMarker.Write(load.Plan!, DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task Validate_Unreviewed_WarnsGR2025_ButExitsZero()
    {
        using var plan = new ScriptPlanBuilder().AddTask("01-first");

        (int exit, string output) = await InvokeAsync("validate", plan.PlanDir);

        // A missing review marker is a WARNING — never fails validate.
        Assert.Equal(ExitCodes.Success, exit);
        Assert.Contains(DiagnosticCodes.ReviewMarkerMissingOrStale, output); // "GR2025"
        Assert.Contains("WARNING", output);
        Assert.Contains("/guardrails-review", output);
        Assert.Contains("OK: plan is valid.", output);
    }

    [Fact]
    public async Task Validate_FreshlyReviewed_IsQuiet()
    {
        using var plan = new ScriptPlanBuilder().AddTask("01-first");
        WriteFreshMarker(plan.PlanDir);

        (int exit, string output) = await InvokeAsync("validate", plan.PlanDir);

        Assert.Equal(ExitCodes.Success, exit);
        Assert.DoesNotContain(DiagnosticCodes.ReviewMarkerMissingOrStale, output);
    }

    [Fact]
    public async Task Run_Unreviewed_PrintsNudge_StillRunsGreen()
    {
        using var plan = new ScriptPlanBuilder().AddTask("01-first");

        (int exit, string output) = await InvokeAsync("run", plan.PlanDir, "--no-ui");

        // Warn, never block: the run proceeds to green; the nudge is just printed.
        Assert.Equal(ExitCodes.Success, exit);
        Assert.Contains(DiagnosticCodes.ReviewMarkerMissingOrStale, output);
    }

    [Fact]
    public async Task Run_SkipReviewCheck_SuppressesTheNudge()
    {
        using var plan = new ScriptPlanBuilder().AddTask("01-first");

        (int exit, string output) = await InvokeAsync("run", plan.PlanDir, "--no-ui", "--skip-review-check");

        Assert.Equal(ExitCodes.Success, exit);
        Assert.DoesNotContain(DiagnosticCodes.ReviewMarkerMissingOrStale, output);
    }

    [Fact]
    public async Task DryRun_Unreviewed_PrintsNudge_Honors_SkipReviewCheck()
    {
        using var plan = new ScriptPlanBuilder().AddTask("01-first");

        (int withNudge, string nudged) = await InvokeAsync("run", plan.PlanDir, "--dry-run");
        (int suppressed, string quiet) = await InvokeAsync("run", plan.PlanDir, "--dry-run", "--skip-review-check");

        Assert.Equal(ExitCodes.Success, withNudge);
        Assert.Equal(ExitCodes.Success, suppressed);
        Assert.Contains(DiagnosticCodes.ReviewMarkerMissingOrStale, nudged);
        Assert.DoesNotContain(DiagnosticCodes.ReviewMarkerMissingOrStale, quiet);
    }

    // ── mark-reviewed (the writer, issue #131) ──────────────────────────────────────────────────

    [Fact]
    public async Task MarkReviewed_WritesMarker_ThenValidateIsQuiet()
    {
        using var plan = new ScriptPlanBuilder().AddTask("01-first");

        // Before: validate nudges (unreviewed).
        (int _, string before) = await InvokeAsync("validate", plan.PlanDir);
        Assert.Contains(DiagnosticCodes.ReviewMarkerMissingOrStale, before);

        // mark-reviewed writes the planHash-keyed marker.
        (int markExit, string markOut) = await InvokeAsync("mark-reviewed", plan.PlanDir);
        Assert.Equal(ExitCodes.Success, markExit);
        Assert.Contains("marked reviewed", markOut);
        Assert.True(File.Exists(ReviewMarker.PathFor(plan.PlanDir)));

        // After: validate is quiet — the writer cleared the GR2025 nudge (#131 closes the #79 loop).
        (int valExit, string after) = await InvokeAsync("validate", plan.PlanDir);
        Assert.Equal(ExitCodes.Success, valExit);
        Assert.DoesNotContain(DiagnosticCodes.ReviewMarkerMissingOrStale, after);
    }

    [Fact]
    public async Task MarkReviewed_ThenPlanChanges_WarnsStaleAgain()
    {
        using var plan = new ScriptPlanBuilder().AddTask("01-first");
        await InvokeAsync("mark-reviewed", plan.PlanDir);

        // Editing the plan (a new task changes the plan hash) re-stales the marker.
        plan.AddTask("02-second");

        (int exit, string output) = await InvokeAsync("validate", plan.PlanDir);
        Assert.Equal(ExitCodes.Success, exit);
        Assert.Contains(DiagnosticCodes.ReviewMarkerMissingOrStale, output); // stale → warns again
    }
}
