using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Guardrails.Core.Journal;
using Guardrails.Core.Model;
using Guardrails.Core.State;
using Guardrails.Core.Telemetry;
using JournalTaskStatus = Guardrails.Core.Journal.TaskStatus;

namespace Guardrails.Core.Execution;

/// <summary>
/// Turns an attempt's disposition into its journal record and its terminal
/// <see cref="AttemptResult"/> (SSOT §6/§7/§8): merging the state fragment on success, writing
/// <c>feedback.md</c> on failure, and journaling each attempt with the right status transition
/// (<c>succeeded</c>, <c>running</c>/<c>needs-human</c>, or back to <c>pending</c> on cancel).
/// Extracted from <see cref="TaskExecutor"/> so the loop stays a thin orchestrator and every
/// journal transition for a task lives in one place.
/// </summary>
internal sealed class AttemptJournaler
{
    private readonly StateManager _stateManager;
    private readonly RunJournal _journal;
    private readonly IRunObserver _observer;

    /// <summary>
    /// <paramref name="observer"/> defaults to <see cref="IRunObserver.Null"/> so the existing
    /// direct-construction call sites in tests (which exercise journal behavior, not observer
    /// forwarding) keep compiling unchanged.
    /// </summary>
    public AttemptJournaler(StateManager stateManager, RunJournal journal, IRunObserver? observer = null)
    {
        _stateManager = stateManager;
        _journal = journal;
        _observer = observer ?? IRunObserver.Null;
    }

    /// <summary>
    /// Plan 30 §3.2: the task's fingerprint bucket, derived from the two structural facts the task
    /// already carries — its <c>writeScope</c> roots and its guardrail archetypes — and NEVER from its
    /// name (<see cref="TaskFingerprintBucket.Classify"/> is handed no task identity at all, so the
    /// report legend's "a bucket is a fact about a task, never one read off its name" is a compile-time
    /// property rather than a convention).
    /// <para>
    /// Every journal call below stamps it, INCLUDING every failure path — not just the succeeded settle.
    /// §2 measured that provenance landing on successes alone made each stratum read 100% first-pass by
    /// construction, which is survivorship rather than a measurement; a bucket populated only on success
    /// would reproduce that defect one grain down, hiding a hard bucket's failures from the bucket
    /// itself. <c>null</c> (an off-switch <c>writeScope</c>, or a write surface no rule matches) is a
    /// legitimate result and is passed through unchanged — the corpus reader renders it
    /// <c>(unbucketed)</c>.
    /// </para>
    /// </summary>
    private static string? BucketFor(TaskNode task) =>
        TaskFingerprintBucket.Classify(task.WriteScope, task.Guardrails);

    /// <summary>
    /// Plan 30 §3.4: this attempt's two measured phases — the action's wall time
    /// (<see cref="ActionRun.ActionMs"/>) and the guardrail pass's
    /// (<see cref="GuardrailRunResult.GuardrailMs"/>) — or <c>null</c> when NEITHER was measured.
    /// <para>
    /// <paramref name="guardrails"/> is null at every settle that fires BETWEEN the two phases: the
    /// needs-human short-circuit, both permission walls, and the action-failed / staging /
    /// harness-write / write-scope failures all report "guardrails skipped". The HALF-populated record
    /// they get — an action time, an absent guardrail time — is the honest one, and withholding the
    /// action segment merely because its sibling is missing would discard a measurement that was
    /// actually taken. Only when neither phase ran is the whole object absent, and it is absent rather
    /// than a pair of nulls: an <see cref="AttemptSegments"/> of two nulls CLAIMS a measurement was
    /// taken and came back empty, which is the same false claim <see cref="AttemptRecord.CostUsd"/>
    /// refuses when it distinguishes a null from a recorded <c>0</c>.
    /// </para>
    /// <para>
    /// Each carrying <c>TaskExecutor</c> call site decides what it holds and calls this; the recorders
    /// below take the answer as a parameter rather than reaching for it, because whether a
    /// <see cref="GuardrailRunResult"/> exists at all is a property of the SITE, not of the method —
    /// <see cref="Cancelled"/> alone is reached from a pre-attempt site with no action, a mid-attempt
    /// site with an action and no guardrail pass, and a post-guardrail site with both.
    /// </para>
    /// </summary>
    internal static AttemptSegments? SegmentsFor(ActionRun action, GuardrailRunResult? guardrails = null)
    {
        long? actionMs = action.ActionMs;
        long? guardrailMs = guardrails?.GuardrailMs;

        return actionMs is null && guardrailMs is null
            ? null
            : new AttemptSegments { ActionMs = actionMs, GuardrailMs = guardrailMs };
    }

    public AttemptResult CompleteSucceededOrInvalidFragment(
        TaskNode task,
        int attemptNumber,
        DateTimeOffset startedAt,
        string relativeLogDir,
        string logDir,
        string fragmentOutPath,
        ActionRun action,
        GuardrailRunResult guardrails,
        bool isFinal,
        AttemptProvenance? provenance = null)
    {
        long? mergeSequence = null;

        if (File.Exists(fragmentOutPath))
        {
            long reserved = _journal.ReserveMergeSequence();
            MergeFragmentResult merge = _stateManager.MergeFragment(task.Id, fragmentOutPath, reserved, logDir);

            if (!merge.Merged)
            {
                string reason = merge.Reason ?? "invalid state fragment";
                // A foreign top-level key gets feedback that names the exact stray key so a confused
                // agent drops it on retry (SSOT §6.2, single-writer-per-key); any other rejection uses
                // the generic invalid-fragment feedback. Both route to the same invalid-fragment outcome.
                string feedback = merge.Rejection == FragmentRejection.ForeignKey
                    ? RetryPolicy.ForForeignKey(task, attemptNumber, merge.ForeignKeys)
                    : RetryPolicy.ForInvalidFragment(task, attemptNumber, reason);
                return FailedAttempt(
                    task, attemptNumber, startedAt, relativeLogDir, logDir, feedback, isFinal,
                    AttemptOutcome.InvalidFragment,
                    new TaskResult
                    {
                        TaskId = task.Id,
                        Outcome = TaskOutcome.InvalidFragment,
                        ActionExitCode = action.ExitCode,
                        Guardrails = guardrails.Results,
                        Summary = reason
                    },
                    costUsd: action.CostUsd, usage: action.Usage, turns: action.Turns,
                    segments: SegmentsFor(action, guardrails));
            }

            mergeSequence = reserved;
        }

        var record = new AttemptRecord
        {
            Attempt = attemptNumber,
            StartedAt = startedAt,
            EndedAt = DateTimeOffset.UtcNow,
            ActionExitCode = action.ExitCode,
            Outcome = AttemptOutcome.Succeeded,
            CostUsd = action.CostUsd,
            // #475: the tokens axis travels with its cost sibling, wherever the cost goes.
            Usage = action.Usage,
            // Plan 30 §3.4: and so does the turn count — same carrier, same rule, on every path the
            // cost travels rather than on the success settle alone.
            Turns = action.Turns,
            // Plan 30 §3.4: the only recorder that receives BOTH phases, so it builds the pair itself
            // from what it already has rather than being handed one — the same reason it reads CostUsd
            // and Turns off the action above instead of taking them as parameters.
            Segments = SegmentsFor(action, guardrails),
            LogDir = relativeLogDir,
            Provenance = provenance
        };
        // §7.2 (#274 Part A): stamp the task's definition hash on the serial-mode success settle, so a
        // later resume compares the current definition against it and halts on drift instead of skipping.
        // Plan 32 §5.2: the pin captured at load, never a disk recompute — no fallback, ever.
        _journal.RecordAttempt(
            task.Id, record, JournalTaskStatus.Succeeded, mergeSequence, task.DefinitionHashAtLoad,
            bucket: BucketFor(task));
        _observer.AttemptFinished(task, record);

        // Always show a cost field so the summary column never reads as a reporting gap (issue #58).
        // Key the marker off the ACTION KIND, not cost-nullness: a succeeded PROMPT action can
        // legitimately have a null CostUsd (the Claude `result` line omitted total_cost_usd, or a
        // non-Claude runner reports no cost — see ClaudeStreamParser), so inferring "no LLM used
        // (script)" from null would lie about a task that DID call a model. A script never invokes a
        // model; a prompt whose cost wasn't reported says exactly that.
        string costSegment = task.Action.Kind == ActionKind.Script
            ? "; no LLM used (script)"
            : action.CostUsd is { } cost ? $"; cost ${cost:0.0000}" : "; cost not reported";

        return new AttemptResult(new TaskResult
        {
            TaskId = task.Id,
            Outcome = TaskOutcome.Succeeded,
            ActionExitCode = action.ExitCode,
            Guardrails = guardrails.Results,
            Summary = $"action ok; {guardrails.Results.Count} guardrail(s) passed"
                      + costSegment
                      + (mergeSequence is null ? "" : $"; merged (seq {mergeSequence})")
        }, FeedbackPath: null);
    }

    /// <summary>
    /// Worktree-mode success path: validate the fragment (same rules as
    /// <see cref="CompleteSucceededOrInvalidFragment"/>) but do NOT merge into state.json and do
    /// NOT call RecordAttempt. Returns a succeeded <see cref="AttemptResult"/> with
    /// <see cref="TaskResult.FragmentPath"/> set so the Scheduler can perform the B1 deferred settle
    /// (fragment merge → git commit → journal settle) under the integration lock.
    /// </summary>
    public AttemptResult ValidateFragmentForSettle(
        TaskNode task,
        int attemptNumber,
        DateTimeOffset startedAt,
        string relativeLogDir,
        string logDir,
        string fragmentOutPath,
        ActionRun action,
        GuardrailRunResult guardrails,
        bool isFinal,
        AttemptProvenance? provenance = null)
    {
        string? validatedFragmentPath = null;

        // Worktree mode: a state-rejected non-final attempt has its segment reset to taskBase before
        // the next attempt (TaskExecutor F2 reset), so the attempt's FILE writes are reverted too. The
        // rejection feedback discloses that rollback (issue #162) so the agent re-authors its files
        // instead of fixing only the key and then failing a file-exists guardrail. The final attempt is
        // never reset, so it claims no rollback.
        bool fileWritesRolledBack = !isFinal;

        if (File.Exists(fragmentOutPath))
        {
            string raw;
            try { raw = File.ReadAllText(fragmentOutPath); }
            catch (Exception ex)
            {
                string msg = $"cannot read fragment: {ex.Message}";
                return FailedAttempt(task, attemptNumber, startedAt, relativeLogDir, logDir,
                    RetryPolicy.ForInvalidFragment(task, attemptNumber, msg, fileWritesRolledBack), isFinal,
                    AttemptOutcome.InvalidFragment,
                    new TaskResult { TaskId = task.Id, Outcome = TaskOutcome.InvalidFragment, ActionExitCode = action.ExitCode, Guardrails = guardrails.Results, Summary = msg },
                    costUsd: action.CostUsd, usage: action.Usage);
            }

            JsonNode? node;
            try { node = JsonNode.Parse(raw); }
            catch (JsonException ex)
            {
                string msg = $"fragment is not valid JSON: {ex.Message}";
                return FailedAttempt(task, attemptNumber, startedAt, relativeLogDir, logDir,
                    RetryPolicy.ForInvalidFragment(task, attemptNumber, msg, fileWritesRolledBack), isFinal,
                    AttemptOutcome.InvalidFragment,
                    new TaskResult { TaskId = task.Id, Outcome = TaskOutcome.InvalidFragment, ActionExitCode = action.ExitCode, Guardrails = guardrails.Results, Summary = msg },
                    costUsd: action.CostUsd, usage: action.Usage);
            }

            if (node is not JsonObject fragObj)
            {
                string kind = node is null ? "null" : node.GetValueKind().ToString().ToLowerInvariant();
                string msg = $"invalid state fragment: top-level value must be a JSON object, was {kind}";
                return FailedAttempt(task, attemptNumber, startedAt, relativeLogDir, logDir,
                    RetryPolicy.ForInvalidFragment(task, attemptNumber, msg, fileWritesRolledBack), isFinal,
                    AttemptOutcome.InvalidFragment,
                    new TaskResult { TaskId = task.Id, Outcome = TaskOutcome.InvalidFragment, ActionExitCode = action.ExitCode, Guardrails = guardrails.Results, Summary = msg },
                    costUsd: action.CostUsd, usage: action.Usage);
            }

            List<string> foreignKeys = fragObj
                .Select(pair => pair.Key)
                .Where(k => !string.Equals(k, task.Id, StringComparison.Ordinal) && !StateManager.ReservedMergeKeys.Contains(k))
                .ToList();

            if (foreignKeys.Count > 0)
            {
                string reason = $"foreign top-level key(s): {string.Join(", ", foreignKeys.Select(k => $"'{k}'"))}";
                return FailedAttempt(task, attemptNumber, startedAt, relativeLogDir, logDir,
                    RetryPolicy.ForForeignKey(task, attemptNumber, foreignKeys, fileWritesRolledBack), isFinal,
                    AttemptOutcome.InvalidFragment,
                    new TaskResult { TaskId = task.Id, Outcome = TaskOutcome.InvalidFragment, ActionExitCode = action.ExitCode, Guardrails = guardrails.Results, Summary = reason },
                    costUsd: action.CostUsd, usage: action.Usage);
            }

            validatedFragmentPath = fragmentOutPath;
        }

        string costSegment = task.Action.Kind == ActionKind.Script
            ? "; no LLM used (script)"
            : action.CostUsd is { } cost ? $"; cost ${cost:0.0000}" : "; cost not reported";

        // #196: carry the not-yet-journaled attempt data to the Scheduler's B1 settle. The settle
        // records a real AttemptRecord (built from these fields — the SAME shape the serial success
        // path records above) TOGETHER with the reserved mergeSequence, so a succeeded worktree task's
        // Attempts list is non-empty (SSOT §7), matching serial mode. The record is deferred (not
        // written here) because the outcome and the mergeSequence are only known after the integration
        // commit, under the integration lock.
        var pendingAttempt = new PendingAttempt
        {
            Attempt = attemptNumber,
            StartedAt = startedAt,
            ActionExitCode = action.ExitCode,
            CostUsd = action.CostUsd,
            // #475: WITHOUT this line the value the record above sets reaches serial runs only — the
            // settle path builds its own AttemptRecord from this object, never from the journaller.
            Usage = action.Usage,
            // Plan 30 §3.4: without this line the turn count 12-record-the-turn-count journals on the
            // serial record above reaches serial runs only, and worktree is the DEFAULT mode — so the
            // column would be empty for the majority of real rows while every run stayed green.
            Turns = action.Turns,
            // Plan 30 §3.4: same loss, one member over — the action and guardrail durations would be
            // measured on this attempt, printed once and then discarded at the settle boundary. Built
            // by the SAME helper the serial record above calls, so the two paths cannot disagree about
            // what "neither phase measured" means.
            Segments = SegmentsFor(action, guardrails),
            // Plan 30 §3.2: without this the worktree settle has no bucket to hand its recorder, so
            // every default-mode task entry renders `(unbucketed)` in the corpus report. Computed by
            // the SAME BucketFor the serial path uses — a second classification site here would be a
            // second answer, free to disagree with the journalled one without either looking wrong.
            Bucket = BucketFor(task),
            LogDir = relativeLogDir,
            Provenance = provenance
        };

        return new AttemptResult(new TaskResult
        {
            TaskId = task.Id,
            Outcome = TaskOutcome.Succeeded,
            ActionExitCode = action.ExitCode,
            Guardrails = guardrails.Results,
            FragmentPath = validatedFragmentPath,
            DeferredSettle = true,
            PendingAttempt = pendingAttempt,
            Summary = $"action ok; {guardrails.Results.Count} guardrail(s) passed{costSegment}"
        }, FeedbackPath: null);
    }

    /// <summary>
    /// Record a failed attempt: write <c>feedback.md</c> into the attempt's log dir, journal
    /// the attempt (non-final attempts keep status <c>running</c>; the final one goes
    /// <c>needs-human</c>), and hand the feedback path to the next attempt.
    /// </summary>
    public AttemptResult FailedAttempt(
        TaskNode task,
        int attemptNumber,
        DateTimeOffset startedAt,
        string relativeLogDir,
        string logDir,
        string feedback,
        bool isFinal,
        AttemptOutcome outcome,
        TaskResult result,
        IReadOnlyList<FailedGuardrail>? failedGuardrails = null,
        decimal? costUsd = null,
        AttemptUsage? usage = null,
        AttemptProvenance? provenance = null,
        int? turns = null,
        AttemptSegments? segments = null)
    {
        string feedbackPath = Path.Combine(logDir, "feedback.md");
        AtomicFile.WriteAllText(feedbackPath, feedback);

        var record = new AttemptRecord
        {
            Attempt = attemptNumber,
            StartedAt = startedAt,
            EndedAt = DateTimeOffset.UtcNow,
            ActionExitCode = result.ActionExitCode,
            Outcome = outcome,
            FailedGuardrails = failedGuardrails ?? [],
            CostUsd = costUsd,
            // #475: a FAILED attempt burned tokens too, and the per-tier spend line aggregates every
            // recorded attempt — not just the ones that converged.
            Usage = usage,
            // #532: and it burned them ON A MODEL, which is the half #475 left behind. The route was
            // resolved BEFORE the action ran and is already on disk in attempt-route.log; carrying it
            // here is plumbing, not new knowledge. Without it every failure lands in `(no route
            // recorded)` and each stratum keeps only its own successes — so first-pass rates read 100%
            // by construction and per-model cost understates each model by exactly its failure rate.
            Provenance = provenance,
            // Plan 30 §3.4: and the turn count, which §2's survivorship finding puts on exactly this
            // path. A count recorded only where an attempt CONVERGED would populate the column on the
            // successes and leave it empty on the failures a first-pass-rate comparison is trying to
            // measure — the #532 defect one column over. It arrives as a parameter rather than off an
            // ActionRun because this method takes none: the caller holds the action, exactly as it does
            // for `costUsd`/`usage` above.
            Turns = turns,
            // Plan 30 §3.4: and what the attempt COST IN TIME before it went red — §2's finding is
            // exactly this shape, and this is the path it is about (ten of plan 27's twenty-three
            // attempts settled here). An attempt that burned twenty minutes and then failed its
            // guardrails is the evidence a cost comparison is missing; recorded on the success settle
            // alone, the durations would describe only the attempts that converged. Five of this
            // method's call sites report "guardrails skipped" and pass an ACTION-only pair — see
            // SegmentsFor on why a half-populated record is the honest one there.
            Segments = segments,
            LogDir = relativeLogDir
        };
        _journal.RecordAttempt(
            task.Id, record, isFinal ? JournalTaskStatus.NeedsHuman : JournalTaskStatus.Running,
            bucket: BucketFor(task));
        _observer.AttemptFinished(task, record);

        return new AttemptResult(result, feedbackPath, Outcome: outcome);
    }

    /// <summary>
    /// The rate-limit-exhausted halt (issue #115): a transient pause budget was spent without the
    /// limit clearing. Record one attempt with the <see cref="AttemptOutcome.Timeout"/>-distinct
    /// rate-limit signal and settle the task <c>needs-human</c> — but with a DISTINCT, actionable
    /// reason ("re-run later") so the operator waits rather than debugging a healthy task. Distinct
    /// from a generic budget-exhaustion needs-human.
    /// <para>
    /// <b>Journal vs in-memory outcome (issue #190 part 1).</b> The JOURNAL's <c>status</c> string
    /// stays <c>needs-human</c> (<see cref="JournalTaskStatus.NeedsHuman"/>) — deliberately NOT a new
    /// journal-level status. A rate-limited task IS, durably, halted pending a human/time-based
    /// re-run — exactly what <c>needs-human</c> means for resume purposes (§7: any non-succeeded
    /// status resumes to <c>pending</c> with a fresh budget; a distinct journal status would need its
    /// own <c>ResumeStatus</c> entry that behaves IDENTICALLY, pure churn with no behavioral payoff).
    /// The attempt-level <c>outcome</c> already carries the distinct <c>rate-limited</c> string (SSOT
    /// §7), which is sufficient to reconstruct "why" from the journal on disk. What was missing was the
    /// PER-RUN/UI-facing signal: the live table and the run summary rendered every non-green,
    /// non-blocked outcome as a generic "needs human", indistinguishable from a genuine stuck task. The
    /// fix is therefore <see cref="TaskOutcome"/>-only: <see cref="TaskOutcome.RateLimited"/> is a new
    /// terminal value the CLI's observers/renderers switch on, while the returned <see cref="TaskResult"/>
    /// still reports as non-green (<see cref="TaskResult.IsGreen"/> already excludes anything but
    /// <see cref="TaskOutcome.Succeeded"/>/<see cref="TaskOutcome.Skipped"/>) so scheduling/exit-code
    /// behavior is UNCHANGED.
    /// </para>
    /// </summary>
    public AttemptResult RateLimitExhausted(
        TaskNode task,
        int attemptNumber,
        DateTimeOffset startedAt,
        string relativeLogDir,
        string logDir,
        string reason,
        TimeSpan pausedFor)
    {
        Directory.CreateDirectory(logDir);
        string summary = $"paused (rate-limited): {reason}; did not clear within " +
                         $"{(int)pausedFor.TotalSeconds}s — re-run later";

        string feedback =
            $"# Task '{task.Id}' is rate-limited\n\n" +
            $"Task: {task.Description}\n\n" +
            $"A transient infrastructure limit did not clear within the pause budget " +
            $"({(int)pausedFor.TotalSeconds}s):\n\n> {reason}\n\n" +
            "This is NOT a task defect and NOT something to debug — the provider was rate-limiting/" +
            "overloaded. RE-RUN this plan later (the harness will resume from here) once the limit " +
            "has cleared. Raise `transientPauseBudgetSeconds` in guardrails.json to wait longer " +
            "automatically.\n";
        AtomicFile.WriteAllText(Path.Combine(logDir, "feedback.md"), feedback);

        var record = new AttemptRecord
        {
            Attempt = attemptNumber,
            StartedAt = startedAt,
            EndedAt = DateTimeOffset.UtcNow,
            ActionExitCode = null,
            Outcome = AttemptOutcome.RateLimited,
            // #532: deliberately NO Provenance, and this record carries no CostUsd either. It is a
            // SETTLE MARKER written from ExecuteAsync after the pause budget ran out — a synthetic
            // attempt number for a model call that never happened. The attempts that did run and did
            // cost money are journaled separately and now carry their route. Resolving the route here
            // just to fill the field would be a SECOND derivation of a decision this code insists must
            // have exactly one (TaskExecutor: "One resolution, two consumers").
            // Plan 30 §3.4: and NO Turns, on the same reasoning — null says no model was invoked,
            // whereas `0` would claim one was invoked and took no turns. NO Segments either: there is no
            // ActionRun in scope at the ExecuteAsync call site, and neither phase of an attempt that was
            // never launched has a duration to report.
            LogDir = relativeLogDir
        };
        _journal.RecordAttempt(task.Id, record, JournalTaskStatus.NeedsHuman, bucket: BucketFor(task));
        _observer.AttemptFinished(task, record);

        return new AttemptResult(new TaskResult
        {
            TaskId = task.Id,
            Outcome = TaskOutcome.RateLimited,
            Summary = summary
        }, FeedbackPath: null, Outcome: AttemptOutcome.RateLimited);
    }

    /// <summary>
    /// The needsHuman short-circuit (SSOT §9): a prompt action wrote a root <c>needsHuman</c>
    /// key to its fragment. Record the attempt with the <c>needs-human</c> outcome and journal
    /// the task <c>needs-human</c> immediately — no retry, no guardrails. Returns a non-green
    /// result so the scheduler blocks dependents.
    /// <para>
    /// <b>Issue #554 — <paramref name="salvage"/>.</b> An agent that writes real work and THEN asks a
    /// human used to leave nothing behind: this path returns before any salvage call, and its tree is not
    /// reset but ORPHANED, so the ref and the patch are the only durable copies anyone can be pointed at.
    /// When the caller preserved one, its recovery routing is appended to the <c>feedback.md</c> composed
    /// here — the artifact a resumed agent and a triaging human both read. It carries the
    /// <see cref="SalvageFraming.Escalation"/> wording, never the retry path's rollback claim, because on
    /// this path no rollback happened. Null (salvage off, serial mode, or nothing IN SCOPE was written)
    /// leaves the bytes exactly as they were.
    /// </para>
    /// </summary>
    public AttemptResult NeedsHuman(
        TaskNode task,
        int attemptNumber,
        DateTimeOffset startedAt,
        string relativeLogDir,
        string logDir,
        ActionRun action,
        string question,
        IReadOnlyList<string> options,
        string? kind = null,
        AttemptProvenance? provenance = null,
        SalvageRef? salvage = null,
        AttemptSegments? segments = null)
    {
        string feedback =
            $"# Task '{task.Id}' needs a human\n\n" +
            $"Task: {task.Description}\n\n" +
            $"The prompt action signalled it cannot proceed without a human decision:\n\n> {question}\n";
        if (salvage is not null)
        {
            var body = new StringBuilder(feedback);
            RetryPolicy.AppendSalvageSection(body, salvage, SalvageFraming.Escalation);
            feedback = body.ToString();
        }

        AtomicFile.WriteAllText(Path.Combine(logDir, "feedback.md"), feedback);

        var record = new AttemptRecord
        {
            Attempt = attemptNumber,
            StartedAt = startedAt,
            EndedAt = DateTimeOffset.UtcNow,
            ActionExitCode = action.ExitCode,
            Outcome = AttemptOutcome.NeedsHuman,
            CostUsd = action.CostUsd,
            Usage = action.Usage,
            // Plan 30 §3.4: a paid attempt also burned TURNS, and `needs-human` is an outcome real
            // run.json rows carry — leaving it null here would blank the column on precisely the halts
            // a reader is trying to compare against the converged attempts.
            Turns = action.Turns,
            // Plan 30 §3.4: and the TIME it burned before it asked. This settle short-circuits on the
            // state-out signal, above the guardrail pass, so the caller's pair carries ActionMs with
            // GuardrailMs absent — the half-populated record described on SegmentsFor.
            Segments = segments,
            // #532: a needs-human attempt is a PAID attempt — this one carries action.CostUsd right
            // above — so it must say which model was paid.
            Provenance = provenance,
            LogDir = relativeLogDir,
            // #485: the agent's claim, canonicalized once more at the journal boundary so a caller that
            // hand-builds a kind cannot write an unrecognised token into run.json.
            NeedsHumanKind = NeedsHumanKinds.Parse(kind)
        };
        _journal.RecordAttempt(task.Id, record, JournalTaskStatus.NeedsHuman, bucket: BucketFor(task));
        _observer.AttemptFinished(task, record);

        return new AttemptResult(new TaskResult
        {
            TaskId = task.Id,
            Outcome = TaskOutcome.NeedsHuman,
            ActionExitCode = action.ExitCode,
            // The kind is deliberately NOT spliced into this summary: Scheduler.ExtractNeedsHumanQuestion
            // parses the `needs human: ` prefix and treats the remainder as the escalation's question.
            Summary = $"needs human: {question}",
            NeedsHumanOptions = options,
            NeedsHumanKind = NeedsHumanKinds.Parse(kind)
        }, FeedbackPath: null);
    }

    /// <summary>
    /// The §6.2 <c>no-route</c> settle (DoR §6.2/§12.4, model tiering #201): tier resolution found NO
    /// candidate block at the rung this attempt asked for, nor at any STRONGER one, so the attempt was
    /// never launched — no model ran, no guardrail was evaluated, no retry was burned. Record ONE
    /// attempt with the distinct <see cref="AttemptOutcome.NoRoute"/> outcome, write an actionable
    /// <c>feedback.md</c>, and settle the task <c>needs-human</c> immediately: the same shape
    /// <see cref="NeedsHuman"/> uses, and for the same reason it skips the rest of the budget — a
    /// further attempt resolves IDENTICALLY, because v1 resolution is a pure function of the tier tag
    /// and the registry, and neither changes between attempts of one run.
    ///
    /// <para><b>The wording is the CALLER's; the record shape is this method's.</b>
    /// <paramref name="reason"/> arrives already composed from the resolution's own D28 data (its
    /// costly-ceiling flag and the blocks behind it), exactly as <see cref="RateLimitExhausted"/>
    /// receives its reason. Re-deriving that diagnosis here would need a second copy of the candidacy
    /// predicate, which D22a forbids.</para>
    ///
    /// <para><paramref name="provenance"/> rides the record AS USUAL — its <c>tierSource</c> and the
    /// REQUESTED rung are how a reader learns WHICH rung could not be served — while
    /// <see cref="AttemptProvenance.Tier"/>, the rung actually SERVED, is absent because none was.
    /// Nothing here names a route: a costly block the ceiling excluded is a CAUSE, never a
    /// destination (D22).</para>
    /// </summary>
    public AttemptResult NoRoute(
        TaskNode task,
        int attemptNumber,
        DateTimeOffset startedAt,
        string relativeLogDir,
        string logDir,
        AttemptProvenance? provenance,
        string reason)
    {
        Directory.CreateDirectory(logDir);

        string feedback =
            $"# Task '{task.Id}' has no route for the tier it asked for\n\n" +
            $"Task: {task.Description}\n\n" +
            $"{reason}\n\n" +
            "The attempt was NOT launched: no model ran, no guardrail was evaluated and no retry was " +
            "burned. Once an effective tier exists, resolution OWNS the outcome (DoR §6.1, D30) — the " +
            "harness does not fall back to the runner's own model, and never routes weaker than " +
            "asked.\n\n" +
            "This is a routing-CONFIGURATION gap, not a task defect, so no number of retries can clear " +
            "it. `guardrails validate` reports the same gap statically as GR2048, before a token is " +
            "spent; run it against this plan to catch it there next time.\n";
        AtomicFile.WriteAllText(Path.Combine(logDir, "feedback.md"), feedback);

        var record = new AttemptRecord
        {
            Attempt = attemptNumber,
            StartedAt = startedAt,
            EndedAt = DateTimeOffset.UtcNow,
            // Nothing ran: no process exited, nothing was spent, and (plan 30 §3.4) no turns were
            // taken — so Turns stays absent rather than reading `0`, which would claim a model was
            // invoked and did nothing. Segments is absent for the same reason and one step earlier:
            // this settles ABOVE the runner call, so neither phase has begun and there is no
            // ActionRun in scope to read a duration from.
            ActionExitCode = null,
            Outcome = AttemptOutcome.NoRoute,
            LogDir = relativeLogDir,
            Provenance = provenance
        };
        _journal.RecordAttempt(task.Id, record, JournalTaskStatus.NeedsHuman, bucket: BucketFor(task));
        _observer.AttemptFinished(task, record);

        return new AttemptResult(new TaskResult
        {
            TaskId = task.Id,
            Outcome = TaskOutcome.NeedsHuman,
            Summary = $"needs human: {reason}"
        }, FeedbackPath: null, Outcome: AttemptOutcome.NoRoute);
    }

    /// <summary>
    /// The permission-wall early halt (issues #86 / #104): the runtime refused a write/edit on a path
    /// retrying cannot clear (a <c>.claude/</c> structural path, or the same path across repeated
    /// attempts). Record ONE attempt with the distinct <see cref="AttemptOutcome.PermissionDenied"/>
    /// outcome, write a task-level <c>feedback.md</c> naming the wall and its remediation, and settle
    /// the task <c>needs-human</c> immediately — no further retries. Returns a non-green result so the
    /// scheduler blocks dependents.
    /// </summary>
    public AttemptResult PermissionWall(
        TaskNode task,
        int attemptNumber,
        DateTimeOffset startedAt,
        string relativeLogDir,
        string logDir,
        ActionRun action,
        PermissionWallDecision decision,
        AttemptProvenance? provenance = null,
        AttemptSegments? segments = null)
    {
        Directory.CreateDirectory(logDir);
        string feedback = RetryPolicy.ForPermissionWall(task, decision.StructuralPaths, decision.RepeatedPaths);
        AtomicFile.WriteAllText(Path.Combine(logDir, "feedback.md"), feedback);

        string paths = string.Join(", ", decision.AllPaths);
        string summary = decision.HasStructural
            ? $"needs human: write to .claude/ blocked by the runtime (structural) — {paths}"
            : $"needs human: write repeatedly refused (permission wall) — {paths}";

        var record = new AttemptRecord
        {
            Attempt = attemptNumber,
            StartedAt = startedAt,
            EndedAt = DateTimeOffset.UtcNow,
            ActionExitCode = action.ExitCode,
            Outcome = AttemptOutcome.PermissionDenied,
            CostUsd = action.CostUsd,
            Usage = action.Usage,
            // Plan 30 §3.4: the wall stopped the work AFTER the turns were spent, so they are recorded.
            Turns = action.Turns,
            // Plan 30 §3.4: and after the action's clock had run. Both of this method's call sites halt
            // before any guardrail does, so the pair they build is ActionMs-only.
            Segments = segments,
            // #532: the wall stopped the WORK, not the billing — the model ran and was paid.
            Provenance = provenance,
            LogDir = relativeLogDir
        };
        _journal.RecordAttempt(task.Id, record, JournalTaskStatus.NeedsHuman, bucket: BucketFor(task));
        _observer.AttemptFinished(task, record);

        return new AttemptResult(new TaskResult
        {
            TaskId = task.Id,
            Outcome = TaskOutcome.NeedsHuman,
            ActionExitCode = action.ExitCode,
            Summary = summary
        }, FeedbackPath: null, Outcome: AttemptOutcome.PermissionDenied);
    }

    /// <summary>
    /// #329: the OUTCOME-AWARE structural <c>.claude/</c>-wall halt. #326 settles a NON-converged attempt
    /// that carries a structural <c>.claude/</c> wall to <c>needs-human</c> on ONE attempt (the #104
    /// fast-halt). This method preserves that halt DECISION unchanged — one recorded attempt, journal
    /// status <c>needs-human</c>, no further retries — but journals the TRUE primary outcome and its
    /// evidence instead of a blanket <see cref="AttemptOutcome.PermissionDenied"/> with an EMPTY
    /// <c>failedGuardrails[]</c>: a guardrail that genuinely ran and FAILED is recorded as
    /// <see cref="AttemptOutcome.GuardrailFailed"/> with its <paramref name="failedGuardrails"/> populated.
    /// The <see cref="TaskResult.Summary"/> and <c>feedback.md</c> LEAD with that cause and disclose the
    /// <c>.claude/</c> wall as SECONDARY context, so a human is not misdirected into chasing a
    /// permission/config issue when a real guardrail failed (issue #329). Returns a non-green result
    /// (<see cref="TaskOutcome.NeedsHuman"/>) so the scheduler blocks dependents, exactly as the
    /// <see cref="PermissionWall"/> halt did.
    /// </summary>
    public AttemptResult StructuralWallHalt(
        TaskNode task,
        int attemptNumber,
        DateTimeOffset startedAt,
        string relativeLogDir,
        string logDir,
        ActionRun action,
        AttemptOutcome primaryOutcome,
        string summary,
        string feedback,
        IReadOnlyList<GuardrailResult> guardrailResults,
        IReadOnlyList<FailedGuardrail> failedGuardrails,
        AttemptProvenance? provenance = null,
        AttemptSegments? segments = null)
    {
        Directory.CreateDirectory(logDir);
        AtomicFile.WriteAllText(Path.Combine(logDir, "feedback.md"), feedback);

        var record = new AttemptRecord
        {
            Attempt = attemptNumber,
            StartedAt = startedAt,
            EndedAt = DateTimeOffset.UtcNow,
            ActionExitCode = action.ExitCode,
            Outcome = primaryOutcome,
            FailedGuardrails = failedGuardrails,
            CostUsd = action.CostUsd,
            Usage = action.Usage,
            // Plan 30 §3.4: same as every other paid halt — the attempt ran, so its turns are recorded.
            Turns = action.Turns,
            // Plan 30 §3.4: and BOTH durations, not just the action's. This method takes no
            // GuardrailRunResult, but its call site holds one — the guardrails demonstrably ran and
            // failed there, which is the whole reason #329 reports this halt as guardrail-failed — so
            // the guardrail half arrives in the pair the caller builds. Reading only the ActionRun in
            // hand here would silently record GuardrailMs = null on a path that measured it.
            Segments = segments,
            // #532: same as every other paid halt — the model that ran is the model that is billed.
            Provenance = provenance,
            LogDir = relativeLogDir
        };
        _journal.RecordAttempt(task.Id, record, JournalTaskStatus.NeedsHuman, bucket: BucketFor(task));
        _observer.AttemptFinished(task, record);

        return new AttemptResult(new TaskResult
        {
            TaskId = task.Id,
            Outcome = TaskOutcome.NeedsHuman,
            ActionExitCode = action.ExitCode,
            Guardrails = guardrailResults,
            Summary = summary
        }, FeedbackPath: null, Outcome: primaryOutcome);
    }

    /// <summary>
    /// The task-level preflight short-circuit (two-scope preflights F9, SSOT §7): a RED
    /// <c>tasks/&lt;id&gt;/preflights/</c> slot fired BEFORE the attempt loop. Record ONE attempt with the
    /// distinct <see cref="AttemptOutcome.TaskPreflightFailed"/> outcome carrying the failed preflight
    /// checks (name + actionable reason), write a task-level <c>feedback.md</c> naming what was missing,
    /// and settle the task <c>needs-human</c> — so <c>run.json</c> shows WHAT preflight failed and WHY
    /// (not a bare <c>{status: needs-human, attempts: []}</c>).
    /// <para>
    /// This attempt does NOT burn a retry: the action never ran and the retry budget is never consulted
    /// (the short-circuit returns before the attempt loop AND before <see cref="RunJournal.MarkRunning"/>).
    /// The no-burn property is STRUCTURAL — a preflight-fail record is present but the budget is untouched,
    /// exactly as the SSOT §7 wire example shows (<c>attempts: [ { attempt: 1, outcome: "task-preflight-failed" } ]</c>).
    /// Returns a non-green result so the scheduler blocks the transitive cone.
    /// </para>
    /// </summary>
    public AttemptResult TaskPreflightFailed(
        TaskNode task,
        int attemptNumber,
        DateTimeOffset startedAt,
        string relativeLogDir,
        string logDir,
        IReadOnlyList<FailedGuardrail> failedChecks)
    {
        Directory.CreateDirectory(logDir);

        string checkList = string.Join(", ", failedChecks.Select(c => c.Name));
        string detail = string.Join(
            "\n", failedChecks.Select(c => $"- **{c.Name}** — {c.Reason}"));
        string feedback =
            $"# Task '{task.Id}' failed its task-level preflight\n\n" +
            $"Task: {task.Description}\n\n" +
            "A `tasks/<id>/preflights/` check gates this task on a producer having actually delivered in " +
            "the bytes this task inherited. The following preflight check(s) failed, so the task did NOT " +
            "run its action (no retry attempt was burned):\n\n" +
            $"{detail}\n\n" +
            "This is a dependency-delivery gate, not a task defect: fix the upstream producer (or the " +
            "inherited bytes) so the preflight passes, then re-run.\n";
        AtomicFile.WriteAllText(Path.Combine(logDir, "feedback.md"), feedback);

        var record = new AttemptRecord
        {
            Attempt = attemptNumber,
            StartedAt = startedAt,
            EndedAt = DateTimeOffset.UtcNow,
            ActionExitCode = null,
            Outcome = AttemptOutcome.TaskPreflightFailed,
            FailedGuardrails = failedChecks,
            // #532: deliberately NO Provenance, and no CostUsd on this record either — the action never
            // ran (that is the whole point of a preflight gate), so no model was chosen and none was
            // billed. A route here would name a model that did nothing.
            // Plan 30 §3.4: no Turns either, and no Segments. This fires BEFORE the attempt loop exists,
            // so there is no ActionRun in scope to read either one from — the mechanical form of the
            // same honesty rule. A duration here would time a phase that never started.
            LogDir = relativeLogDir
        };
        _journal.RecordAttempt(task.Id, record, JournalTaskStatus.NeedsHuman, bucket: BucketFor(task));
        _observer.AttemptFinished(task, record);

        return new AttemptResult(new TaskResult
        {
            TaskId = task.Id,
            Outcome = TaskOutcome.NeedsHuman,
            Summary = $"task-preflight failed: {checkList}"
        }, FeedbackPath: null, Outcome: AttemptOutcome.TaskPreflightFailed);
    }

    public AttemptResult Cancelled(
        TaskNode task,
        int attemptNumber,
        DateTimeOffset startedAt,
        string relativeLogDir,
        ProcessResult actionResult,
        decimal? costUsd,
        AttemptUsage? usage = null,
        AttemptProvenance? provenance = null,
        int? turns = null,
        AttemptSegments? segments = null)
    {
        var record = new AttemptRecord
        {
            Attempt = attemptNumber,
            StartedAt = startedAt,
            EndedAt = DateTimeOffset.UtcNow,
            ActionExitCode = actionResult.ExitCode,
            Outcome = AttemptOutcome.Cancelled,
            CostUsd = costUsd,
            Usage = usage,
            // Plan 30 §3.4: decided at the CALL SITE, never here — the two mid-attempt cancels in
            // RunAttemptAsync have an ActionRun in hand and pass its turn count; the pre-attempt cancel
            // inside the transient backoff passes nothing, for the same reason it passes costUsd: null.
            // One method, two honest answers.
            Turns = turns,
            // Plan 30 §3.4: same split, and it goes one grain finer — the two mid-attempt cancels do not
            // agree with EACH OTHER either. The earlier one fires right after the action returns and
            // passes an ActionMs-only pair; the later one fires after the guardrail pass and passes
            // both; the pre-attempt cancel passes nothing at all.
            Segments = segments,
            // #532: a cancel mid-attempt can still have spent real money before the token tripped.
            // Null here is honest for the pre-attempt cancel in ExecuteAsync, where no route was
            // resolved and no model ran — see the note at that call site.
            Provenance = provenance,
            LogDir = relativeLogDir
        };

        // Back to pending: a resumed run re-attempts this task (SSOT §7 resume rules).
        _journal.RecordAttempt(task.Id, record, JournalTaskStatus.Pending, bucket: BucketFor(task));
        _observer.AttemptFinished(task, record);

        return new AttemptResult(new TaskResult
        {
            TaskId = task.Id,
            Outcome = TaskOutcome.Cancelled,
            ActionExitCode = actionResult.ExitCode,
            Summary = "cancelled mid-attempt; journaled back to pending"
        }, FeedbackPath: null);
    }
}

/// <summary>
/// One attempt's terminal result plus the feedback file it left for the next attempt.
/// <see cref="TransientReason"/> is set ONLY for a transient pause (issue #115): the operator-facing
/// cause (with any reset hint), which the loop passes to <see cref="IRunObserver.PromptPaused"/>.
/// <para>
/// <see cref="ActionWasNoOp"/>, <see cref="ActionOutputFingerprint"/> and
/// <see cref="GuardrailFailureFingerprint"/> drive the no-op short-circuit (issues #174 / #182): set
/// ONLY on a guardrail-failed attempt. <see cref="ActionWasNoOp"/> is true when the action exited 0,
/// wrote no state fragment, and — in a real git segment (worktree mode) — made no file changes this
/// attempt; <see cref="GuardrailFailureFingerprint"/> is a canonical signature of the failed
/// guardrails' names + reasons + output; <see cref="ActionOutputFingerprint"/> is a canonical
/// signature of the action's own stdout+stderr (the serial-mode proxy for "the action behaved
/// identically", since serial mode has no <c>taskBase</c> to diff files against).
/// Worktree mode (#174): two consecutive attempts that are BOTH no-ops AND carry the IDENTICAL
/// guardrail fingerprint cannot differ — the loop escalates to needs-human immediately. Serial mode
/// (#182): with no <c>taskBase</c>, the loop additionally requires the action output fingerprint to
/// match across the two attempts before escalating — the loop escalates to needs-human immediately
/// instead of exhausting the retry budget.
/// </para>
/// <para>
/// #264 (deterministic-script reproduction): the SAME three fields also drive a sibling short-circuit
/// for a <c>script</c> action that WROTE FILES (so it is not a no-op and #174 never fires) but whose
/// <see cref="ActionOutputFingerprint"/> reproduced byte-identically across two guardrail-failed
/// attempts — positive evidence the script is deterministic, so re-running it is provably pointless.
/// A write-scope violation (a guardrail-class failure raised before the task's own guardrails) sets
/// <see cref="GuardrailFailureFingerprint"/> to the stable set of offending paths so it participates
/// too. Scoped to worktree mode; the byte-identical action-output requirement is the
/// flaky/nondeterministic-script escape hatch.
/// </para>
/// </summary>
internal sealed record AttemptResult(
    TaskResult Result,
    string? FeedbackPath,
    string? TransientReason = null,
    AttemptOutcome? Outcome = null,
    bool ActionWasNoOp = false,
    string? GuardrailFailureFingerprint = null,
    string? ActionOutputFingerprint = null);
