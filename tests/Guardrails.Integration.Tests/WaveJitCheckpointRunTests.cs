using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using Guardrails.Core.Loading;
using Guardrails.Core.Model;

namespace Guardrails.Integration.Tests;

/// <summary>
/// #360 Phase 0: the between-wave JIT checkpoint (SSOT §14.4/§14.10) on a REAL script run. A waved plan whose
/// next wave is an empty (unauthored) stub honest-halts with <see cref="WaveHaltKind.NextWaveUnauthored"/>;
/// when the stub carries an OPTIONAL human-authored <c>brief.md</c> the halt message NAMES it (auto-breakdown
/// eligible in a future phase), and either way the checkpoint records a <c>boundary:"wave"</c>,
/// <c>decision:"halted"</c> <c>decisions[]</c> entry — the gap Phase 0 closes. Serial mode (maxParallelism 1,
/// no git): <c>integ</c> is null, so this exercises the brief naming + the checkpoint decision on a genuinely
/// executed wave-1, without the worktree-path detail.
/// </summary>
public sealed class WaveJitCheckpointRunTests
{
    private static async Task<RunReport> RunAsync(string planDir)
    {
        PlanLoadResult load = new PlanLoader().Load(planDir);
        Assert.NotNull(load.Plan);
        Assert.False(load.HasErrors, string.Join("\n", load.Diagnostics));

        Scheduler scheduler = SchedulerFactory.Create(
            load.Plan!, new ProcessRunner(), new PathExecutableProbe(), IRunObserver.Null);
        return await scheduler.RunAsync(load.Plan!, TestContext.Current.CancellationToken);
    }

    private static IReadOnlyList<DecisionEntry> ReloadDecisions(string planDir)
    {
        RunJournal reloaded = RunJournal.LoadOrCreate(new PlanLoader().Load(planDir).Plan!);
        return reloaded.Document.Decisions ?? [];
    }

    [Fact]
    public async Task Wave2StubWithBrief_HaltsUnauthored_NamesBrief_RecordsWaveHaltedDecision()
    {
        using var plan = new ScriptPlanBuilder();
        plan.AddWave("wave-01-scaffold").AddTask("01-config"); // wave-1 runs to green
        plan.AddWave("wave-02-build");                          // JIT stub: empty tasks/, no tasks authored

        // The opt-in signal: an OPTIONAL human-authored brief.md in the stub wave folder.
        File.WriteAllText(
            Path.Combine(plan.PlanDir, "wave-02-build", "brief.md"),
            "# wave-02-build\nBuild the compiled artifact from wave-01's config.\n");

        RunReport report = await RunAsync(plan.PlanDir);

        // Halted at the JIT checkpoint — not a wholly-green run.
        Assert.False(report.AllSucceeded);
        Assert.NotNull(report.WaveHalt);
        Assert.Equal(WaveHaltKind.NextWaveUnauthored, report.WaveHalt!.Kind);
        Assert.Equal("wave-02-build", report.WaveHalt.WaveDir);

        // The halt message NAMES the brief + the future autonomyPolicy path.
        Assert.Contains("wave-02-build/brief.md", report.WaveHalt.Detail);
        Assert.Contains("is present", report.WaveHalt.Detail);
        Assert.Contains("autonomyPolicy", report.WaveHalt.Detail);

        // The checkpoint recorded a boundary:"wave", decision:"halted" decisions[] entry (the #360 gap).
        DecisionEntry decision = Assert.Single(ReloadDecisions(plan.PlanDir));
        Assert.Equal("wave", decision.Boundary);
        Assert.Equal("halted", decision.Decision);
        Assert.Equal("wave-02-build", decision.Subject);
        Assert.Contains("wave-02-build/brief.md", decision.Detail);
    }

    [Fact]
    public async Task Wave2StubWithoutBrief_HaltsUnauthored_NamesTheConvention_RecordsWaveHaltedDecision()
    {
        using var plan = new ScriptPlanBuilder();
        plan.AddWave("wave-01-scaffold").AddTask("01-config");
        plan.AddWave("wave-02-build"); // JIT stub, NO brief.md

        RunReport report = await RunAsync(plan.PlanDir);

        Assert.False(report.AllSucceeded);
        Assert.NotNull(report.WaveHalt);
        Assert.Equal(WaveHaltKind.NextWaveUnauthored, report.WaveHalt!.Kind);

        // brief ABSENT → the message names the brief.md CONVENTION as the way to enable auto-breakdown.
        Assert.Contains("Create 'wave-02-build/brief.md'", report.WaveHalt.Detail);
        Assert.DoesNotContain("is present", report.WaveHalt.Detail);

        DecisionEntry decision = Assert.Single(ReloadDecisions(plan.PlanDir));
        Assert.Equal("wave", decision.Boundary);
        Assert.Equal("halted", decision.Decision);
        Assert.Equal("wave-02-build", decision.Subject);
    }
}
