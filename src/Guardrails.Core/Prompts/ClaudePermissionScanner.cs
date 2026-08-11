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
/// <para><b>Why target extraction is from the error text, not tool-use pairing.</b> The Claude
/// permission-denial message embeds the refused target verbatim
/// (<c>"Claude requested permissions to write to &lt;path&gt;, but you haven't granted it yet."</c>, or
/// <c>"…The following part requires approval: &lt;command&gt;"</c>), so it is read straight from the
/// error rather than by pairing a <c>tool_result</c> back to its <c>tool_use</c> by id/order (which is
/// fragile across stream shapes). When a denial carries no target — a tool-level refusal such as
/// <c>"…to use Write…"</c>, or the bare <c>"This command requires approval"</c>, which names nothing at
/// all — the scanner falls back to the most recent wall-capable <c>tool_use</c>'s
/// <c>file_path</c>/<c>path</c>/<c>command</c> input, so a repeated tool-level wall is still
/// attributable to a stable key.</para>
///
/// <para><b>A refused <c>Bash</c> command is a wall too.</b> It is the same failure as a refused
/// <c>Write</c>: the call never runs and the agent burns its budget working around it. Leaving
/// <c>Bash</c> out of the tracked tool set is what let a real run refuse 86 git calls and report ZERO
/// permission walls, so it is included here and the refused COMMAND is the attribution key. The list is
/// still surfaced as <c>BlockedWritePaths</c>: to the runner-agnostic tracker a path and a refused
/// command are both just opaque, stable wall keys.</para>
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
        @"not granted permission|operation not permitted: \.claude|" +
        // The Bash/approval family: "This command requires approval" and "This <shell> command contains
        // multiple operations. The following part requires approval: <command>". Deliberately anchored on
        // the NOUN before "requires approval" rather than matching a bare "approval": tool output that
        // merely mentions the word (a git-log hit over docs about the approval prompt) must not be
        // manufactured into a wall.
        @"(?:command|part|operation|call|tool)\s+requires\s+approval",
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

    /// <summary>
    /// Extracts the refused COMMAND from the compound-Bash refusal, which names only the part it will
    /// not run: <c>"This Bash command contains multiple operations. The following part requires
    /// approval: git restore --staged src/Foo.cs"</c>. Preferred over the tracked <c>tool_use</c>
    /// command because it is the precise thing that needs granting, not the whole chain around it.
    /// Returns null for the bare <c>"This command requires approval"</c> (nothing named → fallback).
    /// </summary>
    private static readonly Regex RefusedCommandPart = new(
        @"requires\s+approval:\s*(?<command>[^\r\n]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>
    /// Tools whose refusal registers a permission wall (the ones #86 / #104 care about) — the write
    /// family PLUS <c>Bash</c>, whose refused commands were previously dropped on the floor.
    /// </summary>
    private static readonly HashSet<string> WallCapableTools =
        new(StringComparer.OrdinalIgnoreCase) { "Write", "Edit", "MultiEdit", "NotebookEdit", "Bash" };

    /// <summary>
    /// True when <paramref name="text"/> reads as a permission denial. Public so the harness-agnostic
    /// classifier and tests share one definition of the wall phrasing.
    /// </summary>
    public static bool IsPermissionDenial(string? text) =>
        !string.IsNullOrWhiteSpace(text) && DenialPhrase.IsMatch(text);

    /// <summary>
    /// Stateful, streaming scan of a <c>stream-json</c> log. Feed each raw line as it arrives (the
    /// same lines teed to <c>claude-stream.jsonl</c>); call <see cref="BlockedWritePaths"/> at the
    /// end. Tracks the most recent wall-capable <c>tool_use</c>'s target (path, or Bash command) so a
    /// denial that names nothing is still attributable. Not thread-safe (the runner's stdout callback
    /// is serialized).
    /// </summary>
    public sealed class Scanner
    {
        // Ordinal-distinct, INSERTION-ORDERED so feedback lists the walls in the order they were hit.
        private readonly List<string> _blocked = new();
        private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
        private string? _lastToolTarget;

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

        /// <summary>
        /// The distinct refused targets this scan — write paths and refused Bash commands — in
        /// first-seen order.
        /// </summary>
        public IReadOnlyList<string> BlockedWritePaths => _blocked;

        private void TrackToolUse(JsonElement root)
        {
            foreach (JsonElement block in Content(root))
            {
                if (!IsBlock(block, "tool_use") ||
                    !block.TryGetProperty("name", out JsonElement nameEl) ||
                    nameEl.ValueKind != JsonValueKind.String ||
                    !WallCapableTools.Contains(nameEl.GetString() ?? string.Empty))
                {
                    continue;
                }

                if (block.TryGetProperty("input", out JsonElement input) &&
                    input.ValueKind == JsonValueKind.Object)
                {
                    // The write family names a path; Bash names a command. Either is the key a
                    // target-less denial gets attributed to.
                    string? target = StringProp(input, "file_path") ?? StringProp(input, "path") ??
                                     StringProp(input, "notebook_path") ?? StringProp(input, "command");
                    if (!string.IsNullOrWhiteSpace(target))
                    {
                        _lastToolTarget = target;
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

                // Prefer what the message NAMES (the refused path, or the refused part of a compound
                // Bash command), and only then the tool_use it must have come from.
                string? target = ExtractRefusedCommand(text) ?? ExtractPath(text) ?? _lastToolTarget;
                if (string.IsNullOrWhiteSpace(target))
                {
                    continue;
                }

                string normalized = target.Trim().Trim('"', '\'', '`');
                if (_seen.Add(normalized))
                {
                    _blocked.Add(normalized);
                }
            }
        }

        /// <summary>
        /// The command named after <c>"…requires approval:"</c>, or null when the refusal names none.
        /// </summary>
        private static string? ExtractRefusedCommand(string text)
        {
            Match m = RefusedCommandPart.Match(text);
            if (!m.Success)
            {
                return null;
            }

            string command = m.Groups["command"].Value.Trim();
            return command.Length > 0 ? command : null;
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
