using System.CommandLine;

namespace Guardrails.Cli;

/// <summary>
/// Issue #603 — the process-wide invocation settings, chosen rather than inherited.
///
/// <para>
/// <b>What this is.</b> System.CommandLine registers a SIGINT/SIGTERM handler that cancels the
/// invocation's <see cref="CancellationToken"/> and then gives the invocation
/// <see cref="ProcessTerminationTimeout"/> to unwind before abandoning it and returning 130. Passing no
/// <see cref="InvocationConfiguration"/> does not disable that — it takes the library's default of
/// <b>2 seconds</b>, which is what every <c>guardrails</c> command ran under until this type existed.
/// </para>
/// </summary>
public static class CliInvocation
{
    /// <summary>
    /// How long the whole process gets to unwind after Ctrl-C (SIGINT) or SIGTERM before System.CommandLine
    /// abandons the invocation and returns 130.
    ///
    /// <para>
    /// <b>It is a CEILING, not a delay.</b> Teardown that finishes in 40 ms still exits in 40 ms; this value
    /// is only ever reached by a teardown that is genuinely stuck. That is what makes a number this large
    /// affordable on `plan-hash` as well as on `run`.
    /// </para>
    ///
    /// <para>
    /// <b>Where 15 seconds comes from.</b> The lower bound is arithmetic — the bounded budgets a cancelled
    /// <c>run</c> spends IN SERIES on its way out, each one deliberate and documented where it lives:
    /// </para>
    /// <list type="bullet">
    /// <item><c>WebhookEventSink</c> cancelled teardown — 0 ms backlog + 500 ms terminal attempt + 250 ms
    ///   pump grace = <b>750 ms</b> (SSOT §8.3);</item>
    /// <item><see cref="Ui.LogServer.ShutdownDrainTimeout"/> — <b>5 s</b>, the in-flight drain that makes a
    ///   parked <c>GET /events</c> subscriber receive the terminal row (§12.2, PR #599);</item>
    /// <item><see cref="Ui.LogServer.ListenerTeardownLinger"/> — <b>250 ms</b> before the listener is
    ///   actually stopped, which is that subscriber's only window to read what was flushed to it.</item>
    /// </list>
    /// <para>
    /// That is <b>6 s</b> of bounded budget, and it is not the whole cost: the scheduler's own unwind kills
    /// a process TREE per in-flight task and drains its readers, the journal is written, and the worktree
    /// exit sweep runs — all unbounded in principle and fast in practice, on a machine that is by
    /// definition busy (it was running a build). 15 s is 2.5x the bounded sum, which leaves the unbounded
    /// remainder roughly as much room again as everything measured.
    /// </para>
    ///
    /// <para>
    /// <b>The upper bound is a person.</b> Past roughly this long an operator concludes the process is hung
    /// and reaches for a harder kill, and a ceiling nobody waits out delivers nothing — so there is no point
    /// buying more headroom by making Ctrl-C feel broken. 15 s sits at that edge deliberately.
    /// </para>
    ///
    /// <para>
    /// <b>What it costs.</b> A Ctrl-C on a run whose teardown is genuinely stuck now appears to hang for up
    /// to 15 s instead of 2 s. That trade is right because the 2 s case was never "fast" — it was
    /// <i>truncating</i>. The drain alone is 2.5x the old ceiling, so on the cancelled path the terminal-row
    /// delivery that two layers (§8.3's webhook POST, §12.2's <c>/events</c> stream) exist to guarantee was
    /// structurally unable to finish, silently, on the one path an operator invokes deliberately. Paying up
    /// to 13 extra seconds in the rare stuck case to make the common cancelled case actually deliver is the
    /// right side of that trade. Nothing is lost either way — <c>events.jsonl</c> is the durable record
    /// (§8.1) — but "the file has it" is a poor answer to a supervising agent that was watching the wire.
    /// </para>
    ///
    /// <para>
    /// <b>Why not null.</b> <see cref="InvocationConfiguration.ProcessTerminationTimeout"/> is nullable and
    /// null means "do not handle process termination at all" — Ctrl-C would then kill the process outright
    /// with no token cancellation and no teardown whatsoever. That is strictly worse than the 2 s default,
    /// not a simplification of it.
    /// </para>
    /// </summary>
    public static readonly TimeSpan ProcessTerminationTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// System.CommandLine's default when no <see cref="InvocationConfiguration"/> is supplied, recorded here
    /// so the tests can state the defect as a POSITIVE control — the log server's drain alone exceeds it —
    /// rather than merely asserting that the chosen value is the chosen value.
    /// </summary>
    public static readonly TimeSpan LibraryDefaultProcessTerminationTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The configuration every <c>guardrails</c> invocation runs under. A fresh instance per call: an
    /// <see cref="InvocationConfiguration"/> carries mutable Output/Error writers, so handing the same one
    /// to two invocations would share them.
    /// </summary>
    public static InvocationConfiguration Create() =>
        new() { ProcessTerminationTimeout = ProcessTerminationTimeout };
}
