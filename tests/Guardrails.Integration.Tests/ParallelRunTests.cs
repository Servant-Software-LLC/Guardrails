using Guardrails.Core.Execution;
using Guardrails.Core.Loading;

namespace Guardrails.Integration.Tests;

/// <summary>
/// M4 parallel-execution end-to-end tests with real processes: diamond DAG runs
/// branches concurrently, and <c>exclusive: true</c> tasks never overlap anything.
/// Overlap is proven with start/end timestamp files, not timing guesses.
/// </summary>
public sealed class ParallelRunTests
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

    /// <summary>
    /// An action that records a start mark, holds for ~1.2s, then records an end mark —
    /// long enough that two serialized runs cannot interleave their windows.
    /// </summary>
    private static string MarkingAction(string name) => StatePlanBuilder.UsePowerShell
        ? $"""
           $d = Join-Path $env:GUARDRAILS_PLAN_DIR 'marks'
           New-Item -ItemType Directory -Force $d | Out-Null
           [DateTimeOffset]::UtcNow.UtcTicks | Set-Content (Join-Path $d '{name}.start')
           Start-Sleep -Milliseconds 1200
           [DateTimeOffset]::UtcNow.UtcTicks | Set-Content (Join-Path $d '{name}.end')
           exit 0
           """
        : $"""
           d="$GUARDRAILS_PLAN_DIR/marks"; mkdir -p "$d"
           date +%s%N > "$d/{name}.start"
           sleep 1.2
           date +%s%N > "$d/{name}.end"
           exit 0
           """;

    private static long Mark(string planDir, string file) =>
        long.Parse(File.ReadAllText(Path.Combine(planDir, "marks", file)).Trim());

    private static bool Overlap(string planDir, string a, string b)
    {
        long aStart = Mark(planDir, $"{a}.start"), aEnd = Mark(planDir, $"{a}.end");
        long bStart = Mark(planDir, $"{b}.start"), bEnd = Mark(planDir, $"{b}.end");
        return aStart < bEnd && bStart < aEnd;
    }

    [Fact]
    public async Task Diamond_IndependentBranches_RunConcurrently()
    {
        using var plan = new StatePlanBuilder(maxParallelism: 4)
            .AddTask("01-root")
            .AddTask("02-left", actionBody: MarkingAction("left"), dependsOn: "01-root")
            .AddTask("03-right", actionBody: MarkingAction("right"), dependsOn: "01-root")
            .AddTask("04-join", dependsOn: ["02-left", "03-right"]);

        RunReport report = await RunAsync(plan.PlanDir);

        Assert.True(report.AllSucceeded, string.Join("\n", report.Tasks.Select(t => $"{t.TaskId}:{t.Summary}")));
        Assert.True(Overlap(plan.PlanDir, "left", "right"),
            "independent diamond branches did not overlap — parallelism is not happening");
    }

    [Fact]
    public async Task ExclusiveTask_NeverOverlapsAnotherTask()
    {
        using var plan = new StatePlanBuilder(maxParallelism: 4)
            .AddTask("01-a", actionBody: MarkingAction("a"))
            .AddTask("02-b", actionBody: MarkingAction("b"), exclusive: true)
            .AddTask("03-c", actionBody: MarkingAction("c"));

        RunReport report = await RunAsync(plan.PlanDir);

        Assert.True(report.AllSucceeded);
        Assert.False(Overlap(plan.PlanDir, "b", "a"), "exclusive task overlapped a shared task");
        Assert.False(Overlap(plan.PlanDir, "b", "c"), "exclusive task overlapped a shared task");
    }
}
