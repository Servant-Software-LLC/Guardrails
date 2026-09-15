using System.CommandLine;
using System.Globalization;
using Guardrails.Cli.Ui;
using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using JournalTaskStatus = Guardrails.Core.Journal.TaskStatus;

namespace Guardrails.Cli.Commands;

/// <summary>
/// <c>guardrails status [folder]</c> — print a read-only table from the run journal:
/// task, status, attempt count, last failure reason, and the latest attempt's log dir — under one line saying
/// whether the run is alive (issue #704).
/// Works mid-run (the journal is persisted at every transition) and after a run completes.
/// Defaults to the current directory when the folder is omitted.
/// </summary>
public static class StatusCommand
{
    /// <summary>
    /// Build the command. <paramref name="processProbe"/> is the process table the run-liveness line is checked
    /// against (issue #704): null means the real one, which is what the CLI's command factory builds; a test passes
    /// a fake to decide the answer without a real process to kill.
    /// </summary>
    public static Command Create(IConsoleIo io, IProcessProbe? processProbe = null)
    {
        var folderArgument = FolderArgument.Create();

        var command = new Command("status", "Show per-task status from the run journal (read-only).");
        command.Add(folderArgument);

        command.SetAction(parseResult => Run(
            FolderArgument.ResolveAndAnnounce(parseResult.GetValue(folderArgument), io.Out),
            io,
            processProbe ?? SystemProcessProbe.Instance));
        return command;
    }

    private static int Run(string folder, IConsoleIo io, IProcessProbe processProbe)
    {
        TextWriter output = io.Out;

        PlanProbe.Result probe = PlanProbe.LoadAndValidate(folder);
        if (probe.HasErrors || probe.Plan is null)
        {
            PlanProbe.PrintDiagnostics(probe.Diagnostics, output);
            output.WriteLine("\nCould not load the plan.");
            return ExitCodes.HarnessError;
        }

        string journalPath = RunJournal.PathFor(probe.Plan.PlanDirectory);
        if (!File.Exists(journalPath))
        {
            output.WriteLine("No run journal yet — this plan has not been run. Use 'guardrails run'.");
            return ExitCodes.Success;
        }

        // Read-only: do not normalize statuses (that is a resume concern), just report the journal as it
        // stands on disk.
        //
        // #639 — that is correct and it was not enough. A halted run leaves `blocked` / `failed` /
        // `needs-human` / `running` entries on disk, and a resume turns every one of them back into
        // `pending` (RunJournal.ResumeStatus) before scheduling anything. So this table was literally true
        // about the FILE and misleading about the PLAN: it showed `blocked` for tasks the very next
        // `guardrails run` would execute, and a reader asking the only question this command exists to
        // answer — what happens next — got the previous run's outcome presented as a prediction. The
        // report from the field was exactly that shape: "every task that had not yet started displayed
        // blocked, and they then ran normally when their turn came".
        //
        // The fix is not to normalize here (that would discard which tasks a failure had blocked, which
        // is the one thing this command can tell you that a resumed run cannot). It is to say BOTH facts.
        JournalDocument document = JournalReader.Read(journalPath);

        // #704 — and the file cannot say whether anything is still RUNNING. A run that died with a sleeping or
        // rebooting laptop leaves the journal exactly as a live run leaves it, so the table alone misreported in
        // both directions: a dead run read as in progress, and a live run's in-flight task was listed as leftover
        // state a resume would discard. The verdict comes from facts — did the owner record an end, is its pid
        // (with its start time) still in the process table — never from how long ago the journal last moved.
        RunLivenessState liveness = RunLiveness.Assess(document.Owner, RunLiveness.ThisHost(), processProbe);

        output.WriteLine($"Run {document.RunId}  ({document.PlanHash})");
        output.WriteLine(LivenessLine(
            liveness, document.Owner, File.GetLastWriteTimeUtc(journalPath), DateTimeOffset.UtcNow, folder));
        output.WriteLine();

        int taskWidth = TaskColumnWidth(probe.Plan.Tasks.Select(task => task.Id));
        output.WriteLine($"  {"TASK".PadRight(taskWidth)} {"STATUS",-12} {"ATTEMPTS",-9} {"LAST FAILURE",-40} LOG DIR");
        output.WriteLine(new string('-', taskWidth + 88));

        // Print in plan (ordinal) order so the table matches the run order.
        foreach (Core.Model.TaskNode task in probe.Plan.Tasks)
        {
            document.Tasks.TryGetValue(task.Id, out TaskJournalEntry? entry);
            PrintRow(task.Id, taskWidth, entry, liveness, output);
        }

        // #639: what a resume would do with the rows above. Omitted entirely when nothing is affected —
        // a clean or still-pending journal prints no footer at all.
        //
        // #704: and omitted while the owner is ALIVE. The footer exists because a halted run's table is a last
        // outcome that reads like a prediction; a live run's table is neither — it is the run's current state,
        // and its `running` task is in flight, not something a resume will discard.
        IReadOnlyList<string> resumable = liveness == RunLivenessState.Running
            ? []
            : ResumableTasks(probe.Plan, document, liveness);
        if (resumable.Count > 0)
        {
            output.WriteLine();
            output.WriteLine(
                "A resume will RE-RUN these — the status above is the last run's outcome, not a prediction:");
            foreach (string line in resumable)
            {
                output.WriteLine($"  {line}");
            }

            output.WriteLine(
                "Only 'succeeded' survives a resume; blocked / failed / needs-human / running all become "
                + "pending.");
        }

        // #515: the provider-pause ledger, omitted entirely when nothing paused (nearly every run).
        IReadOnlyList<string> pauseLines = TransientPauseLines(document);
        if (pauseLines.Count > 0)
        {
            output.WriteLine();
            output.WriteLine("Provider pauses (transient; did NOT consume the retry budget)");
            foreach (string line in pauseLines)
            {
                output.WriteLine(line);
            }
        }

        // Run-level cost (SSOT §7 costUsd) — omitted entirely when no attempt recorded a
        // cost, so deterministic-only plans stay noise-free.
        if (JournalCost.Total(document) is { } total)
        {
            output.WriteLine();
            output.WriteLine($"Total prompt cost: ${total:F4}");
        }

        return ExitCodes.Success;
    }

    /// <summary>
    /// The one line above the table that says whether the run is alive (issue #704, SSOT §7 <c>owner</c>) — pure and
    /// public for the same reason <see cref="LastFailureText"/> is. <paramref name="owner"/> is the owner
    /// <paramref name="state"/> was assessed from.
    /// <para>
    /// Every verdict short of FINISHED also carries the journal's last write time and its age. That is an
    /// OBSERVATION for the operator and nothing more: it is what tells a live-but-stuck run (the first occurrence —
    /// alive, idle, 28 hours without a write) from a live-and-busy one. It is kept out of the verdict itself on
    /// purpose, because a suspended laptop's clock keeps moving and "no write for N hours" would condemn a healthy
    /// run that merely slept. FINISHED omits it: the recorded end already says when the journal stopped moving.
    /// </para>
    /// </summary>
    public static string LivenessLine(
        RunLivenessState state, RunOwner? owner, DateTimeOffset lastJournalWrite, DateTimeOffset now, string folder)
    {
        string lastWrite =
            $"Last journal write {Timestamp(lastJournalWrite)} ({BreakdownProgress.FormatClock(now - lastJournalWrite)} ago).";

        return state switch
        {
            RunLivenessState.Running =>
                $"Run state: RUNNING — owner process {owner!.Pid} is alive. {lastWrite}",
            RunLivenessState.ExitedWithoutFinishing =>
                $"Run state: EXITED WITHOUT FINISHING — owner process {owner!.Pid} is gone and never recorded an end, "
                + $"so nothing is running. {lastWrite} Resume with: guardrails run {PasteableFolder(folder)}",
            RunLivenessState.Finished =>
                $"Run state: FINISHED — owner process {owner!.Pid} recorded its end at {Timestamp(owner.FinishedAt!.Value)}; "
                + "nothing is running.",
            RunLivenessState.OnAnotherHost =>
                $"Run state: UNKNOWN — owner process {owner!.Pid} ran on host '{owner.Host}', and its liveness can only "
                + $"be checked there. {lastWrite}",
            _ =>
                "Run state: UNKNOWN — this journal names no owner process (it predates #704), so a live run and a dead "
                + $"one look the same here. {lastWrite}"
        };
    }

    /// <summary>
    /// The TASK column's width: the longest task id, never narrower than the header (issue #704). A fixed
    /// <c>,-32</c> pushed every longer id's row out of line — plan 40's <c>19-author-tests-overwatcher-autoresolve</c>
    /// is 39 characters — and a scripted parse of the table then produced nonsense counts. <c>run --dry-run</c>'s
    /// per-task table sizes its column by this same rule, so the two tables cannot size one plan's ids two ways.
    /// </summary>
    public static int TaskColumnWidth(IEnumerable<string> taskIds) =>
        taskIds.Select(id => id.Length).Append("TASK".Length).Max();

    /// <summary>
    /// One line per task that took at least one class-(b) transient pause (issue #515, SSOT §7
    /// <c>transientPauses[]</c>) — empty when none did, so the table stays noise-free on the overwhelming
    /// majority of runs.
    /// <para>
    /// This is the question the durable record now exists to answer: <i>did this run hit provider
    /// trouble?</i> A task that quietly paused six times and then went green used to be indistinguishable
    /// here from one that ran clean, because the pause reached the observer and nothing else.
    /// </para>
    /// <para>Ordered by task id (ORDINAL, like every other sort in this codebase) so the output is stable
    /// across platforms and locales — <c>document.Tasks</c> is a dictionary and carries no order of its own.
    /// Pure — public for the same reason <see cref="LastFailureText"/> is.</para>
    /// </summary>
    public static IReadOnlyList<string> TransientPauseLines(JournalDocument document) =>
    [
        .. document.Tasks
            .Where(pair => pair.Value.TransientPauses is { Count: > 0 })
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair =>
            {
                IReadOnlyList<TransientPauseRecord> pauses = pair.Value.TransientPauses!;
                int waited = (int)pauses.Sum(p => p.WaitSeconds);
                string last = pauses[^1].Reason;
                return $"  {pair.Key,-32} {pauses.Count} pause(s), {waited}s waited — last: {Truncate(last, 60)}";
            })
    ];

    private static void PrintRow(
        string taskId, int taskWidth, TaskJournalEntry? entry, RunLivenessState liveness, TextWriter output)
    {
        string task = taskId.PadRight(taskWidth);
        if (entry is null)
        {
            output.WriteLine($"  {task} {"(unknown)",-12} {"-",-9} {"-",-40} -");
            return;
        }

        AttemptRecord? last = entry.Attempts.Count == 0 ? null : entry.Attempts[^1];
        string failure = LastFailureText(last);
        string logDir = last?.LogDir ?? "-";

        output.WriteLine($"  {task} {StatusCell(entry.Status, liveness),-12} {entry.Attempts.Count,-9} {Truncate(failure, 40),-40} {logDir}");
    }

    /// <summary>
    /// The LAST FAILURE cell for a task's most recent attempt (<c>-</c> when there is none, or it succeeded).
    /// Pure — public for the same reason <see cref="RunCommand.Hyperlink"/> is: the Cli assembly ships no
    /// <c>InternalsVisibleTo</c>, so the mapping itself is the test seam.
    /// <para>Issue #485 lands HERE rather than in the STATUS column: <c>needs-human</c> is the journal status
    /// and the column is <c>,-12</c> with no room. This cell has a <c>,-40</c> budget, and it previously
    /// leaked the raw enum name (<c>NeedsHuman</c>) for an agent escalation — a pre-#485 defect fixed in the
    /// same edit. A retry-exhaustion halt records <c>GuardrailFailed</c>/<c>ActionFailed</c> on its last
    /// attempt, not <c>NeedsHuman</c>, so it never reaches this branch and is unaffected.</para>
    /// </summary>
    public static string LastFailureText(AttemptRecord? attempt)
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
            AttemptOutcome.NeedsHuman => NeedsHumanKinds.Parse(attempt.NeedsHumanKind) is { } kind
                ? $"agent escalated [{kind}]"
                : "agent escalated",
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

    /// <summary>
    /// The STATUS cell (issue #704): the journal's own word, except where the process table has just disproved it.
    /// A task the journal holds <c>running</c> under a run that is no longer going — its owner exited without
    /// finishing, or recorded an end without settling that task (a fault that unwound mid-attempt) — is not in
    /// progress, and prints <c>interrupted</c>. A resume still re-runs it, exactly as the footer says. Where
    /// liveness is UNKNOWN the journal's word stands: there is nothing to disprove it with.
    /// </summary>
    private static string StatusCell(JournalTaskStatus status, RunLivenessState liveness) =>
        status == JournalTaskStatus.Running
        && liveness is RunLivenessState.ExitedWithoutFinishing or RunLivenessState.Finished
            ? "interrupted"
            : StatusText(status);

    /// <summary>
    /// The tasks a resume would put back to <c>pending</c>, in plan order, each with the status cell the table
    /// printed for it (#639, #704).
    ///
    /// <para>
    /// Deliberately derived from <see cref="RunJournal.WouldResumeRun"/> rather than from a second list of
    /// statuses maintained here. Two copies of "which statuses survive a resume" is exactly how this
    /// command would come to disagree with the resume it is describing — and a status report that
    /// disagrees with the scheduler is worse than one that says nothing, because it is believed.
    /// </para>
    /// </summary>
    private static IReadOnlyList<string> ResumableTasks(
        Core.Model.PlanDefinition plan, JournalDocument document, RunLivenessState liveness)
    {
        var lines = new List<string>();
        foreach (Core.Model.TaskNode task in plan.Tasks)
        {
            if (document.Tasks.TryGetValue(task.Id, out TaskJournalEntry? entry)
                && RunJournal.WouldResumeRun(entry.Status))
            {
                lines.Add($"{task.Id} ({StatusCell(entry.Status, liveness)})");
            }
        }

        return lines;
    }

    /// <summary>
    /// A journal timestamp as the status line prints it: UTC, to the second, ISO-8601.
    /// </summary>
    private static string Timestamp(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>
    /// The folder as a shell takes it back: the resume command is printed to be pasted, so a path containing a
    /// space arrives quoted — the same rule <c>run</c> applies to its <c>guardrails logs</c> remedy.
    /// </summary>
    private static string PasteableFolder(string folder) =>
        folder.Contains(' ', StringComparison.Ordinal) ? $"\"{folder}\"" : folder;

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..(max - 1)] + "…";
}
