using System.Globalization;
using Guardrails.Core.Model;
using Guardrails.Core.Prompts;

namespace Guardrails.Core.Execution;

/// <summary>
/// The #817 diagnosis: a FAILED attempt (max-turns, timeout, stall or a generic action failure) whose context was
/// compacted so often, relative to the turns it took, that re-reading what each compaction dropped is what spent its
/// budget. The cause is then the context window, not the turn cap or the clock, so the retry is told CONTEXT THRASH
/// instead of "turn budget", and the harness does not raise the next attempt's turn budget or extend its clock.
/// </summary>
/// <param name="Compactions">The attempt's compaction episodes (<see cref="CompactionCounts.Compactions"/>).</param>
/// <param name="Failures">How many of them failed.</param>
/// <param name="Turns">The runner's turn count, or null when it reported none (a timeout or stall ends with no result).</param>
public sealed record ContextThrash(int Compactions, int Failures, int? Turns)
{
    /// <summary>
    /// The absolute floor: fewer compactions than this are never thrash, however few turns the attempt took. Three is
    /// the smallest count that means the working set refilled the window more than once after it was first compacted.
    /// </summary>
    public const int MinCompactions = 3;

    /// <summary>
    /// The ratio: thrash when the attempt compacted at least once per this many turns
    /// (<c>compactions × TurnsPerCompaction ≥ turns</c>). Twelve comes from the #817 dogfood streams, counted in
    /// episodes: the thrashing attempts compacted 9 times in 76 turns (max-turns) and 8 in 81, while attempts that
    /// worked normally compacted 2 in 36 and 3 in 51. When the turn count is unknown the floor alone decides.
    /// </summary>
    public const int TurnsPerCompaction = 12;

    /// <summary>
    /// The verdict for a finished action, or null when it is not thrash: the action succeeded, its failure has its own
    /// more specific diagnosis (an output cap, a context overflow, #800's context exhaustion, a transient pause, a
    /// runner-configuration fault), or it compacted too rarely for the thresholds above.
    /// </summary>
    internal static ContextThrash? Diagnose(ActionRun action)
    {
        if (action.Succeeded
            || action.FailureKind is not (PromptFailureKind.MaxTurns or PromptFailureKind.Timeout
                or PromptFailureKind.Stalled or PromptFailureKind.Error)
            || action.Compactions is not { } counts)
        {
            return null;
        }

        return IsThrash(counts.Compactions, action.Turns)
            ? new ContextThrash(counts.Compactions, counts.Failures, action.Turns)
            : null;
    }

    /// <summary>The threshold alone: at least <see cref="MinCompactions"/>, and one per <see cref="TurnsPerCompaction"/> turns when turns are known.</summary>
    public static bool IsThrash(int compactions, int? turns) =>
        compactions >= MinCompactions
        && (turns is not { } t || (long)compactions * TurnsPerCompaction >= t);

    /// <summary>The counts in words: <c>9 compactions in 76 turns, 1 failed</c>.</summary>
    public string Describe()
    {
        string text = string.Create(CultureInfo.InvariantCulture, $"{Compactions} compactions");
        if (Turns is { } turns)
        {
            text += string.Create(CultureInfo.InvariantCulture, $" in {turns} turns");
        }

        return Failures > 0
            ? text + string.Create(CultureInfo.InvariantCulture, $", {Failures} failed")
            : text;
    }

    /// <summary>
    /// The attempt summary's clause (the line that becomes the needs-human reason on the final attempt), naming the
    /// cause and the operator's levers for <paramref name="block"/>.
    /// </summary>
    public string SummaryClause(PromptRunnerConfig? block) =>
        $"CONTEXT THRASH ({Describe()}): the context window is too small for this task's working set, so raising " +
        $"maxTurns or the timeout will not help and neither was raised. {RetryPolicy.ContextLevers(block)}";
}
