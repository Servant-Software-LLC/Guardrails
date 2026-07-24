using Guardrails.Core.Journal;
using Guardrails.Core.Model;

namespace Guardrails.Core.Tests;

/// <summary>
/// <see cref="PlanDefinitionHash"/> coverage of a WAVED plan's gate folders (SSOT §7.3 step 5 / §14, issue
/// #386). The whole-plan definition hash keys the <c>/guardrails-review</c> marker (§13), so it must cover
/// every guardrail/preflight/action BODY a review pass scrutinizes. For a WAVED plan the gates live at
/// <c>&lt;plan&gt;/&lt;wave&gt;/guardrails/**</c> (EXIT) and <c>&lt;plan&gt;/&lt;wave&gt;/preflights/**</c>
/// (ENTRY) — which match NEITHER the plan-root gate folders (steps 3–4) NOR any task's file set (step 2).
/// Before the #386 fix a post-review edit to a wave gate escaped the hash and the marker kept vouching; for a
/// waved plan whose gates are ALL wave-level that is EVERY gate. These tests pin that a wave gate edit now
/// re-hashes, that the task-level controls still move the hash, and that a flat plan is unaffected.
/// </summary>
public sealed class PlanDefinitionHashWaveTests
{
    private static PlanDefinition Load(WavePlanBuilder plan)
    {
        Loading.PlanLoadResult result = plan.Load();
        Assert.False(result.HasErrors, string.Join("\n", result.Diagnostics.Select(d => $"{d.Code}: {d.Message}")));
        return result.Plan!;
    }

    [Fact]
    public void EditingAWaveGuardrailBody_ChangesTheHash()
    {
        // THE #386 BUG: weaken a wave EXIT gate to `exit 0`. Before the fix this did NOT move the hash.
        using var plan = new WavePlanBuilder()
            .Task("wave-01-scaffold", "01-init")
            .WaveGuardrail("wave-01-scaffold", "01-exit.sh", "make build || exit 1\n");

        PlanDefinition loaded = Load(plan);
        string before = PlanDefinitionHash.Compute(loaded);

        File.WriteAllText(
            Path.Combine(plan.PlanDir, "wave-01-scaffold", "guardrails", "01-exit.sh"),
            "# catches: a wrong implementation\nexit 0\n");

        Assert.NotEqual(before, PlanDefinitionHash.Compute(loaded));
    }

    [Fact]
    public void EditingAWavePreflightBody_ChangesTheHash()
    {
        // THE #386 BUG (entry gate): break a wave ENTRY preflight. Before the fix this did NOT move the hash.
        using var plan = new WavePlanBuilder()
            .Task("wave-01-scaffold", "01-init")
            .WavePreflight("wave-01-scaffold", "01-entry.sh", "test -f dep.txt || exit 1\n");

        PlanDefinition loaded = Load(plan);
        string before = PlanDefinitionHash.Compute(loaded);

        File.WriteAllText(
            Path.Combine(plan.PlanDir, "wave-01-scaffold", "preflights", "01-entry.sh"),
            "# catches: a missing dependency\nexit 0\n");

        Assert.NotEqual(before, PlanDefinitionHash.Compute(loaded));
    }

    [Fact]
    public void AddingAWaveGateFileToAReviewedWave_ChangesTheHash()
    {
        // A wave gate folder that did not exist at review time gets a check added afterward: the hash must
        // move (an added gate file is authored behavior a review pass would scrutinize).
        using var plan = new WavePlanBuilder().Task("wave-01-scaffold", "01-init");

        PlanDefinition loaded = Load(plan);
        string before = PlanDefinitionHash.Compute(loaded);

        string dir = Path.Combine(plan.PlanDir, "wave-01-scaffold", "guardrails");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "01-exit.sh"), "# catches: a wrong impl\nexit 1\n");

        Assert.NotEqual(before, PlanDefinitionHash.Compute(loaded));
    }

    [Fact]
    public void EditingAWaveTaskGuardrailBody_ChangesTheHash_Control()
    {
        // CONTROL: task-level coverage ALREADY worked (step 2 folds every wave task's file set). Must stay.
        using var plan = new WavePlanBuilder().Task("wave-01-scaffold", "01-init");

        PlanDefinition loaded = Load(plan);
        string before = PlanDefinitionHash.Compute(loaded);

        File.WriteAllText(
            Path.Combine(plan.PlanDir, "wave-01-scaffold", "tasks", "01-init", "guardrails", "01-ok.sh"),
            "#!/bin/sh\nexit 1\n");

        Assert.NotEqual(before, PlanDefinitionHash.Compute(loaded));
    }

    [Fact]
    public void EditingAWaveTaskJson_ChangesTheHash_Control()
    {
        // CONTROL: a wave task's task.json is folded via step 2. Must stay.
        using var plan = new WavePlanBuilder().Task("wave-01-scaffold", "01-init");

        PlanDefinition loaded = Load(plan);
        string before = PlanDefinitionHash.Compute(loaded);

        File.WriteAllText(
            Path.Combine(plan.PlanDir, "wave-01-scaffold", "tasks", "01-init", "task.json"),
            """{ "description": "edited after review" }""");

        Assert.NotEqual(before, PlanDefinitionHash.Compute(loaded));
    }

    [Fact]
    public void WaveGateEditsAcrossTwoWaves_EachMoveTheHash()
    {
        // Both waves' gates are covered (the loop iterates every wave), not just the first.
        using var plan = new WavePlanBuilder()
            .Task("wave-01-scaffold", "01-init")
            .WaveGuardrail("wave-01-scaffold", "01-exit.sh", "exit 0\n")
            .Task("wave-02-provision", "01-provision")
            .WaveGuardrail("wave-02-provision", "01-exit.sh", "make test || exit 1\n");

        PlanDefinition loaded = Load(plan);
        string before = PlanDefinitionHash.Compute(loaded);

        // Edit wave-02's exit gate.
        File.WriteAllText(
            Path.Combine(plan.PlanDir, "wave-02-provision", "guardrails", "01-exit.sh"),
            "# catches: a wrong impl\nexit 0\n");
        string afterWave2 = PlanDefinitionHash.Compute(loaded);
        Assert.NotEqual(before, afterWave2);

        // Edit wave-01's exit gate — a further, distinct change.
        File.WriteAllText(
            Path.Combine(plan.PlanDir, "wave-01-scaffold", "guardrails", "01-exit.sh"),
            "# catches: a wrong impl\nmake build || exit 1\n");
        Assert.NotEqual(afterWave2, PlanDefinitionHash.Compute(loaded));
    }

    [Fact]
    public void Compute_IsDeterministic_ForAWavedPlan()
    {
        using var plan = new WavePlanBuilder()
            .Task("wave-01-scaffold", "01-init")
            .WaveGuardrail("wave-01-scaffold", "01-exit.sh", "exit 0\n")
            .WavePreflight("wave-01-scaffold", "01-entry.sh", "exit 0\n");

        PlanDefinition loaded = Load(plan);
        Assert.Equal(PlanDefinitionHash.Compute(loaded), PlanDefinitionHash.Compute(loaded));
    }

    [Fact]
    public void FlatPlan_HasNoWaves_AndTheWaveLoopIsANoOp_NoRegression()
    {
        // A FLAT plan has empty Waves, so the #386 wave loop iterates zero times and contributes nothing —
        // its hash is byte-identical to before this fix. Deterministic recompute proves the no-op path.
        using var plan = new WavePlanBuilder().FlatTask("01-task");

        PlanDefinition loaded = Load(plan);
        Assert.Empty(loaded.Waves);
        Assert.Equal(PlanDefinitionHash.Compute(loaded), PlanDefinitionHash.Compute(loaded));
    }

    [Fact]
    public void FlatPlan_EditingATaskGuardrail_StillChangesTheHash_NoRegression()
    {
        // Guards the AppendFolder signature refactor: the plan-root/task paths still move the hash on a flat
        // plan (the change to AppendFolder must not have disturbed the pre-existing flat behavior).
        using var plan = new WavePlanBuilder().FlatTask("01-task");

        PlanDefinition loaded = Load(plan);
        string before = PlanDefinitionHash.Compute(loaded);

        File.WriteAllText(
            Path.Combine(plan.PlanDir, "tasks", "01-task", "guardrails", "01-ok.sh"),
            "#!/bin/sh\nexit 1\n");

        Assert.NotEqual(before, PlanDefinitionHash.Compute(loaded));
    }
}
