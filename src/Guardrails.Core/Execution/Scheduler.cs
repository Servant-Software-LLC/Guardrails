using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Guardrails.Core.Graph;
using Guardrails.Core.Loading;
using Guardrails.Core.Model;
using Guardrails.Core.State;
using JournalTaskStatus = Guardrails.Core.Journal.TaskStatus;

namespace Guardrails.Core.Execution;

/// <summary>
/// The M4 DAG scheduler. Kahn-style readiness (a task becomes ready when every
/// dependency is green) feeding an unbounded <see cref="Channel{T}"/> consumed by
/// <c>maxParallelism</c> workers. A task that ends <c>needs-human</c> (or otherwise
/// non-green) blocks its TRANSITIVE dependents immediately while independent branches
/// keep running — every completed task is durable progress in the journal, and one run
/// surfaces every needs-human halt instead of one per run.
/// </summary>
public sealed class Scheduler
{
    private readonly PlanDefinition _plan;
    private readonly ITaskExecutor _executor;
    private readonly ISchedulerJournal _journal;
    private readonly IWorktreeProvider? _worktreeProvider;
    private readonly IRunObserver _observer;
    private readonly int _maxParallelism;
    private readonly IReVerifier? _reVerifier;

    private readonly object _gate = new();

    // Serialize-merges lock (plan 08 §3): one integration-settle at a time so that
    // non-FF union re-verify and B1 rollback are atomic w.r.t. other settling workers.
    private readonly SemaphoreSlim _integrationLock = new(1, 1);

    // First unexpected (non-cancellation) executor fault wins; surfaced after WhenAll so the
    // run terminates deterministically with a harness error instead of hanging (see WorkerLoopAsync).
    private Exception? _fault;

    private readonly IAiMergeWorker? _aiMergeWorker;

    // Part C (#274, SSOT §7.2): the operator-authorized safe-rewind plan captured by the CLI's pre-DAG
    // probe (S + reset target + the plan-branch tip the operator saw). Non-null ONLY on a Prompt-policy
    // run the CLI already confirmed OUTSIDE the live region with a `y`. Core never prompts itself, so
    // without this a Prompt-policy safe drift HALTS. The Scheduler executes the CAPTURED plan (verifying
    // it still matches + a tip compare-and-swap), never a possibly-diverged recompute. autonomyPolicy=auto
    // auto-resolves on its own fresh decision; halt/unconfirmed-prompt halt.
    private readonly DriftAuthorization? _driftAuthorization;

    // #254 M2b (SSOT §14.6): the wave dirs the CLI already confirmed rewinding for a Prompt-policy run
    // (an operator `y` OUTSIDE the live region). A wave-scoped rewind is ALWAYS a safe trailing suffix
    // (§14.8), so — unlike the task-level DriftAuthorization — this needs only the set of authorized wave
    // dirs. Empty (the default) for auto (resolves on its own) and halt / unconfirmed prompt (halts).
    private readonly IReadOnlySet<string> _waveDriftAuthorized;

    // #360 Phase 1 (SSOT §14.4, doc 11 §9): the between-wave breakdown actor. Null in serial mode (no
    // integration worktree for materialized upstream) and for a plan that declares no prompt runner —
    // either leaves the JIT checkpoint honest-halting exactly as before. The `overwatch`-style seam.
    private readonly WaveBreakdownInvoker? _breakdownInvoker;

    // #360 Phase 1: the JIT-checkpoint breakdown decisions the CLI captured BEFORE the live region under a
    // `prompt` policy (waveDir → operator's y/N). The Scheduler cannot prompt (it never touches the console,
    // and the checkpoint fires inside the Spectre live region), so — mirroring the wave-drift confirm — the
    // CLI prompts up front and passes the answers here. Absent entry ⇒ non-interactive ⇒ honest-halt.
    private readonly IReadOnlyDictionary<string, bool> _breakdownConfirmations;

    // #361 Phase 3 (doc 12 §4/§7): the classify-then-act escalation machinery, injected by SchedulerFactory
    // ONLY for a non-interactive `autonomyPolicy: auto` run that carries an `autonomy` block (else all three
    // are null → the dial is INERT and the run is byte-identical to today, the §3.2 back-compat guarantee).
    // At a task-level needs-human / rate-limit gate (and the JIT wave-checkpoint) the Scheduler CLASSIFIES the
    // stop with GateClassifier.Classify and acts: escalate via the sink, RECORD a proceed-best-guess, or run a
    // bounded class-(b) retry — every gate landing in decisions[] + the run-level autonomy.jsonl (§6). The
    // production sink is FileEscalationSink; the judge/blocker-retry may be null (a script-only plan resolves
    // no overwatch runner → a judgment call escalates, the safe default).
    private readonly IEscalationSink? _escalationSink;
    private readonly CriticalityJudge? _criticalityJudge;
    private readonly BlockerRetry? _blockerRetry;

    // #361 Phase 3 (doc 12 §4.1/§7.4): the reply channel's in-run handoff. A below-threshold judgment call
    // records a best-guess (ActOnJudgmentCallAsync) whose text must reach the NEXT attempt's composed prompt —
    // but the executor terminates a needs-human short-circuit WITHOUT retrying, so the Scheduler re-drives one
    // bounded injected attempt. This map carries taskId → best-guess text from the classify step to that
    // OnSettledAsync re-drive; an entry lives only until the re-drive consumes it (TryRemove).
    private readonly ConcurrentDictionary<string, string> _pendingBestGuessInjection =
        new(StringComparer.Ordinal);

    // #457: the integration handle of a delivery HELD BACK by Finalize because the plan declares a
    // <plan>/guardrails/ terminal gate whose verdict only the CLI has. Non-null between RunAsync
    // returning and CompleteDeferredDelivery being called; cleared there so a delivery can never run
    // twice. Written once on the single-threaded Finalize path after every worker has quiesced.
    private IntegrationHandle? _pendingDeliveryIntegration;

    // #545 part 3 (plan 31 §5.2): the mid-run plan-folder edit watch. Constructed HERE rather than at the
    // composition root — unlike the Scheduler's other collaborators — because nothing depends on the seam
    // being injectable: the watch has no substitutable behaviour any test needs to fake, and it is built
    // from the PlanDefinition this Scheduler already holds. Null only when construction itself failed (see
    // TryCreatePlanEditWatch), which leaves the advisory inert and the run otherwise untouched.
    private readonly LivePlanEditWatch? _planEditWatch;

    // The plan-edit observations this run raised, in order, for the end-of-run advisory
    // (RunReport.Observations). Each is ALSO already durable in decisions[] and already emitted live;
    // this is the report's copy. Written under _gate by PollPlanEdits.
    private readonly List<DecisionEntry> _planEditObservations = [];

    public Scheduler(
        PlanDefinition plan,
        ITaskExecutor executor,
        ISchedulerJournal journal,
        IWorktreeProvider? worktreeProvider = null,
        IRunObserver? observer = null,
        int? maxParallelism = null,
        IReVerifier? reVerifier = null,
        IAiMergeWorker? aiMergeWorker = null,
        DriftAuthorization? driftAuthorization = null,
        IReadOnlySet<string>? waveDriftAuthorized = null,
        WaveBreakdownInvoker? breakdownInvoker = null,
        IReadOnlyDictionary<string, bool>? breakdownConfirmations = null,
        IEscalationSink? escalationSink = null,
        CriticalityJudge? criticalityJudge = null,
        BlockerRetry? blockerRetry = null)
    {
        _plan = plan;
        _executor = executor;
        _journal = journal;
        _worktreeProvider = worktreeProvider;
        _observer = observer ?? IRunObserver.Null;
        _reVerifier = reVerifier;
        _aiMergeWorker = aiMergeWorker;
        _driftAuthorization = driftAuthorization;
        _waveDriftAuthorized = waveDriftAuthorized ?? new HashSet<string>(StringComparer.Ordinal);
        _breakdownInvoker = breakdownInvoker;
        _breakdownConfirmations = breakdownConfirmations ?? new Dictionary<string, bool>(StringComparer.Ordinal);
        _escalationSink = escalationSink;
        _criticalityJudge = criticalityJudge;
        _blockerRetry = blockerRetry;

        // Baseline the definition surface as early as the Scheduler can see the plan: the watch takes its
        // baseline in its own constructor (not at the first Poll), so an operator edit landing between plan
        // load and the first scheduler boundary is still reported — which is the point.
        _planEditWatch = TryCreatePlanEditWatch(plan);

        int requested = Math.Max(1, maxParallelism ?? plan.Config.MaxParallelism);

        // F7 HARD GUARD: worktree mode (parallelism > 1) requires a worktree provider for
        // per-task isolation. With no provider, parallel workers would share the single
        // workspace and race undetectably (the rejected shared-workspace corruption class).
        // CLAMP to 1 (serial shared-workspace, the pre-plan-08 model) rather than running
        // an unsafe parallel run — and tell the observer so the demotion is not silent.
        if (requested > 1 && _worktreeProvider is null)
        {
            _observer.ParallelismClampedNoProvider(requested);
            requested = 1;
        }

        _maxParallelism = requested;
    }

    /// <summary>
    /// Run the plan to quiescence: every task green, blocked, or needs-human — or the
    /// token cancelled (in-flight attempts are journaled back to pending by the
    /// executor; unstarted tasks are reported <see cref="TaskOutcome.Cancelled"/>).
    /// </summary>
    public async Task<RunReport> RunAsync(PlanDefinition plan, CancellationToken cancellationToken = default)
    {
        var fullGraph = new DependencyGraph(plan.Tasks);
        if (fullGraph.FindCycle() is { } cycle)
        {
            // Validation (GR2007) catches this before a run; this guard keeps the
            // scheduler safe when embedded directly.
            throw new InvalidOperationException($"Dependency cycle: {string.Join(" -> ", cycle)}");
        }

        // #229 / DoR §6.5 — the RUN-START half of the advisory's de-duplication ruling: say it ONCE,
        // up front, before anything is paid for. The JIT half (record into judge provenance always,
        // log only on a DIFFERENCE) lives at the guardrail boundary and deliberately stays quiet when
        // it agrees with what this walk predicted. Runs before the integration handle exists because
        // it touches nothing but the plan: no worktree, no journal, no DAG.
        EmitVerifierAdvisories(plan);

        // Shared, CONTINUOUS run state across every wave (SSOT §14): ONE settled map (all waves' task
        // results coexist in the final report), ONE directoryOwner map for the end-of-run sweep, ONE
        // runId, ONE integration handle / plan branch, and ONE journal (_journal). A WAVED run drives N
        // per-wave DAG drains against THIS shared state — it never forks a fresh integration worktree /
        // runId / journal per wave (the M2a continuity blocker; SSOT §14.4).
        var settled = new Dictionary<string, TaskResult>(StringComparer.Ordinal);
        var directoryOwner = new Dictionary<string, string>(StringComparer.Ordinal);

        // Create the ONE integration handle for the whole run (worktree mode only).
        string runId = Guid.NewGuid().ToString("N")[..8];
        IntegrationHandle? integ;
        try
        {
            integ = _worktreeProvider?.CreateIntegration(
                planName: Path.GetFileName(plan.PlanDirectory),
                runId: runId,
                cancellationToken);
        }
        catch (Exception ex)
        {
            // Issue #150/#160 — CreateIntegration runs BEFORE the worker loop's fault capture, so a
            // setup fault (e.g. a plan folder with no usable name component → the #160 guard's clear
            // diagnostic, or git unavailable) would otherwise escape unhandled as a raw stack trace.
            // Surface it through the same honest-halt ABORTED report the CLI renders cleanly.
            return BuildReport(plan, settled, cancelled: cancellationToken.IsCancellationRequested)
                with { Abort = BuildAbort(ex) };
        }

        // Whole-plan resume reconcile — ONCE, before any wave. Prune this run's stale segment refs,
        // replay a surviving Part C rewind-intent marker (crash-atomicity), then read the plan branch's
        // Guardrails-Task: trailers (the durable cross-run resume truth). Shared by every wave's drain.
        IReadOnlyDictionary<string, PlanBranchTaskRecord> planBranchRecords =
            new Dictionary<string, PlanBranchTaskRecord>(StringComparer.Ordinal);
        bool trailerTracking = _worktreeProvider?.TracksPlanBranchTrailers == true && integ is not null;
        if (_worktreeProvider is { } wp && integ is { } activeInteg)
        {
            wp.PruneStaleRunBranches(runId, activeInteg);

            // Part C crash-atomicity (#274, SSOT §7.2): replay a rewind-intent marker left by a run killed
            // BETWEEN a plan-branch rewind and its journal-resets. Runs BEFORE the reconcile read below so
            // the replayed statuses are seen. Idempotent.
            ReplayRewindIntentIfPresent();

            planBranchRecords = wp.ReconcileFromPlanBranch(activeInteg);
        }

        // Dispatch: a WAVED plan runs its waves in strict order behind hard barriers (SSOT §14.4); a
        // FLAT plan is one drain over all tasks (the pre-M2b behaviour, unchanged).
        return plan.IsWaved
            ? await RunWavedAsync(plan, integ, settled, directoryOwner, planBranchRecords, trailerTracking, cancellationToken).ConfigureAwait(false)
            : await RunFlatAsync(plan, fullGraph, integ, settled, directoryOwner, planBranchRecords, trailerTracking, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The DoR §6.5 (#229) RUN-START verifier advisory: walk the plan once, ask
    /// <see cref="Prompts.VerifierAdvisory"/> which tasks are affected, and raise ONE
    /// <see cref="IRunObserver.VerifierAdvisoryFound"/> per affected task. A run whose judges are all
    /// strong enough raises nothing and prints nothing at all.
    ///
    /// <para><b>It never halts and never blocks the DAG.</b> §12.6 forbids a verifier condition from
    /// failing a build, so every step here is contained: a task whose route or judge cannot be
    /// resolved is SKIPPED rather than surfaced as an error, and the whole walk is wrapped so a
    /// diagnostic can never abort the run it was added to describe. A diagnostic that can kill a run
    /// is strictly worse than no diagnostic.</para>
    ///
    /// <para><b>The rule is not re-derived here.</b> "Is this judge weak" is decided ONCE, by
    /// <see cref="Prompts.VerifierAdvisory"/> over <see cref="Prompts.TierResolver"/>'s resolutions;
    /// this method only supplies the (actor, judge) pairs and prints what comes back. A second
    /// implementation of the condition is the exact divergence D22a forbids — and it would stay
    /// invisible until the day the two answers disagreed.</para>
    ///
    /// <para><c>plan.Tasks</c> is already the flattened union of every wave's tasks, so ONE walk
    /// covers a waved plan as well as a flat one — deliberately, because the advisory is about what
    /// the operator is about to pay for, and that is the whole run rather than wave 1.</para>
    /// </summary>
    private void EmitVerifierAdvisories(PlanDefinition plan)
    {
        try
        {
            List<Prompts.VerifierAdvisoryPair> pairs = [];

            foreach (TaskNode task in plan.Tasks)
            {
                // Contained PER TASK: an unreadable guardrail prompt file, or a registry a resolver
                // rejects, costs that task's advisory line and nothing else. The alternative — one
                // throw taking the run down before the DAG starts — is the failure mode this whole
                // walk is not allowed to have.
                try
                {
                    // The PROMPT guard, carried from TaskExecutor.ResolveRoute (which is private, so the
                    // call is spelled again here rather than shared). A script action resolves no route
                    // and grades against no judge; walking it anyway would emit advisories an operator
                    // cannot act on, which is how a diagnostic teaches people to stop reading it.
                    if (task.Action.Kind != ActionKind.Prompt)
                    {
                        continue;
                    }

                    Prompts.TierResolution actor =
                        Prompts.TierResolver.Resolve(task.Action, plan.Config, cliDefaultModel: null);

                    // Every PROMPT guardrail is a judge; a script guardrail runs no model and can never
                    // be one. Each contributes a pair, so a task whose FIRST prompt guardrail resolves a
                    // strong judge and whose second does not is still reported — while the emit loop
                    // below keeps the operator-facing contract at one line per affected task.
                    foreach (GuardrailDefinition judgeGuardrail in task.Guardrails)
                    {
                        if (judgeGuardrail.Kind != ActionKind.Prompt)
                        {
                            continue;
                        }

                        // Rule 1's other spelling — the judge prompt's frontmatter `runner` — read the
                        // same way GuardrailRunner reads it at the JIT boundary, so the preflight is a
                        // model of that resolution rather than a different one.
                        string? runnerPin = PromptExecutionSupport
                            .LoadPromptFile(judgeGuardrail.Path).Frontmatter.Runner;

                        Prompts.JudgeResolution judge = Prompts.TierResolver.ResolveJudge(
                            judgeGuardrail, runnerPin, actor, plan.Config, cliDefaultModel: null);

                        pairs.Add(new Prompts.VerifierAdvisoryPair(task.Id, actor, judge));
                    }
                }
                catch (Exception)
                {
                    // Skip this task. Nothing to report and nothing to fail: see the class remark above.
                }
            }

            // THE rule owner decides which pairs are findings and in what order — Preflight is where
            // "one line per AFFECTED task" lives, next to the condition it depends on.
            var reported = new HashSet<string>(StringComparer.Ordinal);
            foreach (Prompts.VerifierAdvisoryFinding finding in Prompts.VerifierAdvisory.Preflight(pairs))
            {
                string taskId = finding.TaskId ?? string.Empty;

                // One line per affected TASK, not per affected guardrail: several judges on one task
                // usually resolve identically, and repeating the same sentence three times before the
                // run starts is how an operator learns to skip the block entirely.
                if (reported.Add(taskId))
                {
                    _observer.VerifierAdvisoryFound(taskId, finding.Message);
                }
            }
        }
        catch (Exception)
        {
            // The outer containment. An advisory is not allowed to be the reason a run does not start.
        }
    }

    /// <summary>
    /// Build the mid-run plan-folder edit watch (plan 31 §5.2, #545 part 3), or null if that fails. The
    /// watch is an ADVISORY, and an advisory that can be the reason a run does not start is strictly worse
    /// than no advisory. Its constructor reads the whole plan's definition surface off disk, so a plan
    /// folder that vanished (or a synthetic <see cref="TaskNode"/> whose directory is not a real path) must
    /// degrade to silence rather than take the run down before the DAG starts.
    /// </summary>
    private static LivePlanEditWatch? TryCreatePlanEditWatch(PlanDefinition plan)
    {
        try
        {
            return new LivePlanEditWatch(plan);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// What one poll boundary does with its result (plan 31 §5.2): when an OPERATOR edited the definition
    /// surface since the last boundary, record ONE <c>plan-edit</c>/<c>observed</c> entry on all three
    /// surfaces — live (<see cref="IRunObserver.DecisionRecorded"/>), durable (<c>decisions[]</c>) and
    /// terminal (<see cref="RunReport.Observations"/>, rendered by the CLI at end of run). Empty (or a run
    /// whose watch failed to construct) is silence.
    ///
    /// <para>The two boundaries that call this are the ones that already exist — task DISPATCH and task
    /// SETTLE — and each does its own <see cref="LivePlanEditWatch.Poll"/> on the scheduler's own thread.
    /// No new thread, no daemon, and no <c>FileSystemWatcher</c>: that would fire on the harness's own
    /// writes under the plan folder, needs a debounce policy, and is platform-quirky. Polling costs at most
    /// 2N recomputes of the definition surface per run — a few hundred KB of reads against a run that spends
    /// dollars per attempt — and the price is timeliness: the warning appears at the NEXT scheduler boundary
    /// rather than instantly, and a single long task retrying alone can delay it by one attempt (§11 risk 3).</para>
    ///
    /// <para>Each <c>Poll()</c> is taken under the Scheduler's EXISTING <see cref="_gate"/> (no new lock):
    /// both boundaries are reached from the parallel worker loop, and <see cref="LivePlanEditWatch"/>
    /// replaces its whole baseline per call, so two concurrent polls would race on it. Serializing there is
    /// also what makes "exactly one entry per edit" true — the first poll to run consumes the diff and
    /// re-baselines. The journal write and the observer notification are deliberately OUTSIDE the gate:
    /// neither touches scheduler state, and neither should hold a worker-visible lock over file IO.
    /// <see cref="LivePlanEditWatch.Poll"/> never throws (an unreadable file is carried forward, not
    /// reported), so no try/catch is needed around it.</para>
    /// </summary>
    private void RecordPlanEdits(IReadOnlyList<PlanEdit>? edits)
    {
        if (edits is not { Count: > 0 })
        {
            return;
        }

        DecisionEntry entry = PlanEditDecisions.Observed(_plan.Config.AutonomyPolicy, edits);
        lock (_gate)
        {
            _planEditObservations.Add(entry);
        }

        _journal.RecordDecision(entry);
        _observer.DecisionRecorded(entry);
    }

    /// <summary>
    /// Silently re-baseline the WHOLE plan after a harness-authored definition write (plan 31 §5.3) — a
    /// harness write is not an operator edit, and an advisory that fires on the harness's own writes stops
    /// being read (#229).
    ///
    /// <para><b>Plan-wide, never per-task</b>, because three of the five writers have authority over files
    /// outside the unit they nominally act on — most of all the JIT breakdown, which runs a Claude subprocess
    /// rooted at the PLAN directory with <c>Write</c>/<c>Edit</c>/<c>Bash</c> at <c>acceptEdits</c> and no
    /// containment hook. A per-task re-baseline would leave the watch reporting the harness's own writes as
    /// operator edits.</para>
    ///
    /// <para><b>This is a workaround for #557, not a fix.</b> Re-baselining plan-wide is only necessary
    /// because <c>WaveBreakdownInvoker</c> has plan-wide write authority it should not have. Until #557
    /// scopes that authority to the wave being authored, the watch pays for the reach by going blind to any
    /// operator edit landing in the same window as a JIT breakdown — a real, accepted hole in this feature,
    /// caused by a hole in a different one.</para>
    /// </summary>
    private void RebaselinePlanEdits()
    {
        if (_planEditWatch is not { } watch)
        {
            return;
        }

        lock (_gate)
        {
            watch.Rebaseline();
        }
    }

    /// <summary>The report's copy of this run's plan-edit observations, read under <see cref="_gate"/>.</summary>
    private IReadOnlyList<DecisionEntry> PlanEditObservationsSnapshot()
    {
        lock (_gate)
        {
            return _planEditObservations.Count == 0 ? [] : _planEditObservations.ToArray();
        }
    }

    /// <summary>
    /// A FLAT plan: ONE drain over every task, then the legacy terminal integration gate (§3.3, when no
    /// plan-level <c>&lt;plan&gt;/guardrails/</c> folder supersedes it) + delivery + cleanup. Byte-for-byte
    /// the pre-M2b behaviour, now expressed on top of the shared <see cref="DrainAsync"/>.
    /// </summary>
    private async Task<RunReport> RunFlatAsync(
        PlanDefinition plan, DependencyGraph graph, IntegrationHandle? integ,
        Dictionary<string, TaskResult> settled, Dictionary<string, string> directoryOwner,
        IReadOnlyDictionary<string, PlanBranchTaskRecord> planBranchRecords, bool trailerTracking,
        CancellationToken cancellationToken)
    {
        DrainOutcome drain = await DrainAsync(
            plan, plan.Tasks, graph, integ, settled, directoryOwner, planBranchRecords, trailerTracking, cancellationToken)
            .ConfigureAwait(false);

        if (drain.ReadAbort is { } readAbort)
        {
            // Pre-schedule read/git abort during the drift check — nothing scheduled, no sweep.
            return BuildReport(plan, settled, cancelled: cancellationToken.IsCancellationRequested)
                with { Abort = readAbort };
        }

        if (drain.Drift is { } drift)
        {
            // Pre-schedule definition-drift halt — nothing scheduled, no sweep.
            return BuildReport(plan, settled, cancelled: false) with { DefinitionDrift = drift };
        }

        if (drain.Faulted)
        {
            RunReport aborted = BuildReport(plan, settled, cancelled: cancellationToken.IsCancellationRequested)
                with { Abort = BuildAbort(_fault!) };
            if (!cancellationToken.IsCancellationRequested)
            {
                EndOfRunSweep(directoryOwner, settled, integ);
            }

            return aborted;
        }

        RunReport report = BuildReport(plan, settled, cancelled: cancellationToken.IsCancellationRequested)
            with { Decision = drain.Decision };

        // Legacy terminal whole-repo integration gate (§3.3/§4a) — FLAT plans only, and only when the plan
        // declares no <plan>/guardrails/ folder (the CLI PlanGuardrailPhase supersedes it). A WAVED plan's
        // terminal soundness boundary is its LAST wave's exit gate (§14.3), so this never runs there.
        if (report.AllSucceeded && _reVerifier != null && integ != null && plan.PlanGuardrails.Count == 0)
        {
            IReadOnlyList<GuardrailDefinition> integrationSet = UnionIntegrationSet(plan);

            if (integrationSet.Count > 0)
            {
                ReVerifyResult gate = await _reVerifier
                    .ReVerifyAsync(integ.IntegrationWorktreePath, integrationSet, cancellationToken)
                    .ConfigureAwait(false);

                if (!gate.Passed)
                {
                    report = WithTerminalGateFailure(plan, report, gate);
                }
            }
        }

        return Finalize(plan, report, integ, directoryOwner, settled, cancellationToken);
    }

    /// <summary>
    /// A WAVED plan (SSOT §14.4): run each wave in strict order behind a HARD BARRIER — wave entry
    /// preflight, then drain the wave's DAG on the CONTINUOUS plan branch, then (full drain) the wave exit
    /// gate, then the <c>Guardrails-Wave:</c> marker commit + journal-complete. A completed wave is skipped
    /// on resume (with a wave-drift check, §14.6); an unauthored next wave honest-halts for JIT breakdown
    /// (§14.4); any needs-human/blocked/failed inside a wave, or a failed gate, HALTS the whole run — later
    /// waves never start.
    /// </summary>
    private async Task<RunReport> RunWavedAsync(
        PlanDefinition plan, IntegrationHandle? integ,
        Dictionary<string, TaskResult> settled, Dictionary<string, string> directoryOwner,
        IReadOnlyDictionary<string, PlanBranchTaskRecord> planBranchRecords, bool trailerTracking,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<WaveNode> waves = plan.Waves; // strict total order (loader sorts by numeric prefix)

        // Durable wave-completion anchors from the plan branch (Guardrails-Wave: markers) — the backstop
        // when run.json is lost, and the source of predecessor-wave rewind targets (SSOT §14.5).
        IReadOnlyDictionary<string, PlanBranchWaveRecord> waveMarkers =
            _worktreeProvider is { } wpm && integ is { } integM
                ? wpm.ReconcileWavesFromPlanBranch(integM)
                : new Dictionary<string, PlanBranchWaveRecord>(StringComparer.Ordinal);

        DecisionEntry? lastDecision = null;

        for (int i = 0; i < waves.Count; i++)
        {
            WaveNode wave = waves[i];

            // 1. Completion + wave-drift (SSOT §14.5/§14.6).
            (bool complete, string? recordedHash) = EvaluateWaveCompletion(wave, planBranchRecords, waveMarkers, trailerTracking);
            if (complete)
            {
                string currentHash = Journal.WaveDefinitionHash.Compute(wave);
                if (recordedHash is { } rh && !string.Equals(rh, currentHash, StringComparison.Ordinal))
                {
                    // WAVE DRIFT: a COMPLETED wave's definition changed. auto (or a prompt the CLI already
                    // confirmed) rewinds + re-runs; halt / unconfirmed-prompt HALTS (SSOT §14.6).
                    bool authorized = _plan.Config.AutonomyPolicy == AutonomyPolicy.Auto
                        || (_plan.Config.AutonomyPolicy == AutonomyPolicy.Prompt && _waveDriftAuthorized.Contains(wave.Dir));
                    if (!authorized)
                    {
                        return BuildReport(plan, settled, cancelled: false)
                            with { WaveHalt = BuildWaveDriftHalt(waves, i, wave, rh, currentHash, unsafeRefusal: null) };
                    }

                    // The rewind is validated by the marker-aware SafeSuffixEvaluator + a tip CAS (BLOCKER /
                    // WEAK-4): a human hand-fix (trailer-less NON-marker commit) in the range, or a concurrent
                    // same-plan session that moved the tip, REFUSES the rewind → HALT rather than discard it.
                    WaveRewindResult resolved = ResolveWaveDrift(plan, waves, i, wave, integ, rh, currentHash,
                        ref planBranchRecords, ref waveMarkers);
                    if (resolved.Decision is null)
                    {
                        return BuildReport(plan, settled, cancelled: false)
                            with { WaveHalt = BuildWaveDriftHalt(waves, i, wave, rh, currentHash, unsafeRefusal: resolved.Refusal) };
                    }

                    lastDecision = resolved.Decision;
                    _journal.RecordDecision(lastDecision);
                    _observer.DecisionRecorded(lastDecision);
                    // fall through — this wave is no longer complete; run it.
                }
                else
                {
                    foreach (TaskNode t in wave.Tasks)
                    {
                        var s = new TaskResult
                        {
                            TaskId = t.Id,
                            Outcome = TaskOutcome.Skipped,
                            Summary = "already succeeded (resumed) — skipped"
                        };
                        settled[t.Id] = s;
                        _observer.TaskFinished(s);
                    }

                    _observer.WaveFinished(wave, Journal.WaveStatus.Completed, skipped: true);
                    continue;
                }
            }

            // 2. Between-wave JIT checkpoint (SSOT §14.4/§14.10): an unauthored/empty wave. #360 Phase 0
            //    detected an OPTIONAL human-authored brief.md and named it in the halt. #360 Phase 1 (doc 11
            //    §9) plugs the between-wave breakdown ACTOR in HERE: when a brief.md is present AND the policy
            //    authorizes it (auto, or a prompt approval the CLI captured), the harness INVOKES plan-breakdown
            //    against the materialized upstream, runs `guardrails validate` as the deterministic gate, and
            //    halts BreakdownComplete (for review) / BreakdownFailed (quarantining the partial). Otherwise
            //    (brief absent, halt policy, non-interactive prompt, or no invoker) it honest-halts exactly as
            //    before. Either way it records a boundary:"wave" decisions[] entry.
            //    #402/SSOT §14.11: the checkpoint ALSO fires for a wave that already has tasks but carries an
            //    UNSATISFIED breakdown-intent manifest — a valid PREFIX from a cut-off session. That manifest
            //    is the only durable signal separating "11 of 14, resume me" from "authored, run me"; without
            //    consulting it here, preserving a prefix would simply move the "a truncated wave reads as
            //    finished" hazard one run boundary later, which is worse than today's loud quarantine.
            if (wave.Tasks.Count == 0 || HasUnsatisfiedBreakdownIntent(wave))
            {
                JitCheckpointOutcome jit = await RunJitCheckpointAsync(
                    plan, wave, i + 1, waves.Count, integ, settled, cancellationToken)
                    .ConfigureAwait(false);
                if (jit.Halt is { } haltReport)
                {
                    return haltReport;
                }

                // Review-gate Option P (doc 12 §5.2, issue #361 Phase 4): the freshly-authored wave RUNS
                // UNREVIEWED — its indelible proceeded-unreviewed decision is already recorded. Splice the
                // authored wave into the in-memory plan (the run LOADED the empty JIT stub) so this drain AND
                // the end-of-run report reflect its real tasks, then fall through to the normal
                // entry/drain/exit path for this same wave.
                plan = SpliceAuthoredWave(plan, jit.ProceedWithWave!);
                waves = plan.Waves;
                wave = waves[i];
            }

            _observer.WaveStarting(wave, i + 1, waves.Count);

            // 3. Wave ENTRY preflight (skip-once-per-hash; SSOT §14.3/§14.6).
            GateOutcome entry = await RunWaveEntryGateAsync(plan, wave, integ, cancellationToken).ConfigureAwait(false);
            if (!entry.Passed)
            {
                _journal.RecordWaveStatus(wave.Dir, Journal.WaveStatus.NeedsHuman);
                BlockLaterWaves(waves, i, wave, settled);
                _observer.WaveFinished(wave, Journal.WaveStatus.NeedsHuman, skipped: false);
                WaveHalt gateHalt = BuildGateHalt(wave, WaveHaltKind.EntryGateFailed, entry.Failed);
                RecordGateHalt(Journal.RunHaltKind.WaveEntryGateFailed, wave.Dir, gateHalt, entry);
                RunReport entryHalt = BuildReport(plan, settled, cancelled: cancellationToken.IsCancellationRequested)
                    with { WaveHalt = gateHalt };
                if (!cancellationToken.IsCancellationRequested) EndOfRunSweep(directoryOwner, settled, integ);
                return entryHalt;
            }

            // 4. Drain the wave's DAG on the CONTINUOUS plan branch (shared integ / journal / settled).
            var waveGraph = new DependencyGraph(wave.Tasks);
            DrainOutcome drain = await DrainAsync(
                plan, wave.Tasks, waveGraph, integ, settled, directoryOwner, planBranchRecords, trailerTracking, cancellationToken)
                .ConfigureAwait(false);

            if (drain.ReadAbort is { } readAbort)
            {
                return BuildReport(plan, settled, cancelled: cancellationToken.IsCancellationRequested) with { Abort = readAbort };
            }

            if (drain.Drift is { } taskDrift)
            {
                return BuildReport(plan, settled, cancelled: false) with { DefinitionDrift = taskDrift };
            }

            if (drain.Faulted)
            {
                RunReport aborted = BuildReport(plan, settled, cancelled: cancellationToken.IsCancellationRequested)
                    with { Abort = BuildAbort(_fault!) };
                if (!cancellationToken.IsCancellationRequested) EndOfRunSweep(directoryOwner, settled, integ);
                return aborted;
            }

            if (drain.Decision is not null)
            {
                lastDecision = drain.Decision;
            }

            // 5. HARD BARRIER (SSOT §14.4): the wave must fully drain green. Any needs-human/blocked/failed
            // HALTS the whole run here — later waves never start.
            if (!drain.AllGreen)
            {
                _journal.RecordWaveStatus(wave.Dir, Journal.WaveStatus.NeedsHuman);
                BlockLaterWaves(waves, i, wave, settled);
                _observer.WaveFinished(wave, Journal.WaveStatus.NeedsHuman, skipped: false);
                RunReport barrierHalt = BuildReport(plan, settled, cancelled: cancellationToken.IsCancellationRequested)
                    with { Decision = lastDecision };
                if (!cancellationToken.IsCancellationRequested) EndOfRunSweep(directoryOwner, settled, integ);
                return barrierHalt;
            }

            // 6. Wave EXIT / terminal gate (SSOT §14.3): on the merged HEAD-so-far.
            GateOutcome exit = await RunWaveExitGateAsync(plan, wave, integ, cancellationToken).ConfigureAwait(false);
            if (!exit.Passed)
            {
                _journal.RecordWaveStatus(wave.Dir, Journal.WaveStatus.NeedsHuman);
                BlockLaterWaves(waves, i, wave, settled);
                _observer.WaveFinished(wave, Journal.WaveStatus.NeedsHuman, skipped: false);
                WaveHalt gateHalt = BuildGateHalt(wave, WaveHaltKind.ExitGateFailed, exit.Failed);
                RecordGateHalt(Journal.RunHaltKind.WaveExitGateFailed, wave.Dir, gateHalt, exit);
                RunReport exitHalt = BuildReport(plan, settled, cancelled: cancellationToken.IsCancellationRequested)
                    with { WaveHalt = gateHalt };
                if (!cancellationToken.IsCancellationRequested) EndOfRunSweep(directoryOwner, settled, integ);
                return exitHalt;
            }

            // 7. Wave-completion marker commit (decision E) + journal the wave complete (SSOT §14.5).
            string waveHash = Journal.WaveDefinitionHash.Compute(wave);
            string? markerSha = _worktreeProvider is { } wpc && integ is { } integC
                ? wpc.CommitWaveMarker(integC, wave.Dir, waveHash, cancellationToken)
                : null;
            _journal.RecordWaveCompleted(wave.Dir, waveHash, markerSha);
            if (markerSha is { Length: > 0 })
            {
                waveMarkers = WithWaveMarker(waveMarkers, wave.Dir, new PlanBranchWaveRecord(markerSha, waveHash));
            }

            _observer.WaveFinished(wave, Journal.WaveStatus.Completed, skipped: false);
        }

        // Every wave complete → deliver + sweep. No legacy terminal integ gate: the LAST wave's exit gate
        // is the whole-plan terminal soundness boundary (§14.3); a plan-root <plan>/guardrails/ is
        // optional-additive and run by the CLI PlanGuardrailPhase after this returns.
        RunReport report = BuildReport(plan, settled, cancelled: cancellationToken.IsCancellationRequested)
            with { Decision = lastDecision };
        return Finalize(plan, report, integ, directoryOwner, settled, cancellationToken);
    }

    /// <summary>
    /// Drain ONE set of tasks (a whole flat plan, or one wave's DAG) against the shared integration
    /// handle + journal + <paramref name="settled"/>/<paramref name="directoryOwner"/> accumulators: the
    /// resume pre-pass + task-level definition-drift check (§7.2) for this subset, then the Channel
    /// scheduler's worker loop (workers, maxParallelism, retry, needs-human/blocked, B1 settle — all
    /// unchanged). Appends every result to <paramref name="settled"/>. Returns a <see cref="DrainOutcome"/>
    /// so the caller decides: a drift/abort halt, an infra fault, or whether the subset fully drained green.
    /// </summary>
    private async Task<DrainOutcome> DrainAsync(
        PlanDefinition plan, IReadOnlyList<TaskNode> tasksToRun, DependencyGraph graph, IntegrationHandle? integ,
        Dictionary<string, TaskResult> settled, Dictionary<string, string> directoryOwner,
        IReadOnlyDictionary<string, PlanBranchTaskRecord> planBranchRecords, bool trailerTracking,
        CancellationToken cancellationToken)
    {
        var byId = tasksToRun.ToDictionary(t => t.Id, StringComparer.Ordinal);
        var pendingDeps = new Dictionary<string, int>(StringComparer.Ordinal);
        var channel = Channel.CreateUnbounded<TaskEnvelope>();

        HashSet<string> preSettledGreen;
        List<DefinitionDriftReporter.DriftInput> drifted;
        DecisionEntry? driftDecision = null;
        try
        {
            (preSettledGreen, drifted) = DetectDefinitionDrift(tasksToRun, planBranchRecords, trailerTracking);

            if (drifted.Count > 0)
            {
                DriftGateResult gate = TryResolveDrift(plan, graph, drifted, integ);
                if (gate.Decision is null)
                {
                    return DrainOutcome.DriftHalt(
                        DefinitionDriftReporter.Build(plan, graph, drifted, _worktreeProvider)
                            with { SafeToAutoResolve = gate.SafeToAutoResolve, RewindRefusal = gate.Refusal, RewindBlockingTask = gate.BlockingTask });
                }

                driftDecision = gate.Decision;
                _journal.RecordDecision(driftDecision);
                _observer.DecisionRecorded(driftDecision);

                // Harness writer 5 of 5 (plan 31 §5.3): a TryResolveDrift that RESOLVED. Its destructive
                // section is a `git reset --hard`, and this is NOT pre-DAG on a waved plan — DrainAsync is
                // called once per wave, so it fires mid-run. Whatever it moved on disk is the harness's own
                // work, not an operator edit.
                RebaselinePlanEdits();

                IReadOnlyDictionary<string, PlanBranchTaskRecord> refreshed = planBranchRecords;
                if (_worktreeProvider is { } wpAfter && integ is { } integAfter)
                {
                    refreshed = wpAfter.ReconcileFromPlanBranch(integAfter);
                }

                (preSettledGreen, drifted) = DetectDefinitionDrift(tasksToRun, refreshed, trailerTracking);
                if (drifted.Count > 0)
                {
                    return DrainOutcome.DriftHalt(DefinitionDriftReporter.Build(plan, graph, drifted, _worktreeProvider));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DrainOutcome.Abort(BuildDefinitionReadAbort(ex));
        }
        catch (InvalidOperationException ex)
        {
            return DrainOutcome.Abort(BuildAbort(ex));
        }

        // Emit resume skips for the pre-settled-green candidates (in subset order).
        foreach (TaskNode task in tasksToRun)
        {
            if (!preSettledGreen.Contains(task.Id))
            {
                continue;
            }

            var skipped = new TaskResult
            {
                TaskId = task.Id,
                Outcome = TaskOutcome.Skipped,
                Summary = "already succeeded (resumed) — skipped"
            };
            settled[task.Id] = skipped;
            _observer.TaskFinished(skipped);
        }

        int remaining = 0;
        foreach (TaskNode task in tasksToRun)
        {
            if (preSettledGreen.Contains(task.Id))
            {
                continue;
            }

            remaining++;
            pendingDeps[task.Id] = task.DependsOn.Count(d => !preSettledGreen.Contains(d));
        }

        if (remaining == 0)
        {
            return new DrainOutcome { AllGreen = AllGreenFor(tasksToRun, settled), Decision = driftDecision };
        }

        var handles = new Dictionary<string, WorktreeHandle>(StringComparer.Ordinal);
        foreach (TaskNode task in tasksToRun)
        {
            if (!preSettledGreen.Contains(task.Id) && pendingDeps[task.Id] == 0)
            {
                WorktreeHandle handle = _worktreeProvider != null && integ != null
                    ? _worktreeProvider.CreateSegment(task.Id, attempt: 1, integ, cancellationToken)
                    : new WorktreeHandle();
                handles[task.Id] = handle;
                if (!string.IsNullOrEmpty(handle.WorktreePath))
                {
                    directoryOwner[handle.WorktreePath] = task.Id;
                }
            }
        }

        foreach (TaskNode task in tasksToRun)
        {
            if (!preSettledGreen.Contains(task.Id) && pendingDeps[task.Id] == 0)
            {
                channel.Writer.TryWrite(new TaskEnvelope(task, handles[task.Id]));
            }
        }

        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var context = new RunContext(graph, byId, settled, pendingDeps, channel, remaining, handles, directoryOwner, integ);
        int workerCount = Math.Min(_maxParallelism, remaining);
        Task[] workers = Enumerable.Range(0, workerCount)
            .Select(_ => Task.Run(() => WorkerLoopAsync(context, runCts), CancellationToken.None))
            .ToArray();

        await Task.WhenAll(workers).ConfigureAwait(false);

        if (_fault is not null)
        {
            return DrainOutcome.Fault();
        }

        return new DrainOutcome { AllGreen = AllGreenFor(tasksToRun, settled), Decision = driftDecision };
    }

    /// <summary>Deliver (mergeOnSuccess) + end-of-run cleanup sweep — shared by the flat and waved paths.</summary>
    private RunReport Finalize(
        PlanDefinition plan, RunReport report, IntegrationHandle? integ,
        Dictionary<string, string> directoryOwner, IReadOnlyDictionary<string, TaskResult> settled,
        CancellationToken cancellationToken)
    {
        // #361 Phase 4 / doc 12 §1 hard rule (#340): a run whose result was SHAPED BY A MACHINE DECISION
        // (a proceeded-best-guess or proceeded-unreviewed recorded in decisions[]) DEFAULTS delivery OFF —
        // the verified work stays on the plan branch, never auto-delivered — UNLESS the operator EXPLICITLY
        // forced delivery on (guardrails.json "mergeOnSuccess": true, i.e. MergeOnSuccessExplicit == true;
        // the CLI --merge-on-success/--no-merge-on-success already resolved into plan.Config.MergeOnSuccess
        // by RunCommand, and an explicit-ON manifest key is the override signal that reaches the Scheduler).
        // SuppressesDelivery is the PURE RunOutcomePolicy call (task 03) over the run's recorded decisions[];
        // the decisions come from the real RunJournal — a unit-test fake journal records none, so nothing is
        // suppressed there.
        IReadOnlyList<DecisionEntry> decisions =
            (_journal as Journal.RunJournal)?.Document.Decisions ?? [];
        bool operatorForcedDelivery = plan.Config.MergeOnSuccessExplicit == true;
        bool deliverySuppressedByDecision =
            RunOutcomePolicy.SuppressesDelivery(decisions) && !operatorForcedDelivery;

        // The effective delivery gate: mergeOnSuccess enabled AND not suppressed by a machine decision.
        bool deliver = plan.Config.MergeOnSuccess && !deliverySuppressedByDecision;

        // Deliver the completed plan branch to the user's branch when every task succeeded and delivery
        // resolved on. AI-merge is withheld: a conflict halts with the plan branch intact.
        //
        // #457 ORDERING: `report.AllSucceeded` is TASKS ONLY. It already folds in the LEGACY in-Scheduler
        // terminal gate (RunFlatAsync rewrites the gate task to NeedsHuman on failure) and a waved run's
        // exit gates (a failed gate returns a WaveHalt, which AllSucceeded excludes) — so for a plan with
        // no <plan>/guardrails/ folder every terminal check has already run and delivering here is
        // correctly ordered. It does NOT cover the FOUR-FOLDER terminal gate: <plan>/guardrails/ is
        // evaluated by the CLI's PlanGuardrailPhase AFTER RunAsync returns, and that phase must stay
        // there — it writes plain console heartbeat lines that are only #145-safe OUTSIDE the Spectre
        // live region (which is disposed only after this method's caller returns), and it owns the
        // journal/halt/artifact writing that belongs behind the CLI seam. So DELIVERY moves instead of
        // the gate: hold it back, flag it on the report, and let the CLI complete it via
        // CompleteDeferredDelivery once — and only once — the gate has PASSED.
        bool terminalGateVerdictPending = plan.PlanGuardrails.Count > 0;
        bool deliverable = report.AllSucceeded && deliver && _worktreeProvider != null && integ != null;

        MergeOnSuccessResult? mergeOutcome = null;
        string? mergeDetail = null;
        bool deliveryPendingTerminalGate = false;

        if (deliverable && terminalGateVerdictPending)
        {
            // Hold the delivery. The integration handle is stashed for CompleteDeferredDelivery; nothing
            // touches the user's branch until the terminal gate certifies the merged HEAD.
            _pendingDeliveryIntegration = integ;
            deliveryPendingTerminalGate = true;
        }
        else if (deliverable)
        {
            (mergeOutcome, mergeDetail) = DeliverToUserBranch(integ!, cancellationToken);
        }

        // Issue #340: a wholly-green run whose delivery did NOT happen because delivery resolved OFF — the
        // verified work is sitting undelivered on the plan branch guardrails/<plan-name>. HONEST: only a run
        // with a real, SEPARATE plan branch has anything undelivered. A serial run has no provider/integ (the
        // work is already in the shared workspace) — the `integ != null` guard suppresses the warning there.
        // This is the symmetric complement of the delivery guard above (same worktree preconditions, delivery
        // off): keyed on the SAME effective `deliver` gate, so it now ALSO fires when delivery was defaulted
        // OFF by a machine decision (#361 Phase 4), not only when mergeOnSuccess itself is off. The CLI turns
        // it into a loud end-of-run warning once the terminal gate also passes.
        //
        // #345 review (finding 1c): the warning is NOT suppressed for runOnCurrentBranch. runOnCurrentBranch
        // is currently an UNWIRED STUB (read only by PlanLoader/RunConfig + this warning path; NOT wired into
        // GitWorktreeProvider), so a worktree-mode run still creates a SEPARATE guardrails/<plan> branch — an
        // opt-out run therefore genuinely STRANDS verified work on that branch with nothing on the user's
        // checkout, the exact #340 incident. Warning on it is correct. #340 follow-up: when runOnCurrentBranch
        // is actually wired to deliver onto the current branch, re-add a guard keyed on delivery-target ==
        // current-branch (nothing undelivered because it IS the current branch), NOT on the stub flag.
        bool whollyGreenButUndelivered =
            report.AllSucceeded
            && !deliver
            && _worktreeProvider != null
            && integ != null;

        if (!cancellationToken.IsCancellationRequested)
        {
            EndOfRunSweep(directoryOwner, settled, integ);
        }

        // #340: NAME the branch a successful delivery landed on (purely descriptive — no gate/exit change),
        // so the CLI's one-time "delivered by default" notice can name it. Non-null only when delivery
        // actually ran green (FF or clean merge); null for a halted delivery, a no-delivery run, or serial.
        string? deliveredToBranch =
            mergeOutcome is MergeOnSuccessResult.FastForwarded or MergeOnSuccessResult.Merged
                ? integ?.OriginalBranch
                : null;

        return report with
        {
            MergeOnSuccessOutcome = mergeOutcome,
            MergeOnSuccessDetail = mergeDetail,
            DeliveredToBranch = deliveredToBranch,
            WhollyGreenButUndelivered = whollyGreenButUndelivered,
            DeliveryPendingTerminalGate = deliveryPendingTerminalGate,
            UnreviewedWaveCount = RunOutcomePolicy.ProceededUnreviewedWaveCount(decisions)
        };
    }

    /// <summary>
    /// Perform a delivery that <see cref="Finalize"/> HELD BACK pending the terminal gate (issue #457),
    /// and return <paramref name="report"/> stamped with its outcome exactly as
    /// <see cref="Finalize"/> would have.
    /// <para>
    /// <b>The caller's obligation.</b> Call this ONLY when
    /// <see cref="RunReport.DeliveryPendingTerminalGate"/> is true AND the terminal plan-guardrail phase
    /// PASSED on the merged HEAD. It is a no-op (returns the report unchanged) when nothing was
    /// deferred, so it is safe on every path; a caller that never invokes it simply leaves the verified
    /// work on the plan branch, which is the SAFE failure direction and exactly what a FAILED gate must
    /// produce.
    /// </para>
    /// </summary>
    public RunReport CompleteDeferredDelivery(RunReport report, CancellationToken cancellationToken)
    {
        if (!report.DeliveryPendingTerminalGate || _pendingDeliveryIntegration is not { } integ)
        {
            return report;
        }

        _pendingDeliveryIntegration = null; // one delivery per run — never re-entrant

        (MergeOnSuccessResult outcome, string? detail) = DeliverToUserBranch(integ, cancellationToken);

        return report with
        {
            MergeOnSuccessOutcome = outcome,
            MergeOnSuccessDetail = detail,
            DeliveredToBranch =
                outcome is MergeOnSuccessResult.FastForwarded or MergeOnSuccessResult.Merged
                    ? integ.OriginalBranch
                    : null,
            DeliveryPendingTerminalGate = false
        };
    }

    /// <summary>
    /// The end-of-run merge-back itself (SSOT §5.3) — shared by the immediate path in
    /// <see cref="Finalize"/> and the terminal-gate-deferred path in
    /// <see cref="CompleteDeferredDelivery"/>, so both stamp identical outcomes. Threads the provider's
    /// detail out for the two halts that carry one: the git hook's stderr (HookRejected, #149/#150) and
    /// the blocking dirty paths (DirtyWorkingTree, #448), so the CLI can NAME what refused a green run's
    /// delivery instead of sending the user to <c>git status</c>.
    /// </summary>
    private (MergeOnSuccessResult Outcome, string? Detail) DeliverToUserBranch(
        IntegrationHandle integ, CancellationToken cancellationToken)
    {
        MergeOnSuccessResult outcome =
            _worktreeProvider!.MergePlanBranchIntoUserBranch(integ, cancellationToken);

        string? detail = outcome is MergeOnSuccessResult.HookRejected or MergeOnSuccessResult.DirtyWorkingTree
            ? _worktreeProvider.LastMergeOnSuccessDetail
            : null;

        return (outcome, detail);
    }

    private static bool AllGreenFor(IReadOnlyList<TaskNode> tasks, IReadOnlyDictionary<string, TaskResult> settled) =>
        tasks.All(t => settled.TryGetValue(t.Id, out TaskResult? r) && r.IsGreen);

    /// <summary>The outcome of one <see cref="DrainAsync"/>: a halt (drift/abort), an infra fault, or a completed drain.</summary>
    private sealed record DrainOutcome
    {
        /// <summary>True when every task in the drained subset is green (succeeded this run or skipped).</summary>
        public bool AllGreen { get; init; }

        /// <summary>Non-null on a pre-schedule task-level definition-drift halt (§7.2) — nothing scheduled.</summary>
        public DefinitionDriftReport? Drift { get; init; }

        /// <summary>Non-null on a pre-schedule read/git abort during the drift check — nothing scheduled (no sweep).</summary>
        public RunAbort? ReadAbort { get; init; }

        /// <summary>True when a worker loop hit an infra fault (<see cref="_fault"/> is set) — the caller sweeps.</summary>
        public bool Faulted { get; init; }

        /// <summary>A task-level drift auto-resolution decision recorded this drain (for the summary), or null.</summary>
        public DecisionEntry? Decision { get; init; }

        public static DrainOutcome DriftHalt(DefinitionDriftReport drift) => new() { Drift = drift };
        public static DrainOutcome Abort(RunAbort abort) => new() { ReadAbort = abort };
        public static DrainOutcome Fault() => new() { Faulted = true };
    }

    // --- wave loop helpers (SSOT §14, #254 M2b) -------------------------------------------

    /// <summary>
    /// Whether a wave is COMPLETE (SSOT §14.5): every task DURABLY green AND its completion is recorded
    /// (journal <c>completed</c> OR a <c>Guardrails-Wave:</c> marker). Also returns the recorded
    /// <c>WaveDefinitionHash</c> for the drift check (null ⇒ "unknown — assume unchanged").
    /// <para>
    /// "Durably green" is trailer-authoritative in worktree mode (#311 NIT-2): when the plan branch is the
    /// durable integration record (<paramref name="trailerTracking"/>), a task counts green ONLY if its
    /// <c>Guardrails-Task:</c> trailer is on the branch — a journal-<c>succeeded</c>-but-trailer-ABSENT task
    /// (a kill / rewind that discarded the commit) forces the wave INCOMPLETE, mirroring the flat path's
    /// "trailer-absent ⇒ re-run" reconciliation (<see cref="DetectDefinitionDrift"/>) so a no-drift crash
    /// can't leave a wave falsely complete over a missing base. In serial / non-trailer mode there are no
    /// trailers, so the journal <c>succeeded</c> status is authoritative (the pre-#311 behaviour).
    /// </para>
    /// </summary>
    private (bool Complete, string? RecordedHash) EvaluateWaveCompletion(
        WaveNode wave,
        IReadOnlyDictionary<string, PlanBranchTaskRecord> planBranchRecords,
        IReadOnlyDictionary<string, PlanBranchWaveRecord> waveMarkers,
        bool trailerTracking)
    {
        Journal.WaveJournalEntry? je = _journal.WaveEntryOf(wave.Dir);
        waveMarkers.TryGetValue(wave.Dir, out PlanBranchWaveRecord? marker);

        bool allTasksGreen = wave.Tasks.Count > 0 && wave.Tasks.All(t => trailerTracking
            ? planBranchRecords.ContainsKey(t.Id)
            : _journal.StatusOf(t.Id) == JournalTaskStatus.Succeeded);

        bool completionRecorded = je?.Status == Journal.WaveStatus.Completed || marker is not null;

        bool complete = allTasksGreen && completionRecorded;
        string? recordedHash = je?.DefinitionHash ?? marker?.WaveDefinitionHash;
        return (complete, recordedHash);
    }

    /// <summary>
    /// Run a wave's ENTRY preflight gate (SSOT §14.3) against the plan-branch HEAD (= materialized prior
    /// wave), or the workspace in serial mode. Skip-once: a passed entry marker for this wave is not
    /// re-evaluated on resume (a negative-baseline entry check runs exactly once; the wave-drift/reset path
    /// clears the marker so a changed wave re-runs it). Self-records the entry marker — including, per
    /// issue #432, WHERE each check's captured stdout/stderr landed — + sets the wave <c>running</c>.
    /// </summary>
    private async Task<GateOutcome> RunWaveEntryGateAsync(
        PlanDefinition plan, WaveNode wave, IntegrationHandle? integ, CancellationToken ct)
    {
        if (wave.Preflights.Count == 0)
        {
            _journal.RecordWaveStatus(wave.Dir, Journal.WaveStatus.Running);
            return GateOutcome.Pass;
        }

        if (_journal.WaveEntryOf(wave.Dir)?.Entry is { Status: Journal.PlanPhaseStatus.Passed })
        {
            _journal.RecordWaveStatus(wave.Dir, Journal.WaveStatus.Running);
            return GateOutcome.Pass; // skip-once: already passed this run's journal.
        }

        string workspace = integ?.IntegrationWorktreePath ?? plan.Workspace;
        (string? artifactDir, string? relativeLogDir) = GateLogLocation(wave.Dir, GateArtifacts.PreflightsFolder);
        ReVerifyResult result = _reVerifier is not null
            ? await _reVerifier
                .ReVerifyAsync(workspace, wave.Preflights, new ReVerifyOptions { ArtifactDirectory = artifactDir }, ct)
                .ConfigureAwait(false)
            : new ReVerifyResult { Passed = true };

        var checks = wave.Preflights.Select(g =>
        {
            GuardrailResult? failure = result.FailedGuardrails
                .FirstOrDefault(f => string.Equals(f.Name, g.Name, StringComparison.Ordinal));
            return new Journal.PlanPreflightCheck { Name = g.Name, Passed = failure is null, Reason = failure?.Reason };
        }).ToList();

        _journal.RecordWaveEntry(wave.Dir, new Journal.PlanPreflightsSection
        {
            Status = result.Passed ? Journal.PlanPhaseStatus.Passed : Journal.PlanPhaseStatus.PlanPreflightFailed,
            PlanHash = Journal.PlanHash.Compute(plan),
            EvaluatedAt = DateTimeOffset.UtcNow,
            Checks = checks,
            LogDir = relativeLogDir
        });

        // #513: the journal has always recorded this; no observer ever heard it, so no surface could
        // render it. Raised AFTER the journal write so an observer can never see a result the record does
        // not already hold.
        _observer.WaveGateFinished(wave, isEntryGate: true, checks);

        return new GateOutcome(result.Passed, result.FailedGuardrails, relativeLogDir);
    }

    /// <summary>
    /// Run a wave's EXIT / terminal gate (SSOT §14.3) on the merged HEAD-so-far — the per-wave analogue of
    /// the plan-terminal <c>&lt;plan&gt;/guardrails/</c> phase. Always re-evaluated (never skipped). The LAST
    /// wave's exit gate is the whole-plan terminal soundness boundary. Self-records the exit marker —
    /// including, per issue #432, every check's result and WHERE its captured stdout/stderr landed.
    /// </summary>
    private async Task<GateOutcome> RunWaveExitGateAsync(
        PlanDefinition plan, WaveNode wave, IntegrationHandle? integ, CancellationToken ct)
    {
        if (wave.Guardrails.Count == 0)
        {
            return GateOutcome.Pass;
        }

        string workspace = integ?.IntegrationWorktreePath ?? plan.Workspace;
        (string? artifactDir, string? relativeLogDir) = GateLogLocation(wave.Dir, GateArtifacts.GuardrailsFolder);
        ReVerifyResult result = _reVerifier is not null
            ? await _reVerifier
                .ReVerifyAsync(workspace, wave.Guardrails, new ReVerifyOptions { ArtifactDirectory = artifactDir }, ct)
                .ConfigureAwait(false)
            : new ReVerifyResult { Passed = true };

        var failed = result.FailedGuardrails
            .Select(f => new Journal.FailedGuardrail { Name = f.Name, Reason = f.Reason ?? "failed" })
            .ToList();
        var checks = wave.Guardrails.Select(g =>
        {
            GuardrailResult? failure = result.FailedGuardrails
                .FirstOrDefault(f => string.Equals(f.Name, g.Name, StringComparison.Ordinal));
            return new Journal.PlanPreflightCheck { Name = g.Name, Passed = failure is null, Reason = failure?.Reason };
        }).ToList();

        _journal.RecordWaveExit(wave.Dir, new Journal.PlanGuardrailsSection
        {
            Status = result.Passed ? Journal.PlanPhaseStatus.Passed : Journal.PlanPhaseStatus.PlanGuardrailFailed,
            PlanHash = Journal.PlanHash.Compute(plan),
            FailedChecks = failed,
            EvaluatedAt = DateTimeOffset.UtcNow,
            Checks = checks,
            LogDir = relativeLogDir
        });

        // #513, the exit half. This is the widest-blast-radius check in the plan and was the one the
        // operator noticed missing from the diagram.
        _observer.WaveGateFinished(wave, isEntryGate: false, checks);

        return new GateOutcome(result.Passed, result.FailedGuardrails, relativeLogDir);
    }

    /// <summary>
    /// The verdict of a wave ENTRY/EXIT gate plus WHERE its captured per-check output landed (issue #432):
    /// the plan-relative <c>logs/&lt;runId&gt;/&lt;wave&gt;/&lt;preflights|guardrails&gt;</c> path the halt
    /// record points a post-mortem at. Null <see cref="RelativeLogDir"/> means nothing was captured (the
    /// gate declared no checks, was skipped, or no run id was available).
    /// </summary>
    private sealed record GateOutcome(bool Passed, IReadOnlyList<GuardrailResult> Failed, string? RelativeLogDir)
    {
        /// <summary>A gate that declared nothing to run (or was skipped on resume).</summary>
        public static readonly GateOutcome Pass = new(true, [], null);
    }

    /// <summary>
    /// Resolve a wave gate's capture directory (issue #432) as (absolute, plan-relative). Both are null
    /// when the journal exposes no run id — a fake in a unit test — so nothing is ever written to a
    /// mis-rooted path.
    /// </summary>
    private (string? Absolute, string? Relative) GateLogLocation(string waveDir, string gateFolder) =>
        (GateArtifacts.DirectoryFor(_plan.PlanDirectory, _journal.RunId, waveDir, gateFolder),
         GateArtifacts.RelativeDirectoryFor(_journal.RunId, waveDir, gateFolder));

    /// <summary>The outcome of a wave-drift rewind: a resolved <see cref="DecisionEntry"/>, or a REFUSE reason (halt).</summary>
    private sealed record WaveRewindResult(DecisionEntry? Decision, string? Refusal);

    /// <summary>
    /// Wave-level drift resolution (SSOT §14.6/§14.8): rewind the plan branch past this wave + all its
    /// downstream waves and journal-reset them, then refresh the reconciled maps. The rewind ROUTES THROUGH
    /// the marker-aware <see cref="SafeSuffixEvaluator"/> (via <see cref="IWorktreeProvider.EvaluateSafeSuffix"/>)
    /// exactly like the task-level Part C path (BLOCKER fix, #311): the evaluator DERIVES the reset target
    /// from the live first-parent history (always an ancestor of the tip — no dangling-sha sideways reset),
    /// EXEMPTS the harness's own <c>Guardrails-Wave:</c> markers, and REFUSES if a trailer-less NON-marker
    /// commit (a human #197 hand-fix) is in the removed range — so the §14.8 "always safe" property holds for
    /// pure-harness history but a rewind never silently eats a human's fix. A tip compare-and-swap (WEAK-4)
    /// guards a concurrent same-plan session. Crash-atomic via <see cref="State.RewindIntent"/> (now carrying
    /// the wave dirs too, BLOCKER-1b). Returns the <c>wave</c>-boundary decision, or a REFUSE reason to halt.
    /// </summary>
    private WaveRewindResult ResolveWaveDrift(
        PlanDefinition plan, IReadOnlyList<WaveNode> waves, int waveIndex, WaveNode wave, IntegrationHandle? integ,
        string oldHash, string newHash,
        ref IReadOnlyDictionary<string, PlanBranchTaskRecord> planBranchRecords,
        ref IReadOnlyDictionary<string, PlanBranchWaveRecord> waveMarkers)
    {
        var affectedWaves = new List<WaveNode>();
        for (int j = waveIndex; j < waves.Count; j++)
        {
            affectedWaves.Add(waves[j]);
        }

        List<string> affectedTaskIds = affectedWaves.SelectMany(w => w.Tasks.Select(t => t.Id)).ToList();
        List<string> affectedWaveDirs = affectedWaves.Select(w => w.Dir).ToList();
        var safeSet = new HashSet<string>(affectedTaskIds, StringComparer.Ordinal);

        // Safe-suffix check against the plan branch (marker-aware). Serial / no provider → NothingToRewind
        // (a journal-only reset is sound where there is no branch to carry a stale commit). The journal-
        // recorded settle hashes corroborate each removed commit's Guardrails-Task-Hash: trailer (issue
        // #322) — a copied-trailer #197 hand-fix in the range REFUSES, exactly like the task path.
        SafeSuffixDecision decision = _worktreeProvider is { } provider && integ is { } activeInteg
            ? provider.EvaluateSafeSuffix(activeInteg, safeSet, _journal.RecordedDefinitionHashes())
            : SafeSuffixDecision.Nothing();

        // Refuse floor (un-overridable, exactly like the task path): a human hand-fix in the range refuses.
        if (decision.Outcome == SafeSuffixOutcome.Refused)
        {
            return new WaveRewindResult(null, decision.Refusal);
        }

        string? resetTarget = decision.Outcome == SafeSuffixOutcome.Safe ? decision.ResetTarget : null;

        // Compare-and-swap (WEAK-4): for a real rewind, the tip must still be where the decision saw it, or
        // a concurrent same-plan session moved it — REFUSE rather than discard its work.
        if (decision.Outcome == SafeSuffixOutcome.Safe)
        {
            string currentTip = _worktreeProvider is { } tp && integ is { } ti ? tp.CurrentPlanBranchTip(ti) : "";
            if (!string.Equals(currentTip, decision.ExpectedTip ?? "", StringComparison.Ordinal))
            {
                return new WaveRewindResult(null,
                    "the plan branch changed while the wave-drift rewind was deciding (a concurrent same-plan run?) — refusing.");
            }
        }

        // Crash-atomic: record the intent (affected task ids AND wave dirs, BLOCKER-1b) BEFORE the
        // destructive rewind so a kill in between is idempotently replayed on resume; clear only AFTER both
        // effects persist. The wave dirs ensure the replay clears the wave entries too (no dangling MarkerSha).
        bool useMarker = decision.Outcome == SafeSuffixOutcome.Safe;
        if (useMarker)
        {
            State.RewindIntent.Write(_plan.PlanDirectory, new State.RewindIntent
            {
                SafeSet = affectedTaskIds.OrderBy(x => x, StringComparer.Ordinal).ToList(),
                Waves = affectedWaveDirs,
                PreRewindTip = decision.ExpectedTip,
                ResetTarget = resetTarget
            });
        }

        if (resetTarget is { Length: > 0 } && _worktreeProvider is { } wpr && integ is { } integR)
        {
            wpr.RewindPlanBranchTo(integR, resetTarget);
        }

        foreach (WaveNode w in affectedWaves)
        {
            _journal.ResetWaveToPending(w.Dir);
            foreach (TaskNode t in w.Tasks)
            {
                _journal.ResetTaskToPending(t.Id);
            }
        }

        if (useMarker)
        {
            State.RewindIntent.Clear(_plan.PlanDirectory);
        }

        // The drifted+downstream commits/markers are gone from the branch — refresh so a subsequent drain
        // does not treat them as pre-settled via a stale trailer.
        if (_worktreeProvider is { } wpf && integ is { } integF)
        {
            planBranchRecords = wpf.ReconcileFromPlanBranch(integF);
            waveMarkers = wpf.ReconcileWavesFromPlanBranch(integF);
        }

        return new WaveRewindResult(
            DriftDecisions.WaveDriftResolved(
                _plan.Config.AutonomyPolicy, wave.Dir, resetTarget, oldHash, newHash, affectedWaveDirs),
            null);
    }

    /// <summary>Represent every task in the waves AFTER a halted wave as <c>blocked</c> (SSOT §14.4: later waves never start).</summary>
    private static void BlockLaterWaves(
        IReadOnlyList<WaveNode> waves, int haltedIndex, WaveNode haltedWave, Dictionary<string, TaskResult> settled)
    {
        for (int j = haltedIndex + 1; j < waves.Count; j++)
        {
            foreach (TaskNode t in waves[j].Tasks)
            {
                if (settled.ContainsKey(t.Id))
                {
                    continue;
                }

                settled[t.Id] = new TaskResult
                {
                    TaskId = t.Id,
                    Outcome = TaskOutcome.Blocked,
                    Summary = $"not started — halted at wave '{haltedWave.Dir}' barrier (SSOT §14.4)"
                };
            }
        }
    }

    private WaveHalt BuildWaveDriftHalt(
        IReadOnlyList<WaveNode> waves, int waveIndex, WaveNode wave, string oldHash, string newHash,
        string? unsafeRefusal)
    {
        var affected = new List<string>();
        for (int j = waveIndex; j < waves.Count; j++)
        {
            affected.Add(waves[j].Dir);
        }

        string folder = Path.GetFileName(
            _plan.PlanDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        // When the rewind was REFUSED as unsound (a human hand-fix / unattributed commit in the range, or a
        // concurrent tip move — BLOCKER/WEAK-4), steer to the always-sound full rebuild and name WHY; an
        // auto-resolve flag would just re-refuse. Otherwise (a policy/consent halt) offer --autonomy auto.
        string detail = unsafeRefusal is { Length: > 0 } refusal
            ? $"WaveDefinitionHash {ShortHash(oldHash)} -> {ShortHash(newHash)}. Cannot safely rewind this wave: "
              + $"{refusal} Resolve the plan branch manually, or 'guardrails reset {folder} -y' for a full rebuild."
            : $"WaveDefinitionHash {ShortHash(oldHash)} -> {ShortHash(newHash)}. Resolving would rewind + "
              + $"re-run this wave + {affected.Count - 1} downstream wave(s). Re-run with '--autonomy auto' to "
              + $"rewind + re-run, or 'guardrails reset {folder} {wave.Dir}' to reset it explicitly.";

        return new WaveHalt
        {
            WaveDir = wave.Dir,
            Kind = WaveHaltKind.WaveDrift,
            Headline = $"Wave '{wave.Dir}' DRIFTED — its definition changed since it completed (SSOT §14.6).",
            Detail = detail,
            AffectedWaves = affected,
            OldHash = oldHash,
            NewHash = newHash
        };
    }

    private static WaveHalt BuildUnauthoredWaveHalt(WaveNode wave, IntegrationHandle? integ, bool briefPresent)
    {
        string? worktree = integ?.IntegrationWorktreePath;
        string at = worktree is not null ? $" at:\n  {worktree}" : "";
        string brief = $"{wave.Dir}/{WaveNode.BriefFileName}";

        // #360 §14.4/§14.10: a PRESENT brief.md is the opt-in signal for auto-breakdown, gated by
        // 'autoBreakdown' (default true, DECOUPLED from 'autonomyPolicy'); an ABSENT one names the convention.
        // With the default, this brief-present honest-halt is reached only when auto-breakdown CANNOT run — no
        // 'breakdown' prompt runner, serial mode (no integration worktree = no materialized upstream), or the
        // cost cap is hit — OR when 'autoBreakdown' is false and the 'autonomyPolicy' path did not authorize it.
        string detail = briefPresent
            ? $"A wave brief '{brief}' is present — auto-breakdown is on by default ('autoBreakdown'), but this "
              + "run honest-halts because auto-breakdown could not run (no 'breakdown' prompt runner, serial "
              + "mode, or the cost cap is hit), or 'autoBreakdown' is false (which gates invocation on "
              + $"'autonomyPolicy'). The prior wave(s) completed and are materialized on the plan branch{at}\n"
              + $"Break down + review '{wave.Dir}' against the materialized upstream artifacts, then re-run "
              + "'guardrails run' to continue."
            : "The prior wave(s) completed and are materialized on the plan branch. Break down + review "
              + $"'{wave.Dir}' against the materialized upstream artifacts{at}\nCreate '{brief}' to enable "
              + "auto-breakdown here (on by default, 'autoBreakdown'), or author the wave manually, then re-run "
              + "'guardrails run' to continue.";

        // SSOT §14.11: the checkpoint also re-fires on a wave that HAS tasks but carries an unsatisfied
        // breakdown-intent manifest. Saying "has no authored tasks" there would be false in the direction that
        // misleads (#471's lesson about halt text), so the headline states which of the two situations it is
        // and the detail names the remedy that actually applies to a stalled RESUME.
        IReadOnlyList<string> owed =
            BreakdownIntent.TryRead(wave.Directory)?.MissingFolders(wave.Directory) ?? [];
        bool resuming = wave.Tasks.Count > 0 && owed.Count > 0;
        if (resuming)
        {
            detail = $"Wave '{wave.Dir}' carries a valid PREFIX of {wave.Tasks.Count} task(s) from a cut-off "
                     + $"breakdown, and its 'state/{BreakdownIntent.FileName}' still owes {owed.Count}: "
                     + $"{string.Join(", ", owed)}.\n{detail}\n"
                     + $"To accept the wave as authored instead, delete "
                     + $"'{wave.Dir}/state/{BreakdownIntent.FileName}' (which also clears GR2063).";
        }

        return new WaveHalt
        {
            WaveDir = wave.Dir,
            Kind = WaveHaltKind.NextWaveUnauthored,
            Headline = resuming
                ? $"Wave '{wave.Dir}' breakdown is INCOMPLETE ({owed.Count} declared task(s) still owed) — "
                  + "halting for JIT breakdown (SSOT §14.11)."
                : $"Wave '{wave.Dir}' has no authored tasks — halting for JIT breakdown (SSOT §14.4).",
            Detail = detail,
            IntegrationWorktreePath = worktree,
            WaveDirectory = wave.Directory
        };
    }

    // --- #360 Phase 1: the between-wave breakdown actor at the JIT checkpoint (SSOT §14.4, doc 11 §9) -------

    /// <summary>
    /// Handle the JIT wave checkpoint for an unauthored (empty <c>tasks/</c>) wave (SSOT §14.4/§14.10, #360).
    /// When <see cref="RunConfig.AutoBreakdown"/> is <c>true</c> (the DEFAULT, decoupled from
    /// <see cref="RunConfig.AutonomyPolicy"/>): INVOKE breakdown whenever a <c>brief.md</c> is present AND the
    /// actor + integration worktree exist AND the cost cap is un-hit — with NO prompt, at ANY policy. When
    /// <c>AutoBreakdown</c> is <c>false</c>: fall back to the EXACT #368 <c>autonomyPolicy</c>-gated path
    /// (auto → invoke; prompt + a CLI-captured approval → invoke; else honest-halt). An absent <c>brief.md</c>
    /// (or no actor / serial mode / hit cost cap) always honest-halts. Records a <c>boundary:"wave"</c>
    /// <c>decisions[]</c> entry either way; the human review gate always halts (never auto-satisfied).
    /// </summary>
    private async Task<JitCheckpointOutcome> RunJitCheckpointAsync(
        PlanDefinition plan, WaveNode wave, int waveIndex, int waveTotal, IntegrationHandle? integ,
        Dictionary<string, TaskResult> settled, CancellationToken cancellationToken)
    {
        AutonomyPolicy policy = _plan.Config.AutonomyPolicy;
        bool autoBreakdown = _plan.Config.AutoBreakdown;
        bool briefPresent = File.Exists(Path.Combine(wave.Directory, WaveNode.BriefFileName));

        // Invocation requires: a brief (opt-in), a breakdown runner, and the integration worktree (materialized
        // upstream — absent in serial mode). Don't spend a full authoring session once maxCostUsd is reached.
        bool costCapHit = _plan.Config.MaxCostUsd is { } cap && _journal.CurrentCostUsd() >= cap;
        bool canInvoke = briefPresent && _breakdownInvoker is not null && integ is not null && !costCapHit;
        bool prompted = _breakdownConfirmations.TryGetValue(wave.Dir, out bool approved);

        string? invocationToken = null; // set only when we actually invoke
        if (canInvoke)
        {
            if (autoBreakdown)
            {
                // The DEFAULT (SSOT §14.4/§14.10, #360): a present brief.md AUTO-FIRES the breakdown with NO
                // prompt (even non-interactive), DECOUPLED from autonomyPolicy — this knob never reads or
                // modifies the policy, and the run-time judgment gates (needsHuman, drift §7.2, overwatcher
                // §9.2) keep their own policy behavior untouched. The human REVIEW gate still HALTS below at
                // every policy (BreakdownComplete → /guardrails-review); autoBreakdown governs INVOCATION only.
                invocationToken = "auto-applied";
            }
            else if (policy == AutonomyPolicy.Auto)
            {
                // autoBreakdown:false → the EXACT #368 autonomyPolicy-gated fallback (preserved verbatim).
                invocationToken = "auto-applied";
            }
            else if (policy == AutonomyPolicy.Prompt && prompted && approved)
            {
                invocationToken = "prompted-approved";
            }
        }

        if (invocationToken is not null)
        {
            return await RunBreakdownAsync(
                plan, wave, waveIndex, waveTotal, integ!, settled, policy, invocationToken, cancellationToken)
                .ConfigureAwait(false);
        }

        // #375 (wave-checkpoint answer channel now LIVE): on resume, BEFORE re-classifying/re-escalating, try
        // to consume a still-open wave-checkpoint answer for THIS wave. It needs the breakdown actor +
        // integration worktree, exactly as the auto-invoke above does. The clamp threads automatically
        // (§5.2/§7.3 Blocker 1): a high/critical-assessed wave-checkpoint under proceed-unreviewed stays
        // NON-answerable — Consume rejects it (⇒ None), so no answer ever runs or holds the wave.
        WaveProceedConsumeResult consumed = _breakdownInvoker is not null && integ is not null
            ? TryConsumeWaveProceed(wave)
            : WaveProceedConsumeResult.None;

        if (consumed == WaveProceedConsumeResult.Proceed)
        {
            // A valid `proceed` short-circuits to the SAME break-down-and-run path an authorized auto-breakdown
            // takes (integ is non-null here — TryConsumeWaveProceed only ran when it was).
            return await RunBreakdownAsync(
                plan, wave, waveIndex, waveTotal, integ!, settled, policy, "answer-proceed", cancellationToken)
                .ConfigureAwait(false);
        }

        // #361 Phase 3 (doc 12 §4): when the dial is wired, classify-then-act the JIT wave-checkpoint (a
        // class-(a) judgment call — assess criticality → escalate via the sink or RECORD a best-guess) BEFORE
        // the shipped honest-halt below. The shipped WaveCheckpointHalt decision + WaveHalt report are
        // preserved unchanged (an escalation records the open question; it does not itself author the wave).
        //
        // A valid `hold` (§7.4) is DEFINITIVE: the human said wait, so SKIP this re-assessment entirely — else
        // the judge could reassess below threshold and best-guess-and-proceed, overriding "wait" — and go
        // straight to the honest-halt below (no re-classify, no NEW escalation). Only a None (no answer /
        // rejected / clamped) re-poses the gate through classify-then-act.
        if (consumed == WaveProceedConsumeResult.None && _escalationSink is not null)
        {
            await ClassifyAndActAsync(
                GateSignal.WaveCheckpoint(WaveHaltKind.NextWaveUnauthored), gate: "wave-checkpoint",
                subject: wave.Dir, boundary: "wave",
                question: $"Wave '{wave.Dir}' is unauthored — the next-wave JIT breakdown checkpoint.",
                definitionHash: Journal.WaveDefinitionHash.Compute(wave),
                criticalityGate: CriticalityGate.WaveCheckpoint, cancellationToken).ConfigureAwait(false);
        }

        // Honest-halt. An interactive DECLINE reads as prompted-declined; everything else (halt policy, a
        // non-interactive prompt, an absent brief, no runner, or a hit cost cap) is a plain halted.
        string haltToken = policy == AutonomyPolicy.Prompt && prompted && !approved ? "prompted-declined" : "halted";
        DecisionEntry checkpoint = DriftDecisions.WaveCheckpointHalt(policy, wave.Dir, briefPresent, haltToken);
        _journal.RecordDecision(checkpoint);
        _observer.DecisionRecorded(checkpoint);
        return JitCheckpointOutcome.HaltWith(BuildReport(plan, settled, cancelled: false)
            with { WaveHalt = BuildUnauthoredWaveHalt(wave, integ, briefPresent) });
    }

    /// <summary>
    /// Invoke <c>plan-breakdown</c> for the wave, then gate its output on the DETERMINISTIC in-process
    /// <c>guardrails validate</c> (invariant 1): PASS → <see cref="WaveHaltKind.BreakdownComplete"/> (halt for
    /// the human review gate — never auto-satisfied, doc 11 §9.6); FAIL → quarantine the partial invalid
    /// <c>tasks/</c> (so the plan stays loadable + the checkpoint re-fires on resume) →
    /// <see cref="WaveHaltKind.BreakdownFailed"/> carrying the validate errors.
    /// </summary>
    private async Task<JitCheckpointOutcome> RunBreakdownAsync(
        PlanDefinition plan, WaveNode wave, int waveIndex, int waveTotal, IntegrationHandle integ,
        Dictionary<string, TaskResult> settled, AutonomyPolicy policy, string invocationToken,
        CancellationToken cancellationToken)
    {
        // Transcript location (SSOT §8): logs/<runId>/<wave-dir>/breakdown/ — NOT a per-task attempt dir.
        string breakdownLogDir = Path.Combine(plan.PlanDirectory, "logs", integ.RunId, wave.Dir, "breakdown");
        string rejectedRoot = Path.Combine(breakdownLogDir, "rejected");
        try { Directory.CreateDirectory(breakdownLogDir); } catch (IOException) { /* the invoker retries */ }

        // The pre-invocation inventory (SSOT §14.11, #471): the harness's OWN record of what pre-dated the
        // attempt, taken before a single file is written. It is what makes the revert exact instead of a
        // guess about provenance — and specifically what stops a blind revert from moving a human's
        // hand-authored wave gate into rejected/ while calling it a revert.
        BreakdownInventory? inventory = BreakdownInventory.Capture(wave.Directory, breakdownLogDir);

        bool waveSettled = false;
        try
        {
            JitCheckpointOutcome outcome = await RunBreakdownSegmentsAsync(
                plan, wave, waveIndex, waveTotal, integ, settled, policy, invocationToken, inventory,
                rejectedRoot, cancellationToken)
                .ConfigureAwait(false);
            waveSettled = true;
            return outcome;
        }
        finally
        {
            // #489 — Ctrl+C during a breakdown used to propagate PAST the quarantine entirely, leaving the
            // partially-authored wave on disk: the operator's own escape hatch manufacturing the #385
            // artifact, on tracked content, with no message. The guarantee wanted is a PROPERTY — "the plan
            // folder is never left in a state the loader rejects" — and enumerating the exception types that
            // could violate it is precisely how this one was missed, so this is structural: whatever leaves
            // this method other than a settled classification runs the same cleanup. It cannot itself be
            // token-bound (the token is already signalled by the time we get here), and it is pure
            // synchronous file IO, so it is not.
            if (!waveSettled)
            {
                LeaveWaveLoadable(plan, wave, inventory, rejectedRoot);
            }
        }
    }

    /// <summary>
    /// Drive up to <see cref="MaxBreakdownSegments"/> breakdown segments for one wave in one run, gating each
    /// on the deterministic in-process <c>guardrails validate</c> and classifying on DIAGNOSTIC CODES plus
    /// the runner's own termination classification — never on the breakdown's opinion of its own completeness
    /// (invariant 1, design 20 §C6).
    /// </summary>
    private async Task<JitCheckpointOutcome> RunBreakdownSegmentsAsync(
        PlanDefinition plan, WaveNode wave, int waveIndex, int waveTotal, IntegrationHandle integ,
        Dictionary<string, TaskResult> settled, AutonomyPolicy policy, string invocationToken,
        BreakdownInventory? inventory, string rejectedRoot, CancellationToken cancellationToken)
    {
        string breakdownLogDir = Path.GetDirectoryName(rejectedRoot)!;

        for (int segment = 1; segment <= MaxBreakdownSegments; segment++)
        {
            int completeBefore = CountSatisfiedDeclaredFolders(wave);
            BreakdownResumeContext? resume = segment == 1 ? null : BuildResumeContext(wave, segment);

            // Compose + tee the prompt FIRST so the phase can be announced with the real evidence paths
            // (issue #469): until this event existed, the 30-minute authoring session raised nothing at all
            // and the live table — which emits rows per wave.Tasks, and a JIT stub has none — rendered the
            // run as finished while it was mid-authoring.
            BreakdownInvocationPlan prepared = WaveBreakdownInvoker.PrepareInvocation(
                wave, plan, integ.IntegrationWorktreePath, breakdownLogDir, resume);
            WaveBreakdownContext phase = BuildBreakdownContext(wave, waveIndex, waveTotal, breakdownLogDir, prepared);
            var phaseClock = Stopwatch.StartNew();
            _observer.WaveBreakdownStarting(phase);

            WaveBreakdownOutcome outcome = await _breakdownInvoker!
                .InvokeAsync(wave, plan, integ.IntegrationWorktreePath, breakdownLogDir, _journal,
                    cancellationToken, resume, prepared)
                .ConfigureAwait(false);

            // Harness writer 1 of 5 (plan 31 §5.3): a JIT wave BREAKDOWN ATTEMPT. Plan-wide, because the
            // invoker's subprocess is rooted at the plan directory with no containment hook — it can rewrite
            // any other wave's tasks/, any task's guardrails/, or guardrails.json (#557).
            RebaselinePlanEdits();

            // Sweep the half-written TRAILING task folder(s) this attempt created before the gate runs, so an
            // "11 complete + 1 with a task.json and no action file" truncation is judged as the 11-task valid
            // prefix it is — rather than discarding 79% of the work because of one missing file (§4.3).
            inventory?.SweepIncompleteTrailingTaskFolders(rejectedRoot);

            // Harness writer 3 of 5, call site 1 of its TWO: the post-invoke sweep moved task folders into
            // rejected/tasks/. (Site 2 is the cancel/fault cleanup in LeaveWaveLoadable — re-baselining only
            // here would leave that path blind, reporting the harness's own deletions as operator edits.)
            RebaselinePlanEdits();

            // Read at FULL fidelity: the quarantine reason below has to say which of "no manifest" and "a
            // manifest that declares nothing" actually happened, and TryRead collapses them into one null.
            // Read BEFORE the gate (#501) — whether the wave is a knowingly-incomplete prefix decides which
            // errors the gate may fairly hold against it, so the gate cannot be the one to find out.
            BreakdownIntentRead intentRead = BreakdownIntent.Read(wave.Directory);
            BreakdownIntent? intent = intentRead.Usable;
            IReadOnlyList<string> missing = intent?.MissingFolders(wave.Directory) ?? [];
            int declared = intent?.DeclaredFolders().Count ?? 0;

            (bool valid, string report, int authoredTaskCount, WaveNode? authoredWave) =
                ValidatePlanAfterBreakdown(plan.PlanDirectory, wave.Dir, wavePrefixIsIncomplete: missing.Count > 0);

            // #501: tee the gate's reasoning beside the session's other artifacts, on EVERY path. The halt
            // detail carries `report` only when the gate REJECTS, so a successful salvage — the interesting
            // case, and the one that was silently not happening — previously left no record at all. This is
            // the post-mortem the missing unit test cannot provide: the bug was found by reading a live run,
            // so the live run has to be readable.
            TeeBreakdownGateDecision(breakdownLogDir, wave, report, intentRead, declared, missing, outcome);

            // The settlements, decided ONCE and then both reported and acted on — so the phase event and the
            // branch below can never disagree about what happened. `quarantined` is the second FAIL route:
            // a valid prefix with no usable manifest is reverted, because nothing durable would distinguish
            // it from a finished wave on the next run.
            bool gateRejected = !valid || authoredTaskCount == 0;
            bool completed = !gateRejected && outcome.TerminatedCleanly && missing.Count == 0;
            bool quarantined = !gateRejected && !completed && intent is null;
            bool failed = gateRejected || quarantined;
            bool proceeding = completed && authoredWave is not null
                                        && ResolveReviewGate() == ReviewGateDecision.ProceedUnreviewed;

            // Raised AFTER the deterministic validate gate so authoredTaskCount is the count the HARNESS
            // found on disk, never the session's own claim (invariant 1). The ~1s validate is invisible.
            // authoredWave is handed on only where the run will PROCEED with it — the #404 seam; every
            // halting path passes null, because the wave is not going to run.
            //
            // The reason token reports the SETTLEMENT first and the runner's stop cause second. A session
            // can end perfectly cleanly and still be rejected by the gate ("invalid") or fall short of its
            // own manifest ("incomplete"); reporting the runner's silence as success there would settle the
            // live phase row GREEN for a run that is about to halt.
            string? phaseFailure = completed
                ? null
                : failed
                    ? outcome.FailureKindToken ?? BreakdownFailureTokens.Invalid
                    : BreakdownFailureTokens.Incomplete;

            _observer.WaveBreakdownFinished(
                phase, phaseClock.Elapsed, authoredTaskCount, phaseFailure,
                proceeding ? authoredWave : null);

            if (gateRejected)
            {
                return FailBreakdown(plan, wave, settled, policy, invocationToken, inventory, rejectedRoot,
                    outcome, report, authoredTaskCount, reason: null);
            }

            if (completed)
            {
                RemoveIntentManifest(wave); // its lifetime is one breakdown attempt
                return CompleteBreakdown(plan, wave, settled, policy, invocationToken, authoredWave, authoredTaskCount);
            }

            // From here the session was CUT OFF, or the wave is short of its own declaration — either way it
            // can NEVER be reported BreakdownComplete (§4.2), whatever validate says.
            if (quarantined)
            {
                // No USABLE manifest, so nothing durable distinguishes this prefix from a finished wave on
                // the NEXT run — and a valid prefix that reads as finished is strictly worse than a loud
                // quarantine. Salvage is exactly what the manifest buys; without it we keep today's
                // behaviour. Which of the three no-manifest states holds is named, never generalised into
                // "carries no manifest" — that sentence is FALSE when the file is sitting right there, and
                // #471's lesson is that a halt saying a false thing costs more than a halt saying nothing.
                string manifestClause = intentRead.Presence switch
                {
                    BreakdownIntentPresence.Absent =>
                        $"the wave carries no 'state/{BreakdownIntent.FileName}' declaring what it intended "
                        + "to author",
                    BreakdownIntentPresence.Unreadable =>
                        $"the wave's 'state/{BreakdownIntent.FileName}' exists but could not be read or "
                        + "parsed, so it declares nothing",
                    _ => $"the wave's 'state/{BreakdownIntent.FileName}' exists but {intentRead.Explanation} "
                         + "(GR2064), so it declares nothing"
                };

                string rejected = intentRead.RejectedEntries.Count == 0
                    ? ""
                    : "\nRejected manifest entries: " + string.Join("; ", intentRead.RejectedEntries) + ".";

                return FailBreakdown(plan, wave, settled, policy, invocationToken, inventory, rejectedRoot,
                    outcome, report, authoredTaskCount,
                    reason: $"The session {outcome.CutOffCause}, and {manifestClause}, so "
                            + "the harness cannot tell a finished wave from a truncated prefix. The partial "
                            + "output is quarantined rather than silently read as complete." + rejected);
            }

            int completeAfter = declared - missing.Count;
            bool noProgress = completeAfter <= completeBefore;
            bool costCapHit = _plan.Config.MaxCostUsd is { } cap && _journal.CurrentCostUsd() >= cap;
            bool lastSegment = segment == MaxBreakdownSegments;

            if (missing.Count == 0 || noProgress || costCapHit || lastSegment)
            {
                return IncompleteBreakdown(plan, wave, settled, policy, invocationToken, outcome,
                    declared, completeAfter, missing, segment,
                    stopReason: StopReasonFor(missing.Count == 0, noProgress, costCapHit, lastSegment));
            }

            // Otherwise: resume. The next segment is told what is already on disk and what is still owed, so
            // the 232 KB brief is not re-paid for work already done (§4.5).
        }

        // Unreachable: the loop's last iteration always returns (lastSegment is true there).
        throw new InvalidOperationException("breakdown segment loop exited without settling the wave");
    }

    /// <summary>
    /// The configured review-gate FLOOR (doc 12 §5.2, issue #361 Phase 4), resolved in ONE place so the
    /// phase event's "will the run proceed with this wave?" answer and the branch that actually proceeds
    /// cannot drift apart. Absent config defaults to <see cref="ReviewGateDecision.Escalate"/> — the harness
    /// never self-attests a review at any dial setting.
    /// </summary>
    private ReviewGateDecision ResolveReviewGate() =>
        _plan.Config.Autonomy?.GateThresholds?.ReviewGate ?? ReviewGateDecision.Escalate;

    /// <summary>
    /// The rendering context for one breakdown segment (design 23 §10.1, issue #469): the wave's place in
    /// the plan plus the three probe targets a UI needs — the evidence directory, the teed stream (liveness),
    /// and the <c>tasks/</c> folder (forward progress). The intent manifest is named only when it is really
    /// on disk, because it is the ONLY honest denominator and a missing one must never be synthesised.
    /// </summary>
    private static WaveBreakdownContext BuildBreakdownContext(
        WaveNode wave, int waveIndex, int waveTotal, string breakdownLogDir, BreakdownInvocationPlan prepared)
    {
        string manifest = BreakdownIntent.PathFor(wave.Directory);
        bool manifestPresent;
        try { manifestPresent = File.Exists(manifest); } catch { manifestPresent = false; }

        return new WaveBreakdownContext
        {
            WaveDir = wave.Dir,
            Index = waveIndex,
            Total = waveTotal,
            BreakdownLogDir = breakdownLogDir,
            StreamLogPath = prepared.StreamLogPath,
            TasksDirectory = Path.Combine(wave.Directory, "tasks"),
            ComposedPromptBytes = prepared.ComposedPromptBytes,
            Ceiling = WaveBreakdownInvoker.BreakdownTimeout,
            IntentManifestPath = manifestPresent ? manifest : null
        };
    }

    /// <summary>The BreakdownComplete path, unchanged in behaviour (doc 12 §5.2, #361 Phase 4).</summary>
    private JitCheckpointOutcome CompleteBreakdown(
        PlanDefinition plan, WaveNode wave, Dictionary<string, TaskResult> settled, AutonomyPolicy policy,
        string invocationToken, WaveNode? authoredWave, int authoredTaskCount)
    {
        // The wave was authored + passed 'guardrails validate' — it is now UNREVIEWED. Resolve the
        // review-gate FLOOR (doc 12 §5.2, issue #361 Phase 4) by consulting the configured threshold.
        // NEITHER branch writes a review marker — the harness never self-attests a review at any dial
        // setting (§5 floor 3, #375).
        ReviewGateDecision reviewGate = ResolveReviewGate();

        if (reviewGate == ReviewGateDecision.ProceedUnreviewed && authoredWave is not null)
        {
            // Option P — proceed-with-recorded-unreviewed-risk: record the indelible proceeded-unreviewed
            // decision, then hand the authored wave back to the loop to RUN (skip the review halt). The
            // run can never be reported fully-reviewed-green; the recorded decision is the durable teeth.
            RecordProceededUnreviewed(policy, wave.Dir, authoredTaskCount);
            return JitCheckpointOutcome.Proceed(authoredWave);
        }

        // Option E (default) — escalate to a human review pass: record the breakdown-complete audit, raise
        // a review-gate escalation through the shipped IEscalationSink (a human must run /guardrails-review
        // before the wave runs), and keep the shipped BreakdownComplete halt so later waves stay blocked
        // behind the barrier.
        DecisionEntry done = DriftDecisions.WaveBreakdownComplete(policy, wave.Dir, invocationToken, authoredTaskCount);
        _journal.RecordDecision(done);
        _observer.DecisionRecorded(done);
        if (_escalationSink is not null)
        {
            EscalateReviewGate(authoredWave ?? wave);
        }

        return JitCheckpointOutcome.HaltWith(BuildReport(plan, settled, cancelled: false)
            with { WaveHalt = BuildBreakdownCompleteHalt(wave, authoredTaskCount) });
    }

    /// <summary>
    /// The BreakdownFailed path: revert exactly what the attempt wrote (§14.11), record the decision, and halt
    /// with a detail that now NAMES the bound the session hit and states what moved and what was kept.
    /// </summary>
    private JitCheckpointOutcome FailBreakdown(
        PlanDefinition plan, WaveNode wave, Dictionary<string, TaskResult> settled, AutonomyPolicy policy,
        string invocationToken, BreakdownInventory? inventory, string rejectedRoot,
        WaveBreakdownOutcome outcome, string validateReport, int authoredTaskCount, string? reason)
    {
        RevertSummary revert = RevertScoped(wave, inventory, rejectedRoot);
        string detail = ComposeBreakdownFailedDetail(
            validateReport, outcome, rejectedRoot, authoredTaskCount, revert, reason);
        DecisionEntry failed = DriftDecisions.WaveBreakdownFailed(policy, wave.Dir, invocationToken, detail);
        _journal.RecordDecision(failed);
        _observer.DecisionRecorded(failed);
        return JitCheckpointOutcome.HaltWith(BuildReport(plan, settled, cancelled: false)
            with { WaveHalt = BuildBreakdownFailedHalt(wave, detail) });
    }

    /// <summary>
    /// The BreakdownIncomplete path (SSOT §14.11): a VALID prefix from a cut-off session, PRESERVED rather
    /// than quarantined, with the manifest left in place as the resume ticket the JIT checkpoint reads.
    /// </summary>
    private JitCheckpointOutcome IncompleteBreakdown(
        PlanDefinition plan, WaveNode wave, Dictionary<string, TaskResult> settled, AutonomyPolicy policy,
        string invocationToken, WaveBreakdownOutcome outcome,
        int declared, int complete, IReadOnlyList<string> missing, int segments, string stopReason)
    {
        string detail = ComposeBreakdownIncompleteDetail(wave, outcome, declared, complete, missing, segments, stopReason);
        DecisionEntry entry = DriftDecisions.WaveBreakdownIncomplete(
            policy, wave.Dir, invocationToken, complete, declared, detail);
        _journal.RecordDecision(entry);
        _observer.DecisionRecorded(entry);
        return JitCheckpointOutcome.HaltWith(BuildReport(plan, settled, cancelled: false)
            with { WaveHalt = BuildBreakdownIncompleteHalt(wave, declared, complete, detail) });
    }

    /// <summary>
    /// The outcome of the between-wave JIT checkpoint (doc 12 §5.2, issue #361 Phase 4): either a terminal
    /// <see cref="RunReport"/> HALT (an honest-halt, a BreakdownComplete/Failed halt, or the review-gate
    /// Option E escalation-then-halt) OR — the review-gate Option P (<c>proceed-unreviewed</c>) — the freshly
    /// authored <see cref="WaveNode"/> the wave loop must now RUN in place of the empty JIT stub.
    /// </summary>
    private sealed record JitCheckpointOutcome(RunReport? Halt, WaveNode? ProceedWithWave)
    {
        public static JitCheckpointOutcome HaltWith(RunReport report) => new(report, null);

        public static JitCheckpointOutcome Proceed(WaveNode authoredWave) => new(null, authoredWave);
    }

    /// <summary>
    /// Splice a freshly-authored wave (Option P, §5.2) into the in-memory plan the run LOADED with that wave
    /// as an empty JIT stub — so the wave loop drains its real tasks and the end-of-run report lists them. The
    /// plan's <see cref="RunConfig"/> (the dial / autonomy block) is preserved unchanged; only the one wave and
    /// the flattened <see cref="PlanDefinition.Tasks"/> union are replaced.
    /// </summary>
    private static PlanDefinition SpliceAuthoredWave(PlanDefinition plan, WaveNode authoredWave)
    {
        var updatedWaves = plan.Waves
            .Select(w => string.Equals(w.Dir, authoredWave.Dir, StringComparison.Ordinal) ? authoredWave : w)
            .ToList();
        return plan with { Waves = updatedWaves, Tasks = updatedWaves.SelectMany(w => w.Tasks).ToList() };
    }

    /// <summary>
    /// Option P (§5.2): record the indelible <see cref="DecisionTokens.ProceededUnreviewed"/> <c>wave</c>-boundary
    /// decision for a wave the harness is about to run UNREVIEWED — the durable teeth that keep the run from
    /// ever being reported fully-reviewed-green. NEVER writes a review marker (§5 floor 3); also appends the
    /// run-level <c>autonomy.jsonl</c> detail line (§6) so the forensic trail stays non-lossy.
    /// </summary>
    private void RecordProceededUnreviewed(AutonomyPolicy policy, string waveDir, int taskCount)
    {
        var entry = new DecisionEntry
        {
            Boundary = "wave",
            Policy = AutonomyPolicies.Token(policy),
            Decision = DecisionTokens.ProceededUnreviewed,
            Subject = waveDir,
            Headline = $"Wave '{waveDir}' ran UNREVIEWED ({taskCount} task(s)) — review-gate proceed-unreviewed "
                       + "(§5.2 Option P). The run can NEVER be reported fully-reviewed-green.",
            Detail = "The wave's tasks still pass their deterministic guardrails; only the adversarial review "
                     + "pass was skipped, and that skip is indelible. The harness never marks a wave reviewed "
                     + "on a human's behalf.",
            At = DateTimeOffset.UtcNow,
            Gate = "review-gate"
        };
        _journal.RecordDecision(entry);
        _observer.DecisionRecorded(entry);
        AppendAutonomyRecord("review-gate", "wave", waveDir, "review-gate", DecisionTokens.ProceededUnreviewed,
            criticality: null, confidence: null, threshold: null, question: null, bestGuess: null, rationale: null);
    }

    /// <summary>
    /// Option E (default, §5.2): escalate the review gate for a freshly-authored but UNREVIEWED wave through the
    /// shipped <see cref="IEscalationSink"/> — the sink writes the record, appends the <c>escalated</c>
    /// <c>review-gate</c> <c>wave</c>-boundary decision, and surfaces it live — then adds the run-level
    /// <c>autonomy.jsonl</c> detail line. The wave still HALTS (the shipped BreakdownComplete halt); this only
    /// records the open question a human resolves out of band by running <c>/guardrails-review</c>. The review
    /// gate has no answer kind (§7.5): it clears only by a real human review pass, never a forged marker (§5
    /// floor 3).
    /// </summary>
    private void EscalateReviewGate(WaveNode wave)
    {
        string question = $"Wave '{wave.Dir}' was authored but is UNREVIEWED — a human must run "
                          + "/guardrails-review before it runs (doc 12 §5.2 Option E).";
        _escalationSink!.Escalate(new EscalationRequest
        {
            Gate = "review-gate",
            Subject = wave.Dir,
            Question = question,
            Context = BuildGateContext("review-gate", wave.Dir, question),
            Criticality = null,
            DefinitionHash = Journal.WaveDefinitionHash.Compute(wave),
            At = DateTimeOffset.UtcNow
        });
        AppendAutonomyRecord("review-gate", "wave", wave.Dir, "review-gate", DecisionTokens.Escalated,
            criticality: null, confidence: null, threshold: null, question: question, bestGuess: null, rationale: null);
    }

    /// <summary>
    /// The deterministic gate on the breakdown output (design-360 Q3, doc 11 §9.4): re-load + validate the
    /// plan IN-PROCESS — exactly what <c>guardrails validate</c> does (PlanLoader + PlanValidator), no
    /// subprocess (and never the installed tool — dogfood safety). Returns whether the plan is error-free,
    /// the joined diagnostic report, and how many tasks the target wave now carries.
    /// </summary>
    /// <param name="wavePrefixIsIncomplete">
    /// True when a usable <c>breakdown-intent.json</c> still owes folders — i.e. the wave on disk is a
    /// KNOWINGLY partial prefix rather than a finished wave. It suppresses the completeness errors that
    /// such a prefix cannot satisfy by construction (see <see cref="UnsatisfiableWhileIncomplete"/>);
    /// every other error still vetoes, because a prefix that is malformed is not worth resuming.
    /// </param>
    private static (bool Valid, string Report, int AuthoredTaskCount, WaveNode? AuthoredWave) ValidatePlanAfterBreakdown(
        string planDirectory, string waveDir, bool wavePrefixIsIncomplete = false)
    {
        var loader = new PlanLoader();
        PlanLoadResult loadResult = loader.Load(planDirectory);
        var diagnostics = new List<Diagnostic>(loadResult.Diagnostics);
        if (loadResult.Plan is not null && !loadResult.HasErrors)
        {
            diagnostics.AddRange(new PlanValidator().Validate(loadResult.Plan));
        }

        // #501. The gate asks "is this SOUND as far as it goes", not "is this a finished plan" — those are
        // different questions and conflating them silently defeated the whole #385/#402 salvage. Measured
        // on the first real JIT run: a wave cut off after 5 of 12 task folders had, by construction, no
        // wave-root guardrails/ exit gate yet (breakdowns author tasks first, gates last), so GR2028 fired,
        // `valid` went false, and the prefix the manifest existed to preserve was reverted wholesale — while
        // GR2063 printed "the valid prefix is preserved and the JIT checkpoint resumes it" in the SAME halt.
        // Suppressed errors stay in the report; they simply stop casting a veto they cannot fairly cast.
        Diagnostic[] errors = diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
        Diagnostic[] excused = wavePrefixIsIncomplete
            ? errors.Where(UnsatisfiableWhileIncomplete).ToArray()
            : [];
        Diagnostic[] blocking = errors.Except(excused).ToArray();
        bool valid = blocking.Length == 0;
        WaveNode? authoredWave = loadResult.Plan?.Waves
            .FirstOrDefault(w => string.Equals(w.Dir, waveDir, StringComparison.Ordinal));
        int authoredTaskCount = authoredWave?.Tasks.Count ?? 0;

        // #501: say WHICH errors decided the verdict, and which were excused. Without this the two paths
        // are indistinguishable from the outside — the original bug printed a full diagnostic report and
        // a "prefix preserved" warning side by side, and nothing said which error had actually cast the
        // veto. A reader could not tell a suppression that fired from one that never ran.
        var header = new List<string>
        {
            // #512: report the COMPOSITE decision, not the validate half. `gateRejected` is
            // `!valid || authoredTaskCount == 0`, so a session that authored NOTHING is rejected even
            // though an empty prefix validates trivially — and printing "PASS" there was wrong in exactly
            // the case a reader most needs this file (a breakdown that produced nothing, e.g. one killed
            // by a provider 429 at turn 1). Every case #501 was written against had authoredTasks > 0, so
            // the two verdicts agreed and the gap never showed.
            $"gate verdict : {(valid && authoredTaskCount > 0 ? "PASS" : "REJECT")}  (blocking={blocking.Length}, excused={excused.Length}, authoredTasks={authoredTaskCount}{(authoredTaskCount == 0 ? " — nothing was authored" : string.Empty)})",
            $"prefix state : {(wavePrefixIsIncomplete ? "KNOWINGLY INCOMPLETE (manifest still owes folders)" : "not flagged incomplete")}"
        };
        if (blocking.Length > 0)
        {
            header.Add($"blocking     : {string.Join(", ", blocking.Select(d => d.Code).Distinct())}");
        }
        if (excused.Length > 0)
        {
            header.Add($"excused (#501): {string.Join(", ", excused.Select(d => d.Code).Distinct())} — unsatisfiable while the wave is unfinished; NOT a veto");
        }

        string report = string.Join("\n", header.Concat(["", .. diagnostics.Select(d => d.ToString())]));
        return (valid, report, authoredTaskCount, authoredWave);
    }

    /// <summary>
    /// Errors a knowingly-INCOMPLETE wave prefix cannot satisfy by construction, and which must therefore
    /// not veto its preservation (#501).
    ///
    /// <para>Deliberately a NAMED ALLOW-LIST of one, not a category. Every entry has to earn its place by
    /// being unsatisfiable <i>because the wave is unfinished</i> — never merely inconvenient. The temptation
    /// is to widen this to "completeness-ish" codes; that would turn the gate into a rubber stamp and the
    /// resumed prefix into something nobody checked.</para>
    ///
    /// <para><b>GR2028</b> requires a parallel-topology wave's <c>guardrails/</c> exit gate to carry an
    /// integration re-run. A breakdown authors task folders first and the wave gates last, so any truncation
    /// leaves no exit gate at all — the error is a statement that the wave is unfinished, which is precisely
    /// what the manifest already told us.</para>
    ///
    /// <para>Not here, on purpose: a malformed <c>task.json</c>, a bad reference, a cycle, a missing
    /// <c>writeScope</c> (GR2041). Those say the prefix itself is wrong, and resuming onto a wrong prefix is
    /// worse than re-authoring. GR2062 and GR2063 need no entry — both are WARNINGS and never vetoed.</para>
    /// </summary>
    /// <summary>
    /// Tee the post-breakdown gate's reasoning to <c>gate-decision.txt</c> beside the session's stream and
    /// transcript (#501). Best-effort by construction — a log write must never affect a run.
    ///
    /// <para><b>Why a file and not just the halt text.</b> The halt carries the validate report only when
    /// the gate REJECTS. The case that actually went wrong was the opposite one: a prefix that should have
    /// been kept and was not, with GR2063 announcing a resume that never came. That path printed no report,
    /// so there was nothing to read afterwards. Recording the verdict, its inputs, and which errors were
    /// excused makes the decision reconstructible from the logs on every path — which is the substitute
    /// for the unit test this bug has so far resisted.</para>
    /// </summary>
    private static void TeeBreakdownGateDecision(
        string breakdownLogDir,
        WaveNode wave,
        string report,
        BreakdownIntentRead intentRead,
        int declared,
        IReadOnlyList<string> missing,
        WaveBreakdownOutcome outcome)
    {
        try
        {
            Directory.CreateDirectory(breakdownLogDir);
            string manifest = intentRead.Usable is not null
                ? $"usable — declares {declared}, still owes {missing.Count}"
                : $"NOT usable ({intentRead.Presence}) — no salvage is possible without one";

            string text = string.Join("\n",
            [
                $"wave            : {wave.Dir}",
                $"session ended   : {(outcome.TerminatedCleanly ? "cleanly" : outcome.FailureKindToken ?? "not cleanly")}"
                    + $" (turns {outcome.NumTurns?.ToString() ?? "?"} of {outcome.MaxTurns?.ToString() ?? "?"})",
                $"intent manifest : {manifest}",
                missing.Count > 0 ? $"still owed      : {string.Join(", ", missing)}" : "still owed      : nothing",
                "",
                report,
                ""
            ]);
            File.WriteAllText(Path.Combine(breakdownLogDir, "gate-decision.txt"), text);
        }
        catch
        {
            // Best-effort: diagnostics must never be able to fail a run.
        }
    }

    internal static bool UnsatisfiableWhileIncomplete(Diagnostic diagnostic) =>
        string.Equals(diagnostic.Code, DiagnosticCodes.PlanGuardrailsMissingIntegrationReRun, StringComparison.Ordinal);

    /// <summary>The hard cap on breakdown segments for ONE wave in ONE run (SSOT §14.11, design 20 §4.5).</summary>
    private const int MaxBreakdownSegments = 3;

    /// <summary>
    /// Revert exactly what the breakdown attempt wrote and nothing it did not (SSOT §14.11, #471). With an
    /// inventory this is precise: attempt-written files move to <c>rejected/</c> preserving relative paths,
    /// pre-existing files are restored byte-for-byte, and untouched pre-existing files — a human's
    /// hand-authored wave gate among them — are left where they are. Without one (the capture itself failed)
    /// it degrades to the pre-#471 whole-<c>tasks/</c> move, because a guess about provenance is worse than
    /// a coarse but honest fallback.
    /// </summary>
    private RevertSummary RevertScoped(WaveNode wave, BreakdownInventory? inventory, string rejectedRoot)
    {
        if (inventory is not null)
        {
            RevertSummary reverted = inventory.Revert(rejectedRoot);

            // Harness writer 2 of 5 (plan 31 §5.3): BreakdownInventory.Revert moved attempt-created files to
            // rejected/ and restored pre-existing ones from snapshot.
            RebaselinePlanEdits();
            return reverted;
        }

        RevertSummary quarantined = QuarantineWholeTasksFolder(wave, rejectedRoot);

        // Harness writer 4 of 5 (plan 31 §5.3): QuarantineWholeTasksFolder moved the wave's ENTIRE tasks/
        // directory to rejected/tasks — or, on its catch branch, hard-deleted it recursively.
        RebaselinePlanEdits();
        return quarantined;
    }

    /// <summary>
    /// The pre-#471 fallback: move the whole <c>tasks/</c> to <c>rejected/tasks/</c> and RESTORE an empty stub
    /// — so a partial invalid wave never wedges the next resume's plan LOAD (§14.4/doc 11 §9.4), the
    /// checkpoint cleanly re-fires, and the rejected output is preserved for a human. Best-effort with a
    /// revert (empty the stub) fallback if the move fails. Reached only when the inventory capture failed.
    /// </summary>
    private static RevertSummary QuarantineWholeTasksFolder(WaveNode wave, string rejectedRoot)
    {
        string tasksDir = Path.Combine(wave.Directory, "tasks");
        string rejectedTasks = Path.Combine(rejectedRoot, "tasks");
        try
        {
            Directory.CreateDirectory(rejectedRoot);
            if (Directory.Exists(tasksDir))
            {
                if (Directory.Exists(rejectedTasks))
                {
                    Directory.Delete(rejectedTasks, recursive: true);
                }

                Directory.Move(tasksDir, rejectedTasks);
            }

            Directory.CreateDirectory(tasksDir); // restore the empty JIT stub → plan stays loadable, checkpoint re-fires
        }
        catch
        {
            // Fallback (design-360 §9.4: "revert is the simpler fallback"): if the move failed, empty the
            // stub so the plan still loads. The most useful debugging artifact may be lost, but liveness wins.
            try
            {
                if (Directory.Exists(tasksDir))
                {
                    Directory.Delete(tasksDir, recursive: true);
                }

                Directory.CreateDirectory(tasksDir);
            }
            catch
            {
                // Nothing more we can safely do; the halt still names the failure for the human.
            }
        }

        return new RevertSummary { MovedPaths = ["tasks/"] };
    }

    /// <summary>
    /// The #489 structural cleanup: whatever leaves the breakdown other than a settled classification —
    /// a Ctrl+C, an unexpected fault — must still leave the plan folder in a state the loader ACCEPTS. Sweep
    /// the half-written trailing folders, then decide: a valid prefix that a manifest makes RESUMABLE is
    /// kept (the checkpoint re-fires on it); anything else is reverted to the pre-invocation state. Never
    /// throws — a cleanup fault must not mask the cancellation it is cleaning up after.
    /// </summary>
    private void LeaveWaveLoadable(
        PlanDefinition plan, WaveNode wave, BreakdownInventory? inventory, string rejectedRoot)
    {
        try
        {
            inventory?.SweepIncompleteTrailingTaskFolders(rejectedRoot);

            // Harness writer 3 of 5, call site 2 of its TWO (plan 31 §5.3): the CANCEL/FAULT cleanup sweep.
            // Without this the fault path is blind — a cancelled or faulted wave sweeps task folders into
            // rejected/tasks/, the watch sees its own harness's deletions on the next Poll(), and reports
            // them to the operator as edits they did not make.
            RebaselinePlanEdits();

            bool resumable = BreakdownIntent.TryRead(wave.Directory) is { } intent
                             && intent.MissingFolders(wave.Directory).Count > 0;

            // Same #501 ordering as the gate above: `resumable` is exactly the "knowingly incomplete" fact,
            // so it has to be known BEFORE the validate that decides whether to keep the prefix.
            (bool valid, _, int authoredTaskCount, _) =
                ValidatePlanAfterBreakdown(plan.PlanDirectory, wave.Dir, wavePrefixIsIncomplete: resumable);

            if (valid && authoredTaskCount > 0 && resumable)
            {
                return; // a preserved, resumable prefix — loadable, and the manifest keeps it honest
            }

            RevertScoped(wave, inventory, rejectedRoot);
        }
        catch
        {
            // Best-effort by construction: this runs in a finally, often with the token already signalled.
        }
    }

    /// <summary>Delete a wave's breakdown-intent manifest unconditionally — the wave has settled, so the attempt is over.</summary>
    private static void RemoveIntentManifest(WaveNode wave)
    {
        try
        {
            string path = BreakdownIntent.PathFor(wave.Directory);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // best-effort; a stale satisfied manifest is silent at validate anyway
        }
    }

    /// <summary>
    /// True when the wave carries an UNSATISFIED <c>state/breakdown-intent.json</c> — the durable signal that
    /// a cut-off breakdown left a valid PREFIX rather than a finished wave (SSOT §14.11, GR2063). It is what
    /// re-opens the JIT checkpoint on a wave that already has tasks, so the remainder is RESUMED instead of
    /// the partial wave being run as though it were whole. Absent or satisfied manifest ⇒ false (the wave is
    /// authored, and the checkpoint stays closed exactly as before).
    /// </summary>
    private static bool HasUnsatisfiedBreakdownIntent(WaveNode wave) =>
        BreakdownIntent.TryRead(wave.Directory) is { } intent
        && intent.MissingFolders(wave.Directory).Count > 0;

    /// <summary>How many of the manifest's declared folders exist COMPLETE on disk right now (0 with no manifest).</summary>
    private static int CountSatisfiedDeclaredFolders(WaveNode wave) =>
        BreakdownIntent.TryRead(wave.Directory) is { } intent
            ? intent.DeclaredFolders().Count - intent.MissingFolders(wave.Directory).Count
            : 0;

    /// <summary>Build the resume context for segment <paramref name="segment"/> from the manifest + what is on disk.</summary>
    private static BreakdownResumeContext? BuildResumeContext(WaveNode wave, int segment)
    {
        if (BreakdownIntent.TryRead(wave.Directory) is not { } intent)
        {
            return null;
        }

        IReadOnlyList<string> declared = intent.DeclaredFolders();
        IReadOnlyList<string> owed = intent.MissingFolders(wave.Directory);
        var owedSet = new HashSet<string>(owed, StringComparer.Ordinal);
        return new BreakdownResumeContext
        {
            Segment = segment,
            MaxSegments = MaxBreakdownSegments,
            DeclaredCount = declared.Count,
            CompleteFolders = [.. declared.Where(f => !owedSet.Contains(f))],
            OwedFolders = owed
        };
    }

    /// <summary>Why the segment loop stopped, in one clause the halt detail can carry verbatim.</summary>
    private static string StopReasonFor(bool nothingOwed, bool noProgress, bool costCapHit, bool lastSegment) =>
        nothingOwed
            ? "every declared task folder is present and the wave validates, but the session never reported "
              + "completion, so the harness will not call it complete"
            : noProgress
                ? "the last segment added no complete task folder, so resuming again would repeat it"
                : costCapHit
                    ? "the run's 'maxCostUsd' cap is reached, so no further segment was spent"
                    : lastSegment
                        ? $"the {MaxBreakdownSegments}-segment cap for one wave in one run is reached"
                        : "the segment loop stopped";

    private static string ComposeBreakdownFailedDetail(
        string validateReport, WaveBreakdownOutcome outcome, string quarantineDir, int authoredTaskCount,
        RevertSummary revert, string? reason)
    {
        var sb = new StringBuilder();
        if (reason is { Length: > 0 })
        {
            sb.Append(reason).Append('\n');
        }
        else if (outcome.Error is { Length: > 0 } err)
        {
            sb.Append($"The breakdown invocation faulted: {err}\n");
        }
        else if (!outcome.TerminatedCleanly)
        {
            // Milestone 1 (#385): NAME the bound. "Timeout" and "MaxTurns" are two different remedies and
            // only the second one is a budget — the halt used to say neither.
            sb.Append($"The breakdown session {outcome.CutOffCause}{TurnEvidence(outcome)}.\n");
        }

        if (authoredTaskCount == 0 && string.IsNullOrWhiteSpace(validateReport))
        {
            sb.Append("The breakdown authored NO tasks for this wave.\n");
        }
        else
        {
            sb.Append("The authored wave FAILED 'guardrails validate':\n");
            sb.Append(validateReport);
            sb.Append('\n');
        }

        AppendRevertSummary(sb, quarantineDir, revert);

        // The NEXT ACTION, and it INVERTS against the incomplete halt below (design 23 §6.1/§6.2): this
        // attempt was reverted, so the re-run does not resume anything — it starts over, and the operator
        // has to change something first or buy the same failure twice.
        sb.Append("Next: this checkpoint re-fires on the next 'guardrails run', and the breakdown starts "
                  + "FROM SCRATCH.\nFix the brief, split the wave, or author the tasks by hand first.");
        return sb.ToString();
    }

    private static string ComposeBreakdownIncompleteDetail(
        WaveNode wave, WaveBreakdownOutcome outcome, int declared, int complete,
        IReadOnlyList<string> missing, int segments, string stopReason)
    {
        var sb = new StringBuilder();
        sb.Append($"The breakdown session {outcome.CutOffCause}{TurnEvidence(outcome)} after authoring ")
          .Append($"{complete} of {declared} declared task(s) across {segments} segment(s); {stopReason}.\n");
        sb.Append("The valid prefix was PRESERVED — nothing was quarantined, and nothing that pre-dated the "
                  + "attempt was touched.\n");

        // #508: the two cases below are DIFFERENT halts and must not share their wording. Everything that
        // is only true of a genuine shortfall — "not ready for review", the GR2063 promise — lives inside
        // the `missing` branch. Emitting them for a declared==authored prefix produced a halt that
        // contradicted itself twice ("NOT ready for review" beside "nothing is owed, review it") and
        // promised a GR2063 that CANNOT fire once the manifest is satisfied. Measured on two consecutive
        // waves, and the operator's read of the first one was "it's stuck".
        if (missing.Count > 0)
        {
            // Design 20 §4.2's safety floor, made OPERATOR-facing (design 23 §6.2): a prefix of N
            // well-formed folders reads as complete to a HUMAN unless the halt says otherwise.
            sb.Append("This wave is NOT complete and is NOT ready for review. Do not run /guardrails-review "
                      + "on it yet.\n");
            sb.Append($"Still owed ({missing.Count}): {string.Join(", ", missing)}\n");
            sb.Append($"Next: re-run 'guardrails run'. The breakdown RESUMES '{wave.Dir}' from the preserved "
                      + "prefix and authors only the folders still owed; the composed brief is not re-paid "
                      + "for work already on disk.\n");
            sb.Append("'guardrails validate' reports GR2063 for this wave until the manifest is satisfied or "
                      + "removed; that warning is the record of the shortfall, not a defect in the prefix.");
        }
        else
        {
            sb.Append("NOTHING IS OWED: every declared task folder is present and the wave validates. What "
                      + "is missing is only the session's own sign-off — it was cut off before reporting "
                      + "completion, and the harness will not infer completion it was not told about.\n");
            sb.Append($"Next: read '{wave.Dir}/tasks/' yourself. If it looks right, delete "
                      + $"'{wave.Dir}/state/{BreakdownIntent.FileName}' to accept the wave as authored, then "
                      + "run /guardrails-review on it as usual.\n");
            sb.Append("GR2063 will NOT fire for this wave — the manifest is satisfied, so there is no "
                      + "shortfall to report. Do not go looking for that warning as confirmation.");
        }

        return sb.ToString();
    }

    /// <summary>The turn evidence beside the cut-off cause — the number that told us the turn cap was never binding.</summary>
    private static string TurnEvidence(WaveBreakdownOutcome outcome) =>
        outcome.NumTurns is { } turns
            ? outcome.MaxTurns is { } cap ? $" (used {turns} of {cap} turns)" : $" (used {turns} turns)"
            : "";

    /// <summary>
    /// State plainly what the quarantine moved and what it kept (#471 §5.3). The old text — "the wave
    /// reverted to its empty stub" — was false in the direction that misleads: eight files stayed behind.
    /// </summary>
    private static void AppendRevertSummary(StringBuilder sb, string quarantineDir, RevertSummary revert)
    {
        sb.Append("Everything this attempt wrote was reverted; nothing that pre-dated it was touched.\n");
        sb.Append($"  moved to      : {quarantineDir}\n");
        sb.Append($"  files moved   : {DescribePaths(revert.MovedPaths)}\n");
        if (revert.RestoredPaths.Count > 0)
        {
            sb.Append($"  restored      : {DescribePaths(revert.RestoredPaths)}\n");
        }

        sb.Append($"  left in place : {DescribePaths(revert.KeptPaths)} (pre-existing)\n");
        sb.Append("The wave folder is byte-identical to its pre-breakdown state; PlanDefinitionHash is unchanged.\n");
    }

    /// <summary>A short, bounded rendering of a path list for a halt detail (never an unbounded wall of paths).</summary>
    private static string DescribePaths(IReadOnlyList<string> paths)
    {
        const int max = 8;
        if (paths.Count == 0)
        {
            return "(none)";
        }

        return paths.Count <= max
            ? string.Join(", ", paths)
            : $"{string.Join(", ", paths.Take(max))} … (+{paths.Count - max} more)";
    }

    private static WaveHalt BuildBreakdownCompleteHalt(WaveNode wave, int taskCount)
    {
        string detail =
            $"'{wave.Dir}' authored ({taskCount} task(s)) and passed 'guardrails validate'. The output is a "
            + "DRAFT — the review gate is the human gate; do not skip it:\n"
            + $"  1. Inspect {wave.Dir}/tasks/ — verify the tasks, guardrails, and the DAG.\n"
            + "  2. Run /guardrails-review on the wave folder.\n"
            + "  3. Re-run 'guardrails run' to continue.\n"
            + "The harness never marks a wave reviewed on a human's behalf (any autonomyPolicy).";
        return new WaveHalt
        {
            WaveDir = wave.Dir,
            Kind = WaveHaltKind.BreakdownComplete,
            Headline = $"Wave '{wave.Dir}' broken down ({taskCount} task(s)) — review it before it runs (SSOT §14.4).",
            Detail = detail,
            WaveDirectory = wave.Directory
        };
    }

    private static WaveHalt BuildBreakdownFailedHalt(WaveNode wave, string detail) =>
        new()
        {
            WaveDir = wave.Dir,
            Kind = WaveHaltKind.BreakdownFailed,
            Headline = $"Wave '{wave.Dir}' breakdown FAILED validation — partial output quarantined (SSOT §14.4).",
            Detail = detail,
            WaveDirectory = wave.Directory
        };

    /// <summary>
    /// The two headlines a preserved-prefix halt can carry (#508). <b>"INCOMPLETE — 5 of 5" is a
    /// contradiction</b>, and it shipped on two consecutive barriers: when the declared count IS met, the
    /// wave is not what fell short — the SESSION is, having been cut off before it reported completion.
    /// The operator reading the first one concluded the run was stuck.
    /// <para>Internal so the halt builder and its test read the same function; there is no second copy to
    /// drift.</para>
    /// </summary>
    internal static string ComposeBreakdownIncompleteHeadline(string waveDir, int declared, int complete) =>
        complete >= declared
            ? $"Wave '{waveDir}' breakdown UNCONFIRMED — all {declared} declared task(s) were authored and "
              + "the wave validates, but the session was cut off before reporting completion (SSOT §14.11)."
            : $"Wave '{waveDir}' breakdown INCOMPLETE — {complete} of {declared} declared task(s) authored; "
              + "the valid prefix is preserved for resume (SSOT §14.11).";

    private static WaveHalt BuildBreakdownIncompleteHalt(WaveNode wave, int declared, int complete, string detail) =>
        new()
        {
            WaveDir = wave.Dir,
            Kind = WaveHaltKind.BreakdownIncomplete,
            Headline = ComposeBreakdownIncompleteHeadline(wave.Dir, declared, complete),
            Detail = detail,
            WaveDirectory = wave.Directory
        };

    private static WaveHalt BuildGateHalt(WaveNode wave, WaveHaltKind kind, IReadOnlyList<GuardrailResult> failed)
    {
        string gate = kind == WaveHaltKind.EntryGateFailed ? "entry preflight" : "exit gate";
        string names = failed.Count == 0 ? "(no per-check detail)" : string.Join(", ", failed.Select(f => f.Name));
        return new WaveHalt
        {
            WaveDir = wave.Dir,
            Kind = kind,
            Headline = $"Wave '{wave.Dir}' {gate} FAILED: {names}",
            Detail = string.Join("\n", failed.Select(f => $"{f.Name} — {f.Reason ?? "failed"}")),
            FailedGates = failed
        };
    }

    /// <summary>
    /// Persist the machine-readable reason a wave gate STOPPED the run (issue #432, SSOT §7 <c>halt</c>).
    /// A gate halt settles no task, so without this the journal reads as "nothing happened" — every task
    /// still <c>pending</c>, the cause only on the operator's terminal. Records the SAME headline the
    /// console prints, the failing check names + reasons, and the plan-relative path to their captured
    /// stdout/stderr. Purely additive: it does not touch the wave's own entry/exit marker or the report.
    /// </summary>
    private void RecordGateHalt(Journal.RunHaltKind kind, string waveDir, WaveHalt halt, GateOutcome outcome) =>
        _journal.RecordHalt(new Journal.RunHalt
        {
            Kind = kind,
            HaltedAt = DateTimeOffset.UtcNow,
            Headline = halt.Headline,
            WaveDir = waveDir,
            FailedChecks = outcome.Failed
                .Select(f => new Journal.FailedGuardrail { Name = f.Name, Reason = f.Reason ?? "failed" })
                .ToList(),
            LogDir = outcome.RelativeLogDir
        });

    private static IReadOnlyDictionary<string, PlanBranchWaveRecord> WithWaveMarker(
        IReadOnlyDictionary<string, PlanBranchWaveRecord> map, string waveDir, PlanBranchWaveRecord record)
    {
        var copy = new Dictionary<string, PlanBranchWaveRecord>(map, StringComparer.Ordinal) { [waveDir] = record };
        return copy;
    }

    /// <summary>Shorten a <c>sha256:</c>-prefixed hash for display.</summary>
    private static string ShortHash(string hash)
    {
        const string prefix = "sha256:";
        string body = hash.StartsWith(prefix, StringComparison.Ordinal) ? hash[prefix.Length..] : hash;
        return body.Length <= 10 ? body : body[..10];
    }

    /// <summary>
    /// The resume drift pre-pass (§7.2, #274 Part A): determine the pre-settled-green candidates (journal
    /// <c>Succeeded</c> OR a plan-branch trailer) and, for each one carrying a recorded definition hash,
    /// recompute the current <see cref="Journal.TaskDefinitionHash"/> and record a drift when they differ.
    /// A recorded-absent candidate (pre-upgrade) is treated as "unknown — assume unchanged". Reads the
    /// task's definition files from disk, so its IO is wrapped by the caller's #150 honest-abort guard.
    /// </summary>
    private (HashSet<string> PreSettledGreen, List<DefinitionDriftReporter.DriftInput> Drifted)
        DetectDefinitionDrift(
            IReadOnlyList<TaskNode> tasks,
            IReadOnlyDictionary<string, PlanBranchTaskRecord> planBranchRecords,
            bool trailerTracking)
    {
        var preSettledGreen = new HashSet<string>(StringComparer.Ordinal);
        var drifted = new List<DefinitionDriftReporter.DriftInput>();

        foreach (TaskNode task in tasks)
        {
            bool journalGreen = _journal.StatusOf(task.Id) == JournalTaskStatus.Succeeded;
            planBranchRecords.TryGetValue(task.Id, out PlanBranchTaskRecord? trailer);
            if (!journalGreen && trailer is null)
            {
                continue;
            }

            // Part C resume reconciliation (#274, SSOT §7.2): where the plan branch is the authoritative
            // integration record (worktree mode), a task that the journal calls Succeeded but whose
            // integration trailer is ABSENT from the current plan-branch history had its commit rewound off
            // (a crash mid safe-drift-resolution, or an external rewind). Its work is NOT on the branch —
            // it MUST re-run, never be skipped. This closes the new invariant Part C's reset --hard can
            // break, catching the inconsistency however it arose. Serial / non-trailer providers keep the
            // journal-only semantics (no trailers to consult).
            if (trailerTracking && journalGreen && trailer is null)
            {
                _journal.ResetTaskToPending(task.Id);
                continue; // pending → scheduled (re-run)
            }

            preSettledGreen.Add(task.Id);

            // Recorded hash: prefer the journal's (the primary record); fall back to the plan-branch
            // trailer (covers a journal-reset resume where only the plan branch survives). Both are
            // stamped at the same settle, so they agree; either being present enables the check.
            string? recordedHash = _journal.RecordedDefinitionHash(task.Id) ?? trailer?.DefinitionHash;
            if (recordedHash is null)
            {
                continue;
            }

            string currentHash = Journal.TaskDefinitionHash.Compute(task);
            if (!string.Equals(recordedHash, currentHash, StringComparison.Ordinal))
            {
                drifted.Add(new DefinitionDriftReporter.DriftInput(
                    task.Id, recordedHash, currentHash, trailer?.CommitSha));
            }
        }

        return (preSettledGreen, drifted);
    }

    /// <summary>The outcome of the Part C gate: a decision (rewound + reset) or a halt, with the safe/unsafe distinction the CLI needs to render the right remedy.</summary>
    private sealed record DriftGateResult(
        DecisionEntry? Decision, bool SafeToAutoResolve, string? Refusal, string? BlockingTask)
    {
        public static DriftGateResult Resolved(DecisionEntry decision) => new(decision, true, null, null);

        /// <summary>Halt because the rewind is UNSOUND (a non-suffix / uncontained fan-in / trailer-less commit) — no flag authorizes it.</summary>
        public static DriftGateResult Unsafe(string? refusal, string? blockingTask) => new(null, false, refusal, blockingTask);

        /// <summary>Halt a provably-SAFE drift because the policy/consent did not authorize it (strict halt, unconfirmed prompt, a consent-void plan, or a moved tip).</summary>
        public static DriftGateResult HaltSafe() => new(null, true, null, null);
    }

    /// <summary>
    /// Part C safe-auto-resolve (issue #274, SSOT §7.2). The drifted set <c>S</c> = the drifted tasks ∪
    /// their <see cref="DependencyGraph.TransitiveDependentsOf"/> closure (a changed producer can change a
    /// consumer's inputs). Evaluate whether <c>S</c> forms a provably-safe trailing suffix of the plan
    /// branch (<see cref="SafeSuffixEvaluator"/> via the provider), then apply the gating:
    /// <list type="bullet">
    ///   <item>UNSAFE (Refused) → HALT (unsafe). No policy authorizes an unsound rewind.</item>
    ///   <item><see cref="AutonomyPolicy.Halt"/> → HALT (safe; strict opt-out, the Part A behavior).</item>
    ///   <item><see cref="AutonomyPolicy.Auto"/> → resolve on this run's own fresh decision (pre-authorized spend).</item>
    ///   <item><see cref="AutonomyPolicy.Prompt"/> → resolve ONLY when the CLI captured an operator <c>y</c>
    ///     (<see cref="_driftAuthorization"/>) AND that captured plan still matches this fresh decision AND
    ///     the branch tip has not moved; otherwise HALT (Core never prompts).</item>
    /// </list>
    /// The destructive section is CRASH-ATOMIC: a rewind-intent marker is written BEFORE the
    /// <c>git reset --hard</c> and cleared only AFTER both the rewind and every journal-reset persist, so a
    /// kill in between is idempotently replayed on resume. A COMPARE-AND-SWAP on the plan-branch tip guards
    /// against a concurrent same-plan session (or an operator editing during the blocking prompt) making the
    /// harness rewind a set the decision/human never saw — a mismatch HALTS, never rewinds.
    /// </summary>
    private DriftGateResult TryResolveDrift(
        PlanDefinition plan,
        DependencyGraph graph,
        List<DefinitionDriftReporter.DriftInput> drifted,
        IntegrationHandle? integ)
    {
        // S = drifted ∪ transitive descendants (this run's OWN fresh computation).
        var safeSet = new HashSet<string>(StringComparer.Ordinal);
        foreach (DefinitionDriftReporter.DriftInput d in drifted)
        {
            safeSet.Add(d.TaskId);
            foreach (string dependent in graph.TransitiveDependentsOf(d.TaskId))
            {
                safeSet.Add(dependent);
            }
        }

        // Safety check against the plan branch. Serial / no-provider → NothingToRewind (a journal-only
        // reset is sound where there is no branch to carry a stale commit). The journal-recorded settle
        // hashes corroborate each removed commit's Guardrails-Task-Hash: trailer (issue #322) — a commit
        // carrying a hash the harness never recorded is a copied-trailer #197 hand-fix → REFUSE.
        SafeSuffixDecision decision = _worktreeProvider is { } provider && integ is { } activeInteg
            ? provider.EvaluateSafeSuffix(activeInteg, safeSet, _journal.RecordedDefinitionHashes())
            : SafeSuffixDecision.Nothing();

        // Refuse floor (un-overridable): an unsafe rewind ALWAYS halts, regardless of policy. Surface the
        // reason + blocker so the CLI steers to the always-sound rebuild rather than a re-halting flag.
        if (decision.Outcome == SafeSuffixOutcome.Refused)
        {
            return DriftGateResult.Unsafe(decision.Refusal, decision.BlockingTask);
        }

        // Authorization gate (only a provably-safe / nothing-to-rewind drift reaches here). The switch
        // decides resolve-vs-halt ONLY; the DecisionEntry is built from _plan.Config.AutonomyPolicy below.
        switch (_plan.Config.AutonomyPolicy)
        {
            case AutonomyPolicy.Auto:
                break;

            case AutonomyPolicy.Prompt when _driftAuthorization is { } auth:
                // Consent integrity: the operator approved a SPECIFIC plan (from the probe's preview). If
                // files edited during the blocking prompt changed what would be rewound (S / target
                // diverges), HALT — never rewind a set the human did not see.
                if (!AuthorizationMatches(auth, safeSet, decision))
                {
                    return DriftGateResult.HaltSafe();
                }

                break;

            default: // Halt policy, or an unconfirmed Prompt policy — Core never prompts.
                return DriftGateResult.HaltSafe();
        }

        // Compare-and-swap: for a real rewind, the branch must still be exactly where the decision (and,
        // for a prompt, the operator) saw it. A concurrent same-plan session that advanced/rewound the
        // branch since is DETECTED here → HALT rather than discard its work.
        string? resetTarget = decision.Outcome == SafeSuffixOutcome.Safe ? decision.ResetTarget : null;
        if (decision.Outcome == SafeSuffixOutcome.Safe)
        {
            string authorizedTip = _driftAuthorization?.ExpectedTip ?? decision.ExpectedTip ?? "";
            string currentTip = _worktreeProvider is { } tipProvider && integ is { } tipInteg
                ? tipProvider.CurrentPlanBranchTip(tipInteg)
                : "";
            if (!string.Equals(currentTip, authorizedTip, StringComparison.Ordinal))
            {
                return DriftGateResult.HaltSafe();
            }
        }

        // CRASH-ATOMIC destructive section. The marker is only needed for a real plan-branch rewind
        // (trailer-tracking); a serial journal-only reset has no discarded commits to lose and self-heals
        // via re-detection. Write BEFORE the rewind, clear only AFTER both effects persist.
        bool useMarker = decision.Outcome == SafeSuffixOutcome.Safe
            && _worktreeProvider?.TracksPlanBranchTrailers == true;
        if (useMarker)
        {
            State.RewindIntent.Write(_plan.PlanDirectory, new State.RewindIntent
            {
                SafeSet = safeSet.OrderBy(x => x, StringComparer.Ordinal).ToList(),
                PreRewindTip = decision.ExpectedTip,
                ResetTarget = resetTarget
            });
        }

        if (resetTarget is not null && _worktreeProvider is { } rewindProvider && integ is { } rewindInteg)
        {
            rewindProvider.RewindPlanBranchTo(rewindInteg, resetTarget);
        }

        // Journal-reset every member of S so the next scheduling wave re-runs it from the clean base.
        foreach (string taskId in safeSet)
        {
            _journal.ResetTaskToPending(taskId);
        }

        if (useMarker)
        {
            State.RewindIntent.Clear(_plan.PlanDirectory);
        }

        return DriftGateResult.Resolved(DriftDecisions.AutoResolved(
            _plan.Config.AutonomyPolicy, resetTarget, BuildResolvedTasks(plan, drifted, safeSet)));
    }

    /// <summary>
    /// True when the operator-approved plan (<paramref name="auth"/>, captured by the CLI probe) still
    /// matches this run's fresh decision — same reset target and same safe set. A mismatch means files were
    /// edited during the blocking prompt so a rewind now would discard a set the human never saw → the
    /// caller HALTS (issue #274 Part C consent integrity).
    /// </summary>
    private static bool AuthorizationMatches(
        DriftAuthorization auth, HashSet<string> safeSet, SafeSuffixDecision decision)
    {
        if (!string.Equals(auth.ResetTarget, decision.ResetTarget, StringComparison.Ordinal))
        {
            return false;
        }

        if (auth.SafeSet.Count != safeSet.Count)
        {
            return false;
        }

        foreach (string t in auth.SafeSet)
        {
            if (!safeSet.Contains(t))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Replay a surviving rewind-intent marker (issue #274 Part C crash-atomicity): a prior resolution was
    /// killed between the plan-branch rewind and its journal-resets. Idempotently re-reset the whole
    /// recorded set to <c>pending</c> (so a non-drifted descendant whose commit was already discarded
    /// re-runs, never silently skipped) — AND, for a WAVE-scoped rewind (#254 M2b, BLOCKER-1b), re-reset the
    /// recorded wave entries too, so a wave never survives as <c>Completed</c> with a now-dangling
    /// <c>MarkerSha</c> a later <c>reset --hard</c> could resolve SIDEWAYS. Then clear the marker.
    /// Best-effort: a read hiccup leaves the general trailer-reconciliation invariant as the safety net.
    /// </summary>
    private void ReplayRewindIntentIfPresent()
    {
        if (State.RewindIntent.TryRead(_plan.PlanDirectory) is not { } intent)
        {
            return;
        }

        foreach (string taskId in intent.SafeSet)
        {
            _journal.ResetTaskToPending(taskId);
        }

        foreach (string waveDir in intent.Waves)
        {
            _journal.ResetWaveToPending(waveDir);
        }

        State.RewindIntent.Clear(_plan.PlanDirectory);
    }

    /// <summary>
    /// Build the per-task old→new definition-hash audit for a Part C resolution (issue #274): a drifted
    /// task carries the hash pair the drift check already computed; a rebuilt descendant carries its
    /// last-recorded hash (or a sentinel when none) → its current on-disk hash. Emitted in plan order.
    /// </summary>
    private IReadOnlyList<DriftResolvedTask> BuildResolvedTasks(
        PlanDefinition plan, List<DefinitionDriftReporter.DriftInput> drifted, IReadOnlySet<string> safeSet)
    {
        var driftById = drifted.ToDictionary(d => d.TaskId, StringComparer.Ordinal);
        var resolved = new List<DriftResolvedTask>();

        foreach (TaskNode task in plan.Tasks)
        {
            if (!safeSet.Contains(task.Id))
            {
                continue;
            }

            if (driftById.TryGetValue(task.Id, out DefinitionDriftReporter.DriftInput input))
            {
                resolved.Add(new DriftResolvedTask { TaskId = task.Id, OldHash = input.OldHash, NewHash = input.NewHash });
            }
            else
            {
                // A rebuilt descendant that did not itself drift: report its recorded → current hash. This
                // runs AFTER the rewind + journal-reset, so a read failure here must NOT throw (which would
                // abort a run whose branch is already rewound) — the audit degrades to a sentinel instead.
                string oldHash = _journal.RecordedDefinitionHash(task.Id) ?? "(none recorded)";
                string newHash;
                try
                {
                    newHash = Journal.TaskDefinitionHash.Compute(task);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    newHash = "(unreadable)";
                }

                resolved.Add(new DriftResolvedTask { TaskId = task.Id, OldHash = oldHash, NewHash = newHash });
            }
        }

        return resolved;
    }

    /// <summary>
    /// The <see cref="RunAbort"/> for a definition-file read failure during the resume drift pre-pass
    /// (§7.2, #274 Part A): typically a transient file lock (an editor / antivirus / indexer holding a
    /// guardrail or <c>task.json</c>, common on Windows). Distinct from <see cref="BuildAbort"/> so the
    /// remedy is specific — and it makes explicit that the drift check ABORTS rather than silently skips,
    /// so a real definition change can never slip through unseen.
    /// </summary>
    private static RunAbort BuildDefinitionReadAbort(Exception fault) => new()
    {
        Headline = "The run was aborted: a task definition file could not be read during the resume "
                 + $"drift check: {fault.Message}",
        Remedy = "A definition file (task.json / the action / a guardrail) could not be read — often a "
               + "transient file lock (an editor, antivirus, or indexer holding it, common on Windows). "
               + "Release it and re-run. The drift check is aborted rather than skipped, so a real "
               + "definition change can never slip through unseen.",
        Detail = fault.ToString()
    };

    /// <summary>
    /// Build the <see cref="RunAbort"/> for an infrastructure fault (issue #150): a one-line headline
    /// + remedy for the console, and the full exception text for the run logs. A dev tool keeps the
    /// detail — just not as the headline.
    /// </summary>
    private static RunAbort BuildAbort(Exception fault) => new()
    {
        Headline = $"The run was aborted by an unexpected infrastructure fault: {fault.Message}",
        Remedy = "See the full exception in the run logs below. This is a harness/environment fault "
               + "(e.g. an offline or failing git hook on an internal commit, or git unavailable), "
               + "not a task failure — resolve it and re-run to resume.",
        Detail = fault.ToString()
    };

    /// <summary>
    /// plan 08 topology-wiring M2 §D (#126): remove every segment/fork worktree directory owned by a
    /// task that settled GREEN, then prune stale registrations. A green task's work is durable on the
    /// plan branch, so its directory is pure waste — this is the direct #126 fix (a wholly-green run
    /// leaves no segment worktree behind).
    ///
    /// A NON-green task (needs-human / failed / blocked) keeps its directory: the "fix, don't restart"
    /// invariant (§3.2, open-risk #4) requires a failed attempt's worktree to survive so a human — or
    /// a resume's reset-and-retry — can inspect the scoped-revert artifacts and WIP. The next run's
    /// PruneStaleRunBranches pre-pass reclaims those. The <c>_integration</c> worktree is never in
    /// <see cref="RunContext.DirectoryOwner"/> and is therefore never swept.
    ///
    /// Best-effort — a cleanup failure logs (via <see cref="IRunObserver.CleanupFailed"/>) and
    /// continues; it must NEVER flip an otherwise-green run off-green (GitWorktreeProvider.Discard
    /// throws on a non-zero git exit, so each call-site swallows).
    /// </summary>
    private void EndOfRunSweep(
        Dictionary<string, string> directoryOwner, IReadOnlyDictionary<string, TaskResult> settled, IntegrationHandle? integ)
    {
        if (_worktreeProvider is not { } provider || integ is null)
        {
            return;
        }

        // Snapshot under the gate (no workers are running now, but keep the discipline consistent).
        // Sweep only GREEN-owned directories; non-green tasks keep their worktree for fix/resume.
        List<KeyValuePair<string, string>> sweepable;
        lock (_gate)
        {
            sweepable = directoryOwner
                .Where(kv => settled.TryGetValue(kv.Value, out TaskResult? r) && r.IsGreen)
                .ToList();
        }

        foreach ((string path, string owner) in sweepable)
        {
            try
            {
                provider.Discard(new WorktreeHandle { WorktreePath = path });
            }
            catch (Exception ex)
            {
                _observer.CleanupFailed(owner, ex);
            }
            finally
            {
                lock (_gate)
                {
                    directoryOwner.Remove(path);
                }
            }
        }

        try
        {
            provider.PruneOrphans(Array.Empty<string>(), integ);
        }
        catch (Exception ex)
        {
            _observer.CleanupFailed("(prune-orphans)", ex);
        }
    }

    private async Task WorkerLoopAsync(RunContext context, CancellationTokenSource runCts)
    {
        CancellationToken cancellationToken = runCts.Token;
        try
        {
            await foreach (TaskEnvelope envelope in context.Channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                TaskNode task = envelope.Task;
                WorktreeHandle handle = MaterializeForkIfDeferred(context, envelope);

                // Plan 31 §5.2 — poll boundary 1 of 2: task DISPATCH. Placed before the attempt runs, so an
                // edit made while the DAG was busy elsewhere is reported at the first boundary that follows
                // it rather than waiting for something to settle. Under the EXISTING _gate (see
                // RecordPlanEdits); the recording itself is done off-gate.
                IReadOnlyList<PlanEdit>? editsAtDispatch;
                lock (_gate)
                {
                    editsAtDispatch = _planEditWatch?.Poll();
                }

                RecordPlanEdits(editsAtDispatch);

                if (CostCapHaltFor(task) is { } capped)
                {
                    await OnSettledAsync(context, task, capped, handle, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                // #361 Phase 3 (doc 12 §7.6): resume answer-consumption pre-check. BEFORE an escalated unit
                // re-hits its gate, consume any pending firstmate answer for it — record answer-injected, flip
                // the escalation to consumed, and stage the answer text for this re-run's composed prompt — so
                // the reply channel intercepts the #190 outcome-agnostic re-run. Inert (no matching open
                // escalation) on a first run and whenever the dial is not wired.
                if (_escalationSink is not null)
                {
                    ConsumePendingAnswers(task);
                }

                TaskResult result = await _executor.ExecuteAsync(task, handle, cancellationToken).ConfigureAwait(false);

                await OnSettledAsync(context, task, result, handle, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Cancelled drain.
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                _fault ??= ex;
            }

            context.Channel.Writer.TryComplete();
            runCts.Cancel();
        }
    }

    /// <summary>
    /// plan 08 topology-wiring M1 §B: materialize a deferred fork-the-rest sibling's worktree at
    /// dequeue — the actual <c>git worktree add</c> runs HERE, OFF the <see cref="_gate"/> every
    /// settling worker contends for. The fork roots off the producer's RECORDED sha (captured in
    /// the request under <c>_gate</c> at assignment, W-2), never a live rev-parse of the segment
    /// branch the inheritor may have advanced. Returns the envelope's existing handle unchanged
    /// when there is no deferred fork.
    /// </summary>
    private WorktreeHandle MaterializeForkIfDeferred(RunContext context, TaskEnvelope envelope)
    {
        if (envelope.Fork is not { } fork || _worktreeProvider is not { } provider)
        {
            return envelope.Handle;
        }

        // git I/O off the gate.
        WorktreeHandle handle = provider.ForkFromTip(fork.ProducerRecordedSha, envelope.Task.Id, attempt: 1);

        // Bookkeeping under the gate: record the assigned handle + directory ownership.
        lock (_gate)
        {
            context.Handles[envelope.Task.Id] = handle;
            if (!string.IsNullOrEmpty(handle.WorktreePath))
            {
                context.DirectoryOwner[handle.WorktreePath] = envelope.Task.Id;
            }
        }

        return handle;
    }

    private TaskResult? CostCapHaltFor(TaskNode task)
    {
        if (_plan.Config.MaxCostUsd is not { } cap || _journal.CurrentCostUsd() < cap)
        {
            return null;
        }

        return new TaskResult
        {
            TaskId = task.Id,
            Outcome = TaskOutcome.NeedsHuman,
            Summary = $"cost cap reached: cumulative journaled cost has reached the configured " +
                      $"maxCostUsd (${cap}); task not launched."
        };
    }

    // ================================================================================================
    //  #361 Phase 3 — the classify-then-act dispatch at an autonomous gate (doc 12 §4/§7).
    //  Reached ONLY when SchedulerFactory wired the run-level sink/judge/blocker-retry (an `autonomy`
    //  block under `autonomyPolicy: auto`). The construction + injection of those components is task 15's
    //  slice, as is this dispatch; the RESUME answer-consumption path (AnswerFileConsumer) and the
    //  ActionRunner→PromptComposer best-guess/answer INJECTION are task 16; the distinct exit code is task 17.
    // ================================================================================================

    /// <summary>
    /// Map a just-settled task's outcome to a <see cref="GateSignal"/> and dispatch it through
    /// <see cref="ClassifyAndActAsync"/> (doc 12 §4.1). Three task-level stops are dial/forensic-eligible: an
    /// agent-emitted <c>{"needsHuman": "…"}</c> (a class-(a) judgment call, recognised by the settled
    /// <see cref="TaskResult.Summary"/>'s stable <c>needs human: </c> prefix); a rate-limit EXHAUSTION (a
    /// class-(b) transient that never cleared → <see cref="TaskOutcome.RateLimited"/>); and a SUCCEEDED task
    /// that carries a <see cref="TaskResult.ResolvedTransient"/> signal (a class-(b) transient that DID clear
    /// within the pause budget — the executor already resolved it, so this only RECORDS the <c>blocker-retried</c>
    /// forensic entry, never re-runs a wait). Every other outcome (a terminal-exhaustion needs-human, a cost-cap
    /// halt, an overwatcher floor, a plain success) already carries its own shipped handling and is untouched.
    /// </summary>
    private async Task ClassifyTaskGateAsync(TaskNode task, TaskResult result, CancellationToken ct)
    {
        try
        {
            string definitionHash = Journal.TaskDefinitionHash.Compute(task);

            if (result.Outcome == TaskOutcome.NeedsHuman
                && ExtractNeedsHumanQuestion(result.Summary) is { } question)
            {
                await ClassifyAndActAsync(
                    GateSignal.AgentNeedsHuman(question), gate: "needs-human", subject: task.Id, boundary: "task",
                    question: question, definitionHash: definitionHash, criticalityGate: CriticalityGate.NeedsHuman,
                    ct, options: result.NeedsHumanOptions, kind: result.NeedsHumanKind).ConfigureAwait(false);
            }
            else if (result.Outcome == TaskOutcome.RateLimited)
            {
                await ClassifyAndActAsync(
                    GateSignal.PromptFailure(Prompts.PromptFailureKind.Transient), gate: "blocker",
                    subject: task.Id, boundary: "task", question: null, definitionHash: definitionHash,
                    criticalityGate: CriticalityGate.NeedsHuman, ct).ConfigureAwait(false);
            }
            else if (result.Outcome == TaskOutcome.Succeeded && result.ResolvedTransient is { } resolved)
            {
                // A class-(b) transient that RESOLVED WITHIN THE CEILING (doc 12 §4.1/§6.2 `blocker-retried`).
                // The executor's TransientBackoff already re-ran the paused attempt to green — so, unlike the
                // RateLimited branch, this does NOT invoke BlockerRetry's bounded wait again; it only RECORDS
                // the resolved-transient ledger so the forensic trail is non-lossy (§6). Never escalates.
                RecordResolvedTransientBlocker(task.Id, boundary: "task", resolved);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A forensic escalation write must never flip the run's verdict or abort the drain (§6, the
            // don't-fault-on-audit posture) — surface it and continue.
            _observer.CleanupFailed(task.Id, ex);
        }
    }

    /// <summary>
    /// The deterministic classify-then-act core (doc 12 §4): <see cref="GateClassifier.Classify"/> routes the
    /// gate, then — a judgment call (a) → the advisory <see cref="CriticalityJudge"/> → escalate (≥ threshold)
    /// via the injected <see cref="FileEscalationSink"/> or RECORD a proceed-best-guess; a retryable blocker
    /// (b) → <see cref="BlockerRetry"/> bounded wait/backoff; a permanent blocker (c) / floor →
    /// halt-and-escalate. The judge/best-guess INJECTION into the next attempt is task 16 — here a below-
    /// threshold call only RECORDS the best-guess text on the <c>decisions[]</c> entry.
    /// </summary>
    private async Task ClassifyAndActAsync(
        GateSignal signal, string gate, string subject, string boundary, string? question,
        string definitionHash, CriticalityGate criticalityGate, CancellationToken ct,
        IReadOnlyList<string>? options = null, string? kind = null)
    {
        options ??= [];
        switch (GateClassifier.Classify(signal))
        {
            case GateClass.JudgmentCall:
                await ActOnJudgmentCallAsync(gate, subject, boundary, question, definitionHash, criticalityGate, options, kind, ct)
                    .ConfigureAwait(false);
                break;

            case GateClass.HardBlockerRetryable:
                await ActOnRetryableBlockerAsync(gate, subject, boundary, definitionHash, ct).ConfigureAwait(false);
                break;

            default: // HardBlockerPermanent or Floor — no best-guess, no retry clears it: halt-and-escalate.
                EscalateGate(gate, subject, boundary, question, definitionHash, "hard-blocker-permanent",
                    criticality: null, options: options, kind: kind);
                break;
        }
    }

    /// <summary>
    /// A class-(a) judgment call (doc 12 §4 row a / §3.3): run the advisory assessment; at/above the effective
    /// threshold escalate via the sink, else RECORD a proceed-best-guess (the injection into the next attempt
    /// is task 16). A null judge (a script-only plan with no overwatch runner) escalates — invariant 1.
    /// </summary>
    private async Task ActOnJudgmentCallAsync(
        string gate, string subject, string boundary, string? question, string definitionHash,
        CriticalityGate criticalityGate, IReadOnlyList<string> options, string? kind, CancellationToken ct)
    {
        if (_criticalityJudge is null)
        {
            EscalateGate(gate, subject, boundary, question, definitionHash, "judgment-call", criticality: null,
                options: options, kind: kind);
            return;
        }

        CriticalityDecision decision = await _criticalityJudge
            .AssessAsync(new CriticalityGateContext { Gate = criticalityGate, Detail = question ?? "" }, ct)
            .ConfigureAwait(false);

        string threshold = EffectiveThresholdToken(criticalityGate);
        string? criticality = decision.Criticality is { } c ? c.ToString().ToLowerInvariant() : null;
        string? confidence = decision.Confidence is { } cf ? cf.ToString().ToLowerInvariant() : null;

        if (decision.Outcome == CriticalityOutcome.Escalate)
        {
            _escalationSink!.Escalate(new EscalationRequest
            {
                Gate = gate,
                Subject = subject,
                Question = question ?? $"A {gate} judgment call for '{subject}' needs a human answer.",
                Context = BuildGateContext(gate, subject, question),
                Criticality = criticality,
                DefinitionHash = definitionHash,
                At = DateTimeOffset.UtcNow,
                Options = options,
                Kind = kind
            });
            // The sink already appended the 'escalated' decisions[] entry + emitted DecisionRecorded; add the
            // run-level autonomy.jsonl detail line (§6.3).
            AppendAutonomyRecord(gate, boundary, subject, "judgment-call", DecisionTokens.Escalated,
                criticality, confidence, threshold, question, bestGuess: null, decision.Rationale);
            return;
        }

        // Below threshold ⇒ proceed on the recorded best-guess. Task 15 RECORDS it (decisions[] + the best-guess
        // text); task 16 injects that text into the next attempt's composed prompt as delimited UNTRUSTED data.
        var entry = new DecisionEntry
        {
            Boundary = boundary,
            Policy = AutonomyPolicies.Token(AutonomyPolicy.Auto),
            Decision = DecisionTokens.ProceededBestGuess,
            Subject = subject,
            Headline = $"Proceeded on a recorded best-guess at the {gate} gate for '{subject}'"
                       + (criticality is not null ? $" (criticality {criticality} < threshold {threshold})" : ""),
            At = DateTimeOffset.UtcNow,
            Gate = gate,
            Classification = "judgment-call",
            Criticality = criticality,
            Confidence = confidence,
            Threshold = threshold,
            BestGuess = decision.BestGuess
        };
        _journal.RecordDecision(entry);
        _observer.DecisionRecorded(entry);
        AppendAutonomyRecord(gate, boundary, subject, "judgment-call", DecisionTokens.ProceededBestGuess,
            criticality, confidence, threshold, question, decision.BestGuess, decision.Rationale);

        // Reply channel (doc 12 §4.1, §7.4 Finding 4): the recorded best-guess is injected into the NEXT
        // attempt's composed prompt as delimited UNTRUSTED data. Stage it here for the OnSettledAsync re-drive
        // to hand to ActionRunner — a task-level gate (boundary "task") only; a wave-checkpoint best-guess has
        // no per-attempt prompt to inject into (it auto-invokes breakdown instead, §5.1).
        if (boundary == "task" && decision.BestGuess is { Length: > 0 } bestGuessText)
        {
            _pendingBestGuessInjection[subject] = bestGuessText;
        }
    }

    /// <summary>
    /// A class-(b) retryable hard blocker (doc 12 §4 row b / §4.2): the bounded wait/backoff. By the time a
    /// transient reaches this run-level gate the shipped transient-pause discipline (the executor's
    /// <see cref="TransientBackoff"/>) has already elapsed against the pause budget, so the run-level
    /// <see cref="BlockerRetry"/> re-probe treats it as cleared and RECORDS a <c>blocker-retried</c> ledger —
    /// never an immediate escalation (the whole point of class (b) vs class (c)). The live re-run probe the
    /// executor supplies on resume is task 16's concern. On a ceiling it escalates to class (c).
    /// </summary>
    private async Task ActOnRetryableBlockerAsync(
        string gate, string subject, string boundary, string definitionHash, CancellationToken ct)
    {
        BlockerRetryResult retry = await _blockerRetry!
            .RunAsync(hasCleared: _ => true, resetHint: null, ct).ConfigureAwait(false);

        if (retry.Outcome == BlockerRetryOutcome.Escalate)
        {
            EscalateGate(gate, subject, boundary, question: null, definitionHash, "hard-blocker-retryable",
                criticality: null);
            return;
        }

        var entry = new DecisionEntry
        {
            Boundary = boundary,
            Policy = AutonomyPolicies.Token(AutonomyPolicy.Auto),
            Decision = DecisionTokens.BlockerRetried,
            Subject = subject,
            Headline = $"Class-(b) transient at the {gate} gate for '{subject}' cleared after "
                       + $"{retry.Ledger.Attempts} attempt(s)",
            At = DateTimeOffset.UtcNow,
            Gate = gate,
            Classification = "hard-blocker-retryable",
            BlockerAttempts = retry.Ledger.Attempts,
            BlockerWaitedSeconds = (int)retry.Ledger.CumulativeWait.TotalSeconds
        };
        _journal.RecordDecision(entry);
        _observer.DecisionRecorded(entry);
        AppendAutonomyRecord(gate, boundary, subject, "hard-blocker-retryable", DecisionTokens.BlockerRetried,
            criticality: null, confidence: null, threshold: null, question: null, bestGuess: null,
            rationale: null);
    }

    /// <summary>
    /// Record a <c>blocker-retried</c> forensic entry (doc 12 §4.1/§6.2) for a class-(b) transient that PAUSED
    /// and then CLEARED WITHIN the executor's per-task pause budget (surfaced as
    /// <see cref="TaskResult.ResolvedTransient"/> on a <see cref="TaskOutcome.Succeeded"/> settle). This is the
    /// within-budget sibling of <see cref="ActOnRetryableBlockerAsync"/>: the executor already re-ran the paused
    /// attempt to green, so — unlike that path — NO <see cref="BlockerRetry"/> wait is invoked here; only the
    /// ledger (pauses + cumulative wait) is written, matching that path's <see cref="DecisionEntry"/> shape so
    /// the two record identically. Never escalates — a resolved transient is a success (§4.2).
    /// </summary>
    private void RecordResolvedTransientBlocker(string subject, string boundary, ResolvedTransient resolved)
    {
        var entry = new DecisionEntry
        {
            Boundary = boundary,
            Policy = AutonomyPolicies.Token(AutonomyPolicy.Auto),
            Decision = DecisionTokens.BlockerRetried,
            Subject = subject,
            Headline = $"Class-(b) transient for '{subject}' cleared within budget after "
                       + $"{resolved.Pauses} pause(s)",
            At = DateTimeOffset.UtcNow,
            Gate = "blocker",
            Classification = "hard-blocker-retryable",
            BlockerAttempts = resolved.Pauses,
            BlockerWaitedSeconds = (int)resolved.Waited.TotalSeconds
        };
        _journal.RecordDecision(entry);
        _observer.DecisionRecorded(entry);
        AppendAutonomyRecord("blocker", boundary, subject, "hard-blocker-retryable", DecisionTokens.BlockerRetried,
            criticality: null, confidence: null, threshold: null, question: null, bestGuess: null,
            rationale: null);
    }

    /// <summary>
    /// Halt-and-escalate a gate via the injected <see cref="FileEscalationSink"/> (doc 12 §7.2): fire-and-record
    /// (the sink writes the escalations/&lt;seq&gt;-&lt;gate&gt;.json record, appends the 'escalated'
    /// <c>decisions[]</c> entry, and emits <see cref="IRunObserver.DecisionRecorded"/>), then add the run-level
    /// autonomy.jsonl detail line. The task/wave still settles non-green — an escalation records the open
    /// question for an out-of-band answer (task 16's resume), it does not clear the gate.
    /// </summary>
    private void EscalateGate(
        string gate, string subject, string boundary, string? question, string definitionHash,
        string classification, string? criticality, IReadOnlyList<string>? options = null, string? kind = null)
    {
        _escalationSink!.Escalate(new EscalationRequest
        {
            Gate = gate,
            Subject = subject,
            Question = question ?? $"A {gate} blocker for '{subject}' needs a human — no best-guess is available.",
            Context = BuildGateContext(gate, subject, question),
            Criticality = criticality,
            DefinitionHash = definitionHash,
            At = DateTimeOffset.UtcNow,
            Options = options ?? [],
            Kind = kind
        });
        AppendAutonomyRecord(gate, boundary, subject, classification, DecisionTokens.Escalated,
            criticality, confidence: null, threshold: null, question: question, bestGuess: null, rationale: null);
    }

    /// <summary>The full reconstruction context a human/firstmate reads to answer the escalation (doc 12 §7.1).</summary>
    private string BuildGateContext(string gate, string subject, string? question)
    {
        string logs = Path.Combine(_plan.PlanDirectory, "logs");
        string q = question is { Length: > 0 } ? $" Question: {question}." : "";
        return $"Autonomous {gate} gate for '{subject}'.{q} Full logs under {logs}.{DescribePreservedWork(subject)}";
    }

    /// <summary>
    /// What the halting attempt already BUILT, for whoever answers the gate (issue #554, plan 31 §3.3).
    /// The context above tells a human what is WRONG and nothing about what exists — plan 28's attempt-7
    /// escalation enumerated its completed work in detail, none of it was reachable, and the record pointed
    /// at none of it. When the attempt was preserved, both durable copies are named: the git ref (its own
    /// segment worktree is orphaned, so the ref is the only thing that outlives the run) and the readable
    /// patch beside its logs.
    ///
    /// <para>Empty for a gate whose <paramref name="subject"/> is not a task (a wave dir), for a task with
    /// no attempts, and — deliberately — whenever the LAST attempt left no patch: naming an earlier
    /// attempt's ref would answer a question about the halting attempt with someone else's work.</para>
    /// </summary>
    private string DescribePreservedWork(string subject)
    {
        if (_journal is not Journal.RunJournal run
            || run.AttemptsFor(subject) is not { Count: > 0 } attempts)
        {
            return "";
        }

        Journal.AttemptRecord last = attempts.MaxBy(a => a.Attempt)!;
        string patch = Path.GetFullPath(Path.Combine(
            _plan.PlanDirectory, last.LogDir, DependencyContextBuilder.SalvagePatchFileName));

        if (!File.Exists(patch))
        {
            return "";
        }

        // Forward slashes so the path reads the same on every OS, matching the salvage section's own
        // convention (RetryPolicy.AppendSalvageSection).
        return $" Attempt {last.Attempt} wrote work before it stopped, and its in-scope files were preserved: "
             + $"git ref {DependencyContextBuilder.SalvageRefNameFor(subject, last.Attempt)}, "
             + $"readable patch {patch.Replace('\\', '/')}.";
    }

    /// <summary>
    /// The effective escalation threshold token for <paramref name="gate"/> (doc 12 §3.5): a per-gate
    /// <see cref="GateThresholds"/> override when present, else the run-wide dial — the same resolution the
    /// judge applies, recomputed here only to STAMP the forensic record (the judge does not surface it).
    /// </summary>
    private string EffectiveThresholdToken(CriticalityGate gate)
    {
        AutonomyConfig? cfg = _plan.Config.Autonomy;
        if (cfg is null)
        {
            return "";
        }

        EscalationThreshold? perGate = gate switch
        {
            CriticalityGate.NeedsHuman => cfg.GateThresholds?.NeedsHuman,
            CriticalityGate.WaveCheckpoint => cfg.GateThresholds?.WaveCheckpoint,
            _ => null
        };
        return (perGate ?? cfg.EscalationThreshold).ToString().ToLowerInvariant();
    }

    /// <summary>
    /// The agent-emitted needs-human question carried on a settled <see cref="TaskResult.Summary"/> (the
    /// executor stamps <c>needs human: &lt;question&gt;</c> for an agent <c>{"needsHuman": …}</c> short-circuit).
    /// Returns null for any other needs-human summary (a terminal exhaustion, a cost cap) so those are NOT
    /// misclassified as dial-eligible judgment calls.
    /// </summary>
    private static string? ExtractNeedsHumanQuestion(string summary)
    {
        const string prefix = "needs human: ";
        return summary.StartsWith(prefix, StringComparison.Ordinal) ? summary[prefix.Length..] : null;
    }

    /// <summary>
    /// Append one compact <c>autonomy.jsonl</c> detail line for this gate (doc 12 §6.1/§6.3) — the multi-fire
    /// DETAIL behind the durable <c>decisions[]</c> audit. Written only when the journal is the real
    /// <see cref="Journal.RunJournal"/> (a unit-test fake models neither the run id nor the detail stream).
    /// </summary>
    private void AppendAutonomyRecord(
        string gate, string boundary, string subject, string classification, string decision,
        string? criticality, string? confidence, string? threshold, string? question, string? bestGuess,
        string? rationale)
    {
        if (_journal is not Journal.RunJournal runJournal)
        {
            return;
        }

        var record = new Journal.AutonomyRecord
        {
            At = DateTimeOffset.UtcNow,
            Gate = gate,
            Boundary = boundary,
            Subject = subject,
            Classification = classification,
            Decision = decision,
            Criticality = criticality,
            Confidence = confidence,
            Threshold = threshold,
            Question = question,
            BestGuess = bestGuess,
            Rationale = rationale
        };
        Journal.AutonomyJsonl.Append(Path.Combine(_plan.PlanDirectory, "logs"), runJournal.Document.RunId, record);
    }

    // ================================================================================================
    //  #361 Phase 3 — the reply channel (doc 12 §4.1/§7.4/§7.6). Two directions, both threaded through
    //  ActionRunner → PromptComposer.ComposeAction's injectedHumanAnswer section via a per-task
    //  injected-human-answer.txt handoff: (1) a below-threshold best-guess re-driven into the NEXT attempt,
    //  and (2) a resume consuming a firstmate answer BEFORE the escalated unit re-hits its gate.
    // ================================================================================================

    /// <summary>
    /// Reply channel direction 1 (doc 12 §4.1): when the just-settled task recorded a below-threshold
    /// best-guess (staged in <see cref="_pendingBestGuessInjection"/>), re-drive ONE bounded attempt with the
    /// best-guess injected into the composed prompt (via <see cref="ActionRunner"/> →
    /// <see cref="Prompts.PromptComposer.ComposeAction"/>'s <c>injectedHumanAnswer</c> channel, delimited
    /// UNTRUSTED data). The executor terminates a needs-human short-circuit WITHOUT retrying, so the injection
    /// is OBSERVABLE only if the Scheduler re-runs the unit — this is that re-run. Driven DIRECTLY (never
    /// through the worker loop) so it does not re-enter the classify-then-act dispatch.
    /// <para>
    /// <b>Returns the re-driven result (#550), and the caller adopts it ONLY if it is green.</b> This method
    /// used to discard it and document that as intent — "a pure side-effect; the ORIGINAL settle stands" —
    /// which silently threw away attempts that had run the action and passed every guardrail. Returning the
    /// result does not weaken §6's "never faults the run": a failed or faulted re-drive still returns
    /// something the caller ignores (null on fault, non-green otherwise), so the reply channel still cannot
    /// turn a passing task into a failing one. It can now only do the opposite.
    /// </para>
    /// </summary>
    private async Task<TaskResult?> RerunForBestGuessInjectionIfPendingAsync(
        TaskNode task, WorktreeHandle handle, CancellationToken ct)
    {
        if (!_pendingBestGuessInjection.TryRemove(task.Id, out string? bestGuess))
        {
            return null;
        }

        try
        {
            WriteInjectionFile(task.Id, bestGuess);
            return await _executor.ExecuteAsync(task, handle, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A faulted re-drive is still never allowed to fault the run — swallow it and report nothing
            // adoptable, exactly as before.
            _observer.CleanupFailed(task.Id, ex);
            return null;
        }
    }

    /// <summary>
    /// Reply channel direction 2 (doc 12 §7.6): for a unit about to re-hit an escalated gate, find its OPEN
    /// answerable escalation(s) in the CREATING run's <c>escalations/</c> dir (anchored on the STABLE journal
    /// runId, §7.1 — the same anchor <see cref="FileEscalationSink"/> wrote to, unchanged across resume) and
    /// try to consume a pending firstmate answer via <see cref="AnswerFileConsumer"/>. On a valid answer:
    /// record the <see cref="DecisionTokens.AnswerInjected"/> decision (the consumer already flipped the
    /// record's <c>status</c> to <c>consumed</c>, CAS-guarded) and stage the answer text for this re-run's
    /// composed prompt. A missing/rejected answer is left to re-escalate through the normal gate path. Inert
    /// when the journal is not a real <see cref="Journal.RunJournal"/> (a unit-test fake).
    /// </summary>
    private void ConsumePendingAnswers(TaskNode task)
    {
        if (_journal is not Journal.RunJournal runJournal)
        {
            return;
        }

        string escDir = Path.Combine(_plan.PlanDirectory, "logs", runJournal.Document.RunId, "escalations");
        if (!Directory.Exists(escDir))
        {
            return;
        }

        string currentHash = Journal.TaskDefinitionHash.Compute(task);
        var consumer = new AnswerFileConsumer(escDir);

        // Whether this run is under the review-gate proceed-unreviewed opt-in — the SAME expression the JIT
        // checkpoint reads (see RunBreakdownAsync). Passed to Consume so its step-7 clamp actually fires in
        // production: under proceed-unreviewed a clamped high/critical hard call is NON-ANSWERABLE and must
        // stay escalated — a firstmate answer file can never auto-clear it (doc 12 §5.2/§7.3, Blocker 1, #375).
        bool proceedUnreviewed =
            _plan.Config.Autonomy?.GateThresholds?.ReviewGate == ReviewGateDecision.ProceedUnreviewed;

        foreach (string recordPath in Directory.EnumerateFiles(escDir, "*.json"))
        {
            if (recordPath.EndsWith(".answer.json", StringComparison.Ordinal))
            {
                continue; // the reply file, not an escalation record
            }

            (int? seq, string? gate, string? subject, string? status) = ReadEscalationBinding(recordPath);
            // Only THIS unit's still-open, answerable (needs-human — a task-level gate) escalations bind here.
            if (seq is not { } s || gate != "needs-human" || subject != task.Id
                || string.Equals(status, "consumed", StringComparison.Ordinal))
            {
                continue;
            }

            AnswerConsumptionResult consumed = consumer.Consume(s, gate, currentHash, proceedUnreviewed);
            if (consumed.Outcome != AnswerOutcome.Injected || consumed.Decision is null)
            {
                continue; // no / rejected answer ⇒ re-escalate through the normal gate path (§7.6.4)
            }

            _journal.RecordDecision(consumed.Decision);
            _observer.DecisionRecorded(consumed.Decision);
            AppendAutonomyRecord(gate, consumed.Decision.Boundary, task.Id, "judgment-call",
                DecisionTokens.AnswerInjected, criticality: null, confidence: null, threshold: null,
                question: null, bestGuess: null, rationale: consumed.Decision.Detail);

            // Stage the human answer text for the re-run's composed prompt (§7.4 needs-human injection).
            if (TryReadAnswerText(escDir, s, gate) is { Length: > 0 } answerText)
            {
                WriteInjectionFile(task.Id, answerText);
            }
        }
    }

    /// <summary>
    /// Resume-time consumption of a firstmate <c>wave-proceed</c> answer for THIS wave's <c>wave-checkpoint</c>
    /// escalation (doc 12 §7.4, issue #375) — the wave-boundary sibling of <see cref="ConsumePendingAnswers"/>.
    /// Scans the creating-run's <c>escalations/</c> dir for a still-OPEN <c>wave-checkpoint</c> escalation whose
    /// <c>subject</c> is <paramref name="wave"/>.Dir, then <see cref="AnswerFileConsumer.Consume"/>s it against
    /// the wave's CURRENT <see cref="Journal.WaveDefinitionHash"/> and the SAME <c>proceed-unreviewed</c> flag
    /// the task path passes — so a clamped high/critical wave-checkpoint stays NON-answerable (§5.2/§7.3
    /// Blocker 1). Tri-state: a valid <c>proceed</c> ⇒ <see cref="WaveProceedConsumeResult.Proceed"/> (the
    /// caller breaks down + runs the wave); a valid <c>hold</c> ⇒ <see cref="WaveProceedConsumeResult.Hold"/>
    /// (the human's decision is recorded and the caller DEFINITIVELY honest-halts — no re-classify); a rejected
    /// (incl. the clamp) / absent answer ⇒ <see cref="WaveProceedConsumeResult.None"/> (the caller re-poses the
    /// gate). Inert (<see cref="WaveProceedConsumeResult.None"/>) when the journal is not a real
    /// <see cref="Journal.RunJournal"/> (a unit-test fake).
    /// </summary>
    private WaveProceedConsumeResult TryConsumeWaveProceed(WaveNode wave)
    {
        if (_journal is not Journal.RunJournal runJournal)
        {
            return WaveProceedConsumeResult.None;
        }

        string escDir = Path.Combine(_plan.PlanDirectory, "logs", runJournal.Document.RunId, "escalations");
        if (!Directory.Exists(escDir))
        {
            return WaveProceedConsumeResult.None;
        }

        // The SAME proceed-unreviewed expression the task path computes (see ConsumePendingAnswers) — the clamp
        // must apply identically at the wave boundary.
        bool proceedUnreviewed =
            _plan.Config.Autonomy?.GateThresholds?.ReviewGate == ReviewGateDecision.ProceedUnreviewed;
        string currentHash = Journal.WaveDefinitionHash.Compute(wave);
        var consumer = new AnswerFileConsumer(escDir);

        foreach (string recordPath in Directory.EnumerateFiles(escDir, "*.json"))
        {
            if (recordPath.EndsWith(".answer.json", StringComparison.Ordinal))
            {
                continue; // the reply file, not an escalation record
            }

            (int? seq, string? gate, string? subject, string? status) = ReadEscalationBinding(recordPath);
            // Only THIS wave's still-open wave-checkpoint escalation binds here.
            if (seq is not { } s || gate != "wave-checkpoint" || subject != wave.Dir
                || string.Equals(status, "consumed", StringComparison.Ordinal))
            {
                continue;
            }

            AnswerConsumptionResult consumed = consumer.Consume(s, gate, currentHash, proceedUnreviewed);
            if (consumed.Outcome != AnswerOutcome.Injected || consumed.Decision is null)
            {
                continue; // rejected (incl. the clamp) / absent ⇒ None: re-pose the gate through classify-then-act
            }

            // A valid wave-proceed answer was consumed once (status flipped). Record the decision for the audit
            // (a `hold` still records the human chose to wait, §7.4), then map proceed→Proceed, hold→Hold.
            _journal.RecordDecision(consumed.Decision);
            _observer.DecisionRecorded(consumed.Decision);
            AppendAutonomyRecord("wave-checkpoint", consumed.Decision.Boundary, wave.Dir, "judgment-call",
                DecisionTokens.AnswerInjected, criticality: null, confidence: null, threshold: null,
                question: null, bestGuess: null, rationale: consumed.Decision.Detail);

            return string.Equals(consumed.WaveDecision, WaveProceedDecisions.Proceed, StringComparison.Ordinal)
                ? WaveProceedConsumeResult.Proceed
                : WaveProceedConsumeResult.Hold;
        }

        return WaveProceedConsumeResult.None;
    }

    /// <summary>
    /// The tri-state outcome of <see cref="TryConsumeWaveProceed"/> at the JIT wave checkpoint (doc 12 §7.4,
    /// issue #375). <see cref="Hold"/> is DISTINCT from <see cref="None"/> precisely so a human's "wait" forces a
    /// definitive honest-halt instead of re-entering the classify-then-act re-assessment (which could
    /// best-guess-and-proceed and override the hold).
    /// </summary>
    private enum WaveProceedConsumeResult
    {
        /// <summary>No bindable answer (none present, or rejected — including the proceed-unreviewed clamp): re-pose the gate through classify-then-act.</summary>
        None,

        /// <summary>A valid <c>wave-proceed: proceed</c> answer was consumed: break down + run the wave.</summary>
        Proceed,

        /// <summary>A valid <c>wave-proceed: hold</c> answer was consumed: the human chose to wait — honest-halt DEFINITIVELY, no re-classify, no new escalation.</summary>
        Hold
    }

    /// <summary>
    /// Write the per-task injected-human-answer file the next attempt's <see cref="ActionRunner"/> reads (and
    /// consumes once). The raw <paramref name="text"/> is wrapped into the delimited UNTRUSTED envelope by
    /// <see cref="Prompts.PromptComposer.ComposeAction"/> (§7.4 Finding 4). Lives at the TASK log level
    /// (<c>logs/&lt;runId&gt;/&lt;taskId&gt;/</c>, the stable journal runId) so it is found regardless of the
    /// next attempt number — the filename literal mirrors <see cref="ActionRunner"/>'s reader.
    /// </summary>
    private void WriteInjectionFile(string taskId, string text)
    {
        if (_journal is not Journal.RunJournal runJournal)
        {
            return;
        }

        string path = Path.Combine(
            _plan.PlanDirectory, "logs", runJournal.Document.RunId, taskId, "injected-human-answer.txt");
        AtomicFile.WriteAllText(path, text);
    }

    /// <summary>Read an escalation record's binding fields (seq/gate/subject/status), tolerating either a top-level or nested <c>id</c> shape; all null on a missing/corrupt record.</summary>
    private static (int? Seq, string? Gate, string? Subject, string? Status) ReadEscalationBinding(string recordPath)
    {
        try
        {
            if (JsonNode.Parse(File.ReadAllText(recordPath)) is not JsonObject record)
            {
                return (null, null, null, null);
            }

            JsonObject? id = record["id"] as JsonObject;
            int? seq = NodeInt(id?["seq"]) ?? NodeInt(record["seq"]);
            string? gate = NodeString(record["gate"]) ?? NodeString(id?["gate"]);
            string? subject = NodeString(record["subject"]) ?? NodeString(id?["subject"]);
            string? status = NodeString(record["status"]);
            return (seq, gate, subject, status);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return (null, null, null, null);
        }
    }

    /// <summary>Read the raw <c>answer.text</c> from a firstmate <c>…answer.json</c> beside an escalation, or null when absent/malformed.</summary>
    private static string? TryReadAnswerText(string escDir, int seq, string gate)
    {
        try
        {
            string answerPath = Path.Combine(escDir, $"{seq}-{gate}.answer.json");
            if (!File.Exists(answerPath))
            {
                return null;
            }

            return JsonNode.Parse(File.ReadAllText(answerPath)) is JsonObject answer
                   && answer["answer"] is JsonObject payload
                ? NodeString(payload["text"])
                : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return null;
        }
    }

    private static int? NodeInt(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue(out int i) ? i : null;

    private static string? NodeString(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue(out string? s) ? s : null;

    /// <summary>
    /// Called after a task finishes (executor or cost-cap halt). For worktree-mode green results,
    /// performs the B1 deferred settle (fragment merge → git integration commit → journal settle)
    /// under <see cref="_integrationLock"/> BEFORE updating the shared run context under
    /// <see cref="_gate"/>. This ordering ensures dependents only become ready after the upstream
    /// integration has advanced the plan branch, making lazy handle creation FF-compatible.
    /// </summary>
    /// <summary>
    /// The green-settle half of <see cref="OnSettledAsync"/>, extracted (#550) so that a result adopted
    /// LATER in the same settle — a best-guess re-drive that passed — travels the identical path instead of
    /// a parallel one that could drift from it. Returns the POST-settle result: <see cref="SettleAsync"/>
    /// can still turn a green-looking result into needs-human on a failed union re-verify, so callers must
    /// use the returned value rather than the one they passed in. Inert for a non-green result, in serial
    /// mode, and with no integration context.
    /// </summary>
    private async Task<TaskResult> SettleGreenIfWorktreeAsync(
        RunContext context, TaskNode task, TaskResult result, WorktreeHandle handle, CancellationToken ct)
    {
        // B1 deferred settle (worktree mode, real segment): ValidateFragmentForSettle sets
        // DeferredSettle=true, meaning the Scheduler owns the fragment merge → git commit →
        // journal settle sequence under the integration lock.
        //
        // Old path (serial mode or fake provider): the executor already merged + journaled;
        // just call provider.Integrate directly so IWorktreeProvider.IntegrateCallCount tests pass.
        if (result.IsGreen && _worktreeProvider is { } provider && context.Integ is { } integ)
        {
            if (result.DeferredSettle)
            {
                await _integrationLock.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    result = await SettleAsync(task, result, handle, provider, integ, ct).ConfigureAwait(false);
                }
                finally
                {
                    _integrationLock.Release();
                }
            }
            else if (!string.IsNullOrEmpty(handle.WorktreePath))
            {
                // Non-deferred: executor already handled journal; just integrate the segment. Stamp the
                // definition hash onto the handle so the integration commit still carries the
                // Guardrails-Task-Hash: trailer (§7.2) — the executor already recorded the journal hash.
                handle.DefinitionHash = Journal.TaskDefinitionHash.Compute(task);
                provider.Integrate(handle, integ, CancellationToken.None);
            }

            // #195 retry-salvage pruning (deliverable 6): once a task's FINAL settle (after the
            // deferred B1 settle above, which can still turn a green-looking result into NeedsHuman on
            // a failed union re-verify) is truly Succeeded, its salvage refs — its own prior
            // rolled-back partial attempts — have served their purpose and are pruned so they never
            // accumulate across a long-lived repo. Checked on result.Outcome (the POST-settle value),
            // never result.IsGreen (which is true before SettleAsync can still flip it to NeedsHuman).
            if (result.Outcome == TaskOutcome.Succeeded)
            {
                try { provider.PruneSalvageRefs(task.Id); }
                catch (Exception ex) { _observer.CleanupFailed(task.Id, ex); }
            }
        }

        return result;
    }

    private async Task OnSettledAsync(
        RunContext context, TaskNode task, TaskResult result, WorktreeHandle handle, CancellationToken ct)
    {
        // B1 deferred settle (worktree mode, real segment): ValidateFragmentForSettle sets
        // DeferredSettle=true, meaning the Scheduler owns the fragment merge → git commit →
        // journal settle sequence under the integration lock.
        //
        // Old path (serial mode or fake provider): the executor already merged + journaled;
        // just call provider.Integrate directly so IWorktreeProvider.IntegrateCallCount tests pass.
        result = await SettleGreenIfWorktreeAsync(context, task, result, handle, ct).ConfigureAwait(false);

        // #361 Phase 3 (doc 12 §4): classify-then-act at a task-level gate when the autonomy dial is wired. A
        // non-green needs-human / rate-limit stop is deterministically classified and acted on (escalate /
        // proceed-best-guess / bounded class-(b) retry) as the task settles — independent branches keep
        // draining (this runs per settling task, OFF the run barrier). Inert when the dial is not wired.
        if (_escalationSink is not null)
        {
            await ClassifyTaskGateAsync(task, result, ct).ConfigureAwait(false);
            // Reply channel part 2 (doc 12 §4.1): if the classify step recorded a below-threshold best-guess,
            // re-drive ONE bounded attempt with it injected — the executor short-circuits a needs-human without
            // retrying, so this is the only place the injection becomes OBSERVABLE in a composed prompt.
            TaskResult? reDriven =
                await RerunForBestGuessInjectionIfPendingAsync(task, handle, ct).ConfigureAwait(false);

            // #550: HONOR a re-driven attempt that passed. This used to discard the result — "the ORIGINAL
            // settle stands" — which meant an attempt that ran the action AND passed every guardrail was
            // reported `needs human` with the PREVIOUS attempt's reason, was never journaled at all (in
            // worktree mode the succeeded record is written by the settle below, not by the executor, so
            // bypassing the settle drops it entirely), left the task on the non-terminal `running` status,
            // and blocked its dependents with "dependency did not succeed" — about a task that had
            // succeeded. Observed on plan 28's task 20, whose 4th attempt built clean and produced the exact
            // designed red bar while the run reported a permission wall from attempt 3.
            //
            // ASYMMETRIC, deliberately, which is what keeps §6's "never faults the run" intact: only a GREEN
            // re-drive is adopted. A re-driven attempt that fails changes nothing — the original settle
            // stands, no extra retry is burned, and the reply channel still cannot turn a passing task into a
            // failing one. What it can now do is let a passing task be recorded as passing, which is the
            // whole point of proceeding on a best-guess: the guardrails, not the injection, are what
            // certify the work.
            //
            // Routed through the SAME SettleGreenIfWorktreeAsync as any other green result rather than a
            // second settle path — a parallel one would be free to disagree with the first, and this bug was
            // born from exactly that kind of bypass.
            if (reDriven is { IsGreen: true })
            {
                result = await SettleGreenIfWorktreeAsync(context, task, reDriven, handle, ct)
                    .ConfigureAwait(false);
            }
        }

        var newlyReady = new List<TaskEnvelope>();
        var newlyBlocked = new List<TaskResult>();

        lock (_gate)
        {
            context.Settled[task.Id] = result;
            context.Remaining--;

            if (result.IsGreen)
            {
                // Which dependents of this producer just had their LAST pending dep cleared?
                // (A multi-producer dependent here is a fan-in: it has other, already-green
                // producers, and reaches the merged plan tip — never reused, M1 §A1.)
                var justReady = new List<string>();
                foreach (string dependent in context.Graph.DependentsOf(task.Id))
                {
                    if (!context.Settled.ContainsKey(dependent) && --context.PendingDeps[dependent] == 0)
                    {
                        justReady.Add(dependent);
                    }
                }

                AssignDependentHandles(context, task, justReady, newlyReady);
            }
            else if (result.Outcome != TaskOutcome.Cancelled)
            {
                foreach (string dependent in context.Graph.TransitiveDependentsOf(task.Id)
                             .OrderBy(d => d, StringComparer.Ordinal))
                {
                    if (context.Settled.ContainsKey(dependent))
                    {
                        continue;
                    }

                    var blocked = new TaskResult
                    {
                        TaskId = dependent,
                        Outcome = TaskOutcome.Blocked,
                        Summary = $"blocked: dependency '{task.Id}' did not succeed"
                    };
                    context.Settled[dependent] = blocked;
                    context.Remaining--;
                    _journal.MarkBlocked(dependent);
                    newlyBlocked.Add(blocked);
                }

                // M2 §C / open-risk #4: a permanently-failed (needs-human/failed) task's segment is
                // NOT Discarded mid-run. The "fix, don't restart" invariant (§3.2) keeps a failed
                // attempt's worktree alive so a human (or a retry) can inspect the scoped-revert
                // artifacts and WIP. Its directory stays owned in DirectoryOwner and is reclaimed by
                // the end-of-run sweep at quiescence — which is what closes #126 (design §D point 2:
                // "#126 is closed by the run-end sweep alone"). Cancellation skips the sweep, so a
                // cancelled task's worktree survives for the resume prune (T-11).
            }

            if (context.Remaining == 0)
            {
                context.Channel.Writer.TryComplete();
            }
        }

        _observer.TaskFinished(result);
        foreach (TaskResult blocked in newlyBlocked)
        {
            _observer.TaskFinished(blocked);
        }

        // Each envelope already carries its assigned handle (fresh segment / reused directory) OR a
        // deferred fork request the worker materializes off-gate at dequeue (M1 §B).
        foreach (TaskEnvelope ready in newlyReady)
        {
            context.Channel.Writer.TryWrite(ready);
        }

        // Plan 31 §5.2 — poll boundary 2 of 2: task SETTLE. This is the boundary that catches an edit made
        // BY a task's own action (or by an operator during it), and the last boundary a run reaches, so a
        // late edit is still reported before the end-of-run advisory is composed.
        IReadOnlyList<PlanEdit>? editsAtSettle;
        lock (_gate)
        {
            editsAtSettle = _planEditWatch?.Poll();
        }

        RecordPlanEdits(editsAtSettle);
    }

    /// <summary>
    /// plan 08 topology-wiring M1 §A/§B: assign worktree handles to the dependents of a just-settled
    /// green producer <paramref name="producer"/>, choosing reuse vs fork vs fresh-segment.
    /// <list type="bullet">
    ///   <item><b>Multi-producer dependents (fan-in)</b> get a fresh <see cref="IWorktreeProvider.CreateSegment"/>
    ///     off the plan-branch tip, which already contains every producer's integrated work — never
    ///     reused (§A1).</item>
    ///   <item><b>Single-producer dependents</b> are the inherit-one/fork-rest fan-out. The inheritor
    ///     (longest downstream chain via <see cref="DependencyGraph.TransitiveDependentsOf"/>, ordinal-id
    ///     tiebreak) reuses the producer's segment directory via the pure-handle
    ///     <see cref="IWorktreeProvider.ReuseSegment"/> (safe under <see cref="_gate"/>; ownership
    ///     transfers to the inheritor). The rest fork off the producer's RECORDED sha — a DEFERRED
    ///     request the worker materializes off-gate (§B, W-2).</item>
    /// </list>
    /// Runs under <see cref="_gate"/>. All assignment + bookkeeping is here; only the fork's
    /// <c>git worktree add</c> is deferred off-gate.
    /// </summary>
    private void AssignDependentHandles(
        RunContext context, TaskNode producer, List<string> justReady, List<TaskEnvelope> newlyReady)
    {
        // Fan-in (multi-producer) dependents reach the merged plan tip with a fresh segment.
        var singleProducer = new List<string>();
        foreach (string dependent in justReady)
        {
            if (context.ById[dependent].DependsOn.Count > 1)
            {
                WorktreeHandle fanInHandle = CreateFreshSegment(context, dependent);
                context.Handles[dependent] = fanInHandle;
                RecordOwnership(context, fanInHandle, dependent);
                newlyReady.Add(new TaskEnvelope(context.ById[dependent], fanInHandle));
            }
            else
            {
                singleProducer.Add(dependent);
            }
        }

        if (singleProducer.Count == 0)
        {
            return;
        }

        // Inherit-one: the single-producer dependent with the longest downstream chain reuses the
        // producer's directory; ordinal-id tiebreak. The producer's handle carries the RecordedSha
        // that Integrate captured during this settle (strict happens-before).
        string inheritor = singleProducer
            .OrderByDescending(d => context.Graph.TransitiveDependentsOf(d).Count)
            .ThenBy(d => d, StringComparer.Ordinal)
            .First();

        WorktreeHandle? producerHandle =
            _worktreeProvider != null ? context.Handles.GetValueOrDefault(producer.Id) : null;

        foreach (string dependent in singleProducer)
        {
            if (dependent == inheritor && _worktreeProvider is { } reuseProvider && producerHandle is { } ph)
            {
                // Pure handle rewrite — no git, safe under _gate. Ownership of the producer's
                // directory transfers to the inheritor.
                WorktreeHandle reused = reuseProvider.ReuseSegment(ph, dependent, attempt: 1);
                context.Handles[dependent] = reused;
                if (!string.IsNullOrEmpty(reused.WorktreePath))
                {
                    context.DirectoryOwner[reused.WorktreePath] = dependent;
                }
                newlyReady.Add(new TaskEnvelope(context.ById[dependent], reused));
            }
            else if (_worktreeProvider is not null && producerHandle is { } pf)
            {
                // Fork-the-rest: defer the git worktree add to the worker (off-gate). Root off the
                // producer's RECORDED sha — never the live segment-branch tip the inheritor advances.
                var fork = new ForkRequest(pf.RecordedCommitSha);
                newlyReady.Add(new TaskEnvelope(context.ById[dependent], new WorktreeHandle(), fork));
            }
            else
            {
                // No provider (serial/fake-less mode): an empty placeholder handle, as before.
                var placeholder = new WorktreeHandle();
                context.Handles[dependent] = placeholder;
                newlyReady.Add(new TaskEnvelope(context.ById[dependent], placeholder));
            }
        }
    }

    /// <summary>Create a fresh segment off the plan-branch tip (or an empty handle without a provider).</summary>
    private WorktreeHandle CreateFreshSegment(RunContext context, string taskId) =>
        _worktreeProvider != null && context.Integ != null
            ? _worktreeProvider.CreateSegment(taskId, attempt: 1, context.Integ, CancellationToken.None)
            : new WorktreeHandle();

    /// <summary>Record directory ownership for a non-empty handle path (M0 bookkeeping; under <see cref="_gate"/>).</summary>
    private static void RecordOwnership(RunContext context, WorktreeHandle handle, string taskId)
    {
        if (!string.IsNullOrEmpty(handle.WorktreePath))
        {
            context.DirectoryOwner[handle.WorktreePath] = taskId;
        }
    }

    /// <summary>
    /// B1 fixed-order settle under <see cref="_integrationLock"/>:
    /// (1) deep-merge fragment into state.json,
    /// (2) git integration commit (FF or non-FF merge),
    /// (3) reserve mergeSequence + journal RecordSettle.
    /// On non-FF failure: restore state.json, reset integration worktree, journal NeedsHuman.
    /// </summary>
    private async Task<TaskResult> SettleAsync(
        TaskNode task,
        TaskResult result,
        WorktreeHandle handle,
        IWorktreeProvider provider,
        IntegrationHandle integ,
        CancellationToken ct)
    {
        string statePath = Path.Combine(_plan.PlanDirectory, "state", "state.json");
        string preMergeState = File.Exists(statePath) ? File.ReadAllText(statePath) : "{}";

        // §7.2 (#274 Part A): the task's definition hash, stamped onto BOTH the integration commit's
        // Guardrails-Task-Hash: trailer (via the handle for FF, the CommitStagedMerge param for non-FF)
        // AND the journal entry (RecordSucceededSettle) — computed once, under the integration lock,
        // from the current on-disk definition. This is what a later resume compares against.
        string definitionHash = Journal.TaskDefinitionHash.Compute(task);
        handle.DefinitionHash = definitionHash;

        // B1 step 1: merge fragment into state.json BEFORE the git commit.
        if (result.FragmentPath is { } fp && File.Exists(fp))
        {
            MergeFragmentIntoState(statePath, preMergeState, fp);
        }

        // B1 step 2: git integration commit (FF or non-FF union).
        IntegrationResult integResult = provider.Integrate(handle, integ, ct);

        if (integResult == IntegrationResult.FastForward)
        {
            // FF is free — no re-verify needed. Consume one merge sequence.
            long seq = _journal.ReserveMergeSequence();
            RecordSucceededSettle(task, result, seq, definitionHash);
            return result;
        }

        if (integResult == IntegrationResult.Conflict)
        {
            // AI merge worker resolves the conflict (§9.1). If no worker is wired or all
            // attempts fail, escalate to needs-human with a full B1 rollback.
            bool aiResolved = _aiMergeWorker != null
                && await _aiMergeWorker.TryResolveAsync(
                    integ.IntegrationWorktreePath,
                    handle.SegmentBranchName,
                    _plan.PlanDirectory,
                    _journal,
                    ct).ConfigureAwait(false);

            // #451 POST-CONDITION: a resolver that returns TRUE is not trusted on its word. The one
            // fact that must hold before B2 is that the index carries NO unmerged path — anything else
            // makes the `git commit` below exit 128 ("Committing is not possible because you have
            // unmerged files") from inside a try{} that classifies every git failure as an
            // INFRASTRUCTURE FAULT, aborting the whole run and stranding every already-settled task's
            // work. A half-resolved merge is a KNOWN state with a designed handler (B1 rollback →
            // needs-human), so demote it to that handler here rather than letting it reach the commit.
            // Free for fake/serial providers: the default UnmergedPaths is empty.
            IReadOnlyList<string> unmergedAfterAi = aiResolved ? provider.UnmergedPaths(integ) : [];
            if (aiResolved && unmergedAfterAi.Count > 0)
            {
                aiResolved = false;
            }

            if (!aiResolved)
            {
                AtomicFile.WriteAllText(statePath, preMergeState);
                provider.RollbackMerge(integ, ct);
                _journal.RecordSettle(task.Id, JournalTaskStatus.NeedsHuman, null);
                return new TaskResult
                {
                    TaskId = task.Id,
                    Outcome = TaskOutcome.NeedsHuman,
                    ActionExitCode = result.ActionExitCode,
                    Guardrails = result.Guardrails,
                    Summary = unmergedAfterAi.Count > 0
                        ? "AI merge reported success but left unmerged path(s): "
                          + string.Join(", ", unmergedAfterAi) + "; rolled back, needs human"
                        : "merge conflict could not be AI-resolved; needs human"
                };
            }

            // AI merge succeeded: re-verify the merged bytes against the SAME integration
            // set as the non-AI-merge union path below (§4.3, v1 contract). Running the FULL
            // per-task set here false-fails by construction — it includes per-attempt
            // anti-tautology guardrails (tests-fail-on-current-code, which PASS post-merge),
            // scaffold-state and state-fragment-present checks (no action fragment exists at a
            // union point), and downstream tasks that have not run yet. The B-3 "AI may drop a
            // colliding sibling's hunk" concern is covered by the integration-scope union
            // guardrails (a well-authored integration/union-verify guardrail catches a dropped
            // hunk), the disjoint-scope CHECK, and the terminal integration gate — not by
            // re-running the full per-task set (which would be inconsistent with the union path).
            IReadOnlyList<GuardrailDefinition> aiIntegGuardrails = UnionIntegrationSet(_plan);

            ReVerifyResult aiReVerify = _reVerifier != null
                ? await _reVerifier.ReVerifyAsync(integ.IntegrationWorktreePath, aiIntegGuardrails, ct).ConfigureAwait(false)
                : new ReVerifyResult { Passed = true };

            if (aiReVerify.Passed)
            {
                // B2: commit the AI-resolved staged merge with the task trailer (incl. the §7.2
                // Guardrails-Task-Hash: line) BEFORE journaling.
                provider.CommitStagedMerge(integ, task.Id, ct, definitionHash);
                long seq = _journal.ReserveMergeSequence();
                RecordSucceededSettle(task, result, seq, definitionHash);
                return result;
            }

            // Re-verify failed after AI merge: B1 four-effect rollback.
            // #188: persist the failing integration guardrails' output + a feedback.md to the task log
            // dir BEFORE the rollback discards the merged bytes, so a human has the WHY on disk.
            string aiFeedbackPath = PersistUnionReVerifyFailure(task, result, integ, aiReVerify, aiMerge: true);
            AtomicFile.WriteAllText(statePath, preMergeState);
            provider.RollbackMerge(integ, ct);
            _journal.RecordSettle(task.Id, JournalTaskStatus.NeedsHuman, null);
            return new TaskResult
            {
                TaskId = task.Id,
                Outcome = TaskOutcome.NeedsHuman,
                ActionExitCode = result.ActionExitCode,
                Guardrails = result.Guardrails,
                Summary = "AI-merge resolution failed integration re-verify; needs human " +
                          $"(see {aiFeedbackPath})"
            };
        }

        // Non-FF union: re-verify the merged bytes in the integration worktree.
        IReadOnlyList<GuardrailDefinition> integGuardrails = UnionIntegrationSet(_plan);

        ReVerifyResult reVerify = _reVerifier != null
            ? await _reVerifier.ReVerifyAsync(integ.IntegrationWorktreePath, integGuardrails, ct).ConfigureAwait(false)
            : new ReVerifyResult { Passed = true };

        if (reVerify.Passed)
        {
            // B2 step 2: commit the staged non-FF union with the task trailer BEFORE journaling,
            // so the plan branch carries this task's Guardrails-Task: trailer (the FF path commits
            // implicitly via the FF move; the non-FF path must commit the staged merge explicitly). The
            // §7.2 Guardrails-Task-Hash: line rides along on the same commit.
            provider.CommitStagedMerge(integ, task.Id, ct, definitionHash);
            long seq = _journal.ReserveMergeSequence();
            RecordSucceededSettle(task, result, seq, definitionHash);
            return result;
        }

        // #188: persist the failing integration guardrails' output + a feedback.md to the task log dir
        // BEFORE the four-effect rollback discards the merged bytes — otherwise the needs-human summary
        // points at a feedback.md that was never written and the failing guardrail output is lost.
        string feedbackPath = PersistUnionReVerifyFailure(task, result, integ, reVerify, aiMerge: false);

        // B1 four-effect rollback:
        // 1. Restore state.json (undo fragment merge).
        AtomicFile.WriteAllText(statePath, preMergeState);
        // 2. Reset integration worktree to pre-merge HEAD.
        provider.RollbackMerge(integ, ct);
        // 3. Journal NeedsHuman — mergeSequence NOT consumed.
        _journal.RecordSettle(task.Id, JournalTaskStatus.NeedsHuman, null);

        return new TaskResult
        {
            TaskId = task.Id,
            Outcome = TaskOutcome.NeedsHuman,
            ActionExitCode = result.ActionExitCode,
            Guardrails = result.Guardrails,
            Summary = $"non-FF union re-verify failed; rolled back (B1 four-effect) (see {feedbackPath})"
        };
    }

    /// <summary>
    /// Persist a failed union re-verify's evidence to the task log dir (issue #188): one
    /// <c>union-reverify-&lt;guardrail&gt;.stdout.log</c> per failing integration guardrail carrying its
    /// captured output, plus the <c>feedback.md</c> the needs-human summary points at (which the B1
    /// rollback path previously PROMISED but never wrote). Called BEFORE the rollback resets the
    /// integration worktree, so the merged-bytes evidence survives the discard. Returns the absolute
    /// <c>feedback.md</c> path for the summary. Best-effort: an IO failure here must never mask the
    /// underlying re-verify failure, so it degrades to returning the intended path.
    /// </summary>
    private string PersistUnionReVerifyFailure(
        TaskNode task, TaskResult result, IntegrationHandle integ, ReVerifyResult reVerify, bool aiMerge)
    {
        // The task log dir is the PARENT of this attempt's log dir. Derive it from the attempt's own
        // relative logDir (logs/<runId>/<taskId>/attempt-N) — which uses the JOURNAL's runId, the same
        // runId the executor writes attempt artifacts under — so the evidence lands beside them. Fall
        // back to integ.RunId only if no attempt data threaded through (defensive).
        string taskLogDir = result.PendingAttempt?.LogDir is { Length: > 0 } relLogDir
            ? Path.GetDirectoryName(Path.Combine(
                _plan.PlanDirectory, relLogDir.Replace('/', Path.DirectorySeparatorChar)))!
            : Path.Combine(_plan.PlanDirectory, "logs", integ.RunId, task.Id);
        string feedbackPath = Path.Combine(taskLogDir, "feedback.md");

        try
        {
            Directory.CreateDirectory(taskLogDir);

            var failingNames = new List<string>();
            foreach (GuardrailResult failed in reVerify.FailedGuardrails)
            {
                failingNames.Add(failed.Name);
                string safe = SanitizeGuardrailName(failed.Name);
                // GuardrailResult.Output is the full captured output on failure (stdout, or stderr when
                // stdout was empty); Reason is its first line. Persist both so the evidence is complete.
                string body = failed.Output ?? failed.Reason ?? "(no output captured)";
                AtomicFile.WriteAllText(
                    Path.Combine(taskLogDir, $"union-reverify-{safe}.stdout.log"), body);
            }

            string mergeKind = aiMerge ? "AI-merge resolution" : "non-FF union merge";
            string detail = reVerify.FailedGuardrails.Count == 0
                ? "The integration re-verify failed but reported no per-guardrail detail."
                : string.Join("\n\n", reVerify.FailedGuardrails.Select(g =>
                    $"## {g.Name}\n\n{g.Reason ?? "(no reason)"}\n\n" +
                    $"Full output persisted to `union-reverify-{SanitizeGuardrailName(g.Name)}.stdout.log`."));

            string feedback =
                $"# Task '{task.Id}' — union re-verify failed\n\n" +
                $"Task: {task.Description}\n\n" +
                $"The {mergeKind} produced bytes that FAILED the integration-guardrail re-verify, so the " +
                "harness rolled the merge back (state.json restored, integration worktree reset) and settled " +
                "this task `needs-human`. The merged bytes were discarded, but each failing integration " +
                "guardrail's output was persisted next to this file:\n\n" +
                $"{detail}\n\n" +
                "This is typically a MERGE COLLISION (two colliding contributions combined into something " +
                "that no longer builds/passes) — inspect the persisted output, fix the offending task(s), " +
                "and re-run.\n";
            AtomicFile.WriteAllText(feedbackPath, feedback);
        }
        catch
        {
            // Best-effort — never let a logging IO failure mask the re-verify failure itself.
        }

        return feedbackPath;
    }

    /// <summary>
    /// Filename-safe form of a guardrail name for the #188 union-reverify log artifacts — the SSOT §8
    /// sanitization rule, shared with the gate captures rather than re-spelled here (issue #432).
    /// </summary>
    private static string SanitizeGuardrailName(string name) => GateArtifacts.Sanitize(name);

    /// <summary>
    /// Record a worktree-mode SUCCESS settle (issue #196): journal a real
    /// <see cref="Journal.AttemptRecord"/> for the just-completed attempt TOGETHER with the reserved
    /// <paramref name="mergeSequence"/>, so a succeeded worktree task has a populated <c>Attempts</c>
    /// list exactly like a succeeded serial task (SSOT §7). The attempt data was computed by the
    /// executor and threaded here on <see cref="TaskResult.PendingAttempt"/>; its stamped
    /// <see cref="AttemptOutcome.Succeeded"/> outcome carries the #198 provenance (model + segment
    /// worktree + base commit). A result missing its <see cref="TaskResult.PendingAttempt"/> (a
    /// fake-provider path that never went through <c>ValidateFragmentForSettle</c>) falls back to the
    /// attempt-less <see cref="ISchedulerJournal.RecordSettle"/>, so no path regresses.
    /// </summary>
    private void RecordSucceededSettle(
        TaskNode task, TaskResult result, long mergeSequence, string? definitionHash = null)
    {
        if (result.PendingAttempt is not { } pending)
        {
            _journal.RecordSettle(task.Id, JournalTaskStatus.Succeeded, mergeSequence, definitionHash);
            return;
        }

        var record = new Journal.AttemptRecord
        {
            Attempt = pending.Attempt,
            StartedAt = pending.StartedAt,
            EndedAt = DateTimeOffset.UtcNow,
            ActionExitCode = pending.ActionExitCode,
            Outcome = Journal.AttemptOutcome.Succeeded,
            CostUsd = pending.CostUsd,
            // #475: the tokens axis travels beside its cost sibling on THIS path too — the default one.
            Usage = pending.Usage,
            LogDir = pending.LogDir,
            Provenance = pending.Provenance
        };
        _journal.RecordSettleWithAttempt(task.Id, record, JournalTaskStatus.Succeeded, mergeSequence, definitionHash);
    }

    /// <summary>
    /// Shallow-merge <paramref name="fragmentPath"/> into state.json at <paramref name="statePath"/>.
    /// The fragment was already validated (valid JSON object, no foreign keys). Uses atomic write.
    /// </summary>
    private static void MergeFragmentIntoState(string statePath, string preMergeState, string fragmentPath)
    {
        var stateObj = (JsonNode.Parse(preMergeState) as JsonObject) ?? new JsonObject();
        string rawFrag = File.ReadAllText(fragmentPath);
        var fragObj = (JsonNode.Parse(rawFrag) as JsonObject) ?? new JsonObject();

        foreach (var (key, value) in fragObj)
        {
            stateObj[key] = value?.DeepClone();
        }

        AtomicFile.WriteAllText(statePath, stateObj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>
    /// The run's INTEGRATION-GUARDRAIL SET (SSOT §4.3) — every guardrail declared
    /// <c>scope:"integration"</c>, re-run on the merged bytes at EVERY union point (clean non-FF and
    /// AI-resolved alike) and by the legacy terminal gate.
    /// <para>
    /// <b>Issue #451.</b> The set is drawn from BOTH guardrail homes: the per-task
    /// <c>&lt;task&gt;/guardrails/</c> folders AND the plan-root <c>&lt;plan&gt;/guardrails/</c> folder
    /// (<see cref="PlanDefinition.PlanGuardrails"/>). It previously read <c>plan.Tasks</c> only, so a
    /// plan-root guardrail tagged <c>scope:"integration"</c> — which under the four-folder model is
    /// exactly where a UNION-INVARIANT check belongs — silently never ran at any union. That is how a
    /// conflict-marker + duplicate-member scan sat in a plan, correctly authored and correctly tagged,
    /// while a union shipped a file with conflict markers still in it.
    /// </para>
    /// <para>
    /// The <c>scope</c> tag remains the ONLY selector, so a plan-root guardrail left at the default
    /// <c>local</c> scope still runs once at the terminal gate and nowhere else — the plan-root folder's
    /// GR2028 terminal-sink obligation is independent of this per-union tag. Extracted to one method so
    /// the three call sites cannot drift apart again.
    /// </para>
    /// </summary>
    private static IReadOnlyList<GuardrailDefinition> UnionIntegrationSet(PlanDefinition plan) =>
        GuardrailScopeFilter.IntegrationSet(
            plan.Tasks.SelectMany(t => t.Guardrails).Concat(plan.PlanGuardrails));

    /// <summary>
    /// Convert an all-green report into a needs-human one when the terminal integration gate
    /// (§3.3) failed on the final plan-branch HEAD. The failure is attributed to the
    /// <c>integrationGate:true</c> sink task (or, absent one, the last task in plan order) so the
    /// run is not certified and mergeOnSuccess is refused.
    /// <para>
    /// #175 attribution: a terminal-gate (typically whole-repo build/test) failure on the merged HEAD
    /// is frequently a MERGE COLLISION — two tasks with OVERLAPPING <c>writeScope</c> on a shared file
    /// both wrote new content there and git's 3-way merge silently kept both (a semantic duplicate, no
    /// textual conflict marker). The harness cannot generically detect the duplicate (that is the build
    /// guardrail's job), but it CAN name the suspects: the diagnosis lists every overlapping-writeScope
    /// task pair and the shared path so a human immediately sees "this looks like a merge collision
    /// between task A and task B on &lt;file&gt;" instead of a bare build error. Advisory and robust —
    /// based PURELY on the writeScope-overlap structure (never the error text / a CS-code), and adds
    /// nothing when no writeScopes overlap.
    /// </para>
    /// </summary>
    private static RunReport WithTerminalGateFailure(
        PlanDefinition plan, RunReport report, ReVerifyResult gate)
    {
        string gateTaskId =
            plan.Tasks.LastOrDefault(t => t.IntegrationGate)?.Id
            ?? plan.Tasks[^1].Id;

        string failed = string.Join(", ", gate.FailedGuardrails.Select(g => g.Name));
        string summary = $"terminal integration gate failed on final HEAD: {failed}";

        string? collisionHint = WriteScope.OverlappingWriteScopeHint(plan);
        if (collisionHint is not null)
        {
            summary += $". {collisionHint}";
        }

        var rewritten = report.Tasks.Select(t => t.TaskId == gateTaskId
            ? t with
            {
                Outcome = TaskOutcome.NeedsHuman,
                Guardrails = gate.FailedGuardrails,
                Summary = summary
            }
            : t).ToList();

        return report with { Tasks = rewritten };
    }

    private RunReport BuildReport(
        PlanDefinition plan,
        IReadOnlyDictionary<string, TaskResult> settled,
        bool cancelled)
    {
        var results = new List<TaskResult>(plan.Tasks.Count);
        foreach (TaskNode task in plan.Tasks)
        {
            results.Add(settled.TryGetValue(task.Id, out TaskResult? result)
                ? result
                : new TaskResult
                {
                    TaskId = task.Id,
                    Outcome = TaskOutcome.Cancelled,
                    Summary = "not started (run cancelled)"
                });
        }

        // Every report this run produces — green, halted, cancelled — comes through here, so the terminal
        // surface of the plan-edit advisory is carried on ALL of them (plan 31 §5.4). An operator who edited
        // the plan folder during a run that then halted for an unrelated reason still needs to be told.
        return new RunReport
        {
            Tasks = results,
            Cancelled = cancelled,
            Observations = PlanEditObservationsSnapshot()
        };
    }

    /// <summary>
    /// Per-task channel item pairing a task with its assigned worktree handle. When
    /// <see cref="Fork"/> is non-null the handle is a placeholder and the worker materializes the
    /// real fork worktree off-gate at dequeue (M1 §B); otherwise <see cref="Handle"/> is the final
    /// assigned segment/reused directory.
    /// </summary>
    private readonly record struct TaskEnvelope(TaskNode Task, WorktreeHandle Handle, ForkRequest? Fork = null);

    /// <summary>
    /// A deferred fork-the-rest request (M1 §B): the producer's RECORDED commit sha to fork off
    /// (W-2 — never a live rev-parse of the inheritor-advanced segment branch). Materialized by the
    /// worker via <see cref="IWorktreeProvider.ForkFromTip"/> before the task's action runs.
    /// </summary>
    private readonly record struct ForkRequest(string ProducerRecordedSha);

    /// <summary>Mutable shared state of one run, guarded by the scheduler's gate.</summary>
    private sealed class RunContext(
        DependencyGraph graph,
        IReadOnlyDictionary<string, TaskNode> byId,
        Dictionary<string, TaskResult> settled,
        Dictionary<string, int> pendingDeps,
        Channel<TaskEnvelope> channel,
        int remaining,
        Dictionary<string, WorktreeHandle> handles,
        Dictionary<string, string> directoryOwner,
        IntegrationHandle? integ)
    {
        public DependencyGraph Graph { get; } = graph;
        public IReadOnlyDictionary<string, TaskNode> ById { get; } = byId;
        public Dictionary<string, TaskResult> Settled { get; } = settled;
        public Dictionary<string, int> PendingDeps { get; } = pendingDeps;
        public Channel<TaskEnvelope> Channel { get; } = channel;
        public int Remaining { get; set; } = remaining;
        public Dictionary<string, WorktreeHandle> Handles { get; } = handles;

        /// <summary>
        /// plan 08 topology-wiring M0 bookkeeping: worktree path → current owning task id. Written
        /// under <see cref="_gate"/> only (CreateSegment/ForkFromTip set it; ReuseSegment transfers
        /// ownership to the inheritor; Discard removes the entry). The single source of truth for
        /// "is this directory free to Discard / reuse?".
        /// </summary>
        public Dictionary<string, string> DirectoryOwner { get; } = directoryOwner;
        public IntegrationHandle? Integ { get; } = integ;
    }
}
