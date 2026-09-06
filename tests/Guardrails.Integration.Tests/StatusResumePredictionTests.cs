using System.CommandLine;
using Guardrails.Cli;
using Guardrails.Cli.Commands;
using Guardrails.Core.Journal;

namespace Guardrails.Integration.Tests;

/// <summary>
/// Issue #639 — <c>guardrails status</c> showed the previous run's outcome where a reader was asking for
/// a prediction.
///
/// <para>
/// <b>The report.</b> A session driving the harness in autonomous mode stopped a doomed task, fixed it,
/// and restarted — <i>"Resume, not fresh"</i>. From then on, every task that had not yet started displayed
/// <c>blocked</c>, and they <b>then ran normally when their turn came</b>. So the status shown was wrong
/// while scheduling was right.
/// </para>
///
/// <para>
/// <b>The mechanism, which is a deliberate choice one layer away from where it shows.</b>
/// <c>StatusCommand</c> reads the journal WITHOUT normalizing, by design — normalization is a resume
/// concern, and normalizing here would discard which tasks a failure had blocked, the one thing this
/// command can tell you that a resumed run cannot. But <c>RunJournal.ResumeStatus</c> turns
/// <c>blocked</c> / <c>failed</c> / <c>needs-human</c> / <c>running</c> back into <c>pending</c> before
/// the next run schedules anything. So the table was literally true about the FILE and misleading about
/// the PLAN, and it is the only question this command exists to answer.
/// </para>
///
/// <para>
/// The fix says BOTH facts rather than trading one for the other. These tests pin the second one, which
/// is the half that did not exist.
/// </para>
/// </summary>
public sealed class StatusResumePredictionTests
{
    private static async Task<(int Exit, string Output)> StatusAsync(string planDir)
    {
        var io = new StringConsoleIo();
        var root = new RootCommand("test root");
        root.Add(StatusCommand.Create(io));
        int exit = await root.Parse(["status", planDir]).InvokeAsync();
        return (exit, io.OutText);
    }

    private static async Task<int> RunAsync(string planDir)
    {
        var io = new StringConsoleIo();
        var root = new RootCommand("test root");
        root.Add(RunCommand.Create(io));
        return await root.Parse(["run", planDir, "--no-log-server"]).InvokeAsync();
    }

    /// <summary>
    /// The headline. A failed task blocks its dependent; the journal then holds <c>failed</c> and
    /// <c>blocked</c>, and a resume will run BOTH. Status must say so — otherwise the reader is told a
    /// task is blocked at the exact moment its blocker is about to be retried.
    /// </summary>
    [Fact]
    public async Task AfterAFailedRun_StatusSaysWhichTasksAResumeWillReRun()
    {
        using var plan = new StatePlanBuilder()
            .AddTask("01-first", guardrailBody: StatePlanBuilder.Fail("not yet"))
            .AddTask("02-second", dependsOn: "01-first");

        Assert.Equal(ExitCodes.TaskFailed, await RunAsync(plan.PlanDir));

        (int exit, string output) = await StatusAsync(plan.PlanDir);
        Assert.Equal(ExitCodes.Success, exit);

        // The on-disk facts are still reported — they are what this command uniquely knows.
        Assert.Contains("blocked", output, StringComparison.Ordinal);

        // ...and so is what a resume will do with them.
        Assert.Contains("A resume will RE-RUN these", output, StringComparison.Ordinal);
        Assert.Contains("01-first", output, StringComparison.Ordinal);
        Assert.Contains("02-second (blocked)", output, StringComparison.Ordinal);
        Assert.Contains(
            "Only 'succeeded' survives a resume", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// The control, and it is the one that keeps the fix from becoming noise: a fully green run says
    /// nothing about resuming, because nothing would be re-run. Without this, the footer could be printed
    /// unconditionally and every clean run would carry a paragraph about a resume that has nothing to do.
    /// </summary>
    [Fact]
    public async Task AfterAGreenRun_StatusSaysNothingAboutResuming()
    {
        using var plan = new StatePlanBuilder().AddTask("01-first");

        Assert.Equal(ExitCodes.Success, await RunAsync(plan.PlanDir));

        (_, string output) = await StatusAsync(plan.PlanDir);
        Assert.DoesNotContain("A resume will RE-RUN", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// The rule has ONE owner. <c>guardrails status</c> derives what a resume would do from
    /// <see cref="RunJournal.WouldResumeRun"/>, which is itself derived from the resume normalization the
    /// scheduler uses — not from a second list of statuses maintained beside it. Two copies is exactly how
    /// a status report comes to disagree with the resume it describes, and a report that disagrees with
    /// the scheduler is worse than one that says nothing, because it is believed.
    ///
    /// <para>
    /// <c>Pending</c> is excluded deliberately: it also stays pending, but it has not run, so calling it a
    /// RE-run would be its own small lie.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(Core.Journal.TaskStatus.Blocked, true)]
    [InlineData(Core.Journal.TaskStatus.Failed, true)]
    [InlineData(Core.Journal.TaskStatus.NeedsHuman, true)]
    [InlineData(Core.Journal.TaskStatus.Running, true)]
    [InlineData(Core.Journal.TaskStatus.Succeeded, false)]
    [InlineData(Core.Journal.TaskStatus.Pending, false)]
    public void WouldResumeRun_MatchesTheSchedulersOwnNormalization(
        Core.Journal.TaskStatus status, bool expected)
    {
        Assert.Equal(expected, RunJournal.WouldResumeRun(status));
    }
}
