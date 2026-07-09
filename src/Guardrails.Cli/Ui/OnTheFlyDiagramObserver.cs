using Guardrails.Core.Execution;
using Guardrails.Core.Graph;
using Guardrails.Core.Journal;
using Guardrails.Core.Model;
using Guardrails.Core.State;

namespace Guardrails.Cli.Ui;

/// <summary>
/// A decorator <see cref="IRunObserver"/> that keeps a DURING-RUN live status diagram
/// (<c>logs/&lt;runId&gt;/diagram.html</c>) up to date as the run proceeds (issue #219, SSOT §10.1) —
/// the sibling of <see cref="OnTheFlyLogSiteObserver"/> for the DAG diagram. It WRAPS the real
/// observer (the log-site decorator, then the live <see cref="LiveRunObserver"/> or
/// <see cref="ConsoleRunObserver"/>), forwards every event verbatim, and AFTER forwarding re-renders
/// the diagram from an in-memory node-id → status map through <see cref="HtmlDiagramRenderer.Render(string, string, System.Collections.Generic.IReadOnlyDictionary{string, string}, System.Collections.Generic.IReadOnlyDictionary{string, string}, bool)"/>:
/// <list type="bullet">
///   <item><see cref="TaskStarting"/> flips a task container to <c>running</c> (a spinner badge).</item>
///   <item><see cref="GuardrailFinished"/> settles the guardrail LEAF <c>(task.Id, result.Name)</c> to
///     <c>passed</c>/<c>failed</c> — the densest, per-leaf live surface.</item>
///   <item><see cref="TaskFinished"/> settles the container (and, on success, every leaf) to its icon.</item>
/// </list>
///
/// <para>
/// <b>Write location.</b> The live diagram is written to <c>logs/&lt;runId&gt;/diagram.html</c> — NOT the
/// plan-root <c>diagram.html</c>, which is a tracked artifact the run must never modify (the user's
/// checkout is read-only for the run, SSOT §5/§10.1). It is gitignored runtime state, <c>--fresh</c>-
/// cleared, and never inspected by <c>graph --check</c>. The plan-root file keeps its stamped
/// <c>source-sha256</c> and never carries badges. Status is HASH-NEUTRAL chrome: the passed-in
/// <c>sourceHash</c> is computed upstream over <see cref="MermaidRenderer.SemanticContent"/> and never
/// sees the status map.
/// </para>
///
/// <para>
/// <b>During-run vs final.</b> Every forwarded event rewrites the during-run page (a
/// <c>&lt;meta http-equiv="refresh"&gt;</c> so a plain <c>file://</c> view re-reads itself, animated
/// spinners). <see cref="WriteFinalStatic"/>, called once at run end while the observer is in scope,
/// drops the refresh and shows every node settled — a durable post-mortem, sourced from the observer's
/// own in-memory map (strictly more accurate than re-deriving per-leaf status from the journal, which
/// does not retain it).
/// </para>
///
/// <para>
/// <b>Plan-level brackets.</b> The Full Flight Checks / Terminal Gate phases run OUTSIDE the DAG and fire
/// no <see cref="IRunObserver"/> event, so their badges are driven at CONTAINER granularity by the
/// concrete <see cref="PlanGuardrailsStarting"/>/<see cref="PlanGuardrailsFinished"/> methods
/// <c>RunCommand</c> calls around those phases — deliberately NOT added to the <see cref="IRunObserver"/>
/// interface (ISP: keep it small). The Full Flight Checks phase runs before this observer exists, so its
/// result is reflected via the journal SEED instead.
/// </para>
///
/// <para>
/// <b>Concurrency + atomicity (mirrors <see cref="OnTheFlyLogSiteObserver"/> exactly).</b> One
/// <c>lock (_gate)</c> guards BOTH the status-map mutation AND the projection/write; the snapshot is
/// taken inside the lock so the rendered view is consistent (M4 workers call in concurrently). The write
/// is atomic (<see cref="AtomicFile.WriteAllText"/>) so a browser never reads a torn file. Renders are
/// best-effort: an <see cref="IOException"/>/<see cref="UnauthorizedAccessException"/> is swallowed so a
/// render hiccup never flips a task outcome, changes the exit code, or aborts the run — the next event
/// re-renders.
/// </para>
/// </summary>
public sealed class OnTheFlyDiagramObserver : IRunObserver
{
    private const string DiagramFileName = "diagram.html";

    // Status tokens the overlay JS (HtmlDiagramRenderer's badge functions) understands. A node absent
    // from the map gets NO badge (the "pending / not started" state).
    private const string Running = "running";
    private const string Passed = "passed";
    private const string Failed = "failed";
    private const string NeedsHuman = "needs-human";
    private const string Blocked = "blocked";

    // Issue #333: on the FINAL settled page a node still `running` at run end is settled to this token so
    // it renders a muted "interrupted/unknown" badge instead of a frozen (un-animated) spinner arc. The
    // overlay JS has no explicit entry for it, so it falls through to the generic muted-circle badge — no
    // template change, so no committed diagram.html needs regenerating.
    private const string Interrupted = "interrupted";

    private readonly IRunObserver _inner;
    private readonly string _diagramPath;
    private readonly string _interactiveSource;
    private readonly string _sourceHash;
    private readonly IReadOnlyDictionary<string, string> _taskFolderTargets;
    private readonly DiagramStatusNodes _nodes;

    // node id -> status token. Mutated and projected under one lock — events arrive from concurrent M4
    // workers, and the render reads the whole map, so the two must not race.
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _statusByNodeId;

    /// <param name="inner">The real observer every event is forwarded to (the log-site decorator, then live/console).</param>
    /// <param name="logsRoot">The run's <c>logs/&lt;runId&gt;/</c> tree the diagram is written into.</param>
    /// <param name="plan">The plan whose DAG is drawn (source, hash, targets, and the status-node surface are derived from it).</param>
    /// <param name="journalForSeed">The freshly-read journal for resume seeding, or null for a fresh run (every node pending).</param>
    public OnTheFlyDiagramObserver(
        IRunObserver inner, string logsRoot, PlanDefinition plan, JournalDocument? journalForSeed)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _diagramPath = Path.Combine(logsRoot, DiagramFileName);
        _interactiveSource = MermaidRenderer.RenderInteractive(plan);
        _sourceHash = GraphSourceHash.Compute(plan);
        _taskFolderTargets = MermaidRenderer.TaskFolderTargets(plan);
        _nodes = MermaidRenderer.StatusNodes(plan);
        _statusByNodeId = BuildSeedMap(_nodes, plan, journalForSeed);
    }

    /// <summary>
    /// Write the initial (seeded) during-run diagram WITHOUT an observer instance — so the live path can
    /// write it, and print its link, BEFORE constructing <see cref="LiveRunObserver"/> (whose ctor starts
    /// the Spectre <c>AnsiConsole.Live</c> region; any console write into an active Live region corrupts
    /// the table, #145). Mirrors <see cref="OnTheFlyLogSiteObserver.WriteInitialIndex(string, string, System.Collections.Generic.IReadOnlyList{TaskNode}, System.Func{string, string})"/>.
    /// Best-effort.
    /// </summary>
    public static void WriteInitialDiagram(string logsRoot, PlanDefinition plan, JournalDocument? journalForSeed)
    {
        DiagramStatusNodes nodes = MermaidRenderer.StatusNodes(plan);
        Dictionary<string, string> seed = BuildSeedMap(nodes, plan, journalForSeed);
        string source = MermaidRenderer.RenderInteractive(plan);
        string hash = GraphSourceHash.Compute(plan);
        IReadOnlyDictionary<string, string> targets = MermaidRenderer.TaskFolderTargets(plan);
        TryRender(() => AtomicFile.WriteAllText(
            Path.Combine(logsRoot, DiagramFileName),
            HtmlDiagramRenderer.Render(source, hash, targets, seed, duringRun: true)));
    }

    /// <summary>
    /// Write the initial (seeded) during-run diagram from this instance's own seeded map. Used by the
    /// <c>--no-ui</c>/console path (no Spectre Live region to order around). Best-effort.
    /// </summary>
    public void WriteInitialDiagram() => RenderUnderLock(duringRun: true);

    /// <summary>
    /// Write the FINAL, settled diagram once at run end (no <c>meta refresh</c>, no spinner animation),
    /// from the observer's own in-memory status map — the durable post-mortem of the run (SSOT §10.1).
    /// Any node still <c>running</c> at this point (issue #333 — e.g. the Terminal Gate bracket whose
    /// phase threw before <see cref="PlanGuardrailsFinished"/> settled it, or a task whose cancellation
    /// propagated as an <see cref="OperationCanceledException"/> and skipped its settle) is rendered as an
    /// <c>interrupted</c> badge, never a frozen spinner. Best-effort.
    /// </summary>
    public void WriteFinalStatic() => RenderUnderLock(duringRun: false);

    // --- plan-level bracket badges (no IRunObserver event — called from RunCommand) ------------------

    /// <summary>Full Flight Checks phase is running → its bracket container shows a spinner.</summary>
    public void PlanPreflightsStarting() => SetContainerAndRender(_nodes.PlanPreflightsContainerId, Running);

    /// <summary>
    /// Full Flight Checks phase finished. On pass, the bracket container AND every plan-preflight leaf
    /// settle to <c>passed</c> (all checks passed). On failure, the container shows <c>needs-human</c>;
    /// the leaves are left as-is (no per-check event yet — TODO(#219 follow-on): a per-check callback
    /// would badge which plan-level check failed).
    /// </summary>
    public void PlanPreflightsFinished(bool passed) =>
        SettlePlanLevel(_nodes.PlanPreflightsContainerId, _nodes.PlanPreflightLeaves.Values, passed);

    /// <summary>Terminal Gate phase is running → its bracket container shows a spinner.</summary>
    public void PlanGuardrailsStarting() => SetContainerAndRender(_nodes.PlanGuardrailsContainerId, Running);

    /// <summary>
    /// Terminal Gate phase finished. On pass, the bracket container AND every plan-guardrail leaf settle
    /// to <c>passed</c>. On failure, the container shows <c>needs-human</c> (per-leaf is a follow-on).
    /// </summary>
    public void PlanGuardrailsFinished(bool passed) =>
        SettlePlanLevel(_nodes.PlanGuardrailsContainerId, _nodes.PlanGuardrailLeaves.Values, passed);

    // --- IRunObserver: forward EVERY event (transparent decorator), badge the diagram where relevant ---

    public void TaskStarting(TaskNode task)
    {
        _inner.TaskStarting(task);
        UpdateAndRenderDuringRun(map =>
        {
            if (_nodes.TaskContainers.TryGetValue(task.Id, out string? id))
            {
                map[id] = Running;
            }
        });
    }

    public void AttemptStarting(TaskNode task, int attempt, int budget) =>
        _inner.AttemptStarting(task, attempt, budget);

    public void GuardrailFinished(TaskNode task, GuardrailResult result)
    {
        _inner.GuardrailFinished(task, result);
        UpdateAndRenderDuringRun(map =>
        {
            if (_nodes.TaskGuardrailLeaves.TryGetValue((task.Id, result.Name), out string? id))
            {
                map[id] = result.Passed ? Passed : Failed;
            }
        });
    }

    public void TaskFinished(TaskResult result)
    {
        _inner.TaskFinished(result);
        UpdateAndRenderDuringRun(map => ApplyTaskSettled(map, result.TaskId, result.Outcome));
    }

    public void PlanHashMismatch(string previousPlanHash) => _inner.PlanHashMismatch(previousPlanHash);

    public void DecisionRecorded(DecisionEntry entry) => _inner.DecisionRecorded(entry);

    public void ParallelismClampedNoProvider(int requested) => _inner.ParallelismClampedNoProvider(requested);

    public void CleanupFailed(string owner, Exception error) => _inner.CleanupFailed(owner, error);

    public void PromptPaused(TaskNode task, string reason, TimeSpan backoff, int pauseCount) =>
        _inner.PromptPaused(task, reason, backoff, pauseCount);

    public void OutOfScopeStripped(TaskNode task, IReadOnlyList<WriteScopeOffense> stripped) =>
        _inner.OutOfScopeStripped(task, stripped);

    public void WaveStarting(WaveNode wave, int index, int total) => _inner.WaveStarting(wave, index, total);

    public void WaveFinished(WaveNode wave, WaveStatus status, bool skipped) =>
        _inner.WaveFinished(wave, status, skipped);

    // --- projection --------------------------------------------------------------------------------

    /// <summary>
    /// Settle a task container (and, on success, every one of its leaves) from its terminal outcome. A
    /// cancelled task clears its badge (it will resume). A green task's preflight leaves settle HERE (they
    /// have no per-check event) — the guardrail leaves already flipped via <see cref="GuardrailFinished"/>,
    /// re-affirmed for the durable page. TODO(#219 follow-on): a per-preflight-check event would badge task
    /// preflight leaves LIVE instead of only at container settle.
    /// </summary>
    private void ApplyTaskSettled(Dictionary<string, string> map, string taskId, TaskOutcome outcome)
    {
        if (!_nodes.TaskContainers.TryGetValue(taskId, out string? containerId))
        {
            return;
        }

        if (outcome == TaskOutcome.Cancelled)
        {
            map.Remove(containerId); // will resume — clear the spinner, show no settled badge
            return;
        }

        map[containerId] = ContainerToken(outcome);

        if (outcome is TaskOutcome.Succeeded or TaskOutcome.Skipped)
        {
            foreach ((var key, string leafId) in _nodes.TaskGuardrailLeaves)
            {
                if (string.Equals(key.TaskId, taskId, StringComparison.Ordinal))
                {
                    map[leafId] = Passed;
                }
            }

            foreach ((var key, string leafId) in _nodes.TaskPreflightLeaves)
            {
                if (string.Equals(key.TaskId, taskId, StringComparison.Ordinal))
                {
                    map[leafId] = Passed;
                }
            }
        }
    }

    /// <summary>Map a finished task's outcome to its container badge token.</summary>
    private static string ContainerToken(TaskOutcome outcome) => outcome switch
    {
        TaskOutcome.Succeeded or TaskOutcome.Skipped => Passed,
        TaskOutcome.Blocked => Blocked,
        // ActionFailed / GuardrailFailed / InvalidFragment / NeedsHuman / RateLimited are needs-human terminal.
        _ => NeedsHuman,
    };

    private void SetContainerAndRender(string containerId, string token) =>
        UpdateAndRenderDuringRun(map => map[containerId] = token);

    private void SettlePlanLevel(string containerId, IEnumerable<string> leafIds, bool passed) =>
        UpdateAndRenderDuringRun(map =>
        {
            map[containerId] = passed ? Passed : NeedsHuman;
            if (passed)
            {
                foreach (string leafId in leafIds)
                {
                    map[leafId] = Passed;
                }
            }
        });

    /// <summary>
    /// Mutate the status map and re-render the during-run page under ONE lock, snapshotting inside the
    /// lock so the rendered view is consistent (SSOT §10.1 / D6).
    /// </summary>
    private void UpdateAndRenderDuringRun(Action<Dictionary<string, string>> mutate)
    {
        lock (_gate)
        {
            mutate(_statusByNodeId);
            RenderSnapshotLocked(duringRun: true);
        }
    }

    /// <summary>Snapshot + render under the lock, without mutation (initial / final writes).</summary>
    private void RenderUnderLock(bool duringRun)
    {
        lock (_gate)
        {
            RenderSnapshotLocked(duringRun);
        }
    }

    /// <summary>Caller holds <c>_gate</c>: take a stable snapshot and atomically write the page (best-effort).</summary>
    private void RenderSnapshotLocked(bool duringRun)
    {
        var snapshot = new Dictionary<string, string>(_statusByNodeId, StringComparer.Ordinal);

        // Issue #333: the FINAL settled page (duringRun == false) must never leave a spinner. Any node
        // still `running` at run end — the Terminal Gate bracket whose phase threw before its settle ran,
        // or a task whose cancellation propagated as an OperationCanceledException and skipped its settle —
        // is mapped to `interrupted` (a muted badge) rather than the frozen, un-animated spinner arc a
        // `running` token would otherwise draw. The during-run pages keep the live spinner untouched.
        if (!duringRun)
        {
            foreach (string nodeId in snapshot.Where(kv => kv.Value == Running).Select(kv => kv.Key).ToList())
            {
                snapshot[nodeId] = Interrupted;
            }
        }

        TryRender(() => AtomicFile.WriteAllText(
            _diagramPath,
            HtmlDiagramRenderer.Render(_interactiveSource, _sourceHash, _taskFolderTargets, snapshot, duringRun)));
    }

    /// <summary>
    /// Seed the node-id → status map from the resumed journal (SSOT §10.1 resume correctness): an
    /// already-succeeded task's container + leaves → passed; a needs-human/failed task's container →
    /// needs-human (its last attempt's failed guardrail leaves → failed); a blocked task → blocked. The
    /// plan-level phases seed from their journal sections. A fresh run (null journal) seeds nothing —
    /// every node pending (no badge) until an event fires.
    /// </summary>
    private static Dictionary<string, string> BuildSeedMap(
        DiagramStatusNodes nodes, PlanDefinition plan, JournalDocument? journal)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (journal is null)
        {
            return map;
        }

        foreach (TaskNode task in plan.Tasks)
        {
            if (!journal.Tasks.TryGetValue(task.Id, out TaskJournalEntry? entry)
                || !nodes.TaskContainers.TryGetValue(task.Id, out string? containerId))
            {
                continue;
            }

            switch (entry.Status)
            {
                case Core.Journal.TaskStatus.Succeeded:
                    map[containerId] = Passed;
                    SeedTaskLeavesPassed(map, nodes, task.Id);
                    break;

                case Core.Journal.TaskStatus.NeedsHuman:
                case Core.Journal.TaskStatus.Failed:
                    map[containerId] = NeedsHuman;
                    AttemptRecord? last = entry.Attempts.Count > 0 ? entry.Attempts[^1] : null;
                    if (last is not null)
                    {
                        // #338: route each failed check to the leaf map for its KIND, not by Name alone. A
                        // task-preflight-failed attempt (§7) records its failed PREFLIGHT check names in
                        // FailedGuardrails — those belong on the `_pf_` leaves; every other failing outcome
                        // records GUARDRAIL names → the `_gr_` leaves. The attempt's own Outcome is the
                        // honest kind signal (no Name-guessing). Keying by Name against only
                        // TaskGuardrailLeaves would (a) never paint a failed preflight leaf on the seed, and
                        // (b) since #332 makes a same-Name preflight + guardrail legal in one task (separate
                        // `_pf_`/`_gr_` namespaces), mis-paint the GUARDRAIL leaf for a failed preflight.
                        IReadOnlyDictionary<(string TaskId, string CheckName), string> leaves =
                            last.Outcome == AttemptOutcome.TaskPreflightFailed
                                ? nodes.TaskPreflightLeaves
                                : nodes.TaskGuardrailLeaves;
                        foreach (FailedGuardrail fg in last.FailedGuardrails)
                        {
                            if (leaves.TryGetValue((task.Id, fg.Name), out string? leaf))
                            {
                                map[leaf] = Failed;
                            }
                        }
                    }

                    break;

                case Core.Journal.TaskStatus.Blocked:
                    map[containerId] = Blocked;
                    break;

                // Pending / Running → no badge (a resumed Running re-runs; a Pending hasn't started).
            }
        }

        if (journal.PlanPreflights is { } pf)
        {
            bool passed = pf.Status == PlanPhaseStatus.Passed;
            map[nodes.PlanPreflightsContainerId] = passed ? Passed : NeedsHuman;
            foreach (PlanPreflightCheck c in pf.Checks)
            {
                if (nodes.PlanPreflightLeaves.TryGetValue(c.Name, out string? leaf))
                {
                    map[leaf] = c.Passed ? Passed : Failed;
                }
            }
        }

        if (journal.PlanGuardrails is { } pg)
        {
            bool passed = pg.Status == PlanPhaseStatus.Passed;
            map[nodes.PlanGuardrailsContainerId] = passed ? Passed : NeedsHuman;
            if (passed)
            {
                foreach (string leaf in nodes.PlanGuardrailLeaves.Values)
                {
                    map[leaf] = Passed;
                }
            }
            else
            {
                foreach (FailedGuardrail fc in pg.FailedChecks)
                {
                    if (nodes.PlanGuardrailLeaves.TryGetValue(fc.Name, out string? leaf))
                    {
                        map[leaf] = Failed;
                    }
                }
            }
        }

        return map;
    }

    private static void SeedTaskLeavesPassed(Dictionary<string, string> map, DiagramStatusNodes nodes, string taskId)
    {
        foreach ((var key, string leafId) in nodes.TaskGuardrailLeaves)
        {
            if (string.Equals(key.TaskId, taskId, StringComparison.Ordinal))
            {
                map[leafId] = Passed;
            }
        }

        foreach ((var key, string leafId) in nodes.TaskPreflightLeaves)
        {
            if (string.Equals(key.TaskId, taskId, StringComparison.Ordinal))
            {
                map[leafId] = Passed;
            }
        }
    }

    /// <summary>
    /// Run a render action, swallowing IO failures: the live diagram is a UX nicety and must never flip a
    /// task's outcome or abort the run. A transient lock/torn-read is retried by the next event.
    /// </summary>
    private static void TryRender(Action render)
    {
        try
        {
            render();
        }
        catch (IOException)
        {
            // best-effort — the next event re-renders
        }
        catch (UnauthorizedAccessException)
        {
            // best-effort — never let a logs-tree permission hiccup abort the run
        }
    }
}
