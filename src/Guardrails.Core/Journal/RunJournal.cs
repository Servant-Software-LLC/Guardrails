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

    /// <summary>
    /// This run's id — the <c>logs/&lt;runId&gt;/</c> tree every attempt and gate writes its artifacts
    /// under. Exposed on the <see cref="Execution.ISchedulerJournal"/> surface (issue #432) so the
    /// Scheduler can locate a wave gate's capture directory without reaching for the whole document.
    /// </summary>
    public string RunId => Document.RunId;

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
            Tasks = ApplyResumeRules(plan, loaded.Tasks),
            // Issue #432: the halt section describes why THIS run stopped. A resume is a new attempt at
            // reaching green, so a halt carried over from the previous one would be read as current. Clear
            // it; a gate that fails again re-records it (the per-gate planPreflights/planGuardrails/waves
            // markers are NOT cleared — those are the durable per-phase record resume reasons about).
            Halt = null
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
    /// Every task with a recorded settle definition hash, as <c>task id → recorded hash</c> (issue #322,
    /// SSOT §7.2) — the single-writer provenance the safe-suffix rewind corroborates a commit's
    /// <c>Guardrails-Task-Hash:</c> trailer against. A never-settled task (no recorded hash) is simply absent,
    /// so a forged trailer for it corroborates against nothing and is refused. Reads only the journal, never
    /// a branch trailer (that would be circular).
    /// </summary>
    public IReadOnlyDictionary<string, string> RecordedDefinitionHashes()
    {
        lock (_gate)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, TaskJournalEntry> pair in _document.Tasks)
            {
                if (pair.Value.DefinitionHash is { } hash)
                {
                    map[pair.Key] = hash;
                }
            }

            return map;
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

    /// <summary>
    /// The attempts recorded for a task so far, in journal order (empty when the task has none yet).
    /// A read-only projection for the harness's own supervisory prompts (issue #452): the #269
    /// overwatcher composes its diagnose brief from the outcomes the harness ALREADY knows —
    /// per-attempt outcome + failed-guardrail names — so the judge is handed the deterministic
    /// evidence rather than made to reconstruct it by reading logs it may not be able to find.
    /// </summary>
    internal IReadOnlyList<AttemptRecord> AttemptsFor(string taskId)
    {
        lock (_gate)
        {
            return _document.Tasks.TryGetValue(taskId, out TaskJournalEntry? entry)
                ? [.. entry.Attempts]
                : [];
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
        string? definitionHash = null, string? definitionHashAtSettle = null, string? bucket = null)
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
                DefinitionHash = definitionHash ?? entry.DefinitionHash,
                DefinitionHashAtSettle = definitionHashAtSettle ?? entry.DefinitionHashAtSettle,
                // Plan 30 §3.2: the task-fingerprint bucket, same null-preserves-prior-value rule as the
                // hashes above. The bucket is TASK grain — both its inputs (writeScope, guardrail
                // archetypes) are constant across a task's own retries — so a later call passing nothing
                // must never CLEAR what an earlier attempt of the same task recorded.
                Bucket = bucket ?? entry.Bucket
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
    /// SSOT §7 (issue #515): append one class-(b) transient PAUSE to the task's
    /// <see cref="TaskJournalEntry.TransientPauses"/> log and persist immediately.
    /// <para>
    /// Called from the pause site itself, BEFORE the backoff is awaited, so a run killed mid-wait still
    /// records that it was waiting and why. This is the ONLY durable record of a transient that RESOLVES —
    /// the exhausted path writes an <see cref="AttemptRecord"/>, the resolving one writes nothing else at
    /// all.
    /// </para>
    /// <para>
    /// It deliberately does NOT touch the task's <see cref="TaskJournalEntry.Status"/>: a pause is not a
    /// state transition. The task is mid-attempt and stays <c>running</c>; writing a status here would make
    /// a paused task look settled to a concurrent reader (<c>guardrails status</c> runs against a live
    /// journal).
    /// </para>
    /// </summary>
    public void RecordTransientPause(string taskId, TransientPauseRecord pause)
    {
        lock (_gate)
        {
            TaskJournalEntry entry = GetOrCreate(taskId);
            var pauses = new List<TransientPauseRecord>(entry.TransientPauses ?? []) { pause };
            UpdateTask(taskId, entry with { TransientPauses = pauses });
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
        string taskId, TaskStatus status, long? mergeSequence = null, string? definitionHash = null,
        string? definitionHashAtSettle = null, string? bucket = null)
    {
        lock (_gate)
        {
            TaskJournalEntry entry = GetOrCreate(taskId);
            TaskJournalEntry updated = entry with
            {
                Status = status,
                MergeSequence = mergeSequence ?? entry.MergeSequence,
                DefinitionHash = definitionHash ?? entry.DefinitionHash,
                DefinitionHashAtSettle = definitionHashAtSettle ?? entry.DefinitionHashAtSettle,
                // Plan 30 §3.2 — see RecordAttempt: null preserves the prior bucket, never clears it.
                Bucket = bucket ?? entry.Bucket
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
    /// <see cref="Execution.ISchedulerJournal"/> still declares <see cref="RecordSettle"/> at its pre-plan-32
    /// 4-parameter arity, and the Scheduler calls it through that interface-typed field. Adding
    /// <paramref name="definitionHashAtSettle"/>'s optional 5th parameter to the public overload above changed
    /// its arity, which stops it matching the interface's default-bodied member — without this explicit
    /// forwarder, every Scheduler call would silently dispatch to the interface's NO-OP default instead of the
    /// real implementation above. Widening the interface itself belongs to the task that wires a caller to
    /// actually pass <c>definitionHashAtSettle</c> (plan 32 §6.3/§15); until then this keeps dispatch correct.
    /// Plan 30 §3.2's optional <c>bucket</c> parameter widened the public overload again for the same
    /// reason and is covered by the same forwarder — every parameter it does not name simply defaults.
    /// </summary>
    void Execution.ISchedulerJournal.RecordSettle(
        string taskId, TaskStatus status, long? mergeSequence, string? definitionHash) =>
        RecordSettle(taskId, status, mergeSequence, definitionHash);

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
        string? definitionHash = null, string? definitionHashAtSettle = null, string? bucket = null)
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
                DefinitionHash = definitionHash ?? entry.DefinitionHash,
                DefinitionHashAtSettle = definitionHashAtSettle ?? entry.DefinitionHashAtSettle,
                // Plan 30 §3.2 — see RecordAttempt: null preserves the prior bucket, never clears it.
                Bucket = bucket ?? entry.Bucket
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
    /// See <see cref="Execution.ISchedulerJournal.RecordSettle"/>'s forwarder above — same reason.
    /// <para>
    /// Plan 30 §3.2 widened the INTERFACE member with an optional <c>bucket</c>, so this forwarder is
    /// re-arity'd to match it (an explicit implementation whose signature matches no interface member is
    /// a hard CS0539, not a silently-defaulted argument). The bucket is forwarded BY NAME because the
    /// two parameter lists do not line up: the interface member has no <c>definitionHashAtSettle</c>, so
    /// the bucket sits one position earlier there than on the public overload below — a positional
    /// forward would land it in <c>definitionHashAtSettle</c>, which compiles silently and stamps a
    /// bucket string into a hash field while every worktree run journals no bucket at all.
    /// </para>
    /// </summary>
    void Execution.ISchedulerJournal.RecordSettleWithAttempt(
        string taskId, AttemptRecord attempt, TaskStatus status, long? mergeSequence, string? definitionHash,
        string? bucket) =>
        RecordSettleWithAttempt(taskId, attempt, status, mergeSequence, definitionHash, bucket: bucket);

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

    /// <summary>
    /// Allocate the next durably-MONOTONIC, never-reused escalation <c>seq</c> for this run (doc 12 §7.1,
    /// Finding 5) — the run-level counter <see cref="Execution.FileEscalationSink"/> stamps onto each
    /// <c>logs/&lt;runId&gt;/escalations/&lt;seq&gt;-&lt;gate&gt;.json</c> record and the returned
    /// <c>EscalationId</c>. It is PERSISTED run-level state — a small companion of <c>run.json</c> in the same
    /// <c>state/</c> directory — and is NOT derived from the <c>escalations/</c> directory listing: so it keeps
    /// climbing across a resume (a second <see cref="LoadOrCreate"/> re-reads the same run) and even after the
    /// on-disk records are wiped, and a stale unconsumed answer can never bind a LATER escalation that reused
    /// the same <c>{ runId, seq, gate, subject }</c> tuple (§7.1). Advances and persists atomically under the
    /// same journal lock as every other mutation, so concurrent escalations never double-issue a seq.
    /// </summary>
    public int NextEscalationSeq()
    {
        lock (_gate)
        {
            int next = ReadEscalationSeq() + 1;
            WriteEscalationSeq(next);
            return next;
        }
    }

    /// <summary>The escalation seq counter's on-disk home: a companion of <c>run.json</c> in the same <c>state/</c> dir.</summary>
    private string EscalationSeqPath =>
        Path.Combine(Path.GetDirectoryName(_journalPath)!, "escalation-seq.json");

    /// <summary>Read the highest seq allocated so far (0 when the counter has never been written), tolerating a missing/corrupt file.</summary>
    private int ReadEscalationSeq()
    {
        string path = EscalationSeqPath;
        if (!File.Exists(path))
        {
            return 0;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.TryGetProperty("lastSeq", out JsonElement value) && value.TryGetInt32(out int seq)
                ? seq
                : 0;
        }
        catch (JsonException)
        {
            // A corrupt/empty counter file must never crash an escalation; restart the counter (the
            // escalations/ records + decisions[] audit remain the human-visible trail regardless).
            return 0;
        }
    }

    /// <summary>Persist the highest seq allocated so far, atomically (same-volume rename via <see cref="AtomicFile"/>).</summary>
    private void WriteEscalationSeq(int lastSeq) =>
        AtomicFile.WriteAllText(EscalationSeqPath, JsonSerializer.Serialize(new { lastSeq }, JournalJson.Options));

    /// <summary>
    /// Charge OVERHEAD prompt spend that is not a task attempt (SSOT §7/§9.2, issues #269/#314) — the
    /// overwatcher's diagnose prompts, the AI-merge worker, and the terminal needs-human triage — to the
    /// run's cumulative cost. It is folded into <see cref="JournalCost.Total"/> so it BOTH counts toward the
    /// <c>maxCostUsd</c> gate (via <see cref="CurrentCostUsd"/>) AND appears in the reported total. A null
    /// cost is a no-op (an unreported prompt cost adds nothing and leaves the section absent); a non-null
    /// cost (even $0) is accumulated and persisted.
    /// </summary>
    public void AddOverheadCost(decimal? cost)
    {
        if (cost is not { } c)
        {
            return;
        }

        lock (_gate)
        {
            _document = _document with { OverheadCostUsd = (_document.OverheadCostUsd ?? 0m) + c };
            Persist();
        }
    }

    // Issue #419: RecordWorktreeJunctionRoot is REMOVED. The Windows short-junction is a process-scoped cwd
    // alias (WorktreeJunctionLifetime), not resume state — nothing to persist.

    /// <summary>
    /// Re-baseline a drifted task's recorded definition hash to <paramref name="newHash"/> WITHOUT re-running
    /// it — the accept-and-continue half of the definition-drift prompt (issue #545, SSOT §7.2).
    /// <para>
    /// <b>What this deliberately does NOT touch.</b> Status, attempts, merge sequence and every other field
    /// stay exactly as they were: the task really did succeed, its output really is on the plan branch, and
    /// the only thing that changed is which definition the journal says that output was produced against.
    /// Re-running is the OTHER branch of the prompt; conflating them here would make an accept silently
    /// re-do work the operator chose not to re-do.
    /// </para>
    /// <para>
    /// <b>The honesty cost, stated where the method lives.</b> After this call the recorded hash matches the
    /// current one, so the task reads as cleanly green and nothing in <c>tasks{}</c> distinguishes it from a
    /// task that was actually built against this definition. That is why the caller MUST also append a
    /// <see cref="Execution.DecisionTokens.DriftAccepted"/> entry to <c>decisions[]</c> — that entry is the
    /// only durable record that a trade was made, and it is what the end-of-run report reads.
    /// </para>
    /// </summary>
    public void RecordDriftAccepted(string taskId, string newHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ArgumentException.ThrowIfNullOrWhiteSpace(newHash);

        lock (_gate)
        {
            if (!_document.Tasks.TryGetValue(taskId, out TaskJournalEntry? entry))
            {
                return; // nothing recorded for this task - there is no baseline to move.
            }

            UpdateTask(taskId, entry with { DefinitionHash = newHash });
            Persist();
        }
    }

    /// <summary>
    /// Record the machine-readable reason the run STOPPED at a deterministic gate (SSOT §7, issue #432).
    /// Overwrites any previous halt — a run stops once, and the LAST gate to fail is the one that stopped
    /// it. Nothing else about the journal is touched, so the per-gate phase markers (which carry the full
    /// per-check detail) remain the authority on what each gate found.
    /// </summary>
    public void RecordHalt(RunHalt halt)
    {
        ArgumentNullException.ThrowIfNull(halt);

        lock (_gate)
        {
            _document = _document with { Halt = halt };
            Persist();
        }
    }

    /// <summary>
    /// Record whether this run's verified work reached the user's branch, and if not, why not (SSOT §7,
    /// issue #542). Called once at the end of a run, after the delivery decision has fully resolved —
    /// including the deferred path, where delivery waits on the terminal gate's verdict
    /// (<see cref="Execution.RunReport.DeliveryPendingTerminalGate"/>), so an early write would record
    /// "not delivered" for a run that then delivered.
    /// <para>
    /// Overwrites any previous record for the same reason <see cref="RecordHalt"/> does: a run delivers
    /// once, and a resume that gets further is the authority on what finally happened.
    /// </para>
    /// </summary>
    public void RecordDelivery(DeliverySection delivery)
    {
        ArgumentNullException.ThrowIfNull(delivery);

        lock (_gate)
        {
            // RE-READ FROM DISK FIRST — this method is unlike every other Record* on this type, and getting
            // it wrong is destructive rather than merely wrong.
            //
            // The others are called DURING the run by the component that owns this instance, so its
            // in-memory document is current. The delivery is recorded by the CLI at the very END, from an
            // instance created BEFORE the run started (RunCommand's `journal`), while tasks are settled
            // through the Scheduler's own journal. This instance's document is therefore stale — still all
            // `pending` — and a plain `_document with { … }` + Persist() serializes that stale document over
            // the real one, silently reverting every task, attempt and gate result on disk.
            //
            // That is not hypothetical: it reverted 26 integration tests to `Pending` on the first cut of
            // this method. Re-reading makes the write additive: take what is actually on disk, add the one
            // field, put it back.
            JournalDocument current = File.Exists(_journalPath) ? Read(_journalPath) : _document;
            _document = current with { Delivery = delivery };
            Persist();
        }
    }

    /// <summary>
    /// Record the machine, concurrency and version profile probed once for this run (plan 30 §3.4,
    /// <see cref="RunEnvironmentProbe"/>) — called by the CLI at run START, right after
    /// <see cref="LoadOrCreate"/> resolves the run id and BEFORE the Scheduler's own, LATER
    /// <see cref="LoadOrCreate"/> (reached when it builds the executor) — that ordering is load-bearing:
    /// a stamp placed after the second load would be silently overwritten by a document read before the
    /// stamp existed.
    /// <para>
    /// RE-READS FROM DISK FIRST, exactly like <see cref="RecordDelivery"/> and for the same reason: this
    /// call site's in-memory document happens to be current, but every document-level recorder on this
    /// type follows the re-read-first shape so a future call site moved later — where staleness would
    /// actually matter — inherits the safe behavior for free rather than needing to re-derive it.
    /// </para>
    /// </summary>
    public void RecordEnvironment(RunEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        lock (_gate)
        {
            JournalDocument current = File.Exists(_journalPath) ? Read(_journalPath) : _document;
            _document = current with { Environment = environment };
            Persist();
        }
    }

    // --- waves[] (SSOT §7/§14, #254 M2b) ----------------------------------------------

    /// <summary>The wave's durable journal record, or null when the waves[] section omits it.</summary>
    public WaveJournalEntry? WaveEntryOf(string waveDir)
    {
        lock (_gate)
        {
            return _document.Waves is { } waves && waves.TryGetValue(waveDir, out WaveJournalEntry? entry)
                ? entry
                : null;
        }
    }

    /// <summary>Record the wave ENTRY-preflight marker (SSOT §14.6) and set the wave <see cref="WaveStatus.Running"/>.</summary>
    public void RecordWaveEntry(string waveDir, PlanPreflightsSection entry)
    {
        lock (_gate)
        {
            WaveJournalEntry existing = GetOrCreateWave(waveDir);
            UpdateWave(waveDir, existing with { Status = WaveStatus.Running, Entry = entry });
            Persist();
        }
    }

    /// <summary>Record the wave EXIT/terminal-gate marker (SSOT §14.6).</summary>
    public void RecordWaveExit(string waveDir, PlanGuardrailsSection exit)
    {
        lock (_gate)
        {
            WaveJournalEntry existing = GetOrCreateWave(waveDir);
            UpdateWave(waveDir, existing with { Exit = exit });
            Persist();
        }
    }

    /// <summary>Record a wave settling <see cref="WaveStatus.Completed"/> with its definition hash + marker commit sha (SSOT §14.5).</summary>
    public void RecordWaveCompleted(string waveDir, string definitionHash, string? markerSha)
    {
        lock (_gate)
        {
            WaveJournalEntry existing = GetOrCreateWave(waveDir);
            UpdateWave(waveDir, existing with
            {
                Status = WaveStatus.Completed,
                DefinitionHash = definitionHash,
                MarkerSha = markerSha ?? existing.MarkerSha
            });
            Persist();
        }
    }

    /// <summary>Set a wave's status (SSOT §14.5) without touching its markers/hash.</summary>
    public void RecordWaveStatus(string waveDir, WaveStatus status)
    {
        lock (_gate)
        {
            WaveJournalEntry existing = GetOrCreateWave(waveDir);
            UpdateWave(waveDir, existing with { Status = status });
            Persist();
        }
    }

    /// <summary>
    /// Reset a wave to <see cref="WaveStatus.Pending"/>, clearing its completion hash, marker sha, and
    /// entry/exit markers — the wave half of a wave-drift resolution / wave-scoped reset (SSOT §14.6/§14.8).
    /// The wave's TASKS are reset separately via <see cref="ResetTaskToPending"/>.
    /// </summary>
    public void ResetWaveToPending(string waveDir)
    {
        lock (_gate)
        {
            if (_document.Waves is not { } waves || !waves.ContainsKey(waveDir))
            {
                return;
            }

            UpdateWave(waveDir, new WaveJournalEntry { Status = WaveStatus.Pending });
            Persist();
        }
    }

    private WaveJournalEntry GetOrCreateWave(string waveDir)
    {
        if (_document.Waves is { } waves && waves.TryGetValue(waveDir, out WaveJournalEntry? entry))
        {
            return entry;
        }

        return new WaveJournalEntry { Status = WaveStatus.Pending };
    }

    private void UpdateWave(string waveDir, WaveJournalEntry entry)
    {
        var waves = _document.Waves is null
            ? new Dictionary<string, WaveJournalEntry>(StringComparer.Ordinal)
            : new Dictionary<string, WaveJournalEntry>(_document.Waves, StringComparer.Ordinal);
        waves[waveDir] = entry;
        _document = _document with { Waves = waves };
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
