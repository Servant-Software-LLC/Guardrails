using Guardrails.Core.Model;

namespace Guardrails.Core.Bundle;

/// <summary>
/// Pass 5 of SSOT §17.6.5, run-scoped: if ANY runner block the plan declares names an <c>authTokenEnv</c> or
/// <c>apiKeyEnv</c> that is unset or empty in the bundling shell, the known-value pass is blind to a token the run
/// held, so <c>bundle</c> refuses before writing anything. Attribution to individual files is unsound — a judge picks
/// its own block, gates run with no attempt route log, and a token-holding child's output can be quoted anywhere.
/// </summary>
public static class BundleD1
{
    /// <summary>Every variable a runner block names (<c>authTokenEnv</c>, <c>apiKeyEnv</c>), distinct, ordinal.</summary>
    public static IReadOnlyList<string> BlockVariables(PlanDefinition plan) =>
    [
        .. plan.Config.PromptRunners.Values
            .SelectMany(block => new[] { block.AuthTokenEnv, block.ApiKeyEnv })
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!.Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
    ];

    /// <summary>The block-named variables unset or empty in <paramref name="environment"/> — the D1 set. Empty: no refusal.</summary>
    public static IReadOnlyList<string> UnsetVariables(PlanDefinition plan, IReadOnlyDictionary<string, string> environment) =>
    [
        .. BlockVariables(plan).Where(name => !environment.TryGetValue(name, out string? value) || string.IsNullOrEmpty(value))
    ];

    /// <summary>
    /// The refusal text: each variable named, with the remedy, then the two ways past it (#814). The remedy shows the
    /// VALUE a variable needs (<c>export NAME=&lt;value&gt;</c>): a bare <c>export NAME</c> exports an empty variable, which
    /// D1 treats as unset, so the same refusal would come back. A variable that exists in
    /// <paramref name="environment"/> but is empty is named as such. <c>--no-redact</c> is offered for a bundle that
    /// stays private, and <c>--without-agent-text</c> for one that keeps no agent text.
    /// </summary>
    public static IReadOnlyList<string> RefusalLines(
        IReadOnlyList<string> unset, IReadOnlyDictionary<string, string>? environment = null) =>
    [
        "guardrails bundle: refused (D1): a runner block names a token variable this shell does not have, so the scrub "
        + "would be blind to a token the run held. Nothing was written.",
        .. unset.Select(name => environment is not null && environment.ContainsKey(name)
            ? $"  {name} is set but empty in this shell: export {name}=<value> (the token itself) and re-run `guardrails bundle`"
            : $"  export {name}=<value> (the token itself) in this shell and re-run `guardrails bundle`"),
        "  Or pass --without-agent-text to ship the bundle with all agent-derived free text removed, run-wide.",
        "  Or pass --no-redact if this bundle stays private (it is written as -UNREDACTED; never attach it to a public issue).",
    ];
}
