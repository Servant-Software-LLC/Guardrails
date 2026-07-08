namespace Guardrails.Core.Execution;

/// <summary>
/// Robust, name-based recognition of guardrail archetypes the harness must treat specially — the
/// single source of truth shared by <see cref="RetryPolicy"/> (feedback) and <see cref="TaskExecutor"/>
/// (retry salvage), so the two can never drift.
/// </summary>
/// <remarks>
/// <b>Why name-based, and what actually guarantees safety (issue #306, review WEAK-1).</b> The
/// deterministic per-attempt re-check — the write-scope check + the task's own guardrails re-run on the
/// FINAL state of every attempt regardless of how it got there — is THE safety backstop: a re-introduced
/// gamed edit (say, an agent that recovered a salvage patch touching a protected test file) simply fails
/// the same guardrail again, so there is no false-green path. Recognizing the archetype here is only
/// <i>defense-in-depth</i>: it lets the harness avoid HANDING BACK / advertising a gamed patch in the
/// first place (suppress-at-creation of the salvage stash). Because it is not load-bearing, a name-based
/// matcher is acceptable — but it is deliberately broadened past a bare <c>"untouched"</c> substring
/// (the review's <c>03-test-files-pristine</c> counter-example slipped that) to the whole
/// "this artifact must NOT be modified" naming family. A miss degrades to "the gamed patch is offered but
/// the re-check still rejects it," never a false green; a false positive merely declines to offer a
/// salvage the agent could re-author instead (fail-safe).
/// </remarks>
internal static class GuardrailArchetypes
{
    /// <summary>
    /// Distinctive tokens marking a "the protected artifact must NOT be modified" guardrail — the
    /// <c>tests-untouched</c> doctrine archetype (issue #51) and its synonyms (a pristine/unchanged
    /// golden or authored-test check). Deliberately curated to distinctive compounds, not generic words
    /// like "preserve", to avoid false positives.
    /// </summary>
    private static readonly string[] ProtectedArtifactTokens =
    [
        "untouched", "unchanged", "unmodified", "unedited", "pristine", "immutable",
        "not-modified", "notmodified", "no-modify", "no-edit", "noedit",
        "do-not-edit", "dont-edit", "do-not-modify", "dont-modify", "read-only", "readonly"
    ];

    /// <summary>
    /// True when <paramref name="guardrailName"/> names a "protected artifact must not be modified"
    /// check (the <c>tests-untouched</c> archetype and its synonyms). Used to suppress retry-salvage of a
    /// gamed-artifact attempt at creation AND to suppress advertising it in the feedback — the same
    /// signal from one place. Case-insensitive; matches the token anywhere in the name.
    /// </summary>
    public static bool IsProtectedArtifactCheck(string guardrailName)
    {
        foreach (string token in ProtectedArtifactTokens)
        {
            if (guardrailName.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
