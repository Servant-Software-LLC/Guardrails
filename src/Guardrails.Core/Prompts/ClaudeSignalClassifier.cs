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
/// phrases — plus <see cref="ConnectionFailure"/>, the connection-level set (DoR §6.3): a failure to
/// REACH the provider completes no request, so it carries no status token at all and its wording is the
/// OS/library's rather than the vendor's. The output-token-cap message (<c>"…exceeded the 32000 output token maximum"</c>) and the
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

    /// <summary>
    /// The connection-level failure set (DoR §6.3): the provider could not be REACHED — DNS never
    /// resolved, the socket was refused/reset, the TLS transport never came up, or the runner binary
    /// itself would not launch. All of it is <see cref="PromptFailureKind.Transient"/>, which routes
    /// to the shipped #115 pause (bounded exponential backoff, no retry-budget consumption): a human
    /// cannot fix a downed provider and an immediate re-launch just re-fails into it.
    ///
    /// <para><b>The answer to §6.3's open question — "does the shipped quarantine already catch a bare
    /// DNS/refused shape, or does it need an additive classification?" — is PARTIALLY, so this set is
    /// the additive half.</b> ALREADY COVERED, by <see cref="TransientPhrases"/> above and left there:
    /// the spelled-out English prose <c>"connection refused"</c>, <c>"connection reset"</c> and
    /// <c>"connection error"</c>. NOT COVERED, and added here: every DNS shape (libuv's
    /// <c>getaddrinfo ENOTFOUND</c>/<c>EAI_AGAIN</c>, curl's "could not resolve host", glibc's "name or
    /// service not known", Winsock's "no such host is known"), the errno spellings of the very same
    /// refused/reset condition (<c>ECONNREFUSED</c>/<c>ECONNRESET</c>, Winsock's "no connection could be
    /// made…"), the whole TLS/handshake family, and a runner binary that never launched.
    /// <see cref="TransientStatus"/> could not have caught any of them at any point: no HTTP request
    /// was ever completed, so there is no 429/503/529 to find. NO new <see cref="PromptFailureKind"/>
    /// member and no probe enum was introduced — §6.3 rules both out for v1 — this widens what the
    /// shipped quarantine RECOGNIZES and nothing else.</para>
    ///
    /// <para><b>Why each shape is spelled out at length.</b> The classifier's conservative-miss rule is
    /// the binding constraint: a false <c>Transient</c> is the expensive direction, because a
    /// deterministic logic failure would then ride the pause machinery to the end of
    /// <c>transientPauseBudgetSeconds</c> instead of consuming its retry budget and surfacing. So every
    /// alternative below is long enough to be unambiguous in ordinary compiler/assertion output —
    /// "could not resolve host" rather than "resolve", the full cmd.exe sentence rather than "is not
    /// recognized". A shape that would also match a test-failure message gets LONGER; the negative
    /// control never gets weaker.</para>
    ///
    /// <para><b>The launch family is the broadest, and is anchored deliberately.</b>
    /// <c>Win32Exception</c> alone is not a signal (an agent's own output can carry one) — it must be
    /// adjacent to a not-found/launch message, which is the shape <see cref="ClaudePromptRunner"/>
    /// hands us when <c>Process.Start</c> fails (it classifies TYPE + native code + message precisely
    /// so this anchor exists; the bare OS string "the system cannot find the file specified" is
    /// deliberately not a signal on its own). The two shell wordings are anchored to their diagnostic
    /// form ("<c>bash: claude: command not found</c>"), not to the bare words. Residual, accepted: a
    /// shell "command not found" inside a failed run's captured output classifies Transient — bounded
    /// by the pause budget, and the launch channel is the shape this exists to catch.</para>
    /// </summary>
    private static readonly Regex ConnectionFailure = new(
        // DNS — the name never resolved, so no connection was even attempted.
        @"getaddrinfo|\bENOTFOUND\b|\bEAI_AGAIN\b|could not resolve host|name or service not known"
        + @"|no such host is known"
        // Refused / reset, in the spellings the prose phrases above miss.
        + @"|\bECONN(?:REFUSED|RESET)\b|no connection could be made"
        // TLS / handshake — the transport never came up.
        + @"|tls handshake timeout|ssl certificate problem|ssl routines"
        + @"|ssl connection could not be established"
        // The runner binary would not launch (.NET's Process.Start fault, then the two shell wordings).
        + @"|an error occurred trying to start process"
        + @"|\bWin32Exception\b[^\r\n]{0,120}(?:cannot find the file specified|no such file or directory)"
        + @"|is not recognized as an internal or external command"
        + @"|:\s*command not found|command not found:",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

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

    /// <summary>
    /// True when <paramref name="text"/> carries a transient signal: a 429/503/529 status, a known
    /// phrase, or a <see cref="ConnectionFailure"/> shape (§6.3). Every recogniser lives HERE, below
    /// both entry points, so this predicate and <see cref="Classify"/> can never disagree — a signal
    /// added to one and not the other would pause the attempt while the reset-hint path and every
    /// other <c>IsTransient</c> caller still saw a non-transient failure.
    /// </summary>
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

        if (ConnectionFailure.IsMatch(text))
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
