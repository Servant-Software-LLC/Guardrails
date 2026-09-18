using Guardrails.Core.Model;

namespace Guardrails.Core.Execution;

/// <summary>
/// What the overwatcher decided at a struggle boundary (doc 11 §5) — the control-flow signal the
/// <see cref="TaskExecutor"/> loop consults. It is NEVER a verdict: the overwatcher can grant an adjusted
/// attempt (coupled to a sanctioned change) or halt honestly, but it can never mark a task succeeded or
/// merge a fragment. "No sanctioned change ⇒ no grant": a <see cref="OverwatchDecisionKind.Grant"/> ALWAYS
/// carries a materially-different next attempt (guidance and/or a budget bump).
/// </summary>
public sealed record OverwatchDecision
{
    /// <summary>The decision kind.</summary>
    public required OverwatchDecisionKind Kind { get; init; }

    /// <summary>
    /// For <see cref="OverwatchDecisionKind.Grant"/>: guidance to inject into the NEXT attempt's composed
    /// prompt (the ephemeral, allowlist lever). Non-empty when a guidance op was sanctioned.
    /// </summary>
    public string? GuidanceInjection { get; init; }

    /// <summary>
    /// For <see cref="OverwatchDecisionKind.Grant"/>: extra retry attempts to add to the budget (a
    /// sanctioned budget lever), already clamped to the hard cap. Zero when only guidance was sanctioned.
    /// </summary>
    public int ExtraRetries { get; init; }

    /// <summary>
    /// A one-line enrichment appended to the task's <c>needs-human</c> summary when the overwatcher halts
    /// with a precise diagnosis (makes the halt earlier + richer, never softer). Null for a grant/no-action.
    /// </summary>
    public string? RichHaltSummary { get; init; }

    /// <summary>The advisory no-op: the deterministic policy stands unchanged (no runner, cost cap hit, or a malformed/errored/absent proposal).</summary>
    public static OverwatchDecision NoAction { get; } = new() { Kind = OverwatchDecisionKind.NoAction };
}

/// <summary>The overwatcher control-flow outcomes.</summary>
public enum OverwatchDecisionKind
{
    /// <summary>The overwatcher stayed out — the deterministic policy (short-circuit / retry / exhaustion) proceeds unchanged.</summary>
    NoAction,

    /// <summary>Halt honestly now, with the precise diagnosis. Never softer than the deterministic policy — only earlier + richer.</summary>
    Halt,

    /// <summary>Grant one more attempt BECAUSE a sanctioned change (guidance / budget) was applied that materially alters it.</summary>
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

/// <summary>The gate's verdict (design 41 §3.1): certified with its supplies, or refused with a reason token.</summary>
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
/// The deterministic certification gate for a missing-resource auto-resolve (design 41 §3.1):
/// <see cref="Certify"/> either certifies every proposed <c>resource-supply</c> op — each paired with its
/// own candidate's source sha — or refuses the WHOLE proposal with one reason token. There is no partial
/// certification: a partial supply would re-arm a task that then halts again on the file it still does not
/// have, spending money doing it.
/// <para>
/// A PURE function: no git, no journal, no filesystem, no child process. Everything <see cref="Certify"/>
/// decides on — the dial, the candidates, the proposal — was established by the harness before it ran, so
/// there is nothing left for it to go and look up. That purity is what makes the gate trustworthy, and a
/// guardrail enforces it mechanically by banning the run-recording and file-supplying machinery's own
/// names, and any direct filesystem or process access, from this whole file.
/// </para>
/// <para>
/// Gated at <c>dial:critical</c> — the SAME composition rule doc 12 §3.2 uses everywhere else the dial
/// engages: <see cref="AutonomyPolicy.Auto"/> AND an <c>autonomy</c> block present (the anti-Option-(c)
/// guard also used by the auto-tier gate in <see cref="Overwatch"/>) AND <see cref="GateThreshold.Effective"/>
/// resolving to <see cref="EscalationThreshold.Critical"/>. Below that composition — or on any of the
/// gate's other refusals — the caller proposes the copy-pasteable <c>supply</c>/<c>reset</c>/<c>run</c>
/// sequence instead of acting on it (the halted task's own halt text is the single producer of that
/// sequence).
/// </para>
/// </summary>
public static class OverwatchSupplyAutoResolve
{
    /// <summary>
    /// The deterministic gate (design 41 §3.1). A PURE function: no git, no journal, no filesystem.
    /// A prompt may propose; only this may certify. Checks run in order — each refusal token below is
    /// observed only once every earlier check has passed:
    /// <list type="number">
    ///   <item><c>dial-not-critical</c>: <paramref name="policy"/> is <see cref="AutonomyPolicy.Auto"/>,
    ///   <paramref name="autonomyBlockPresent"/>, and <see cref="GateThreshold.Effective"/> for
    ///   <see cref="CriticalityGate.NeedsHuman"/> resolves to <see cref="EscalationThreshold.Critical"/>.</item>
    ///   <item><c>proceed-unreviewed</c>: <c>gateThresholds.review-gate</c> is
    ///   <see cref="ReviewGateDecision.ProceedUnreviewed"/>.</item>
    ///   <item><c>doomed</c>: <paramref name="proposal"/> is absent, or is not
    ///   <see cref="OverwatchClassification.Retryable"/>.</item>
    ///   <item><c>no-resource-supply-op</c>: no <see cref="OverwatchFixKind.ResourceSupply"/> op is proposed.</item>
    ///   <item><c>not-a-candidate</c>: some proposed path, after <c>/</c> and <c>./</c> normalization, matches no <paramref name="candidates"/> entry.</item>
    ///   <item><c>duplicate-path</c>: some path is proposed twice.</item>
    /// </list>
    /// A pass certifies every proposed op, each paired with ITS CANDIDATE's source sha — never a sha the
    /// proposal supplied; the model cannot contribute a fact about where bytes came from.
    /// </summary>
    /// <param name="policy">The run's <see cref="AutonomyPolicy"/>. Anything other than <see cref="AutonomyPolicy.Auto"/> keeps the dial inert (doc 12 §3.2).</param>
    /// <param name="autonomyBlockPresent">Whether the run's config carries an explicit <c>autonomy</c> block — the anti-Option-(c) guard; a bare <c>auto</c> with no block keeps the dial inert.</param>
    /// <param name="autonomy">The run's autonomy block, or null when there is none — resolved via <see cref="GateThreshold.Effective"/>.</param>
    /// <param name="candidates">The harness-computed candidates (design 41 §2.2) a proposed path must match.</param>
    /// <param name="proposal">The overwatcher's diagnose proposal, or null when absent/unparseable.</param>
    public static SupplyCertification Certify(
        AutonomyPolicy policy,
        bool autonomyBlockPresent,
        AutonomyConfig? autonomy,
        IReadOnlyList<MissingResourceCandidate> candidates,
        OverwatchProposal? proposal)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        bool dialCritical =
            policy == AutonomyPolicy.Auto
            && autonomyBlockPresent
            && GateThreshold.Effective(autonomy, CriticalityGate.NeedsHuman) == EscalationThreshold.Critical;
        if (!dialCritical)
        {
            return Refuse("dial-not-critical");
        }

        if (autonomy?.GateThresholds?.ReviewGate == ReviewGateDecision.ProceedUnreviewed)
        {
            return Refuse("proceed-unreviewed");
        }

        if (proposal is null || proposal.Classification != OverwatchClassification.Retryable)
        {
            return Refuse("doomed");
        }

        List<string> proposedPaths = proposal.Fixes
            .Where(fix => fix.Kind == OverwatchFixKind.ResourceSupply)
            .Select(fix => NormalizePath(fix.TargetPath ?? ""))
            .ToList();

        if (proposedPaths.Count == 0)
        {
            return Refuse("no-resource-supply-op");
        }

        Dictionary<string, MissingResourceCandidate> byPath = candidates.ToDictionary(c => c.Path);

        foreach (string path in proposedPaths)
        {
            if (!byPath.ContainsKey(path))
            {
                return Refuse("not-a-candidate");
            }
        }

        if (proposedPaths.Distinct(StringComparer.Ordinal).Count() != proposedPaths.Count)
        {
            return Refuse("duplicate-path");
        }

        List<CertifiedSupply> supplies = proposedPaths
            .Select(path => byPath[path])
            .Select(candidate => new CertifiedSupply { Path = candidate.Path, SourceCommit = candidate.SourceCommit })
            .ToList();

        return new SupplyCertification { Certified = true, Reason = null, Supplies = supplies };
    }

    private static SupplyCertification Refuse(string reason) =>
        new() { Certified = false, Reason = reason, Supplies = [] };

    /// <summary>Normalize backslashes to <c>/</c> and strip a leading <c>./</c> before matching a candidate.</summary>
    private static string NormalizePath(string path)
    {
        string normalized = path.Replace('\\', '/');
        while (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        return normalized;
    }
}
