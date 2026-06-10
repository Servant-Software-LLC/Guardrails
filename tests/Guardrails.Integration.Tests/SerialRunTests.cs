using Guardrails.Core.Execution;
using Guardrails.Core.Loading;
using Guardrails.Core.Model;

namespace Guardrails.Integration.Tests;

/// <summary>
/// End-to-end serial-run tests against real, freshly generated script plans. These
/// genuinely spawn child processes (pwsh/powershell on Windows, bash elsewhere).
/// </summary>
public sealed class SerialRunTests
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

    [Fact]
    public async Task AllGreen_EveryTaskSucceeds()
    {
        using var plan = new ScriptPlanBuilder()
            .AddTask("01-first")
            .AddTask("02-second", dependsOn: "01-first");

        RunReport report = await RunAsync(plan.PlanDir);

        Assert.True(report.AllSucceeded, Summarize(report));
        Assert.All(report.Tasks, t => Assert.Equal(TaskOutcome.Succeeded, t.Outcome));
    }

    [Fact]
    public async Task FailingGuardrail_FailsTaskAndBlocksDependent()
    {
        using var plan = new ScriptPlanBuilder()
            .AddTask("01-first", guardrailPasses: false)
            .AddTask("02-second", dependsOn: "01-first");

        RunReport report = await RunAsync(plan.PlanDir);

        TaskResult first = report.Tasks.Single(t => t.TaskId == "01-first");
        TaskResult second = report.Tasks.Single(t => t.TaskId == "02-second");

        Assert.Equal(TaskOutcome.GuardrailFailed, first.Outcome);
        // The dependent never runs — it is reported blocked.
        Assert.Equal(TaskOutcome.Blocked, second.Outcome);
        Assert.False(report.AllSucceeded);
    }

    [Fact]
    public async Task ActionFails_GuardrailsAreSkipped()
    {
        using var plan = new ScriptPlanBuilder()
            .AddTask("01-first", actionSucceeds: false);

        RunReport report = await RunAsync(plan.PlanDir);

        TaskResult first = report.Tasks.Single(t => t.TaskId == "01-first");
        Assert.Equal(TaskOutcome.ActionFailed, first.Outcome);
        // No guardrail ran because the action failed.
        Assert.Empty(first.Guardrails);
    }

    [Fact]
    public async Task EnvironmentContract_VariablesAreInjected()
    {
        // A task whose guardrail asserts the §5.1 env vars are present proves injection.
        using var plan = new EnvAssertingPlan();

        RunReport report = await RunAsync(plan.PlanDir);

        TaskResult task = Assert.Single(report.Tasks);
        Assert.Equal(TaskOutcome.Succeeded, task.Outcome);
    }

    private static string Summarize(RunReport report) =>
        string.Join("\n", report.Tasks.Select(t => $"{t.TaskId}: {t.Outcome} ({t.Summary})"));
}
