using Guardrails.Core.Graph;
using Guardrails.Core.Journal;
using Guardrails.Core.Model;
using Guardrails.Core.Prompts;

namespace Guardrails.Core.Execution;

/// <summary>
/// Builds the prompt-context pointers a prompt action receives (issue #26 Gaps 2–4): the
/// transitive dependency closure's current-success artifacts, and this task's prior attempts.
/// Pure read-side over the journal/graph — extracted so the provenance rules
/// (<see cref="CurrentSuccessfulAttempt"/>) live in one focused place.
/// </summary>
internal sealed class DependencyContextBuilder
{
    private readonly PlanDefinition _plan;
    private readonly RunJournal _journal;
    private readonly DependencyGraph _graph;
    private readonly IReadOnlyDictionary<string, TaskNode> _tasksById;

    public DependencyContextBuilder(
        PlanDefinition plan,
        RunJournal journal,
        DependencyGraph graph,
        IReadOnlyDictionary<string, TaskNode> tasksById)
    {
        _plan = plan;
        _journal = journal;
        _graph = graph;
        _tasksById = tasksById;
    }

    /// <summary>
    /// Build the dependency-context pointers (issue #26 Gap 4): for each task in the transitive
    /// <c>dependsOn</c> closure that has a recorded success, a pointer to the artifacts of its
    /// CURRENT successful result — the attempt whose fragment is reflected in the dependency's
    /// current <c>state.json</c> (which the dependent will read via <c>GUARDRAILS_STATE_IN</c>) —
    /// not merely the last <c>Succeeded</c> attempt in the journal history. After a
    /// <c>reset</c> + re-run, a later succeeded attempt may have contributed NO fragment, so the
    /// current state still comes from an earlier attempt; pointing at the later one would cite a
    /// stale transcript/fragment that disagrees with the state the dependent actually sees.
    /// Ancestors with no success (or no log dir) are skipped. Ordered by id for a deterministic
    /// prompt.
    /// </summary>
    public IReadOnlyList<DependencyContextRef> BuildDependencyContext(TaskNode task)
    {
        JournalDocument document = _journal.Document;
        var refs = new List<DependencyContextRef>();

        foreach (string depId in _graph.TransitiveDependenciesOf(task.Id).OrderBy(d => d, StringComparer.Ordinal))
        {
            if (!_tasksById.TryGetValue(depId, out TaskNode? depTask) ||
                !document.Tasks.TryGetValue(depId, out TaskJournalEntry? entry))
            {
                continue;
            }

            AttemptRecord? current = CurrentSuccessfulAttempt(entry);
            if (current is null)
            {
                continue;
            }

            string absLogDir = ResolveAbsoluteLogDir(current.LogDir);
            refs.Add(new DependencyContextRef
            {
                TaskId = depId,
                Description = depTask.Description,
                LogDir = absLogDir,
                TranscriptPath = ExistingOrNull(Path.Combine(absLogDir, "transcript.md")),
                FragmentPath = ExistingOrNull(Path.Combine(absLogDir, "fragment.json"))
            });
        }

        return refs;
    }

    /// <summary>
    /// Select the succeeded attempt that produced a dependency's CURRENT state — the provenance
    /// the dependent must be pointed at. The authority is the <c>fragment.json</c> audit copy
    /// that <see cref="StateManager.MergeFragment"/> writes (atomically with the
    /// <c>state.json</c> update) into the merging attempt's log dir: merges advance in attempt
    /// order, so the succeeded attempt with the HIGHEST attempt number that has a
    /// <c>fragment.json</c> on disk is the one currently reflected in <c>state.json</c>. If no
    /// succeeded attempt ever merged a fragment (the dependency contributed nothing to state),
    /// fall back to the latest succeeded attempt so its transcript still serves as "what it did".
    /// Returns null when the dependency has no succeeded attempt.
    /// </summary>
    private AttemptRecord? CurrentSuccessfulAttempt(TaskJournalEntry entry)
    {
        IReadOnlyList<AttemptRecord> succeeded = entry.Attempts
            .Where(a => a.Outcome == AttemptOutcome.Succeeded)
            .OrderBy(a => a.Attempt)
            .ToList();

        if (succeeded.Count == 0)
        {
            return null;
        }

        // Prefer the latest attempt that actually merged a fragment (current-state provenance —
        // merges advance in attempt order, so the last fragment.json on disk is the live one);
        // otherwise the latest succeeded attempt (transcript-only, no state contribution).
        AttemptRecord? latestWithFragment = succeeded
            .LastOrDefault(a => File.Exists(Path.Combine(ResolveAbsoluteLogDir(a.LogDir), "fragment.json")));

        return latestWithFragment ?? succeeded[^1];
    }

    /// <summary>
    /// The salvage artifact a preserved attempt leaves in its own log dir (written by
    /// <see cref="AttemptArtifacts.WriteSalvagePatch"/>). Its PRESENCE is the record that the attempt left
    /// recoverable work — plan 31 §3.3 deliberately journals neither this nor the ref name below.
    /// </summary>
    internal const string SalvagePatchFileName = "prior-attempt.patch";

    /// <summary>
    /// The git ref an attempt's preserved working tree lives at, DERIVED from the task id and the attempt
    /// number exactly as <c>TaskExecutor.TryStash</c> mints it (issue #554, plan 31 §3.3). One owner of the
    /// format: the composed-prompt carry (<see cref="BuildPriorAttempts"/>) and the escalation gate context
    /// (<c>Scheduler.BuildGateContext</c>) both read it from here rather than re-spelling it, so a human
    /// answering a halt and the next attempt's prompt can never name different refs for the same work.
    /// </summary>
    internal static string SalvageRefNameFor(string taskId, int attempt) =>
        $"refs/guardrails/{taskId}/attempt-{attempt}";

    /// <summary>
    /// Build pointers to this task's PRIOR attempts (issue #26 Gaps 2 &amp; 3): every recorded
    /// attempt earlier than <paramref name="currentAttemptNumber"/>, most recent first, each
    /// with its transcript (what it did) and feedback (why it failed) if present.
    ///
    /// <para><b>Issue #554 — the salvage pointers.</b> An attempt that was preserved also carries the two
    /// pointers a next attempt needs to RECOVER that work: the patch in its log dir and the ref its tree
    /// was committed to. Neither is journaled (plan 31 §3.3): the patch is found by probing the log dir
    /// this walker already resolves, and the ref name is derived. This matters most on the ESCALATION
    /// path, whose <c>feedback.md</c> is never inlined into the next prompt (that path returns a null
    /// feedback path), so <see cref="PriorAttemptRef"/> is the only carrier the work has.</para>
    /// </summary>
    public IReadOnlyList<PriorAttemptRef> BuildPriorAttempts(string taskId, int currentAttemptNumber)
    {
        JournalDocument document = _journal.Document;
        if (!document.Tasks.TryGetValue(taskId, out TaskJournalEntry? entry))
        {
            return [];
        }

        var refs = new List<PriorAttemptRef>();
        foreach (AttemptRecord record in entry.Attempts
                     .Where(a => a.Attempt < currentAttemptNumber)
                     .OrderByDescending(a => a.Attempt))
        {
            string absLogDir = ResolveAbsoluteLogDir(record.LogDir);

            // Both salvage pointers are set together, or neither is: the ref is minted by the same call
            // that writes the patch, so advertising a ref for an attempt that left no patch would point
            // the next agent at something that may not exist.
            string? salvagePatch = ExistingOrNull(Path.Combine(absLogDir, SalvagePatchFileName));

            refs.Add(new PriorAttemptRef
            {
                Attempt = record.Attempt,
                Outcome = JournalJson.OutcomeToken(record.Outcome),
                LogDir = absLogDir,
                TranscriptPath = ExistingOrNull(Path.Combine(absLogDir, "transcript.md")),
                FeedbackPath = ExistingOrNull(Path.Combine(absLogDir, "feedback.md")),
                SalvagePatchPath = salvagePatch,
                SalvageRefName = salvagePatch is null ? null : SalvageRefNameFor(taskId, record.Attempt)
            });
        }

        return refs;
    }

    /// <summary>Resolve a journal's plan-relative log dir (forward-slash) to an absolute path.</summary>
    private string ResolveAbsoluteLogDir(string relativeLogDir) =>
        Path.GetFullPath(Path.Combine(_plan.PlanDirectory, relativeLogDir));

    private static string? ExistingOrNull(string path) => File.Exists(path) ? path : null;
}
