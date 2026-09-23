namespace Guardrails.Core.Execution;

/// <summary>
/// The harness's one cap on how much captured text is shown to a reader: the LAST <see cref="MaxLines"/> lines,
/// then the LAST <see cref="MaxChars"/> characters of those. Tail-biased on purpose: a failing check re-emits its
/// failure detail at the END of its output (#179), after the preamble. Shared by <c>feedback.md</c>'s output tails
/// (<see cref="RetryPolicy"/>) and the console's gate-halt block (#762), so the operator and the retrying agent
/// see the same cut of the same text.
/// </summary>
public static class OutputTail
{
    /// <summary>The most lines kept, counted from the end.</summary>
    public const int MaxLines = 60;

    /// <summary>The most characters kept, counted from the end, after the line cut.</summary>
    public const int MaxChars = 4000;

    /// <summary>
    /// The tail of <paramref name="content"/> (trailing whitespace trimmed first), and whether anything was cut.
    /// Line breaks are split on <c>'\n'</c> exactly as <c>feedback.md</c> always has, so a <c>\r</c> stays on its
    /// line for callers that normalize afterwards.
    /// </summary>
    public static (string Text, bool Truncated) Take(string content)
    {
        string trimmed = content.TrimEnd();
        string[] lines = trimmed.Split('\n');
        bool truncated = lines.Length > MaxLines;
        string joined = string.Join('\n', truncated ? lines[^MaxLines..] : lines);
        if (joined.Length > MaxChars)
        {
            joined = joined[^MaxChars..];
            truncated = true;
        }

        return (joined, truncated);
    }
}
