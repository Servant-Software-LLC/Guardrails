// INVALID sample for 03-deleted-members-are-gone.ps1 — expect exit 1.
// IDENTICAL to the .valid sample except for ONE defect: OverwatchSupplyAutoResolve.Resolve survived
// the change. Certify was added beside it rather than replacing it, which is the wrong
// implementation this guardrail exists to catch — every test passes either way, because task 04's
// rewritten tests simply have no reference to Resolve any more, so nothing else in the plan can see
// that a second, unreachable decision is still sitting in the file.
//
// Note what this sample deliberately does NOT do: its Resolve body is trivial and pure, so the
// sibling 04-certify-is-pure.ps1 still exits 0 here. That is the pair behaving correctly — the two
// scripts check independent properties and neither stands in for the other.
using Guardrails.Core.Model;

namespace Guardrails.Core.Execution;

/// <summary>
/// What the overwatcher decided at a struggle boundary (doc 11 §5) — the control-flow signal the
/// <see cref="TaskExecutor"/> loop consults.
/// </summary>
public sealed record OverwatchDecision
{
    /// <summary>The decision kind.</summary>
    public required OverwatchDecisionKind Kind { get; init; }

    /// <summary>Guidance to inject into the NEXT attempt's composed prompt.</summary>
    public string? GuidanceInjection { get; init; }

    /// <summary>Extra retry attempts to add to the budget, already clamped to the hard cap.</summary>
    public int ExtraRetries { get; init; }

    /// <summary>A one-line enrichment appended to the task's needs-human summary.</summary>
    public string? RichHaltSummary { get; init; }

    /// <summary>The advisory no-op: the deterministic policy stands unchanged.</summary>
    public static OverwatchDecision NoAction { get; } = new() { Kind = OverwatchDecisionKind.NoAction };
}

/// <summary>The overwatcher control-flow outcomes.</summary>
public enum OverwatchDecisionKind
{
    /// <summary>The overwatcher stayed out — the deterministic policy proceeds unchanged.</summary>
    NoAction,

    /// <summary>Halt honestly now, with the precise diagnosis.</summary>
    Halt,

    /// <summary>Grant one more attempt BECAUSE a sanctioned change was applied that materially alters it.</summary>
    Grant
}

/// <summary>One certified supply: a candidate path, paired with the checkout commit its bytes are read at.</summary>
public sealed record CertifiedSupply
{
    /// <summary>The workspace-relative path, normalized.</summary>
    public required string Path { get; init; }

    /// <summary>The candidate's own source sha — never a sha the proposal supplied.</summary>
    public required string SourceCommit { get; init; }
}

/// <summary>The gate's verdict: certified with its supplies, or refused with a reason token.</summary>
public sealed record SupplyCertification
{
    /// <summary>True only when every check passed. There is no partial certification.</summary>
    public required bool Certified { get; init; }

    /// <summary>The refusal token when <see cref="Certified"/> is false; otherwise null.</summary>
    public string? Reason { get; init; }

    /// <summary>Every certified op, each paired with its candidate's source sha. Empty on a refusal.</summary>
    public required IReadOnlyList<CertifiedSupply> Supplies { get; init; }
}

/// <summary>
/// The deterministic gate a missing-resource auto-resolve must pass (design 41 §3.1). A prompt may
/// propose; only this may certify.
/// </summary>
public static class OverwatchSupplyAutoResolve
{
    // THE ONE DEFECT: design 41 §3.4 deletes this. Certify was added BESIDE it instead of REPLACING
    // it, so the retired decision is still here with no caller — the #712 shape exactly.
    /// <summary>The shipped auto-resolve. Nothing reaches it.</summary>
    public static OverwatchDecision Resolve(AutonomyPolicy policy, bool autonomyBlockPresent) =>
        OverwatchDecision.NoAction;

    /// <param name="policy">The run's autonomy policy. Anything but Auto keeps the dial inert.</param>
    /// <param name="autonomyBlockPresent">Whether the config carries an explicit autonomy block.</param>
    /// <param name="autonomy">The autonomy block, for the shared effective-threshold rule and the review gate.</param>
    /// <param name="candidates">The paths the harness verified it MAY supply, each with its source sha.</param>
    /// <param name="proposal">The overwatcher's parsed proposal.</param>
    public static SupplyCertification Certify(
        AutonomyPolicy policy,
        bool autonomyBlockPresent,
        AutonomyConfig? autonomy,
        IReadOnlyList<MissingResourceCandidate> candidates,
        OverwatchProposal? proposal)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        bool atCritical =
            policy == AutonomyPolicy.Auto
            && autonomyBlockPresent
            && GateThreshold.Effective(autonomy, CriticalityGate.NeedsHuman) == EscalationThreshold.Critical;

        if (!atCritical)
        {
            return Refused("dial-not-critical");
        }

        if (autonomy?.GateThresholds?.ReviewGate == ReviewGateDecision.ProceedUnreviewed)
        {
            return Refused("proceed-unreviewed");
        }

        if (proposal is not { Classification: OverwatchClassification.Retryable })
        {
            return Refused("doomed");
        }

        List<OverwatchFixOp> ops = proposal.Fixes
            .Where(op => op.Kind == OverwatchFixKind.ResourceSupply)
            .ToList();

        if (ops.Count == 0)
        {
            return Refused("no-resource-supply-op");
        }

        var supplies = new List<CertifiedSupply>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (OverwatchFixOp op in ops)
        {
            string normalized = Normalize(op.TargetPath);
            MissingResourceCandidate? candidate =
                candidates.FirstOrDefault(c => string.Equals(Normalize(c.Path), normalized, StringComparison.Ordinal));

            if (candidate is null)
            {
                return Refused("not-a-candidate");
            }

            if (!seen.Add(normalized))
            {
                return Refused("duplicate-path");
            }

            supplies.Add(new CertifiedSupply { Path = normalized, SourceCommit = candidate.SourceCommit });
        }

        return new SupplyCertification { Certified = true, Supplies = supplies };
    }

    private static SupplyCertification Refused(string reason) =>
        new() { Certified = false, Reason = reason, Supplies = [] };

    /// <summary>Normalize a proposed or candidate path: separators to '/', and a leading './' removed.</summary>
    private static string Normalize(string? path)
    {
        string value = (path ?? string.Empty).Trim().Replace('\\', '/');
        while (value.StartsWith("./", StringComparison.Ordinal))
        {
            value = value[2..];
        }

        return value;
    }
}
