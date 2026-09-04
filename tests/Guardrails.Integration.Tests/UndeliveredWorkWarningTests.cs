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
    // Issue #597 — the banner's TWO causes. WhollyGreenButUndelivered covers both "mergeOnSuccess is
    // genuinely off" and "the #361 autonomous-mode interlock held the work back", and the banner used to
    // render only the first. On a suppression-by-decision run BOTH halves of that text were false:
    // mergeOnSuccess was ON (the #340 default), and the recommended --merge-on-success could not lift the
    // interlock. The measured operator burned three dead ends (guardrails.json → the default in source →
    // the release history) before finding the real cause in RunOutcomePolicy.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    private static DecisionEntry BestGuessAt(string subject) => new()
    {
        Boundary = "task",
        Policy = "auto",
        Decision = DecisionTokens.ProceededBestGuess,
        Subject = subject,
        Headline = "best-guessed at the needs-human gate"
    };

    private static RunReport SuppressedReport(DecisionEntry? suppressing) =>
        new()
        {
            Tasks = [new TaskResult { TaskId = "01-do-thing", Outcome = TaskOutcome.Succeeded, Summary = "ok" }],
            WhollyGreenButUndelivered = true,
            DeliverySuppressingDecision = suppressing
        };

    [Fact]
    public void SuppressedByMachineDecision_NamesTheDecisionAndItsTask_NotMergeOnSuccess()
    {
        string rendered = Render(
            SuppressedReport(BestGuessAt("12-implement-events-endpoint")),
            terminalGatePassed: true, planDirectory: Path.Combine("repo", "35-event-vocabulary"));

        Assert.Contains(Marker, rendered);

        // The REAL cause, and the task it came from — so the operator can judge whether it is stale.
        Assert.Contains("proceeded-best-guess", rendered, StringComparison.Ordinal);
        Assert.Contains("12-implement-events-endpoint", rendered, StringComparison.Ordinal);
        Assert.Contains("interlock", rendered, StringComparison.OrdinalIgnoreCase);

        // The false cause must be GONE. Naming mergeOnSuccess as off, when it is on, is the whole defect.
        Assert.DoesNotContain("mergeOnSuccess is off", rendered, StringComparison.Ordinal);

        // The remedy is still given (the flag now genuinely works), plus the manual merge and the risk.
        Assert.Contains("--merge-on-success", rendered, StringComparison.Ordinal);
        Assert.Contains("'guardrails/35-event-vocabulary'", rendered, StringComparison.Ordinal);
        Assert.Contains("--fresh", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void GenuinelyOff_KeepsTheOriginalWording()
    {
        // The load-bearing negative: with NO suppressing decision the cause really IS mergeOnSuccess, and
        // the shipped text stays exactly as it was — this change adds a case, it does not replace one.
        string rendered = Render(
            SuppressedReport(suppressing: null),
            terminalGatePassed: true, planDirectory: Path.Combine("repo", "27-operator-visibility"));

        Assert.Contains(Marker, rendered);
        Assert.Contains("mergeOnSuccess is off", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("interlock", rendered, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("--merge-on-success", rendered, StringComparison.Ordinal);
    }

    // ── The override's own notice: delivery that WENT AHEAD past a machine decision ────────────

    private static string RenderForced(RunReport report)
    {
        using var writer = new StringWriter();
        RunCommand.RenderForcedDeliveryNotice(report, writer);
        return writer.ToString();
    }

    [Fact]
    public void ForcedDelivery_IsAnnounced_NamingTheDecisionItOverrode()
    {
        var report = new RunReport
        {
            Tasks = [new TaskResult { TaskId = "01-do-thing", Outcome = TaskOutcome.Succeeded, Summary = "ok" }],
            MergeOnSuccessOutcome = MergeOnSuccessResult.FastForwarded,
            DeliveredToBranch = "master",
            DeliverySuppressingDecision = BestGuessAt("12-implement-events-endpoint"),
            DeliveryForcedPastDecision = true
        };

        string rendered = RenderForced(report);

        Assert.Contains("DELIVERY FORCED PAST A MACHINE DECISION", rendered, StringComparison.Ordinal);
        Assert.Contains("proceeded-best-guess", rendered, StringComparison.Ordinal);
        Assert.Contains("12-implement-events-endpoint", rendered, StringComparison.Ordinal);
        Assert.Contains("--merge-on-success", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void OrdinaryDelivery_PrintsNoForcedNotice()
    {
        // Nearly every run: no interlock was in play, so announcing an override would be a lie.
        Assert.Equal(string.Empty, RenderForced(DeliveredReport("master")));

        // And a run that merely RECORDED a decision without the override having fired stays silent too.
        Assert.Equal(string.Empty, RenderForced(SuppressedReport(BestGuessAt("01-thing"))));
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
