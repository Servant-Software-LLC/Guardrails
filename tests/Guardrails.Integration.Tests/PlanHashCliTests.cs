using System.CommandLine;
using Guardrails.Cli;
using Guardrails.Core.Journal;
using Guardrails.Core.Loading;

namespace Guardrails.Integration.Tests;

/// <summary>
/// TDD-red integration tests for the read-only <c>guardrails plan-hash &lt;folder&gt;</c> command
/// (issue #366, doc 16 OD-3 / §12.2) — the affordance the <c>/guardrails-review</c> skill needs to
/// embed the plan hash it reviewed (F2a). The command prints the plan's <c>PlanDefinitionHash</c>
/// (<c>sha256:…</c>).
///
/// <para>Both tests drive the REAL production dispatch — <see cref="CommandFactory.BuildRootCommand"/>,
/// not a hand-assembled <see cref="RootCommand"/> — so a passing test proves the command is WIRED
/// into production (issue #120), not merely that a handler type exists. Against the current stub
/// (whose handler throws <see cref="NotImplementedException"/>) both tests FAIL by design; they turn
/// green when the hash-printing handler is implemented.</para>
/// </summary>
public sealed class PlanHashCliTests
{
    [Fact]
    public async Task PlanHash_PrintsPlanDefinitionHash_OfTheLoadedPlan()
    {
        using var plan = new ScriptPlanBuilder().AddTask("01-first");
        // Oracle: the exact hash the command must print, computed straight from the same loaded plan.
        string expected = ExpectedHash(plan.PlanDir);

        (int exit, string output) = await InvokeAsync("plan-hash", plan.PlanDir);

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Contains(expected, output);
    }

    [Fact]
    public async Task PlanHash_IsDeterministic_AcrossInvocations_OnAnUnchangedPlan()
    {
        using var plan = new ScriptPlanBuilder().AddTask("01-first");

        (int firstExit, string firstOut) = await InvokeAsync("plan-hash", plan.PlanDir);
        (int secondExit, string secondOut) = await InvokeAsync("plan-hash", plan.PlanDir);

        Assert.Equal(ExitCodes.Success, firstExit);
        Assert.Equal(ExitCodes.Success, secondExit);
        // Same unchanged plan → the SAME sha256:… hash printed both times.
        Assert.Equal(ExtractHash(firstOut), ExtractHash(secondOut));
    }

    /// <summary>
    /// Drive the command through the REAL production factory (not a bespoke <see cref="RootCommand"/>)
    /// so the test exercises the same dispatch a user hits — proving <c>plan-hash</c> is registered.
    /// </summary>
    private static async Task<(int ExitCode, string Output)> InvokeAsync(params string[] args)
    {
        var io = new StringConsoleIo();
        RootCommand root = CommandFactory.BuildRootCommand(io);
        int exit = await root.Parse(args).InvokeAsync();
        return (exit, io.OutText);
    }

    /// <summary>The oracle hash: load the plan the way the harness does and compute its definition hash.</summary>
    private static string ExpectedHash(string planDir)
    {
        PlanLoadResult load = new PlanLoader().Load(planDir);
        Assert.False(load.HasErrors, string.Join("\n", load.Diagnostics));
        return PlanDefinitionHash.Compute(load.Plan!);
    }

    /// <summary>Pull the <c>sha256:&lt;hex&gt;</c> token out of command output (no regex — analyzer-clean).</summary>
    private static string ExtractHash(string output)
    {
        int start = output.IndexOf("sha256:", StringComparison.Ordinal);
        Assert.True(start >= 0, $"expected a sha256:… hash in the output but found none:\n{output}");

        int end = start + "sha256:".Length;
        while (end < output.Length && Uri.IsHexDigit(output[end]))
        {
            end++;
        }

        return output[start..end];
    }
}
