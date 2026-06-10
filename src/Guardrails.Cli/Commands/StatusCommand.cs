using System.CommandLine;
using Guardrails.Core.Journal;
using JournalTaskStatus = Guardrails.Core.Journal.TaskStatus;

namespace Guardrails.Cli.Commands;

/// <summary>
/// <c>guardrails status &lt;folder&gt;</c> — print a read-only table from the run journal:
/// task, status, attempt count, last failure reason, and the latest attempt's log dir.
/// Works mid-run (the journal is persisted at every transition) and after a run completes.
/// </summary>
public static class StatusCommand
{
    public static Command Create()
    {
        var folderArgument = new Argument<string>("folder")
        {
            Description = "Path to the plan folder (contains guardrails.json)."
        };

        var command = new Command("status", "Show per-task status from the run journal (read-only).");
        command.Add(folderArgument);

        command.SetAction(parseResult => Run(parseResult.GetRequiredValue(folderArgument)));
        return command;
    }

    private static int Run(string folder)
    {
        PlanProbe.Result probe = PlanProbe.LoadAndValidate(folder);
        if (probe.HasErrors || probe.Plan is null)
        {
            PlanProbe.PrintDiagnostics(probe.Diagnostics);
            Console.WriteLine("\nCould not load the plan.");
            return ExitCodes.HarnessError;
        }

        string journalPath = RunJournal.PathFor(probe.Plan.PlanDirectory);
        if (!File.Exists(journalPath))
        {
            Console.WriteLine("No run journal yet — this plan has not been run. Use 'guardrails run'.");
            return ExitCodes.Success;
        }

        // Read-only: do not normalize statuses (that is a resume concern), just report the
        // journal as it stands on disk.
        JournalDocument document = JournalReader.Read(journalPath);

        Console.WriteLine($"Run {document.RunId}  ({document.PlanHash})");
        Console.WriteLine();
        Console.WriteLine($"  {"TASK",-32} {"STATUS",-12} {"ATTEMPTS",-9} {"LAST FAILURE",-40} LOG DIR");
        Console.WriteLine(new string('-', 120));

        // Print in plan (ordinal) order so the table matches the run order.
        foreach (Core.Model.TaskNode task in probe.Plan.Tasks)
        {
            document.Tasks.TryGetValue(task.Id, out TaskJournalEntry? entry);
            PrintRow(task.Id, entry);
        }

        return ExitCodes.Success;
    }

    private static void PrintRow(string taskId, TaskJournalEntry? entry)
    {
        if (entry is null)
        {
            Console.WriteLine($"  {taskId,-32} {"(unknown)",-12} {"-",-9} {"-",-40} -");
            return;
        }

        AttemptRecord? last = entry.Attempts.Count == 0 ? null : entry.Attempts[^1];
        string failure = LastFailure(last);
        string logDir = last?.LogDir ?? "-";

        Console.WriteLine($"  {taskId,-32} {StatusText(entry.Status),-12} {entry.Attempts.Count,-9} {Truncate(failure, 40),-40} {logDir}");
    }

    private static string LastFailure(AttemptRecord? attempt)
    {
        if (attempt is null || attempt.Outcome == AttemptOutcome.Succeeded)
        {
            return "-";
        }

        if (attempt.FailedGuardrails.Count > 0)
        {
            FailedGuardrail first = attempt.FailedGuardrails[0];
            return $"{first.Name}: {first.Reason}";
        }

        return attempt.Outcome switch
        {
            AttemptOutcome.ActionFailed => $"action exited {attempt.ActionExitCode}",
            AttemptOutcome.Timeout => "timed out",
            AttemptOutcome.InvalidFragment => "invalid state fragment",
            AttemptOutcome.Cancelled => "cancelled",
            _ => attempt.Outcome.ToString()
        };
    }

    private static string StatusText(JournalTaskStatus status) => status switch
    {
        JournalTaskStatus.Pending => "pending",
        JournalTaskStatus.Running => "running",
        JournalTaskStatus.Succeeded => "succeeded",
        JournalTaskStatus.NeedsHuman => "needs-human",
        JournalTaskStatus.Blocked => "blocked",
        JournalTaskStatus.Failed => "failed",
        _ => status.ToString()
    };

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..(max - 1)] + "…";
}
