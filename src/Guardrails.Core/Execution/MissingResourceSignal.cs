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
    /// A workspace-relative path token: two or more '/'-joined segments, e.g. <c>vendor/mermaid.min.js</c>
    /// or <c>node_modules/@scope/x/index.js</c>. The mandatory '/' is load-bearing: prose like <c>e.g.</c>
    /// or <c>Node.js</c> never matches, which is the only thing standing between "a blocked-work question
    /// mentioned a filename" and "the harness went looking for a file to commit."
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
        var result = new List<string>();

        foreach (Match match in PathToken.Matches(question))
        {
            string token = match.Value;
            if (token.StartsWith("./", StringComparison.Ordinal))
            {
                token = token["./".Length..];
            }

            if (seen.Add(token))
            {
                result.Add(token);
            }
        }

        return result;
    }
}
