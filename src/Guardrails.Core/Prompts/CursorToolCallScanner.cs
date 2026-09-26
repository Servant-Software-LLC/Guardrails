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
/// <para><b>What it reads.</b> <c>tool_call</c> events; the tool is the first key of <c>tool_call</c> that ends
/// in <c>ToolCall</c> and holds an object (the object also carries sibling keys such as <c>toolCallId</c>). A
/// <c>completed</c> call whose <c>result</c> has a <c>rejected</c> member is a REFUSAL; any other completed call
/// RAN. For a refused shell call <c>args</c> may be absent — the command is then <c>result.rejected.command</c>
/// (or the paired <c>started</c> event's <c>args.command</c>). An edit/write call names its target in
/// <c>args.path</c>.</para>
///
/// <para><b>Abandoned calls (#778).</b> <c>started</c> and <c>completed</c> are paired by
/// <see cref="CursorCallPairing"/> (the top-level <c>call_id</c>, else the <c>toolCallId</c>). Measured under
/// <c>--auto-review</c>: a <c>git commit</c> the classifier sent to human approval emitted <c>started</c> and
/// NEVER <c>completed</c> — no <c>rejected</c> anywhere — and the session still ended <c>result/success</c>, exit 0,
/// with the commit not made. So a call still open when the terminal <c>result</c> event arrives is a REFUSAL with
/// reason <see cref="AbandonedReason"/>, taken from the <c>started</c> event's <c>args</c>: it is in
/// <see cref="Refusals"/> and its target in the wall lists, exactly like a <c>rejected</c> completion. Two
/// differences, both deliberate:</para>
/// <list type="bullet">
/// <item>An abandoned SHELL call counts in <see cref="ShellCallsAbandoned"/>, not <see cref="ShellCallsRefused"/>.
/// The every-shell-refused verdict for an ACTION reads only explicit refusals (<see cref="EveryShellCallRefused"/>):
/// under auto-review Cursor habitually tries a <c>git commit</c> — which Guardrails never needs, the harness
/// commits — and it is abandoned, so a lone abandoned commit must not turn an ordinary guardrail failure into a
/// no-retry needs-human. A JUDGE counts abandoned calls too (<see cref="EveryShellCallRefusedOrAbandoned"/>): a
/// verifier that ran none of its checks fails closed however they were stopped.</item>
/// <item>It does NOT feed <see cref="ConsecutiveDenials"/>: that counter bounds a LIVE streak, and after a terminal
/// result there is nothing left to abort (tripping it there would turn a finished session into an abort).</item>
/// </list>
///
/// <para><b>A session with no terminal result</b> (killed by the harness on a timeout, a stall or a cancel, or
/// crashed) is already a failed attempt with its own failure kind, which <see cref="CursorPromptRunner.Finish"/>
/// never re-classifies. <see cref="EndOfStream"/> puts its still-open calls in <see cref="InFlightCalls"/> — NOT in
/// <see cref="Refusals"/>: nothing refused them, and the retry feedback for a refusal ("the same call will be refused
/// again") would be wrong advice about, say, a <c>dotnet test</c> the timeout cut short. They touch nothing else: not
/// the wall lists, not the shell accounting, not the counter.</para>
///
/// <para><b>What it produces.</b></para>
/// <list type="bullet">
/// <item>Every refusal, in order, with its reason (<see cref="Refusals"/>); an empty reason reads
/// <see cref="NoReasonGiven"/>.</item>
/// <item><see cref="BlockedWritePaths"/> / <see cref="RefusedCommands"/> for <c>PermissionWallTracker</c>: a
/// refused edit/write/delete contributes its PATH; a refused shell call its COMMAND; any other refused tool
/// its tool NAME as a command (as Claude's scanner attributes a non-write tool), so a refused READ of a
/// <c>.claude/</c> file is never mistaken for the structural write wall.</item>
/// <item><see cref="ConsecutiveDenials"/>, reset by every call that ran — the #452 fail-fast counter. It trips only
/// when a caller sets <see cref="PromptInvocation.AbortAfterConsecutiveToolDenials"/>; no task-action caller does
/// today, so for cursor actions it is available but not active.</item>
/// <item>Shell accounting (<see cref="ShellCallsRefused"/>, <see cref="ShellCallsAbandoned"/>,
/// <see cref="ShellCallsRan"/>), which is what the runner's "every shell call was refused" verdict is decided
/// from.</item>
/// <item>Calls cut off by a session that ended with no result (<see cref="InFlightCalls"/>).</item>
/// </list>
/// <para><b>Known gaps.</b> A <c>taskToolCall</c> (Cursor's subagent) carries its own nested conversation steps;
/// refusals INSIDE them are not read — only the top-level stream's events are. A shell <c>success</c> whose
/// result carries <c>isBackground</c> counts as RAN, although it may only mean "launched in the background" (a
/// sandboxed <c>dotnet restore</c> was measured completing that way without producing its output); ordinary
/// successful <c>--auto-review</c> shell calls carry the flag too, so it is not read as a refusal. And a
/// <c>completed</c> event that arrives AFTER the terminal result for a call already reported abandoned is ignored:
/// the call stays abandoned (the verdict is decided at the result, not revised afterwards).</para>
/// </summary>
internal sealed class CursorToolCallScanner : IToolDenialScanner
{
    /// <summary>The reason recorded for a refusal whose <c>reason</c> was empty — Cursor's own policy said no.</summary>
    internal const string NoReasonGiven = "refused by Cursor approval policy";

    /// <summary>
    /// The reason recorded for a call that <c>started</c> and was still open when the terminal <c>result</c> arrived
    /// (#778) — held for an approval that print mode has nobody to give.
    /// </summary>
    internal const string AbandonedReason =
        "abandoned: started but never completed (awaiting an approval Cursor print mode cannot give)";

    private const string ToolCallSuffix = "ToolCall";

    /// <summary>The tool kinds (suffix dropped) whose target is a PATH being written.</summary>
    private static readonly HashSet<string> WriteKinds =
        new(StringComparer.OrdinalIgnoreCase) { "edit", "write", "delete", "multiEdit", "notebookEdit" };

    private readonly List<ToolRefusal> _refusals = new();
    private readonly List<InFlightToolCall> _inFlight = new();
    private readonly List<string> _blocked = new();
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly List<string> _commands = new();
    private readonly CursorCallPairing _pairing = new();
    private bool _terminalResultSeen;
    private bool _ended;

    /// <summary>Every refused call — rejected or abandoned — in the order it was known to be refused, with its reason.</summary>
    public IReadOnlyList<ToolRefusal> Refusals => _refusals;

    /// <summary>
    /// Calls still running when a stream with NO terminal result ended (#778) — cut off, not refused. Filled by
    /// <see cref="EndOfStream"/>.
    /// </summary>
    public IReadOnlyList<InFlightToolCall> InFlightCalls => _inFlight;

    /// <inheritdoc />
    public IReadOnlyList<string> BlockedWritePaths => _blocked;

    /// <inheritdoc />
    public IReadOnlyList<string> RefusedCommands => _commands;

    /// <inheritdoc />
    public int ConsecutiveDenials { get; private set; }

    /// <summary>Shell calls that completed with a <c>rejected</c> result — an explicit refusal.</summary>
    public int ShellCallsRefused { get; private set; }

    /// <summary>Shell calls still open at the terminal result (#778) — refused by abandonment.</summary>
    public int ShellCallsAbandoned { get; private set; }

    /// <summary>Shell calls that completed with any other result (they RAN — success or not).</summary>
    public int ShellCallsRan { get; private set; }

    /// <summary>
    /// The ACTION rule: the agent had shell calls explicitly <c>rejected</c> and not ONE shell call ran. Abandoned
    /// calls do not count toward it (a lone abandoned <c>git commit</c> is not a policy that refuses all shell).
    /// </summary>
    public bool EveryShellCallRefused => ShellCallsRefused > 0 && ShellCallsRan == 0;

    /// <summary>The JUDGE rule: shell was attempted and not ONE call ran, whether rejected or abandoned.</summary>
    public bool EveryShellCallRefusedOrAbandoned => ShellCallsRefused + ShellCallsAbandoned > 0 && ShellCallsRan == 0;

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
            if (root.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            if (StringProp(root, "type") == "result")
            {
                // The terminal result: every call still open was abandoned (#778).
                _terminalResultSeen = true;
                AbandonOpenCalls();
                return;
            }

            if (_pairing.Observe(root) is { } completed)
            {
                Observe(completed.Kind, completed.Body, completed.StartedArgs);
            }
        }
    }

    /// <summary>
    /// The stream ended (idempotent). With a terminal result, a call still open (one that started after it) is
    /// abandoned like any other; without one, the still-open calls were cut off by a session that already failed on
    /// its own, and go to <see cref="InFlightCalls"/> only (#778).
    /// </summary>
    public void EndOfStream()
    {
        if (_ended)
        {
            return;
        }

        _ended = true;
        if (_terminalResultSeen)
        {
            AbandonOpenCalls();
            return;
        }

        foreach (CursorCallPairing.OpenCall call in _pairing.Drain())
        {
            _inFlight.Add(DescribeInFlight(call));
        }
    }

    private void AbandonOpenCalls()
    {
        foreach (CursorCallPairing.OpenCall call in _pairing.Drain())
        {
            if (IsShell(call.Kind))
            {
                ShellCallsAbandoned++;
            }

            Record(call.Kind, call.Args, rejected: default, AbandonedReason);
        }
    }

    private void Observe(string kind, JsonElement body, JsonElement startedArgs)
    {
        if (!body.TryGetProperty("result", out JsonElement result) ||
            result.ValueKind != JsonValueKind.Object ||
            !result.TryGetProperty("rejected", out JsonElement rejected))
        {
            // Completed and NOT refused: the call ran. That ends a refusal streak (#452 bounds CONSECUTIVE
            // refusals, so an agent that hits one wall and then reaches for something that works keeps its
            // budget) and, for shell, is the evidence the session could run commands at all.
            ConsecutiveDenials = 0;
            if (IsShell(kind))
            {
                ShellCallsRan++;
            }

            return;
        }

        ConsecutiveDenials++;
        if (IsShell(kind))
        {
            ShellCallsRefused++;
        }

        JsonElement args = body.TryGetProperty("args", out JsonElement a) && a.ValueKind == JsonValueKind.Object
            ? a
            : startedArgs;
        Record(kind, args, rejected.ValueKind == JsonValueKind.Object ? rejected : default, reason: null);
    }

    /// <summary>
    /// Record one refusal — a <c>rejected</c> completion or an abandoned call — in the list and the wall targets. A
    /// non-null <paramref name="reason"/> stands in for <c>rejected.reason</c>.
    /// </summary>
    private void Record(string kind, JsonElement args, JsonElement rejected, string? reason)
    {
        (ToolRefusal refusal, string wallTarget, bool isCommand) = Describe(kind, args, rejected, reason);
        _refusals.Add(refusal);

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
    /// How a refused (or abandoned) call reads, and the target it contributes to the wall lists: a shell call its
    /// COMMAND, an edit/write/delete its PATH, any other tool its NAME as a command. Shared with the transcript
    /// renderer, so both name an abandoned call the same way.
    /// </summary>
    internal static (ToolRefusal Refusal, string WallTarget, bool IsCommand) Describe(
        string kind, JsonElement args, JsonElement rejected, string? reason)
    {
        string? command = StringProp(args, "command") ?? StringProp(rejected, "command");
        string? path = StringProp(args, "path") ?? StringProp(rejected, "path")
            ?? StringProp(args, "filePath") ?? StringProp(rejected, "filePath");
        reason ??= StringProp(rejected, "reason");

        (string display, string wallTarget, bool isCommand) = IsShell(kind)
            ? (command ?? kind, command ?? kind, true)
            : WriteKinds.Contains(kind) && path is not null
                ? (path, path, false)
                : (path ?? command ?? kind, kind, true);

        var refusal = new ToolRefusal(
            kind,
            display.Trim(),
            string.IsNullOrWhiteSpace(reason) ? NoReasonGiven : reason.Trim());
        return (refusal, wallTarget, isCommand);
    }

    /// <summary>A call cut off by a session with no result, named as <see cref="Describe"/> names its target.</summary>
    internal static InFlightToolCall DescribeInFlight(CursorCallPairing.OpenCall call)
    {
        ToolRefusal named = Describe(call.Kind, call.Args, rejected: default, reason: null).Refusal;
        return new InFlightToolCall(named.Tool, named.Target);
    }

    private static bool IsShell(string kind) => string.Equals(kind, "shell", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The tool inside a <c>tool_call</c> object: the first key ending in <c>ToolCall</c> that holds an object,
    /// with the suffix dropped (<c>shellToolCall</c> ⇒ <c>shell</c>).
    /// </summary>
    internal static (string Kind, JsonElement Body)? FindTool(JsonElement call)
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

    internal static string? StringProp(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object &&
        obj.TryGetProperty(name, out JsonElement value) &&
        value.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()
            : null;
}

/// <summary>
/// Pairs Cursor's <c>tool_call</c> <c>started</c> and <c>completed</c> events (#778). One instance per stream, fed
/// every parsed event in order; shared by <see cref="CursorToolCallScanner"/> (the verdict) and the transcript
/// renderer (the <c>REFUSED</c> line), so the two cannot disagree about which calls were left open.
///
/// <para><b>Identity.</b> A call is known by EVERY id its event carries, each compared as an exact ordinal string
/// (measured <c>call_id</c>s contain a newline): the top-level <c>call_id</c>, then the <c>toolCallId</c> — which the
/// measured streams put beside the tool inside <c>tool_call</c> — and, defensively, a <c>toolCallId</c> inside the
/// tool object or its <c>args</c>. A <c>completed</c> event closes the open call that ANY of its ids names, so a
/// stream that drops <c>call_id</c> on one of the pair still pairs. A <c>started</c> event with no id at all cannot
/// be paired and is not tracked; a <c>completed</c> event for a call already drained as abandoned is ignored (it was
/// already reported).</para>
/// </summary>
internal sealed class CursorCallPairing
{
    private readonly List<string> _order = new();
    private readonly Dictionary<string, OpenCall> _open = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _alias = new(StringComparer.Ordinal);
    private readonly HashSet<string> _drained = new(StringComparer.Ordinal);

    /// <summary>A call that started and has not completed: its tool kind and the <c>started</c> event's <c>args</c>.</summary>
    internal sealed record OpenCall(string Kind, JsonElement Args);

    /// <summary>
    /// A <c>completed</c> tool call to judge: its kind, its body and — when its <c>started</c> twin was seen — that
    /// event's <c>args</c> (else <c>default</c>).
    /// </summary>
    internal sealed record Completion(string Kind, JsonElement Body, JsonElement StartedArgs);

    /// <summary>
    /// Consume one parsed stream object. Returns the completion to judge for a <c>completed</c> tool call, null for
    /// anything else (a <c>started</c> event, another event type, a late completion of an abandoned call).
    /// </summary>
    public Completion? Observe(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            CursorToolCallScanner.StringProp(root, "type") != "tool_call" ||
            !root.TryGetProperty("tool_call", out JsonElement call) ||
            call.ValueKind != JsonValueKind.Object ||
            CursorToolCallScanner.FindTool(call) is not { } tool)
        {
            return null;
        }

        JsonElement args = tool.Body.TryGetProperty("args", out JsonElement a) && a.ValueKind == JsonValueKind.Object
            ? a
            : default;
        List<string> ids = Ids(root, call, tool.Body, args);
        switch (CursorToolCallScanner.StringProp(root, "subtype"))
        {
            case "started":
                if (ids.Count > 0 && !ids.Any(_alias.ContainsKey))
                {
                    string key = ids[0];
                    _open[key] = new OpenCall(tool.Kind, args.ValueKind == JsonValueKind.Object ? args.Clone() : default);
                    _order.Add(key);
                    foreach (string id in ids)
                    {
                        _alias[id] = key;
                    }
                }

                return null;

            case "completed":
                string? primary = ids.Select(id => _alias.GetValueOrDefault(id)).FirstOrDefault(k => k is not null);
                if (primary is not null && _drained.Contains(primary))
                {
                    return null;
                }

                JsonElement startedArgs = default;
                if (primary is not null && _open.Remove(primary, out OpenCall? started))
                {
                    _order.Remove(primary);
                    startedArgs = started.Args;
                }

                return new Completion(tool.Kind, tool.Body, startedArgs);

            default:
                return null;
        }
    }

    /// <summary>Every call still open, in the order it started; each is then closed for good.</summary>
    public IReadOnlyList<OpenCall> Drain()
    {
        var calls = new List<OpenCall>(_order.Count);
        foreach (string key in _order)
        {
            calls.Add(_open[key]);
            _drained.Add(key);
        }

        _order.Clear();
        _open.Clear();
        return calls;
    }

    /// <summary>The event's ids, most specific first, distinct. Read raw: an id is compared exactly, whitespace included.</summary>
    private static List<string> Ids(JsonElement root, JsonElement call, JsonElement body, JsonElement args)
    {
        var ids = new List<string>(2);
        Add(root, "call_id");
        Add(call, "toolCallId");
        Add(body, "toolCallId");
        Add(args, "toolCallId");
        return ids;

        void Add(JsonElement obj, string name)
        {
            if (obj.ValueKind == JsonValueKind.Object &&
                obj.TryGetProperty(name, out JsonElement value) &&
                value.ValueKind == JsonValueKind.String &&
                value.GetString() is { Length: > 0 } id &&
                !ids.Contains(id, StringComparer.Ordinal))
            {
                ids.Add(id);
            }
        }
    }
}
