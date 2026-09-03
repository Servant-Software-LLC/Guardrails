using Guardrails.Core.Model;

namespace Guardrails.Core.Execution;

/// <summary>
/// The SECOND projection off the <see cref="IRunObserver"/> seam (plan 34 §5) — the render-FIDELITY
/// stream <c>guardrails attach</c> replays into a real <see cref="IRunObserver"/> (the live table). It is
/// deliberately NOT the same file as <c>events.jsonl</c> (<see cref="RunEventStream"/>'s job): that stream
/// is semantic and low-frequency for a supervising agent, while a renderer needs every call verbatim,
/// including the live-only ones (elapsed time, the guardrail currently executing) a filtered agent stream
/// would starve.
///
/// <para>A DECORATOR, wrapping the real <paramref name="inner"/> observer of a run. Every call is:</para>
/// <list type="number">
///   <item>appended as one JSON line to <c>observer.jsonl</c> in the given directory, naming the member and
///     carrying its arguments — so reading the file back reproduces the exact call sequence, in order, which
///     is the property <c>guardrails attach</c> depends on to drive a REAL <see cref="LiveRunObserver"/> in a
///     second terminal (not a reimplementation of it);</item>
///   <item>forwarded to <paramref name="inner"/> — this decorator must never be the run's only observer.</item>
/// </list>
///
/// <para>Every member is declared EXPLICITLY, not left to the interface's default no-op body: §3 of plan 34
/// names the exact trap — <c>IRunObserver</c>'s default-implemented members mean a decorator that omits one
/// silently swallows that event in every mode, the same defect already fixed four times over
/// (<see cref="IRunObserver.VerifierAdvisoryFound"/>, <see cref="IRunObserver.AttemptModelResolved"/>,
/// <see cref="IRunObserver.WaveGateFinished"/>, <see cref="IRunObserver.WaveBreakdownStarting"/>). A
/// projection whose entire purpose is "record every observed call" cannot itself rely on that default body,
/// or the "every" is false from the day it ships.</para>
///
/// <para><b>Stub — every member throws <see cref="NotImplementedException"/>.</b> The recording + forwarding
/// logic lands in task 08; this task only cuts the seam and the failing tests
/// (<c>ObserverProjectionTests</c>) that pin the behaviour above.</para>
/// </summary>
public sealed class ObserverProjection : IRunObserver
{
    private readonly IRunObserver _inner;
    private readonly string _directory;

    /// <param name="inner">The real observer (live or console) every call is forwarded to, verbatim.</param>
    /// <param name="directory">
    /// The run's <c>logs/&lt;runId&gt;/</c> tree — <c>observer.jsonl</c> is appended to inside it.
    /// </param>
    public ObserverProjection(IRunObserver inner, string directory)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _directory = directory ?? throw new ArgumentNullException(nameof(directory));
    }

    public void TaskStarting(TaskNode task) => throw new NotImplementedException();

    public void AttemptStarting(TaskNode task, int attempt, int budget) => throw new NotImplementedException();

    public void AttemptModelResolved(TaskNode task, int attempt, string model, string? requestedModel) =>
        throw new NotImplementedException();

    public void AttemptRouteResolved(
        TaskNode task, int attempt, string runner, string model, string? tier, string? requestedTier) =>
        throw new NotImplementedException();

    public void AttemptFinished(TaskNode task, int attempt, Journal.AttemptOutcome outcome) =>
        throw new NotImplementedException();

    public void TaskFinished(TaskResult result) => throw new NotImplementedException();

    public void GuardrailFinished(TaskNode task, GuardrailResult result) => throw new NotImplementedException();

    public void PlanHashMismatch(string previousPlanHash) => throw new NotImplementedException();

    public void ParallelismClampedNoProvider(int requested) => throw new NotImplementedException();

    public void CleanupFailed(string owner, Exception error) => throw new NotImplementedException();

    public void PromptPaused(TaskNode task, string reason, TimeSpan backoff, int pauseCount) =>
        throw new NotImplementedException();

    public void OutOfScopeStripped(TaskNode task, IReadOnlyList<WriteScopeOffense> stripped) =>
        throw new NotImplementedException();

    public void DecisionRecorded(DecisionEntry entry) => throw new NotImplementedException();

    public void VerifierAdvisoryFound(string taskId, string finding) => throw new NotImplementedException();

    public void OverwatchNoVerdict(string taskId, string reason) => throw new NotImplementedException();

    public void WaveStarting(WaveNode wave, int index, int total) => throw new NotImplementedException();

    public void WaveFinished(WaveNode wave, Journal.WaveStatus status, bool skipped) =>
        throw new NotImplementedException();

    public void WaveGateFinished(
        WaveNode wave, bool isEntryGate, IReadOnlyList<Journal.PlanPreflightCheck> checks) =>
        throw new NotImplementedException();

    public void WaveBreakdownStarting(WaveBreakdownContext context) => throw new NotImplementedException();

    public void WaveBreakdownFinished(
        WaveBreakdownContext context, TimeSpan elapsed, int authoredTaskCount, string? failureKind,
        WaveNode? authoredWave) =>
        throw new NotImplementedException();
}
