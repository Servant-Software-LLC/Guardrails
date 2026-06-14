using System.CommandLine;
using Guardrails.Cli;
using Guardrails.Cli.Commands;

namespace Guardrails.Integration.Tests;

/// <summary>
/// Drives the real CLI pipeline for the M3 commands (<c>status</c>, <c>reset</c>, and
/// <c>run --fresh</c>) and the resume exit-code transition (2 → 0). Output goes to a
/// per-invocation <see cref="StringConsoleIo"/> (discarded — these are exit-code tests), so
/// nothing touches the process-global console and the class is parallel-safe.
/// </summary>
[Collection(ConsoleCaptureCollection.Name)]
public sealed class StatusResetCliTests
{
    private static async Task<int> InvokeAsync(params string[] args)
    {
        var io = new StringConsoleIo();
        var root = new RootCommand("test root");
        root.Add(RunCommand.Create(io));
        root.Add(ValidateCommand.Create(io));
        root.Add(StatusCommand.Create(io));
        root.Add(ResetCommand.Create(io));
        return await root.Parse(args).InvokeAsync();
    }

    [Fact]
    public async Task Resume_ExitTwoThenZero_AfterGuardrailFixed()
    {
        using var plan = new StatePlanBuilder()
            .AddTask("01-first")
            .AddTask("02-second",
                guardrailBody: StatePlanBuilder.Fail("not yet"),
                dependsOn: "01-first");

        int firstExit = await InvokeAsync("run", plan.PlanDir);
        Assert.Equal(ExitCodes.TaskFailed, firstExit);

        plan.SetGuardrail("02-second", StatePlanBuilder.Succeed());

        int secondExit = await InvokeAsync("run", plan.PlanDir);
        Assert.Equal(ExitCodes.Success, secondExit);
    }

    [Fact]
    public async Task Status_BeforeAndAfterRun_ExitsZero()
    {
        using var plan = new StatePlanBuilder().AddTask("01-first");

        // Before any run: status still succeeds (reports "not run yet").
        Assert.Equal(ExitCodes.Success, await InvokeAsync("status", plan.PlanDir));

        await InvokeAsync("run", plan.PlanDir);

        // After a run: status reads the journal.
        Assert.Equal(ExitCodes.Success, await InvokeAsync("status", plan.PlanDir));
    }

    [Fact]
    public async Task ResetTask_ThroughCli_Succeeds()
    {
        using var plan = new StatePlanBuilder()
            .AddTask("01-first")
            .AddTask("02-second", dependsOn: "01-first");

        await InvokeAsync("run", plan.PlanDir);

        int resetExit = await InvokeAsync("reset", plan.PlanDir, "01-first");
        Assert.Equal(ExitCodes.Success, resetExit);

        // Re-run is green.
        Assert.Equal(ExitCodes.Success, await InvokeAsync("run", plan.PlanDir));
    }

    [Fact]
    public async Task ResetUnknownTask_ThroughCli_ExitsOne()
    {
        using var plan = new StatePlanBuilder().AddTask("01-first");
        await InvokeAsync("run", plan.PlanDir);

        int exit = await InvokeAsync("reset", plan.PlanDir, "99-does-not-exist");
        Assert.Equal(ExitCodes.HarnessError, exit);
    }

    [Fact]
    public async Task RunFresh_ThroughCli_ExitsZero()
    {
        using var plan = new StatePlanBuilder().AddTask("01-first");
        await InvokeAsync("run", plan.PlanDir);

        int exit = await InvokeAsync("run", plan.PlanDir, "--fresh");
        Assert.Equal(ExitCodes.Success, exit);
    }
}
