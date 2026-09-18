using System.Text.RegularExpressions;

namespace Guardrails.Core.Execution;

/// <summary>
/// The shared "does this halt name a workspace path?" predicate (design 41 §2.1). Moved out of
/// <c>RunCommand.MissingResourceHaltLines</c> so the halt text and the overwatcher consult read the
/// SAME signal and can never disagree about what the agent asked for.
/// </summary>
public static class MissingResourceSignal
{
    /// <summary>
    /// A workspace-relative path token: two or more '/'-joined segments, e.g. <c>vendor/mermaid.min.js</c>.
    /// A segment may carry an <c>@</c> (a scoped npm package directory, e.g. <c>node_modules/@scope/x</c>).
    /// The mandatory interior '/' is load-bearing: it is the only thing keeping prose like <c>e.g.</c> or
    /// <c>Node.js</c> from matching (design 41 §2.1).
    /// </summary>
    private static readonly Regex PathToken =
        new(@"[A-Za-z0-9_.@-]+(?:/[A-Za-z0-9_.@-]+)+", RegexOptions.Compiled);

    /// <summary>Every workspace-relative path token in <paramref name="question"/>, in order, deduplicated.</summary>
    public static IReadOnlyList<string> PathsIn(string? question)
    {
        if (string.IsNullOrEmpty(question))
        {
            return [];
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var paths = new List<string>();
        foreach (Match match in PathToken.Matches(question))
        {
            string path = match.Value.StartsWith("./", StringComparison.Ordinal)
                ? match.Value[2..]
                : match.Value;
            if (seen.Add(path))
            {
                paths.Add(path);
            }
        }

        return paths;
    }
}
