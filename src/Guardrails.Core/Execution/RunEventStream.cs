using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Guardrails.Core.Model;

namespace Guardrails.Core.Execution;

/// <summary>
/// The semantic, low-frequency, agent-facing run-event projection off the one emission seam (plan 34):
/// a DECORATOR that sits beside every other <see cref="IRunObserver"/> in the chain and appends one JSON
/// object per line to <c>events.jsonl</c> in the directory it is constructed with (the run's own log
/// directory, <c>logs/&lt;runId&gt;/</c> — <paramref name="directory"/>'s own name IS the run id, since
/// no member of <see cref="IRunObserver"/> carries one separately). A supervising agent filters rows on
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
///   <item><c>attempt-finished</c> — an attempt settled, carrying the <c>outcome</c> that decides whether
///         a retry is worth waiting out (<c>max-turns</c>) or fixing (<c>guardrail-failed</c>).</item>
///   <item><c>task-settled</c> — a task reached a terminal outcome.</item>
/// </list>
/// Run-level bracketing (<c>run-started</c>/<c>run-finished</c>) is NOT here: <see cref="IRunObserver"/>
/// has no run-scoped member to project, so it needs a new seam rather than a new projection (#595).</para>
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

    /// <param name="inner">The real observer every event is forwarded to.</param>
    /// <param name="directory">The run's log directory; events land in <c>events.jsonl</c> underneath it.</param>
    public RunEventStream(IRunObserver inner, string directory)
    {
        _inner = inner;
        _directory = directory;
        _runId = Path.GetFileName(Path.TrimEndingDirectorySeparator(directory));
    }

    /// <inheritdoc/>
    public void TaskStarting(TaskNode task)
    {
        _inner.TaskStarting(task);

        AppendLine(new EventRow
        {
            Kind = "task-started",
            At = DateTimeOffset.UtcNow,
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
            At = DateTimeOffset.UtcNow,
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
    public void AttemptFinished(TaskNode task, int attempt, Journal.AttemptOutcome outcome)
    {
        _inner.AttemptFinished(task, attempt, outcome);

        AppendLine(new EventRow
        {
            Kind = "attempt-finished",
            At = DateTimeOffset.UtcNow,
            RunId = _runId,
            TaskId = task.Id,
            Attempt = attempt,
            Outcome = Journal.JournalJson.OutcomeToken(outcome)
        });
    }

    /// <inheritdoc/>
    public void TaskFinished(TaskResult result)
    {
        _inner.TaskFinished(result);

        AppendLine(new EventRow
        {
            Kind = "task-settled",
            At = DateTimeOffset.UtcNow,
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
            At = DateTimeOffset.UtcNow,
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
    /// </summary>
    private void AppendLine(EventRow row)
    {
        string line = JsonSerializer.Serialize(row, LineOptions);

        lock (_gate)
        {
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
    /// One row of <c>events.jsonl</c>. <c>Kind</c>/<c>At</c>/<c>RunId</c>/<c>TaskId</c> are on every row;
    /// the rest apply per kind and are omitted when null (see <see cref="LineOptions"/>).
    /// </summary>
    private sealed record EventRow
    {
        public required string Kind { get; init; }
        public required DateTimeOffset At { get; init; }
        public required string RunId { get; init; }
        public required string TaskId { get; init; }

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
    }
}
