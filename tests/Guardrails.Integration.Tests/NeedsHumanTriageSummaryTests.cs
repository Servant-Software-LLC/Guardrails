using Guardrails.Cli.Commands;
using Guardrails.Core.Execution;

namespace Guardrails.Integration.Tests;

/// <summary>
/// Issue #163: the post-run NEEDS HUMAN console section surfaces the AI triage root-cause CATEGORY
/// + one-line diagnosis (and the drafted GH-issue title when present) directly, so the user does not
/// open each <c>feedback.md</c>. These pin <see cref="RunCommand.RenderNeedsHumanSections"/> — the
/// pure renderer the run summary delegates to — with an injected triage resolver (the production
/// path reads <c>triage.json</c>; here the lookup is faked so the test is IO-free and deterministic).
/// </summary>
public sealed class NeedsHumanTriageSummaryTests
{
    private static TaskResult NeedsHuman(string taskId, string summary) => new()
    {
        TaskId = taskId,
        Outcome = TaskOutcome.NeedsHuman,
        Summary = summary
    };

    private static string Render(
        IReadOnlyList<TaskResult> tasks,
        Func<string, TriageSummary?> triageFor)
    {
        using var writer = new StringWriter();
        RunCommand.RenderNeedsHumanSections(tasks, logsRoot: "LOGS", writer, triageFor);
        return writer.ToString();
    }

    [Fact]
    public void StructuredTriage_SurfacesCategoryAndOneLine_AndTitle()
    {
        var tasks = new[] { NeedsHuman("04-author-tests", "foreign top-level key(s): 'j9hf6y' — needs human after 3 attempt(s)") };

        var triage = new TriageSummary(
            "guardrails-tool",
            "plan-breakdown emits stableId as state-out key",
            "plan-breakdown: action.prompt.md emits stableId as state-out key instead of task folder name");

        string output = Render(tasks, _ => triage);

        // The leading line stays parseable (consumers rely on the "NEEDS HUMAN: <id> — <summary>" shape).
        Assert.Contains("NEEDS HUMAN: 04-author-tests — foreign top-level key(s): 'j9hf6y'", output);
        // The root-cause category + one-line are now visible WITHOUT opening feedback.md.
        Assert.Contains("Root cause [guardrails-tool]: plan-breakdown emits stableId as state-out key", output);
        // The drafted GH issue title (distinct from the one-line) is surfaced too.
        Assert.Contains("Draft GH issue: plan-breakdown: action.prompt.md emits stableId as state-out key", output);
    }

    [Fact]
    public void NoStructuredTriage_RendersUnchanged_NoSpuriousRootCauseLine()
    {
        var tasks = new[] { NeedsHuman("06-doomed", "guardrail(s) failed: 02-tests — needs human after 2 attempt(s)") };

        // The resolver returns null (unstructured / failed triage) — the section must be unchanged.
        string output = Render(tasks, _ => null);

        Assert.Contains("NEEDS HUMAN: 06-doomed — guardrail(s) failed: 02-tests", output);
        Assert.DoesNotContain("Root cause", output);
        Assert.DoesNotContain("Draft GH issue", output);
        // The original inspect/fix guidance is still there.
        Assert.Contains("feedback.md has the full failure detail", output);
    }

    [Fact]
    public void SharedCategory_AcrossTasks_IsAnnotated_SoOneFixResolvesSeveral()
    {
        var tasks = new[]
        {
            NeedsHuman("04-author-tests", "foreign top-level key(s): 'j9hf6y' — needs human after 3 attempt(s)"),
            NeedsHuman("06-author-rest-importer", "foreign top-level key(s): 'ab12cd' — needs human after 1 attempt(s)")
        };

        // Both tasks share the same root-cause CATEGORY (the issue's main ask: make the shared cause obvious).
        var shared = new TriageSummary("guardrails-tool", "stableId key rejected, file writes rolled back", null);

        string output = Render(tasks, _ => shared);

        // First task: establishes the category; no back-reference yet.
        Assert.Contains("Root cause [guardrails-tool]: stableId key rejected, file writes rolled back", output);
        // Second task: annotated as the SAME root cause as the first, so one fix is seen to resolve both.
        Assert.Contains("(same root cause as 04-author-tests)", output);

        // The back-reference annotates only the LATER occurrence, not the first.
        int firstIdx = output.IndexOf("04-author-tests", StringComparison.Ordinal);
        int annotationIdx = output.IndexOf("(same root cause as 04-author-tests)", StringComparison.Ordinal);
        Assert.True(annotationIdx > firstIdx, "the group annotation must appear on the second task, after the first");
    }

    [Fact]
    public void CategoryWithoutOneLine_StillSurfacesTheCategory()
    {
        var tasks = new[] { NeedsHuman("07-x", "needs human after 2 attempt(s)") };

        // A sidecar with a diagnosis but no one-line (e.g. neither analysis nor title parsed).
        var triage = new TriageSummary("local-repo", OneLine: null, GhIssueTitle: null);

        string output = Render(tasks, _ => triage);

        Assert.Contains("Root cause [local-repo]", output);
        // No trailing ": " when there is no one-line.
        Assert.DoesNotContain("Root cause [local-repo]:", output);
    }
}
