using System.Globalization;
using Guardrails.Core.Model;
using Guardrails.Core.Prompts;

namespace Guardrails.Core.Execution;

/// <summary>
/// The #817 diagnosis: a FAILED attempt (max-turns, timeout, stall or a generic action failure) whose context was
/// compacted so often, relative to the turns it took, that re-reading what each compaction dropped is what spent its
/// budget. The cause is then the context window, not the turn cap or the clock, so the retry is told CONTEXT THRASH
/// instead of "turn budget", the harness does not raise the next attempt's turn budget or extend its clock, and a
/// second consecutive attempt under context pressure (this, or #800's context exhaustion) settles needs-human.
/// </summary>
/// <param name="Compactions">The attempt's compaction episodes (<see cref="CompactionCounts.Compactions"/>).</param>
/// <param name="Failures">How many of them failed.</param>
/// <param name="Turns">The turn count the ratio was applied to, or null when neither a reported nor an estimated one exists.</param>
/// <param name="TurnsEstimated">
/// True when the runner reported no turn count: <paramref name="Turns"/> is then <see cref="CompactionCounts.EstimatedTurns"/>
/// (a lower bound), or null when the floor decided alone. Such a verdict is ADVISORY — its text only, never a budget
/// freeze or a count toward the repeat halt.
/// </param>
/// <param name="Kind">How the attempt ended, which decides what the text says was (not) raised.</param>
public sealed record ContextThrash(int Compactions, int Failures, int? Turns, bool TurnsEstimated, PromptFailureKind Kind)
{
    /// <summary>
    /// The absolute floor: fewer compactions than this are never thrash, however few turns the attempt took. Three is
    /// the smallest count that means the working set refilled the window more than once after it was first compacted.
    /// </summary>
    public const int MinCompactions = 3;

    /// <summary>
    /// The ratio: thrash when the attempt compacted at least once per this many turns
    /// (<c>compactions × TurnsPerCompaction ≥ turns</c>). Calibrated on few samples from ONE plan on a 64K window (the
    /// #817 dogfood): the attempt that ran out of turns after 9 compactions in 76 turns must be caught; one that compacted
    /// twice in 36 turns and succeeded must not be. When the runner reported no turns, the ratio is applied to the
    /// estimate (<see cref="CompactionCounts.EstimatedTurns"/>); only when that is missing too does the floor decide alone.
    /// The estimate is a LOWER bound (0.35–1.0× the reported count on the dogfood streams), which pushes toward thrash,
    /// so a verdict on it is advisory: it changes the feedback text only, never the budget or the repeat count.
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

        int? turns = action.Turns ?? counts.EstimatedTurns;
        return IsThrash(counts.Compactions, turns)
            ? new ContextThrash(
                counts.Compactions, counts.Failures, turns, TurnsEstimated: action.Turns is null, action.FailureKind)
            : null;
    }

    /// <summary>The threshold alone: at least <see cref="MinCompactions"/>, and one per <see cref="TurnsPerCompaction"/> turns when turns are known.</summary>
    public static bool IsThrash(int compactions, int? turns) =>
        compactions >= MinCompactions
        && (turns is not { } t || (long)compactions * TurnsPerCompaction >= t);

    /// <summary>The counts in words: <c>9 compactions in 76 turns, 1 failed</c>, or <c>in about 30 turns (estimated)</c>.</summary>
    public string Describe()
    {
        string text = string.Create(CultureInfo.InvariantCulture, $"{Compactions} compactions");
        if (Turns is { } turns)
        {
            text += TurnsEstimated
                ? string.Create(CultureInfo.InvariantCulture, $" in about {turns} turns (estimated)")
                : string.Create(CultureInfo.InvariantCulture, $" in {turns} turns");
        }

        return Failures > 0
            ? text + string.Create(CultureInfo.InvariantCulture, $", {Failures} failed")
            : text;
    }

    /// <summary>
    /// What more budget would not have fixed, naming only the budget this kind of stop actually hit: the turn cap for a
    /// max-turns stop, the clock for a timeout. "Further" because an earlier, non-thrash attempt may already have raised it.
    /// A verdict on ESTIMATED turns is advisory (the estimate is a lower bound): the budget is raised as usual, and the
    /// sentence says so.
    /// </summary>
    public string BudgetSentence() => (Kind, TurnsEstimated) switch
    {
        (PromptFailureKind.MaxTurns, false) => "raising maxTurns will not help, so it was not raised further",
        (PromptFailureKind.Timeout, false) => "extending the timeout will not help, so it was not extended further",
        (PromptFailureKind.MaxTurns, true) =>
            "raising maxTurns is unlikely to help, but it was still raised because the turn count is only an estimate",
        (PromptFailureKind.Timeout, true) =>
            "extending the timeout is unlikely to help, but it was still extended because the turn count is only an estimate",
        _ => "more turns or more time will not help"
    };

    /// <summary>
    /// The attempt summary's clause (the line that becomes the needs-human reason on the final attempt), naming the
    /// cause and the operator's levers for <paramref name="block"/>.
    /// </summary>
    public string SummaryClause(PromptRunnerConfig? block) =>
        $"CONTEXT THRASH ({Describe()}): the context window is too small for this task's working set; " +
        $"{BudgetSentence()}. {RetryPolicy.ContextLevers(block)}";
}
