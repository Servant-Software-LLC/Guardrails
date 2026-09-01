namespace Guardrails.Core.Model;

/// <summary>
/// One wave of a WAVED plan (SSOT §14) — a first-class completion unit made of a task DAG plus its own
/// entry (<c>preflights/</c>) and exit (<c>guardrails/</c>) gates, run in strict total order between hard
/// barriers. A wave is a mini-plan folder: <c>&lt;plan&gt;/&lt;waveDir&gt;/{preflights,guardrails,tasks}/…</c>.
/// A FLAT plan has no waves (<see cref="PlanDefinition.Waves"/> is empty).
/// </summary>
public sealed record WaveNode
{
    /// <summary>
    /// The conventional file name of a wave's OPTIONAL human-authored brief (SSOT §14.10, #360 Phase 0):
    /// the <c>plan-breakdown</c> input at the JIT checkpoint. Its PRESENCE is the opt-in signal — absent
    /// = honest-halt exactly as today; present = auto-breakdown-*eligible* in a future phase. Folded into
    /// <see cref="Journal.WaveDefinitionHash"/> (a changed brief on a COMPLETED wave is legitimate drift)
    /// but EXCLUDED from <see cref="Journal.PlanDefinitionHash"/> (it is breakdown INPUT, not reviewed
    /// output). The single spelling both the hasher and the scheduler's checkpoint reference.
    /// </summary>
    public const string BriefFileName = "brief.md";

    /// <summary>The wave directory name (e.g. "wave-02-provision"), matching <c>^wave-([0-9]+)-[a-z0-9-]+$</c>.</summary>
    public required string Dir { get; init; }

    /// <summary>
    /// The wave's numeric prefix (the <c>[0-9]+</c> group; e.g. 2 for "wave-02-provision"). Load-bearing —
    /// it drives the strict total order, since there is no <c>dependsOnWave</c> edge (SSOT §14.1).
    /// </summary>
    public required int Number { get; init; }

    /// <summary>The wave's slug (the part after the number; e.g. "provision" for "wave-02-provision").</summary>
    public required string Slug { get; init; }

    /// <summary>Absolute path to the wave folder.</summary>
    public required string Directory { get; init; }

    /// <summary>
    /// This wave's tasks (from <c>&lt;waveDir&gt;/tasks/</c>), in folder-name (ordinal) order, each carrying
    /// its WAVE-QUALIFIED <see cref="TaskNode.Id"/> and its <see cref="TaskNode.WaveDir"/> set to
    /// <see cref="Dir"/>. May be empty for a not-yet-authored (JIT) wave.
    /// </summary>
    public required IReadOnlyList<TaskNode> Tasks { get; init; }

    /// <summary>
    /// The wave's ENTRY gate — guardrail-shaped files from <c>&lt;waveDir&gt;/preflights/</c> (SSOT §14.3),
    /// evaluated before the wave's DAG against the plan-branch HEAD (the materialized prior wave). The same
    /// <see cref="GuardrailDefinition"/> shape and parser as a task's preflights; opens with a
    /// <c>catches:</c> declaration (GR2027). Empty when the folder is absent.
    /// </summary>
    public IReadOnlyList<GuardrailDefinition> Preflights { get; init; } = [];

    /// <summary>
    /// The wave's EXIT / terminal gate — guardrail-shaped files from <c>&lt;waveDir&gt;/guardrails/</c>
    /// (SSOT §14.3), evaluated at wave end on the merged HEAD-so-far. The last wave's exit gate runs on the
    /// fully-merged HEAD and IS the whole-plan terminal soundness boundary (a multi-leaf/fan-in wave must
    /// carry ≥1 real integration re-run, GR2028 per wave). Empty when the folder is absent.
    /// </summary>
    public IReadOnlyList<GuardrailDefinition> Guardrails { get; init; } = [];

    /// <summary>
    /// The wave's OWN half of its definition surface — <c>guardrails/**</c>, then <c>preflights/**</c>, then
    /// the optional <c>brief.md</c> — captured EAGERLY by the loader from the SAME bytes it read to build
    /// this node (plan 32-executed-definition-hash §5.4, issue #556). The twin of
    /// <see cref="TaskNode.DefinitionHashAtLoad"/> one level up: the definition the wave actually RAN
    /// against, so a mid-run edit to a gate script never reaches the wave-completion stamp. Write site W5
    /// (the journal wave entry and the <c>Guardrails-Wave:</c> marker commit) folds THIS value; there is no
    /// fallback to disk at that write site, ever. Null only for a <see cref="WaveNode"/> the loader did not
    /// construct.
    ///
    /// <para><b>It holds the labeled-segment TEXT, not a digest — and that is load-bearing, not an
    /// oversight.</b> The disk-reading form of <see cref="Journal.WaveDefinitionHash"/> inlines each gate
    /// file's BODY into its builder, and only the wave-completion WRITE is pinned: the wave-drift COMPARE on
    /// the next resume is still a disk recompute (§5.4 keeps <c>Compute(wave)</c> unchanged for every READ).
    /// The two must therefore be BYTE-IDENTICAL on an unedited tree, or every completed wave reads as
    /// drifted on the very next resume. Folding a SHA into the position the disk form fills with bodies
    /// produces a different digest, so this capture holds exactly the bytes that position needs. The
    /// property name is <see cref="TaskNode.DefinitionHashAtLoad"/>'s for symmetry across the two levels
    /// (§5.4 names it), which is the one place that name and its contents pull apart.</para>
    /// </summary>
    public string? DefinitionHashAtLoad { get; init; }
}
