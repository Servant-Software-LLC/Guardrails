namespace Guardrails.Core.Execution;

/// <summary>
/// The <c>failureKind</c> tokens <see cref="IRunObserver.WaveBreakdownFinished"/> carries — the SINGLE owner
/// of their spelling, so the scheduler that raises them and every surface that renders them agree by
/// construction rather than by matching literals.
///
/// <para>Two families, deliberately in one field. The first four are the RUNNER's own stop classification
/// (SSOT §9 <c>PromptFailureKind</c>), which design 20 §4.1 stopped discarding. The last two are the
/// HARNESS's: a session can end perfectly cleanly and still not produce an acceptable wave, and reporting
/// <c>null</c> there would settle the live phase row GREEN for a run that is about to halt. Which one it is
/// matters to the operator (a budget vs a defect), so the row names it.</para>
/// </summary>
public static class BreakdownFailureTokens
{
    /// <summary>The session exceeded its wall-clock ceiling and was killed.</summary>
    public const string Timeout = "timeout";

    /// <summary>The session exhausted its <c>--max-turns</c> budget.</summary>
    public const string MaxTurns = "max-turns";

    /// <summary>
    /// The session produced NO OUTPUT for longer than its stall bound and was killed (issue #504).
    /// Distinct from <see cref="Timeout"/> on purpose: "it was silent" and "it ran long" are different
    /// diagnoses with different fixes, and conflating them is what made a healthy 30-minute session read
    /// as a runaway.
    /// </summary>
    public const string Stalled = "stalled";

    /// <summary>The session hit the runner's output-token cap.</summary>
    public const string OutputCap = "output-cap";

    /// <summary>The session stopped on a transient runner condition (rate limit / overload).</summary>
    public const string Transient = "transient";

    /// <summary>The runner itself faulted (e.g. its binary is not on PATH).</summary>
    public const string Error = "error";

    /// <summary>The session ended CLEANLY but the deterministic <c>guardrails validate</c> gate rejected the wave.</summary>
    public const string Invalid = "invalid";

    /// <summary>A VALID PREFIX was preserved, short of the wave's own <c>breakdown-intent.json</c> declaration (SSOT §14.11).</summary>
    public const string Incomplete = "incomplete";
}

/// <summary>
/// Everything a UI needs to render the between-wave JIT breakdown phase (SSOT §14.4, design 23 §10.1,
/// issue #469) — the one phase of a run that used to raise NO observer event at all.
///
/// <para><b>Why a record and not loose parameters.</b> The phase is rendered on four surfaces (live table,
/// <c>--no-ui</c> heartbeat, log site, halt text) and every one of them needs the same probe targets. A
/// record keeps them in one place so the surfaces cannot drift; it is <b>public</b> because
/// <see cref="IRunObserver"/> is public and <c>Guardrails.Cli</c> has no <c>InternalsVisibleTo</c> into
/// <c>Guardrails.Core</c> — a non-public type on that signature is CS0051 (the precedent
/// <see cref="DecisionEntry"/> already set).</para>
///
/// <para><b>What is deliberately NOT here: a task count.</b> The eventual task count is not knowable at
/// invocation time (design 20 §3.2 measured <c>brief.md</c>'s work-item count under-declaring by 3–5×), so
/// no field on this record can be used as a progress denominator. The only denominator carried is
/// <see cref="Ceiling"/>, which denominates the BUDGET, not the work.</para>
/// </summary>
public sealed record WaveBreakdownContext
{
    /// <summary>The wave's folder name (<c>wave-NN-slug</c>) — the phase row's label and the log-site page key.</summary>
    public required string WaveDir { get; init; }

    /// <summary>The wave's 1-based position in the plan, for "Wave 2/2".</summary>
    public required int Index { get; init; }

    /// <summary>How many waves the plan has.</summary>
    public required int Total { get; init; }

    /// <summary>The breakdown's log directory (SSOT §8 <c>logs/&lt;runId&gt;/&lt;waveDir&gt;/breakdown/</c>) — the evidence pointer.</summary>
    public required string BreakdownLogDir { get; init; }

    /// <summary>
    /// The absolute path of the teed <c>claude-stream.jsonl</c> for THIS segment — the liveness stat target.
    /// A file that has never existed means the runner does not tee, and the UI omits the stream fragment
    /// entirely rather than inventing an "idle" alarm about a file nobody promised to write.
    /// </summary>
    public required string StreamLogPath { get; init; }

    /// <summary>The wave's <c>tasks/</c> directory — the forward-progress (folder-count) probe target.</summary>
    public required string TasksDirectory { get; init; }

    /// <summary>
    /// The composed prompt's size in bytes. Kept OFF every live surface on purpose (design 23 §4): an
    /// operator has no calibration for "KB of composed prompt", so the raw number at the moment they are
    /// most anxious invites the wrong inference. It belongs in the log-site evidence list, where a
    /// post-mortem reader correlating truncations across runs is not making a Ctrl+C decision.
    /// </summary>
    public required long ComposedPromptBytes { get; init; }

    /// <summary>The hard wall-clock ceiling for the session (<c>WaveBreakdownInvoker.BreakdownTimeout</c>).</summary>
    public required TimeSpan Ceiling { get; init; }

    /// <summary>
    /// The wave's <c>state/breakdown-intent.json</c> (SSOT §14.11) when the harness can point at one — the
    /// only HONEST denominator, because the session itself declared it. Null when the manifest is absent,
    /// and a null here must never be replaced by a synthesised total.
    /// </summary>
    public string? IntentManifestPath { get; init; }
}
