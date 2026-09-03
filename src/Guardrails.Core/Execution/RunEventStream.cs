using System.Text;
using System.Text.Json;
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
/// Every member forwards to the inner observer; only <see cref="AttemptFinished"/> also appends a row
/// today — it is the one event this projection's row shape (below) is defined for.</para>
///
/// <para><b>Row shape.</b> The row is the telemetry corpus row (<see cref="Telemetry.TelemetryRow"/>)
/// emitted LIVE rather than at settle: <c>runId</c>/<c>taskId</c>/<c>attempt</c>/<c>outcome</c> are the
/// same fields, the same wire tokens (<see cref="Journal.JournalJson.OutcomeToken"/>), that the settled
/// row carries. <c>kind</c> and <c>at</c> exist only because a live stream needs them and a settled row
/// does not: <c>kind</c> discriminates the event, <c>at</c> is when it was observed.</para>
/// </summary>
public sealed class RunEventStream : IRunObserver
{
    private static readonly JsonSerializerOptions LineOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
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
    public void TaskStarting(TaskNode task) => _inner.TaskStarting(task);

    /// <inheritdoc/>
    public void AttemptStarting(TaskNode task, int attempt, int budget) =>
        _inner.AttemptStarting(task, attempt, budget);

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
    public void TaskFinished(TaskResult result) => _inner.TaskFinished(result);

    /// <inheritdoc/>
    public void GuardrailFinished(TaskNode task, GuardrailResult result) => _inner.GuardrailFinished(task, result);

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

    private sealed record EventRow
    {
        public required string Kind { get; init; }
        public required DateTimeOffset At { get; init; }
        public required string RunId { get; init; }
        public required string TaskId { get; init; }
        public required int Attempt { get; init; }
        public required string Outcome { get; init; }
    }
}
