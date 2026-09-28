using System.Text.Json;

namespace Guardrails.Core.Prompts;

/// <summary>
/// Which runner output counts as PROGRESS for the stall watchdog (#811). <see cref="StallWatch.Beat"/> restarts the
/// silence window, so beating on a line that is not progress lets a wedged session live forever. The motivating
/// case: a Claude Code session stuck in an auto-compaction that never returned emitted
/// <c>{"type":"system","subtype":"status","status":"compacting"}</c> more than 70 times over six hours, and nothing
/// else. Every one of those lines used to beat.
/// </summary>
internal static class StreamProgress
{
    /// <summary>
    /// A <c>stream-json</c> line (Claude Code and Cursor share the envelope) is progress when its top-level
    /// <c>type</c> is:
    /// <list type="bullet">
    ///   <item><c>assistant</c> — model output (text or a <c>tool_use</c>);</item>
    ///   <item><c>user</c> carrying a <c>tool_result</c> — a tool call came back (a plain user echo does not
    ///         count: Cursor echoes the prompt once, and Claude re-injects a summary after a compaction);</item>
    ///   <item><c>result</c> — the terminal result;</item>
    ///   <item><c>tool_call</c> and <c>thinking</c> — Cursor's tool-call and reasoning events;</item>
    ///   <item><c>stream_event</c> — Claude's partial-message deltas, when enabled.</item>
    /// </list>
    /// Everything else is not: every <c>system</c> line (<c>init</c>, <c>status</c> including
    /// <c>compacting</c>, <c>compact_boundary</c>, hooks), any type not listed, and any line that is not a JSON
    /// object. The list is closed on purpose: an unknown line type that beat would reopen the hole this closes, and
    /// the cost of the other direction is bounded, because the session still has to emit real output within the
    /// bound.
    /// </summary>
    internal static bool IsStreamJsonProgress(string line)
    {
        if (string.IsNullOrWhiteSpace(line) || line.AsSpan().TrimStart()[0] != '{')
        {
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("type", out JsonElement type)
                || type.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            return type.GetString() switch
            {
                "assistant" or "result" or "tool_call" or "thinking" or "stream_event" => true,
                "user" => CarriesToolResult(root),
                _ => false
            };
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// A Server-Sent Events line from an OpenAI-compatible endpoint is progress when it is a <c>data:</c> frame with a
    /// payload, or a line of a non-streamed whole body (a server that ignored <c>"stream": true</c>). A blank line,
    /// an SSE comment (<c>: keep-alive</c>) and the <c>event:</c>/<c>id:</c>/<c>retry:</c> fields are not: a proxy
    /// that sends keep-alive comments forever while the model produces nothing is the SSE form of the same wedge.
    /// </summary>
    internal static bool IsSseProgress(string line)
    {
        if (string.IsNullOrWhiteSpace(line) || line[0] == ':')
        {
            return false;
        }

        if (line.StartsWith("data:", StringComparison.Ordinal))
        {
            return line.AsSpan(5).Trim().Length > 0;
        }

        return !(line.StartsWith("event:", StringComparison.Ordinal)
                 || line.StartsWith("id:", StringComparison.Ordinal)
                 || line.StartsWith("retry:", StringComparison.Ordinal));
    }

    /// <summary>Beat <paramref name="watch"/> when <paramref name="line"/> is stream-json progress. The one call the session makes.</summary>
    internal static void BeatOnStreamJsonProgress(StallWatch? watch, string line)
    {
        if (watch is not null && IsStreamJsonProgress(line))
        {
            watch.Beat();
        }
    }

    private static bool CarriesToolResult(JsonElement root) =>
        root.TryGetProperty("message", out JsonElement message)
        && message.ValueKind == JsonValueKind.Object
        && message.TryGetProperty("content", out JsonElement content)
        && content.ValueKind == JsonValueKind.Array
        && content.EnumerateArray().Any(block =>
            block.ValueKind == JsonValueKind.Object
            && block.TryGetProperty("type", out JsonElement blockType)
            && blockType.ValueKind == JsonValueKind.String
            && blockType.GetString() == "tool_result");
}
