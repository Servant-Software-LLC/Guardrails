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
/// <para>The loop, per doc 12 §4.2: retry the same attempt with backoff (honoring any parsed reset hint)
/// UNTIL either <c>maxAttempts</c> OR the effective wall-clock ceiling —
/// <c>min(totalWaitSeconds, transientPauseBudgetSeconds)</c> — is reached (whichever first). On the transient
/// clearing ⇒ <see cref="BlockerRetryOutcome.Resolved"/>; on a ceiling ⇒
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
    /// The wall-clock ceiling is <c>min(totalWaitSeconds, transientPauseBudgetSeconds)</c> — the blocker's own
    /// ceiling floored by the shipped transient-pause budget (doc 12 §4.2). Backoff TIMING is delegated to the
    /// shipped <see cref="TransientBackoff"/> (the exponential 2s/4s/… schedule and the per-pause budget clamp,
    /// SSOT §9 / issue #115) — this loop only adds the <c>maxAttempts</c> ceiling and the first-pause reset-hint
    /// override on top. The wait is charged against a single cumulative total so the two ceilings compose
    /// (whichever trips first escalates), and the outcome NEVER decrements the task's logic-retry budget.
    /// </remarks>
    public async Task<BlockerRetryResult> RunAsync(
        Func<int, bool> hasCleared,
        TimeSpan? resetHint = null,
        CancellationToken cancellationToken = default)
    {
        // The effective wall-clock bound is the blocker's own totalWaitSeconds ceiling FLOORED by the shipped
        // transient-pause budget — the blocker ceiling never exceeds it (doc 12 §4.2, SSOT §9). i.e. min().
        TimeSpan blockerCeiling = TimeSpan.FromSeconds(_ceilings.TotalWaitSeconds);
        TimeSpan effectiveBound = blockerCeiling < _transientPauseBudget ? blockerCeiling : _transientPauseBudget;

        // The shipped transient discipline drives the exponential backoff timing + per-pause budget clamp.
        // Created lazily for the exponential phase so a first-pause reset hint (which overrides the 2s
        // exponential head) is charged against the bound FIRST, leaving the backoff the exact remaining budget.
        TransientBackoff? backoff = null;
        TimeSpan cumulativeWait = TimeSpan.Zero;

        for (int attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (hasCleared(attempt))
            {
                // The transient cleared within the ceiling — continue as if the blocker never happened (§4.2).
                return Result(BlockerRetryOutcome.Resolved, attempt, cumulativeWait);
            }

            // Not cleared on this re-run. Escalate to class (c) once EITHER ceiling is reached (whichever
            // first, §4.2): the maxAttempts ceiling, OR the effective wall-clock bound. The ledger the
            // escalation carries records the attempts + cumulative wait so the escalation record can report it.
            if (attempt >= _ceilings.MaxAttempts || cumulativeWait >= effectiveBound)
            {
                return Result(BlockerRetryOutcome.Escalate, attempt, cumulativeWait);
            }

            // Back off before the next re-run. The FIRST backoff honors any parsed reset hint (wait until the
            // limit resets), clamped to the remaining bound; every subsequent backoff uses the shipped
            // exponential schedule (2s, 4s, …), which TransientBackoff itself clamps to the remaining bound.
            TimeSpan waited;
            if (attempt == 1 && resetHint is { } hint)
            {
                TimeSpan remaining = effectiveBound - cumulativeWait;
                waited = hint < remaining ? hint : remaining;
                await _delay(waited).ConfigureAwait(false);
            }
            else
            {
                backoff ??= new TransientBackoff(effectiveBound - cumulativeWait, (d, _) => _delay(d));
                waited = await backoff.PauseAsync(cancellationToken).ConfigureAwait(false);
            }

            cumulativeWait += waited;
        }
    }

    private static BlockerRetryResult Result(BlockerRetryOutcome outcome, int attempts, TimeSpan cumulativeWait) =>
        new()
        {
            Outcome = outcome,
            Ledger = new BlockerRetryLedger
            {
                Attempts = attempts,
                CumulativeWait = cumulativeWait,
                // ALWAYS false: a transient is not a logic failure, so class (b) never decrements the task's
                // logic-retry budget — even when it escalates to class (c) (§4.2, the shipped transient rule).
                ConsumedLogicRetry = false,
            },
        };
}
