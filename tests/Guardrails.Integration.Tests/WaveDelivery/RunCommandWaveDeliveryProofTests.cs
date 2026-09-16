using System.Diagnostics;
using System.Text.Json;
using Guardrails.Cli;
using Guardrails.Core.Journal;

namespace Guardrails.Integration.Tests.WaveDelivery;

/// <summary>
/// Design 39 (issue #525) proven through the REAL <c>guardrails run</c> command path: in-process through
/// <see cref="CommandFactory.BuildRootCommand"/>, so the run goes through <c>RunCommand</c>'s flag
/// resolution, <c>SchedulerFactory</c>'s real <c>GitWorktreeProvider</c>, the observer chain, the
/// end-of-run report and the exit-code mapping — against a temp git repository, script actions only.
/// <para>
/// <b>Why this class exists alongside the WaveDelivery suite.</b> Most of that suite drives the
/// <c>Scheduler</c> directly, and its CLI-driven rows each check one surface: exit code and report order
/// (<see cref="PartialDeliveryReportTests"/>), or git state for the final wave and the override
/// (<see cref="WaveBarrierDeliveryTests"/>). No row checks, on one real run, that git state, <c>run.json</c>
/// and the console tell the same story. Review residual 19 records that nothing pinned the run path's call
/// to <c>RunCommand.RenderWaveDeliveryReport</c> at all.
/// </para>
/// <para>
/// <b>How "delivered at the barrier" is observed.</b> Each wave's one task records the tip of the user's
/// branch into a file outside the repository while it runs. Wave 1's recording is the control: the branch
/// had not moved yet, so the mechanism can see "not delivered". Wave 2's recording happens after wave 1's
/// barrier and before run end, so it shows whether the barrier really moved the branch. Commits are
/// immutable, so every "does this tip carry wave 1's file" question is asked of git after the run. The
/// branch's reflog counts how many times the harness moved the user's branch: one entry per ref update,
/// a decision rather than a duration.
/// </para>
/// <para>
/// Each scenario is ONE real run, executed lazily by <see cref="Runs"/> the first time a fact asks for it
/// and then shared, so every claim below gets its own verdict without paying for another run.
/// </para>
/// </summary>
[Trait("Category", "WaveDelivery")]
public sealed class RunCommandWaveDeliveryProofTests : IClassFixture<RunCommandWaveDeliveryProofTests.Runs>
{
    private const string Wave1 = "wave-01-deliver";
    private const string Wave2 = "wave-02-final";
    private const string PlanBranch = "guardrails/plan";

    private static readonly bool Ps = OperatingSystem.IsWindows();

    private readonly Runs _runs;

    public RunCommandWaveDeliveryProofTests(Runs runs) => _runs = runs;

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Scenario 1 — wave 1 `delivers: true`, wave 2 an ordinary final wave, everything green.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task BarrierDelivery_PutsWaveOneOnTheUsersBranchBeforeWaveTwoRuns()
    {
        ScenarioRun run = await _runs.BarrierDeliveryAsync();

        // Controls: nothing of the plan was on the user's branch before the run, and while wave 1 ran the
        // branch had not moved — so the observation below is capable of seeing "not delivered".
        Assert.False(run.Wave1OnUserBranchBeforeRun, "fixture error: wave1.txt was on the user's branch before the run.");
        Assert.Equal(run.InitialTip, run.RequireTipDuringWave1());

        string duringWave2 = run.RequireTipDuringWave2();
        Assert.True(duringWave2 != run.InitialTip,
            "while wave 2 ran, the user's branch was still at its pre-run tip: wave 1 was not delivered at its barrier.\n"
            + run.Describe());
        Assert.True(run.Repo.HasFile(duringWave2, "wave1.txt"),
            $"the user's branch tip while wave 2 ran ({duringWave2}) does not carry wave1.txt.\n{run.Describe()}");
        Assert.False(run.Repo.HasFile(duringWave2, "wave2.txt"),
            $"the user's branch tip while wave 2 ran ({duringWave2}) already carries wave2.txt — that is not a barrier delivery.\n{run.Describe()}");
    }

    [Fact]
    public async Task BarrierDelivery_TheFinalWaveLandsAtRunEnd_InExactlyTwoMovesOfTheUsersBranch()
    {
        ScenarioRun run = await _runs.BarrierDeliveryAsync();

        Assert.True(run.Repo.HasFile(run.FinalTip, "wave1.txt"), run.Describe());
        Assert.True(run.Repo.HasFile(run.FinalTip, "wave2.txt"),
            "wave 2's work did not land on the user's branch at run end.\n" + run.Describe());
        Assert.True(run.Repo.IsAncestor(run.RequireTipDuringWave2(), run.FinalTip),
            "the run-end delivery did not build on the barrier delivery.\n" + run.Describe());

        // One ref update at wave 1's barrier, one at run end.
        Assert.True(run.UserBranchMoves == 2,
            $"expected the harness to move the user's branch twice (barrier, then run end); it moved {run.UserBranchMoves} time(s).\n"
            + run.Describe());
    }

    [Fact]
    public async Task BarrierDelivery_IsRecordedInRunJson()
    {
        ScenarioRun run = await _runs.BarrierDeliveryAsync();

        JsonElement wave1 = run.WaveEntry(Wave1);
        Assert.Equal("completed", Json.String(wave1, "status", $"waves.{Wave1}"));

        JsonElement delivered = Json.Prop(wave1, "delivered", $"waves.{Wave1}");
        Assert.Equal("delivered", Json.String(delivered, "status", $"waves.{Wave1}.delivered"));
        Assert.Equal("fast-forwarded", Json.String(delivered, "outcome", $"waves.{Wave1}.delivered"));
        Assert.Equal(run.RequireTipDuringWave2(), Json.String(delivered, "commit", $"waves.{Wave1}.delivered"));
        string[] expectedCovers = [Wave1];
        Assert.Equal(expectedCovers, Json.Strings(delivered, "covers", $"waves.{Wave1}.delivered"));

        // Design 39 §4: the plan's final wave delivers through the run-end call, never at its barrier.
        Assert.False(run.WaveEntry(Wave2).TryGetProperty("delivered", out _),
            $"waves.{Wave2} carries a 'delivered' record, but the final wave never barrier-delivers:\n{run.RunJson}");

        JsonElement delivery = Json.Prop(run.RunJson, "delivery", "run.json");
        Assert.True(Json.Bool(delivery, "delivered", "delivery"), $"delivery:\n{delivery}");
        Assert.Equal("fast-forwarded", Json.String(delivery, "outcome", "delivery"));
        Assert.Equal(run.UserBranch, Json.String(delivery, "deliveredToBranch", "delivery"));
    }

    [Fact]
    public async Task BarrierDelivery_IsAnnouncedBeforeWaveTwoStarts_AndTheRunExitsSuccess()
    {
        ScenarioRun run = await _runs.BarrierDeliveryAsync();

        Assert.True(run.ExitCode == ExitCodes.Success,
            $"expected exit {ExitCodes.Success}, got {run.ExitCode}.\n{run.Output}");

        string announcement = $"[delivered] {Wave1}: {run.RequireTipDuringWave2()} (covers {Wave1})";
        int announcedAt = run.Output.IndexOf(announcement, StringComparison.Ordinal);
        int wave2StartsAt = run.Output.IndexOf($"===== Wave 2/2: {Wave2}", StringComparison.Ordinal);
        Assert.True(announcedAt >= 0, $"the console never announced the barrier delivery ('{announcement}'):\n{run.Output}");
        Assert.True(wave2StartsAt >= 0, $"the console never showed wave 2 starting:\n{run.Output}");
        Assert.True(announcedAt < wave2StartsAt,
            $"the barrier delivery was announced after wave 2 started, not at wave 1's barrier:\n{run.Output}");

        // The partial-delivery report is for a run where a later wave fails (README "The partial-delivery
        // report"); a wholly delivered run announces its deliveries and prints no such report.
        Assert.DoesNotContain("WAVE DELIVERY REPORT", run.Output, StringComparison.Ordinal);

        // #340's delivered-by-default notice speaks for the run-end delivery. This fixture sets neither the
        // mergeOnSuccess key nor a flag, so it fires here — the control for its absence on a partial run.
        Assert.Contains($"delivered to {run.UserBranch} (mergeOnSuccess now defaults on", run.Output, StringComparison.Ordinal);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Scenario 2 — same shape, but wave 2's EXIT GATE fails.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ExitGatePartial_WaveOneIsOnTheUsersBranch_WaveTwoIsHeldOnThePlanBranch()
    {
        ScenarioRun run = await _runs.ExitGatePartialAsync();

        Assert.False(run.Wave1OnUserBranchBeforeRun, "fixture error: wave1.txt was on the user's branch before the run.");
        Assert.Equal(run.InitialTip, run.RequireTipDuringWave1());

        Assert.True(run.Repo.HasFile(run.FinalTip, "wave1.txt"),
            "wave 1 delivered at its barrier, but its work is not on the user's branch.\n" + run.Describe());
        Assert.False(run.Repo.HasFile(run.FinalTip, "wave2.txt"),
            "wave 2 failed its exit gate, but its work reached the user's branch.\n" + run.Describe());
        Assert.Equal(run.RequireTipDuringWave2(), run.FinalTip);
        Assert.True(run.UserBranchMoves == 1,
            $"expected exactly one move of the user's branch (wave 1's barrier); saw {run.UserBranchMoves}.\n{run.Describe()}");

        // The held work really exists: wave 2's task merged onto the plan branch before its exit gate failed,
        // and git's own confirmation — the one the README tells the operator to use — names that branch.
        Assert.True(run.Repo.HasFile(PlanBranch, "wave2.txt"), "wave 2's work is not on the plan branch.\n" + run.Describe());
        Assert.Contains(PlanBranch, run.BranchesNotMergedIntoUserBranch);
    }

    [Fact]
    public async Task ExitGatePartial_RunJsonRecordsPartiallyDelivered()
    {
        ScenarioRun run = await _runs.ExitGatePartialAsync();

        JsonElement delivered = Json.Prop(run.WaveEntry(Wave1), "delivered", $"waves.{Wave1}");
        Assert.Equal("delivered", Json.String(delivered, "status", $"waves.{Wave1}.delivered"));
        Assert.Equal(run.FinalTip, Json.String(delivered, "commit", $"waves.{Wave1}.delivered"));

        JsonElement delivery = Json.Prop(run.RunJson, "delivery", "run.json");
        Assert.Equal("partially-delivered", Json.String(delivery, "outcome", "delivery"));
        Assert.False(Json.Bool(delivery, "delivered", "delivery"), $"delivery:\n{delivery}");
    }

    [Fact]
    public async Task ExitGatePartial_PrintsTheReportBeforeTheVerdict_AndExitsFailure()
    {
        ScenarioRun run = await _runs.ExitGatePartialAsync();

        Assert.True(run.ExitCode == ExitCodes.TaskFailed,
            $"expected exit {ExitCodes.TaskFailed}, got {run.ExitCode}.\n{run.Output}");

        string report = RequireReport(run.Output);
        Assert.Contains($"Delivered at their own barrier: {Wave1}.", report, StringComparison.Ordinal);
        Assert.Contains($"Held (the run halted here): {Wave2}.", report, StringComparison.Ordinal);

        int reportAt = run.Output.IndexOf("WAVE DELIVERY REPORT", StringComparison.Ordinal);
        int verdictAt = run.Output.IndexOf("WAVE EXIT GATE FAILED:", StringComparison.Ordinal);
        Assert.True(verdictAt >= 0, $"the wave exit-gate verdict was not printed:\n{run.Output}");
        Assert.True(reportAt < verdictAt, $"the delivery report must print before the verdict:\n{run.Output}");

        // Wave 1 landed on the user's branch, but the run-end delivery did not: #340's delivered-by-default
        // notice speaks for the run-end delivery, so it must not print on a partial run.
        Assert.DoesNotContain("mergeOnSuccess now defaults on", run.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// Design 39 §4, "run.json's top-level delivery on a partial run — DECIDED (review round 4,
    /// d39-partial-delivery-record)": "<c>deliveredToBranch</c> and <c>planBranch</c> are both set". The only
    /// row pinning it (<c>PartialDeliveryReportTests.DescribeDelivery_APartialDelivery_IsPartiallyDelivered</c>)
    /// hand-builds a <c>RunReport</c> with <c>DeliveredToBranch</c> already filled in; this one reads what a
    /// real run wrote.
    /// </summary>
    [Fact]
    public async Task ExitGatePartial_RunJsonNamesThePlanBranchAndTheBranchItDeliveredTo()
    {
        ScenarioRun run = await _runs.ExitGatePartialAsync();

        JsonElement delivery = Json.Prop(run.RunJson, "delivery", "run.json");
        Assert.Equal(PlanBranch, Json.String(delivery, "planBranch", "delivery"));
        Assert.Equal(run.UserBranch, Json.String(delivery, "deliveredToBranch", "delivery"));
    }

    /// <summary>Design 39 §4 (same DECIDED paragraph): "<c>reason</c> names the delivered and held waves."</summary>
    [Fact]
    public async Task ExitGatePartial_RunJsonReasonNamesTheDeliveredAndTheHeldWave()
    {
        ScenarioRun run = await _runs.ExitGatePartialAsync();

        JsonElement delivery = Json.Prop(run.RunJson, "delivery", "run.json");
        string reason = Json.String(delivery, "reason", "delivery");
        Assert.Contains(Wave1, reason, StringComparison.Ordinal);
        Assert.True(reason.Contains(Wave2, StringComparison.Ordinal),
            $"delivery.reason does not name the held wave '{Wave2}': {reason}");
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Scenario 2b — same shape, but a wave-2 TASK fails, so wave 2 never reaches its barrier.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TaskFailurePartial_WaveOneIsOnTheUsersBranch_WaveTwoIsNot()
    {
        ScenarioRun run = await _runs.TaskFailurePartialAsync();

        Assert.False(run.Wave1OnUserBranchBeforeRun, "fixture error: wave1.txt was on the user's branch before the run.");
        Assert.Equal(run.InitialTip, run.RequireTipDuringWave1());

        Assert.True(run.Repo.HasFile(run.FinalTip, "wave1.txt"),
            "wave 1 delivered at its barrier, but its work is not on the user's branch.\n" + run.Describe());
        Assert.False(run.Repo.HasFile(run.FinalTip, "wave2.txt"),
            "a wave-2 task failed, but wave 2's work reached the user's branch.\n" + run.Describe());
        Assert.Equal(run.RequireTipDuringWave2(), run.FinalTip);
        Assert.True(run.UserBranchMoves == 1,
            $"expected exactly one move of the user's branch (wave 1's barrier); saw {run.UserBranchMoves}.\n{run.Describe()}");
        Assert.Contains(PlanBranch, run.BranchesNotMergedIntoUserBranch);
    }

    [Fact]
    public async Task TaskFailurePartial_RunJsonRecordsPartiallyDelivered()
    {
        ScenarioRun run = await _runs.TaskFailurePartialAsync();

        JsonElement delivered = Json.Prop(run.WaveEntry(Wave1), "delivered", $"waves.{Wave1}");
        Assert.Equal("delivered", Json.String(delivered, "status", $"waves.{Wave1}.delivered"));

        JsonElement delivery = Json.Prop(run.RunJson, "delivery", "run.json");
        Assert.Equal("partially-delivered", Json.String(delivery, "outcome", "delivery"));
        Assert.False(Json.Bool(delivery, "delivered", "delivery"), $"delivery:\n{delivery}");
    }

    /// <summary>Design 39 §4: "<c>deliveredToBranch</c> and <c>planBranch</c> are both set" — on this path too.</summary>
    [Fact]
    public async Task TaskFailurePartial_RunJsonNamesThePlanBranchAndTheBranchItDeliveredTo()
    {
        ScenarioRun run = await _runs.TaskFailurePartialAsync();

        JsonElement delivery = Json.Prop(run.RunJson, "delivery", "run.json");
        Assert.Equal(PlanBranch, Json.String(delivery, "planBranch", "delivery"));
        Assert.Equal(run.UserBranch, Json.String(delivery, "deliveredToBranch", "delivery"));
    }

    /// <summary>Design 39 §4: "<c>reason</c> names the delivered and held waves."</summary>
    [Fact]
    public async Task TaskFailurePartial_RunJsonReasonNamesTheDeliveredAndTheHeldWave()
    {
        ScenarioRun run = await _runs.TaskFailurePartialAsync();

        JsonElement delivery = Json.Prop(run.RunJson, "delivery", "run.json");
        string reason = Json.String(delivery, "reason", "delivery");
        Assert.Contains(Wave1, reason, StringComparison.Ordinal);
        Assert.True(reason.Contains(Wave2, StringComparison.Ordinal),
            $"delivery.reason does not name the held wave '{Wave2}': {reason}");
    }

    /// <summary>
    /// README "The partial-delivery report": "When an earlier wave has delivered and a later one fails, the
    /// run prints which waves landed on your branch and which are held on the plan branch, before the
    /// verdict". Design 39 §4's own example is this case — a later wave's TASK needs a human.
    /// </summary>
    [Fact]
    public async Task TaskFailurePartial_PrintsTheReportNamingTheHeldWave_BeforeTheVerdict_AndExitsFailure()
    {
        ScenarioRun run = await _runs.TaskFailurePartialAsync();

        Assert.True(run.ExitCode == ExitCodes.TaskFailed,
            $"expected exit {ExitCodes.TaskFailed}, got {run.ExitCode}.\n{run.Output}");

        string report = RequireReport(run.Output);
        Assert.Contains($"Delivered at their own barrier: {Wave1}.", report, StringComparison.Ordinal);
        Assert.Contains($"Held on the plan branch: {Wave2} (01-write needs-human).", report, StringComparison.Ordinal);

        int reportAt = run.Output.IndexOf("WAVE DELIVERY REPORT", StringComparison.Ordinal);
        int verdictAt = run.Output.IndexOf($"NEEDS HUMAN: {Wave2}/01-write", StringComparison.Ordinal);
        Assert.True(verdictAt >= 0, $"the needs-human verdict for {Wave2}/01-write was not printed:\n{run.Output}");
        Assert.True(reportAt < verdictAt, $"the delivery report must print before the verdict:\n{run.Output}");
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // The checkout, not only the ref. A promotion that moved the branch ref without updating the index and
    // working tree (update-ref instead of merge --ff-only) passed every ref-reading check above while the user's
    // checkout showed wave1.txt as a staged deletion. `--untracked-files=no`: the fixture's plan folder is
    // untracked inside the repository.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task BarrierDelivery_LandsInTheUsersCheckout_NotOnlyOnTheBranchRef()
    {
        ScenarioRun run = await _runs.BarrierDeliveryAsync();

        Assert.True(run.Wave1FileInCheckout, "wave1.txt is not in the user's checkout after delivery.\n" + run.Describe());
        Assert.True(run.TrackedStatus.Trim().Length == 0,
            $"the user's checkout has tracked changes after delivery:\n{run.TrackedStatus}\n{run.Describe()}");
    }

    [Fact]
    public async Task ExitGatePartial_TheBarrierDeliveryLandsInTheUsersCheckout_NotOnlyOnTheBranchRef()
    {
        ScenarioRun run = await _runs.ExitGatePartialAsync();

        Assert.True(run.Wave1FileInCheckout, "wave1.txt is not in the user's checkout after the barrier delivery.\n" + run.Describe());
        Assert.True(run.TrackedStatus.Trim().Length == 0,
            $"the user's checkout has tracked changes after the barrier delivery:\n{run.TrackedStatus}\n{run.Describe()}");
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // B1 through the real run command: every process re-pins the delivery target from HEAD, so a resume on
    // another branch — or on a detached HEAD — must not rename the branch wave 1's barrier delivery landed on.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AResumeOnAnotherBranch_KeepsDeliveredToBranchNamingTheBranchTheBarrierLandedOn()
    {
        using var repo = new TempGitRepo();
        string userBranch = repo.CurrentBranch();
        string planDir = CreateTwoWavePlan(
            repo, userBranch, wave1Delivers: true, Wave2Failure.Task,
            Path.Combine(repo.Root, "tip-during-wave1.txt"), Path.Combine(repo.Root, "tip-during-wave2.txt"));

        (int exit1, string output1) = await RunCliAsync("run", planDir, "--no-ui", "--no-log-server");
        Assert.True(exit1 == ExitCodes.TaskFailed, $"run 1: expected exit {ExitCodes.TaskFailed}, got {exit1}.\n{output1}");
        Assert.True(repo.HasFile(userBranch, "wave1.txt"), "run 1: wave 1 did not deliver at its barrier.\n" + output1);
        Assert.Equal(userBranch, PartialDeliveredToBranch(planDir));

        repo.Switch("-c", "spike");
        (int exit2, string output2) = await RunCliAsync("run", planDir, "--no-ui", "--no-log-server");
        Assert.True(exit2 == ExitCodes.TaskFailed, $"run 2 (resumed on 'spike'): expected exit {ExitCodes.TaskFailed}, got {exit2}.\n{output2}");
        Assert.Equal(userBranch, PartialDeliveredToBranch(planDir));

        repo.Switch("--detach");
        (int exit3, string output3) = await RunCliAsync("run", planDir, "--no-ui", "--no-log-server");
        Assert.True(exit3 == ExitCodes.TaskFailed, $"run 3 (resumed on a detached HEAD): expected exit {ExitCodes.TaskFailed}, got {exit3}.\n{output3}");
        Assert.Equal(userBranch, PartialDeliveredToBranch(planDir));
    }

    private static async Task<(int Exit, string Output)> RunCliAsync(params string[] args)
    {
        var io = new StringConsoleIo();
        int exit = await CommandFactory.BuildRootCommand(io).Parse(args)
            .InvokeAsync(configuration: null, TestContext.Current.CancellationToken);
        return (exit, io.OutText);
    }

    private static string PartialDeliveredToBranch(string planDir)
    {
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(RunJournal.PathFor(planDir)));
        JsonElement delivery = Json.Prop(doc.RootElement.Clone(), "delivery", "run.json");
        Assert.Equal("partially-delivered", Json.String(delivery, "outcome", "delivery"));
        return Json.String(delivery, "deliveredToBranch", "delivery");
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Regression controls.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task NoDeliveringWave_LeavesTheUsersBranchAloneAtTheBarrier_AndMergesOnceAtRunEnd()
    {
        ScenarioRun run = await _runs.NoDeliveringWaveAsync();

        Assert.True(run.ExitCode == ExitCodes.Success, $"expected exit {ExitCodes.Success}, got {run.ExitCode}.\n{run.Output}");
        Assert.False(run.Wave1OnUserBranchBeforeRun, "fixture error: wave1.txt was on the user's branch before the run.");

        Assert.Equal(run.InitialTip, run.RequireTipDuringWave1());
        Assert.True(run.RequireTipDuringWave2() == run.InitialTip,
            "a plan marking no wave `delivers` moved the user's branch before run end.\n" + run.Describe());

        Assert.True(run.Repo.HasFile(run.FinalTip, "wave1.txt"), run.Describe());
        Assert.True(run.Repo.HasFile(run.FinalTip, "wave2.txt"), run.Describe());
        Assert.True(run.UserBranchMoves == 1,
            $"expected exactly one merge, at run end; the user's branch moved {run.UserBranchMoves} time(s).\n{run.Describe()}");

        Assert.False(run.WaveEntry(Wave1).TryGetProperty("delivered", out _), $"run.json:\n{run.RunJson}");
        Assert.False(run.WaveEntry(Wave2).TryGetProperty("delivered", out _), $"run.json:\n{run.RunJson}");
        JsonElement delivery = Json.Prop(run.RunJson, "delivery", "run.json");
        Assert.True(Json.Bool(delivery, "delivered", "delivery"), $"delivery:\n{delivery}");
        Assert.Equal("fast-forwarded", Json.String(delivery, "outcome", "delivery"));

        Assert.DoesNotContain("[delivered]", run.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("WAVE DELIVERY REPORT", run.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoMergeOnSuccessFlag_SuppressesTheBarrierDelivery()
    {
        ScenarioRun run = await _runs.NoMergeOnSuccessAsync();

        Assert.True(run.ExitCode == ExitCodes.Success, $"expected exit {ExitCodes.Success}, got {run.ExitCode}.\n{run.Output}");
        Assert.False(run.Wave1OnUserBranchBeforeRun, "fixture error: wave1.txt was on the user's branch before the run.");

        Assert.Equal(run.InitialTip, run.RequireTipDuringWave1());
        Assert.True(run.RequireTipDuringWave2() == run.InitialTip,
            "--no-merge-on-success did not stop wave 1's barrier delivery.\n" + run.Describe());
        Assert.True(run.FinalTip == run.InitialTip,
            "--no-merge-on-success did not stop delivery: the user's branch moved.\n" + run.Describe());
        Assert.True(run.UserBranchMoves == 0,
            $"--no-merge-on-success: the user's branch moved {run.UserBranchMoves} time(s).\n{run.Describe()}");

        // The work itself was done and kept — the absence above is suppression, not a run that did nothing.
        Assert.True(run.Repo.HasFile(PlanBranch, "wave1.txt"), run.Describe());
        Assert.True(run.Repo.HasFile(PlanBranch, "wave2.txt"), run.Describe());

        Assert.False(run.WaveEntry(Wave1).TryGetProperty("delivered", out _),
            $"design 39 §4: a wave whose delivery resolved off (--no-merge-on-success) has no 'delivered' key:\n{run.RunJson}");
        JsonElement delivery = Json.Prop(run.RunJson, "delivery", "run.json");
        Assert.False(Json.Bool(delivery, "delivered", "delivery"), $"delivery:\n{delivery}");
        Assert.Equal("not-attempted", Json.String(delivery, "outcome", "delivery"));

        // Control: this plan HAS a delivery point, but nothing landed on the user's branch, so no branch is named.
        Assert.False(delivery.TryGetProperty("deliveredToBranch", out _),
            $"nothing reached the user's branch, yet delivery names a branch it delivered to:\n{delivery}");

        Assert.DoesNotContain("[delivered]", run.Output, StringComparison.Ordinal);
        Assert.Contains("WORK NOT DELIVERED", run.Output, StringComparison.Ordinal);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // The report block, scoped: a wave dir also appears in the per-task summary ("wave-02-final/01-write"),
    // so "the report names wave 2" must be asked of the report's own lines, never of the whole output.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    private static string RequireReport(string output)
    {
        int start = output.IndexOf("WAVE DELIVERY REPORT", StringComparison.Ordinal);
        Assert.True(start >= 0, $"the wave-delivery report was not printed:\n{output}");
        int end = output.IndexOf("git branch --no-merged", start, StringComparison.Ordinal);
        Assert.True(end >= 0, $"the wave-delivery report does not point at `git branch --no-merged`:\n{output}");
        return output[start..end];
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // The shared runs.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    public enum Wave2Failure
    {
        None,
        ExitGate,
        Task
    }

    /// <summary>One real <c>run</c> per scenario, started by the first fact that asks for it.</summary>
    public sealed class Runs : IDisposable
    {
        private readonly List<Lazy<Task<ScenarioRun>>> _all = [];
        private readonly Lazy<Task<ScenarioRun>> _barrier;
        private readonly Lazy<Task<ScenarioRun>> _exitGatePartial;
        private readonly Lazy<Task<ScenarioRun>> _taskFailurePartial;
        private readonly Lazy<Task<ScenarioRun>> _noDeliveringWave;
        private readonly Lazy<Task<ScenarioRun>> _noMergeOnSuccess;

        public Runs()
        {
            _barrier = Track(() => ScenarioRun.ExecuteAsync(wave1Delivers: true, Wave2Failure.None));
            _exitGatePartial = Track(() => ScenarioRun.ExecuteAsync(wave1Delivers: true, Wave2Failure.ExitGate));
            _taskFailurePartial = Track(() => ScenarioRun.ExecuteAsync(wave1Delivers: true, Wave2Failure.Task));
            _noDeliveringWave = Track(() => ScenarioRun.ExecuteAsync(wave1Delivers: false, Wave2Failure.None));
            _noMergeOnSuccess = Track(() => ScenarioRun.ExecuteAsync(
                wave1Delivers: true, Wave2Failure.None, "--no-merge-on-success"));
        }

        public Task<ScenarioRun> BarrierDeliveryAsync() => _barrier.Value;
        public Task<ScenarioRun> ExitGatePartialAsync() => _exitGatePartial.Value;
        public Task<ScenarioRun> TaskFailurePartialAsync() => _taskFailurePartial.Value;
        public Task<ScenarioRun> NoDeliveringWaveAsync() => _noDeliveringWave.Value;
        public Task<ScenarioRun> NoMergeOnSuccessAsync() => _noMergeOnSuccess.Value;

        private Lazy<Task<ScenarioRun>> Track(Func<Task<ScenarioRun>> start)
        {
            var lazy = new Lazy<Task<ScenarioRun>>(start, LazyThreadSafetyMode.ExecutionAndPublication);
            _all.Add(lazy);
            return lazy;
        }

        public void Dispose()
        {
            foreach (Lazy<Task<ScenarioRun>> lazy in _all)
            {
                if (lazy.IsValueCreated && lazy.Value.IsCompletedSuccessfully)
                {
                    lazy.Value.Result.Repo.Dispose();
                }
            }
        }
    }

    /// <summary>Everything one real run left behind, captured once, right after the run returned.</summary>
    public sealed class ScenarioRun
    {
        public required TempGitRepo Repo { get; init; }
        public required string PlanDir { get; init; }
        public required string UserBranch { get; init; }
        public required string InitialTip { get; init; }
        public required bool Wave1OnUserBranchBeforeRun { get; init; }
        public required string? TipDuringWave1 { get; init; }
        public required string? TipDuringWave2 { get; init; }
        public required int ExitCode { get; init; }
        public required string Output { get; init; }
        public required JsonElement RunJson { get; init; }
        public required string FinalTip { get; init; }
        public required IReadOnlyList<string> UserBranchReflog { get; init; }
        public required IReadOnlyList<string> BranchesNotMergedIntoUserBranch { get; init; }
        public required bool Wave1FileInCheckout { get; init; }
        public required string TrackedStatus { get; init; }

        /// <summary>Ref updates of the user's branch during the run — its reflog minus the fixture's own initial commit.</summary>
        public int UserBranchMoves => UserBranchReflog.Count - 1;

        public string RequireTipDuringWave1() => RequireTip(TipDuringWave1, Wave1);

        public string RequireTipDuringWave2() => RequireTip(TipDuringWave2, Wave2);

        private string RequireTip(string? tip, string waveDir)
        {
            Assert.True(tip is not null,
                $"{waveDir}'s task never recorded the user's branch tip — did it run?\n{Describe()}");
            Assert.Matches("^[0-9a-f]{40}([0-9a-f]{24})?$", tip);
            return tip!;
        }

        public JsonElement WaveEntry(string waveDir)
        {
            JsonElement waves = Json.Prop(RunJson, "waves", "run.json");
            return Json.Prop(waves, waveDir, "waves");
        }

        public string Describe() =>
            $"user branch '{UserBranch}': initial {InitialTip}, during wave 1 {TipDuringWave1 ?? "<not recorded>"}, "
            + $"during wave 2 {TipDuringWave2 ?? "<not recorded>"}, final {FinalTip}; exit {ExitCode}.\n"
            + $"reflog of '{UserBranch}' (newest first):\n  {string.Join("\n  ", UserBranchReflog)}\n"
            + $"--- run output ---\n{Output}";

        public static async Task<ScenarioRun> ExecuteAsync(bool wave1Delivers, Wave2Failure wave2Failure, params string[] extraArgs)
        {
            var repo = new TempGitRepo();
            try
            {
                string userBranch = repo.CurrentBranch();
                string initialTip = repo.TipOf(userBranch);
                string wave1Observation = Path.Combine(repo.Root, "user-tip-during-wave1.txt");
                string wave2Observation = Path.Combine(repo.Root, "user-tip-during-wave2.txt");
                string planDir = CreateTwoWavePlan(
                    repo, userBranch, wave1Delivers, wave2Failure, wave1Observation, wave2Observation);
                bool wave1BeforeRun = repo.HasFile(userBranch, "wave1.txt");

                var io = new StringConsoleIo();
                string[] args = ["run", planDir, "--no-ui", "--no-log-server", .. extraArgs];
                int exit = await CommandFactory.BuildRootCommand(io).Parse(args)
                    .InvokeAsync(configuration: null, TestContext.Current.CancellationToken);

                string runJsonPath = RunJournal.PathFor(planDir);
                Assert.True(File.Exists(runJsonPath), $"the run never wrote {runJsonPath}:\n{io.OutText}");
                using JsonDocument runJson = JsonDocument.Parse(File.ReadAllText(runJsonPath));

                return new ScenarioRun
                {
                    Repo = repo,
                    PlanDir = planDir,
                    UserBranch = userBranch,
                    InitialTip = initialTip,
                    Wave1OnUserBranchBeforeRun = wave1BeforeRun,
                    TipDuringWave1 = ReadObservation(wave1Observation),
                    TipDuringWave2 = ReadObservation(wave2Observation),
                    ExitCode = exit,
                    Output = io.OutText,
                    RunJson = runJson.RootElement.Clone(),
                    FinalTip = repo.TipOf(userBranch),
                    UserBranchReflog = repo.BranchReflog(userBranch),
                    BranchesNotMergedIntoUserBranch = repo.BranchesNotMergedInto(userBranch),
                    Wave1FileInCheckout = File.Exists(Path.Combine(repo.RepoPath, "wave1.txt")),
                    TrackedStatus = repo.TrackedStatus()
                };
            }
            catch
            {
                repo.Dispose();
                throw;
            }
        }

        private static string? ReadObservation(string path) =>
            File.Exists(path) ? File.ReadAllText(path).Trim() : null;
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // The plan: two waves in a temp repo, maxParallelism 2 so the run is in worktree mode (a plan branch
    // exists — SchedulerFactory.ResolveWorktreeMode resolves maxParallelism <= 1 to serial), retries pinned
    // to 0.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    private static string CreateTwoWavePlan(
        TempGitRepo repo, string userBranch, bool wave1Delivers, Wave2Failure wave2Failure,
        string wave1Observation, string wave2Observation)
    {
        string planDir = Path.Combine(repo.RepoPath, "plan");
        Directory.CreateDirectory(Path.Combine(planDir, "state"));
        File.WriteAllText(Path.Combine(planDir, "guardrails.json"),
            """
            {
              "version": 1,
              "guardrailMode": "failFast",
              "workspace": "..",
              "defaultRetries": 0,
              "maxParallelism": 2
            }
            """);

        string w1 = Path.Combine(planDir, Wave1);
        Directory.CreateDirectory(w1);
        if (wave1Delivers)
        {
            File.WriteAllText(Path.Combine(w1, "brief.md"),
                "---\ndelivers: true\n---\n# wave-01\n\nDelivers at its own barrier.\n");
        }

        WriteObservingTask(Path.Combine(w1, "tasks", "01-write"), "wave1.txt", repo.RepoPath, userBranch,
            wave1Observation, guardrailPasses: true);
        WriteFileExistsGate(Path.Combine(w1, "guardrails", Script("01-wave1-present")), "wave1.txt");

        string w2 = Path.Combine(planDir, Wave2);
        WriteObservingTask(Path.Combine(w2, "tasks", "01-write"), "wave2.txt", repo.RepoPath, userBranch,
            wave2Observation, guardrailPasses: wave2Failure != Wave2Failure.Task);
        if (wave2Failure == Wave2Failure.ExitGate)
        {
            WriteAlwaysFailGate(Path.Combine(w2, "guardrails", Script("01-always-fail")));
        }
        else
        {
            WriteFileExistsGate(Path.Combine(w2, "guardrails", Script("01-wave2-present")), "wave2.txt");
        }

        return planDir;
    }

    private static string Script(string stem) => Ps ? stem + ".ps1" : stem + ".sh";

    /// <summary>
    /// A task that writes <paramref name="file"/> into its workspace and records the tip of
    /// <paramref name="userBranch"/> in <paramref name="userRepo"/> to <paramref name="observationPath"/>
    /// (outside the repository). Its one guardrail checks the file, or fails by design.
    /// </summary>
    private static void WriteObservingTask(
        string taskDir, string file, string userRepo, string userBranch, string observationPath, bool guardrailPasses)
    {
        Directory.CreateDirectory(taskDir);
        File.WriteAllText(Path.Combine(taskDir, "task.json"),
            $$"""{ "description": "write {{file}} and record the user's branch tip", "writeScope": ["{{file}}"] }""");

        string action = Ps
            ? """
              Set-Content -NoNewline -Path (Join-Path $env:GUARDRAILS_WORKSPACE "__FILE__") -Value 'x'
              $tip = git -C "__REPO__" rev-parse "refs/heads/__BRANCH__"
              if ($LASTEXITCODE -ne 0) { Write-Output "could not read the tip of '__BRANCH__' in __REPO__"; exit 1 }
              Set-Content -NoNewline -Path "__OBS__" -Value ($tip.Trim())
              exit 0
              """
            : """
              #!/usr/bin/env bash
              printf 'x' > "$GUARDRAILS_WORKSPACE/__FILE__"
              tip=$(git -C "__REPO__" rev-parse "refs/heads/__BRANCH__") || { echo "could not read the tip of '__BRANCH__' in __REPO__"; exit 1; }
              printf '%s' "$tip" > "__OBS__"
              exit 0
              """;
        action = action.Replace("__FILE__", file).Replace("__REPO__", userRepo)
            .Replace("__BRANCH__", userBranch).Replace("__OBS__", observationPath);
        WriteExecutable(Path.Combine(taskDir, Script("action")), action);

        string guardrailPath = Path.Combine(taskDir, "guardrails", Script("01-check"));
        if (guardrailPasses)
        {
            WriteFileExistsGate(guardrailPath, file);
        }
        else
        {
            WriteAlwaysFailGate(guardrailPath, "this wave-2 task fails by design, so the final wave never reaches its barrier");
        }
    }

    private static void WriteFileExistsGate(string path, string file)
    {
        string body = Ps
            ? $"# catches: {file} not present\n" +
              $"if (-not (Test-Path (Join-Path $env:GUARDRAILS_WORKSPACE \"{file}\"))) {{ Write-Output \"{file} is missing from the workspace\"; exit 1 }}\nexit 0\n"
            : $"#!/usr/bin/env bash\n# catches: {file} not present\n" +
              $"[ -f \"$GUARDRAILS_WORKSPACE/{file}\" ] || {{ echo \"{file} is missing from the workspace\"; exit 1; }}\nexit 0\n";
        WriteExecutable(path, body);
    }

    private static void WriteAlwaysFailGate(string path, string reason = "fails by design")
    {
        string body = Ps
            ? $"# catches: {reason}\nWrite-Output \"{reason}\"\nexit 1\n"
            : $"#!/usr/bin/env bash\n# catches: {reason}\necho \"{reason}\"\nexit 1\n";
        WriteExecutable(path, body);
    }

    private static void WriteExecutable(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // run.json reads with actionable failures (a missing key names its parent and prints it).
    // ─────────────────────────────────────────────────────────────────────────────────────────

    private static class Json
    {
        public static JsonElement Prop(JsonElement parent, string name, string where)
        {
            JsonElement value = default;
            bool found = parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out value);
            Assert.True(found, $"run.json: expected '{where}.{name}' to exist; '{where}' is:\n{parent}");
            return value;
        }

        public static string String(JsonElement parent, string name, string where)
        {
            JsonElement value = Prop(parent, name, where);
            Assert.True(value.ValueKind == JsonValueKind.String, $"run.json: '{where}.{name}' is not a string:\n{parent}");
            return value.GetString()!;
        }

        public static bool Bool(JsonElement parent, string name, string where)
        {
            JsonElement value = Prop(parent, name, where);
            Assert.True(value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                $"run.json: '{where}.{name}' is not a boolean:\n{parent}");
            return value.GetBoolean();
        }

        public static string[] Strings(JsonElement parent, string name, string where)
        {
            JsonElement value = Prop(parent, name, where);
            Assert.True(value.ValueKind == JsonValueKind.Array, $"run.json: '{where}.{name}' is not an array:\n{parent}");
            return [.. value.EnumerateArray().Select(e => e.GetString() ?? "")];
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // TempGitRepo — the house fixture (WaveBarrierDeliveryTests / PartialDeliveryReportTests), plus the
    // queries this class needs: a revision's file, ancestry, the branch reflog, and `git branch --no-merged`.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    public sealed class TempGitRepo : IDisposable
    {
        public string Root { get; }
        public string RepoPath { get; }

        public TempGitRepo()
        {
            Root = Path.Combine(Path.GetTempPath(), "gr-cliwd-" + Guid.NewGuid().ToString("N"));
            RepoPath = Path.Combine(Root, "repo");
            Directory.CreateDirectory(RepoPath);

            Git("init");
            Git("config", "user.email", "test@guardrails.local");
            Git("config", "user.name", "Guardrails Test");
            // The branch-move count reads this branch's reflog: pin reflogs on rather than trusting machine config.
            Git("config", "core.logAllRefUpdates", "true");
            File.WriteAllText(Path.Combine(RepoPath, "README.md"), "# run-command wave-delivery proof\n");
            Git("add", ".");
            Git("commit", "-m", "Initial commit");
        }

        public string CurrentBranch() => Git("rev-parse", "--abbrev-ref", "HEAD").Trim();

        public string TipOf(string branch) => Git("rev-parse", "refs/heads/" + branch).Trim();

        public bool HasFile(string revision, string relativePath) =>
            TryGit("cat-file", "-e", $"{revision}:{relativePath}") == 0;

        public bool IsAncestor(string ancestor, string descendant) =>
            TryGit("merge-base", "--is-ancestor", ancestor, descendant) == 0;

        /// <summary>
        /// One line per reflog ENTRY of <paramref name="branch"/>: <c>%gd</c> (the entry's own selector) leads every
        /// line, so an entry whose subject is empty still counts — counting subjects dropped those.
        /// </summary>
        public IReadOnlyList<string> BranchReflog(string branch) =>
            Lines(Git("reflog", "show", "--format=%gd %gs", "refs/heads/" + branch));

        /// <summary>Tracked changes in the user's checkout; the fixture's plan folder is untracked, so it is excluded.</summary>
        public string TrackedStatus() => Git("status", "--porcelain", "--untracked-files=no");

        /// <summary><c>git switch</c> in the user's checkout, e.g. <c>-c spike</c> or <c>--detach</c>.</summary>
        public void Switch(params string[] args) => _ = Git(["switch", .. args]);

        public IReadOnlyList<string> BranchesNotMergedInto(string branch) =>
            Lines(Git("branch", "--format=%(refname:short)", "--no-merged", branch));

        private static IReadOnlyList<string> Lines(string text) =>
            [.. text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0)];

        private string Git(params string[] args)
        {
            (string stdout, string stderr, int exit) = RunGit(args);
            if (exit != 0)
            {
                throw new InvalidOperationException(
                    $"git {string.Join(" ", args)} (in {RepoPath}) exited {exit}: {stderr.Trim()}");
            }

            return stdout;
        }

        private int TryGit(params string[] args) => RunGit(args).ExitCode;

        private (string Stdout, string Stderr, int ExitCode) RunGit(string[] args)
        {
            var psi = new ProcessStartInfo("git")
            {
                WorkingDirectory = RepoPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            foreach (string arg in args) psi.ArgumentList.Add(arg);
            using Process proc = Process.Start(psi)!;
            string stdout = proc.StandardOutput.ReadToEnd();
            string stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit();
            return (stdout, stderr, proc.ExitCode);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root))
                {
                    foreach (string f in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
                    {
                        File.SetAttributes(f, FileAttributes.Normal);
                    }

                    Directory.Delete(Root, recursive: true);
                }
            }
            catch
            {
                // best-effort teardown
            }
        }
    }
}
