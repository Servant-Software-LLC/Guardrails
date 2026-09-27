using System.Text.Json;
using System.Text.RegularExpressions;

namespace Guardrails.Core.Bundle;

/// <summary>
/// Pass 4's first rule (SSOT §17.6.4): a stream log whose runner emitted <c>*_delta</c> events can split a value
/// across lines, so it is ALSO scanned as the concatenated delta text of each content block. The joined view maps
/// every character back to its own event's string, so a value split across two events is scrubbed in both, and the
/// JSON around each piece is untouched.
/// <para>
/// Recognized shapes: an Anthropic-style <c>content_block_delta</c> (<c>delta.text</c>, <c>delta.partial_json</c>,
/// <c>delta.thinking</c>, grouped by <c>index</c>, a <c>content_block_start</c> or <c>message_start</c> opening a new
/// group) and an OpenAI-style <c>choices[n].delta.content</c> (grouped by choice). A line that does not parse, or that
/// carries no delta string, contributes nothing.
/// </para>
/// </summary>
internal static partial class StreamDeltaJoin
{
    /// <summary>The joined view, or null when the text has no delta event.</summary>
    public static RedactionView? Build(string text)
    {
        if (!text.Contains("delta\"", StringComparison.Ordinal))
        {
            return null;
        }

        var groups = new Dictionary<string, RedactionView.Builder>(StringComparer.Ordinal);
        var order = new List<string>();
        var generation = new Dictionary<string, int>(StringComparer.Ordinal);
        int lineStart = 0;
        while (lineStart < text.Length)
        {
            int newline = text.IndexOf('\n', lineStart);
            int lineEnd = newline < 0 ? text.Length : newline;
            AddLine(text, lineStart, lineEnd, groups, order, generation);
            lineStart = lineEnd + 1;
        }

        if (order.Count == 0)
        {
            return null;
        }

        var joined = new RedactionView.Builder(text.Length);
        foreach (string key in order)
        {
            RedactionView piece = groups[key].Build();
            for (int i = 0; i < piece.Text.Length; i++)
            {
                joined.Add(piece.Text[i], piece.Start[i], piece.End[i]);
            }

            joined.AddSeparator();
        }

        return joined.Build();
    }

    private static void AddLine(
        string text, int lineStart, int lineEnd,
        Dictionary<string, RedactionView.Builder> groups, List<string> order, Dictionary<string, int> generation)
    {
        string line = text[lineStart..lineEnd];
        if (!line.Contains("delta", StringComparison.Ordinal) && !line.Contains("_start\"", StringComparison.Ordinal))
        {
            return;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            return;
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            string? type = root.TryGetProperty("type", out JsonElement typeElement) && typeElement.ValueKind == JsonValueKind.String
                ? typeElement.GetString()
                : null;

            if (type is "message_start")
            {
                // A new message: every open block starts a fresh group.
                foreach (string open in generation.Keys.ToList())
                {
                    generation[open]++;
                }

                return;
            }

            if (type is "content_block_start")
            {
                string startKey = "block-" + IndexOf(root);
                generation[startKey] = generation.GetValueOrDefault(startKey) + 1;
                return;
            }

            string groupKey;
            string[] fields;
            if (type is "content_block_delta" && root.TryGetProperty("delta", out JsonElement delta) && delta.ValueKind == JsonValueKind.Object)
            {
                groupKey = "block-" + IndexOf(root);
                fields = ["text", "partial_json", "thinking"];
                if (!fields.Any(f => delta.TryGetProperty(f, out JsonElement v) && v.ValueKind == JsonValueKind.String))
                {
                    return;
                }
            }
            else if (root.TryGetProperty("choices", out JsonElement choices) && choices.ValueKind == JsonValueKind.Array
                && choices.GetArrayLength() > 0 && choices[0].ValueKind == JsonValueKind.Object
                && choices[0].TryGetProperty("delta", out JsonElement choiceDelta) && choiceDelta.ValueKind == JsonValueKind.Object
                && choiceDelta.TryGetProperty("content", out JsonElement content) && content.ValueKind == JsonValueKind.String)
            {
                groupKey = "choice-" + IndexOf(choices[0]);
                fields = ["content"];
            }
            else
            {
                return;
            }

            // Locate the delta string's own token in the raw line: the key after `"delta"`, unescaped (an escaped
            // `\"text\"` inside another string is not it).
            int deltaAt = line.IndexOf("\"delta\"", StringComparison.Ordinal);
            if (deltaAt < 0)
            {
                return;
            }

            Match key = DeltaStringKey().Match(line, deltaAt);
            while (key.Success && !fields.Contains(key.Groups["key"].Value, StringComparer.Ordinal))
            {
                key = key.NextMatch();
            }

            if (!key.Success)
            {
                return;
            }

            string fullKey = groupKey + "#" + generation.GetValueOrDefault(groupKey);
            if (!groups.TryGetValue(fullKey, out RedactionView.Builder? builder))
            {
                builder = new RedactionView.Builder(64);
                groups[fullKey] = builder;
                order.Add(fullKey);
            }

            AppendDecodedString(text, lineStart + key.Index + key.Length, lineEnd, builder);
        }
    }

    /// <summary>Decode the JSON string whose body starts at <paramref name="from"/>, mapping each character back.</summary>
    private static void AppendDecodedString(string text, int from, int limit, RedactionView.Builder builder)
    {
        int close = from;
        while (close < limit && text[close] != '"')
        {
            close += text[close] == '\\' ? 2 : 1;
        }

        close = Math.Min(close, limit);
        var body = RedactionView.Identity(text[from..close]);
        RedactionView decoded = body.JsonUnescaped() ?? body;
        for (int i = 0; i < decoded.Text.Length; i++)
        {
            builder.Add(decoded.Text[i], decoded.Start[i] + from, decoded.End[i] + from);
        }
    }

    private static string IndexOf(JsonElement element) =>
        element.TryGetProperty("index", out JsonElement index) && index.ValueKind == JsonValueKind.Number
            ? index.GetRawText()
            : "0";

    [GeneratedRegex(@"(?<!\\)""(?<key>[a-z_]+)""\s*:\s*""", RegexOptions.CultureInvariant)]
    private static partial Regex DeltaStringKey();
}
