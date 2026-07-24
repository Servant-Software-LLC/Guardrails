using Guardrails.Core.Prompts;

namespace Guardrails.Core.Execution;

/// <summary>
/// The deterministic class an unattended run's stop is mapped to before it is acted on
/// (issue #361 Phase 3, doc 12 §4 / §4.1). The dial governs class <see cref="JudgmentCall"/> ONLY;
/// the hard-blocker classes and the <see cref="Floor"/> are NOT dial-eligible — they act
/// unconditionally (invariant 1: classification is deterministic wherever the harness has the signal).
/// </summary>
public enum GateClass
{
    /// <summary>
    /// (a) A gate with no deterministic answer where a best-guess exists (a design question, a wave
    /// breakdown). This is the ONLY class the dial governs: assess criticality → escalate (≥ threshold)
    /// or proceed-with-recorded-best-guess (&lt; threshold). Doc 12 §4 row (a).
    /// </summary>
    JudgmentCall,

    /// <summary>
    /// (b) An external/recoverable hard blocker (rate limit, 503, a service momentarily down). NOT the
    /// dial: bounded wait + backoff/retry against the config ceiling, then re-evaluate (doc 12 §4 row (b)
    /// / §4.2). On ceiling it escalates to <see cref="HardBlockerPermanent"/>.
    /// </summary>
    HardBlockerRetryable,

    /// <summary>
    /// (c) A permanent or retry-exhausted hard blocker where no best-guess exists and no retry will
    /// clear it (missing credential, permission wall, unready environment, DB unreachable after ceiling).
    /// NOT the dial: halt-and-escalate unconditionally with full failure context. This is also the safe
    /// default for an UNKNOWN/ambiguous signal — escalate, never silently spin (doc 12 §4 row (c) / §4.3).
    /// </summary>
    HardBlockerPermanent,

    /// <summary>
    /// A deterministic FLOOR the dial may NEVER lower at any threshold (doc 12 §4.1 / §5, invariant 5):
    /// a terminal-exhaustion <c>needs-human</c> (a task that could not converge to green) or the
    /// overwatcher's deterministic floor (no-op deadlock / max-turns / write-scope loop, doc 11 §8).
    /// A doomed/exhausted run is never best-guessed past.
    /// </summary>
    Floor
}

/// <summary>
/// Which already-observed signal a <see cref="GateSignal"/> carries — the discriminator the classifier
/// switches on (doc 12 §4.1). Each value corresponds to one row of the classify-then-act table; the
/// signal itself is produced elsewhere in the harness (the Claude quarantine, the permission-wall
/// tracker, the overwatcher, the wave gate) and only mapped to a <see cref="GateClass"/> here.
/// </summary>
public enum GateSignalKind
{
    /// <summary>A classified prompt failure (<see cref="PromptFailureKind"/>) — e.g. a transient 429/503/529.</summary>
    PromptFailure,

    /// <summary>A permission-wall halt decision (<see cref="PermissionWallDecision"/>, #266 / #86 / #104).</summary>
    PermissionWall,

    /// <summary>An infrastructure fault / honest abort (<see cref="RunAbort"/>, #150).</summary>
    InfrastructureFault,

    /// <summary>A plan/wave preflight failure — a dependency artifact was not materialized (SSOT §3.3/§14.3).</summary>
    PreflightFailure,

    /// <summary>An agent-emitted <c>{"needsHuman": "..."}</c> — the agent explicitly asked a human a design question (SSOT §9).</summary>
    AgentNeedsHuman,

    /// <summary>A JIT wave checkpoint — the next wave is unauthored/empty (SSOT §14.4, #360).</summary>
    WaveCheckpoint,

    /// <summary>An overwatcher deterministic-floor trigger (<see cref="OverwatchTrigger"/>, doc 11 §8).</summary>
    Overwatch,

    /// <summary>An unrecognized / ambiguous stop with no known signal — the load-bearing negative case (§4.3).</summary>
    Unknown
}

/// <summary>
/// An already-observed run stop, in the shape the deterministic gate classifier consumes (doc 12 §4.1).
/// The classifier is a PURE function: detection of each concrete signal lives elsewhere (the Claude
/// quarantine, <see cref="PermissionWallTracker"/>, the overwatcher, the wave gate); this record simply
/// carries the observed signal so <see cref="GateClassifier.Classify"/> can map it to a
/// <see cref="GateClass"/> with zero judge involvement. Construct one via the static factory that names
/// the observed signal.
/// </summary>
public sealed record GateSignal
{
    private GateSignal(GateSignalKind kind) => Kind = kind;

    /// <summary>Which observed signal this is (the classifier's discriminator).</summary>
    public GateSignalKind Kind { get; }

    /// <summary>The classified prompt failure, when <see cref="Kind"/> is <see cref="GateSignalKind.PromptFailure"/>; else <see cref="PromptFailureKind.None"/>.</summary>
    public PromptFailureKind Prompt { get; private init; }

    /// <summary>The permission-wall decision, when <see cref="Kind"/> is <see cref="GateSignalKind.PermissionWall"/>; else null.</summary>
    public PermissionWallDecision? Wall { get; private init; }

    /// <summary>The infrastructure-fault abort, when <see cref="Kind"/> is <see cref="GateSignalKind.InfrastructureFault"/>; else null.</summary>
    public RunAbort? Abort { get; private init; }

    /// <summary>The overwatcher floor trigger, when <see cref="Kind"/> is <see cref="GateSignalKind.Overwatch"/>; else null.</summary>
    public OverwatchTrigger? Trigger { get; private init; }

    /// <summary>The wave-halt kind, when <see cref="Kind"/> is <see cref="GateSignalKind.WaveCheckpoint"/>; else null.</summary>
    public WaveHaltKind? Checkpoint { get; private init; }

    /// <summary>Free-text detail for a preflight failure, an agent question, or an unknown signal; else null.</summary>
    public string? Detail { get; private init; }

    /// <summary>A classified prompt failure (e.g. <see cref="PromptFailureKind.Transient"/>).</summary>
    public static GateSignal PromptFailure(PromptFailureKind kind) =>
        new(GateSignalKind.PromptFailure) { Prompt = kind };

    /// <summary>A permission-wall halt decision (#266 / #86 / #104).</summary>
    public static GateSignal PermissionWall(PermissionWallDecision decision) =>
        new(GateSignalKind.PermissionWall) { Wall = decision };

    /// <summary>An infrastructure fault / honest abort (#150).</summary>
    public static GateSignal InfrastructureFault(RunAbort abort) =>
        new(GateSignalKind.InfrastructureFault) { Abort = abort };

    /// <summary>A plan/wave preflight failure (a dependency artifact was not materialized).</summary>
    public static GateSignal PreflightFailure(string detail) =>
        new(GateSignalKind.PreflightFailure) { Detail = detail };

    /// <summary>An agent-emitted <c>{"needsHuman": "..."}</c> design question.</summary>
    public static GateSignal AgentNeedsHuman(string question) =>
        new(GateSignalKind.AgentNeedsHuman) { Detail = question };

    /// <summary>A JIT wave checkpoint (the next wave is unauthored/empty).</summary>
    public static GateSignal WaveCheckpoint(WaveHaltKind kind) =>
        new(GateSignalKind.WaveCheckpoint) { Checkpoint = kind };

    /// <summary>An overwatcher deterministic-floor trigger (doc 11 §8).</summary>
    public static GateSignal Overwatch(OverwatchTrigger trigger) =>
        new(GateSignalKind.Overwatch) { Trigger = trigger };

    /// <summary>An unrecognized / ambiguous stop with no known signal.</summary>
    public static GateSignal Unknown(string detail) =>
        new(GateSignalKind.Unknown) { Detail = detail };
}

/// <summary>
/// Maps an already-observed <see cref="GateSignal"/> to its deterministic <see cref="GateClass"/>
/// (issue #361 Phase 3, doc 12 §4 / §4.1). A PURE function and the deterministic authority for the
/// dangerous cases: a known-transient failure is class (b) as a FACT; an unknown/ambiguous failure
/// defaults to <see cref="GateClass.HardBlockerPermanent"/> — escalate, never silently spin (§4.3,
/// invariant 1). The classifier itself runs no prompt and makes no judgment; a judge may only ever
/// widen the retryable set, recorded and bounded, elsewhere (§4.3).
/// </summary>
public static class GateClassifier
{
    /// <summary>
    /// Classify an observed <paramref name="signal"/> into its <see cref="GateClass"/> per the doc 12
    /// §4.1 classify-then-act table. A PURE function of its input — no I/O, no prompt, no judgment — so it
    /// is the deterministic authority for the dangerous cases and a trivially re-runnable unit-test base.
    /// A KNOWN-transient prompt failure is class (b) as a FACT; an UNKNOWN/ambiguous signal defaults to
    /// <see cref="GateClass.HardBlockerPermanent"/> — escalate, never silently spin (§4.3, invariant 1).
    /// </summary>
    /// <param name="signal">The already-observed run stop to classify.</param>
    /// <returns>The deterministic <see cref="GateClass"/> the stop maps to.</returns>
    public static GateClass Classify(GateSignal signal)
    {
        ArgumentNullException.ThrowIfNull(signal);

        return signal.Kind switch
        {
            // (b) Hard blocker, retryable/transient (§4.1): a KNOWN transient (429/503/529, overloaded,
            // rate/session/usage limit) is the ONLY prompt failure that backs off + retries. Every other
            // PromptFailureKind (None/OutputCap/Timeout/MaxTurns/Error) is NOT a known-transient, so the
            // §4.3 safe default applies — escalate, never silently spin.
            GateSignalKind.PromptFailure => signal.Prompt == PromptFailureKind.Transient
                ? GateClass.HardBlockerRetryable
                : GateClass.HardBlockerPermanent,

            // (c) Hard blocker, permanent (§4.1): no best-guess exists and no retry clears these. A
            // permission wall (#266/#86/#104) halts unconditionally — no best-guess grants a missing path;
            // an infrastructure fault / RunAbort (#150) is an honest abort; a plan/wave preflight failure
            // (SSOT §3.3/§14.3) means the environment is not ready. All escalate with full context.
            GateSignalKind.PermissionWall => GateClass.HardBlockerPermanent,
            GateSignalKind.InfrastructureFault => GateClass.HardBlockerPermanent,
            GateSignalKind.PreflightFailure => GateClass.HardBlockerPermanent,

            // (a) Judgment call — the ONLY dial-eligible class (§4 row a). An agent-emitted needsHuman is
            // an explicit design question with a best-guess. The JIT wave-checkpoint is dial-eligible only
            // when it is the "next wave unauthored" checkpoint (§14.4, #360); any other WaveHaltKind is not
            // that checkpoint, so it takes the §4.3 safe default rather than being best-guessed past.
            GateSignalKind.AgentNeedsHuman => GateClass.JudgmentCall,
            GateSignalKind.WaveCheckpoint => signal.Checkpoint == WaveHaltKind.NextWaveUnauthored
                ? GateClass.JudgmentCall
                : GateClass.HardBlockerPermanent,

            // Floor (§4.1 / §5, invariant 5): the overwatcher's DETERMINISTIC floor — a no-op deadlock
            // (#174), the #264 deterministic-script reproduction, or a terminal-exhaustion needs-human (a
            // task that could not converge to green). The dial may NEVER lower these at any threshold,
            // including `critical`. Any other overwatch trigger is not a floor here, so it escalates via
            // the §4.3 safe default rather than being mislabelled a floor.
            GateSignalKind.Overwatch => signal.Trigger is
                OverwatchTrigger.NoOpDeadlock or
                OverwatchTrigger.DeterministicScript or
                OverwatchTrigger.TerminalExhaustion
                    ? GateClass.Floor
                    : GateClass.HardBlockerPermanent,

            // The load-bearing negative (§4.3): an UNKNOWN/ambiguous stop is NOT silently treated as
            // retryable — it escalates as a permanent blocker. Escalate, never spin. An unrecognized
            // discriminator is itself ambiguous and takes the same safe default.
            GateSignalKind.Unknown => GateClass.HardBlockerPermanent,
            _ => GateClass.HardBlockerPermanent
        };
    }
}
