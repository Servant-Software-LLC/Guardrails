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
///
/// <para><b>Polarity (issue #177).</b> A coverage token is one the action prompt must MENTION
/// because the guardrail requires it to be PRESENT in the authored file. Not every
/// <c>-match</c>/<c>-notmatch</c> line is such a token: a guardrail can also make a NEGATIVE
/// assertion — fail when a keyword is PRESENT (<c>if ($content -match "Foo") { … exit 1 }</c>) —
/// whose keyword is INTENTIONALLY absent from the prompt, so flagging it would be a false positive.
/// Each match-line is therefore classified by the polarity that makes its <c>exit 1</c> fire:
/// <list type="bullet">
///   <item><c>-notmatch … exit &lt;non-zero&gt;</c> — fails when the token is ABSENT ⇒
///     require-PRESENT ⇒ a coverage token (KEEP).</item>
///   <item><c>-match … $hits++</c> (the counting form, threshold <c>$hits -lt N</c>) — counts the
///     token's PRESENCE ⇒ require-PRESENT ⇒ a coverage token (KEEP).</item>
///   <item><c>-match … exit &lt;non-zero&gt;</c> — fails when the token is PRESENT ⇒ require-ABSENT
///     (negative assertion) ⇒ NOT a coverage token (EXCLUDE).</item>
/// </list>
/// When a line's polarity cannot be confidently classified, its token is NOT emitted (a silent
/// false negative is preferred to the #177 false positive).</para>
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
    /// The <c>op</c> group captures the optional <c>not</c> so polarity can be classified. The text
    /// BETWEEN one match and the next (its enclosing <c>if</c>-block body, single- or multi-line) is
    /// inspected separately for the <c>exit</c>/<c>$hits++</c> decision that fixes polarity.
    /// </summary>
    private static readonly Regex MatchLine = new(
        """\$(?:content|tn|code|text|file)\s+-(?<op>(?:not)?)match\s+(?:'(?<lit>[^']*)'|"(?<lit>[^"]*)")""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>
    /// The <c>$hits</c> counter increment (<c>$hits++</c> or <c>$hits += 1</c>) that marks the
    /// counting form's match-block as a PRESENCE count (a require-present coverage token).
    /// </summary>
    private static readonly Regex HitsIncrement = new(
        @"\$hits\s*(?:\+\+|\+=\s*1)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>
    /// A fail-the-guardrail <c>exit &lt;non-zero&gt;</c> (with optional sign) — the decision that, on a
    /// <c>-match</c> line, makes the token a NEGATIVE assertion and, on a <c>-notmatch</c> line, makes
    /// it a require-present coverage token. <c>exit 0</c> is deliberately NOT a fail and is excluded.
    /// </summary>
    private static readonly Regex FailExit = new(
        @"\bexit\s+-?(?!0\b)\d+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>
    /// The start of a PowerShell <c>if (</c> statement — used to bound one grep's <c>if</c>-block
    /// body so its polarity decision is read from THIS block only, never bleeding into the NEXT
    /// statement (e.g. a trailing <c>if ($hits -lt N) { … exit 1 }</c> threshold).
    /// </summary>
    private static readonly Regex IfStatement = new(
        @"\bif\s*\(",
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

        MatchCollection matchLines = MatchLine.Matches(guardrailBody);
        for (int i = 0; i < matchLines.Count; i++)
        {
            Match match = matchLines[i];
            string literal = match.Groups["lit"].Value;
            if (!IsClearKeyword(literal))
            {
                continue; // a regex/metachar literal is not a plain keyword we can confidently match
            }

            // Inspect this match's enclosing if-block body — the text from the end of this match up to
            // the start of the NEXT `if (` statement (or end of body) — for the exit/$hits++ decision
            // that fixes its polarity. Bounding at the next `if (` keeps the single- OR multi-line
            // `if (...) { …; exit 1 }` block intact while excluding a trailing `if ($hits -lt N) { …
            // exit 1 }` threshold, whose exit must NOT be read as this grep's decision.
            int blockStart = match.Index + match.Length;
            int blockEnd = guardrailBody.Length;
            Match nextIf = IfStatement.Match(guardrailBody, blockStart);
            if (nextIf.Success)
            {
                blockEnd = nextIf.Index;
            }
            string blockBody = guardrailBody[blockStart..blockEnd];

            // Only require-PRESENT tokens are coverage tokens (issue #177). When polarity can't be
            // confidently classified, drop the token (a silent false negative beats #177's false positive).
            bool isNegated = match.Groups["op"].Value.Length > 0; // "-notmatch" vs "-match"
            if (!IsRequirePresent(isNegated, blockBody))
            {
                continue;
            }

            if (seen.Add(literal))
            {
                tokens.Add(literal);
            }
        }

        return tokens;
    }

    /// <summary>
    /// Polarity classification for one match-block (issue #177). The token is a require-PRESENT
    /// coverage token only when the block's <c>exit 1</c> fires on the token being absent, or the
    /// block counts the token's presence:
    /// <list type="bullet">
    ///   <item><c>-notmatch … exit &lt;non-zero&gt;</c> (fail-on-absent) ⇒ require-present (true).</item>
    ///   <item><c>-match … $hits++</c> (presence-counting form) ⇒ require-present (true).</item>
    ///   <item><c>-match … exit &lt;non-zero&gt;</c> (fail-on-present, a negative assertion) ⇒ false.</item>
    /// </list>
    /// Anything else — a <c>-match</c> block that neither increments <c>$hits</c> nor fails, or a block
    /// in which no decision is found — is NOT confidently require-present and is excluded (conservatism:
    /// a silent false negative beats the #177 false positive). <paramref name="blockBody"/> is the text
    /// of the enclosing <c>if</c>-block, which may span multiple lines.
    /// </summary>
    private static bool IsRequirePresent(bool isNegated, string blockBody)
    {
        bool failsHere = FailExit.IsMatch(blockBody);

        if (isNegated)
        {
            // -notmatch: requires the token present iff its absence fails the guardrail.
            return failsHere;
        }

        // -match: a fail-on-present block is a NEGATIVE assertion (token must be absent) → exclude.
        // The only require-present -match shape is the presence-counting $hits++ form.
        return !failsHere && HitsIncrement.IsMatch(blockBody);
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
