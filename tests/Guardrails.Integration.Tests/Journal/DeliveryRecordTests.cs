using System.Text.Json;
using Guardrails.Cli.Commands;
using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using Guardrails.Core.Model;

namespace Guardrails.Integration.Tests.Journal;

/// <summary>
/// Issue #542 — <c>state/run.json</c> recorded every task, attempt, cost, gate and decision of a run, but
/// NOT whether the run's work was ever delivered to the user's branch. That outcome existed only in the
/// end-of-run console banner, so once the terminal was closed nothing on disk answered "did this run
/// deliver?" — the only remaining signal was noticing later that a plan branch was unmerged. It cost
/// exactly that: a wholly-green run was read as shipped, and two issues were closed against a branch that
/// had never been merged.
/// <para>
/// These tests pin the durable record. The banner is NOT replaced — it is the right operator surface and it
/// works; this is its machine-readable counterpart, for post-mortem and for #496's unattended pipeline,
/// which has no console for a banner to print to.
/// </para>
/// </summary>
public sealed class DeliveryRecordTests
{
    private const string PlanDir = "/repo/docs/plans/27-operator-visibility";
    private const string PlanBranch = "guardrails/27-operator-visibility";

    private static RunReport Report(
        bool allSucceeded = true,
        MergeOnSuccessResult? outcome = null,
        string? detail = null,
        string? deliveredToBranch = null,
        bool whollyGreenButUndelivered = false) => new()
        {
            // AllSucceeded is derived from the task results, so the green/non-green cases are driven by a
            // real TaskResult rather than a flag — the same way UndeliveredWorkWarningTests does it.
            Tasks =
            [
                new TaskResult
                {
                    TaskId = "01-do-thing",
                    Outcome = allSucceeded ? TaskOutcome.Succeeded : TaskOutcome.GuardrailFailed,
                    Summary = allSucceeded ? "ok" : "a guardrail failed",
                }
            ],
            MergeOnSuccessOutcome = outcome,
            MergeOnSuccessDetail = detail,
            DeliveredToBranch = deliveredToBranch,
            WhollyGreenButUndelivered = whollyGreenButUndelivered,
        };

    /// <summary>
    /// THE case this issue was filed from: a wholly-green run launched with <c>--no-merge-on-success</c>.
    /// The record must say plainly that it did not deliver, and must NAME the branch holding the work —
    /// that name is the entire actionable content, and it is what a reader coming back days later needs.
    /// </summary>
    [Fact]
    public void AGreenRunThatDidNotDeliver_RecordsNotDelivered_AndNamesTheBranchHoldingTheWork()
    {
        // #710: the fixture states the opt-out it stands for, since the record now renders the resolved setting.
        DeliverySection d = RunCommand.DescribeDelivery(
            Report(whollyGreenButUndelivered: true) with
            {
                MergeOnSuccess = false,
                MergeOnSuccessSource = MergeOnSuccessSource.Flag
            },
            terminalGatePassed: true, PlanDir);

        Assert.False(d.Delivered);
        Assert.Equal(DeliveryOutcome.NotAttempted, d.Outcome);
        Assert.Equal(PlanBranch, d.PlanBranch);
        Assert.NotNull(d.Reason);
        Assert.Contains("mergeOnSuccess is off (set by --no-merge-on-success)", d.Reason, StringComparison.Ordinal);
        Assert.Contains(PlanBranch, d.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// Issue #597: the SAME "nothing was delivered" outcome, a DIFFERENT cause. When the #361
    /// autonomous-mode interlock held a wholly-green run's work back, <c>mergeOnSuccess</c> was ON — so
    /// recording "mergeOnSuccess resolved off" wrote a flatly untrue cause into the one file an unattended
    /// pipeline (#496) can read, and a wrong answer there is worse than none. The record must name the
    /// decision AND its subject, exactly as the console banner does.
    /// </summary>
    [Fact]
    public void AGreenRunSuppressedByAMachineDecision_RecordsThatCause_NotMergeOnSuccess()
    {
        RunReport report = Report(whollyGreenButUndelivered: true) with
        {
            DeliverySuppressingDecision = new DecisionEntry
            {
                Boundary = "task",
                Policy = "auto",
                Decision = DecisionTokens.ProceededBestGuess,
                Subject = "12-implement-events-endpoint",
                Headline = "best-guessed at the needs-human gate"
            }
        };

        DeliverySection d = RunCommand.DescribeDelivery(report, terminalGatePassed: true, PlanDir);

        Assert.False(d.Delivered);
        Assert.Equal(DeliveryOutcome.NotAttempted, d.Outcome);
        Assert.Equal(PlanBranch, d.PlanBranch); // still the case that STRANDS work
        Assert.NotNull(d.Reason);

        Assert.Contains("proceeded-best-guess", d.Reason!, StringComparison.Ordinal);
        Assert.Contains("12-implement-events-endpoint", d.Reason!, StringComparison.Ordinal);
        Assert.Contains("interlock", d.Reason!, StringComparison.OrdinalIgnoreCase);
        // #710 re-worded the setting clause the record shares with the banner; the false cause stays absent in it.
        Assert.DoesNotContain("mergeOnSuccess is off", d.Reason!, StringComparison.Ordinal);

        // The interlock HELD, so no override fired — the audit object must be absent.
        Assert.Null(d.ForcedPastDecision);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Issue #710 — the cause as the durable record states it. #597's split chose on the decision alone, so a run
    // resumed with --no-merge-on-success that had also recorded a proceeded-best-guess wrote "mergeOnSuccess itself
    // is ON" into this record: a false fact, in the one file an unattended pipeline (#496) reads. Pinned whole,
    // because a reader of this field reads the whole sentence. UndeliveredWorkWarningTests pins the banner, and its
    // theory pins that the two surfaces name the same cause.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    private static DecisionEntry BestGuessAt(string subject) => new()
    {
        Boundary = "task",
        Policy = "auto",
        Decision = DecisionTokens.ProceededBestGuess,
        Subject = subject,
        Headline = "best-guessed at the needs-human gate"
    };

    [Fact]
    public void AGreenRunTurnedOffThatAlsoRecordedADecision_RecordsBothCauses_AndNeverClaimsTheSettingIsOn()
    {
        RunReport report = Report(whollyGreenButUndelivered: true) with
        {
            MergeOnSuccess = false,
            MergeOnSuccessSource = MergeOnSuccessSource.Flag,
            DeliverySuppressingDecision = BestGuessAt("20-implement-overwatcher-autoresolve")
        };

        DeliverySection d = RunCommand.DescribeDelivery(report, terminalGatePassed: true, PlanDir);

        Assert.False(d.Delivered);
        Assert.Equal(DeliveryOutcome.NotAttempted, d.Outcome);
        Assert.Equal(PlanBranch, d.PlanBranch);
        Assert.Equal(
            "delivery was held back for two reasons, either of which alone would have held it: "
            + "mergeOnSuccess is off (set by --no-merge-on-success), and the autonomous-mode interlock (#361) — "
            + "this run recorded 'proceeded-best-guess' at '20-implement-overwatcher-autoresolve' (task boundary), "
            + "so machine-decided work is not auto-delivered. The verified work is sitting on "
            + "'guardrails/27-operator-visibility' and NOT on your checkout; a later --fresh or 'reset -y' destroys it. "
            + "Judge the decision (decisions[]) first, then re-run with --merge-on-success, which both turns delivery "
            + "on and overrides the interlock, or merge the branch by hand",
            d.Reason);
        Assert.DoesNotContain("is ON", d.Reason!, StringComparison.Ordinal);

        // No override fired, so the audit object stays absent.
        Assert.Null(d.ForcedPastDecision);
    }

    [Fact]
    public void AGreenRunHeldByTheInterlock_RecordsThatTheSettingIsOnByDefault()
    {
        RunReport report = Report(whollyGreenButUndelivered: true) with
        {
            MergeOnSuccess = true,
            MergeOnSuccessSource = MergeOnSuccessSource.Default,
            DeliverySuppressingDecision = BestGuessAt("12-implement-events-endpoint")
        };

        Assert.Equal(
            "delivery was suppressed by the autonomous-mode interlock (#361) — this run recorded "
            + "'proceeded-best-guess' at '12-implement-events-endpoint' (task boundary), so machine-decided work is "
            + "not auto-delivered; mergeOnSuccess is ON (the default). The verified work is sitting on "
            + "'guardrails/27-operator-visibility' and NOT on your checkout; a later --fresh or 'reset -y' destroys it. "
            + "Judge the decision (decisions[]), then re-run with --merge-on-success to override, or merge the branch "
            + "by hand",
            RunCommand.DescribeDelivery(report, terminalGatePassed: true, PlanDir).Reason);
    }

    [Fact]
    public void AGreenRunTurnedOffByGuardrailsJson_RecordsThatInput_NotTheFlag()
    {
        RunReport report = Report(whollyGreenButUndelivered: true) with
        {
            MergeOnSuccess = false,
            MergeOnSuccessSource = MergeOnSuccessSource.Config
        };

        Assert.Equal(
            "mergeOnSuccess is off (set by \"mergeOnSuccess\": false in guardrails.json), so this wholly-green run's "
            + "verified work is sitting on 'guardrails/27-operator-visibility' and NOT on your checkout; a later "
            + "--fresh or 'reset -y' destroys it",
            RunCommand.DescribeDelivery(report, terminalGatePassed: true, PlanDir).Reason);
    }

    /// <summary>
    /// The qualifier is the ONLY difference (design 39 §4). The same held run, with and without a wave that already
    /// delivered, produces the same sentence apart from "the rest of", so a future edit to the partial wording cannot
    /// quietly reword the flat one — and the flat run keeps saying "The verified work ... NOT on your checkout",
    /// which is true exactly when nothing landed.
    /// </summary>
    [Fact]
    public void ADeliveredWave_AddsOnlyTheRestOfQualifier_AndLeavesTheFlatRunsWordsAlone()
    {
        RunReport flat = Report(whollyGreenButUndelivered: true) with
        {
            MergeOnSuccess = true,
            MergeOnSuccessSource = MergeOnSuccessSource.Default,
            DeliverySuppressingDecision = BestGuessAt("12-implement-events-endpoint")
        };

        RunReport partial = flat with
        {
            WaveDeliveries = new Dictionary<string, WaveDeliveredRecord>
            {
                ["wave-02-build"] = new()
                {
                    Status = WaveDeliveryStatus.Delivered,
                    StartedAt = DateTimeOffset.UnixEpoch,
                    At = DateTimeOffset.UnixEpoch,
                    Commit = "deadbeef",
                    Covers = ["wave-02-build"],
                },
            },
        };

        string flatReason = RunCommand.DescribeDelivery(flat, terminalGatePassed: true, PlanDir).Reason!;
        string partialReason = RunCommand.DescribeDelivery(partial, terminalGatePassed: true, PlanDir).Reason!;

        Assert.Contains(
            "The verified work is sitting on 'guardrails/27-operator-visibility' and NOT on your checkout",
            flatReason, StringComparison.Ordinal);
        Assert.DoesNotContain("the rest of", flatReason, StringComparison.OrdinalIgnoreCase);

        const string prefix = "delivered: wave-02-build — ";
        Assert.StartsWith(prefix, partialReason, StringComparison.Ordinal);
        Assert.Equal(
            flatReason.Replace(
                "The verified work is sitting on",
                "The rest of the verified work is sitting on",
                StringComparison.Ordinal),
            partialReason[prefix.Length..]);
    }

    /// <summary>
    /// The waved shape of the same run (design 39 §4): an earlier run delivered wave-02 at its own barrier, and this
    /// resume passed <c>--no-merge-on-success</c>. The record reads <c>partially-delivered</c>, and the rest is held
    /// for both causes, named in the flat run's words with ONE qualifier: "the REST of the verified work". Wave-02
    /// landed on the checkout, so the flat run's unqualified "The verified work ... NOT on your checkout" would
    /// contradict this record's own "delivered: wave-02-build" prefix.
    /// </summary>
    [Fact]
    public void APartialDeliveryTurnedOffThatAlsoRecordedADecision_NamesBothCauses_AfterTheDeliveredWave()
    {
        RunReport report = Report(whollyGreenButUndelivered: true) with
        {
            MergeOnSuccess = false,
            MergeOnSuccessSource = MergeOnSuccessSource.Flag,
            DeliverySuppressingDecision = BestGuessAt("wave-03-build/01-compile"),
            WaveDeliveries = new Dictionary<string, WaveDeliveredRecord>
            {
                ["wave-02-build"] = new()
                {
                    Status = WaveDeliveryStatus.Delivered,
                    StartedAt = DateTimeOffset.UnixEpoch,
                    At = DateTimeOffset.UnixEpoch,
                    Commit = "deadbeef",
                    Covers = ["wave-02-build"],
                },
            },
        };

        DeliverySection d = RunCommand.DescribeDelivery(report, terminalGatePassed: true, PlanDir);

        Assert.Equal(DeliveryOutcome.PartiallyDelivered, d.Outcome);
        Assert.False(d.Delivered);
        Assert.Equal(
            "delivered: wave-02-build — delivery was held back for two reasons, either of which alone would have held "
            + "it: mergeOnSuccess is off (set by --no-merge-on-success), and the autonomous-mode interlock (#361) — "
            + "this run recorded 'proceeded-best-guess' at 'wave-03-build/01-compile' (task boundary), so "
            + "machine-decided work is not auto-delivered. The rest of the verified work is sitting on "
            + "'guardrails/27-operator-visibility' and NOT on your checkout; a later --fresh or 'reset -y' destroys it. "
            + "Judge the decision (decisions[]) first, then re-run with --merge-on-success, which both turns delivery "
            + "on and overrides the interlock, or merge the branch by hand",
            d.Reason);
    }

    /// <summary>
    /// Issue #597 — the same interlock, OVERRIDDEN. <c>--merge-on-success</c> is the documented operator
    /// override of #361's delivery suppression, and it is the one action in the system that deliberately
    /// bypasses a safety interlock. Before this, it left NO durable trace: the fact lived on
    /// <see cref="RunReport"/> and in the console banner and nowhere on disk, so a delivery forced past a
    /// machine decision was indistinguishable, after the terminal closed, from one that was never
    /// suppressed. The record must carry the same pair the banner names — the decision token and the task.
    /// </summary>
    [Fact]
    public void AForcedDelivery_RecordsThatTheInterlockWasOverridden_AndWhichDecision()
    {
        RunReport report = Report(outcome: MergeOnSuccessResult.FastForwarded, deliveredToBranch: "master") with
        {
            DeliverySuppressingDecision = new DecisionEntry
            {
                Boundary = "task",
                Policy = "auto",
                Decision = DecisionTokens.ProceededBestGuess,
                Subject = "12-implement-events-endpoint",
                Headline = "best-guessed at the needs-human gate"
            },
            DeliveryForcedPastDecision = true
        };

        DeliverySection d = RunCommand.DescribeDelivery(report, terminalGatePassed: true, PlanDir);

        Assert.True(d.Delivered);
        Assert.Equal(DeliveryOutcome.FastForwarded, d.Outcome);

        Assert.NotNull(d.ForcedPastDecision);
        Assert.Equal(DecisionTokens.ProceededBestGuess, d.ForcedPastDecision!.Decision);
        Assert.Equal("12-implement-events-endpoint", d.ForcedPastDecision.Subject);
        Assert.Equal("task", d.ForcedPastDecision.Boundary);
    }

    /// <summary>
    /// The override is a fact about the DELIVERY ATTEMPT, not about its success: an override that unlocked
    /// a merge the user's dirty tree then refused still happened, and a post-mortem asking "why did this
    /// run try to deliver machine-decided work?" needs the answer on the refused run too.
    /// </summary>
    [Fact]
    public void AForcedDeliveryTheMergeThenRefused_StillRecordsTheOverride()
    {
        RunReport report = Report(outcome: MergeOnSuccessResult.Conflict) with
        {
            DeliverySuppressingDecision = new DecisionEntry
            {
                Boundary = "wave",
                Policy = "auto",
                Decision = DecisionTokens.ProceededUnreviewed,
                Subject = "wave-02-build",
                Headline = "proceeded through an unreviewed wave"
            },
            DeliveryForcedPastDecision = true
        };

        DeliverySection d = RunCommand.DescribeDelivery(report, terminalGatePassed: true, PlanDir);

        Assert.False(d.Delivered);
        Assert.Equal(DeliveryOutcome.Conflict, d.Outcome);
        Assert.NotNull(d.ForcedPastDecision);
        Assert.Equal(DecisionTokens.ProceededUnreviewed, d.ForcedPastDecision!.Decision);
        Assert.Equal("wave-02-build", d.ForcedPastDecision.Subject);
    }

    [Fact]
    public void AnOrdinaryDelivery_RecordsNoOverride()
    {
        // The load-bearing negative: nearly every run. Recording an override here would claim a bypass
        // that never happened, of an interlock that never engaged.
        DeliverySection d = RunCommand.DescribeDelivery(
            Report(outcome: MergeOnSuccessResult.FastForwarded, deliveredToBranch: "master"),
            terminalGatePassed: true, PlanDir);

        Assert.True(d.Delivered);
        Assert.Null(d.ForcedPastDecision);
    }

    [Theory]
    [InlineData(MergeOnSuccessResult.FastForwarded, DeliveryOutcome.FastForwarded)]
    [InlineData(MergeOnSuccessResult.Merged, DeliveryOutcome.Merged)]
    public void ADeliveredRun_RecordsDelivered_WithTheBranchItLandedOn(
        MergeOnSuccessResult outcome, DeliveryOutcome expected)
    {
        DeliverySection d = RunCommand.DescribeDelivery(
            Report(outcome: outcome, deliveredToBranch: "master"), terminalGatePassed: true, PlanDir);

        Assert.True(d.Delivered);
        Assert.Equal(expected, d.Outcome);
        Assert.Equal("master", d.DeliveredToBranch);

        // No reason and no plan branch on a delivered run: both fields exist to explain a NON-delivery, and
        // populating them here would make "is this delivered?" ambiguous to a reader scanning for them.
        Assert.Null(d.Reason);
        Assert.Null(d.PlanBranch);
    }

    /// <summary>
    /// A refused merge is NOT a delivery, however green the DAG was. Each refusing outcome keeps its own
    /// token — collapsing them to a single "failed" would throw away the one thing that tells the operator
    /// what to fix.
    /// </summary>
    [Theory]
    [InlineData(MergeOnSuccessResult.Conflict, DeliveryOutcome.Conflict)]
    [InlineData(MergeOnSuccessResult.DirtyWorkingTree, DeliveryOutcome.DirtyWorkingTree)]
    [InlineData(MergeOnSuccessResult.HookRejected, DeliveryOutcome.HookRejected)]
    [InlineData(MergeOnSuccessResult.BranchMoved, DeliveryOutcome.BranchMoved)]
    public void ARefusedMerge_IsNotDelivered_AndKeepsItsOwnOutcomeAndDetail(
        MergeOnSuccessResult outcome, DeliveryOutcome expected)
    {
        DeliverySection d = RunCommand.DescribeDelivery(
            Report(outcome: outcome, detail: "src/Thing.cs"), terminalGatePassed: true, PlanDir);

        Assert.False(d.Delivered);
        Assert.Equal(expected, d.Outcome);
        Assert.Equal(PlanBranch, d.PlanBranch);
        Assert.Equal("src/Thing.cs", d.Detail);
        Assert.NotNull(d.Reason);
    }

    /// <summary>
    /// Issue #588: a run whose checkout moved off the branch it pinned merged NOTHING, so the durable
    /// record must say so — its own <c>branch-moved</c> token (not the generic <c>not-attempted</c> that
    /// would send a reader hunting for a delivery that never happened), the two branch names in
    /// <c>detail</c>, the plan branch holding the work, and NO <c>deliveredToBranch</c>. That last absence
    /// is the whole issue: the pre-fix run reported a delivery to a branch the work never reached, and
    /// the durable record would have repeated the claim for any post-mortem reading it later.
    /// </summary>
    [Fact]
    public void AMovedCheckout_RecordsBranchMoved_NamingBothBranches_AndNoDeliveryTarget()
    {
        const string detail = "run started on 'master'; HEAD is now 'design/34-run-event-stream-and-attach'";

        DeliverySection d = RunCommand.DescribeDelivery(
            Report(outcome: MergeOnSuccessResult.BranchMoved, detail: detail),
            terminalGatePassed: true, PlanDir);

        Assert.False(d.Delivered);
        Assert.Equal(DeliveryOutcome.BranchMoved, d.Outcome);
        Assert.Null(d.DeliveredToBranch);
        Assert.Equal(PlanBranch, d.PlanBranch);
        Assert.Equal(detail, d.Detail);

        // The SSOT §7 kebab spelling survives the round-trip that makes the record durable.
        var doc = new JournalDocument
        {
            RunId = "2026-09-01T00-00-00Z-abcd",
            PlanHash = "sha256:abc",
            Delivery = d,
        };
        string json = JsonSerializer.Serialize(doc, JournalJson.Options);
        Assert.Contains("\"outcome\": \"branch-moved\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("deliveredToBranch", json, StringComparison.Ordinal);

        JournalDocument back = JsonSerializer.Deserialize<JournalDocument>(json, JournalJson.Options)!;
        Assert.Equal(DeliveryOutcome.BranchMoved, back.Delivery!.Outcome);
        Assert.Equal(detail, back.Delivery.Detail);
    }

    /// <summary>
    /// The three ways nothing was ever attempted must be DISTINGUISHABLE in the record. "Not delivered"
    /// alone sends a reader hunting for an unmerged branch that, in two of these three cases, holds nothing
    /// they need — and in the third holds everything.
    /// </summary>
    [Fact]
    public void TheReasonsForNotAttempting_AreDistinguishable_NotJustNotDelivered()
    {
        string failedGate = RunCommand.DescribeDelivery(
            Report(), terminalGatePassed: false, PlanDir).Reason!;
        string notGreen = RunCommand.DescribeDelivery(
            Report(allSucceeded: false), terminalGatePassed: true, PlanDir).Reason!;
        string serial = RunCommand.DescribeDelivery(
            Report(), terminalGatePassed: true, PlanDir).Reason!;

        Assert.Contains("terminal gate", failedGate, StringComparison.Ordinal);
        Assert.Contains("not wholly green", notGreen, StringComparison.Ordinal);
        Assert.Contains("serial mode", serial, StringComparison.Ordinal);

        Assert.Equal(3, new HashSet<string>(StringComparer.Ordinal) { failedGate, notGreen, serial }.Count);
    }

    /// <summary>
    /// Serial mode strands nothing — the work is already in the checkout — so it must NOT name a plan
    /// branch. Naming one would send an operator to merge a branch that does not exist, which is a worse
    /// failure than the silence this issue is about.
    /// </summary>
    [Fact]
    public void SerialMode_NamesNoPlanBranch_BecauseNothingIsStranded()
    {
        DeliverySection d = RunCommand.DescribeDelivery(Report(), terminalGatePassed: true, PlanDir);

        Assert.Null(d.PlanBranch);
        Assert.Contains("already in your checkout", d.Reason!, StringComparison.Ordinal);
    }

    /// <summary>
    /// The record has to survive the round-trip that makes it durable, in the SSOT's kebab spelling. A
    /// section that serializes but cannot be read back is not a record.
    /// </summary>
    [Fact]
    public void TheSection_RoundTripsThroughRunJson_InTheSsotKebabSpelling()
    {
        var doc = new JournalDocument
        {
            RunId = "2026-08-30T00-00-00Z-abcd",
            PlanHash = "sha256:abc",
            Delivery = new DeliverySection
            {
                Delivered = false,
                Outcome = DeliveryOutcome.DirtyWorkingTree,
                Reason = "refused",
                PlanBranch = PlanBranch,
            },
        };

        string json = JsonSerializer.Serialize(doc, JournalJson.Options);

        Assert.Contains("\"delivery\"", json, StringComparison.Ordinal);
        Assert.Contains("\"outcome\": \"dirty-working-tree\"", json, StringComparison.Ordinal);

        JournalDocument back = JsonSerializer.Deserialize<JournalDocument>(json, JournalJson.Options)!;
        Assert.NotNull(back.Delivery);
        Assert.False(back.Delivery!.Delivered);
        Assert.Equal(DeliveryOutcome.DirtyWorkingTree, back.Delivery.Outcome);
        Assert.Equal(PlanBranch, back.Delivery.PlanBranch);
    }

    /// <summary>
    /// Additive and backward-compatible, on the same terms as every other optional section: a run that
    /// never reached a delivery decision writes NO <c>delivery</c> key at all — not a null one. An older
    /// reader, and every existing byte-comparison, must see exactly what it saw before #542.
    /// </summary>
    [Fact]
    public void ARunWithNoDeliveryDecision_WritesNoDeliveryKeyAtAll()
    {
        var doc = new JournalDocument
        {
            RunId = "2026-08-30T00-00-00Z-abcd",
            PlanHash = "sha256:abc",
        };

        Assert.DoesNotContain("delivery", JsonSerializer.Serialize(doc, JournalJson.Options),
            StringComparison.OrdinalIgnoreCase);
    }
}
