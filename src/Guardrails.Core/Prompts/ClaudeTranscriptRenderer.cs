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
        var cursorCalls = new CursorCallState();
        foreach (string line in streamJsonl.Replace("\r\n", "\n").Split('\n'))
        {
            RenderLine(line, text, cursorCalls);
        }

        cursorCalls.RenderStillOpen(text);

        // Collapse any run of 3+ blank lines to a single blank line, trim trailing whitespace.
        return Normalize(text.ToString());
    }

    private static void RenderLine(string line, StringBuilder text, CursorCallState cursorCalls)
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
            RenderDocument(document.RootElement, text, cursorCalls);
        }
    }

    /// <summary>Map one parsed stream object to its transcript fragment (shared by batch and streaming).</summary>
    private static void RenderDocument(JsonElement root, StringBuilder text, CursorCallState cursorCalls)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("type", out JsonElement typeElement) ||
            typeElement.ValueKind != JsonValueKind.String)
        {
            return;
        }

        cursorCalls.Observe(root, typeElement.GetString(), text);

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
            case "tool_call":
                RenderCursorToolCall(root, text);
                break;
            // system, rate_limit_event, etc. — telemetry, dropped. Cursor's `thinking` (delta/completed)
            // events land here too and render NOTHING: a real Cursor stream carries dozens of them.
        }
    }

    /// <summary>
    /// Stateful, streaming counterpart to <see cref="Render"/>: feed raw stream lines as they
    /// arrive and the transcript is written to the wrapped <see cref="TextWriter"/> incrementally,
    /// so a "view log" tail sees the transcript grow in real time rather than appearing only when
    /// the task finishes (issue #41). Two properties hold:
    /// <list type="bullet">
    /// <item><b>Per-line independence.</b> Each fed line is one newline-delimited JSON object,
    ///   parsed and rendered on its own as it arrives (whitespace-only and unparseable lines are
    ///   skipped). This mirrors <see cref="Render"/>, which is itself a per-line parse-and-skip over
    ///   the <c>\n</c>-split stream — so byte-identity to <see cref="Render"/> holds BY CONSTRUCTION,
    ///   and a malformed line cannot poison a later valid one. The process reader (AsyncStreamReader)
    ///   delivers complete lines, never partial chunks, so there is no "object split across feeds"
    ///   case to defend against.</item>
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
        private readonly TextWriter _writer;
        private readonly CursorCallState _cursorCalls = new();
        private int _pendingNewlines;
        private bool _wroteContent;
        private bool _completed;

        public StreamingWriter(TextWriter writer) => _writer = writer;

        /// <summary>Feed one raw stream line (newline excluded), as delivered by the process reader.</summary>
        public void Feed(string line)
        {
            // One line == one independent JSON object (mirrors RenderLine exactly): skip
            // whitespace-only lines, skip lines that don't parse on their own, render the rest.
            // A trailing '\r' on a CRLF stream needs no handling — JsonDocument.Parse treats it as
            // trailing whitespace, and rendered fragments come from parsed JSON values (content
            // newlines are JSON-escaped, not raw), so it cannot reach the output. Do NOT re-add
            // cross-line buffering "to be safe": it would break per-line independence (a malformed
            // line would poison later valid ones) and diverge from Render.
            if (string.IsNullOrWhiteSpace(line))
            {
                return;
            }

            JsonDocument? document = TryParse(line);
            if (document is null)
            {
                return; // tolerant: skip garbage / partial lines, exactly like RenderLine
            }

            using (document)
            {
                var fragment = new StringBuilder();
                RenderDocument(document.RootElement, fragment, _cursorCalls);
                EmitClamped(fragment.ToString());
            }

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

            // Calls a stream with no terminal result left open (#778) — the same lines Render appends at its end.
            var fragment = new StringBuilder();
            _cursorCalls.RenderStillOpen(fragment);
            EmitClamped(fragment.ToString());

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

    /// <summary>
    /// The one piece of cross-line state a transcript keeps (#778): Cursor's open tool calls, paired by
    /// <c>call_id</c> through the same <see cref="CursorCallPairing"/> the verdict uses. A call still open when the
    /// terminal <c>result</c> arrives renders <c>⎿ REFUSED: shell `git commit …` — abandoned: …</c> just before the
    /// final message (its <c>started</c> line is already written, so the call is named); one still open when a stream
    /// with no result ends was cut off, not refused, and renders <c>⎿ STILL RUNNING when the session ended (not
    /// refused): shell `dotnet test`</c>. Per-line
    /// independence is unchanged — a malformed line is still skipped on its own and poisons nothing — and
    /// <see cref="Render"/> and <see cref="StreamingWriter"/> share this class, so byte-identity holds. A Claude
    /// stream has no <c>tool_call</c> events and renders nothing here.
    /// </summary>
    private sealed class CursorCallState
    {
        private readonly CursorCallPairing _pairing = new();
        private bool _resultSeen;

        public void Observe(JsonElement root, string? type, StringBuilder text)
        {
            if (type == "result")
            {
                _resultSeen = true;
                RenderAbandoned(_pairing.Drain(), CursorToolCallScanner.AbandonedReason, text);
                return;
            }

            if (type == "tool_call")
            {
                _pairing.Observe(root);
            }
        }

        public void RenderStillOpen(StringBuilder text)
        {
            if (_resultSeen)
            {
                RenderAbandoned(_pairing.Drain(), CursorToolCallScanner.AbandonedReason, text);
                return;
            }

            foreach (CursorCallPairing.OpenCall call in _pairing.Drain())
            {
                InFlightToolCall cut = CursorToolCallScanner.DescribeInFlight(call);
                text.Append("  ").Append(ResultBullet).Append(" STILL RUNNING when the session ended (not refused): ")
                    .Append(Truncate(CollapseWhitespace(cut.ToString()), MaxResultLineChars + MaxArgValueChars))
                    .Append('\n');
            }
        }

        private static void RenderAbandoned(IReadOnlyList<CursorCallPairing.OpenCall> calls, string reason, StringBuilder text)
        {
            foreach (CursorCallPairing.OpenCall call in calls)
            {
                ToolRefusal refusal = CursorToolCallScanner.Describe(call.Kind, call.Args, rejected: default, reason).Refusal;
                text.Append("  ").Append(ResultBullet).Append(" REFUSED: ")
                    .Append(Truncate(CollapseWhitespace(refusal.ToString()), MaxResultLineChars + MaxArgValueChars))
                    .Append('\n');
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

    /// <summary>
    /// Cursor's tool event (#764): <c>{"type":"tool_call","subtype":"started","tool_call":{"readToolCall":
    /// {"args":{…}}}}</c>, the key varying per tool (<c>readToolCall</c>, <c>writeToolCall</c>,
    /// <c>shellToolCall</c>…). Rendered as the same <c>● name(args)</c> tool line a Claude <c>tool_use</c>
    /// gets, with the <c>ToolCall</c> suffix dropped from the name (<c>readToolCall</c> ⇒ <c>read</c>). Only
    /// the <c>started</c> event renders — its <c>completed</c> twin repeats the call with a result whose shape
    /// is per-tool and undocumented, so it is dropped rather than guessed at. Claude never emits this type,
    /// so a Claude transcript is unchanged.
    /// <para>The live <c>tool_call</c> object carries sibling keys beside the tool (<c>toolCallId</c>,
    /// <c>startedAtMs</c>, <c>hookAdditionalContexts</c>), so the tool is the first key that ends in
    /// <c>ToolCall</c> and holds an object — never simply the first key.</para>
    /// <para><b>One exception (#773):</b> a <c>completed</c> event whose result is <c>rejected</c> renders a
    /// <c>⎿ REFUSED: &lt;reason&gt;</c> line under its call. That shape IS known — it is the one
    /// <see cref="CursorToolCallScanner"/> reads — and a transcript that shows a refused <c>git status</c> as a
    /// plain tool line reads as if it ran. A call that STARTED and never completed (#778) gets its
    /// <c>REFUSED</c> line from <see cref="CursorCallState"/> instead, at the terminal result.</para>
    /// </summary>
    private static void RenderCursorToolCall(JsonElement root, StringBuilder text)
    {
        if (!root.TryGetProperty("subtype", out JsonElement subtype) ||
            subtype.ValueKind != JsonValueKind.String ||
            !root.TryGetProperty("tool_call", out JsonElement call) ||
            call.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (subtype.GetString() == "completed")
        {
            RenderCursorRefusal(call, text);
            return;
        }

        if (subtype.GetString() != "started")
        {
            return;
        }

        const string suffix = "ToolCall";
        foreach (JsonProperty tool in call.EnumerateObject())
        {
            if (tool.Value.ValueKind != JsonValueKind.Object ||
                !tool.Name.EndsWith(suffix, StringComparison.Ordinal) ||
                tool.Name.Length <= suffix.Length)
            {
                continue;
            }

            string name = tool.Name[..^suffix.Length];
            string args = tool.Value.TryGetProperty("args", out JsonElement input)
                ? RenderToolArgs(input)
                : string.Empty;
            text.Append(ToolBullet).Append(' ').Append(name).Append('(').Append(args).Append(')').Append('\n');
            return; // one tool per event
        }
    }

    /// <summary>The <c>⎿ REFUSED: …</c> line for a completed Cursor tool call whose result is <c>rejected</c> (#773).</summary>
    private static void RenderCursorRefusal(JsonElement call, StringBuilder text)
    {
        foreach (JsonProperty tool in call.EnumerateObject())
        {
            if (tool.Value.ValueKind != JsonValueKind.Object ||
                !tool.Name.EndsWith("ToolCall", StringComparison.Ordinal) ||
                !tool.Value.TryGetProperty("result", out JsonElement result) ||
                result.ValueKind != JsonValueKind.Object ||
                !result.TryGetProperty("rejected", out JsonElement rejected))
            {
                continue;
            }

            string reason = rejected.ValueKind == JsonValueKind.Object &&
                            rejected.TryGetProperty("reason", out JsonElement r) &&
                            r.ValueKind == JsonValueKind.String &&
                            !string.IsNullOrWhiteSpace(r.GetString())
                ? CollapseWhitespace(r.GetString()!)
                : CursorToolCallScanner.NoReasonGiven;
            text.Append("  ").Append(ResultBullet).Append(" REFUSED: ")
                .Append(Truncate(reason, MaxArgValueChars * 2)).Append('\n');
            return;
        }
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
