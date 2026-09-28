namespace Guardrails.Core.Execution;

/// <summary>
/// The silence bound for a task ACTION's prompt session (#811, SSOT §9): how long the session may produce no
/// PROGRESS line before it is killed as stalled. Before #811 only breakdowns had one
/// (<see cref="WaveBreakdownInvoker.BreakdownStallBound"/>); a task action relied on its whole-attempt timeout alone,
/// so a session wedged in a compaction that never returned sat for the full timeout.
///
/// <para><b>The derived default is <c>clamp(timeout / 3, 15m, 20m)</c></b>, and it is none at all when that is not
/// shorter than the timeout (the timeout then already bounds silence, and a second bound that can never fire first
/// is noise in every summary).</para>
/// <list type="bullet">
///   <item><b>The 15-minute floor is evidence, not taste.</b> A tool call is SILENT on the stream from its
///         <c>tool_use</c> to its <c>tool_result</c>, and one <c>dotnet test</c> measured 10m44s silent (#504). A
///         5-minute floor would kill that healthy call; 15 minutes clears it with about 40% margin.</item>
///   <item><b>The 20-minute ceiling</b> is the breakdown bound, which has the same evidence behind it: past it, a
///         longer action timeout buys no more patience for silence, only for work.</item>
///   <item><b><c>timeout / 3</c></b> keeps a stall kill well inside the attempt, so a wedged session gives back at
///         least two thirds of its budget, where the timeout gave back none.</item>
/// </list>
///
/// <para><b>The timeout-extension multiplier (#119).</b> The derived bound is computed from the EXTENDED timeout,
/// so the "no bound unless shorter than the timeout" rule always compares against the clock that actually runs. A
/// configured bound is an absolute the operator chose, so it is not scaled. A stall is recorded as an ordinary
/// action failure, never as a timeout, so it does not extend the next attempt's clock: more wall time does not help
/// a session that has stopped producing.</para>
///
/// <para><b>Sleep does not count as silence.</b> The bound is policed by <see cref="Prompts.StallWatch"/>, whose
/// poll-gap check resets the silence window after a host suspend (#517). There is no suspend-excluding clock in
/// this codebase to use instead; see <see cref="Prompts.StallWatch"/> for why the wall clock is kept on purpose.</para>
/// </summary>
internal static class ActionStallBound
{
    /// <summary>The smallest derived bound (the measured 10m44s silent tool call, with margin).</summary>
    internal static readonly TimeSpan Floor = TimeSpan.FromMinutes(15);

    /// <summary>The largest derived bound (the breakdown bound's value).</summary>
    internal static readonly TimeSpan Ceiling = TimeSpan.FromMinutes(20);

    /// <summary>
    /// The bound for one action attempt, or null for none.
    /// </summary>
    /// <param name="configuredSeconds">
    /// The dispatched runner block's <c>stallTimeoutSeconds</c>: null derives the bound, 0 disables it, a positive
    /// value is used as given. A negative value is rejected at validation (GR2088) and treated as absent here.
    /// </param>
    /// <param name="actionTimeout">The attempt's timeout AFTER the #119 extension.</param>
    internal static TimeSpan? Resolve(int? configuredSeconds, TimeSpan actionTimeout)
    {
        TimeSpan bound;
        switch (configuredSeconds)
        {
            case 0:
                return null;

            case > 0:
                bound = TimeSpan.FromSeconds(configuredSeconds.Value);
                break;

            default:
                long third = actionTimeout.Ticks / 3;
                bound = TimeSpan.FromTicks(Math.Clamp(third, Floor.Ticks, Ceiling.Ticks));
                break;
        }

        return bound < actionTimeout ? bound : null;
    }
}
