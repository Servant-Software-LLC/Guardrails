// EXTRA CASE (not executed by `guardrails samples verify`, which matches only the exact .valid/
// .invalid pair - kept committed so a later editor can re-run it by hand). THE W5 CASE: the token is
// present in the FILE - in DescribeDelivery, which is a real and unrelated stage-15 deliverable -
// and ABSENT from the drift prompt, which is untouched. A file-wide clause goes green here. Only a
// clause scoped to ConfirmSafeDriftIfInteractive's own region sees it.
using System;
using System.Linq;
using Guardrails.Core.Journal;
using Guardrails.Core.Model;

namespace Guardrails.Cli.Commands;

public static partial class RunCommand
{
    /// <summary>The durable delivery reason (a different stage-15 deliverable entirely).</summary>
    public static string DescribeDelivery(RunJournal journal) =>
        journal.Document.Tasks.Values.Any(t => t.DefinitionHashAtSettle is not null)
            ? "delivery blocked: a task settled against a definition that had already moved"
            : "the run was not wholly green, so delivery was never attempted";

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
