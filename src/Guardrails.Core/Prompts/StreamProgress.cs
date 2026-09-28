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
    /// A <c>stream-json</c> line (Claude Code and Cursor share the envelope) is progress when it is:
    /// <list type="bullet">
    ///   <item><c>assistant</c> — model output (text or a <c>tool_use</c>);</item>
    ///   <item><c>user</c> carrying a <c>tool_result</c> — a tool call came back (a plain user echo does not
    ///         count: Cursor echoes the prompt once, and Claude re-injects a summary after a compaction);</item>
    ///   <item><c>result</c> — the terminal result;</item>
    ///   <item><c>system/thinking_tokens</c> with <c>estimated_tokens_delta &gt; 0</c> — Claude Code's running count of
    ///         the model's reasoning. The model is generating; the most common line in real captures (11,778 in 58
    ///         captured sessions);</item>
    ///   <item><c>tool_progress</c> — Claude Code's heartbeat for a running tool call, about every 30 s with an
    ///         <c>elapsed_time_seconds</c>. A long build or test is therefore NOT silent on the stream. A tool that
    ///         hangs forever is bounded by Claude Code's own per-call timeout and by the attempt timeout, not by
    ///         this watchdog;</item>
    ///   <item><c>system/compact_boundary</c> — a compaction that SUCCEEDED, which is progress (the status lines
    ///         around it are not);</item>
    ///   <item><c>tool_call</c> and <c>thinking</c> — Cursor's tool-call and reasoning events;</item>
    ///   <item><c>stream_event</c> — Claude's partial-message deltas. Kept for completeness: the harness does not pass
    ///         <c>--include-partial-messages</c>, so none arrive today (0 in the same 58 captures).</item>
    /// </list>
    /// Everything else is not: every other <c>system</c> line (<c>init</c>, <c>status</c> including
    /// <c>compacting</c>, hooks, task bookkeeping), <c>rate_limit_event</c>, any type not listed, and any line that is
    /// not a JSON object. The list is closed on purpose: an unknown line type that beat would reopen the hole this
    /// closes, and the cost of the other direction is bounded, because the session still has to emit real output
    /// within the bound.
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
                "assistant" or "result" or "tool_progress" or "tool_call" or "thinking" or "stream_event" => true,
                "user" => CarriesToolResult(root),
                "system" => IsProgressSystemLine(root),
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

    private static bool IsProgressSystemLine(JsonElement root)
    {
        if (!root.TryGetProperty("subtype", out JsonElement subtype) || subtype.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        return subtype.GetString() switch
        {
            "compact_boundary" => true,
            "thinking_tokens" => root.TryGetProperty("estimated_tokens_delta", out JsonElement delta)
                                 && delta.ValueKind == JsonValueKind.Number
                                 && delta.TryGetDouble(out double value)
                                 && value > 0,
            _ => false
        };
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
