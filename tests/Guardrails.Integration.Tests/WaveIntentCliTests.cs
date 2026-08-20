using System.CommandLine;
using Guardrails.Cli;

namespace Guardrails.Integration.Tests;

/// <summary>
/// The <c>intendedWaves</c> reporting line and GR2062, through the REAL production dispatch
/// (<see cref="CommandFactory.BuildRootCommand"/>) — issue #477, doc 19 §3.2.
///
/// <para>#477's explicit floor is that <c>validate</c> and <c>plan</c> can each answer "how many waves was
/// this plan supposed to have?" — a question that had no answer anywhere before the field existed: the run
/// config carried no wave information, <c>diagram.md</c> is regenerated FROM the wave folders so it can
/// never disagree with them, and the charter that settles the count is a sibling of the plan folder with no
/// reference from inside it.</para>
/// </summary>
public sealed class WaveIntentCliTests
{
    private const string Wave1 = "wave-01-scaffold";
    private const string Wave2 = "wave-02-provision";

    private static ScriptPlanBuilder TwoWavePlan(int? intendedWaves)
    {
        var builder = new ScriptPlanBuilder();
        builder.AddWave(Wave1).AddTask("01-init");
        builder.AddWave(Wave2).AddTask("01-provision");

        string intent = intendedWaves is { } n ? $"\n  \"intendedWaves\": {n}," : "";
        File.WriteAllText(Path.Combine(builder.PlanDir, "guardrails.json"),
            $$"""
            {
              "version": 1,
              "guardrailMode": "failFast",
              "workspace": ".",
              "defaultRetries": 0,{{intent}}
              "maxParallelism": 1
            }
            """);
        return builder;
    }

    [Fact]
    public async Task Validate_OnAWavedPlan_ReportsIntendedVersusDeclared_AndWarnsGr2062WhenAWaveIsGone()
    {
        using ScriptPlanBuilder plan = TwoWavePlan(intendedWaves: 3);

        (int exit, string output, _) = await InvokeAsync("validate", plan.PlanDir);

        // A warning never fails validate — the value is that a lost wave becomes NAMEABLE, not enforcement.
        Assert.Equal(ExitCodes.Success, exit);
        Assert.Contains("Waves: 3 intended, 2 declared (1 not yet created)", output, StringComparison.Ordinal);
        Assert.Contains("GR2062", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Plan_OnAWavedPlan_ReportsTheSameLine_FromTheSameRenderer()
    {
        using ScriptPlanBuilder plan = TwoWavePlan(intendedWaves: 3);

        (int exit, string output, _) = await InvokeAsync("plan", plan.PlanDir);

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Contains("Waves: 3 intended, 2 declared (1 not yet created)", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Validate_WithNoIntentRecorded_SaysSo_AndEmitsNoGr2062()
    {
        using ScriptPlanBuilder plan = TwoWavePlan(intendedWaves: null);

        (int exit, string output, _) = await InvokeAsync("validate", plan.PlanDir);

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Contains("Waves: 2 declared (intent not recorded)", output, StringComparison.Ordinal);
        Assert.DoesNotContain("GR2062", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Validate_OnAFlatPlan_PrintsNoWaveLine()
    {
        using var plan = new ScriptPlanBuilder();
        plan.AddTask("01-init");

        (int exit, string output, _) = await InvokeAsync("validate", plan.PlanDir);

        Assert.Equal(ExitCodes.Success, exit);
        Assert.DoesNotContain("Waves:", output, StringComparison.Ordinal);
    }

    private static async Task<(int ExitCode, string Output, string Error)> InvokeAsync(params string[] args)
    {
        var io = new StringConsoleIo();
        RootCommand root = CommandFactory.BuildRootCommand(io);
        int exit = await root.Parse(args).InvokeAsync();
        return (exit, io.OutText, io.ErrorText);
    }
}
