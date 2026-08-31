using System.Text.Json;
using System.Text.RegularExpressions;

namespace Guardrails.Core.Prompts;

/// <summary>
/// Recovers a JSON candidate from a model's raw final message (plan 28 §3.3/§6.4): the last fenced
/// <c>```json</c> block if one exists, else the last top-level JSON object. The candidate must parse
/// as JSON or nothing is extracted — fail closed, never a partial or a guess.
/// <para>
/// Shared by three consumers so their leniency cannot drift apart: the guardrail verdict
/// transcription path, <c>OverwatchProposal.TryParse</c>, and the needs-human triage sidecar writer.
/// </para>
/// </summary>
public static class PromptJsonExtractor
{
    /// <summary>Matches a complete fenced block: an opening ``` line (any/no info string) up to the next ```.</summary>
    private static readonly Regex FencedBlockPattern = new(@"```[^\n]*\r?\n(.*?)```", RegexOptions.Singleline);

    /// <summary>
    /// Extract the JSON candidate from <paramref name="text"/>, or <c>null</c> when none is found or
    /// the candidate does not parse.
    /// </summary>
    public static string? Extract(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        string? fenced = ExtractLastFencedBlock(text);
        if (fenced is not null)
        {
            return IsValidJson(fenced) ? fenced : null;
        }

        string? bare = ExtractLastTopLevelObject(text);
        return bare is not null && IsValidJson(bare) ? bare : null;
    }

    /// <summary>
    /// The content of the LAST fenced block in <paramref name="text"/>, or <c>null</c> when no fence
    /// marker is present at all. A fence with no closing marker (a stream cut mid-response) still
    /// counts: its body runs to the end of the text.
    /// </summary>
    private static string? ExtractLastFencedBlock(string text)
    {
        MatchCollection matches = FencedBlockPattern.Matches(text);
        if (matches.Count > 0)
        {
            return matches[^1].Groups[1].Value.Trim();
        }

        int lastFence = text.LastIndexOf("```", StringComparison.Ordinal);
        if (lastFence < 0)
        {
            return null;
        }

        int lineEnd = text.IndexOf('\n', lastFence);
        return lineEnd < 0 ? null : text[(lineEnd + 1)..].Trim();
    }

    /// <summary>
    /// The last depth-0 <c>{ ... }</c> substring in <paramref name="text"/>, ignoring braces that
    /// appear inside quoted strings, or <c>null</c> when no top-level object is found.
    /// </summary>
    private static string? ExtractLastTopLevelObject(string text)
    {
        int depth = 0;
        int candidateStart = -1;
        int lastStart = -1;
        int lastEnd = -1;
        bool inString = false;
        bool escapeNext = false;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (inString)
            {
                if (escapeNext)
                {
                    escapeNext = false;
                }
                else if (c == '\\')
                {
                    escapeNext = true;
                }
                else if (c == '"')
                {
                    inString = false;
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    inString = true;
                    break;
                case '{':
                    if (depth == 0)
                    {
                        candidateStart = i;
                    }

                    depth++;
                    break;
                case '}':
                    if (depth > 0)
                    {
                        depth--;
                        if (depth == 0 && candidateStart >= 0)
                        {
                            lastStart = candidateStart;
                            lastEnd = i;
                        }
                    }

                    break;
            }
        }

        return lastStart >= 0 ? text[lastStart..(lastEnd + 1)] : null;
    }

    private static bool IsValidJson(string candidate)
    {
        try
        {
            using JsonDocument _ = JsonDocument.Parse(candidate);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
