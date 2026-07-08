namespace Guardrails.Core.Execution;

/// <summary>
/// Extracts the operator-facing failure REASON from a failed check's captured output for the
/// PLAN-LEVEL gates (<c>&lt;plan&gt;/preflights/</c> and <c>&lt;plan&gt;/guardrails/</c>, run via
/// <see cref="GuardrailReVerifier"/>) — issue #272 Part 1, the plan-level analogue of #179.
/// <para>
/// A plan-level gate's <c>reason</c> is the ONLY signal an operator (or the #269 overwatcher) gets:
/// unlike a task-level guardrail — whose one-line reason is a UI label while its FULL output is carried
/// SEPARATELY into <c>feedback.md</c>'s tail (issue #26 Gap 1 / #179) — a plan gate does not retry and
/// composes no feedback, so nothing else surfaces the detail. The reason itself must therefore carry the
/// actual failure. The #179 convention re-emits that detail at the END of stdout (a guardrail's preamble —
/// an <c>npm ci</c>, a <c>dotnet restore</c>, an <c>echo</c> — sits at the START), so the reason must be
/// drawn from the TAIL. Taking the FIRST non-empty line (the pre-#272 behaviour) surfaced the preamble
/// noise (<c>added 464 packages, and audited 465 packages in 24s</c>) and hid the real cause (a vitest
/// error further down).
/// </para>
/// </summary>
internal static class GuardrailFailureReason
{
    /// <summary>Max NON-EMPTY lines carried from the tail of the output into the reason.</summary>
    private const int MaxTailLines = 15;

    /// <summary>Hard cap on the reason length; the LAST <see cref="MaxChars"/> chars win (tail-biased).</summary>
    private const int MaxChars = 2000;

    /// <summary>
    /// The last (up to <see cref="MaxTailLines"/>) NON-EMPTY lines of <paramref name="text"/>, joined by
    /// newlines and bounded to <see cref="MaxChars"/> from the end. Returns null when the text is empty or
    /// all-whitespace, so the caller can fall through to the next source (stderr tail, then the exit code).
    /// Each carried line is trimmed; interior blank lines are dropped so the re-emitted failure block reads
    /// cleanly even when the guardrail spaced it out with separators.
    /// </summary>
    public static string? Tail(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        string normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var nonEmpty = new List<string>();
        foreach (string line in normalized.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.Length > 0)
            {
                nonEmpty.Add(trimmed);
            }
        }

        if (nonEmpty.Count == 0)
        {
            return null;
        }

        int start = Math.Max(0, nonEmpty.Count - MaxTailLines);
        string joined = string.Join('\n', nonEmpty.Skip(start));
        return joined.Length > MaxChars ? joined[^MaxChars..] : joined;
    }
}
