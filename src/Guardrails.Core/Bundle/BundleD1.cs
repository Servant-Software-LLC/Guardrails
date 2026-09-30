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
    /// The refusal text: each variable named, told apart as UNSET (key absent) or SET BUT EMPTY (a bare
    /// <c>export NAME</c> with no <c>=value</c> exports an empty variable, which D1 still refuses), then the three
    /// remedies: export the real value, <c>--no-redact</c> for a bundle that stays private, or
    /// <c>--without-agent-text</c> (#814).
    /// </summary>
    public static IReadOnlyList<string> RefusalLines(IReadOnlyList<string> unset, IReadOnlyDictionary<string, string> environment) =>
    [
        "guardrails bundle: refused (D1): a runner block names a token variable that is unset or empty in this shell, "
        + "so the scrub would be blind to a token the run held. Nothing was written.",
        .. unset.Select(name => VariableLine(name, environment)),
        "  Or pass --no-redact if this bundle stays private (it is written as -UNREDACTED; never attach it to a public issue).",
        "  Or pass --without-agent-text to ship the bundle with all agent-derived free text removed, run-wide.",
    ];

    private static string VariableLine(string name, IReadOnlyDictionary<string, string> environment)
    {
        string remedy = $"export {name}=<value> in this shell and re-run `guardrails bundle`";
        return environment.ContainsKey(name)
            ? $"  {name} is set but EMPTY (did you run `export {name}` without `=value`?): {remedy}"
            : $"  {name} is not set: {remedy}";
    }
}
