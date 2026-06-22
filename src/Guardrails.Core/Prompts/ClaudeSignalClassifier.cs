using System.Text.RegularExpressions;

namespace Guardrails.Core.Prompts;

/// <summary>
/// Claude-specific classification of an error response into a runner-agnostic
/// <see cref="PromptFailureKind"/> (SSOT §9). This is the SOLE home of the fragile vendor
/// error-string matching for the prompt pipeline — it stays inside the Claude quarantine so a
/// vendor wording change is a one-line edit here with a failing test pointing at it, never a
/// change scattered through the harness. The harness routes on the returned enum only.
///
/// <para>Matching prefers STRUCTURED signals (an HTTP status 429/503/529, the
/// <c>error_max_turns</c> terminal subtype) and falls back to a small, explicit set of free-text
/// phrases. The output-token-cap message (<c>"…exceeded the 32000 output token maximum"</c>) and the
/// turn-budget message (<c>"Reached maximum number of turns (N)"</c>, issue #129) are each matched
/// distinctly so the retry can carry actionable, signal-specific feedback (issues #114 / #129). A
/// miss is conservative: an UNrecognized error yields <see cref="PromptFailureKind.Error"/> (consumes
/// the budget, the status quo) — never a false <see cref="PromptFailureKind.Transient"/> that could
/// loop.</para>
/// </summary>
internal static class ClaudeSignalClassifier
{
    // HTTP statuses that are retryable infrastructure conditions (rate-limit / unavailable / overload).
    // Matched as a standalone token so a "529" inside a larger number (e.g. a cost) cannot trip it.
    private static readonly Regex TransientStatus = new(
        @"\b(429|503|529)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Free-text transient phrases (lowercased compare). Each is a deliberate, pinned signal — keep
    // this list small and assert it in tests so a vendor change is caught, not silently regressed.
    private static readonly string[] TransientPhrases =
    [
        "overloaded",
        "rate limit",
        "rate-limit",
        "ratelimit",
        "usage limit",
        "session limit",
        "too many requests",
        "service unavailable",
        "temporarily unavailable",
        "connection error",
        "connection reset",
        "connection refused",
    ];

    // The output-token-cap message. The numeric cap varies with CLAUDE_CODE_MAX_OUTPUT_TOKENS, so the
    // match is on the stable surrounding phrase, not the number.
    private static readonly Regex OutputCap = new(
        @"output\s+token\s+maximum|exceeded\s+the\s+\d+\s+output\s+token|max(imum)?\s+output\s+token",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    // The max-turns (turn-budget exhaustion) signal (issue #129 / #94). Two stable shapes: the
    // terminal-result SUBTYPE token "error_max_turns" (the structured, preferred signal) and the
    // free-text "Reached maximum number of turns (N)" message. The turn count varies, so the match is
    // on the surrounding phrase, not the number. Deliberately NOT matched by OutputCap above — "output
    // token maximum" (a single response too long) and "maximum number of turns" (too many tool turns)
    // are categorically different budgets, surfaced as distinct kinds.
    private static readonly Regex MaxTurns = new(
        @"error_max_turns|maximum\s+number\s+of\s+turns|max(imum)?\s+turns?\s+(reached|exceeded)|reached\s+max(imum)?\s+turns?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    // A reset hint the harness can surface ("resets 11:20am"); advisory only — never parsed into a
    // sleep duration (timezone/day ambiguity makes that unsafe). Captured for the operator message.
    private static readonly Regex ResetHint = new(
        @"resets?\s+(?<when>[0-9][0-9:apmAPM\s.]*\b)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>
    /// Classify an error <paramref name="text"/> (a terminal <c>result</c> message's error text, or,
    /// when there was no terminal result, the captured stdout/stderr of the failed run) into a
    /// <see cref="PromptFailureKind"/>. Returns <see cref="PromptFailureKind.None"/> for empty input.
    /// Precedence: output-cap, then max-turns, then transient, then a generic error — each special
    /// case is a distinct, actionable signal; a miss is conservative (→ <c>Error</c>, never a false
    /// <c>Transient</c> that could loop).
    /// </summary>
    public static PromptFailureKind Classify(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return PromptFailureKind.None;
        }

        if (OutputCap.IsMatch(text))
        {
            return PromptFailureKind.OutputCap;
        }

        if (MaxTurns.IsMatch(text))
        {
            return PromptFailureKind.MaxTurns;
        }

        if (IsTransient(text))
        {
            return PromptFailureKind.Transient;
        }

        return PromptFailureKind.Error;
    }

    /// <summary>True when <paramref name="text"/> carries a transient (429/503/529 or a known phrase) signal.</summary>
    public static bool IsTransient(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        if (TransientStatus.IsMatch(text))
        {
            return true;
        }

        string lower = text.ToLowerInvariant();
        foreach (string phrase in TransientPhrases)
        {
            if (lower.Contains(phrase, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The operator-facing reset hint from a rate-limit message ("11:20am"), or null if none.</summary>
    public static string? ExtractResetHint(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        Match match = ResetHint.Match(text);
        return match.Success ? match.Groups["when"].Value.Trim() : null;
    }
}
