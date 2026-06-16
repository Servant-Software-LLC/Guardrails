using System.Text;
using System.Text.Json;

namespace Guardrails.Core.Prompts;

/// <summary>
/// Renders a Claude Code <c>stream-json</c> log (<c>claude-stream.jsonl</c>) into a compact,
/// human-readable <c>transcript.md</c> — the CLI-equivalent view (issue #27). The raw stream
/// is the canonical debug artifact but is ~80% telemetry (thinking-token counters, rate-limit
/// events, init/usage blocks, full tool-result dumps); it is the wrong thing to feed to a
/// dependent task's agent (issue #26) or to a human skimming "what happened".
///
/// This is a PURE, DETERMINISTIC transformation: the SAME stream in ⇒ byte-identical transcript
/// out, every run. No model is in the loop — every line maps to its rendering by a fixed rule.
/// (Different runs produce different streams, so the transcript naturally differs run-to-run;
/// the guarantee is over identical input.) For a given stream the transcript a downstream task
/// reads cannot hallucinate or drop a tool call. Quarantined here with the other Claude-specific
/// parsing (SSOT §9).
///
/// Mapping:
/// <list type="bullet">
/// <item><c>assistant</c> → <c>text</c> blocks become prose; <c>tool_use</c> blocks become
///   <c>● Tool(args)</c>; <c>thinking</c> blocks are dropped (reasoning, not output).</item>
/// <item><c>user</c> → <c>tool_result</c> blocks become a truncated <c>⎿ summary</c> line.</item>
/// <item><c>result</c> → the final agent message (its <c>result</c> text only).</item>
/// <item><c>system</c>/<c>rate_limit_event</c> and all telemetry are dropped.</item>
/// </list>
/// </summary>
public static class ClaudeTranscriptRenderer
{
    /// <summary>Long scalar tool-arg values are truncated to this many chars (+ ellipsis).</summary>
    private const int MaxArgValueChars = 80;

    /// <summary>The whole rendered tool-arg list is capped at this many chars.</summary>
    private const int MaxArgListChars = 200;

    /// <summary>A tool-result summary line is capped at this many chars.</summary>
    private const int MaxResultLineChars = 200;

    private const char ToolBullet = '●';   // ●
    private const char ResultBullet = '⎿';  // ⎿
    private const char FinalBullet = '⏺';   // ⏺

    /// <summary>
    /// Render a whole <c>stream-json</c> log into the transcript. Lines are independent JSON
    /// objects; unparseable or irrelevant lines are skipped (tolerant, like the parser).
    /// </summary>
    public static string Render(string streamJsonl)
    {
        var text = new StringBuilder();
        foreach (string line in streamJsonl.Replace("\r\n", "\n").Split('\n'))
        {
            RenderLine(line, text);
        }

        // Collapse any run of 3+ blank lines to a single blank line, trim trailing whitespace.
        return Normalize(text.ToString());
    }

    private static void RenderLine(string line, StringBuilder text)
    {
        if (string.IsNullOrWhiteSpace(line))
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
            return; // tolerant: skip garbage / partial lines
        }

        using (document)
        {
            RenderDocument(document.RootElement, text);
        }
    }

    /// <summary>Map one parsed stream object to its transcript fragment (shared by batch and streaming).</summary>
    private static void RenderDocument(JsonElement root, StringBuilder text)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("type", out JsonElement typeElement) ||
            typeElement.ValueKind != JsonValueKind.String)
        {
            return;
        }

        switch (typeElement.GetString())
        {
            case "assistant":
                RenderAssistant(root, text);
                break;
            case "user":
                RenderUser(root, text);
                break;
            case "result":
                RenderResult(root, text);
                break;
            // system, rate_limit_event, etc. — telemetry, dropped.
        }
    }

    /// <summary>
    /// Stateful, streaming counterpart to <see cref="Render"/>: feed raw stream lines as they
    /// arrive and the transcript is written to the wrapped <see cref="TextWriter"/> incrementally,
    /// so a "view log" tail sees the transcript grow in real time rather than appearing only when
    /// the task finishes (issue #41). Two properties hold:
    /// <list type="bullet">
    /// <item><b>Chunk-boundary safe.</b> Lines are buffered until they parse as a complete JSON
    ///   object, so an object split between two stream chunks still renders once its closing brace
    ///   arrives (newline-delimited JSON normally lands one object per line, but this is defensive).</item>
    /// <item><b>Byte-identical at completion.</b> After <see cref="Complete"/>, the written file
    ///   equals <see cref="Render"/> over the same concatenated stream: a pending-newline counter
    ///   carries the 3+→blank-line collapse across feeds, and the trailing newline is finalized in
    ///   <see cref="Complete"/> exactly as <c>Normalize</c> does.</item>
    /// </list>
    /// Not thread-safe: feed from a single sequence of calls (the runner's stdout callback is
    /// serialized, so this holds there).
    /// </summary>
    public sealed class StreamingWriter
    {
        // A genuinely malformed line can never complete into valid JSON; cap the buffer so it
        // can't grow without bound. The raw line is still preserved in claude-stream.jsonl (the
        // canonical artifact), so dropping it from the transcript loses nothing recoverable.
        private const int MaxBufferChars = 1_000_000;

        private readonly TextWriter _writer;
        private readonly StringBuilder _jsonBuffer = new();
        private int _pendingNewlines;
        private bool _wroteContent;
        private bool _completed;

        public StreamingWriter(TextWriter writer) => _writer = writer;

        /// <summary>Feed one raw stream line (newline excluded), as delivered by the process reader.</summary>
        public void Feed(string line)
        {
            // Skip whitespace-only input only when nothing is buffered (matches Render's tolerance);
            // once a partial object is buffered, every line is part of it until it parses.
            if (_jsonBuffer.Length == 0 && string.IsNullOrWhiteSpace(line))
            {
                return;
            }

            if (_jsonBuffer.Length > 0)
            {
                _jsonBuffer.Append('\n');
            }

            _jsonBuffer.Append(line);

            JsonDocument? document = TryParse(_jsonBuffer.ToString());
            if (document is null)
            {
                // Not yet a complete object — could be a chunk boundary; keep buffering. Drop the
                // buffer if it overflows the cap (malformed, never-closing line).
                if (_jsonBuffer.Length > MaxBufferChars)
                {
                    _jsonBuffer.Clear();
                }

                return;
            }

            using (document)
            {
                var fragment = new StringBuilder();
                RenderDocument(document.RootElement, fragment);
                EmitClamped(fragment.ToString());
            }

            _jsonBuffer.Clear();
            _writer.Flush();
        }

        /// <summary>Finalize the transcript: emit the single trailing newline a non-empty transcript ends with.</summary>
        public void Complete()
        {
            if (_completed)
            {
                return;
            }

            _completed = true;
            if (_wroteContent)
            {
                _writer.Write('\n'); // Normalize ends a non-empty transcript with exactly one newline.
            }

            _writer.Flush();
        }

        // Stream a fragment through the same newline policy as Normalize: a run of newlines is
        // held (not written) until the next non-newline char, then flushed clamped to at most 2
        // (3+ blank lines collapse to one). Trailing newlines therefore stay pending — which also
        // gives the trailing-trim for free, finalized by Complete().
        private void EmitClamped(string fragment)
        {
            foreach (char c in fragment)
            {
                if (c == '\n')
                {
                    _pendingNewlines++;
                    continue;
                }

                int run = Math.Min(_pendingNewlines, 2);
                for (int i = 0; i < run; i++)
                {
                    _writer.Write('\n');
                }

                _pendingNewlines = 0;
                _writer.Write(c);
                _wroteContent = true;
            }
        }

        private static JsonDocument? TryParse(string candidate)
        {
            try
            {
                return JsonDocument.Parse(candidate);
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }

    private static void RenderAssistant(JsonElement root, StringBuilder text)
    {
        foreach (JsonElement block in Content(root))
        {
            if (block.ValueKind != JsonValueKind.Object ||
                !block.TryGetProperty("type", out JsonElement blockType) ||
                blockType.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            switch (blockType.GetString())
            {
                case "text":
                    string prose = block.TryGetProperty("text", out JsonElement t) && t.ValueKind == JsonValueKind.String
                        ? (t.GetString() ?? string.Empty).Trim()
                        : string.Empty;
                    if (prose.Length > 0)
                    {
                        text.Append(prose).Append('\n').Append('\n');
                    }

                    break;
                case "tool_use":
                    string name = block.TryGetProperty("name", out JsonElement n) && n.ValueKind == JsonValueKind.String
                        ? n.GetString() ?? "tool"
                        : "tool";
                    string args = block.TryGetProperty("input", out JsonElement input)
                        ? RenderToolArgs(input)
                        : string.Empty;
                    text.Append(ToolBullet).Append(' ').Append(name).Append('(').Append(args).Append(')').Append('\n');
                    break;
                // thinking — intentionally dropped.
            }
        }
    }

    private static void RenderUser(JsonElement root, StringBuilder text)
    {
        foreach (JsonElement block in Content(root))
        {
            if (block.ValueKind != JsonValueKind.Object ||
                !block.TryGetProperty("type", out JsonElement blockType) ||
                blockType.ValueKind != JsonValueKind.String ||
                blockType.GetString() != "tool_result")
            {
                continue;
            }

            bool isError = block.TryGetProperty("is_error", out JsonElement err) && err.ValueKind == JsonValueKind.True;
            string summary = SummarizeToolResult(block);
            text.Append("  ").Append(ResultBullet).Append(' ');
            if (isError)
            {
                text.Append("Error: ");
            }

            text.Append(summary).Append('\n');
        }
    }

    private static void RenderResult(JsonElement root, StringBuilder text)
    {
        if (!root.TryGetProperty("result", out JsonElement result) || result.ValueKind != JsonValueKind.String)
        {
            return;
        }

        string final = (result.GetString() ?? string.Empty).Trim();
        if (final.Length == 0)
        {
            return;
        }

        text.Append('\n').Append(FinalBullet).Append(' ').Append(final).Append('\n');
    }

    /// <summary>The <c>message.content</c> array, or empty when absent/malformed.</summary>
    private static IEnumerable<JsonElement> Content(JsonElement root)
    {
        if (root.TryGetProperty("message", out JsonElement message) &&
            message.ValueKind == JsonValueKind.Object &&
            message.TryGetProperty("content", out JsonElement content) &&
            content.ValueKind == JsonValueKind.Array)
        {
            return content.EnumerateArray();
        }

        return [];
    }

    /// <summary>
    /// Render a tool-use <c>input</c> object as a compact, single-line arg list. Scalar values
    /// (string/number/bool) are shown as <c>key: value</c> in document order (deterministic);
    /// complex values become <c>key: […]</c> / <c>key: {…}</c>. Long values and the whole list
    /// are length-capped so a Write/Edit payload never bloats the transcript.
    /// </summary>
    private static string RenderToolArgs(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }

        var parts = new List<string>();
        foreach (JsonProperty property in input.EnumerateObject())
        {
            string rendered = property.Value.ValueKind switch
            {
                JsonValueKind.String => Truncate(CollapseWhitespace(property.Value.GetString() ?? string.Empty), MaxArgValueChars),
                JsonValueKind.Number => property.Value.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                JsonValueKind.Array => "[…]",
                JsonValueKind.Object => "{…}",
                _ => null!
            };

            if (rendered is not null)
            {
                parts.Add($"{property.Name}: {rendered}");
            }
        }

        return Truncate(string.Join(", ", parts), MaxArgListChars);
    }

    /// <summary>
    /// Summarize a <c>tool_result</c> block to one line: the CLI's <c>⎿</c> view. Single-line
    /// output is shown (truncated); multi-line output shows the first line plus a count of the
    /// remainder. The result content is either a string or an array of text blocks.
    /// </summary>
    private static string SummarizeToolResult(JsonElement block)
    {
        string content = ExtractResultText(block);

        // Count non-blank lines; the first non-blank is shown, the rest summarized as a count.
        string[] nonEmpty = content.Replace("\r\n", "\n").Split('\n')
            .Where(l => l.Trim().Length > 0)
            .ToArray();

        if (nonEmpty.Length == 0)
        {
            return "(no output)";
        }

        string first = Truncate(nonEmpty[0].Trim(), MaxResultLineChars);
        int more = nonEmpty.Length - 1;
        return more > 0
            ? $"{first} … (+{more} more line{(more == 1 ? "" : "s")})"
            : first;
    }

    private static string ExtractResultText(JsonElement block)
    {
        if (!block.TryGetProperty("content", out JsonElement content))
        {
            return string.Empty;
        }

        if (content.ValueKind == JsonValueKind.String)
        {
            return content.GetString() ?? string.Empty;
        }

        if (content.ValueKind == JsonValueKind.Array)
        {
            var sb = new StringBuilder();
            foreach (JsonElement item in content.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object &&
                    item.TryGetProperty("text", out JsonElement itemText) &&
                    itemText.ValueKind == JsonValueKind.String)
                {
                    if (sb.Length > 0)
                    {
                        sb.Append('\n');
                    }

                    sb.Append(itemText.GetString());
                }
            }

            return sb.ToString();
        }

        return string.Empty;
    }

    private static string CollapseWhitespace(string value)
    {
        var sb = new StringBuilder(value.Length);
        bool lastWasSpace = false;
        foreach (char c in value)
        {
            if (char.IsWhiteSpace(c))
            {
                if (!lastWasSpace)
                {
                    sb.Append(' ');
                }

                lastWasSpace = true;
            }
            else
            {
                sb.Append(c);
                lastWasSpace = false;
            }
        }

        return sb.ToString().Trim();
    }

    private static string Truncate(string value, int max)
    {
        if (value.Length <= max)
        {
            return value;
        }

        // Don't slice between the halves of a surrogate pair: if the char immediately before the
        // cut is a high surrogate, value[max] is its low surrogate, so backing the cut off by one
        // keeps the pair whole (or drops the orphaned high surrogate when max == 1). Deterministic.
        int cut = max > 0 && char.IsHighSurrogate(value[max - 1]) ? max - 1 : max;
        return value[..cut].TrimEnd() + "…";
    }

    /// <summary>Collapse 3+ consecutive newlines to a blank line; trim trailing whitespace; end with one newline.</summary>
    private static string Normalize(string text)
    {
        var sb = new StringBuilder(text.Length);
        int newlineRun = 0;
        foreach (char c in text)
        {
            if (c == '\n')
            {
                newlineRun++;
                if (newlineRun <= 2)
                {
                    sb.Append('\n');
                }
            }
            else
            {
                newlineRun = 0;
                sb.Append(c);
            }
        }

        string result = sb.ToString().TrimEnd('\n', ' ', '\t');
        return result.Length == 0 ? result : result + "\n";
    }
}
