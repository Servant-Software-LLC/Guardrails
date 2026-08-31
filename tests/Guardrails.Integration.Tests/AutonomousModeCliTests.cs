using System.CommandLine;
using Guardrails.Cli;
using Guardrails.Cli.Commands;

namespace Guardrails.Integration.Tests;

/// <summary>
/// The autonomous-mode CLI flags <c>--autonomous</c> and <c>--dial &lt;level&gt;</c> on the <c>run</c>
/// command (issue #361, doc 12 §3.4; decided §10 I/N), driven end-to-end through the real
/// <see cref="RunCommand"/> — build a <see cref="RootCommand"/>, <c>Parse(args).InvokeAsync()</c>, and
/// capture a per-invocation <see cref="StringConsoleIo"/> (no <c>Console.SetOut</c>; parallel-safe),
/// exactly like <see cref="ReviewMarkerCliTests"/> / <see cref="DryRunCliTests"/>.
///
/// <para>
/// TDD RED. These are authored BEFORE the flags' RESOLUTION exists — the paired task
/// <c>06-author-tests-autonomous-cli</c> adds only the option STUBS (so the args parse), leaving the
/// resolved-autonomy summary line, the built-in-$20 <c>maxCostUsd</c> default warning, <c>--dial</c>
/// validation, and the GR2040 re-check on the post-flag effective config UNIMPLEMENTED. So every test
/// here FAILS against the stubs and is made green by <c>07-implement-autonomous-cli</c> WITHOUT editing
/// this file. Almost everything uses <c>--dry-run</c> so the plan validates and the flags resolve but the
/// DAG never executes — the effect is observable from the summary line / warnings alone.
/// </para>
/// </summary>
public sealed class AutonomousModeCliTests
{
    private static async Task<(int ExitCode, string Output)> InvokeAsync(params string[] args)
    {
        var io = new StringConsoleIo();
        var root = new RootCommand("test root");
        root.Add(RunCommand.Create(io));
        int exit = await root.Parse(args).InvokeAsync();
        return (exit, io.OutText);
    }

    // ── --autonomous defaults + the REQUIRED cost cap (doc 12 §3.4, §10 N) ────────────────────────

    [Fact]
    public async Task Autonomous_NoCostCap_ResolvesHighDial_AndLoudlyWarnsAboutTheDefaultCap()
    {
        // A plain plan: no cost cap in guardrails.json, and no --max-cost-usd flag. --autonomous must
        // resolve the conservative default dial (escalationThreshold=high — best-guess only low/moderate,
        // §10 N) AND, because an unattended run must never run uncapped, LOUDLY warn that it is applying
        // the built-in $20 maxCostUsd default.
        using var plan = new ScriptPlanBuilder().AddTask("01-first");

        (int exit, string output) = await InvokeAsync("run", plan.PlanDir, "--autonomous", "--dry-run");

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Contains("escalationThreshold=high", output);
        // The loud default-cap warning names both the amount and the field it defaulted.
        Assert.Contains("$20", output);
        Assert.Contains("maxCostUsd", output);
    }

    [Fact]
    public async Task Autonomous_WithExplicitCostCap_ResolvesWithoutTheDefaultCapWarning()
    {
        // An effective cost cap IS set (via the flag), so --autonomous must NOT apply the built-in $20
        // default and must NOT emit its loud warning. It still resolves the default high dial.
        using var plan = new ScriptPlanBuilder().AddTask("01-first");

        (int exit, string output) =
            await InvokeAsync("run", plan.PlanDir, "--autonomous", "--max-cost-usd", "50", "--dry-run");

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Contains("escalationThreshold=high", output); // resolution still happens
        Assert.DoesNotContain("$20", output);                // but the built-in-default warning does not
    }

    // ── --dial overrides the run-wide threshold (doc 12 §3.3/§3.4) ────────────────────────────────

    [Fact]
    public async Task Autonomous_DialCritical_ResolvesCriticalThreshold()
    {
        // --dial overrides the run-wide escalationThreshold. Reaching fully-autonomous (critical) is an
        // explicit, deliberate act — never a default — so the operator must type it.
        using var plan = new ScriptPlanBuilder().AddTask("01-first");

        (int exit, string output) =
            await InvokeAsync("run", plan.PlanDir, "--autonomous", "--dial", "critical", "--dry-run");

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Contains("escalationThreshold=critical", output);
    }

    [Fact]
    public async Task InvalidDial_ExitsNonZero_NamingTheInvalidValue()
    {
        // An unrecognized --dial value is a usage error: non-zero exit and a message that names the
        // offending token so the operator can fix it. (No --dry-run — dial validation is not dry-run-only.)
        using var plan = new ScriptPlanBuilder().AddTask("01-first");

        (int exit, string output) = await InvokeAsync("run", plan.PlanDir, "--dial", "bogus", "--no-ui", "--no-log-server");

        Assert.NotEqual(ExitCodes.Success, exit);
        Assert.Contains("bogus", output);
    }

    // ── GR2040 on the EFFECTIVE post-flag config (B1) — black-box (doc 12 §5.2) ───────────────────

    [Fact]
    public async Task DialCritical_OverProceedUnreviewedHighConfig_FiresGR2040_AndExitsNonZero()
    {
        // The fixture config is VALID at load: review-gate=proceed-unreviewed + escalationThreshold=high
        // (high is not critical, so the load-time GR2040 never fires). But --dial critical mutates the
        // config AFTER load-time validation, pushing the EFFECTIVE end-state to the forbidden
        // critical + proceed-unreviewed compound — which nothing catches at load. The run must re-check
        // the compound on the post-flag config, exit non-zero, and surface GR2040. Black-box: it drives
        // the command and greps stdout for the code; it never references the validator predicate type
        // (so this task's dependsOn is unchanged).
        using var plan = new ScriptPlanBuilder().AddTask("01-first");
        WriteProceedUnreviewedHighConfig(plan.PlanDir);

        (int exit, string output) = await InvokeAsync("run", plan.PlanDir, "--dial", "critical", "--dry-run");

        Assert.NotEqual(ExitCodes.Success, exit);
        Assert.Contains("GR2040", output);
    }

    /// <summary>
    /// Overwrite a plan's <c>guardrails.json</c> with an autonomy block that is VALID at load but one
    /// dial-turn from the forbidden compound: <c>review-gate: "proceed-unreviewed"</c> +
    /// <c>escalationThreshold: "high"</c>. Because <c>high</c> is not <c>critical</c>, nothing catches it
    /// at load; a later <c>--dial critical</c> pushes the effective end-state to
    /// <c>critical</c> + <c>proceed-unreviewed</c> — the GR2040 compound (doc 12 §5.2).
    /// </summary>
    private static void WriteProceedUnreviewedHighConfig(string planDir) =>
        File.WriteAllText(
            Path.Combine(planDir, "guardrails.json"),
            """
            {
              "version": 1,
              "guardrailMode": "failFast",
              "workspace": ".",
              "defaultRetries": 0,
              "maxParallelism": 1,
              "autonomyPolicy": "auto",
              "autonomy": {
                "escalationThreshold": "high",
                "gateThresholds": {
                  "review-gate": "proceed-unreviewed"
                }
              }
            }
            """);
}
