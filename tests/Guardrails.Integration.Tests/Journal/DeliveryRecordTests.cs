using System.Text.Json;
using Guardrails.Cli.Commands;
using Guardrails.Core.Execution;
using Guardrails.Core.Journal;

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
        DeliverySection d = RunCommand.DescribeDelivery(
            Report(whollyGreenButUndelivered: true), terminalGatePassed: true, PlanDir);

        Assert.False(d.Delivered);
        Assert.Equal(DeliveryOutcome.NotAttempted, d.Outcome);
        Assert.Equal(PlanBranch, d.PlanBranch);
        Assert.NotNull(d.Reason);
        Assert.Contains("mergeOnSuccess resolved off", d.Reason, StringComparison.Ordinal);
        Assert.Contains(PlanBranch, d.Reason, StringComparison.Ordinal);
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
