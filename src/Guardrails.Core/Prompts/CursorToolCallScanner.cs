using System.Text.Json;

namespace Guardrails.Core.Prompts;

/// <summary>
/// Cursor-specific detection of REFUSED tool calls (#773), inside the Cursor quarantine (SSOT §9.9) exactly as
/// <see cref="ClaudePermissionScanner"/> is inside Claude's. The harness routes on the runner-agnostic lists it
/// produces, never on Cursor's stream shape.
///
/// <para><b>Why it exists.</b> Measured on an enterprise account (Cursor <c>agent</c> 2026.09.18–09.23): in print
/// mode without an approval flag, every shell call completes as
/// <c>{"type":"tool_call","subtype":"completed","tool_call":{"shellToolCall":{"result":{"rejected":{"command":
/// "git status","workingDirectory":"","reason":""}}}}}</c> — and the session STILL exits 0 with a terminal
/// <c>result</c> of <c>subtype: "success"</c>. Under <c>--auto-review</c> the server-side classifier can refuse
/// an individual command the same way, and a Claude Code hook that Cursor imported ("Include Third-Party
/// Configs") refuses with a reason such as <c>"Hook blocked with message: …"</c>. None of that is visible in
/// the terminal result, so it is read per call here.</para>
///
/// <para><b>What it reads.</b> Only <c>tool_call</c> events with <c>subtype: "completed"</c>; the tool is the
/// first key of <c>tool_call</c> that ends in <c>ToolCall</c> and holds an object (the object also carries
/// sibling keys such as <c>toolCallId</c>). A completed call whose <c>result</c> has a <c>rejected</c> member
/// is a REFUSAL; any other completed call RAN. For a refused shell call <c>args</c> may be absent — the command
/// is then <c>result.rejected.command</c>. An edit/write call names its target in <c>args.path</c>.</para>
///
/// <para><b>What it produces.</b></para>
/// <list type="bullet">
/// <item>Every refusal, in order, with its reason (<see cref="Refusals"/>); an empty reason reads
/// <see cref="NoReasonGiven"/>.</item>
/// <item><see cref="BlockedWritePaths"/> / <see cref="RefusedCommands"/> for <c>PermissionWallTracker</c>: a
/// refused edit/write/delete contributes its PATH; a refused shell call its COMMAND; any other refused tool
/// its tool NAME as a command (as Claude's scanner attributes a non-write tool), so a refused READ of a
/// <c>.claude/</c> file is never mistaken for the structural write wall.</item>
/// <item><see cref="ConsecutiveDenials"/>, reset by every call that ran — the #452 fail-fast counter.</item>
/// <item>Shell accounting (<see cref="ShellCallsRefused"/>, <see cref="ShellCallsRan"/>), which is what the
/// runner's "every shell call was refused" verdict is decided from.</item>
/// </list>
/// </summary>
internal sealed class CursorToolCallScanner : IToolDenialScanner
{
    /// <summary>The reason recorded for a refusal whose <c>reason</c> was empty — Cursor's own policy said no.</summary>
    internal const string NoReasonGiven = "refused by Cursor approval policy";

    private const string ToolCallSuffix = "ToolCall";

    /// <summary>The tool kinds (suffix dropped) whose target is a PATH being written.</summary>
    private static readonly HashSet<string> WriteKinds =
        new(StringComparer.OrdinalIgnoreCase) { "edit", "write", "delete", "multiEdit", "notebookEdit" };

    private readonly List<ToolRefusal> _refusals = new();
    private readonly List<string> _blocked = new();
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly List<string> _commands = new();

    /// <summary>Every refused call, in the order it happened, with its reason.</summary>
    public IReadOnlyList<ToolRefusal> Refusals => _refusals;

    /// <inheritdoc />
    public IReadOnlyList<string> BlockedWritePaths => _blocked;

    /// <inheritdoc />
    public IReadOnlyList<string> RefusedCommands => _commands;

    /// <inheritdoc />
    public int ConsecutiveDenials { get; private set; }

    /// <summary>Shell calls that completed with a <c>rejected</c> result.</summary>
    public int ShellCallsRefused { get; private set; }

    /// <summary>Shell calls that completed with any other result (they RAN — success or not).</summary>
    public int ShellCallsRan { get; private set; }

    /// <summary>
    /// True when the agent attempted shell and not ONE shell call ran: the session could do none of the
    /// building, testing or git work shell exists for, whatever its terminal result says.
    /// </summary>
    public bool EveryShellCallRefused => ShellCallsRefused > 0 && ShellCallsRan == 0;

    /// <inheritdoc />
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
            return; // tolerant, like the parser and the renderer
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                StringProp(root, "type") != "tool_call" ||
                StringProp(root, "subtype") != "completed" ||
                !root.TryGetProperty("tool_call", out JsonElement call) ||
                call.ValueKind != JsonValueKind.Object ||
                FindTool(call) is not { } tool)
            {
                return;
            }

            Observe(tool.Kind, tool.Body);
        }
    }

    private void Observe(string kind, JsonElement body)
    {
        bool isShell = string.Equals(kind, "shell", StringComparison.OrdinalIgnoreCase);
        if (!body.TryGetProperty("result", out JsonElement result) ||
            result.ValueKind != JsonValueKind.Object ||
            !result.TryGetProperty("rejected", out JsonElement rejected))
        {
            // Completed and NOT refused: the call ran. That ends a refusal streak (#452 bounds CONSECUTIVE
            // refusals, so an agent that hits one wall and then reaches for something that works keeps its
            // budget) and, for shell, is the evidence the session could run commands at all.
            ConsecutiveDenials = 0;
            if (isShell)
            {
                ShellCallsRan++;
            }

            return;
        }

        ConsecutiveDenials++;
        if (isShell)
        {
            ShellCallsRefused++;
        }

        JsonElement args = body.TryGetProperty("args", out JsonElement a) && a.ValueKind == JsonValueKind.Object
            ? a
            : default;
        JsonElement refusal = rejected.ValueKind == JsonValueKind.Object ? rejected : default;

        string? command = StringProp(args, "command") ?? StringProp(refusal, "command");
        string? path = StringProp(args, "path") ?? StringProp(refusal, "path")
            ?? StringProp(args, "filePath") ?? StringProp(refusal, "filePath");
        string? reason = StringProp(refusal, "reason");

        (string display, string wallTarget, bool isCommand) = isShell
            ? (command ?? kind, command ?? kind, true)
            : WriteKinds.Contains(kind) && path is not null
                ? (path, path, false)
                : (path ?? command ?? kind, kind, true);

        _refusals.Add(new ToolRefusal(
            kind,
            display.Trim(),
            string.IsNullOrWhiteSpace(reason) ? NoReasonGiven : reason.Trim()));

        string target = isCommand ? wallTarget.Trim() : wallTarget.Trim().Trim('"', '\'', '`');
        if (target.Length > 0 && _seen.Add(target))
        {
            _blocked.Add(target);
            if (isCommand)
            {
                _commands.Add(target);
            }
        }
    }

    /// <summary>
    /// The tool inside a <c>tool_call</c> object: the first key ending in <c>ToolCall</c> that holds an object,
    /// with the suffix dropped (<c>shellToolCall</c> ⇒ <c>shell</c>).
    /// </summary>
    private static (string Kind, JsonElement Body)? FindTool(JsonElement call)
    {
        foreach (JsonProperty property in call.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Object &&
                property.Name.EndsWith(ToolCallSuffix, StringComparison.Ordinal) &&
                property.Name.Length > ToolCallSuffix.Length)
            {
                return (property.Name[..^ToolCallSuffix.Length], property.Value);
            }
        }

        return null;
    }

    private static string? StringProp(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object &&
        obj.TryGetProperty(name, out JsonElement value) &&
        value.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()
            : null;
}
