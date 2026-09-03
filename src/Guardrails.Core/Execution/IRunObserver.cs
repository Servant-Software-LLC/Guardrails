using Guardrails.Core.Model;

namespace Guardrails.Core.Execution;

/// <summary>
/// Receives progress events as a run proceeds. Keeps the <see cref="Scheduler"/> and
/// <see cref="TaskExecutor"/> free of any console/UI dependency; the CLI supplies a
/// plain-text or live (Spectre) implementation. Implementations MUST be thread-safe —
/// M4 workers emit events concurrently. A no-op default is available via <see cref="Null"/>.
/// </summary>
public interface IRunObserver
{
    /// <summary>A task is about to run its action.</summary>
    void TaskStarting(TaskNode task);

    /// <summary>
    /// An attempt is starting: <paramref name="attempt"/> of <paramref name="budget"/>
    /// for this run (1-based; budget = 1 + retries).
    /// </summary>
    void AttemptStarting(TaskNode task, int attempt, int budget) { }

    /// <summary>
    /// This attempt's model is now KNOWN (#349). <paramref name="model"/> is the BEST-KNOWN-ACTUAL model —
    /// the one the runner reported itself running on, else the resolved route's model, else the
    /// <c>"(cli default)"</c> sentinel. <paramref name="requestedModel"/> is non-null ONLY when the route
    /// asked for something ELSE, so its PRESENCE is the mismatch signal and there is no separate flag
    /// beside it.
    ///
    /// <para><b>The same two fields <see cref="Journal.AttemptProvenance"/> already carries</b>
    /// (<see cref="Journal.AttemptProvenance.Model"/> / <see cref="Journal.AttemptProvenance.RequestedModel"/>,
    /// SSOT §7). The harness folds the observed model over the requested one ONCE, at the attempt, and hands
    /// both down; no observer re-derives "did the provider serve something else". A surface that recomputed
    /// it would be a second owner of the rule and would drift from the <c>run.json</c> it is supposed to be
    /// showing — the <see cref="VerifierAdvisoryFound"/> D22a discipline. Both are primitives beside
    /// <see cref="TaskNode"/> for that event's other reason too: this interface is public,
    /// <c>Guardrails.Cli</c> has no <c>InternalsVisibleTo</c> into <c>Guardrails.Core</c>, and a provenance
    /// TYPE on this signature would be inconsistent accessibility (CS0051) the moment the type is not public.</para>
    ///
    /// <para>Default no-op so non-CLI observers need not handle it — but a transparent DECORATOR must
    /// still forward it EXPLICITLY: an unforwarded call resolves to this empty body and the disclosure is
    /// swallowed silently, in every mode.</para>
    /// </summary>
    void AttemptModelResolved(TaskNode task, int attempt, string model, string? requestedModel) { }

    /// <summary>
    /// A prompt attempt's ROUTE is resolved and the attempt is about to LAUNCH (#524). Raised BEFORE the
    /// action runs — unlike <see cref="AttemptModelResolved"/>, which cannot fire until the runner has
    /// reported what it ran on, so a surface fed only from that one is a placeholder for the whole
    /// attempt (MEASURED at 14m02s and longer). <paramref name="tier"/> is the rung SERVED (after any
    /// §6.2 climb), <paramref name="requestedTier"/> is non-null ONLY when the climb moved it — its
    /// PRESENCE is the climb signal, exactly as <c>AttemptProvenance.RequestedModel</c>'s presence is the
    /// substitution signal — <paramref name="runner"/> is the <c>promptRunners</c> block key, and
    /// <paramref name="model"/> is the route's model.
    ///
    /// <para>Primitives beside <see cref="TaskNode"/> for the same reason
    /// <see cref="AttemptModelResolved"/> spells out above: this interface is public,
    /// <c>Guardrails.Cli</c> has no <c>InternalsVisibleTo</c> into <c>Guardrails.Core</c>, and a
    /// provenance TYPE on this signature would be inconsistent accessibility (CS0051) the moment the
    /// type is not public. A script attempt resolves no route and raises nothing.</para>
    ///
    /// <para>Default no-op so non-CLI observers need not handle it — but a transparent DECORATOR must
    /// still forward it EXPLICITLY: an unforwarded call resolves to this empty body and the disclosure is
    /// swallowed silently, in every mode. Both transparent decorators sit in BOTH the live and the
    /// <c>--no-ui</c> chain, so one missing forward takes the route away from every operator everywhere,
    /// and nothing in the build can see it.</para>
    /// </summary>
    void AttemptRouteResolved(
        TaskNode task, int attempt, string runner, string model,
        string? tier, string? requestedTier) { }

    /// <summary>
    /// Attempt <paramref name="attempt"/> of <paramref name="task"/> just FINISHED — the retry loop is
    /// about to either burn another attempt or settle the task. Until this event existed, the only
    /// per-attempt signal an observer had was <see cref="AttemptStarting"/>; the next thing it heard was
    /// <see cref="TaskFinished"/>, after the WHOLE retry loop, so nothing could say WHY a single attempt
    /// failed — the shipped <c>[retry] &lt;task&gt;: attempt 2/3</c> line carries no reason. <paramref
    /// name="outcome"/> is the SAME <see cref="Journal.AttemptOutcome"/> already recorded for this
    /// attempt in the journal (SSOT §7) — no new vocabulary, no observer re-deriving why an attempt ended
    /// the way it did.
    ///
    /// <para>Default no-op so non-CLI observers need not handle it — but a transparent DECORATOR must
    /// still forward it EXPLICITLY: an unforwarded call resolves to this empty body and the disclosure is
    /// swallowed silently, in every mode (the <see cref="WaveGateFinished"/> / <see cref="VerifierAdvisoryFound"/>
    /// lesson).</para>
    /// </summary>
    void AttemptFinished(TaskNode task, int attempt, Journal.AttemptOutcome outcome) { }

    /// <summary>A task finished (succeeded, failed, or was blocked).</summary>
    void TaskFinished(TaskResult result);

    /// <summary>A guardrail finished running.</summary>
    void GuardrailFinished(TaskNode task, GuardrailResult result);

    /// <summary>
    /// The resumed journal's plan hash differs from the current plan's — the manifests
    /// changed since the journal was written. The run continues (SSOT §7); this is a loud
    /// warning, not an error.
    /// </summary>
    void PlanHashMismatch(string previousPlanHash) { }

    /// <summary>
    /// A plan requested <paramref name="requested"/>-way parallelism but no worktree provider
    /// was available, so the scheduler clamped to serial (shared-workspace) execution to avoid
    /// an unsafe shared-workspace race (plan 08 §1 / F7). The run continues serially; this is a
    /// loud demotion notice, not an error. Default no-op so non-CLI observers need not handle it.
    /// </summary>
    void ParallelismClampedNoProvider(int requested) { }

    /// <summary>
    /// A best-effort cleanup operation (a segment-worktree <c>Discard</c> or <c>PruneOrphans</c>)
    /// failed during the M2 end-of-run sweep or a failed-task Discard. <paramref name="owner"/> is
    /// the owning task id (or a sentinel like <c>(prune-orphans)</c>). The run's verdict is
    /// unaffected — a cleanup failure must never flip a green run off-green (plan 08 §D / #126).
    /// Default no-op so non-CLI observers need not handle it.
    /// </summary>
    void CleanupFailed(string owner, Exception error) { }

    /// <summary>
    /// A task's prompt action hit a TRANSIENT, retryable infrastructure condition (an HTTP 429/503/529,
    /// "overloaded", or a usage/session/rate limit) and the harness is PAUSING before re-running the
    /// same attempt — WITHOUT consuming the retry budget (SSOT §9, issue #115). <paramref name="reason"/>
    /// is the operator-facing cause (carrying a reset hint like "resets 11:20am" when one was present),
    /// <paramref name="backoff"/> is how long this pause waits, and <paramref name="pauseCount"/> is the
    /// 1-based pause number for this task. A distinct signal so an operator sees a HEALTHY task waiting
    /// out a rate limit, not a failing one. Default no-op so non-CLI observers need not handle it.
    /// </summary>
    void PromptPaused(TaskNode task, string reason, TimeSpan backoff, int pauseCount) { }

    /// <summary>
    /// Phase-2 scope-clean (SSOT §3.4, issue #280): after a <c>writeScope</c> task's guardrails PASSED,
    /// the harness re-computed the out-of-scope changed paths a passing guardrail left as side effects
    /// (an <c>npm ci</c>, a build cache, a generated <c>dist/</c>) and STRIPPED them from the segment
    /// before the commit, so the commit carries exactly the in-scope diff. This is NOT a failure — the
    /// paths are echoed for diagnosability (the #253 "don't silently vanish files" posture), never
    /// punished. Default no-op so non-CLI observers need not handle it.
    /// </summary>
    void OutOfScopeStripped(TaskNode task, IReadOnlyList<WriteScopeOffense> stripped) { }

    /// <summary>
    /// An autonomy-policy decision was recorded to the unified <c>decisions[]</c> log (SSOT §2.1/§7): the
    /// <paramref name="entry"/> carries the <c>boundary</c> (M1 emits only <c>drift</c> — a safe
    /// definition-drift auto-resolved at the pre-DAG gate, §7.2), the policy in force, how it resolved, and
    /// a human headline/subject. Surfaced under the live task table and the static log site so an operator
    /// sees exactly what a decision did — the same payload the durable <c>decisions[]</c> journal section
    /// records. NOT a failure (an auto-resolved drift then proceeds and returns the normal exit code).
    /// Default no-op so non-CLI observers need not handle it.
    /// </summary>
    void DecisionRecorded(DecisionEntry entry) { }

    /// <summary>
    /// The DoR §6.5 <b>verifier advisory</b> (#229), raised at RUN START — once per AFFECTED task,
    /// before the DAG executes — so an operator learns that a judge is weaker than (or equal-and-weak
    /// to) the work it grades BEFORE paying for the run, instead of reading it out of <c>run.json</c>
    /// afterwards. <paramref name="taskId"/> is the task the advisory is about;
    /// <paramref name="finding"/> is the already-composed human line from <c>VerifierAdvisory</c>, the
    /// ONE owner of the rule — no observer re-derives "is this judge weak" (D22a).
    ///
    /// <para><b>Advisory means advisory.</b> §12.6 forbids any verifier condition from failing a
    /// build, so this event only reports and nothing branches on it; a run with no findings raises it
    /// zero times and prints nothing at all. Both parameters are primitives on purpose — this
    /// interface is public, <c>Guardrails.Cli</c> has no <c>InternalsVisibleTo</c> into
    /// <c>Guardrails.Core</c>, and a finding TYPE on this signature would be inconsistent
    /// accessibility (CS0051) the moment the type is not public.</para>
    ///
    /// <para>Default no-op so non-CLI observers need not handle it — but a transparent DECORATOR must
    /// still forward it EXPLICITLY: an unforwarded call resolves to this empty body and the advisory
    /// is swallowed silently, in exactly the mode most operators run.</para>
    /// </summary>
    void VerifierAdvisoryFound(string taskId, string finding) { }

    /// <summary>
    /// The #269 overwatcher was CONSULTED, SPENT, and came back with nothing (issue #452): the diagnose
    /// runner errored, exhausted its turns, was aborted on a run of permission denials, or returned a body
    /// that does not parse as a verdict. <paramref name="taskId"/> is the task it was supervising;
    /// <paramref name="reason"/> is the already-composed one-line cause (the runner's own summary).
    ///
    /// <para><b>Why this exists at all.</b> Before #452 this outcome was indistinguishable from "the
    /// supervisor had nothing to report" — it recorded no <c>decisions[]</c> entry and printed no line, so
    /// a billed no-op looked exactly like a healthy quiet run. The only trace was <c>is_error: true</c>
    /// inside a per-attempt JSONL nobody opens unless they already suspect a problem. Silence must not be
    /// how a paid supervisor reports its own failure.</para>
    ///
    /// <para>Rendered in the ADVISORY idiom (a yellow tag + task id, above the Spectre live region under
    /// the observer's gate — #145/#372), NOT as a failure: the overwatcher is advisory and a no-verdict
    /// changes no task verdict and no exit code. Raised INSTEAD of
    /// <see cref="DecisionRecorded"/> for the same event so one failure prints one line, while the durable
    /// <c>decisions[]</c> entry (<c>decision: "no-verdict"</c>) is still journaled. Default no-op — but a
    /// transparent DECORATOR must forward it explicitly or the surface is swallowed again.</para>
    /// </summary>
    void OverwatchNoVerdict(string taskId, string reason) { }

    /// <summary>
    /// A WAVED plan's wave <paramref name="wave"/> (the <paramref name="index"/>-th of
    /// <paramref name="total"/>, 1-based) is about to run its DAG drain (SSOT §14.4). The harness runs
    /// waves in strict order behind a hard barrier; this lets the UI retitle/segment the task table per
    /// wave. Default no-op so non-CLI observers and FLAT plans (never emitted) need not handle it.
    /// </summary>
    void WaveStarting(Model.WaveNode wave, int index, int total) { }

    /// <summary>
    /// A WAVED plan's wave <paramref name="wave"/> finished (SSOT §14.4/§14.6): <paramref name="status"/>
    /// is <c>completed</c> (drained green + exit gate passed), or a halt state (<c>needs-human</c>/
    /// <c>blocked</c>) when the barrier stopped the run. <paramref name="skipped"/> is true when the wave
    /// was already complete on resume and was skipped without running (SSOT §14.6). Default no-op.
    /// </summary>
    void WaveFinished(Model.WaveNode wave, Journal.WaveStatus status, bool skipped) { }

    /// <summary>
    /// A wave's ENTRY gate (<paramref name="isEntryGate"/> true — its <c>preflights/</c>) or EXIT gate
    /// (false — its <c>guardrails/</c>) finished, carrying every check's result in the wave's own order
    /// (issue #513). Default no-op.
    ///
    /// <para><b>Why this exists.</b> Per-TASK guardrail results reach observers through
    /// <see cref="GuardrailFinished"/>; wave-level gate results reached them through nothing at all. The
    /// diagram consequently rendered a gate that ran and passed identically to one that never ran — and an
    /// operator reading a finished run asked, correctly, whether the wave-2 exit gate had executed. It
    /// had: a whole-solution build plus both suites unfiltered, 10m44s. The work happened and the record
    /// could not say so.</para>
    ///
    /// <para><b>A transparent DECORATOR must forward this EXPLICITLY.</b> It is a default-method member,
    /// so a decorator that does not declare it inherits the empty body and swallows the event in every
    /// mode — the identical trap <c>AttemptModelResolved</c> carries, and the reason both are asserted on
    /// the decorators themselves rather than only on the renderer.</para>
    /// </summary>
    void WaveGateFinished(
        Model.WaveNode wave,
        bool isEntryGate,
        IReadOnlyList<Journal.PlanPreflightCheck> checks) { }

    /// <summary>
    /// The between-wave JIT breakdown (SSOT §14.4, doc 11 §9) is STARTING for an unauthored wave. Raised
    /// from INSIDE the Spectre live region, so an implementation must not write plain lines (#145/#372) —
    /// the shipped renderer drives a synthetic table row and at most one gated <c>MarkupLine</c>.
    ///
    /// <para>Until this event existed the phase raised NOTHING AT ALL: <see cref="WaveStarting"/> fires only
    /// AFTER the checkpoint, so a wave could be authored for 30 minutes with no observer call of any kind,
    /// while the live table — which emits rows per <c>wave.Tasks</c>, and a JIT stub has none — rendered the
    /// run as FINISHED (issue #469). Default no-op so non-CLI observers and FLAT plans (never emitted) need
    /// not handle it, but a transparent DECORATOR must forward it EXPLICITLY or the phase goes silent again
    /// in every mode (the <see cref="VerifierAdvisoryFound"/> lesson).</para>
    /// </summary>
    void WaveBreakdownStarting(WaveBreakdownContext context) { }

    /// <summary>
    /// The JIT breakdown finished. <paramref name="elapsed"/> is the session's wall clock;
    /// <paramref name="authoredTaskCount"/> is the count the deterministic <c>guardrails validate</c> gate
    /// found on disk (authoritative — never the session's own claim);
    /// <paramref name="failureKind"/> is null on a clean session, else the
    /// <c>PromptResult.FailureKind</c> token (<c>timeout</c> / <c>max-turns</c> / <c>output-cap</c> /
    /// <c>transient</c> / <c>error</c>) that design 20 §4.1 stopped discarding.
    /// <paramref name="authoredWave"/> is the freshly-authored <see cref="Model.WaveNode"/> when the run will
    /// PROCEED with it (review-gate Option P) — the seam #404 needs to add its task rows — and null on every
    /// halting path. Default no-op; a decorator must still forward it explicitly.
    /// </summary>
    void WaveBreakdownFinished(
        WaveBreakdownContext context,
        TimeSpan elapsed,
        int authoredTaskCount,
        string? failureKind,
        Model.WaveNode? authoredWave) { }

    /// <summary>An observer that does nothing.</summary>
    static IRunObserver Null { get; } = new NullObserver();

    private sealed class NullObserver : IRunObserver
    {
        public void TaskStarting(TaskNode task) { }
        public void TaskFinished(TaskResult result) { }
        public void GuardrailFinished(TaskNode task, GuardrailResult result) { }
        public void PlanHashMismatch(string previousPlanHash) { }
    }
}
