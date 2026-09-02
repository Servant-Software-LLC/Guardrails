// A representative, CORRECT GuardrailClauseText.cs: the clause helpers lifted out of PlanValidator with
// PresenceClause's single-quoted-operand restriction intact.
using System.Text.RegularExpressions;

namespace Guardrails.Core.Loading;

internal static class GuardrailClauseText
{
    /// <summary>
    /// A requirement clause: a presence/absence test of a variable against a SINGLE-QUOTED literal.
    /// A DOUBLE-QUOTED or composed operand is deliberately unmatched - PowerShell interpolates $ inside
    /// "..." so the pattern is not statically known.
    /// </summary>
    internal static readonly Regex PresenceClause = new(
        @"\bif\s*\(\s*\$(?<subject>\w+)\s+-[ci]?(?<neg>not)?match\s+'(?<pat>(?:[^'\r\n]|'')*)'\s*\)\s*\{",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    internal static readonly Regex ClauseFailsTheGuardrail = new(
        @"\$\w*fail\w*\s*\+=|\bexit\s+[1-9]|\bthrow\b|\bWrite-Error\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    internal const string RegexMetacharacters = "()[]{}|*+?.^$";

    internal static string BlankCommentLines(string body) =>
        string.Join('\n', body.Split('\n').Select(line => IsCommentLine(line) ? string.Empty : line));

    internal static bool IsCommentLine(string line) => line.TrimStart().StartsWith('#');

    internal static string? TryLiteralWitness(string pattern) => pattern;

    internal static bool MatchesWitness(string pattern, string witness) => pattern == witness;
}
