using System.Text.Json;

namespace Guardrails.Core.Execution;

/// <summary>
/// A fragment control key (<c>needsHarnessWrite</c> / <c>needsHuman</c>, SSOT §9) found nested ONE
/// LEVEL under a top-level fragment key instead of standing beside it — issue #586.
/// </summary>
/// <param name="ControlKey">The control key as it appeared, e.g. <c>needsHarnessWrite</c>.</param>
/// <param name="ContainingKey">The top-level key it was nested inside — in the measured case the task's own folder name.</param>
internal sealed record NestedControlKeySignal(string ControlKey, string ContainingKey);

/// <summary>
/// Detects a fragment control key nested one level under a top-level key (issue #586, SSOT §6.2/§9).
/// <para>
/// <b>The defect this closes.</b> The harness reads <c>needsHuman</c> and <c>needsHarnessWrite</c> at
/// the fragment ROOT only. Written one level down —
/// <c>{ "11-record-gr2060-in-knowledge-skill": { "needsHarnessWrite": { … } } }</c> — the key is not a
/// control key at all: it is ordinary state under a top-level key the task legitimately owns, so the
/// single-writer check (§6.2) passes it, the escape hatch never fires, NOTHING is written, and nothing
/// anywhere mentions the request. The task's guardrails then fail on the CONTENT of a file the agent was
/// never given the chance to touch — a silent failure in the direction that looks like the agent's fault.
/// Measured on plan 33: 7 attempts across two runs and one run-stopping <c>needs-human</c> halt; the same
/// task, model and content passed in 78 seconds once the prompt was corrected by hand.
/// </para>
/// <para>
/// <b>It is a defensible reading of the instructions.</b> The harness-contract header every prompt action
/// carries says to write everything published under the task's FOLDER NAME as the single top-level key,
/// and that the harness REJECTS a fragment keyed by anything else — while the escape hatch is documented
/// at the root. Nothing marked the control keys exempt. The wording is fixed at the source
/// (<c>plan-breakdown</c>), but a wording fix reaches only plans authored afterwards; this check reaches
/// every plan folder already on disk.
/// </para>
/// <para>
/// <b>Exactly ONE level, and only an object-valued container.</b> A control key two or more levels down
/// is NOT flagged — that depth is genuinely reachable by legitimate state (a task recording what it asked
/// the harness to write), and the measured mistake is the one-level one.
/// </para>
/// <para>
/// <b>Matched on PAYLOAD SHAPE, never on the key name alone</b> — see
/// <see cref="LooksLikeHarnessWriteRequest"/> / <see cref="LooksLikeNeedsHumanRequest"/>. A task's own
/// published state could name a key <c>needsHuman</c> for unrelated reasons, and a false rejection of
/// legitimate state is worse than the bug it would be closing: the bug costs a wasted attempt, a false
/// rejection costs a task that can never converge, on every attempt, for as long as it publishes that
/// key. So the nested value must carry the control key's OWN required members before it is treated as a
/// misplaced request.
/// </para>
/// <para>
/// <b>Not <c>stagingOutputs</c>.</b> It is a <c>task.json</c> field (SSOT §3.5), not a fragment key —
/// there is no top-level fragment contract for it to be nested out of, so it carries no trap of this
/// shape and is deliberately absent from <see cref="ControlKeys"/>.
/// </para>
/// </summary>
internal static class NestedControlKey
{
    /// <summary>
    /// The fragment ROOT keys the harness reads as instructions rather than state (SSOT §9) — the
    /// family this check polices. Order is the scan order within one container, so it is the tie-break
    /// when a container nests both; <c>needsHarnessWrite</c> leads because it is the silent one (a
    /// missed <c>needsHuman</c> at least degrades to an ordinary attempt failure).
    /// </summary>
    private static readonly string[] ControlKeys = ["needsHarnessWrite", "needsHuman"];

    /// <summary>
    /// Read the (already-written) action fragment and report the first control key nested one level
    /// under a top-level key, or null when there is none — including for a missing file, unparseable
    /// JSON, or a non-object root (all of which the normal fragment-validation path reports).
    /// </summary>
    public static NestedControlKeySignal? DetectIn(string fragmentOutPath)
    {
        if (!File.Exists(fragmentOutPath))
        {
            return null;
        }

        try
        {
            return DetectInJson(File.ReadAllText(fragmentOutPath));
        }
        catch (JsonException)
        {
            // Unparseable JSON is not a nested-control-key signal; the fragment-merge validation path
            // (StateManager.MergeFragment / AttemptJournaler.ValidateFragmentForSettle) reports it.
            return null;
        }
        catch (IOException)
        {
            // A best-effort diagnostic read must never abort the attempt.
            return null;
        }
    }

    /// <summary>
    /// The pure half of <see cref="DetectIn"/>, over the fragment TEXT — the unit-test seam. Throws
    /// <see cref="JsonException"/> on unparseable input, which the caller above treats as "no signal".
    /// </summary>
    internal static NestedControlKeySignal? DetectInJson(string fragmentJson)
    {
        using JsonDocument document = JsonDocument.Parse(
            fragmentJson,
            new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });

        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (JsonProperty top in document.RootElement.EnumerateObject())
        {
            // Only an OBJECT-valued top-level key can nest anything. A top-level key that IS a control
            // key is the correct shape and is skipped by construction: its value is the request itself,
            // and a request never carries a control key of its own.
            if (top.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            foreach (string controlKey in ControlKeys)
            {
                if (top.Value.TryGetProperty(controlKey, out JsonElement nested) && IsRequestShaped(controlKey, nested))
                {
                    return new NestedControlKeySignal(controlKey, top.Name);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Does <paramref name="value"/> carry the control key's OWN required members — i.e. is it a
    /// misplaced REQUEST rather than a state value that happens to share the name? This predicate is the
    /// whole false-positive defence; see the type remarks for why it is deliberately narrow.
    /// </summary>
    private static bool IsRequestShaped(string controlKey, JsonElement value) => controlKey switch
    {
        "needsHarnessWrite" => LooksLikeHarnessWriteRequest(value),
        "needsHuman" => LooksLikeNeedsHumanRequest(value),
        _ => false
    };

    /// <summary>
    /// A <c>needsHarnessWrite</c> request (SSOT §9): an entry object carrying a non-empty string
    /// <c>path</c> AND at least one of the two mutually exclusive payloads (<c>content</c> /
    /// <c>edits</c>), or an ARRAY containing at least one such entry (the #445 batch form).
    /// <para>
    /// Requiring <c>path</c> + a payload — rather than the key name alone — means a state value named
    /// <c>needsHarnessWrite</c> is flagged only when it is, member for member, the request it would have
    /// been at the root. A nested request that is ALSO malformed (no <c>path</c>, neither payload) is
    /// therefore missed; that is the deliberate side of the trade, and it degrades to what happens
    /// today (nothing written) rather than to a false rejection of legitimate state.
    /// </para>
    /// </summary>
    private static bool LooksLikeHarnessWriteRequest(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement entry in value.EnumerateArray())
            {
                if (LooksLikeHarnessWriteRequest(entry))
                {
                    return true;
                }
            }

            return false;
        }

        return value.ValueKind == JsonValueKind.Object
               && value.TryGetProperty("path", out JsonElement path)
               && path.ValueKind == JsonValueKind.String
               && path.GetString() is { Length: > 0 }
               && (value.TryGetProperty("content", out _) || value.TryGetProperty("edits", out _));
    }

    /// <summary>
    /// A <c>needsHuman</c> request (SSOT §9): the STRUCTURED form — an object carrying a non-empty
    /// string <c>question</c>.
    /// <para>
    /// The free-text form (<c>{"needsHuman": "&lt;question&gt;"}</c>) is deliberately NOT matched. A bare
    /// string carries no structure to distinguish an escalation from an ordinary state value, so
    /// matching it would rest on the key name alone — and a task publishing a string under its own
    /// <c>needsHuman</c> key would then be unable to complete, ever. The structured form is the one the
    /// harness-contract header actually instructs (<c>{"question": …, "kind": …}</c>), so almost nothing
    /// real is given up, and what is given up degrades to the pre-#586 behaviour rather than to a
    /// permanently blocked task.
    /// </para>
    /// </summary>
    private static bool LooksLikeNeedsHumanRequest(JsonElement value) =>
        value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty("question", out JsonElement question)
        && question.ValueKind == JsonValueKind.String
        && question.GetString() is { Length: > 0 };

    /// <summary>
    /// The correct shape to show a retrying agent for <paramref name="controlKey"/> — the control key
    /// and the task's folder-name key SIDE BY SIDE at the root, since the ambiguity #586 measured was
    /// never about the control key in isolation but about whether it belongs inside the folder-name key.
    /// </summary>
    public static string CorrectShapeFor(string controlKey, string taskId)
    {
        string payload = controlKey switch
        {
            "needsHarnessWrite" => "{ \"path\": \"<workspace-relative path>\", \"edits\": [ { \"old\": \"…\", \"new\": \"…\" } ] }",
            "needsHuman" => "{ \"question\": \"<question>\", \"kind\": \"blocked-work\" }",
            _ => "{ … }"
        };

        return $"{{ \"{taskId}\": {{ \"someKey\": \"someValue\" }},{Environment.NewLine}  \"{controlKey}\": {payload} }}";
    }
}
