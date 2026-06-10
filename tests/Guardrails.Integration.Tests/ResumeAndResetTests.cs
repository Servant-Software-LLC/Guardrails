using System.Text.Json.Nodes;
using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using Guardrails.Core.Loading;
using Guardrails.Core.State;
using JournalTaskStatus = Guardrails.Core.Journal.TaskStatus;

namespace Guardrails.Integration.Tests;

/// <summary>
/// M3 resume and reset end-to-end tests with real script processes. Proves the headline
/// M3 exit criterion: a failed run, fixed and re-run, skips already-succeeded tasks
/// (verified by a counter file the action appends to) and finishes green.
/// </summary>
public sealed class ResumeAndResetTests
{
    private static async Task<RunReport> RunAsync(string planDir)
    {
        PlanLoadResult load = new PlanLoader().Load(planDir);
        Assert.NotNull(load.Plan);
        Assert.False(load.HasErrors, string.Join("\n", load.Diagnostics));

        Scheduler scheduler = SchedulerFactory.Create(
            load.Plan!, new ProcessRunner(), new PathExecutableProbe(), IRunObserver.Null);
        return await scheduler.RunAsync(load.Plan!);
    }

    /// <summary>An action body that appends a line to a counter file under the plan dir, then succeeds.</summary>
    private static string CountingAction(string counterName) => StatePlanBuilder.UsePowerShell
        ? $"""Add-Content -Path (Join-Path $env:GUARDRAILS_PLAN_DIR '{counterName}') -Value 'ran'; exit 0"""
        : $"""echo ran >> "$GUARDRAILS_PLAN_DIR/{counterName}"; exit 0""";

    private static int RunCount(string planDir, string counterName)
    {
        string path = Path.Combine(planDir, counterName);
        return File.Exists(path)
            ? File.ReadAllLines(path).Count(l => l.Trim().Length > 0)
            : 0;
    }

    [Fact]
    public async Task Resume_SkipsSucceededTask_AndFinishesGreenAfterFix()
    {
        using var plan = new StatePlanBuilder()
            .AddTask("01-first", actionBody: CountingAction("t1.count"))
            // Task 2's guardrail fails on the first run.
            .AddTask("02-second",
                actionBody: CountingAction("t2.count"),
                guardrailBody: StatePlanBuilder.Fail("second guardrail not satisfied yet"),
                dependsOn: "01-first");

        // --- run 1: t1 succeeds, t2's guardrail fails -------------------------------
        RunReport first = await RunAsync(plan.PlanDir);
        Assert.False(first.AllSucceeded);
        Assert.Equal(TaskOutcome.Succeeded, first.Tasks.Single(t => t.TaskId == "01-first").Outcome);
        Assert.Equal(TaskOutcome.GuardrailFailed, first.Tasks.Single(t => t.TaskId == "02-second").Outcome);

        JournalDocument afterFirst = JournalReader.Read(RunJournal.PathFor(plan.PlanDir));
        Assert.Equal(JournalTaskStatus.Succeeded, afterFirst.Tasks["01-first"].Status);
        // M4: budget exhaustion (0 retries here) journals needs-human, the terminal failure state.
        Assert.Equal(JournalTaskStatus.NeedsHuman, afterFirst.Tasks["02-second"].Status);

        Assert.Equal(1, RunCount(plan.PlanDir, "t1.count"));
        Assert.Equal(1, RunCount(plan.PlanDir, "t2.count"));

        // --- fix the guardrail ------------------------------------------------------
        plan.SetGuardrail("02-second", StatePlanBuilder.Succeed());

        // --- run 2: t1 SKIPPED (action does NOT re-run), t2 now succeeds ------------
        RunReport second = await RunAsync(plan.PlanDir);
        Assert.True(second.AllSucceeded, string.Join("\n", second.Tasks.Select(t => $"{t.TaskId}:{t.Outcome}")));
        Assert.Equal(TaskOutcome.Skipped, second.Tasks.Single(t => t.TaskId == "01-first").Outcome);
        Assert.Equal(TaskOutcome.Succeeded, second.Tasks.Single(t => t.TaskId == "02-second").Outcome);

        // The decisive assertion: t1's action ran exactly ONCE across both runs.
        Assert.Equal(1, RunCount(plan.PlanDir, "t1.count"));
        // t2 re-ran on the second pass.
        Assert.Equal(2, RunCount(plan.PlanDir, "t2.count"));
    }

    [Fact]
    public async Task BlockedDependent_RecordsBlockedStatus_WithNoFabricatedAttempt()
    {
        using var plan = new StatePlanBuilder()
            .AddTask("01-first", guardrailBody: StatePlanBuilder.Fail("first fails"))
            .AddTask("02-second", dependsOn: "01-first");

        RunReport report = await RunAsync(plan.PlanDir);

        Assert.Equal(TaskOutcome.GuardrailFailed, report.Tasks.Single(t => t.TaskId == "01-first").Outcome);
        Assert.Equal(TaskOutcome.Blocked, report.Tasks.Single(t => t.TaskId == "02-second").Outcome);

        JournalDocument journal = JournalReader.Read(RunJournal.PathFor(plan.PlanDir));
        Assert.Equal(JournalTaskStatus.Blocked, journal.Tasks["02-second"].Status);
        // A blocked task never ran — no attempt is fabricated.
        Assert.Empty(journal.Tasks["02-second"].Attempts);
    }

    [Fact]
    public async Task ResetTask_ReExecutesOnlyThatTask()
    {
        using var plan = new StatePlanBuilder()
            .AddTask("01-first", actionBody: CountingAction("t1.count"))
            .AddTask("02-second", actionBody: CountingAction("t2.count"), dependsOn: "01-first");

        // Run once: both green, each action ran once.
        Assert.True((await RunAsync(plan.PlanDir)).AllSucceeded);
        Assert.Equal(1, RunCount(plan.PlanDir, "t1.count"));
        Assert.Equal(1, RunCount(plan.PlanDir, "t2.count"));

        // Reset only task 1.
        PlanLoadResult load = new PlanLoader().Load(plan.PlanDir);
        Assert.True(RunReset.Task(load.Plan!, "01-first"));

        // Re-run: task 1 re-executes; task 2 is skipped (still succeeded).
        RunReport second = await RunAsync(plan.PlanDir);
        Assert.True(second.AllSucceeded);
        Assert.Equal(TaskOutcome.Succeeded, second.Tasks.Single(t => t.TaskId == "01-first").Outcome);
        Assert.Equal(TaskOutcome.Skipped, second.Tasks.Single(t => t.TaskId == "02-second").Outcome);

        Assert.Equal(2, RunCount(plan.PlanDir, "t1.count"));   // re-ran
        Assert.Equal(1, RunCount(plan.PlanDir, "t2.count"));   // untouched
    }

    [Fact]
    public async Task Fresh_ReRunsEverything_AndStateEqualsSeedDerivedMerge()
    {
        // Action publishes a fragment so a successful run produces a known merged state.
        string publish = StatePlanBuilder.UsePowerShell
            ? """[System.IO.File]::WriteAllText($env:GUARDRAILS_STATE_OUT, '{ "01-first": { "done": true } }'); exit 0"""
            : """printf '%s' '{ "01-first": { "done": true } }' > "$GUARDRAILS_STATE_OUT"; exit 0""";

        using var plan = new StatePlanBuilder(seedJson: """{ "recipientName": "World" }""")
            .AddTask("01-first", actionBody: publish);

        // First run.
        Assert.True((await RunAsync(plan.PlanDir)).AllSucceeded);
        JsonObject afterFirst = (JsonObject)JsonNode.Parse(File.ReadAllText(plan.StateJsonPath))!;
        Assert.Equal("\"World\"", afterFirst["recipientName"]!.ToJsonString());
        Assert.True(afterFirst.ContainsKey("01-first"));

        // Fresh reset wipes runtime state and re-seeds.
        RunReset.Fresh(plan.PlanDir);
        JsonObject afterFresh = (JsonObject)JsonNode.Parse(File.ReadAllText(plan.StateJsonPath))!;
        Assert.Equal("\"World\"", afterFresh["recipientName"]!.ToJsonString());
        Assert.False(afterFresh.ContainsKey("01-first")); // back to seed only
        Assert.False(File.Exists(RunJournal.PathFor(plan.PlanDir)));

        // Re-run everything: task runs again and state is the seed-derived merge once more.
        RunReport rerun = await RunAsync(plan.PlanDir);
        Assert.True(rerun.AllSucceeded);
        Assert.Equal(TaskOutcome.Succeeded, rerun.Tasks.Single().Outcome); // NOT skipped — fresh slate

        JsonObject afterRerun = (JsonObject)JsonNode.Parse(File.ReadAllText(plan.StateJsonPath))!;
        Assert.Equal("\"World\"", afterRerun["recipientName"]!.ToJsonString());
        Assert.True(((JsonObject)afterRerun["01-first"]!)["done"]!.GetValue<bool>());
    }
}
