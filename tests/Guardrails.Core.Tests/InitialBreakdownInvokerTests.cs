using Guardrails.Core.Execution;

namespace Guardrails.Core.Tests;

/// <summary>
/// Issue #498 — <c>guardrails breakdown</c>'s composer. The prompt is the ENTIRE contract with the
/// authoring agent (there is no human in this loop and no second chance to clarify), so these pin the
/// clauses whose absence would be invisible until a real breakdown had already gone wrong: the target
/// paths, the DRAFT/never-mark-reviewed instruction, and the honest degradation when the bundled skill
/// cannot be read.
/// </summary>
public sealed class InitialBreakdownInvokerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "gr-initial-breakdown-" + Guid.NewGuid().ToString("N")[..8]);

    public InitialBreakdownInvokerTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    private string WritePlan(string content)
    {
        string path = Path.Combine(_root, "plan.md");
        File.WriteAllText(path, content);
        return path;
    }

    private BreakdownInvocationPlan Prepare(string planText, out string outFolder)
    {
        string plan = WritePlan(planText);
        outFolder = Path.Combine(_root, "plan");
        return InitialBreakdownInvoker.PrepareInvocation(plan, outFolder, Path.Combine(_root, "logs"));
    }

    [Fact]
    public void Prepare_InlinesThePlanText_SoTheTeeIsSelfContained()
    {
        // The path alone would be enough for a session that can read the file — but composed-prompt.md is
        // the only durable record of what the session was ASKED to do, and a record that says "see this
        // path" is worthless the moment the file changes.
        BreakdownInvocationPlan p = Prepare("# Tiny\n\n- Add a greeting file.\n", out _);

        Assert.Contains("Add a greeting file.", p.Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Prepare_NamesBothTheSourcePlanAndTheOutputFolder()
    {
        BreakdownInvocationPlan p = Prepare("# Tiny\n\n- One item.\n", out string outFolder);

        Assert.Contains(outFolder, p.Prompt, StringComparison.Ordinal);
        Assert.Contains("plan.md", p.Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Prepare_ForbidsMarkingReviewed_TheInvariantThisVerbMustNotErode()
    {
        // The whole point of a CLI door is that it authors without a human. That makes it exactly the door
        // through which the review gate could be silently walked past, so the prompt has to say so — and a
        // future edit that drops the sentence must fail here rather than in a run nobody audits.
        BreakdownInvocationPlan p = Prepare("# Tiny\n\n- One item.\n", out _);

        Assert.Contains("DRAFT", p.Prompt, StringComparison.Ordinal);
        Assert.Contains("mark-reviewed", p.Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Prepare_RequiresSelfValidation_BecauseTheCallerGatesOnIt()
    {
        BreakdownInvocationPlan p = Prepare("# Tiny\n\n- One item.\n", out _);

        Assert.Contains("guardrails validate", p.Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Prepare_TeesTheComposedPrompt_AndReportsItsRealSize()
    {
        BreakdownInvocationPlan p = Prepare("# Tiny\n\n- One item.\n", out _);

        Assert.True(File.Exists(p.ComposedPromptPath), $"the composed prompt was not tee'd to {p.ComposedPromptPath}");
        Assert.Equal(p.Prompt, File.ReadAllText(p.ComposedPromptPath));
        Assert.True(p.ComposedPromptBytes > 0);
    }

    [Fact]
    public void Prepare_ScalesTheTurnBudgetWithThePlanSize_UsingTheSameRuleAsTheWavePath()
    {
        // Sharing WaveBreakdownInvoker.ComputeMaxTurns is deliberate (#385's budget lesson applies to both
        // doors); this pins that the initial path actually consults the plan rather than taking the base.
        BreakdownInvocationPlan small = Prepare("# Tiny\n\n- One item.\n", out _);

        var bigPlan = new System.Text.StringBuilder("# Big\n\n");
        for (int i = 0; i < 30; i++) { bigPlan.Append("- Item ").Append(i).Append('\n'); }
        string bigPath = Path.Combine(_root, "big.md");
        File.WriteAllText(bigPath, bigPlan.ToString());
        BreakdownInvocationPlan large = InitialBreakdownInvoker.PrepareInvocation(
            bigPath, Path.Combine(_root, "big"), Path.Combine(_root, "logs-big"));

        Assert.True(large.MaxTurns > small.MaxTurns,
            $"a larger plan must get a larger budget; {large.MaxTurns} !> {small.MaxTurns}");

        // The one-bullet plan scores ONE work-item signal, so it is one increment ABOVE the base — not the
        // base itself. Pinning it that way is the point: an implementation that ignored the plan and always
        // returned the base would pass a `> base` check on the large plan alone.
        Assert.Equal(WaveBreakdownInvoker.ComputeMaxTurns(1), small.MaxTurns);
        Assert.True(small.MaxTurns > WaveBreakdownInvoker.ComputeMaxTurns(0),
            "a plan with a work item must score above the zero-signal base");
    }

    [Fact]
    public void Prepare_UnreadablePlan_StillComposes_RatherThanThrowing()
    {
        // A plan that cannot be read is the caller's problem to report, not a reason for the composer to
        // throw halfway through creating the log directory. It degrades to the base budget.
        string missing = Path.Combine(_root, "does-not-exist.md");

        BreakdownInvocationPlan p = InitialBreakdownInvoker.PrepareInvocation(
            missing, Path.Combine(_root, "out"), Path.Combine(_root, "logs-missing"));

        Assert.Equal(WaveBreakdownInvoker.ComputeMaxTurns(0), p.MaxTurns);
        Assert.Contains("plan-breakdown", p.Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Prepare_AlwaysCarriesThePlanBreakdownDoctrine_InlinedOrNamed()
    {
        // The bundled skill is inlined when readable. When it is NOT (a source build with no skills/ beside
        // the binary), the prompt must still NAME the procedure rather than silently asking for a breakdown
        // against no doctrine at all — an agent given neither would invent its own task shape and the folder
        // would look plausible.
        BreakdownInvocationPlan p = Prepare("# Tiny\n\n- One item.\n", out _);

        Assert.Contains("plan-breakdown skill", p.Prompt, StringComparison.Ordinal);
    }
}
