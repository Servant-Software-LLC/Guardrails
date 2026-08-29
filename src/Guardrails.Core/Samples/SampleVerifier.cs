using Guardrails.Core.Execution;
using Guardrails.Core.Model;

namespace Guardrails.Core.Samples;

/// <summary>
/// Why a pair is unsound (plan of record 26, §2/§7). A pair asserts exactly two facts — the
/// <c>.valid</c> half's guardrail exits 0, the <c>.invalid</c> half's exits non-zero — and this enum
/// names every way that assertion can be false or unverifiable.
/// </summary>
public enum SampleFindingKind
{
    /// <summary>Only one of the two halves (<c>.valid</c>/<c>.invalid</c>) is committed.</summary>
    MissingHalf,

    /// <summary>A sample's base name matches no guardrail in its task — a stale, orphaned pair.</summary>
    OrphanSample,

    /// <summary>The <c>.valid</c> half exited non-zero — the guardrail rejects a correct artifact.</summary>
    ValidHalfFailed,

    /// <summary>The <c>.invalid</c> half exited 0 — the guardrail can never fail.</summary>
    InvalidHalfPassed,

    /// <summary>Both halves are wrong at once (<c>.valid</c> non-zero AND <c>.invalid</c> zero) — one finding, not two.</summary>
    ReversedPolarity,

    /// <summary>The matched guardrail cannot be executed deterministically (e.g. a prompt judge).</summary>
    Unverifiable
}

/// <summary>One problem found while verifying a sample pair against its guardrail (SSOT/plan 26).</summary>
public sealed record SampleFinding
{
    public required SampleFindingKind Kind { get; init; }

    /// <summary>Absolute path to the matched guardrail file; null only for <see cref="SampleFindingKind.OrphanSample"/>.</summary>
    public string? GuardrailPath { get; init; }

    /// <summary>Absolute path to the sample half this finding is about.</summary>
    public required string SamplePath { get; init; }

    /// <summary>The guardrail's observed exit code for <see cref="SamplePath"/>, when a process actually ran.</summary>
    public int? ObservedExitCode { get; init; }

    /// <summary>Human-actionable message naming the guardrail path, the sample path, and the observed exit code.</summary>
    public required string Message { get; init; }
}

/// <summary>The outcome of verifying every committed sample pair in a plan (SSOT/plan 26).</summary>
public sealed record SampleVerifyResult
{
    public required IReadOnlyList<SampleFinding> Findings { get; init; }

    /// <summary>The number of pairs actually run through their guardrail.</summary>
    public required int PairsVerified { get; init; }

    public bool Passed => Findings.Count == 0;
}

/// <summary>
/// Verifies every committed <c>tasks/&lt;id&gt;/samples/</c> pair against its matching
/// <c>tasks/&lt;id&gt;/guardrails/</c> script (plan of record 26). Implementation lands in a later task
/// of this plan; this type is a minimal compile-only skeleton.
/// </summary>
public static class SampleVerifier
{
    public static Task<SampleVerifyResult> VerifyAsync(
        PlanDefinition plan,
        ProcessRunner processRunner,
        TimeSpan perSampleTimeout,
        CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }
}
