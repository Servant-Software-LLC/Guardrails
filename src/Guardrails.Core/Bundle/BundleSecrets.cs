using System.Text.RegularExpressions;
using Guardrails.Core.Prompts;

namespace Guardrails.Core.Bundle;

/// <summary>
/// The secret-name rule of SSOT §17.6.1: a variable or key whose NAME says it holds a credential. Used twice — to
/// collect known values from the bundling shell and from plan <c>env</c> maps (pass 1), and to recognize
/// <c>NAME=value</c> / <c>"name": "value"</c> pairs (pass 2, kind <c>named-secret</c>).
/// </summary>
public static partial class SecretNameRule
{
    /// <summary>
    /// <c>PWD</c> and <c>OLDPWD</c> hold paths and are excluded by exact name (SSOT §17.6.1). They do not match the
    /// rule today; the exclusion keeps it that way if the rule ever grows.
    /// </summary>
    public static bool IsSecretName(string name) =>
        !string.IsNullOrEmpty(name) && name is not ("PWD" or "OLDPWD") && Rule().IsMatch(name);

    // SSOT §17.6.1 verbatim: it includes `_PASS$|^PASS$` (the #799 corpus's `SMTP_PASS`) and a `PIN` segment (#805).
    [GeneratedRegex(
        @"(?i)(TOKEN|SECRET|PASSWORD|PASSWD|_PWD$|^PWD_|API_?KEY|_KEY|KEY\b|CREDENTIAL|AUTH|COOKIE|SESSION|CONN(ECTION)?_?STR|DSN|_PASS$|^PASS$|(?:^|[_-])PIN(?:$|[_-]))",
        RegexOptions.CultureInvariant)]
    private static partial Regex Rule();
}

/// <summary>One collected known value and the stable label it is replaced with (SSOT §17.6.1).</summary>
/// <param name="Variable">The variable or env-map key the value was collected under.</param>
/// <param name="Label">The label body, <c>&lt;VARIABLE&gt;#&lt;n&gt;</c>.</param>
/// <param name="Value">The secret itself. Never written anywhere: not to a file, not hashed.</param>
public sealed record KnownSecret(string Variable, string Label, string Value);

/// <summary>
/// The known values of pass 1 (SSOT §17.6.1), collected ONCE per bundle so the same value carries the same label in
/// every file. Order is a function of the inputs only: environment variables first, ordinally by name, then the
/// plan's <c>env</c> maps in the order the caller gives them (guardrails.json, then each task.json by task id).
/// </summary>
public sealed class BundleSecrets
{
    /// <summary>Values shorter than this are not collected (SSOT §17.6.1).</summary>
    public const int MinimumLength = 8;

    /// <summary>The fixed list of §17.6.1, collected whatever the name rule says.</summary>
    public static IReadOnlyList<string> FixedVariables { get; } =
    [
        "ANTHROPIC_AUTH_TOKEN", "ANTHROPIC_API_KEY", "CLAUDE_CODE_OAUTH_TOKEN", "ANTHROPIC_FOUNDRY_API_KEY",
        "OPENAI_API_KEY", "CURSOR_API_KEY", "GH_TOKEN", "GITHUB_TOKEN",
    ];

    private BundleSecrets(IReadOnlyList<KnownSecret> values) => Values = values;

    /// <summary>The collected values, in collection order.</summary>
    public IReadOnlyList<KnownSecret> Values { get; }

    /// <summary>No known values at all.</summary>
    public static BundleSecrets None { get; } = new([]);

    /// <summary>The bundling shell alone: the fixed list and every name the rule matches.</summary>
    public static BundleSecrets FromEnvironment(IReadOnlyDictionary<string, string> environment) =>
        Collect(environment, [], []);

    /// <summary>
    /// Collect from the bundling shell (<paramref name="environment"/>: the fixed list, every variable a runner block's
    /// <c>authTokenEnv</c>/<c>apiKeyEnv</c> names in <paramref name="blockVariables"/>, and every name the rule
    /// matches), then from the plan's literal <c>env</c> map entries (<paramref name="envMapEntries"/>, in the order
    /// given), keeping an entry whose key matches the rule or is a block-named variable.
    /// <para>
    /// The gateway placeholder <c>guardrails-gateway-no-auth</c> is never collected: it is a public constant the
    /// harness sends when a gateway block names no <c>authTokenEnv</c>, and it is the diagnostic that would have
    /// closed #791 (SSOT §17.6.1).
    /// </para>
    /// </summary>
    public static BundleSecrets Collect(
        IReadOnlyDictionary<string, string> environment,
        IEnumerable<string> blockVariables,
        IEnumerable<KeyValuePair<string, string>> envMapEntries)
    {
        var named = new HashSet<string>(blockVariables.Where(v => !string.IsNullOrWhiteSpace(v)), StringComparer.Ordinal);
        var values = new List<KnownSecret>();
        var seenValues = new HashSet<string>(StringComparer.Ordinal);
        var perVariable = new Dictionary<string, int>(StringComparer.Ordinal);

        void Add(string variable, string? value)
        {
            if (value is null || value.Length < MinimumLength
                || string.Equals(value, ClaudeGatewayEnvironment.PlaceholderToken, StringComparison.Ordinal)
                || !seenValues.Add(value))
            {
                return;
            }

            int n = perVariable.GetValueOrDefault(variable) + 1;
            perVariable[variable] = n;
            values.Add(new KnownSecret(variable, $"{variable}#{n}", value));
        }

        foreach (KeyValuePair<string, string> variable in environment.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (variable.Key is "PWD" or "OLDPWD")
            {
                continue;
            }

            if (FixedVariables.Contains(variable.Key, StringComparer.Ordinal) || named.Contains(variable.Key)
                || SecretNameRule.IsSecretName(variable.Key))
            {
                Add(variable.Key, variable.Value);
            }
        }

        foreach (KeyValuePair<string, string> entry in envMapEntries)
        {
            if (named.Contains(entry.Key) || SecretNameRule.IsSecretName(entry.Key))
            {
                Add(entry.Key, entry.Value);
            }
        }

        return new BundleSecrets(values);
    }
}
