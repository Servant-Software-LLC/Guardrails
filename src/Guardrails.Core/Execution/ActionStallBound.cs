using Guardrails.Core.Model;

namespace Guardrails.Core.Execution;

/// <summary>
/// The silence bound for a task ACTION's prompt session (#811, SSOT §9): how long the session may produce no
/// PROGRESS line (<see cref="Prompts.StreamProgress"/>) before it is killed as stalled. Before #811 only breakdowns had
/// one (<see cref="WaveBreakdownInvoker.BreakdownStallBound"/>); a task action relied on its whole-attempt timeout
/// alone, so a session wedged in a compaction that never returned sat for the full timeout.
///
/// <para><b>What is still legitimately silent.</b> A running tool call is NOT: Claude Code sends a
/// <c>tool_progress</c> heartbeat about every 30 s, and <c>thinking_tokens</c> lines while the model reasons. What
/// remains silent is the backend reading a prompt before the first token (prefill), a reply generated without a
/// thinking count, a compaction (status lines only), and — on Cursor, whose <c>tool_call</c> events are only
/// <c>started</c>/<c>completed</c> — a long tool call. The bound must clear those.</para>
///
/// <para><b>Two derived bands</b>, both disabled when the result is not shorter than the timeout (the timeout then
/// already bounds silence, and a second bound that can never fire first is noise in every summary):</para>
/// <list type="bullet">
///   <item><b>Cloud blocks: <c>clamp(timeout / 3, 15m, 20m)</c>.</b> Prefill on a hosted model is seconds; 15 minutes
///         clears a silent Cursor tool call such as the 10m44s <c>dotnet test</c> measured in #504, and 20 minutes is the
///         breakdown bound. <c>timeout / 3</c> keeps a stall kill well inside the attempt.</item>
///   <item><b>Local backends — a claude gateway block (<c>baseUrl</c>) or an <c>openai-compat</c> block:
///         <c>clamp(timeout / 2, 30m, 60m)</c>.</b> A local server reads a prompt at roughly 150 tokens/s at large
///         contexts, so a 65,536-token prompt is about 7 minutes before the first token, and a compaction re-reads the
///         whole context before it prints anything.</item>
/// </list>
///
/// <para><b>The timeout-extension multiplier (#119).</b> The derived bound is computed from the EXTENDED timeout, so
/// the "shorter than the timeout" rule always compares against the clock that actually runs. A configured bound is an
/// absolute the operator chose, so it is not scaled. A stall is recorded as an ordinary action failure, never as a
/// timeout, so it does not extend the next attempt's clock.</para>
///
/// <para><b>Sleep does not count as silence.</b> The bound is policed by <see cref="Prompts.StallWatch"/>, whose
/// poll-gap check resets the window after a host suspend (#517) and credits a partial one back.</para>
/// </summary>
internal static class ActionStallBound
{
    /// <summary>The smallest derived bound for a cloud block.</summary>
    internal static readonly TimeSpan Floor = TimeSpan.FromMinutes(15);

    /// <summary>The largest derived bound for a cloud block (the breakdown bound's value).</summary>
    internal static readonly TimeSpan Ceiling = TimeSpan.FromMinutes(20);

    /// <summary>The smallest derived bound for a local backend.</summary>
    internal static readonly TimeSpan LocalFloor = TimeSpan.FromMinutes(30);

    /// <summary>The largest derived bound for a local backend.</summary>
    internal static readonly TimeSpan LocalCeiling = TimeSpan.FromMinutes(60);

    /// <summary>
    /// True when <paramref name="block"/> talks to a local-inference backend: a claude gateway block or an
    /// <c>openai-compat</c> block. Those get the larger derived band.
    /// </summary>
    internal static bool IsLocalBackend(PromptRunnerConfig block) =>
        block.IsClaudeGateway || block.Kind == PromptRunnerKind.OpenAiCompat;

    /// <summary>The bound for one action attempt on <paramref name="block"/>, or null for none.</summary>
    internal static TimeSpan? Resolve(PromptRunnerConfig block, TimeSpan actionTimeout) =>
        Resolve(block.StallTimeoutSeconds, actionTimeout, IsLocalBackend(block));

    /// <summary>
    /// The bound for one action attempt, or null for none.
    /// </summary>
    /// <param name="configuredSeconds">
    /// The dispatched runner block's <c>stallTimeoutSeconds</c>: null derives the bound, 0 disables it, a positive
    /// value is used as given. A negative value is rejected at validation (GR2088) and treated as absent here.
    /// </param>
    /// <param name="actionTimeout">The attempt's timeout AFTER the #119 extension.</param>
    /// <param name="localBackend">Whether the block serves a local backend (<see cref="IsLocalBackend"/>).</param>
    internal static TimeSpan? Resolve(int? configuredSeconds, TimeSpan actionTimeout, bool localBackend = false)
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
                bound = localBackend
                    ? TimeSpan.FromTicks(Math.Clamp(actionTimeout.Ticks / 2, LocalFloor.Ticks, LocalCeiling.Ticks))
                    : TimeSpan.FromTicks(Math.Clamp(actionTimeout.Ticks / 3, Floor.Ticks, Ceiling.Ticks));
                break;
        }

        return bound < actionTimeout ? bound : null;
    }
}
