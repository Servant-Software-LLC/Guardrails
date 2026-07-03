using Guardrails.Cli.Commands;
using Guardrails.Cli.Ui;
using Guardrails.Core.Execution;

namespace Guardrails.Integration.Tests;

/// <summary>
/// Issue #190 part 1: <see cref="TaskOutcome.RateLimited"/> must render DISTINCTLY from a generic
/// <see cref="TaskOutcome.NeedsHuman"/> everywhere the CLI surfaces a per-task outcome — the live
/// table (<see cref="LiveRunObserver.StatusMarkup"/>), the run summary's per-task status word
/// (<see cref="RunCommand.StatusLabel"/> is internal — exercised indirectly via
/// <see cref="ConsoleRunObserver"/>/the ExecuteAsync CLI seam elsewhere; here we pin the two PUBLIC
/// rendering seams directly), and the post-run needs-human sections
/// (<see cref="RunCommand.RenderNeedsHumanSections"/>).
/// </summary>
public sealed class RateLimitedRenderingTests
{
    [Fact]
    public void LiveRunObserver_StatusMarkup_RateLimited_IsDistinctFromNeedsHuman()
    {
        string rateLimited = LiveRunObserver.StatusMarkup(TaskOutcome.RateLimited);
        string genericNeedsHuman = LiveRunObserver.StatusMarkup(TaskOutcome.ActionFailed); // falls into the wildcard "needs human"

        Assert.Equal("[blue]rate limited[/]", rateLimited);
        Assert.Equal("[red]needs human[/]", genericNeedsHuman);
        Assert.NotEqual(rateLimited, genericNeedsHuman);
    }

    [Theory]
    [InlineData(TaskOutcome.Succeeded, "[green]succeeded[/]")]
    [InlineData(TaskOutcome.Skipped, "[green]skipped[/]")]
    [InlineData(TaskOutcome.Blocked, "[orange3]blocked[/]")]
    [InlineData(TaskOutcome.Cancelled, "[grey]cancelled[/]")]
    [InlineData(TaskOutcome.NeedsHuman, "[red]needs human[/]")]
    [InlineData(TaskOutcome.GuardrailFailed, "[red]needs human[/]")]
    public void LiveRunObserver_StatusMarkup_UnchangedForExistingOutcomes(TaskOutcome outcome, string expected) =>
        Assert.Equal(expected, LiveRunObserver.StatusMarkup(outcome));

    [Fact]
    public void RenderNeedsHumanSections_RateLimitedTask_GetsOwnSection_NotFoldedIntoNeedsHuman()
    {
        var rateLimited = new TaskResult
        {
            TaskId = "03-flaky",
            Outcome = TaskOutcome.RateLimited,
            Summary = "paused (rate-limited): session limit; did not clear within 60s — re-run later"
        };
        var genuinelyStuck = new TaskResult
        {
            TaskId = "04-broken",
            Outcome = TaskOutcome.NeedsHuman,
            Summary = "needs human: a real question"
        };

        using var writer = new StringWriter();
        RunCommand.RenderNeedsHumanSections(
            [rateLimited, genuinelyStuck], "/tmp/logs", writer, _ => null);

        string rendered = writer.ToString();

        // Distinct section header for the rate-limited task, with "re-run later" guidance — NOT the
        // "fix the action or guardrails" advice, which would mislead an operator into debugging a
        // healthy task.
        Assert.Contains("RATE LIMITED: 03-flaky", rendered);
        Assert.Contains("re-run", rendered, StringComparison.OrdinalIgnoreCase);

        // The genuinely-stuck task still gets the ordinary NEEDS HUMAN section + advice.
        Assert.Contains("NEEDS HUMAN: 04-broken", rendered);
        Assert.Contains("fix the action or guardrails", rendered);

        // The rate-limited task must NOT ALSO appear under a "NEEDS HUMAN:" header.
        Assert.DoesNotContain("NEEDS HUMAN: 03-flaky", rendered);
    }

    [Fact]
    public void RateLimitedOutcome_IsNeverGreen()
    {
        var result = new TaskResult
        {
            TaskId = "01-x",
            Outcome = TaskOutcome.RateLimited,
            Summary = "paused (rate-limited): x — re-run later"
        };

        Assert.False(result.IsGreen);
    }
}
