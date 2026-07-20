// The ceilings come from the shipped autonomy-dial config record Guardrails.Core.Model.BlockerRetry;
// aliased here because THIS file defines the class-(b) EXECUTOR (a different type) with the same short
// name in the Execution namespace. The alias keeps the two unambiguous without a using on the Model
// namespace (which would collide with the executor's own name).
using BlockerRetryConfig = Guardrails.Core.Model.BlockerRetry;

namespace Guardrails.Core.Execution;

/// <summary>
/// The outcome of a class-(b) bounded wait/backoff (issue #361 Phase 3, doc 12 §4.2).
/// </summary>
public enum BlockerRetryOutcome
{
    /// <summary>The transient cleared within the ceiling — continue as if the blocker never happened (doc 12 §4.2).</summary>
    Resolved,

    /// <summary>
    /// A ceiling tripped — <c>maxAttempts</c> OR <c>totalWaitSeconds</c> (whichever first), the latter floored
    /// by the shipped transient-pause budget — so the blocker escalates to class (c): halt-and-escalate
    /// unconditionally, carrying the retry ledger (doc 12 §4.2).
    /// </summary>
    Escalate
}

/// <summary>
/// The retry ledger for a class-(b) blocker (doc 12 §4.2/§6.1): how many attempts were made and how long was
/// cumulatively waited before the transient resolved or the ceiling escalated. This is the forensic detail a
/// <c>decisions[]</c> entry / <c>autonomy.jsonl</c> record carries (<c>blockerAttempts</c> /
/// <c>blockerWaitedSeconds</c>, doc 12 §6.2).
/// </summary>
public sealed record BlockerRetryLedger
{
    /// <summary>How many re-run attempts were made before resolution or escalation.</summary>
    public required int Attempts { get; init; }

    /// <summary>Cumulative wall-clock time waited (summed backoffs) before resolution or escalation.</summary>
    public required TimeSpan CumulativeWait { get; init; }

    /// <summary>
    /// ALWAYS <c>false</c>: a transient is not a logic failure, so a class-(b) blocker NEVER decrements the
    /// task's retry budget (doc 12 §4.2 — the shipped transient-pause rule). Modelled explicitly so the
    /// caller (the task executor) has a positive signal that this outcome consumed no logic retry.
    /// </summary>
    public bool ConsumedLogicRetry { get; init; }
}

/// <summary>
/// The result of running the class-(b) bounded wait/backoff: the terminal <see cref="Outcome"/> plus the
/// <see cref="Ledger"/> that BOTH outcomes carry.
/// </summary>
public sealed record BlockerRetryResult
{
    /// <summary>Whether the transient resolved within the ceiling or the ceiling escalated it to class (c).</summary>
    public required BlockerRetryOutcome Outcome { get; init; }

    /// <summary>The attempts + cumulative-wait ledger (doc 12 §4.2).</summary>
    public required BlockerRetryLedger Ledger { get; init; }
}

/// <summary>
/// The class-(b) bounded wait/backoff for a retryable hard blocker (issue #361 Phase 3, doc 12 §4.2). It
/// REUSES the shipped transient-pause discipline — the bounded exponential backoff of
/// <see cref="TransientBackoff"/> and the cumulative wall-clock <c>transientPauseBudgetSeconds</c> floor
/// (<see cref="Model.RunConfig.TransientPauseBudgetSeconds"/>, SSOT §9) — and bounds it FURTHER with the
/// autonomy-dial ceiling <see cref="Model.BlockerRetry"/> (<c>maxAttempts</c> / <c>totalWaitSeconds</c>).
///
/// <para>The intended loop (NOT implemented here — this is a TDD-red stub) is, per doc 12 §4.2: retry the
/// same attempt with backoff (honoring any parsed reset hint) UNTIL either <c>maxAttempts</c> OR the
/// effective wall-clock ceiling — <c>min(totalWaitSeconds, transientPauseBudgetSeconds)</c> — is reached
/// (whichever first). On the transient clearing ⇒ <see cref="BlockerRetryOutcome.Resolved"/>; on a ceiling ⇒
/// <see cref="BlockerRetryOutcome.Escalate"/> to class (c). Either way the retry ledger records the attempts
/// and cumulative wait, and it NEVER consumes the task's retry budget.</para>
///
/// <para>The wait is delegated to an injected <c>delay</c> seam so tests gate it deterministically (no real
/// sleeps), exactly as <see cref="TransientBackoff"/> does; production passes a real
/// <see cref="Task.Delay(TimeSpan)"/>.</para>
/// </summary>
public sealed class BlockerRetry
{
    private readonly BlockerRetryConfig _ceilings;
    private readonly TimeSpan _transientPauseBudget;
    private readonly Func<TimeSpan, Task> _delay;

    /// <param name="ceilings">
    /// The autonomy-dial ceilings (<c>maxAttempts</c> / <c>totalWaitSeconds</c>) from
    /// <see cref="Model.AutonomyConfig.BlockerRetry"/>.
    /// </param>
    /// <param name="transientPauseBudget">
    /// The shipped cumulative wall-clock pause budget (<see cref="Model.RunConfig.TransientPauseBudgetSeconds"/>),
    /// which floors the effective wait ceiling — the blocker ceiling never exceeds it (doc 12 §4.2).
    /// </param>
    /// <param name="delay">Injected wait; production passes <see cref="Task.Delay(TimeSpan)"/>.</param>
    public BlockerRetry(BlockerRetryConfig ceilings, TimeSpan transientPauseBudget, Func<TimeSpan, Task> delay)
    {
        _ceilings = ceilings;
        _transientPauseBudget = transientPauseBudget;
        _delay = delay;
    }

    /// <summary>
    /// Run the bounded wait/backoff for a class-(b) transient. <paramref name="hasCleared"/> is the re-run
    /// probe: given the 1-based attempt number it returns whether the transient has cleared on that re-run.
    /// <paramref name="resetHint"/>, when present, is the parsed reset-time wait a backoff honors instead of
    /// the exponential schedule (the advisory hint surfaced by
    /// <see cref="Prompts.ClaudeSignalClassifier.ExtractResetHint"/>).
    /// </summary>
    /// <remarks>
    /// TDD-RED STUB: the real class-(b) loop (doc 12 §4.2) is deliberately NOT implemented yet. The delay
    /// seam and both ceilings are wired so the eventual implementation drops in behind this signature; every
    /// seam is referenced below so the stub compiles cleanly under <c>TreatWarningsAsErrors</c> without
    /// pretending to do the work.
    /// </remarks>
    public Task<BlockerRetryResult> RunAsync(
        Func<int, bool> hasCleared,
        TimeSpan? resetHint = null,
        CancellationToken cancellationToken = default)
    {
        _ = (_ceilings, _transientPauseBudget, _delay, hasCleared, resetHint, cancellationToken);
        throw new NotImplementedException(
            "BlockerRetry.RunAsync is a TDD stub — the class-(b) bounded wait/backoff loop (doc 12 §4.2) is not implemented yet.");
    }
}
