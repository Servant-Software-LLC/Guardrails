using System.CommandLine;
using Guardrails.Cli;
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
/// <para>
/// Design 41 §2.1/§6: <see cref="RunCommand.MissingResourceHaltLines"/> is due to move off its private
/// first-match-only regex onto the shared <c>MissingResourceSignal</c> predicate (task 16), and the #597
/// interlock wording is due to generalize from the two best-guess/unreviewed tokens to a machine decision.
/// Neither production change exists yet on this tree — the rows below pin the RENDERED TEXT, never the
/// predicate's own API, so this file compiles unchanged whatever shape task 16 lands.
/// </para>
/// </summary>
[Trait("Category", "OverwatchSupply")]
public sealed class SuppliedHaltTextTests
{
    private const string TaskId = "02-vendor-mermaid-runtime";
    private const string RunId = "2026-09-11T21-32-30Z-demo";
    private const string MissingFilePath = "vendor/mermaid.min.js";
    private const string SecondMissingFilePath = "vendor/other-lib.js";
    private const string DotSlashFilePath = "./mermaid.min.js";
    private const string NormalizedDotSlashFilePath = "mermaid.min.js";
    private const string ScopedNodeModulesPath = "node_modules/@scope/x/index.js";

    private const string MissingFileQuestion =
        MissingFilePath + " is committed to master but is not an ancestor of this run's branch " +
        "lineage; the task cannot proceed without it.";

    private const string TwoMissingFilesQuestion =
        MissingFilePath + " and " + SecondMissingFilePath + " are committed to master but are not " +
        "ancestors of this run's branch lineage; the task cannot proceed without them.";

    private const string DotSlashFileQuestion =
        DotSlashFilePath + " is committed to master but is not an ancestor of this run's branch " +
        "lineage; the task cannot proceed without it.";

    private const string ScopedNodeModulesQuestion =
        ScopedNodeModulesPath + " is committed to master but is not an ancestor of this run's branch " +
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

    [Fact]
    public void MissingResourceHalt_ForASinglePlainPath_IsUnchanged()
    {
        // The NEVER-WEAKER floor (design 41 §2.1): moving onto the shared predicate is allowed to match
        // MORE than today's private regex, never to change what it already got right for the ordinary
        // one-path case. Pins the exact three commands, byte for byte, so a "rewrite the halt text while
        // I'm in here" implementation cannot slip past the other four rows in this file.
        string rendered = RenderOne(Halt(MissingFileQuestion, NeedsHumanKinds.BlockedWork));

        Assert.Contains($"guardrails supply {PlanDirectory} {MissingFilePath}", rendered, StringComparison.Ordinal);
        Assert.Contains($"guardrails reset {PlanDirectory} {TaskId}", rendered, StringComparison.Ordinal);
        Assert.Contains($"guardrails run {PlanDirectory}", rendered, StringComparison.Ordinal);
        Assert.Contains(
            $"guardrails supply --resume {PlanDirectory} {MissingFilePath}", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingResourceHalt_NamesEveryPathTheQuestionNames()
    {
        // Today's private regex takes Match.Value — the FIRST match only — so the second path is invisible
        // and the operator supplies half of what the task needs.
        string rendered = RenderOne(Halt(TwoMissingFilesQuestion, NeedsHumanKinds.BlockedWork));

        string supplyLines = string.Join("\n", LinesContaining(rendered, "guardrails supply"));
        Assert.True(supplyLines.Length > 0, $"expected at least one 'guardrails supply' line in:\n{rendered}");
        Assert.Contains(MissingFilePath, supplyLines, StringComparison.Ordinal);
        Assert.Contains(SecondMissingFilePath, supplyLines, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingResourceHalt_NormalizesALeadingDotSlash_ForARootLevelFile()
    {
        // The domain-knowledge skill tells agents to write the "./" form precisely so a root-level file
        // matches the "names a path" predicate at all; today's private regex captures that leading "./"
        // verbatim into the emitted command, which is not runnable as printed.
        string rendered = RenderOne(Halt(DotSlashFileQuestion, NeedsHumanKinds.BlockedWork));

        string supplyLine = LineContaining(rendered, "guardrails supply");
        Assert.Contains(NormalizedDotSlashFilePath, supplyLine, StringComparison.Ordinal);
        Assert.DoesNotContain("./" + NormalizedDotSlashFilePath, supplyLine, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingResourceHalt_MatchesAScopedNodeModulesPath_Whole()
    {
        // Today's private regex excludes '@', so it matches only the truncated interior substring
        // "scope/x/index.js" — a wrong answer (a path that does not exist), not a missing one.
        string rendered = RenderOne(Halt(ScopedNodeModulesQuestion, NeedsHumanKinds.BlockedWork));

        string supplyLine = LineContaining(rendered, "guardrails supply");
        Assert.Contains(ScopedNodeModulesPath, supplyLine, StringComparison.Ordinal);
    }

    [Fact]
    public void UndeliveredWorkBanner_ForAMachineDecision_DoesNotCallItABestGuess()
    {
        // Design 41 §6 adds 'auto-supplied' to the shared delivery-interlock token set. The banner's
        // "JUDGE THE DECISION FIRST" remedy calls every suppressing decision "A best-guess" unconditionally
        // today — the wrong noun for a certified auto-resolve, which is a bounded judgement, not a guess.
        RunReport report = SuppressedReport(AutoSuppliedAt("07-vendor-mermaid-runtime"));

        string rendered = RenderUndelivered(report);

        Assert.DoesNotContain("A best-guess", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void UndeliveredWorkBanner_NamesTheAutoSuppliedDecisionAndItsTask()
    {
        // Green on the current tree BY CONSTRUCTION: the banner already names the suppressing decision by
        // INTERPOLATING DecisionEntry.Decision / .Subject rather than enumerating known tokens, so an
        // 'auto-supplied' entry is already named correctly. This row exists to keep that true once
        // 'auto-supplied' joins the token set, not to be turned red.
        RunReport report = SuppressedReport(AutoSuppliedAt("07-vendor-mermaid-runtime"));

        string rendered = RenderUndelivered(report);

        Assert.Contains(DecisionTokens.AutoSupplied, rendered, StringComparison.Ordinal);
        Assert.Contains("07-vendor-mermaid-runtime", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void MergeOnSuccessOption_DescribesAMachineDecision_WithoutEnumeratingOnlyTheTwoTokens()
    {
        // Reached through the real command surface (not re-declared here) so the assertion tracks whatever
        // task 16 actually ships in RunCommand.Create.
        Command command = RunCommand.Create(new StringConsoleIo(), TelemetryOverrides.None);
        Option mergeOnSuccessOption = command.Options.Single(o => o.Name == "--merge-on-success");
        string description = mergeOnSuccessOption.Description ?? string.Empty;

        Assert.Contains("machine decision", description, StringComparison.Ordinal);
        Assert.DoesNotContain("proceeded-best-guess / proceeded-unreviewed", description, StringComparison.Ordinal);
        Assert.DoesNotContain("only for those two cases", description, StringComparison.Ordinal);
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

    private static IEnumerable<string> LinesContaining(string rendered, string token) =>
        rendered.Split('\n').Where(l => l.Contains(token, StringComparison.Ordinal));

    private static DecisionEntry AutoSuppliedAt(string subject) => new()
    {
        Boundary = "task",
        Policy = "auto",
        Decision = DecisionTokens.AutoSupplied,
        Subject = subject,
        Headline = "auto-supplied a missing resource"
    };

    private static RunReport SuppressedReport(DecisionEntry suppressing) => new()
    {
        Tasks = [new TaskResult { TaskId = "01-do-thing", Outcome = TaskOutcome.Succeeded, Summary = "ok" }],
        WhollyGreenButUndelivered = true,
        DeliverySuppressingDecision = suppressing
    };

    private static string RenderUndelivered(RunReport report)
    {
        using var writer = new StringWriter { NewLine = "\n" };
        RunCommand.RenderUndeliveredWorkWarning(report, terminalGatePassed: true, PlanDirectory, writer);
        return writer.ToString();
    }
}
