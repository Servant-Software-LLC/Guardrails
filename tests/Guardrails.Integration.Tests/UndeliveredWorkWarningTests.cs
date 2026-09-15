using Guardrails.Cli.Commands;
using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using Guardrails.Core.Model;

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
            WhollyGreenButUndelivered = whollyGreenButUndelivered,

            // An undelivered report with no suppressing decision is the #340 opt-out. Since #710 the banner
            // renders the resolved setting rather than assuming it, so the fixture states the opt-out it stands for.
            MergeOnSuccess = !whollyGreenButUndelivered,
            MergeOnSuccessSource = whollyGreenButUndelivered ? MergeOnSuccessSource.Flag : MergeOnSuccessSource.Default
        };

    private static string Render(
        RunReport report, bool terminalGatePassed, string planDirectory, int? planFolderDrift = null)
    {
        using var writer = new StringWriter();
        RunCommand.RenderUndeliveredWorkWarning(
            report, terminalGatePassed, planDirectory, writer, planFolderDrift);
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
    // Issue #597 — the banner's two causes. WhollyGreenButUndelivered covers both "mergeOnSuccess is
    // genuinely off" and "the #361 autonomous-mode interlock held the work back", and the banner used to
    // render only the first. On a suppression-by-decision run BOTH halves of that text were false:
    // mergeOnSuccess was ON (the #340 default), and the recommended --merge-on-success could not lift the
    // interlock. The measured operator burned three dead ends (guardrails.json → the default in source →
    // the release history) before finding the real cause in RunOutcomePolicy. Issue #710 then found the two
    // causes can hold at once, which makes three cases; the #710 section below pins all three.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    private static DecisionEntry BestGuessAt(string subject) => new()
    {
        Boundary = "task",
        Policy = "auto",
        Decision = DecisionTokens.ProceededBestGuess,
        Subject = subject,
        Headline = "best-guessed at the needs-human gate"
    };

    private static RunReport SuppressedReport(
        DecisionEntry? suppressing, bool mergeOnSuccess = true, MergeOnSuccessSource source = MergeOnSuccessSource.Default) =>
        new()
        {
            Tasks = [new TaskResult { TaskId = "01-do-thing", Outcome = TaskOutcome.Succeeded, Summary = "ok" }],
            WhollyGreenButUndelivered = true,
            MergeOnSuccess = mergeOnSuccess,
            MergeOnSuccessSource = source,
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
        // The load-bearing negative: with NO suppressing decision the cause really IS mergeOnSuccess, and the
        // banner says so without inventing an interlock. #710 added the setting's source to this wording.
        string rendered = Render(
            SuppressedReport(suppressing: null, mergeOnSuccess: false, source: MergeOnSuccessSource.Config),
            terminalGatePassed: true, planDirectory: Path.Combine("repo", "27-operator-visibility"));

        Assert.Contains(Marker, rendered);
        Assert.Contains("mergeOnSuccess is off", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("interlock", rendered, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("--merge-on-success", rendered, StringComparison.Ordinal);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Issue #710 — the cause comes from BOTH facts. #597 chose between its two causes on the decision alone, and
    // the decision is recorded whether or not the interlock held. So a run resumed with --no-merge-on-success
    // that had also recorded a proceeded-best-guess printed "mergeOnSuccess is ON" and named only the interlock
    // (plan 40, run 2026-09-11T21-32-30Z-5d7e, where both causes were true). The first three rows pin the
    // rendered text whole; the theory pins that the banner and delivery.reason name the same cause.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    private const string Rule = "==============================================================================";

    private static string Banner(params string[] causeLines) =>
        string.Concat(
            new[] { "", Rule, Marker }
                .Concat(causeLines)
                .Concat(["A later --fresh or 'reset -y' will DESTROY this undelivered work.", Rule])
                .Select(line => line + Environment.NewLine));

    [Fact]
    public void OffWithADecision_NamesBothCauses_AndNeverClaimsTheSettingIsOn()
    {
        string rendered = Render(
            SuppressedReport(
                BestGuessAt("20-implement-overwatcher-autoresolve"), mergeOnSuccess: false, source: MergeOnSuccessSource.Flag),
            terminalGatePassed: true, planDirectory: Path.Combine("repo", "40-in-flight-resource-supply"));

        Assert.Equal(
            Banner(
                "Delivery was held back for TWO reasons; either one alone would have held it:",
                "  1. mergeOnSuccess is off (set by --no-merge-on-success).",
                "  2. The autonomous-mode interlock (#361) — this run recorded",
                "     'proceeded-best-guess' at '20-implement-overwatcher-autoresolve' (task boundary),",
                "     so machine-decided work is never auto-delivered.",
                "The verified work is sitting on branch",
                "'guardrails/40-in-flight-resource-supply', NOT on your checkout.",
                "JUDGE THE DECISION FIRST — run.json → decisions[]. A best-guess that a later attempt",
                "superseded is stale; one that shaped the result you are looking at is not. Then either:",
                "  guardrails run 40-in-flight-resource-supply --merge-on-success   (turns delivery on AND overrides the interlock)",
                "  or merge 'guardrails/40-in-flight-resource-supply' into your branch yourself."),
            rendered);

        // The false statement the issue was filed for, asserted absent on its own, so a later re-wording of the pin
        // above cannot quietly bring it back.
        Assert.DoesNotContain("mergeOnSuccess is ON", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void OnWithADecision_NamesTheInterlock_AndThatTheSettingIsOnByDefault()
    {
        string rendered = Render(
            SuppressedReport(BestGuessAt("12-implement-events-endpoint")),
            terminalGatePassed: true, planDirectory: Path.Combine("repo", "35-event-vocabulary"));

        Assert.Equal(
            Banner(
                "mergeOnSuccess is ON (the default).",
                "Delivery was held back by the autonomous-mode interlock (#361):",
                "this run recorded 'proceeded-best-guess' at '12-implement-events-endpoint' (task boundary),",
                "so machine-decided work is never auto-delivered. The verified work is sitting on branch",
                "'guardrails/35-event-vocabulary', NOT on your checkout.",
                "JUDGE THE DECISION FIRST — run.json → decisions[]. A best-guess that a later attempt",
                "superseded is stale; one that shaped the result you are looking at is not. Then either:",
                "  guardrails run 35-event-vocabulary --merge-on-success   (an explicit override of the interlock)",
                "  or merge 'guardrails/35-event-vocabulary' into your branch yourself."),
            rendered);
    }

    [Fact]
    public void OffWithoutADecision_NamesTheFlagThatTurnedItOff()
    {
        string rendered = Render(
            SuppressedReport(suppressing: null, mergeOnSuccess: false, source: MergeOnSuccessSource.Flag),
            terminalGatePassed: true, planDirectory: Path.Combine("repo", "27-operator-visibility"));

        Assert.Equal(
            Banner(
                "mergeOnSuccess is off (set by --no-merge-on-success).",
                "This fully-green run's verified work is sitting on branch",
                "'guardrails/27-operator-visibility', NOT on your checkout.",
                "Deliver it before it is lost:  guardrails run 27-operator-visibility --merge-on-success",
                "                               (or merge 'guardrails/27-operator-visibility' into your branch yourself)."),
            rendered);
    }

    private static WaveDeliveredRecord WaveRecord(
        WaveDeliveryStatus status, DeliveryOutcome? outcome = null, string? detail = null) => new()
        {
            Status = status,
            StartedAt = DateTimeOffset.UnixEpoch,
            At = DateTimeOffset.UnixEpoch,
            Commit = status == WaveDeliveryStatus.Delivered ? "deadbeef" : null,
            Outcome = outcome,
            Detail = detail,
            Covers = ["wave-02-build"],
        };

    /// <summary>
    /// Both surfaces, every cause, every source a cause can carry, and the waved shapes that reach the same
    /// derivation (design 39 §4): a partial delivery, one whose later wave a rejecting hook held, and one whose later
    /// wave's delivery was refused. The last two are the resume case: an earlier run left those records in
    /// <c>run.json</c>, and this run passed <c>--no-merge-on-success</c>. The surfaces word things differently (the
    /// banner has room for remedies, the record is one sentence), so this does not compare them to each other. It
    /// requires each to state the SAME setting clause and the same decision, and neither to state the opposite of
    /// the resolved value.
    /// </summary>
    [Theory]
    [InlineData(false, MergeOnSuccessSource.Flag, false, "none", "mergeOnSuccess is off (set by --no-merge-on-success)")]
    [InlineData(false, MergeOnSuccessSource.Config, false, "none", "mergeOnSuccess is off (set by \"mergeOnSuccess\": false in guardrails.json)")]
    [InlineData(true, MergeOnSuccessSource.Default, true, "none", "mergeOnSuccess is ON (the default)")]
    [InlineData(true, MergeOnSuccessSource.Config, true, "none", "mergeOnSuccess is ON (set by \"mergeOnSuccess\": true in guardrails.json)")]
    [InlineData(false, MergeOnSuccessSource.Flag, true, "none", "mergeOnSuccess is off (set by --no-merge-on-success)")]
    [InlineData(false, MergeOnSuccessSource.Config, true, "none", "mergeOnSuccess is off (set by \"mergeOnSuccess\": false in guardrails.json)")]
    [InlineData(true, MergeOnSuccessSource.Default, true, "partial", "mergeOnSuccess is ON (the default)")]
    [InlineData(false, MergeOnSuccessSource.Flag, true, "partial", "mergeOnSuccess is off (set by --no-merge-on-success)")]
    [InlineData(false, MergeOnSuccessSource.Flag, false, "partial-hook-hold", "mergeOnSuccess is off (set by --no-merge-on-success)")]
    [InlineData(false, MergeOnSuccessSource.Flag, true, "partial-refused", "mergeOnSuccess is off (set by --no-merge-on-success)")]
    [InlineData(false, MergeOnSuccessSource.FlagAndConfig, false, "none", "mergeOnSuccess is off (set by --no-merge-on-success and \"mergeOnSuccess\": false in guardrails.json)")]
    [InlineData(false, MergeOnSuccessSource.FlagAndConfig, true, "none", "mergeOnSuccess is off (set by --no-merge-on-success and \"mergeOnSuccess\": false in guardrails.json)")]
    public void TheBannerAndDeliveryReason_NameTheSameCause(
        bool mergeOnSuccess, MergeOnSuccessSource source, bool withDecision, string waves, string settingClause)
    {
        const string planDirectory = "/repo/docs/plans/40-in-flight-resource-supply";
        const string decisionPhrase = "'proceeded-best-guess' at '12-implement-events-endpoint' (task boundary)";

        var waveDeliveries = new Dictionary<string, WaveDeliveredRecord>();
        if (waves != "none")
        {
            waveDeliveries["wave-02-build"] = WaveRecord(WaveDeliveryStatus.Delivered);
        }

        if (waves == "partial-hook-hold")
        {
            waveDeliveries["wave-03-build"] =
                WaveRecord(WaveDeliveryStatus.Refused, DeliveryOutcome.HookRejected, "pre-commit hook exited 1");
            waveDeliveries["wave-04-ride"] =
                WaveRecord(WaveDeliveryStatus.Suppressed, detail: "held by wave-03-build: hook-rejected");
        }
        else if (waves == "partial-refused")
        {
            waveDeliveries["wave-03-build"] = WaveRecord(
                WaveDeliveryStatus.Refused, DeliveryOutcome.BranchMoved, "run started on 'master'; HEAD is now 'spike'");
        }

        RunReport report = SuppressedReport(
            withDecision ? BestGuessAt("12-implement-events-endpoint") : null, mergeOnSuccess, source) with
        {
            WaveDeliveries = waveDeliveries
        };

        string banner = Render(report, terminalGatePassed: true, planDirectory);
        string reason = RunCommand.DescribeDelivery(report, terminalGatePassed: true, planDirectory).Reason!;

        foreach (string surface in new[] { banner, reason })
        {
            Assert.Contains(settingClause, surface, StringComparison.Ordinal);
            Assert.DoesNotContain(
                mergeOnSuccess ? "mergeOnSuccess is off" : "mergeOnSuccess is ON", surface, StringComparison.Ordinal);

            if (withDecision)
            {
                Assert.Contains(decisionPhrase, surface, StringComparison.Ordinal);
                Assert.Contains("interlock", surface, StringComparison.Ordinal);
            }
            else
            {
                Assert.DoesNotContain("interlock", surface, StringComparison.OrdinalIgnoreCase);
            }

            // Two causes are named exactly when two were true.
            Assert.Equal(
                !mergeOnSuccess && withDecision, surface.Contains("two reasons", StringComparison.OrdinalIgnoreCase));

            if (waves != "none")
            {
                Assert.Contains("wave-02-build", surface, StringComparison.Ordinal);
            }
        }

        if (waves is "partial-hook-hold" or "partial-refused")
        {
            Assert.Contains("held: wave-03-build", reason, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Nothing catches an exception this late in a run. The banner call has no catch, and <c>DescribeDelivery</c>'s
    /// catch covers only IO, access and JSON failures, so a throw on an unexpected setting source would turn a
    /// finished run into a harness error. An unrecognized source renders as unknown on both surfaces instead: a true
    /// statement, with no guess at which input it was.
    /// </summary>
    [Fact]
    public void AnUnrecognizedSettingSource_RendersAsUnknown_OnBothSurfaces_AndNeverThrows()
    {
        const string planDirectory = "/repo/docs/plans/40-in-flight-resource-supply";
        RunReport report = SuppressedReport(
            BestGuessAt("12-implement-events-endpoint"), mergeOnSuccess: false, source: (MergeOnSuccessSource)99);

        string banner = Render(report, terminalGatePassed: true, planDirectory);
        string reason = RunCommand.DescribeDelivery(report, terminalGatePassed: true, planDirectory).Reason!;

        foreach (string surface in new[] { banner, reason })
        {
            Assert.Contains("mergeOnSuccess is off (source unknown)", surface, StringComparison.Ordinal);
            Assert.DoesNotContain("set by", surface, StringComparison.Ordinal);
        }
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

    /// <summary>
    /// Issue #576: the banner's own instruction — "merge '&lt;planBranch&gt;' into your branch yourself" —
    /// is what produces the stale state, because the plan branch is cut ONCE and never rebased. A
    /// plan-folder fix made between resumes took effect (the harness reads the folder from the main
    /// checkout) and never landed on that branch, so following the banner literally delivers the CODE
    /// beside a plan folder that could not have produced it.
    ///
    /// <para>Measured on plan 32: three resumes, three real plan-folder fixes, and after the merge master
    /// carried a 16-task plan — task 17 absent — whose task-01 guardrail still held the filter that had
    /// HALTED the run.</para>
    /// </summary>
    [Fact]
    public void PlanFolderCommitsMissingFromThePlanBranch_AreNamedInTheBanner()
    {
        string rendered = Render(
            Report(whollyGreenButUndelivered: true), terminalGatePassed: true,
            planDirectory: Path.Combine("repo", "plans", "32-executed-definition-hash"),
            planFolderDrift: 3);

        Assert.Contains("3 commit(s) touching the plan folder", rendered, StringComparison.Ordinal);
        Assert.Contains("'guardrails/32-executed-definition-hash'", rendered, StringComparison.Ordinal);
        // The REMEDY, not just the fact — a note that only states a discrepancy sends the operator to
        // work out what to do about it, which is the same cost the banner exists to remove.
        Assert.Contains("Merge your own branch as well", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void NoPlanFolderDrift_AddsNothing()
    {
        // Zero is a real answer and it must be quiet: the loud banner is about undelivered WORK, and
        // appending "your folder is in sync" to it is noise on the path that is already correct.
        string rendered = Render(
            Report(whollyGreenButUndelivered: true), terminalGatePassed: true,
            planDirectory: Path.Combine("repo", "plan"), planFolderDrift: 0);

        Assert.Contains(Marker, rendered);
        Assert.DoesNotContain("plan folder", rendered, StringComparison.Ordinal);
    }

    /// <summary>
    /// NOT-KNOWN is not zero, and this is the assertion that keeps it that way.
    ///
    /// <para>The probe answers null whenever it could not ask — no git, no plan branch (a run that never
    /// used worktree mode has none), a failed invocation. Rendering that as silence is correct; rendering
    /// it as "0 commits" or "in sync" would reassure an operator, at the exact moment they are about to
    /// merge, about a question git refused to answer. That is the shape of defect this repository keeps
    /// finding, so it gets a test rather than a comment.</para>
    /// </summary>
    [Fact]
    public void NotKnownDrift_IsSilent_AndNeverRendersAsInSync()
    {
        string rendered = Render(
            Report(whollyGreenButUndelivered: true), terminalGatePassed: true,
            planDirectory: Path.Combine("repo", "plan"), planFolderDrift: null);

        Assert.Contains(Marker, rendered);
        Assert.DoesNotContain("plan folder", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("in sync", rendered, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("0 commit", rendered, StringComparison.Ordinal);
    }

}
