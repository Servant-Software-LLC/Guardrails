using System.Text.RegularExpressions;
using System.Text.Json;

namespace Guardrails.Core.Prompts;

/// <summary>
/// Claude-specific detection of a PERMISSION WALL — a write/edit tool call the runtime refused
/// because the path is not on the granted allow-list (issues #86 / #104). This is the SOLE home of
/// the fragile vendor permission-denial wording for the prompt pipeline; it stays inside the Claude
/// quarantine (SSOT §9) so a vendor wording change is a one-line edit here with a failing test, never
/// a change scattered through the harness. The harness routes on the runner-agnostic list of refused
/// write paths only.
///
/// <para>The signal lives in the <c>tool_result</c> events of the <c>stream-json</c> output, NOT in
/// the terminal <c>result</c> message: an <c>acceptEdits</c>/<c>default</c> permission refusal does
/// not make the agent report <c>is_error</c> on its final result — the agent keeps trying workarounds
/// and eventually exhausts turns/retries (exactly the #86 / #104 waste this detects). So the scanner
/// reads the per-tool-result error text as the stream flows by.</para>
///
/// <para><b>Why path extraction is from the error text, not tool-use pairing.</b> The Claude
/// permission-denial message embeds the refused path verbatim
/// (<c>"Claude requested permissions to write to &lt;path&gt;, but you haven't granted it yet."</c>),
/// so the path is read straight from the error rather than by pairing a <c>tool_result</c> back to its
/// <c>tool_use</c> by id/order (which is fragile across stream shapes). When a denial carries no path
/// (a tool-level refusal such as <c>"…to use Write…"</c>) the scanner falls back to the most recent
/// write-family <c>tool_use</c>'s <c>file_path</c>/<c>path</c> input, so a repeated tool-level wall is
/// still attributable to a stable key.</para>
/// </summary>
internal static class ClaudePermissionScanner
{
    /// <summary>
    /// The permission-denial phrasings the Claude Code runtime emits when a tool call is refused
    /// because the path/tool is not granted. Kept small and asserted in tests so a vendor wording
    /// change is caught here, not silently regressed. Case-insensitive.
    /// </summary>
    private static readonly Regex DenialPhrase = new(
        @"requested permissions?\s+to\s+(write|edit|use)|haven't granted it yet|" +
        @"permission to use .* was denied|permission denied by the runtime|" +
        @"not granted permission|operation not permitted: \.claude",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>
    /// Extracts the refused path from a permission-denial message that embeds it, e.g.
    /// <c>"…permissions to write to C:\repo\.claude\skills\x\SKILL.md, but you haven't granted…"</c>.
    /// Anchored on the ACTION verb (<c>write to</c> / <c>edit to</c> / <c>create</c>) so the capture
    /// starts at the path itself, not the leading <c>"…to write to"</c> noise; the path runs up to the
    /// trailing <c>", but"</c> / end-of-clause. Tolerant: returns null when the message embeds no path
    /// (a tool-level refusal such as <c>"…to use Write…"</c>, handled by the tool-use fallback).
    /// </summary>
    private static readonly Regex EmbeddedPath = new(
        @"(?:write|edit|create|access)\s+(?:to\s+)?(?<path>[^,\n]+?)(?:,\s*but\b|\s*$)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>Tools whose refusal is a WRITE wall (the ones #86 / #104 care about).</summary>
    private static readonly HashSet<string> WriteFamilyTools =
        new(StringComparer.OrdinalIgnoreCase) { "Write", "Edit", "MultiEdit", "NotebookEdit" };

    /// <summary>
    /// True when <paramref name="text"/> reads as a permission denial. Public so the harness-agnostic
    /// classifier and tests share one definition of the wall phrasing.
    /// </summary>
    public static bool IsPermissionDenial(string? text) =>
        !string.IsNullOrWhiteSpace(text) && DenialPhrase.IsMatch(text);

    /// <summary>
    /// Stateful, streaming scan of a <c>stream-json</c> log. Feed each raw line as it arrives (the
    /// same lines teed to <c>claude-stream.jsonl</c>); call <see cref="BlockedWritePaths"/> at the
    /// end. Tracks the most recent write-family <c>tool_use</c> path so a path-less denial is still
    /// attributable. Not thread-safe (the runner's stdout callback is serialized).
    /// </summary>
    public sealed class Scanner
    {
        // Ordinal-distinct, INSERTION-ORDERED so feedback lists the walls in the order they were hit.
        private readonly List<string> _blocked = new();
        private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
        private string? _lastWriteFamilyPath;

        /// <summary>Feed one raw stream line (newline excluded). Non-JSON / irrelevant lines are skipped.</summary>
        public void Feed(string line)
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
                return; // tolerant: skip garbage / partial lines, exactly like the parser/renderer
            }

            using (document)
            {
                JsonElement root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object ||
                    !root.TryGetProperty("type", out JsonElement typeElement) ||
                    typeElement.ValueKind != JsonValueKind.String)
                {
                    return;
                }

                switch (typeElement.GetString())
                {
                    case "assistant":
                        TrackToolUse(root);
                        break;
                    case "user":
                        ScanToolResults(root);
                        break;
                }
            }
        }

        /// <summary>The distinct write paths refused this scan, in first-seen order.</summary>
        public IReadOnlyList<string> BlockedWritePaths => _blocked;

        private void TrackToolUse(JsonElement root)
        {
            foreach (JsonElement block in Content(root))
            {
                if (!IsBlock(block, "tool_use") ||
                    !block.TryGetProperty("name", out JsonElement nameEl) ||
                    nameEl.ValueKind != JsonValueKind.String ||
                    !WriteFamilyTools.Contains(nameEl.GetString() ?? string.Empty))
                {
                    continue;
                }

                if (block.TryGetProperty("input", out JsonElement input) &&
                    input.ValueKind == JsonValueKind.Object)
                {
                    string? path = StringProp(input, "file_path") ?? StringProp(input, "path") ?? StringProp(input, "notebook_path");
                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        _lastWriteFamilyPath = path;
                    }
                }
            }
        }

        private void ScanToolResults(JsonElement root)
        {
            foreach (JsonElement block in Content(root))
            {
                if (!IsBlock(block, "tool_result"))
                {
                    continue;
                }

                bool isError = block.TryGetProperty("is_error", out JsonElement err) && err.ValueKind == JsonValueKind.True;
                string text = ExtractResultText(block);

                // A denial may arrive WITHOUT is_error=true (some runtimes surface it as a plain
                // tool_result), so the phrase match is authoritative — but require either is_error OR
                // an explicit denial phrase so a benign mention can't trip it.
                if (!IsPermissionDenial(text))
                {
                    continue;
                }

                _ = isError; // is_error is advisory here; the phrase is the gate.

                string? path = ExtractPath(text) ?? _lastWriteFamilyPath;
                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                string normalized = path.Trim().Trim('"', '\'', '`');
                if (_seen.Add(normalized))
                {
                    _blocked.Add(normalized);
                }
            }
        }

        private static string? ExtractPath(string text)
        {
            Match m = EmbeddedPath.Match(text);
            if (!m.Success)
            {
                return null;
            }

            string candidate = m.Groups["path"].Value.Trim();
            // Guard against capturing a tool NAME ("to use Write") rather than a path: a real path
            // has a separator or a drive letter; "use Write" / "write Foo" does not.
            bool looksLikePath = candidate.Contains('/') || candidate.Contains('\\') ||
                                 Regex.IsMatch(candidate, @"^[A-Za-z]:") || candidate.StartsWith(".claude", StringComparison.OrdinalIgnoreCase);
            return looksLikePath ? candidate : null;
        }

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

        private static bool IsBlock(JsonElement block, string type) =>
            block.ValueKind == JsonValueKind.Object &&
            block.TryGetProperty("type", out JsonElement t) &&
            t.ValueKind == JsonValueKind.String &&
            t.GetString() == type;

        private static string? StringProp(JsonElement obj, string name) =>
            obj.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : null;

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
                var sb = new System.Text.StringBuilder();
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
    }
}
