// The ONE defect 03-accept-is-refused-for-divergence.ps1 exists to catch: the accept branch left
// exactly as it shipped. Nothing about this file looks wrong - it is the code that is there today -
// and that is the point: this plan CREATES the traffic through that branch, and one keystroke there
// records that a task was built against a definition it never saw.
using System;
using System.Linq;
using Guardrails.Core.Journal;
using Guardrails.Core.Model;

namespace Guardrails.Cli.Commands;

public static partial class RunCommand
{
    private static (DriftPromptDecision Decision, DriftAuthorization? Authorization) ConfirmSafeDriftIfInteractive(
        PlanDefinition plan, RunJournal journal, IConsoleIo io)
    {
        DefinitionDriftProbe.Result drift = DefinitionDriftProbe.Evaluate(plan, journal);
        if (!drift.HasDrift || drift.Decision.Outcome == SafeSuffixOutcome.Refused || Console.IsInputRedirected)
        {
            return (DriftPromptDecision.NotPrompted, null);
        }

        PrintDriftPromptPreview(drift, io);

        int remaining = journal.Document.Tasks.Count(t => t.Value.Status != TaskStatus.Succeeded);
        string rewind = drift.Decision.Outcome == SafeSuffixOutcome.Safe
            ? "rewind the plan branch (" + drift.Decision.RemovedCommitCount + " commit(s)) and re-run " + drift.SafeSet.Count + " task(s)"
            : "reset and re-run " + drift.SafeSet.Count + " task(s)";

        io.Out.WriteLine();
        io.Out.WriteLine("  [y] " + rewind + ".");

        io.Out.WriteLine(
            "  [a] ACCEPT the drift and continue: re-baseline the drifted task(s) WITHOUT re-running them, "
            + "then finish the " + remaining + " task(s) that remain.");
        io.Out.WriteLine(
            "      The delivered artifact then predates its own definition - a real trade, recorded in");
        io.Out.WriteLine(
            "      decisions[] and named in the run report, because nothing else would show it afterwards.");

        io.Out.WriteLine("  [N] abort - change nothing and stop.");
        io.Out.Write("Choose [y/a/N] ");

        string answer = (Console.ReadLine() ?? "").Trim();

        if (answer.Equals("a", StringComparison.OrdinalIgnoreCase))
        {
            foreach (string id in drift.SafeSet)
            {
                journal.RecordDriftAccepted(id, DefinitionDriftProbe.CurrentHash(plan, id));
            }

            return (DriftPromptDecision.Accepted, DriftAuthorization.Accept(drift.SafeSet));
        }

        if (answer.Equals("y", StringComparison.OrdinalIgnoreCase))
        {
            return (DriftPromptDecision.Approved, DriftAuthorization.Rewind(drift.SafeSet));
        }

        return (DriftPromptDecision.Declined, null);
    }
}
