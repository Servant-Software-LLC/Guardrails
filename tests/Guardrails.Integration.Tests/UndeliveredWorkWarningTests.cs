using Guardrails.Cli.Commands;
using Guardrails.Core.Execution;

namespace Guardrails.Integration.Tests;

/// <summary>
/// Pins the issue #340 loud "work not delivered" warning at the PUBLIC render seam
/// (<see cref="RunCommand.RenderUndeliveredWorkWarning"/>) — driven with a <see cref="StringWriter"/>
/// and fabricated <see cref="RunReport"/>s, no live process. The warning must fire exactly once, on a
/// wholly-green + undelivered run whose terminal gate ALSO passed, and be ABSENT on a delivered run, a
/// non-green run, and a run whose terminal gate failed.
/// </summary>
public sealed class UndeliveredWorkWarningTests
{
    private const string Marker = "*** WORK NOT DELIVERED ***";

    private static RunReport Report(bool whollyGreenButUndelivered, MergeOnSuccessResult? mergeOutcome = null) =>
        new()
        {
            Tasks =
            [
                new TaskResult { TaskId = "01-do-thing", Outcome = TaskOutcome.Succeeded, Summary = "ok" }
            ],
            MergeOnSuccessOutcome = mergeOutcome,
            WhollyGreenButUndelivered = whollyGreenButUndelivered
        };

    private static string Render(RunReport report, bool terminalGatePassed, string planDirectory)
    {
        using var writer = new StringWriter();
        RunCommand.RenderUndeliveredWorkWarning(report, terminalGatePassed, planDirectory, writer);
        return writer.ToString();
    }

    [Fact]
    public void WhollyGreenUndelivered_TerminalGatePassed_PrintsLoudWarning_NamingTheBranch()
    {
        string rendered = Render(
            Report(whollyGreenButUndelivered: true), terminalGatePassed: true,
            planDirectory: Path.Combine("repo", "plans", "dfd-threagile-substrate-wave-2b"));

        Assert.Contains(Marker, rendered);
        // The exact branch the undelivered work is sitting on must be named, verbatim.
        Assert.Contains("'guardrails/dfd-threagile-substrate-wave-2b'", rendered);
        Assert.Contains("mergeOnSuccess is off", rendered);
        // The destruction risk (the whole point of the warning) must be spelled out.
        Assert.Contains("--fresh", rendered);
        // The exact command to deliver the work must be given.
        Assert.Contains("--merge-on-success", rendered);
    }

    [Fact]
    public void DeliveredRun_PrintsNothing()
    {
        // A delivered run: the Scheduler set WhollyGreenButUndelivered=false and an outcome is present.
        string rendered = Render(
            Report(whollyGreenButUndelivered: false, mergeOutcome: MergeOnSuccessResult.FastForwarded),
            terminalGatePassed: true, planDirectory: Path.Combine("repo", "plan"));

        Assert.Equal(string.Empty, rendered);
    }

    [Fact]
    public void NonGreenRun_PrintsNothing()
    {
        // Not wholly green ⇒ the Scheduler never set the flag ⇒ silence (the run has its own failure path).
        string rendered = Render(
            Report(whollyGreenButUndelivered: false), terminalGatePassed: true,
            planDirectory: Path.Combine("repo", "plan"));

        Assert.Equal(string.Empty, rendered);
    }

    [Fact]
    public void TerminalGateFailed_PrintsNothing()
    {
        // The DAG drained green + undelivered, but the terminal gate FAILED — that path halts exit 2 on
        // its own; do NOT also claim "fully-green, safe on the branch".
        string rendered = Render(
            Report(whollyGreenButUndelivered: true), terminalGatePassed: false,
            planDirectory: Path.Combine("repo", "plan"));

        Assert.Equal(string.Empty, rendered);
    }

    [Fact]
    public void TrailingSeparator_OnPlanDirectory_DoesNotDoubleTheBranchSlug()
    {
        // A plan dir with a trailing separator must still resolve to guardrails/<plan-name>, not a blank slug.
        string rendered = Render(
            Report(whollyGreenButUndelivered: true), terminalGatePassed: true,
            planDirectory: Path.Combine("repo", "my-plan") + Path.DirectorySeparatorChar);

        Assert.Contains("'guardrails/my-plan'", rendered);
    }
}
