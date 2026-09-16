using System.CommandLine;
using System.Globalization;
using System.Text.Json;
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
        // (with its start identity) still in the process table — never from how long ago anything last moved.
        RunLivenessState liveness = RunLiveness.Assess(document.Owner, RunLiveness.ThisHost(), processProbe);
        IReadOnlyList<string> taskIds = [.. probe.Plan.Tasks.Select(task => task.Id)];

        output.WriteLine($"Run {document.RunId}  ({document.PlanHash})");
        output.WriteLine(LivenessLine(
            liveness,
            document.Owner,
            liveness == RunLivenessState.Ended ? EndedSummary(taskIds, document) : string.Empty,
            LastActivity(probe.Plan.PlanDirectory, journalPath, document),
            DateTimeOffset.UtcNow,
            folder));
        output.WriteLine();

        int taskWidth = TaskColumnWidth(taskIds);
        output.WriteLine($"  {"TASK".PadRight(taskWidth)} {"STATUS",-12} {"ATTEMPTS",-9} {"LAST FAILURE",-40} LOG DIR");
        output.WriteLine(new string('-', taskWidth + 88));

        // Print in plan (ordinal) order so the table matches the run order.
        foreach (Core.Model.TaskNode task in probe.Plan.Tasks)
        {
            document.Tasks.TryGetValue(task.Id, out TaskJournalEntry? entry);
            PrintRow(task.Id, taskWidth, entry, liveness, output);
        }

        // #722: the one thing the journal cannot say. A task the run has DEQUEUED, whose worktree git is
        // still running, is `pending` in the journal with no attempt directory — indistinguishable from a
        // task the run has not reached. That is exactly how plan 40's 28-hour dead run read here. The fact
        // lives in the event stream, so this tail-reads it and prints it as an OBSERVATION beside the
        // verdict above — never inside it: RunLiveness.Assess takes no clock and no task state, and this
        // block deliberately gives it nothing to take.
        IReadOnlyList<string> waiting = WaitingOnWorktreeLines(
            ReadEventLines(probe.Plan.PlanDirectory, document.RunId), taskWidth, DateTimeOffset.UtcNow, liveness);
        if (waiting.Count > 0)
        {
            output.WriteLine();
            output.WriteLine(WaitingOnWorktreeHeader(liveness));
            foreach (string line in waiting)
            {
                output.WriteLine(line);
            }
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

            // #704: a closing sentence that names statuses contradicts the list above it the moment a row prints as
            // `interrupted`, so it names none: the list IS the set a resume puts back to pending.
            output.WriteLine("Only 'succeeded' survives a resume; every task listed above becomes pending.");
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
    /// <paramref name="state"/> was assessed from, and <paramref name="endedSummary"/> the <see cref="EndedSummary"/>
    /// an ENDED line carries.
    /// <para>
    /// A RUNNING line names the next step for the one case "alive" does not cover — alive and not progressing (the
    /// scheduler hang, #722) — because the operator is told to read this line first and would otherwise be left with
    /// "alive" and no action.
    /// </para>
    /// <para>
    /// Every verdict short of ENDED also carries the run's last activity (<see cref="LastActivity"/>). That is an
    /// OBSERVATION for the operator and nothing more: it is what tells a live-but-stuck run from a live-and-busy one. It
    /// is kept out of the verdict itself on purpose, because a suspended laptop's clock keeps moving and "nothing for N
    /// hours" would condemn a healthy run that merely slept. ENDED omits it: the recorded end already says when the run
    /// stopped.
    /// </para>
    /// </summary>
    public static string LivenessLine(
        RunLivenessState state, RunOwner? owner, string endedSummary, DateTimeOffset lastActivity, DateTimeOffset now,
        string folder)
    {
        string activity =
            $"Last activity {Timestamp(lastActivity)} ({BreakdownProgress.FormatClock(now - lastActivity)} ago).";
        string resume = $"guardrails run {PasteableFolder(folder)}";

        return state switch
        {
            RunLivenessState.Running =>
                $"Run state: RUNNING — owner process {owner!.Pid} is alive. {activity} If it is not progressing, stop "
                + $"process {owner.Pid}, then resume with: {resume}",
            RunLivenessState.ExitedWithoutFinishing =>
                $"Run state: EXITED WITHOUT FINISHING — owner process {owner!.Pid} is gone and never recorded an end, "
                + $"so nothing is running. {activity} Resume with: {resume}",
            RunLivenessState.Ended =>
                $"Run state: ENDED at {Timestamp(owner!.FinishedAt!.Value)} — {endedSummary}; nothing is running.",
            RunLivenessState.OnAnotherHost =>
                $"Run state: UNKNOWN — owner process {owner!.Pid} ran on host '{owner.Host}', and its liveness can only "
                + $"be checked there. {activity}",
            RunLivenessState.CannotCheck =>
                $"Run state: UNKNOWN — owner process {owner!.Pid} could not be checked from here, so a live run and a "
                + $"dead one look the same. {activity}",
            _ =>
                "Run state: UNKNOWN — this journal names no owner process (it predates #704, or its run could not read "
                + $"its own identity), so a live run and a dead one look the same here. {activity}"
        };
    }

    /// <summary>
    /// What an ENDED run ended AS (issue #704), read from what the journal already records — because "ended" alone
    /// reads the same for a green delivery, a halt and a cancel, and the operator is told to read this line first. In
    /// precedence order:
    /// <list type="number">
    /// <item>a gate halt (<c>halt</c>) — it settles no task, so its own headline is the outcome;</item>
    /// <item>a task at <c>needs-human</c> or <c>failed</c> — the first in plan order, and how many more;</item>
    /// <item>a task still <c>running</c> — a fault unwound mid-attempt, so it was interrupted;</item>
    /// <item>a <c>pending</c> task whose last attempt was <c>cancelled</c> — the run was cancelled;</item>
    /// <item>every task <c>succeeded</c> — with the delivery outcome, so stranded work names its branch;</item>
    /// <item>otherwise — the run stopped before finishing (a declined drift prompt, a MAX_PATH preflight): how far it got.</item>
    /// </list>
    /// Pure and public for the same reason <see cref="LastFailureText"/> is. <paramref name="taskIds"/> is the plan's
    /// task order.
    /// </summary>
    public static string EndedSummary(IReadOnlyList<string> taskIds, JournalDocument document)
    {
        if (document.Halt is { } halt)
        {
            return $"halted: {halt.Headline}";
        }

        List<(string Id, TaskJournalEntry Entry)> entries =
        [
            .. taskIds
                .Where(document.Tasks.ContainsKey)
                .Select(id => (id, document.Tasks[id]))
        ];

        List<(string Id, TaskJournalEntry Entry)> halted =
            [.. entries.Where(task => task.Entry.Status is JournalTaskStatus.NeedsHuman or JournalTaskStatus.Failed)];
        if (halted.Count > 0)
        {
            return $"halted at {halted[0].Id} ({StatusText(halted[0].Entry.Status)}){AndMore(halted.Count)}";
        }

        List<string> interrupted =
            [.. entries.Where(task => task.Entry.Status == JournalTaskStatus.Running).Select(task => task.Id)];
        if (interrupted.Count > 0)
        {
            return $"interrupted at {interrupted[0]}{AndMore(interrupted.Count)}";
        }

        if (entries.Any(task => task.Entry.Status == JournalTaskStatus.Pending
                && task.Entry.Attempts is [.., { Outcome: AttemptOutcome.Cancelled }]))
        {
            return "cancelled";
        }

        int succeeded = entries.Count(task => task.Entry.Status == JournalTaskStatus.Succeeded);
        return succeeded == taskIds.Count
            ? $"all {taskIds.Count} task(s) succeeded{DeliverySuffix(document.Delivery)}{MachineDecisionSuffix(document)}"
            : $"stopped with {succeeded} of {taskIds.Count} task(s) succeeded";
    }

    /// <summary>
    /// What a wholly-green run does not get to hide (#361/#597): that a machine decision shaped it, or that an
    /// operator override delivered it past one. Without this, "all N task(s) succeeded, delivered to master" reads
    /// exactly like an ordinary green run — and the operator is told to read this line FIRST, while the journal
    /// already records both facts in <c>decisions[]</c> and <c>delivery.forcedPastDecision</c>.
    /// <para>
    /// Derived from the harness's OWN policy (<see cref="RunOutcomePolicy"/>), never a second reading of
    /// <c>decisions[]</c>, so this line cannot disagree with the interlock that acted on them — the #639 rule about
    /// one owner per rule, applied here.
    /// </para>
    /// <para>
    /// First match wins: a forced delivery names the decision it overrode (the most actionable fact, and the run's
    /// unreviewed waves still reach the operator through the end-of-run banner), then an unreviewed run's wave
    /// count, then any other machine decision — naming the judgment to go and check.
    /// </para>
    /// </summary>
    private static string MachineDecisionSuffix(JournalDocument document)
    {
        if (document.Delivery?.ForcedPastDecision is { } forced)
        {
            return $", delivered past a machine decision ({forced.Decision} at {forced.Subject})";
        }

        IReadOnlyList<DecisionEntry> decisions = document.Decisions ?? [];
        if (RunOutcomePolicy.ProceededUnreviewedWaveCount(decisions) is > 0 and var unreviewed)
        {
            return $", ran with {unreviewed} unreviewed wave(s)";
        }

        return RunOutcomePolicy.SuppressingDecision(decisions) is { } shaped
            ? $", shaped by a machine decision ({shaped.Decision} at {shaped.Subject})"
            : string.Empty;
    }

    /// <summary>
    /// When the run last visibly did something (issue #704) — the newest modification time of <c>run.json</c>, the
    /// run's event stream (<c>logs/&lt;runId&gt;/events.jsonl</c>), and the files of each RUNNING task's newest attempt
    /// directory (its stream log and friends). <c>run.json</c> alone moves only at task transitions, so on a long attempt
    /// its age said nothing about whether the run was moving. A settled task's logs are deliberately not read: they are
    /// not this run making progress.
    /// <para>DISPLAY ONLY. It never feeds <see cref="RunLiveness.Assess"/> — see the no-clock rule there.</para>
    /// </summary>
    public static DateTimeOffset LastActivity(string planDirectory, string journalPath, JournalDocument document)
    {
        string runLogs = Path.Combine(planDirectory, "logs", document.RunId);
        IEnumerable<string> sources =
        [
            journalPath,
            Path.Combine(runLogs, "events.jsonl"),
            .. document.Tasks
                .Where(pair => pair.Value.Status == JournalTaskStatus.Running)
                .SelectMany(pair => NewestAttemptFiles(Path.Combine(runLogs, pair.Key)))
        ];

        return sources.Where(File.Exists).Select(File.GetLastWriteTimeUtc).DefaultIfEmpty().Max();
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

    /// <summary>
    /// One line per task whose LAST event-stream row says it is waiting on the git that builds its worktree
    /// (issue #722, SSOT §8.1) — empty when none is, so an ordinary run's status stays noise-free.
    ///
    /// <para><b>The last row PER TASK decides, not the file's last line.</b> Under parallelism another
    /// task's rows land constantly; they say nothing about whether this one is still waiting. A waiting row
    /// followed by that same task's own <c>task-started</c> is history, and naming it would be a false lead
    /// during exactly the triage this exists to serve.</para>
    ///
    /// <para><b>An observation, and nothing more.</b> It reports what is being waited on and when that
    /// began, and applies NO threshold: the harness cannot tell a slow git from a hung one, which is the
    /// same reason the run-state verdict above takes no clock (#704). A reader may draw a conclusion from
    /// the elapsed time; nothing here does.</para>
    ///
    /// <para>Pure — public for the same reason <see cref="LastFailureText"/> is.</para>
    /// </summary>
    public static IReadOnlyList<string> WaitingOnWorktreeLines(
        IEnumerable<string> eventLines, int taskWidth, DateTimeOffset now, RunLivenessState liveness)
    {
        var lastByTask = new Dictionary<string, (string Kind, string? Operation, DateTimeOffset? At)>(StringComparer.Ordinal);
        foreach (string line in eventLines)
        {
            if (ParseRow(line) is not { } row)
            {
                continue;
            }

            lastByTask[row.TaskId] = (row.Kind, row.Operation, row.At);
        }

        return
        [
            .. lastByTask
                .Where(pair => string.Equals(pair.Value.Kind, RunEventStream.WaitingOnWorktreeKind, StringComparison.Ordinal))
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => WaitingLine(pair.Key, pair.Value.Operation, pair.Value.At, now, taskWidth, liveness))
        ];
    }

    /// <summary>
    /// The block's header, in the tense the process table supports. The same rule <see cref="StatusCell"/>
    /// follows: the recorded fact stands, except where liveness has just disproved the PRESENT TENSE of it.
    /// </summary>
    public static string WaitingOnWorktreeHeader(RunLivenessState liveness) =>
        RunIsOver(liveness)
            ? "Was waiting on a worktree when the run stopped (observation):"
            : "Waiting on a worktree (observation — the harness applies no time limit):";

    /// <summary>
    /// One waiting task's line. A row with no readable <c>at</c> still reports the wait — omitting the task
    /// because its timestamp was unreadable would hide the very thing this block exists to show.
    ///
    /// <para><b>Tense follows liveness.</b> A run whose owner is gone is not doing anything now, so a
    /// present-tense "creating a worktree … (3d02h ago)" beside <c>Run state: EXITED WITHOUT FINISHING</c>
    /// would assert activity the process table has already disproved — the same class of output this
    /// command's own <c>interrupted</c> cell exists to prevent. The fact is still worth printing: that a run
    /// died while building a worktree is a forensic lead, so the row is kept and only its tense changes.</para>
    /// </summary>
    private static string WaitingLine(
        string taskId, string? operation, DateTimeOffset? at, DateTimeOffset now, int taskWidth,
        RunLivenessState liveness)
    {
        string what = string.IsNullOrEmpty(operation) ? "a worktree operation" : operation;
        string when = (at, RunIsOver(liveness)) switch
        {
            ({ } started, false) => $"since {Timestamp(started)} ({BreakdownProgress.FormatClock(now - started)} ago)",
            ({ } started, true) => $"last recorded {Timestamp(started)}",
            (null, false) => "since an unrecorded time",
            (null, true) => "at an unrecorded time"
        };

        return $"  {taskId.PadRight(taskWidth)} {what} — {when}";
    }

    /// <summary>
    /// Whether the process table has established that this run is no longer going — the only two verdicts
    /// that disprove the present tense. Every UNKNOWN verdict leaves it alone: not being able to check is
    /// not evidence of death (#704).
    /// </summary>
    private static bool RunIsOver(RunLivenessState liveness) =>
        liveness is RunLivenessState.ExitedWithoutFinishing or RunLivenessState.Ended;

    /// <summary>
    /// The three fields this command reads off one <c>events.jsonl</c> line, or null when the line is not a
    /// task-scoped row it can use. A malformed or half-written line is SKIPPED rather than fatal: the run
    /// appends to this file while the command reads it, so a torn last line is normal, and a status command
    /// that threw over one would fail exactly when it is most needed.
    /// </summary>
    private static (string Kind, string TaskId, string? Operation, DateTimeOffset? At)? ParseRow(string line)
    {
        if (line.Length == 0)
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement row = document.RootElement;
            if (row.ValueKind != JsonValueKind.Object
                || row.TryGetProperty("kind", out JsonElement kind) is false || kind.ValueKind != JsonValueKind.String
                || row.TryGetProperty("taskId", out JsonElement taskId) is false || taskId.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            string? operation = row.TryGetProperty("operation", out JsonElement op) && op.ValueKind == JsonValueKind.String
                ? op.GetString()
                : null;
            DateTimeOffset? at = row.TryGetProperty("at", out JsonElement atValue)
                                 && atValue.TryGetDateTimeOffset(out DateTimeOffset parsed)
                ? parsed
                : null;

            return (kind.GetString()!, taskId.GetString()!, operation, at);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// This run's event-stream lines, or empty when there is no stream (every halt that returns before the
    /// observer chain exists writes none — SSOT §8.1). Display-only: an unreadable stream costs the
    /// observation, never the command.
    /// </summary>
    private static IReadOnlyList<string> ReadEventLines(string planDirectory, string runId)
    {
        try
        {
            string path = Path.Combine(planDirectory, "logs", runId, "events.jsonl");
            return File.Exists(path) ? File.ReadAllLines(path) : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

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
        && liveness is RunLivenessState.ExitedWithoutFinishing or RunLivenessState.Ended
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

    /// <summary>The newest-numbered <c>attempt-N</c> directory's files under one task's log directory; empty when there is none.</summary>
    private static IReadOnlyList<string> NewestAttemptFiles(string taskLogs)
    {
        try
        {
            if (!Directory.Exists(taskLogs))
            {
                return [];
            }

            string? newest = Directory.EnumerateDirectories(taskLogs, "attempt-*")
                .Select(dir => (Dir: dir, Number: AttemptNumber(dir)))
                .Where(attempt => attempt.Number >= 0)
                .OrderByDescending(attempt => attempt.Number)
                .Select(attempt => attempt.Dir)
                .FirstOrDefault();

            return newest is null ? [] : [.. Directory.EnumerateFiles(newest)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return []; // display-only: an unreadable log directory costs a less precise age, never the command
        }
    }

    private static int AttemptNumber(string attemptDir) =>
        int.TryParse(Path.GetFileName(attemptDir)["attempt-".Length..], NumberStyles.None, CultureInfo.InvariantCulture, out int number)
            ? number
            : -1;

    private static string AndMore(int count) => count > 1 ? $" and {count - 1} more" : string.Empty;

    /// <summary>
    /// How a wholly-green run's delivery reads on the ENDED line: where the work went, and — for the one outcome that
    /// strands it — which branch it is sitting on. Nothing when there was nothing to deliver (serial mode) or no record.
    /// </summary>
    private static string DeliverySuffix(DeliverySection? delivery) => delivery switch
    {
        null => string.Empty,
        { Delivered: true, DeliveredToBranch: { } branch } => $", delivered to {branch}",
        { Delivered: true } => ", delivered",
        { Outcome: DeliveryOutcome.PartiallyDelivered } => ", partially delivered",
        { Outcome: DeliveryOutcome.NotAttempted, PlanBranch: { } planBranch } =>
            $", NOT delivered — the work is on {planBranch}",
        { Outcome: DeliveryOutcome.NotAttempted } => string.Empty,
        { Outcome: var refused } => $", not delivered ({JournalJson.DeliveryOutcomeToken(refused)})"
    };

    /// <summary>
    /// A timestamp as the status line prints it: UTC, to the second, ISO-8601.
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
