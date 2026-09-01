// A COMPLETE, representative CORRECT artifact for 03-accept-is-refused-for-divergence.ps1 (#468/#302):
// the interactive drift prompt after stage 15. The accept option is dropped for tasks whose journal
// entry records that they ran a definition they do not match, the reason is stated, the remedy is named,
// and the handler stays exactly as it was for an ordinary between-runs edit. Kept complete rather than a
// fragment; this header quotes none of the tokens the clauses key on.
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

        // A task whose journal entry records a hash captured at settle is, BY CONSTRUCTION, one that ran a
        // definition it does not match. Accepting its current on-disk hash without re-running it would
        // record that the task was built against the new definition when it was built against the old one
        // - the exact claim this whole change exists to make impossible - and would leave the task's
        // plan-branch trailer uncorroborated against the journal, so any later scoped rewind covering it
        // refuses and the operator is pushed to a full reset.
        var divergenceOriginated = drift.SafeSet
            .Where(id => journal.Document.Tasks.TryGetValue(id, out var entry) && entry.DefinitionHashAtSettle is not null)
            .ToList();

        io.Out.WriteLine();
        io.Out.WriteLine("  [y] " + rewind + ".");

        if (divergenceOriginated.Count > 0)
        {
            io.Out.WriteLine(
                "  [a] is NOT offered for " + divergenceOriginated.Count + " of these task(s): they settled against a definition");
            io.Out.WriteLine(
                "      that had already moved, so accepting the current hash would record a verification that");
            io.Out.WriteLine(
                "      never happened. Re-run them instead: guardrails reset <folder> " + string.Join(" ", divergenceOriginated));
        }
        else
        {
            io.Out.WriteLine(
                "  [a] ACCEPT the drift and continue: re-baseline the drifted task(s) WITHOUT re-running them, "
                + "then finish the " + remaining + " task(s) that remain.");
            io.Out.WriteLine(
                "      The delivered artifact then predates its own definition - a real trade, recorded in");
            io.Out.WriteLine(
                "      decisions[] and named in the run report, because nothing else would show it afterwards.");
        }

        io.Out.WriteLine("  [N] abort - change nothing and stop.");
        io.Out.Write(divergenceOriginated.Count > 0 ? "Choose [y/N] " : "Choose [y/a/N] ");

        string answer = (Console.ReadLine() ?? "").Trim();

        if (answer.Equals("a", StringComparison.OrdinalIgnoreCase) && divergenceOriginated.Count == 0)
        {
            // UNCHANGED for an ordinary between-runs edit: that trade is already reviewed.
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
