using Guardrails.Core.Model;

namespace Guardrails.Core.Execution;

/// <summary>
/// One exception an observer threw from a callback, as <see cref="FaultIsolatingObserver"/> reports it (#803).
/// </summary>
/// <param name="Observer">The isolated observer's name, as the composition root gave it.</param>
/// <param name="Callback">The <see cref="IRunObserver"/> member that threw.</param>
/// <param name="Error">The exception.</param>
/// <param name="FaultCount">How many faults this observer has now thrown, across every callback.</param>
/// <param name="Disabled">True on the fault that crossed the limit: the observer is not called again this run.</param>
public sealed record ObserverFault(string Observer, string Callback, Exception Error, int FaultCount, bool Disabled);

/// <summary>
/// Isolates ONE <see cref="IRunObserver"/> so an exception it throws never reaches the harness (#803).
///
/// <para><b>Why.</b> Observers are display and telemetry: the live table, <c>--no-ui</c> output, the on-the-fly
/// diagram and log site, the event stream, the webhook. A rendering bug, a full disk under <c>logs/</c>, or a
/// Spectre markup edge case used to propagate into the Scheduler and abort a healthy run as an infrastructure
/// fault — a DISPLAY defect producing a RUN outcome. Every callback is now caught here, reported once through the
/// fault sink, and the run continues.</para>
///
/// <para><b>Per observer, not per chain.</b> The CLI's observers are a decorator chain, each link forwarding to the
/// next. Wrapping only the head would let one faulting link hide every event from the links after it, so the
/// composition root wraps EACH link. A decorator link forwards before doing its own work, so a fault in a link's
/// own work never stops the event reaching the links after it; and a fault in a later link is caught by that
/// link's own isolator before it can travel back up.</para>
///
/// <para><b>After <see cref="MaxFaults"/> faults</b> the observer is disabled for the rest of the run. Calls then go
/// to <c>whenDisabled</c>: for a decorator link that is the isolated link AFTER it, so disabling one link never
/// cuts the chain; for the last link, nothing.</para>
///
/// <para><b>What this does NOT cover.</b> The journal is written by the Scheduler and executor, never by an
/// observer, so journal writes still fail loudly: they are the run's record, not a view of it.</para>
///
/// <para>Every member is declared explicitly, including those with an empty interface default: a decorator that
/// inherits a default body silently swallows that event (the <c>ObserverForwardingSweepTests</c> contract).</para>
/// </summary>
public sealed class FaultIsolatingObserver : IRunObserver
{
    /// <summary>Faults an observer may throw before it is disabled for the rest of the run.</summary>
    public const int MaxFaults = 3;

    private readonly IRunObserver _target;
    private readonly IRunObserver _whenDisabled;
    private readonly Action<ObserverFault> _onFault;
    private int _faults;

    /// <param name="target">The observer to isolate.</param>
    /// <param name="name">A name for it in fault reports, such as <c>live-table</c> or <c>event-stream</c>.</param>
    /// <param name="onFault">Receives each fault. Must not throw; a throw from it is discarded.</param>
    /// <param name="whenDisabled">
    /// Where calls go once <paramref name="target"/> is disabled. For a decorator, the (isolated) observer it wraps,
    /// so the rest of the chain still hears every event. Null = <see cref="IRunObserver.Null"/>.
    /// </param>
    public FaultIsolatingObserver(
        IRunObserver target, string name, Action<ObserverFault> onFault, IRunObserver? whenDisabled = null)
    {
        _target = target;
        Name = name;
        _onFault = onFault;
        _whenDisabled = whenDisabled ?? IRunObserver.Null;
    }

    /// <summary>The name fault reports carry.</summary>
    public string Name { get; }

    /// <summary>True once the observer has thrown <see cref="MaxFaults"/> times.</summary>
    public bool Disabled => Volatile.Read(ref _faults) >= MaxFaults;

    /// <summary>
    /// Wrap <paramref name="observer"/> unless it is already isolated, so a caller can apply isolation at its own
    /// boundary without doubling it.
    /// </summary>
    public static IRunObserver Wrap(IRunObserver observer, string name, Action<ObserverFault> onFault) =>
        observer is FaultIsolatingObserver ? observer : new FaultIsolatingObserver(observer, name, onFault);

    private void Guard(string callback, Action<IRunObserver> call)
    {
        if (Disabled)
        {
            call(_whenDisabled);
            return;
        }

        try
        {
            call(_target);
        }
        catch (Exception ex)
        {
            int count = Interlocked.Increment(ref _faults);
            if (count > MaxFaults)
            {
                // Another thread's fault already disabled it; that one was reported.
                return;
            }

            try
            {
                _onFault(new ObserverFault(Name, callback, ex, count, Disabled: count == MaxFaults));
            }
            catch (Exception)
            {
                // The sink is best-effort by contract: reporting a display fault must not become a run fault.
            }
        }
    }

    /// <inheritdoc/>
    public void TaskStarting(TaskNode task) => Guard(nameof(TaskStarting), o => o.TaskStarting(task));

    /// <inheritdoc/>
    public void AttemptStarting(TaskNode task, int attempt, int budget, int attemptNumber) =>
        Guard(nameof(AttemptStarting), o => o.AttemptStarting(task, attempt, budget, attemptNumber));

    /// <inheritdoc/>
    public void AttemptModelResolved(TaskNode task, int attempt, string model, string? requestedModel) =>
        Guard(nameof(AttemptModelResolved), o => o.AttemptModelResolved(task, attempt, model, requestedModel));

    /// <inheritdoc/>
    public void AttemptRouteResolved(
        TaskNode task, int attempt, string runner, string model, string? tier, string? requestedTier) =>
        Guard(nameof(AttemptRouteResolved), o => o.AttemptRouteResolved(task, attempt, runner, model, tier, requestedTier));

    /// <inheritdoc/>
    public void AttemptFinished(TaskNode task, Journal.AttemptRecord record) =>
        Guard(nameof(AttemptFinished), o => o.AttemptFinished(task, record));

    /// <inheritdoc/>
    public void AttemptStalled(
        TaskNode task, int attempt, TimeSpan bound, TimeSpan silentFor, int suspendsObserved,
        string? contextManagement, string? contextManagementDetail) =>
        Guard(nameof(AttemptStalled), o => o.AttemptStalled(
            task, attempt, bound, silentFor, suspendsObserved, contextManagement, contextManagementDetail));

    /// <inheritdoc/>
    public void RunFinished(int? exitCode, string? faultKind) =>
        Guard(nameof(RunFinished), o => o.RunFinished(exitCode, faultKind));

    /// <inheritdoc/>
    public void TaskFinished(TaskResult result) => Guard(nameof(TaskFinished), o => o.TaskFinished(result));

    /// <inheritdoc/>
    public void GuardrailFinished(TaskNode task, GuardrailResult result) =>
        Guard(nameof(GuardrailFinished), o => o.GuardrailFinished(task, result));

    /// <inheritdoc/>
    public void PlanHashMismatch(string previousPlanHash) =>
        Guard(nameof(PlanHashMismatch), o => o.PlanHashMismatch(previousPlanHash));

    /// <inheritdoc/>
    public void ParallelismClampedNoProvider(int requested) =>
        Guard(nameof(ParallelismClampedNoProvider), o => o.ParallelismClampedNoProvider(requested));

    /// <inheritdoc/>
    public void CleanupFailed(string owner, Exception error) =>
        Guard(nameof(CleanupFailed), o => o.CleanupFailed(owner, error));

    /// <inheritdoc/>
    public void PromptPaused(TaskNode task, string reason, TimeSpan backoff, int pauseCount) =>
        Guard(nameof(PromptPaused), o => o.PromptPaused(task, reason, backoff, pauseCount));

    /// <inheritdoc/>
    public void OutOfScopeStripped(TaskNode task, IReadOnlyList<WriteScopeOffense> stripped) =>
        Guard(nameof(OutOfScopeStripped), o => o.OutOfScopeStripped(task, stripped));

    /// <inheritdoc/>
    public void DecisionRecorded(DecisionEntry entry) => Guard(nameof(DecisionRecorded), o => o.DecisionRecorded(entry));

    /// <inheritdoc/>
    public void VerifierAdvisoryFound(string taskId, string finding) =>
        Guard(nameof(VerifierAdvisoryFound), o => o.VerifierAdvisoryFound(taskId, finding));

    /// <inheritdoc/>
    public void OverwatchNoVerdict(string taskId, string reason) =>
        Guard(nameof(OverwatchNoVerdict), o => o.OverwatchNoVerdict(taskId, reason));

    /// <inheritdoc/>
    public void WaveStarting(WaveNode wave, int index, int total) =>
        Guard(nameof(WaveStarting), o => o.WaveStarting(wave, index, total));

    /// <inheritdoc/>
    public void WaveFinished(WaveNode wave, Journal.WaveStatus status, bool skipped) =>
        Guard(nameof(WaveFinished), o => o.WaveFinished(wave, status, skipped));

    /// <inheritdoc/>
    public void WaveDelivered(WaveNode wave, Journal.WaveDeliveredRecord delivery) =>
        Guard(nameof(WaveDelivered), o => o.WaveDelivered(wave, delivery));

    /// <inheritdoc/>
    public void WaveGateFinished(WaveNode wave, bool isEntryGate, IReadOnlyList<Journal.PlanPreflightCheck> checks) =>
        Guard(nameof(WaveGateFinished), o => o.WaveGateFinished(wave, isEntryGate, checks));

    /// <inheritdoc/>
    public void WaveBreakdownStarting(WaveBreakdownContext context) =>
        Guard(nameof(WaveBreakdownStarting), o => o.WaveBreakdownStarting(context));

    /// <inheritdoc/>
    public void WaveBreakdownFinished(
        WaveBreakdownContext context, TimeSpan elapsed, int authoredTaskCount, string? failureKind, WaveNode? authoredWave) =>
        Guard(nameof(WaveBreakdownFinished), o => o.WaveBreakdownFinished(context, elapsed, authoredTaskCount, failureKind, authoredWave));

    /// <inheritdoc/>
    public void WaveBreakdownPaused(
        WaveBreakdownContext context, string reason, TimeSpan wait, int probe, DateTimeOffset? resetInstant, TimeSpan waitedSoFar) =>
        Guard(nameof(WaveBreakdownPaused), o => o.WaveBreakdownPaused(context, reason, wait, probe, resetInstant, waitedSoFar));

    /// <inheritdoc/>
    public void TerminalGateStarting(IReadOnlyList<string> checkNames, DateTimeOffset startedAt) =>
        Guard(nameof(TerminalGateStarting), o => o.TerminalGateStarting(checkNames, startedAt));

    /// <inheritdoc/>
    public void TerminalGateFinished(bool passed, IReadOnlyList<string> failedNames) =>
        Guard(nameof(TerminalGateFinished), o => o.TerminalGateFinished(passed, failedNames));

    /// <inheritdoc/>
    public void SuppliedResourcesCommitted(IReadOnlyList<string> paths, string commit, string by) =>
        Guard(nameof(SuppliedResourcesCommitted), o => o.SuppliedResourcesCommitted(paths, commit, by));

    /// <inheritdoc/>
    public void TaskWaitingOnWorktree(TaskNode task, string operation) =>
        Guard(nameof(TaskWaitingOnWorktree), o => o.TaskWaitingOnWorktree(task, operation));
}
