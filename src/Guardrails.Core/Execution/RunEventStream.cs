using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Guardrails.Core.Model;

namespace Guardrails.Core.Execution;

/// <summary>
/// The semantic, low-frequency, agent-facing run-event projection off the one emission seam (plan 34):
/// a DECORATOR that sits beside every other <see cref="IRunObserver"/> in the chain and appends one JSON
/// object per line to <c>events.jsonl</c> in the directory it is constructed with (the run's own log
/// directory, <c>logs/&lt;runId&gt;/</c>), stamping each row with the run id it is constructed with
/// explicitly — since no member of <see cref="IRunObserver"/> carries one separately. A supervising agent filters rows on
/// FIELDS (<c>taskId</c>, <c>attempt</c>, …), so a row whose <c>kind</c> it does not recognise is still a
/// visible line rather than an invisible one — the property that would have prevented all three of the
/// stdout-grep failures in issue #585.
///
/// <para><b>Every member is overridden here</b> — including the ones <see cref="IRunObserver"/> gives an
/// empty default body — because a decorator that leaves even one to the interface default silently
/// swallows that event: the trap <see cref="IRunObserver"/> documents on
/// <see cref="IRunObserver.AttemptModelResolved"/>, <see cref="IRunObserver.WaveGateFinished"/>,
/// <see cref="IRunObserver.VerifierAdvisoryFound"/> and <see cref="IRunObserver.WaveBreakdownStarting"/>.
/// Every member forwards to the inner observer; the members that also append a row are the task/attempt
/// LIFECYCLE ones listed below.</para>
///
/// <para><b>Emitted kinds</b> (issue #595 — the original shipped only <c>attempt-finished</c>, which left
/// a consumer unable to tell a healthy run that has not finished its first attempt from a run that never
/// started, the very ambiguity #585 exists to remove):
/// <list type="bullet">
///   <item><c>task-started</c> — a task entered execution. The FIRST of these is a run's liveness proof.</item>
///   <item><c>attempt-started</c> — an attempt began, carrying its <c>budget</c>.</item>
///   <item><c>guardrail-finished</c> — one guardrail settled: <c>guardrail</c>, <c>passed</c>, and on
///         failure the <c>detail</c> a supervisor would otherwise open <c>feedback.md</c> to read.</item>
///   <item><c>attempt-finished</c> — an attempt settled, carrying the journal's own attempt record
///         (<see cref="Journal.AttemptRecord"/>): <c>outcome</c>, <c>costUsd</c>, <c>turns</c>,
///         <c>model</c>/<c>tier</c>/<c>runner</c>, <c>startedAt</c>/<c>endedAt</c>, and
///         <c>needsHumanKind</c>.</item>
///   <item><c>task-settled</c> — a task reached a terminal outcome.</item>
///   <item><c>run-finished</c> — the run itself reached a terminal outcome, carrying <c>exitCode</c> and
///         <c>faultKind</c>. It is the only kind with no <c>taskId</c>: it is run-scoped, not task-scoped.</item>
/// </list></para>
///
/// <para><b>Row shape.</b> The row is the telemetry corpus row (<see cref="Telemetry.TelemetryRow"/>)
/// emitted LIVE rather than at settle: <c>runId</c>/<c>taskId</c>/<c>attempt</c>/<c>outcome</c> are the
/// same fields, the same wire tokens (<see cref="Journal.JournalJson.OutcomeToken"/>), that the settled
/// row carries. <c>kind</c> and <c>at</c> exist only because a live stream needs them and a settled row
/// does not: <c>kind</c> discriminates the event, <c>at</c> is when it was observed. Fields that do not
/// apply to a kind are OMITTED rather than written null, so a consumer testing for a field's presence
/// gets a straight answer.</para>
/// </summary>
public sealed class RunEventStream : IRunObserver
{
    private static readonly JsonSerializerOptions LineOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,

        // A row carries only the fields its kind defines: `task-started` has no attempt, `guardrail-finished`
        // no outcome. Writing them as explicit nulls would make "absent" and "null" indistinguishable to a
        // consumer filtering on FIELDS, which is this stream's whole contract.
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly IRunObserver _inner;
    private readonly string _directory;
    private readonly string _runId;
    private readonly object _gate = new();

    /// <summary>The last <c>seq</c> assigned. Mutated only inside <see cref="_gate"/>.</summary>
    private int _seq;

    /// <param name="inner">The real observer every event is forwarded to.</param>
    /// <param name="directory">The run's log directory; events land in <c>events.jsonl</c> underneath it.</param>
    /// <param name="runId">
    /// The run's own id, as the composition root already knows it — never derived from
    /// <paramref name="directory"/>'s name, which merely resembles it.
    /// </param>
    public RunEventStream(IRunObserver inner, string directory, string runId)
    {
        _inner = inner;
        _directory = directory;
        _runId = runId;
    }

    /// <inheritdoc/>
    public void TaskStarting(TaskNode task)
    {
        _inner.TaskStarting(task);

        AppendLine(new EventRow
        {
            Kind = "task-started",
            RunId = _runId,
            TaskId = task.Id
        });
    }

    /// <inheritdoc/>
    public void AttemptStarting(TaskNode task, int attempt, int budget)
    {
        _inner.AttemptStarting(task, attempt, budget);

        AppendLine(new EventRow
        {
            Kind = "attempt-started",
            RunId = _runId,
            TaskId = task.Id,
            Attempt = attempt,
            Budget = budget
        });
    }

    /// <inheritdoc/>
    public void AttemptModelResolved(TaskNode task, int attempt, string model, string? requestedModel) =>
        _inner.AttemptModelResolved(task, attempt, model, requestedModel);

    /// <inheritdoc/>
    public void AttemptRouteResolved(
        TaskNode task, int attempt, string runner, string model, string? tier, string? requestedTier) =>
        _inner.AttemptRouteResolved(task, attempt, runner, model, tier, requestedTier);

    /// <inheritdoc/>
    public void AttemptFinished(TaskNode task, Journal.AttemptRecord record)
    {
        _inner.AttemptFinished(task, record);

        AppendLine(new EventRow
        {
            Kind = "attempt-finished",
            RunId = _runId,
            TaskId = task.Id,
            Attempt = record.Attempt,
            Outcome = Journal.JournalJson.OutcomeToken(record.Outcome),
            CostUsd = record.CostUsd,
            Turns = record.Turns,
            Model = record.Provenance?.Model,
            Tier = record.Provenance?.Tier,
            Runner = record.Provenance?.Runner,
            StartedAt = record.StartedAt,
            EndedAt = record.EndedAt,
            NeedsHumanKind = record.NeedsHumanKind
        });
    }

    /// <inheritdoc/>
    public void RunFinished(int? exitCode, string? faultKind)
    {
        _inner.RunFinished(exitCode, faultKind);

        AppendLine(new EventRow
        {
            Kind = "run-finished",
            RunId = _runId,
            TaskId = null,
            ExitCode = exitCode,
            FaultKind = faultKind
        });
    }

    /// <inheritdoc/>
    public void TaskFinished(TaskResult result)
    {
        _inner.TaskFinished(result);

        AppendLine(new EventRow
        {
            Kind = "task-settled",
            RunId = _runId,
            TaskId = result.TaskId,
            Outcome = TaskOutcomeToken(result.Outcome),
            Detail = result.Summary
        });
    }

    /// <inheritdoc/>
    public void GuardrailFinished(TaskNode task, GuardrailResult result)
    {
        _inner.GuardrailFinished(task, result);

        AppendLine(new EventRow
        {
            Kind = "guardrail-finished",
            RunId = _runId,
            TaskId = task.Id,
            Guardrail = result.Name,
            Passed = result.Passed,

            // Only on failure: the reason is what a supervisor would otherwise open feedback.md to read,
            // and a passing guardrail has nothing to say that `passed: true` does not already carry.
            Detail = result.Passed ? null : result.Reason
        });
    }

    /// <inheritdoc/>
    public void PlanHashMismatch(string previousPlanHash) => _inner.PlanHashMismatch(previousPlanHash);

    /// <inheritdoc/>
    public void ParallelismClampedNoProvider(int requested) => _inner.ParallelismClampedNoProvider(requested);

    /// <inheritdoc/>
    public void CleanupFailed(string owner, Exception error) => _inner.CleanupFailed(owner, error);

    /// <inheritdoc/>
    public void PromptPaused(TaskNode task, string reason, TimeSpan backoff, int pauseCount) =>
        _inner.PromptPaused(task, reason, backoff, pauseCount);

    /// <inheritdoc/>
    public void OutOfScopeStripped(TaskNode task, IReadOnlyList<WriteScopeOffense> stripped) =>
        _inner.OutOfScopeStripped(task, stripped);

    /// <inheritdoc/>
    public void DecisionRecorded(DecisionEntry entry) => _inner.DecisionRecorded(entry);

    /// <inheritdoc/>
    public void VerifierAdvisoryFound(string taskId, string finding) => _inner.VerifierAdvisoryFound(taskId, finding);

    /// <inheritdoc/>
    public void OverwatchNoVerdict(string taskId, string reason) => _inner.OverwatchNoVerdict(taskId, reason);

    /// <inheritdoc/>
    public void WaveStarting(Model.WaveNode wave, int index, int total) => _inner.WaveStarting(wave, index, total);

    /// <inheritdoc/>
    public void WaveFinished(Model.WaveNode wave, Journal.WaveStatus status, bool skipped) =>
        _inner.WaveFinished(wave, status, skipped);

    /// <inheritdoc/>
    public void WaveGateFinished(
        Model.WaveNode wave, bool isEntryGate, IReadOnlyList<Journal.PlanPreflightCheck> checks) =>
        _inner.WaveGateFinished(wave, isEntryGate, checks);

    /// <inheritdoc/>
    public void WaveBreakdownStarting(WaveBreakdownContext context) => _inner.WaveBreakdownStarting(context);

    /// <inheritdoc/>
    public void WaveBreakdownFinished(
        WaveBreakdownContext context, TimeSpan elapsed, int authoredTaskCount, string? failureKind,
        Model.WaveNode? authoredWave) =>
        _inner.WaveBreakdownFinished(context, elapsed, authoredTaskCount, failureKind, authoredWave);

    /// <summary>
    /// Appends <paramref name="row"/> as one complete JSON line to <c>events.jsonl</c>, flushed
    /// immediately so a consumer tailing the file sees it without waiting for the run to end. Guarded by
    /// <see cref="_gate"/>: <see cref="IRunObserver"/> requires thread-safety (M4 workers emit events
    /// concurrently), and an unguarded append from two threads could interleave and tear a line — a
    /// projection whose whole point is one-parseable-object-per-line cannot afford that.
    ///
    /// <para><c>Seq</c> and <c>At</c> are stamped HERE, inside the lock, rather than by the caller: both
    /// are ordering-relevant (<c>seq</c> is the field a supervisor keys retry/ordering on, per #585 layer
    /// 3), and stamping either outside the lock would let concurrent M4 workers race both the value they
    /// get and the order two rows land in the file.</para>
    /// </summary>
    private void AppendLine(EventRow row)
    {
        lock (_gate)
        {
            EventRow stamped = row with { Seq = ++_seq, At = DateTimeOffset.UtcNow };
            string line = JsonSerializer.Serialize(stamped, LineOptions);

            Directory.CreateDirectory(_directory);
            File.AppendAllText(Path.Combine(_directory, "events.jsonl"), line + "\n", Utf8NoBom);
        }
    }

    /// <summary>
    /// The kebab wire token for a <see cref="TaskOutcome"/>, matching
    /// <see cref="Journal.JournalJson.OutcomeToken"/>'s spelling of the members the two enums share, so a
    /// consumer filtering <c>outcome</c> across <c>attempt-finished</c> and <c>task-settled</c> rows sees
    /// ONE vocabulary (issue #585: "do NOT invent a second vocabulary").
    ///
    /// <para>Local to this projection rather than added to <c>JournalJson</c>: that class tokenizes
    /// <c>Guardrails.Core.Journal</c>'s own enums, and <see cref="TaskOutcome"/> lives in
    /// <c>Guardrails.Core.Execution</c> — reaching down for it there would invert the layering.
    /// <see cref="ObserverProjection"/> deliberately differs (it writes <c>ToString()</c>): that stream
    /// mirrors observer CALLS for a renderer, this one is a semantic stream for an agent.</para>
    /// </summary>
    private static string TaskOutcomeToken(TaskOutcome outcome) => outcome switch
    {
        TaskOutcome.Succeeded => "succeeded",
        TaskOutcome.ActionFailed => "action-failed",
        TaskOutcome.GuardrailFailed => "guardrail-failed",
        TaskOutcome.InvalidFragment => "invalid-fragment",
        TaskOutcome.NeedsHuman => "needs-human",
        TaskOutcome.RateLimited => "rate-limited",
        TaskOutcome.Blocked => "blocked",
        TaskOutcome.Skipped => "skipped",
        TaskOutcome.Cancelled => "cancelled",
        TaskOutcome.TransientPause => "transient-pause",

        // Throwing beats emitting a silently-wrong token: an unmapped member means the enum grew and this
        // switch did not, and a stream nobody can trust is worse than a run that says so.
        _ => throw new JsonException($"Unhandled task outcome '{outcome}'.")
    };

    /// <summary>
    /// One row of <c>events.jsonl</c>. <c>Kind</c>/<c>Seq</c>/<c>At</c>/<c>RunId</c> are on every row; the
    /// rest apply per kind and are omitted when null (see <see cref="LineOptions"/>). <c>TaskId</c> is
    /// <c>required</c> but nullable rather than merely optional (<see cref="int?"/>-style): every kind but
    /// <c>run-finished</c> is task-scoped, so every call site must make an explicit choice, and a future
    /// kind cannot silently omit it the way an un-set optional field would let it.
    /// </summary>
    private sealed record EventRow
    {
        public required string Kind { get; init; }

        /// <summary>Monotonic, 1-based, per-process. Stamped by <see cref="AppendLine"/>, never by a caller.</summary>
        public int Seq { get; init; }

        /// <summary>Stamped by <see cref="AppendLine"/>, never by a caller — see its doc comment.</summary>
        public DateTimeOffset At { get; init; }

        public required string RunId { get; init; }

        /// <summary>Every kind but <c>run-finished</c>, which is run-scoped rather than task-scoped.</summary>
        public required string? TaskId { get; init; }

        /// <summary><c>attempt-started</c> and <c>attempt-finished</c>.</summary>
        public int? Attempt { get; init; }

        /// <summary><c>attempt-finished</c> (an <see cref="Journal.AttemptOutcome"/>) and <c>task-settled</c> (a <see cref="TaskOutcome"/>).</summary>
        public string? Outcome { get; init; }

        /// <summary><c>attempt-started</c>: the attempt budget this attempt counts against.</summary>
        public int? Budget { get; init; }

        /// <summary><c>guardrail-finished</c>: the guardrail's name.</summary>
        public string? Guardrail { get; init; }

        /// <summary><c>guardrail-finished</c>: whether it passed.</summary>
        public bool? Passed { get; init; }

        /// <summary>Human-readable context — a failing guardrail's reason, or a settled task's summary.</summary>
        public string? Detail { get; init; }

        /// <summary><c>run-finished</c>: the process exit code, when the run reached one.</summary>
        public int? ExitCode { get; init; }

        /// <summary><c>run-finished</c>: the fault's TYPE NAME only — never its message (see <see cref="RunFinished"/>).</summary>
        public string? FaultKind { get; init; }

        /// <summary><c>attempt-finished</c>: <see cref="Journal.AttemptRecord.CostUsd"/>.</summary>
        public decimal? CostUsd { get; init; }

        /// <summary><c>attempt-finished</c>: <see cref="Journal.AttemptRecord.Turns"/>.</summary>
        public int? Turns { get; init; }

        /// <summary><c>attempt-finished</c>: <see cref="Journal.AttemptRecord.Provenance"/>'s <c>Model</c>.</summary>
        public string? Model { get; init; }

        /// <summary><c>attempt-finished</c>: <see cref="Journal.AttemptRecord.Provenance"/>'s <c>Tier</c>.</summary>
        public string? Tier { get; init; }

        /// <summary><c>attempt-finished</c>: <see cref="Journal.AttemptRecord.Provenance"/>'s <c>Runner</c>.</summary>
        public string? Runner { get; init; }

        /// <summary><c>attempt-finished</c>: <see cref="Journal.AttemptRecord.StartedAt"/>.</summary>
        public DateTimeOffset? StartedAt { get; init; }

        /// <summary><c>attempt-finished</c>: <see cref="Journal.AttemptRecord.EndedAt"/>.</summary>
        public DateTimeOffset? EndedAt { get; init; }

        /// <summary><c>attempt-finished</c>: <see cref="Journal.AttemptRecord.NeedsHumanKind"/>.</summary>
        public string? NeedsHumanKind { get; init; }
    }
}
