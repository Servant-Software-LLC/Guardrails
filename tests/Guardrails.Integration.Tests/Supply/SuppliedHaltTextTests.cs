using Guardrails.Cli.Commands;
using Guardrails.Core.Execution;

namespace Guardrails.Integration.Tests.Supply;

/// <summary>
/// Design 40 §3/§5/§6 — the FIRST of the three surfaces the review's <c>d40-asymmetry</c> answer named
/// ("Name it — in the HALT TEXT, the domain-knowledge skill and the README"). §5a calls all three v1
/// acceptance conditions, not follow-ups, and §3 makes the printed sequence the discoverability
/// mechanism: the halt is the ONE surface the operator is looking at in the moment the feature is
/// needed. Driven directly through <see cref="RunCommand.RenderNeedsHumanSections"/>, the same PURE seam
/// <c>NeedsHumanKindRenderingTests</c>/<c>NeedsHumanTriageSummaryTests</c>/<c>RateLimitedRenderingTests</c>
/// already exercise.
/// <para>
/// <b>There is no stub file.</b> The production type already exists — <c>RenderNeedsHumanSections</c> is
/// <c>public static</c> and <c>NeedsHumanClosingLine(kind)</c> already composes the
/// <see cref="NeedsHumanKinds.BlockedWork"/> guidance line. What is missing is the TEXT, not a type, so
/// nothing needs declaring before these tests can compile.
/// </para>
/// <para>
/// <b>Why this file exists at all.</b> The plan's first breakdown delivered the skill and the README and
/// SUBSTITUTED THE SSOT for the halt text — no task touched <c>RunCommand.cs</c> at all. A run that goes
/// green having skipped this surface ships a feature nobody can find.
/// </para>
/// <para>
/// TDD red: today <see cref="RunCommand.RenderNeedsHumanSections"/> never mentions the asymmetry or
/// <c>guardrails supply</c> for any halt, so <see cref="MissingResourceHalt_NamesTheAsymmetry"/> and
/// <see cref="MissingResourceHalt_PrintsTheCopyPasteableThreeCommandSequence"/> — this file's two pinned
/// behaviours (<c>guardrails/02-tests-fail-on-current-code.ps1</c>) — are expected to FAIL against this
/// tree. The two negative controls below are declared-exempt from that census: the never-fire-on-
/// everything requirement (design 40 §3, task 25's own prompt) is already true of code that adds nothing
/// at all, so they are green BY CONSTRUCTION, not despite it — they exist to catch an implementation that
/// over-fires once task 25 lands, the same defect-avoidance shape as this plan's other declared-exempt
/// tests.
/// </para>
/// </summary>
[Trait("Category", "Supply")]
public sealed class SuppliedHaltTextTests
{
    private const string TaskId = "02-vendor-mermaid-runtime";
    private const string RunId = "2026-09-11T21-32-30Z-demo";
    private const string MissingFilePath = "vendor/mermaid.min.js";

    private const string MissingFileQuestion =
        MissingFilePath + " is committed to master but is not an ancestor of this run's branch " +
        "lineage; the task cannot proceed without it.";

    private static readonly string PlanDirectory = Path.Combine("docs", "plans", "40-demo-plan");
    private static readonly string LogsRoot = Path.Combine(PlanDirectory, "logs", RunId);

    [Fact]
    public void MissingResourceHalt_NamesTheAsymmetry()
    {
        string rendered = RenderOne(Halt(MissingFileQuestion, NeedsHumanKinds.BlockedWork));

        // §5's exact rule, quoted rather than paraphrased into a constant this test also owns: "plan-folder
        // edits reach a running plan; code artifacts do not". Split across two Contains so the assertion
        // survives whichever connector (";" vs "and") task 25 actually ships.
        Assert.Contains("plan-folder edits reach a running plan", rendered, StringComparison.Ordinal);
        Assert.Contains("code artifacts do not", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingResourceHalt_PrintsTheCopyPasteableThreeCommandSequence()
    {
        string rendered = RenderOne(Halt(MissingFileQuestion, NeedsHumanKinds.BlockedWork));

        int supplyAt = rendered.IndexOf("guardrails supply", StringComparison.Ordinal);
        int resetAt = rendered.IndexOf("guardrails reset", StringComparison.Ordinal);
        int runAt = rendered.IndexOf("guardrails run", StringComparison.Ordinal);

        Assert.True(supplyAt >= 0, $"expected a 'guardrails supply' command in:\n{rendered}");
        Assert.True(resetAt > supplyAt, $"expected 'guardrails reset' to follow 'guardrails supply' in:\n{rendered}");
        Assert.True(runAt > resetAt, $"expected 'guardrails run' (the resume) to follow 'guardrails reset' in:\n{rendered}");

        string supplyLine = LineContaining(rendered, "guardrails supply");
        string resetLine = LineContaining(rendered, "guardrails reset");
        string runLine = LineContaining(rendered, "guardrails run");

        // #431, applied to a halt (§3): copy-pasteable means no <placeholder> left for the operator to
        // fill in — every argument below must be a real value this render call actually knew.
        Assert.DoesNotContain("<", supplyLine + resetLine + runLine, StringComparison.Ordinal);

        Assert.Contains(PlanDirectory, supplyLine, StringComparison.Ordinal);
        Assert.Contains(MissingFilePath, supplyLine, StringComparison.Ordinal);

        Assert.Contains(PlanDirectory, resetLine, StringComparison.Ordinal);
        Assert.Contains(TaskId, resetLine, StringComparison.Ordinal);

        Assert.Contains(PlanDirectory, runLine, StringComparison.Ordinal);
    }

    [Fact]
    public void BlockedWorkHalt_AboutAnOverScopedTask_KeepsTheOriginalClosingLine()
    {
        // Design 40 §3 scopes this to "the missing-resource case", and task 25's own prompt is explicit:
        // "A halt about a genuinely over-scoped task should not be told to go and supply a file." An
        // implementation that fires on every blocked-work halt would still pass the two tests above —
        // only this one catches it.
        string rendered = RenderOne(Halt(
            "this task's writeScope excludes the file the plan asks it to edit; re-scope the task.",
            NeedsHumanKinds.BlockedWork));

        Assert.Contains("answer the question or re-scope the task", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("guardrails supply", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("plan-folder edits reach a running plan", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void DefectiveGuardrailHalt_MentioningAFile_DoesNotGetTheAsymmetryOrSequence()
    {
        // The trigger is "a BLOCKED-WORK halt" (design 40 §3/§5). A defective-guardrail claim disputes the
        // CHECK, not a resource the operator can supply, and must not get this treatment merely because
        // its question happens to name a file.
        string rendered = RenderOne(Halt(MissingFileQuestion, NeedsHumanKinds.DefectiveGuardrail));

        Assert.DoesNotContain("guardrails supply", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("plan-folder edits reach a running plan", rendered, StringComparison.Ordinal);
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────

    private static TaskResult Halt(string question, string? kind, string taskId = TaskId) => new()
    {
        TaskId = taskId,
        Outcome = TaskOutcome.NeedsHuman,
        Summary = $"needs human: {question}",
        NeedsHumanKind = kind,
        NeedsHumanQuestion = question
    };

    private static string Render(IReadOnlyList<TaskResult> tasks, Func<string, TriageSummary?> triageFor)
    {
        using var writer = new StringWriter { NewLine = "\n" };
        RunCommand.RenderNeedsHumanSections(tasks, LogsRoot, writer, triageFor);
        return writer.ToString();
    }

    private static string RenderOne(TaskResult result) => Render([result], _ => null);

    private static string LineContaining(string rendered, string token)
    {
        string? line = rendered.Split('\n').FirstOrDefault(l => l.Contains(token, StringComparison.Ordinal));
        Assert.True(line is not null, $"expected a line containing '{token}' in:\n{rendered}");
        return line!;
    }
}
