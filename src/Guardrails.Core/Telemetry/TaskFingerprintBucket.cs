using Guardrails.Core.Model;

namespace Guardrails.Core.Telemetry;

/// <summary>
/// The task-fingerprint bucket (plan 30 §3.2, issue #548) — a fact about a task's write surface and
/// guardrail shape, derived from the two things the harness already holds at attempt time, so that the
/// corpus can compare like work to like work instead of reading a category off the task's name (the
/// report's own legend: <c>"a bucket is a fact about a task, never one read off its name"</c>).
///
/// <para><b>The signature carries no task identity, and that is deliberate, not an oversight.</b>
/// <see cref="Classify"/> takes only <paramref name="writeScope">writeScope</paramref> and
/// <paramref name="guardrails">guardrails</paramref> — no <c>TaskNode</c>, no <c>taskId</c>, no
/// <c>name</c> — so reading the bucket off the task's name is not merely discouraged, it is
/// impossible for the compiler to allow.</para>
/// </summary>
public static class TaskFingerprintBucket
{
    /// <summary>Writes <c>tests/**</c> only, gated by a TDD-red guardrail.</summary>
    public const string TestAuthoring = "test-authoring";

    /// <summary>Writes <c>src/**</c> only, gated by a <c>tests-pass</c> guardrail.</summary>
    public const string Implementation = "implementation";

    /// <summary>Writes <c>src/**</c> or <c>tests/**</c> only, with no behavioural gate.</summary>
    public const string Structural = "structural";

    /// <summary>Writes both <c>src/**</c> and <c>tests/**</c> — the write surface decides, regardless of guardrail shape.</summary>
    public const string CodePlusTests = "code+tests";

    /// <summary>Writes <c>docs/**</c> / <c>.claude/**</c> only.</summary>
    public const string Documentation = "documentation";

    /// <summary>An empty <c>writeScope</c> (<c>[]</c>) — the deliberate "writes nothing" declaration.</summary>
    public const string NoWrite = "no-write";

    /// <summary>
    /// Classifies a task's fingerprint bucket from its write-scope roots and its resolved guardrails.
    /// Returns <c>null</c> when no rule matches the write surface — the corpus reader renders that as
    /// <c>(unbucketed)</c> rather than a guessed bucket. A <c>null</c> <paramref name="writeScope"/> (the
    /// write-scope check's off-switch) is a DIFFERENT claim from an empty one and also yields
    /// <c>null</c>, never <see cref="NoWrite"/>.
    /// </summary>
    public static string? Classify(
        IReadOnlyList<string>? writeScope,
        IReadOnlyList<GuardrailDefinition> guardrails)
    {
        throw new NotImplementedException();
    }
}
