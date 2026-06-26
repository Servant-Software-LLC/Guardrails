using System.Text.RegularExpressions;

namespace Guardrails.Core.Loading;

/// <summary>
/// Pure heuristic that recognises the <c>covers-key-behaviors</c> guardrail archetype (SSOT §4,
/// issue #157 §1) in a script-guardrail body and extracts its required coverage tokens. Used by
/// <see cref="PlanValidator"/> to cross-reference those tokens against the same task's action
/// prompt and emit the GR2026 stale-coverage WARNING when a required token is absent from the
/// prompt.
///
/// <para>
/// The archetype (from the <c>plan-breakdown</c> guardrail catalogue) greps ONE test file for a
/// handful of distinctive literal terms — one regex literal per behavior — so the guardrail fails
/// naming the missing behavior. Two real shapes exist and both are recognised:
/// </para>
/// <list type="number">
///   <item>
///     The <c>$hits</c> counter form the issue describes: <c>if ($content -match "&lt;token&gt;")
///     { $hits++ }</c> lines with a final <c>$hits -lt N</c> threshold. The presence of a
///     <c>$hits -lt N</c> threshold is the high-confidence archetype signal.
///   </item>
///   <item>
///     The per-term early-exit form the catalogue/dotnet realization emits: <c>if ($content
///     -notmatch '&lt;token&gt;') { …; exit 1 }</c> lines. This form carries no <c>$hits</c>
///     threshold, so it is only treated as the archetype when the guardrail FILE NAME is the
///     canonical <c>covers-key-behaviors</c> (the name the skill always uses), keeping recognition
///     confident.
///   </item>
/// </list>
///
/// <para><b>Conservatism (zero-false-positive spirit, even for a warning).</b> Tokens are extracted
/// ONLY from quoted string literals on the RIGHT of a <c>-match</c>/<c>-notmatch</c> against the
/// <c>$content</c>/<c>$tn</c>/<c>$code</c> variable the archetype scans, and only when the literal
/// is a "clear keyword" — an alphanumeric run (optionally with <c>. _ -</c>), no regex
/// metacharacters, at least three characters. A literal containing regex syntax (anchors, classes,
/// alternations, escapes) is skipped: it is not a plain keyword we can confidently keyword-match
/// against prose. When the archetype cannot be confidently identified, NO tokens are returned and
/// no warning fires.</para>
/// </summary>
public static class CoverageGuardrailHeuristic
{
    /// <summary>The canonical archetype guardrail name the skill emits (case-insensitive).</summary>
    private const string CanonicalName = "covers-key-behaviors";

    /// <summary>Minimum literal length to be considered a "clear keyword" (avoids tiny noise).</summary>
    private const int MinTokenLength = 3;

    /// <summary>
    /// A <c>-match</c> / <c>-notmatch</c> comparison whose LEFT operand is the content variable the
    /// archetype scans (<c>$content</c>, <c>$tn</c>, <c>$code</c>, <c>$text</c> or <c>$file</c>) and
    /// whose RIGHT operand is a single-or-double-quoted string literal. Case-insensitive on the
    /// operator; the captured literal text is validated separately by <see cref="IsClearKeyword"/>.
    /// </summary>
    private static readonly Regex MatchLine = new(
        """\$(?:content|tn|code|text|file)\s+-(?:not)?match\s+(?:'(?<lit>[^']*)'|"(?<lit>[^"]*)")""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>The high-confidence archetype signal: a final <c>$hits -lt &lt;N&gt;</c> threshold.</summary>
    private static readonly Regex HitsThreshold = new(
        @"\$hits\s+-lt\s+\d+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>A clear literal keyword: alphanumerics plus <c>. _ -</c>, no regex metacharacters.</summary>
    private static readonly Regex ClearKeyword = new(
        @"^[A-Za-z0-9][A-Za-z0-9._-]*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Extract the required coverage tokens from a guardrail body when it is confidently the
    /// covers-key-behaviors archetype, else an empty list. <paramref name="guardrailName"/> is the
    /// guardrail's file basename (used to recognise the canonical name for the no-<c>$hits</c> form).
    /// Returned tokens are de-duplicated (ordinal, case-insensitive) preserving first-seen order.
    /// </summary>
    public static IReadOnlyList<string> ExtractCoverageTokens(string guardrailBody, string guardrailName)
    {
        ArgumentNullException.ThrowIfNull(guardrailBody);
        ArgumentNullException.ThrowIfNull(guardrailName);

        bool hasHitsThreshold = HitsThreshold.IsMatch(guardrailBody);
        bool isCanonicallyNamed =
            guardrailName.Contains(CanonicalName, StringComparison.OrdinalIgnoreCase);

        // Confidently the archetype ONLY when either the $hits -lt N threshold is present (the
        // issue's canonical signal) OR the guardrail carries the canonical covers-key-behaviors
        // name (the per-term early-exit form the catalogue emits). Otherwise: not confident → no
        // tokens, no warning.
        if (!hasHitsThreshold && !isCanonicallyNamed)
        {
            return [];
        }

        var tokens = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in MatchLine.Matches(guardrailBody))
        {
            string literal = match.Groups["lit"].Value;
            if (!IsClearKeyword(literal))
            {
                continue; // a regex/metachar literal is not a plain keyword we can confidently match
            }

            if (seen.Add(literal))
            {
                tokens.Add(literal);
            }
        }

        return tokens;
    }

    /// <summary>
    /// True when <paramref name="literal"/> is a plain keyword — long enough, and only
    /// alphanumerics plus <c>. _ -</c> (no regex anchors/classes/alternations/escapes). A literal
    /// that carries regex syntax is deliberately NOT treated as a keyword: we cannot confidently
    /// keyword-match it against the action prompt's prose, so it is skipped (conservatism).
    /// </summary>
    private static bool IsClearKeyword(string literal) =>
        literal.Length >= MinTokenLength && ClearKeyword.IsMatch(literal);
}
