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
/// This is a STUB (plan 34 task 05): every member throws for now. Task 06 replaces each throw with the
/// real behaviour — forward to the inner observer AND append the event's row.</para>
/// </summary>
public sealed class RunEventStream : IRunObserver
{
    private readonly IRunObserver _inner;
    private readonly string _directory;

    /// <param name="inner">The real observer every event will be forwarded to once implemented.</param>
    /// <param name="directory">The run's log directory; events land in <c>events.jsonl</c> underneath it.</param>
    public RunEventStream(IRunObserver inner, string directory)
    {
        _inner = inner;
        _directory = directory;
    }

    private NotImplementedException NotYetImplemented(string member) =>
        new($"{nameof(RunEventStream)}.{member} does not yet forward to the inner " +
            $"{_inner.GetType().Name} or append to '{Path.Combine(_directory, "events.jsonl")}' — " +
            "that is plan 34 task 06's deliverable.");

    /// <inheritdoc/>
    public void TaskStarting(TaskNode task) => throw NotYetImplemented(nameof(TaskStarting));

    /// <inheritdoc/>
    public void AttemptStarting(TaskNode task, int attempt, int budget) =>
        throw NotYetImplemented(nameof(AttemptStarting));

    /// <inheritdoc/>
    public void AttemptModelResolved(TaskNode task, int attempt, string model, string? requestedModel) =>
        throw NotYetImplemented(nameof(AttemptModelResolved));

    /// <inheritdoc/>
    public void AttemptRouteResolved(
        TaskNode task, int attempt, string runner, string model, string? tier, string? requestedTier) =>
        throw NotYetImplemented(nameof(AttemptRouteResolved));

    /// <inheritdoc/>
    public void AttemptFinished(TaskNode task, int attempt, Journal.AttemptOutcome outcome) =>
        throw NotYetImplemented(nameof(AttemptFinished));

    /// <inheritdoc/>
    public void TaskFinished(TaskResult result) => throw NotYetImplemented(nameof(TaskFinished));

    /// <inheritdoc/>
    public void GuardrailFinished(TaskNode task, GuardrailResult result) =>
        throw NotYetImplemented(nameof(GuardrailFinished));

    /// <inheritdoc/>
    public void PlanHashMismatch(string previousPlanHash) =>
        throw NotYetImplemented(nameof(PlanHashMismatch));

    /// <inheritdoc/>
    public void ParallelismClampedNoProvider(int requested) =>
        throw NotYetImplemented(nameof(ParallelismClampedNoProvider));

    /// <inheritdoc/>
    public void CleanupFailed(string owner, Exception error) =>
        throw NotYetImplemented(nameof(CleanupFailed));

    /// <inheritdoc/>
    public void PromptPaused(TaskNode task, string reason, TimeSpan backoff, int pauseCount) =>
        throw NotYetImplemented(nameof(PromptPaused));

    /// <inheritdoc/>
    public void OutOfScopeStripped(TaskNode task, IReadOnlyList<WriteScopeOffense> stripped) =>
        throw NotYetImplemented(nameof(OutOfScopeStripped));

    /// <inheritdoc/>
    public void DecisionRecorded(DecisionEntry entry) =>
        throw NotYetImplemented(nameof(DecisionRecorded));

    /// <inheritdoc/>
    public void VerifierAdvisoryFound(string taskId, string finding) =>
        throw NotYetImplemented(nameof(VerifierAdvisoryFound));

    /// <inheritdoc/>
    public void OverwatchNoVerdict(string taskId, string reason) =>
        throw NotYetImplemented(nameof(OverwatchNoVerdict));

    /// <inheritdoc/>
    public void WaveStarting(Model.WaveNode wave, int index, int total) =>
        throw NotYetImplemented(nameof(WaveStarting));

    /// <inheritdoc/>
    public void WaveFinished(Model.WaveNode wave, Journal.WaveStatus status, bool skipped) =>
        throw NotYetImplemented(nameof(WaveFinished));

    /// <inheritdoc/>
    public void WaveGateFinished(
        Model.WaveNode wave, bool isEntryGate, IReadOnlyList<Journal.PlanPreflightCheck> checks) =>
        throw NotYetImplemented(nameof(WaveGateFinished));

    /// <inheritdoc/>
    public void WaveBreakdownStarting(WaveBreakdownContext context) =>
        throw NotYetImplemented(nameof(WaveBreakdownStarting));

    /// <inheritdoc/>
    public void WaveBreakdownFinished(
        WaveBreakdownContext context, TimeSpan elapsed, int authoredTaskCount, string? failureKind,
        Model.WaveNode? authoredWave) =>
        throw NotYetImplemented(nameof(WaveBreakdownFinished));
}
