using System.Text.Json;
using Guardrails.Core.Model;
using Guardrails.Core.State;

namespace Guardrails.Core.Journal;

/// <summary>
/// Owns <c>state/run.json</c> (SSOT §7): the durable record of per-task status and
/// attempts that makes resume possible. The journal is the single authority on the merge
/// sequence counter (<see cref="NextMergeSequence"/>) and on attempt numbering. Every
/// transition is persisted atomically, so a crash at any point leaves a readable journal
/// that resume can reason about.
///
/// Mutation is guarded by a lock so the M4 scheduler can record task completions from
/// multiple worker loops without corrupting the file or double-issuing a merge sequence.
/// </summary>
public sealed class RunJournal : Execution.ISchedulerJournal
{
    private readonly string _journalPath;
    private readonly object _gate = new();
    private JournalDocument _document;

    private RunJournal(string journalPath, JournalDocument document)
    {
        _journalPath = journalPath;
        _document = document;
    }

    /// <summary>Absolute path to <c>state/run.json</c>.</summary>
    public string JournalPath => _journalPath;

    /// <summary>True if a previous journal existed and its plan hash differed from the current plan.</summary>
    public bool PlanHashMismatch { get; private init; }

    /// <summary>The previous run's plan hash when <see cref="PlanHashMismatch"/> is true; else null.</summary>
    public string? PreviousPlanHash { get; private init; }

    /// <summary>A read-only snapshot of the current journal document.</summary>
    public JournalDocument Document
    {
        get { lock (_gate) { return _document; } }
    }

    /// <summary>Compute the path to <c>state/run.json</c> for a plan directory.</summary>
    public static string PathFor(string planDirectory) =>
        Path.Combine(planDirectory, "state", "run.json");

    /// <summary>
    /// Load the journal for <paramref name="plan"/>, or create a fresh one if none exists.
    /// On load, applies the SSOT §7 resume rules and seeds every plan task that the journal
    /// does not yet mention as <c>pending</c>. The returned journal is persisted (so a fresh
    /// run.json exists immediately, and resume normalization is durable).
    /// </summary>
    public static RunJournal LoadOrCreate(PlanDefinition plan)
    {
        string journalPath = PathFor(plan.PlanDirectory);
        string currentHash = Journal.PlanHash.Compute(plan);

        if (!File.Exists(journalPath))
        {
            var fresh = new JournalDocument
            {
                RunId = NewRunId(),
                PlanHash = currentHash,
                NextMergeSequence = 1,
                Tasks = SeedPendingTasks(plan, existing: null)
            };
            var journal = new RunJournal(journalPath, fresh);
            journal.Persist();
            return journal;
        }

        JournalDocument loaded = Read(journalPath);
        bool mismatch = !string.Equals(loaded.PlanHash, currentHash, StringComparison.Ordinal);

        JournalDocument resumed = loaded with
        {
            PlanHash = currentHash, // adopt the current hash going forward
            Tasks = ApplyResumeRules(plan, loaded.Tasks)
        };

        var resumedJournal = new RunJournal(journalPath, resumed)
        {
            PlanHashMismatch = mismatch,
            PreviousPlanHash = mismatch ? loaded.PlanHash : null
        };
        resumedJournal.Persist();
        return resumedJournal;
    }

    /// <summary>The current status of a task (defaults to <see cref="TaskStatus.Pending"/> if unknown).</summary>
    public TaskStatus StatusOf(string taskId)
    {
        lock (_gate)
        {
            return _document.Tasks.TryGetValue(taskId, out TaskJournalEntry? entry)
                ? entry.Status
                : TaskStatus.Pending;
        }
    }

    /// <summary>
    /// The <c>TaskDefinitionHash</c> recorded at a task's most recent successful settle (SSOT §7.2,
    /// issue #274 Part A), or null when none was recorded (a task never settled, or an entry predating
    /// this field — treated as "unknown, assume unchanged" by the resume drift check).
    /// </summary>
    public string? RecordedDefinitionHash(string taskId)
    {
        lock (_gate)
        {
            return _document.Tasks.TryGetValue(taskId, out TaskJournalEntry? entry)
                ? entry.DefinitionHash
                : null;
        }
    }

    /// <summary>
    /// The run's cumulative journaled cost (SSOT §7), summed across every recorded attempt of
    /// every task via <see cref="JournalCost.Total"/>. Drives the per-run cost cap
    /// (<see cref="Model.RunConfig.MaxCostUsd"/>); the total is cumulative across resumes because it
    /// reads the durable journal. A deterministic-only run records no cost, which reads as $0 so an
    /// uncapped-cost run never trips a cap.
    /// </summary>
    public decimal CurrentCostUsd()
    {
        lock (_gate)
        {
            return JournalCost.Total(_document) ?? 0m;
        }
    }

    /// <summary>The next attempt number for a task: one past the highest recorded attempt.</summary>
    public int NextAttemptNumber(string taskId)
    {
        lock (_gate)
        {
            if (!_document.Tasks.TryGetValue(taskId, out TaskJournalEntry? entry) || entry.Attempts.Count == 0)
            {
                return 1;
            }

            return entry.Attempts.Max(a => a.Attempt) + 1;
        }
    }

    /// <summary>The next merge sequence the journal will issue (without consuming it).</summary>
    public long NextMergeSequence
    {
        get { lock (_gate) { return _document.NextMergeSequence; } }
    }

    /// <summary>Set a task to <see cref="TaskStatus.Running"/> and persist (SSOT §7 transition).</summary>
    public void MarkRunning(string taskId)
    {
        lock (_gate)
        {
            TaskJournalEntry entry = GetOrCreate(taskId);
            UpdateTask(taskId, entry with { Status = TaskStatus.Running });
            Persist();
        }
    }

    /// <summary>
    /// Set a task to <see cref="TaskStatus.Blocked"/> and persist. A blocked task never ran,
    /// so no attempt is recorded (SSOT §7: <c>attempts</c> are real attempts).
    /// </summary>
    public void MarkBlocked(string taskId)
    {
        lock (_gate)
        {
            TaskJournalEntry entry = GetOrCreate(taskId);
            UpdateTask(taskId, entry with { Status = TaskStatus.Blocked });
            Persist();
        }
    }

    /// <summary>
    /// Record a completed attempt and set the task's terminal status, persisting atomically.
    /// When <paramref name="mergeSequence"/> is non-null the merge counter is advanced and
    /// the sequence stored on the task (the merge already happened in <see cref="StateManager"/>).
    /// </summary>
    public void RecordAttempt(
        string taskId, AttemptRecord attempt, TaskStatus newStatus, long? mergeSequence = null,
        string? definitionHash = null)
    {
        lock (_gate)
        {
            TaskJournalEntry entry = GetOrCreate(taskId);
            var attempts = new List<AttemptRecord>(entry.Attempts) { attempt };

            TaskJournalEntry updated = entry with
            {
                Status = newStatus,
                Attempts = attempts,
                MergeSequence = mergeSequence ?? entry.MergeSequence,
                // Stamp the definition hash on success (§7.2); a null preserves any prior hash so a
                // failed attempt never clears a previously-recorded one.
                DefinitionHash = definitionHash ?? entry.DefinitionHash
            };

            UpdateTask(taskId, updated);

            if (mergeSequence is not null)
            {
                _document = _document with { NextMergeSequence = Math.Max(_document.NextMergeSequence, mergeSequence.Value + 1) };
            }

            Persist();
        }
    }

    /// <summary>
    /// Reserve the next merge sequence (advancing the counter) so a fragment merge can be
    /// stamped with it. The caller passes it to <see cref="StateManager.MergeFragment"/> and
    /// then to <see cref="RecordAttempt"/>. Reserving up front keeps the counter monotonic
    /// even if the eventual merge writes the journal a moment later.
    /// </summary>
    public long ReserveMergeSequence()
    {
        lock (_gate)
        {
            long sequence = _document.NextMergeSequence;
            _document = _document with { NextMergeSequence = sequence + 1 };
            Persist();
            return sequence;
        }
    }

    /// <summary>
    /// Record the terminal settle of a worktree task: update the task's Status and optionally
    /// MergeSequence WITHOUT adding an AttemptRecord. Also advances NextMergeSequence when
    /// mergeSequence is set. Called by the Scheduler under the integration lock (B1 step 3).
    /// </summary>
    public void RecordSettle(
        string taskId, TaskStatus status, long? mergeSequence = null, string? definitionHash = null)
    {
        lock (_gate)
        {
            TaskJournalEntry entry = GetOrCreate(taskId);
            TaskJournalEntry updated = entry with
            {
                Status = status,
                MergeSequence = mergeSequence ?? entry.MergeSequence,
                DefinitionHash = definitionHash ?? entry.DefinitionHash
            };
            UpdateTask(taskId, updated);

            if (mergeSequence is not null)
            {
                _document = _document with
                {
                    NextMergeSequence = Math.Max(_document.NextMergeSequence, mergeSequence.Value + 1)
                };
            }

            Persist();
        }
    }

    /// <summary>
    /// Record the successful settle of a worktree task (issue #196): append <paramref name="attempt"/>
    /// to the task's attempt list AND set Status + MergeSequence atomically. The worktree success path
    /// defers the attempt record to this settle (serial mode records inline via
    /// <see cref="RecordAttempt"/>), so a succeeded worktree task journals the SAME populated
    /// <c>attempts[]</c> shape a succeeded serial task does (SSOT §7). Called by the Scheduler under the
    /// integration lock (B1 step 3), replacing the attempt-less <see cref="RecordSettle"/> on the
    /// success branches.
    /// </summary>
    public void RecordSettleWithAttempt(
        string taskId, AttemptRecord attempt, TaskStatus status, long? mergeSequence = null,
        string? definitionHash = null)
    {
        lock (_gate)
        {
            TaskJournalEntry entry = GetOrCreate(taskId);
            var attempts = new List<AttemptRecord>(entry.Attempts) { attempt };

            TaskJournalEntry updated = entry with
            {
                Status = status,
                Attempts = attempts,
                MergeSequence = mergeSequence ?? entry.MergeSequence,
                DefinitionHash = definitionHash ?? entry.DefinitionHash
            };
            UpdateTask(taskId, updated);

            if (mergeSequence is not null)
            {
                _document = _document with
                {
                    NextMergeSequence = Math.Max(_document.NextMergeSequence, mergeSequence.Value + 1)
                };
            }

            Persist();
        }
    }

    /// <summary>Force a task back to <see cref="TaskStatus.Pending"/> (keeping attempt history) and persist.</summary>
    public void ResetTask(string taskId)
    {
        lock (_gate)
        {
            TaskJournalEntry entry = GetOrCreate(taskId);
            UpdateTask(taskId, entry with { Status = TaskStatus.Pending });
            Persist();
        }
    }

    /// <summary>
    /// Part C (issue #274, SSOT §7.2): the journal half of a safe-drift resolution — force <paramref name="taskId"/>
    /// back to <see cref="TaskStatus.Pending"/> so the next wave re-runs it. Delegates to
    /// <see cref="ResetTask"/> (the ISchedulerJournal seam so the Scheduler can reset without the
    /// concrete type).
    /// </summary>
    public void ResetTaskToPending(string taskId) => ResetTask(taskId);

    /// <summary>
    /// SSOT §2.1/§7: append <paramref name="entry"/> to the durable, unified top-level <c>decisions[]</c>
    /// section and persist. Additive — the section stays absent until the first decision (never <c>null</c>
    /// noise).
    /// </summary>
    public void RecordDecision(Execution.DecisionEntry entry)
    {
        lock (_gate)
        {
            var decisions = new List<Execution.DecisionEntry>(_document.Decisions ?? []) { entry };
            _document = _document with { Decisions = decisions };
            Persist();
        }
    }

    // --- internals --------------------------------------------------------------------

    private TaskJournalEntry GetOrCreate(string taskId)
    {
        if (_document.Tasks.TryGetValue(taskId, out TaskJournalEntry? entry))
        {
            return entry;
        }

        return new TaskJournalEntry { Status = TaskStatus.Pending };
    }

    private void UpdateTask(string taskId, TaskJournalEntry entry)
    {
        var tasks = new Dictionary<string, TaskJournalEntry>(_document.Tasks, StringComparer.Ordinal)
        {
            [taskId] = entry
        };
        _document = _document with { Tasks = tasks };
    }

    private void Persist()
    {
        string json = JsonSerializer.Serialize(_document, JournalJson.Options);
        AtomicFile.WriteAllText(_journalPath, json);
    }

    private static JournalDocument Read(string journalPath) => JournalReader.Read(journalPath);

    /// <summary>
    /// Apply the SSOT §7 resume rules to every task: <c>succeeded</c> stays terminal;
    /// <c>needs-human</c>/<c>failed</c>/<c>blocked</c> → <c>pending</c> (fresh budget);
    /// crashed <c>running</c> → <c>pending</c> (attempt numbering continues, so attempts are
    /// preserved). Plan tasks absent from the journal are seeded <c>pending</c>.
    /// </summary>
    private static IReadOnlyDictionary<string, TaskJournalEntry> ApplyResumeRules(
        PlanDefinition plan,
        IReadOnlyDictionary<string, TaskJournalEntry> existing)
    {
        var result = new Dictionary<string, TaskJournalEntry>(StringComparer.Ordinal);

        // Carry over and normalize tasks the journal already knows.
        foreach (KeyValuePair<string, TaskJournalEntry> pair in existing)
        {
            result[pair.Key] = pair.Value with { Status = ResumeStatus(pair.Value.Status) };
        }

        // Seed any plan task the journal has never seen.
        foreach (TaskNode task in plan.Tasks)
        {
            if (!result.ContainsKey(task.Id))
            {
                result[task.Id] = new TaskJournalEntry { Status = TaskStatus.Pending };
            }
        }

        return result;
    }

    private static TaskStatus ResumeStatus(TaskStatus current) => current switch
    {
        TaskStatus.Succeeded => TaskStatus.Succeeded,            // terminal — skipped on resume
        TaskStatus.Pending => TaskStatus.Pending,
        // needs-human / failed / blocked / running (crash) all become pending; attempt
        // numbering continues because attempt history is preserved.
        _ => TaskStatus.Pending
    };

    private static IReadOnlyDictionary<string, TaskJournalEntry> SeedPendingTasks(
        PlanDefinition plan,
        IReadOnlyDictionary<string, TaskJournalEntry>? existing)
    {
        var tasks = existing is null
            ? new Dictionary<string, TaskJournalEntry>(StringComparer.Ordinal)
            : new Dictionary<string, TaskJournalEntry>(existing, StringComparer.Ordinal);

        foreach (TaskNode task in plan.Tasks)
        {
            if (!tasks.ContainsKey(task.Id))
            {
                tasks[task.Id] = new TaskJournalEntry { Status = TaskStatus.Pending };
            }
        }

        return tasks;
    }

    private static string NewRunId()
    {
        string timestamp = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH-mm-ssZ");
        string suffix = Guid.NewGuid().ToString("N")[..4];
        return $"{timestamp}-{suffix}";
    }
}
