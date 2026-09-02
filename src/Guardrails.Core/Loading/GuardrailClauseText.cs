using System.Text;
using System.Text.RegularExpressions;
using static Guardrails.Core.Loading.PlanValidator;

namespace Guardrails.Core.Loading;

internal static class GuardrailClauseText
{
    /// <summary>
    /// <see cref="StripCommentLines"/>'s line-preserving twin: a comment line is BLANKED rather than
    /// removed, so an offset into the result still maps to the line number the reader will find in the
    /// file. Same #97 exclusion (the shared <see cref="IsCommentLine"/>), so a header comment that merely
    /// DESCRIBES a construction still cannot be what trips a check. Used by GR2057, which cites two clause
    /// LINE NUMBERS — a citation off by however many comment lines sit above it is worse than none.
    /// </summary>
    internal static string BlankCommentLines(string body) =>
        string.Join('\n', body.Split('\n').Select(line => IsCommentLine(line) ? string.Empty : line));

    internal static bool IsCommentLine(string line)
    {
        string trimmed = line.TrimStart();
        return trimmed.StartsWith('#')
            || trimmed.StartsWith("//", StringComparison.Ordinal)
            || trimmed.StartsWith("::", StringComparison.Ordinal)
            || (trimmed.StartsWith("REM", StringComparison.OrdinalIgnoreCase) &&
                (trimmed.Length == 3 || char.IsWhiteSpace(trimmed[3])));
    }

    /// <summary>
    /// A single-clause PowerShell presence test whose ENTIRE condition is ONE <c>-match</c>/<c>-notmatch</c>
    /// of a variable against a SINGLE-QUOTED literal, opening a block:
    /// <c>if ($content -notmatch '…') {</c>. Everything else is deliberately unmatched, because everything
    /// else makes the clause's polarity undecidable from the text:
    /// <list type="bullet">
    /// <item>a COMPOUND condition (<c>-and</c>/<c>-or</c>/<c>-not</c>/nested parens) — the block is then a
    /// verdict on the conjunction, not on this pattern, so taking the branch does not prove the pattern is
    /// required (the <c>\s*\)</c> immediately after the closing quote enforces this);</item>
    /// <item>a DOUBLE-QUOTED or COMPOSED operand (<c>("(?m)\b" + [regex]::Escape($m) + "\s*\(")</c>) — the
    /// pattern is not statically known, since PowerShell interpolates <c>$</c> inside <c>"…"</c>;</item>
    /// <item>a pattern spanning a newline — no guardrail in the field writes one, and admitting it lets a
    /// stray quote swallow half a script.</item>
    /// </list>
    /// <c>-cmatch</c>/<c>-imatch</c> and their <c>not</c> forms are the same operator with an explicit
    /// case rule and are admitted.
    /// </summary>
    internal static readonly Regex PresenceClause = new(
        @"\bif\s*\(\s*\$(?<subject>\w+)\s+-[ci]?(?<neg>not)?match\s+'(?<pat>(?:[^'\r\n]|'')*)'\s*\)\s*\{",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>
    /// Evidence that a clause's branch FAILS the guardrail rather than recording something: an append to a
    /// <c>$failures</c>-shaped accumulator, a non-zero <c>exit</c>, a <c>throw</c>, or a <c>Write-Error</c>.
    /// Both clauses of the measured #470 instance append to <c>$failures</c>; the catalogue's prescribed
    /// form writes a line and <c>exit 1</c>.
    /// </summary>
    internal static readonly Regex ClauseFailsTheGuardrail = new(
        @"\$\w*fail\w*\s*\+=|\bexit\s+[1-9]|\bthrow\b|\bWrite-Error\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>Regex metacharacters that make a pattern non-literal, so no exact witness can be derived.</summary>
    internal const string RegexMetacharacters = "()[]{}|*+?.^$";

    /// <summary>
    /// The exact text every file satisfying <paramref name="pattern"/> must contain, or <c>null</c> when the
    /// pattern does not pin one — see the bounded subset documented on
    /// <see cref="ValidateGuardrailRequiresForbiddenToken"/>.
    /// </summary>
    internal static string? TryLiteralWitness(string pattern)
    {
        int i = 0;

        // Leading inline option groups — (?i), (?m), (?is) — change matching, never the text matched.
        while (i + 2 < pattern.Length && pattern[i] == '(' && pattern[i + 1] == '?')
        {
            int close = i + 2;
            while (close < pattern.Length && "imsxn-".Contains(pattern[close], StringComparison.Ordinal))
            {
                close++;
            }

            if (close == i + 2 || close >= pattern.Length || pattern[close] != ')')
            {
                break;
            }

            i = close + 1;
        }

        if (i < pattern.Length && pattern[i] == '^')
        {
            i++;                                                    // zero-width start anchor
        }

        int end = pattern.Length;
        if (end > i && pattern[end - 1] == '$' && (end - 2 < i || pattern[end - 2] != '\\'))
        {
            end--;                                                  // zero-width end anchor
        }

        StringBuilder witness = new();
        while (i < end)
        {
            char c = pattern[i];
            if (c != '\\')
            {
                if (RegexMetacharacters.Contains(c, StringComparison.Ordinal))
                {
                    return null;
                }

                witness.Append(c);
                i++;
                continue;
            }

            if (i + 1 >= end)
            {
                return null;
            }

            char escaped = pattern[i + 1];
            i += 2;

            if (escaped == 'b')
            {
                continue;                                           // zero-width word boundary
            }

            if (escaped == 's')
            {
                char quantifier = i < end ? pattern[i] : '\0';
                if (quantifier is '*' or '?')
                {
                    i++;                                            // zero whitespace is a valid witness
                    continue;
                }

                if (quantifier == '+')
                {
                    i++;
                }

                witness.Append(' ');
                continue;
            }

            if (char.IsAsciiLetterOrDigit(escaped))
            {
                return null;                                        // \w \d \S \n \t \1 …
            }

            witness.Append(escaped);                                // escaped punctuation is itself
        }

        return witness.ToString();
    }

    /// <summary>
    /// Does <paramref name="pattern"/>, compiled from the PLAN's own text, match <paramref name="witness"/>?
    /// A pattern that is not a valid regex, or that times out, answers NO — <c>validate</c> is read-only and
    /// must degrade rather than throw over a plan author's typo (GR2056's precedent; issue #487).
    /// </summary>
    internal static bool MatchesWitness(string pattern, string witness)
    {
        try
        {
            return new Regex(pattern, RegexOptions.CultureInvariant, ClauseMatchTimeout).IsMatch(witness);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }
}
