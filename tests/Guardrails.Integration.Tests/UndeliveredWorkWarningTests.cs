using Guardrails.Cli.Commands;
using Guardrails.Core.Execution;

namespace Guardrails.Integration.Tests;

/// <summary>
/// Pins the issue #340 loud "work not delivered" warning at the PUBLIC render seam
/// (<see cref="RunCommand.RenderUndeliveredWorkWarning"/>) — driven with a <see cref="StringWriter"/>
/// and fabricated <see cref="RunReport"/>s, no live process. The warning must fire exactly once, on a
/// wholly-green + undelivered run whose terminal gate ALSO passed, and be ABSENT on a delivered run, a
/// non-green run, and a run whose terminal gate failed.
/// </summary>
public sealed class UndeliveredWorkWarningTests
{
    private const string Marker = "*** WORK NOT DELIVERED ***";

    private static RunReport Report(bool whollyGreenButUndelivered, MergeOnSuccessResult? mergeOutcome = null) =>
        new()
        {
            Tasks =
            [
                new TaskResult { TaskId = "01-do-thing", Outcome = TaskOutcome.Succeeded, Summary = "ok" }
            ],
            MergeOnSuccessOutcome = mergeOutcome,
            WhollyGreenButUndelivered = whollyGreenButUndelivered
        };

    private static string Render(RunReport report, bool terminalGatePassed, string planDirectory)
    {
        using var writer = new StringWriter();
        RunCommand.RenderUndeliveredWorkWarning(report, terminalGatePassed, planDirectory, writer);
        return writer.ToString();
    }

    [Fact]
    public void WhollyGreenUndelivered_TerminalGatePassed_PrintsLoudWarning_NamingTheBranch()
    {
        string rendered = Render(
            Report(whollyGreenButUndelivered: true), terminalGatePassed: true,
            planDirectory: Path.Combine("repo", "plans", "dfd-threagile-substrate-wave-2b"));

        Assert.Contains(Marker, rendered);
        // The exact branch the undelivered work is sitting on must be named, verbatim.
        Assert.Contains("'guardrails/dfd-threagile-substrate-wave-2b'", rendered);
        Assert.Contains("mergeOnSuccess is off", rendered);
        // The destruction risk (the whole point of the warning) must be spelled out.
        Assert.Contains("--fresh", rendered);
        // The exact command to deliver the work must be given.
        Assert.Contains("--merge-on-success", rendered);
    }

    [Fact]
    public void DeliveredRun_PrintsNothing()
    {
        // A delivered run: the Scheduler set WhollyGreenButUndelivered=false and an outcome is present.
        string rendered = Render(
            Report(whollyGreenButUndelivered: false, mergeOutcome: MergeOnSuccessResult.FastForwarded),
            terminalGatePassed: true, planDirectory: Path.Combine("repo", "plan"));

        Assert.Equal(string.Empty, rendered);
    }

    [Fact]
    public void NonGreenRun_PrintsNothing()
    {
        // Not wholly green ⇒ the Scheduler never set the flag ⇒ silence (the run has its own failure path).
        string rendered = Render(
            Report(whollyGreenButUndelivered: false), terminalGatePassed: true,
            planDirectory: Path.Combine("repo", "plan"));

        Assert.Equal(string.Empty, rendered);
    }

    [Fact]
    public void TerminalGateFailed_PrintsNothing()
    {
        // The DAG drained green + undelivered, but the terminal gate FAILED — that path halts exit 2 on
        // its own; do NOT also claim "fully-green, safe on the branch".
        string rendered = Render(
            Report(whollyGreenButUndelivered: true), terminalGatePassed: false,
            planDirectory: Path.Combine("repo", "plan"));

        Assert.Equal(string.Empty, rendered);
    }

    [Fact]
    public void TrailingSeparator_OnPlanDirectory_DoesNotDoubleTheBranchSlug()
    {
        // A plan dir with a trailing separator must still resolve to guardrails/<plan-name>, not a blank slug.
        string rendered = Render(
            Report(whollyGreenButUndelivered: true), terminalGatePassed: true,
            planDirectory: Path.Combine("repo", "my-plan") + Path.DirectorySeparatorChar);

        Assert.Contains("'guardrails/my-plan'", rendered);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // #340 delivered-by-default notice — the delivered-case complement of the undelivered warning.
    // Fires ONLY when delivery RAN (DeliveredToBranch non-null) AND it fired purely because of the new
    // default (no config key, no CLI flag). The two NEVER fire together.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    private static RunReport DeliveredReport(string? deliveredToBranch) =>
        new()
        {
            Tasks = [new TaskResult { TaskId = "01-do-thing", Outcome = TaskOutcome.Succeeded, Summary = "ok" }],
            MergeOnSuccessOutcome = deliveredToBranch is null ? null : MergeOnSuccessResult.FastForwarded,
            DeliveredToBranch = deliveredToBranch
        };

    private static string RenderNotice(RunReport report, bool deliveryFromDefaultOnly)
    {
        using var writer = new StringWriter();
        RunCommand.RenderDeliveredByDefaultNotice(report, deliveryFromDefaultOnly, writer);
        return writer.ToString();
    }

    [Fact]
    public void DeliveredByDefault_NamesBranchAndOptOut()
    {
        string rendered = RenderNotice(DeliveredReport("feature/dfd"), deliveryFromDefaultOnly: true);

        Assert.Contains("delivered to feature/dfd", rendered);
        Assert.Contains("mergeOnSuccess now defaults on", rendered);
        // Both opt-out surfaces are named.
        Assert.Contains("--no-merge-on-success", rendered);
        Assert.Contains("\"mergeOnSuccess\": false", rendered);
    }

    [Fact]
    public void DeliveredByExplicitOptIn_PrintsNothing()
    {
        // Delivery ran, but the user explicitly opted in (config true or --merge-on-success) ⇒ no notice.
        string rendered = RenderNotice(DeliveredReport("main"), deliveryFromDefaultOnly: false);

        Assert.Equal(string.Empty, rendered);
    }

    [Fact]
    public void NoDelivery_PrintsNoDeliveredNotice()
    {
        // Delivery did not run (opt-out / serial / non-green ⇒ DeliveredToBranch null) ⇒ no notice even
        // when nothing else was set.
        string rendered = RenderNotice(DeliveredReport(deliveredToBranch: null), deliveryFromDefaultOnly: true);

        Assert.Equal(string.Empty, rendered);
    }
}
