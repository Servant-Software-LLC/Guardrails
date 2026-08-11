using System.Text;
using System.Text.Json;
using Guardrails.Core.State;

namespace Guardrails.Core.Execution;

/// <summary>
/// Parses and performs the <c>needsHarnessWrite</c> escape hatch (issue #191, SSOT §9): a task
/// action running as a Claude Code subprocess can NEVER write under <c>.claude/</c> (the runtime's
/// tool-permission layer refuses it unconditionally in a fresh, never-interactively-approved segment
/// worktree — broader than the new-subdirectory-only gap #101 fixed, and unaffected by
/// <c>dangerouslyDisableSandbox</c>). <c>needsHarnessWrite</c> lets the action ask the .NET HARNESS
/// PROCESS ITSELF — never subject to Claude Code's tool-permission layer — to perform the write on its
/// behalf.
/// <para>
/// TWO MUTUALLY EXCLUSIVE FORMS (issue #437). A root fragment key, parsed the SAME way as
/// <c>needsHuman</c> — see <see cref="ActionRunner"/>:
/// <code>
/// // full-content form — for CREATING a file (or replacing a small one):
/// { "needsHarnessWrite": { "path": ".claude/skills/foo/SKILL.md", "content": "...", "reason": "..." } }
///
/// // anchored-edit form — for MODIFYING an existing file:
/// { "needsHarnessWrite": { "path": ".claude/skills/foo/SKILL.md", "reason": "...",
///     "edits": [ { "old": "&lt;verbatim anchor&gt;", "new": "&lt;replacement&gt;" } ] } }
/// </code>
/// The anchored form exists because full-content mode is UNUSABLE for a large file: re-emitting a
/// 204 KB skill file costs ~60k JSON-escaped tokens, at or over a runner's <c>maxOutputTokens</c> cap,
/// so a task whose deliverable is a big <c>.claude/</c> file was structurally impossible to complete
/// autonomously no matter how small the actual change was (issue #437). With <c>edits</c> the cost
/// scales with the CHANGE, not the FILE — and the untouched bytes are untouched BY CONSTRUCTION rather
/// than by trusting a model to retype thousands of lines of normative text byte-for-byte.
/// </para>
/// <para>
/// Singular only in v1 (one harness-write REQUEST per attempt, though that request may carry many
/// edits to the ONE file it names) — an action needing multiple <c>.claude/</c> files touched does so
/// across multiple attempts/retries. This is a documented v1 limitation.
/// </para>
/// </summary>
public static class HarnessWrite
{
    /// <summary>
    /// The size (in bytes, of the EXISTING target file) beyond which full-content <c>content</c> mode is
    /// refused for a MODIFICATION and the request is routed to the anchored <c>edits</c> form instead
    /// (issue #437, SSOT §9). 64 KiB of prose is already ~19k tokens before JSON escaping, so a
    /// full-content re-emission of a file this size consumes a substantial fraction of a runner's output
    /// budget — and, more importantly, nothing can verify that the thousands of lines the model was NOT
    /// asked to change came back byte-identical. Refusing here converts a would-be silent truncation or
    /// corruption into an actionable, retryable failure pointing at <c>edits</c>.
    /// <para>
    /// This gate applies ONLY when the target file ALREADY EXISTS. Creating a new file of any size with
    /// <c>content</c> stays allowed — creation is exactly what full-content mode is for, and there are no
    /// pre-existing bytes to corrupt.
    /// </para>
    /// </summary>
    public const int FullContentMaxBytes = 65_536;

    /// <summary>How much of an anchor to echo back in a failure message before truncating.</summary>
    private const int AnchorPreviewChars = 160;

    /// <summary>
    /// Read the (already-written) action fragment and, if its root is an object with a
    /// <c>needsHarnessWrite</c> key whose value is an OBJECT, return the parsed request. Anything else —
    /// key absent, value not an object (e.g. a <c>needsHuman</c>-style bare string), unparseable JSON —
    /// returns null (never throws); the normal fragment-validation path then reports the real problem.
    /// <para>
    /// A key that IS an object but whose payload is unusable (no <c>path</c>; neither <c>content</c> nor
    /// <c>edits</c>; BOTH of them; a malformed <c>edits</c> entry) is NOT silently dropped — it comes back
    /// as a request carrying <see cref="HarnessWriteRequest.InvalidReason"/>, so
    /// <see cref="ValidateAndApply"/> fails the attempt with an actionable message naming the mistake
    /// instead of letting the agent puzzle over a generic foreign-key merge error (issue #437).
    /// </para>
    /// </summary>
    public static HarnessWriteRequest? RequestFrom(string fragmentOutPath)
    {
        if (!File.Exists(fragmentOutPath))
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(
                File.ReadAllText(fragmentOutPath),
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });

            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("needsHarnessWrite", out JsonElement request)
                || request.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            return ParsePayload(request);
        }
        catch (JsonException)
        {
            // Not parseable JSON → not a needsHarnessWrite signal; the normal fragment validation
            // path (MergeFragment / ValidateFragmentForSettle) reports the malformed JSON as usual.
            return null;
        }
    }

    /// <summary>
    /// Turn a <c>needsHarnessWrite</c> object into a request. Never throws and never returns null: an
    /// unusable payload becomes a request carrying <see cref="HarnessWriteRequest.InvalidReason"/>.
    /// </summary>
    private static HarnessWriteRequest ParsePayload(JsonElement request)
    {
        string? reason = request.TryGetProperty("reason", out JsonElement reasonEl) && reasonEl.ValueKind == JsonValueKind.String
            ? reasonEl.GetString()
            : null;

        string path = request.TryGetProperty("path", out JsonElement pathEl) && pathEl.ValueKind == JsonValueKind.String
            ? pathEl.GetString() ?? ""
            : "";

        HarnessWriteRequest Invalid(string why) => new() { Path = path, Reason = reason, InvalidReason = why };

        if (path.Length == 0)
        {
            return Invalid("needsHarnessWrite.path is missing or is not a string");
        }

        bool hasContent = request.TryGetProperty("content", out JsonElement contentEl);
        bool hasEdits = request.TryGetProperty("edits", out JsonElement editsEl);

        if (hasContent && hasEdits)
        {
            return Invalid(
                "needsHarnessWrite carries BOTH `content` and `edits` — they are mutually exclusive. Use " +
                "`edits` to MODIFY an existing file, or `content` to CREATE one");
        }

        if (!hasContent && !hasEdits)
        {
            return Invalid(
                "needsHarnessWrite carries neither `content` nor `edits` — exactly one is required. Use " +
                "`edits` to MODIFY an existing file, or `content` to CREATE one");
        }

        if (hasContent)
        {
            return contentEl.ValueKind == JsonValueKind.String
                ? new HarnessWriteRequest { Path = path, Content = contentEl.GetString() ?? "", Reason = reason }
                : Invalid("needsHarnessWrite.content must be a string (the complete file text)");
        }

        if (editsEl.ValueKind != JsonValueKind.Array)
        {
            return Invalid("needsHarnessWrite.edits must be an array of {\"old\": \"...\", \"new\": \"...\"} objects");
        }

        var edits = new List<HarnessWriteEdit>();
        int index = 0;
        foreach (JsonElement edit in editsEl.EnumerateArray())
        {
            if (edit.ValueKind != JsonValueKind.Object)
            {
                return Invalid($"needsHarnessWrite.edits[{index}] is not an object; each edit is {{\"old\": \"...\", \"new\": \"...\"}}");
            }

            if (!edit.TryGetProperty("old", out JsonElement oldEl) || oldEl.ValueKind != JsonValueKind.String)
            {
                return Invalid($"needsHarnessWrite.edits[{index}].old is missing or is not a string");
            }

            if (!edit.TryGetProperty("new", out JsonElement newEl) || newEl.ValueKind != JsonValueKind.String)
            {
                return Invalid($"needsHarnessWrite.edits[{index}].new is missing or is not a string (use \"\" to delete the anchored text)");
            }

            string oldText = oldEl.GetString() ?? "";
            if (oldText.Length == 0)
            {
                return Invalid($"needsHarnessWrite.edits[{index}].old is empty — an empty anchor cannot identify a unique location");
            }

            edits.Add(new HarnessWriteEdit { Old = oldText, New = newEl.GetString() ?? "" });
            index++;
        }

        return edits.Count == 0
            ? Invalid("needsHarnessWrite.edits is an empty array — provide at least one {\"old\", \"new\"} edit")
            : new HarnessWriteRequest { Path = path, Edits = edits, Reason = reason };
    }

    /// <summary>
    /// Remove the <c>needsHarnessWrite</c> top-level key from the fragment at
    /// <paramref name="fragmentOutPath"/> so the harness-consumed control key never reaches the
    /// single-writer-per-key fragment-merge check (SSOT §6.2) as a foreign/reserved key — mirroring how
    /// <c>needsHuman</c> is fully consumed before any merge runs. Any OTHER keys the action wrote to the
    /// same fragment (its own state contribution) are preserved untouched, so a task can request a
    /// harness write AND still contribute state in the same attempt. A no-op when the key is absent or
    /// the file cannot be parsed (the normal validation path then reports the real problem).
    /// </summary>
    public static void StripFromFragment(string fragmentOutPath)
    {
        if (!File.Exists(fragmentOutPath))
        {
            return;
        }

        string raw;
        try { raw = File.ReadAllText(fragmentOutPath); }
        catch (IOException) { return; }

        System.Text.Json.Nodes.JsonNode? node;
        try { node = System.Text.Json.Nodes.JsonNode.Parse(raw); }
        catch (JsonException) { return; }

        if (node is not System.Text.Json.Nodes.JsonObject obj || !obj.ContainsKey("needsHarnessWrite"))
        {
            return;
        }

        obj.Remove("needsHarnessWrite");
        AtomicFile.WriteAllText(fragmentOutPath, obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>
    /// Validate <paramref name="request"/>'s path against <paramref name="workspaceRoot"/> (the
    /// effective workspace — the segment worktree in worktree mode, the plan workspace in serial mode)
    /// and, when in scope, perform the write via the harness process itself
    /// (<see cref="AtomicFile.WriteAllText"/> — a plain atomic-enough write; this is not a
    /// resume-critical file, but atomicity is free and consistent with every other harness write).
    /// </summary>
    /// <remarks>
    /// Three INDEPENDENT safety checks, all load-bearing (a security boundary, SSOT §9) and all run
    /// BEFORE the target file is so much as read:
    /// <list type="bullet">
    /// <item><b>Workspace-escape check (always runs, regardless of <c>writeScope</c>).</b> Reuses
    /// <see cref="WorkspaceContainment.Escapes"/> — the same "does this path escape the boundary"
    /// predicate the worktree-containment hook's design note points at — so a request like
    /// <c>../../etc/passwd</c> or an absolute path is rejected before it is even a writeScope
    /// question. This protects a task with NO declared writeScope too (the segment-worktree
    /// containment is the boundary in that case).</item>
    /// <item><b>Permission-file carve-out (always runs, issue #321).</b> A request whose path resolves
    /// to <c>.claude/settings.json</c> or <c>.claude/settings.local.json</c> is DENIED — the harness
    /// never writes permission-granting files on an agent's behalf (a prompt must not widen its own
    /// permission surface). Checked before writeScope so declaring <c>.claude/**</c> in scope cannot
    /// bypass it. NOT a broad <c>.claude/</c> denylist — every other <c>.claude/</c> deliverable stays
    /// writable. Applies to BOTH forms: <c>edits</c> cannot be used to sneak into a settings file.</item>
    /// <item><b>writeScope-membership check (only when the task DECLARES a writeScope).</b> Reuses
    /// <see cref="WriteScope.IsInScope"/> — the SAME scope-matching predicate the POST-HOC
    /// write-scope CHECK (SSOT §3.4) uses, so the two enforcement points can never drift. A task
    /// with NO writeScope declared allows the write unconditionally (mirroring "absent ⇒ no check"
    /// for the existing retrospective check, SSOT §3.4) — the segment-worktree containment + the
    /// worktree-containment hook (#199/#192) are the backstops in that case.</item>
    /// </list>
    /// Only once all three pass does the MODE-SPECIFIC application run (issue #437): <c>edits</c> resolves
    /// every anchor in memory and writes ONCE if and only if every one of them resolved
    /// (<see cref="ApplyEdits"/>); <c>content</c> writes the supplied text, unless the target already
    /// exists and exceeds <see cref="FullContentMaxBytes"/>, in which case the request is refused and
    /// routed to <c>edits</c>.
    /// </remarks>
    public static HarnessWriteOutcome ValidateAndApply(
        HarnessWriteRequest request, string workspaceRoot, IReadOnlyList<string>? writeScope)
    {
        // A payload the parser could read but not USE (no path, both/neither mode, a malformed edit).
        // Reported as NotApplied so the retry feedback can restate the accepted schema (issue #437).
        if (request.InvalidReason is { } invalidReason)
        {
            return HarnessWriteOutcome.NotApplied(invalidReason);
        }

        string normalizedPath = request.Path.Replace('\\', '/');

        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            return HarnessWriteOutcome.Rejected("needsHarnessWrite.path is empty");
        }

        // Workspace-escape check: ALWAYS runs, independent of writeScope (SSOT §9 / issue #191). An
        // absolute path or a "../" climb-out must never reach the write, even for a task with no
        // declared writeScope at all.
        if (WorkspaceContainment.Escapes(workspaceRoot, normalizedPath))
        {
            return HarnessWriteOutcome.Rejected(
                $"path '{request.Path}' escapes the task's effective workspace — absolute paths and " +
                "'..' climb-outs are never allowed, regardless of writeScope");
        }

        // Permission-file carve-out (issue #321): the harness will NEVER write a Claude Code
        // permission-granting settings file on an agent's behalf. `.claude/settings.json` and
        // `.claude/settings.local.json` grant tool permissions, so honoring a needsHarnessWrite for one
        // would let a prompt widen its OWN permission surface — the exact escalation the escape hatch
        // must not enable. A human must author these. Checked BEFORE the writeScope membership check so
        // declaring `.claude/**` in scope can never bypass it, and BEFORE the mode split so the anchored
        // `edits` form of #437 is denied on exactly the same paths as `content`. NOT a broad `.claude/`
        // denylist: every other `.claude/` deliverable (commands, skills, hooks, agents) is a reviewed
        // artifact and stays writable — the safety boundary there is plan-review + opt-in merge-back,
        // not a filename block.
        if (IsClaudeSettingsFile(normalizedPath))
        {
            return HarnessWriteOutcome.Denied(
                "the harness will not write permission-granting files on an agent's behalf; a human must " +
                $"author `.claude/settings.json` (or `.claude/settings.local.json`) — requested '{request.Path}'");
        }

        // writeScope-membership check: only when the task declares one. Absent ⇒ allowed (mirrors the
        // retrospective write-scope check's "Absent ⇒ no check", SSOT §3.4 — the segment-worktree
        // containment + the worktree-containment hook are the backstops in that case).
        if (writeScope is { Count: > 0 } scope && !WriteScope.IsInScope(normalizedPath, scope))
        {
            return HarnessWriteOutcome.Rejected(
                $"path '{request.Path}' is outside this task's declared writeScope");
        }

        string fullPath = Path.GetFullPath(Path.Combine(workspaceRoot, normalizedPath));

        return request.IsEditForm
            ? ApplyEditForm(request, normalizedPath, fullPath)
            : ApplyContentForm(request, normalizedPath, fullPath);
    }

    /// <summary>
    /// Full-content mode: write <c>content</c> verbatim. The ONLY new restriction (issue #437) is the
    /// size wall — an EXISTING target larger than <see cref="FullContentMaxBytes"/> is refused and routed
    /// to the anchored <c>edits</c> form, because a full re-emission at that size is both liable to blow
    /// the runner's output budget and unverifiable (nothing proves the untouched lines came back
    /// byte-identical). Creating a NEW file is unrestricted.
    /// </summary>
    private static HarnessWriteOutcome ApplyContentForm(
        HarnessWriteRequest request, string normalizedPath, string fullPath)
    {
        long existingBytes;
        try
        {
            var existing = new FileInfo(fullPath);
            existingBytes = existing.Exists ? existing.Length : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            existingBytes = 0;
        }

        if (existingBytes > FullContentMaxBytes)
        {
            return HarnessWriteOutcome.NotApplied(
                $"'{request.Path}' already exists and is {existingBytes:N0} bytes — too large for full-content " +
                $"`content` mode (the limit is {FullContentMaxBytes:N0} bytes). Re-emitting a file this size " +
                "risks exhausting the runner's output budget mid-write, and nothing can verify that the lines " +
                "you did not mean to change came back byte-identical. Use the anchored `edits` form instead — " +
                "its cost scales with the CHANGE, not the file, and the untouched bytes stay untouched by " +
                "construction. `content` is for CREATING a file; `edits` is for MODIFYING one");
        }

        try
        {
            AtomicFile.WriteAllText(fullPath, request.Content ?? "");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return HarnessWriteOutcome.Failed($"the harness could not write '{request.Path}': {ex.Message}");
        }

        return HarnessWriteOutcome.Ok(normalizedPath);
    }

    /// <summary>
    /// Anchored-edit mode (issue #437): resolve EVERY edit against an in-memory copy first, and write
    /// exactly once only if all of them resolved. ATOMIC BY CONSTRUCTION — the file on disk is not opened
    /// for writing until the last anchor has been located, so a set with one bad anchor leaves the target
    /// byte-identical (a half-applied set is worse than a rejected one).
    /// </summary>
    private static HarnessWriteOutcome ApplyEditForm(
        HarnessWriteRequest request, string normalizedPath, string fullPath)
    {
        if (!File.Exists(fullPath))
        {
            return HarnessWriteOutcome.NotApplied(
                $"`edits` requires an EXISTING file, and '{request.Path}' does not exist. Use `content` to " +
                "CREATE the file (the anchored form can only modify text that is already there)");
        }

        byte[] raw;
        try
        {
            raw = File.ReadAllBytes(fullPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return HarnessWriteOutcome.Failed($"the harness could not read '{request.Path}' to edit it: {ex.Message}");
        }

        // Preserve a UTF-8 BOM if the file has one: File.ReadAllText would silently swallow it and
        // AtomicFile writes BOM-less UTF-8, which would change bytes this edit never touched.
        bool hasByteOrderMark = raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF;
        int offset = hasByteOrderMark ? 3 : 0;

        string original;
        try
        {
            // A THROWING decoder, deliberately. The default replaces undecodable bytes with U+FFFD, which
            // would be round-tripped back to disk as a silent corruption of bytes no edit ever named —
            // precisely the failure mode the anchored form exists to make impossible. A target that is not
            // UTF-8 text is refused instead.
            original = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(raw, offset, raw.Length - offset);
        }
        catch (DecoderFallbackException)
        {
            return HarnessWriteOutcome.NotApplied(
                $"'{request.Path}' is not valid UTF-8 text, so it cannot be edited by text anchors — the " +
                "harness will not rewrite a binary or mis-encoded file, because bytes it could not decode " +
                "would be silently replaced. Route this deliverable to a script action or a human");
        }

        (string? edited, string? failure) = ApplyEdits(original, request.Edits);
        if (edited is null)
        {
            return HarnessWriteOutcome.NotApplied($"'{request.Path}' was left UNCHANGED — {failure}");
        }

        if (string.Equals(edited, original, StringComparison.Ordinal))
        {
            return HarnessWriteOutcome.NotApplied(
                $"every edit resolved but the result is byte-identical to '{request.Path}' — each `new` " +
                "repeats its own `old`, so the request would change nothing. Emit the intended replacement " +
                "text, or drop the harness-write request");
        }

        try
        {
            // AtomicFile writes BOM-less UTF-8, so a file that HAD a BOM gets it back as an explicit
            // leading U+FEFF — otherwise an edit would silently strip three bytes it never touched.
            AtomicFile.WriteAllText(fullPath, hasByteOrderMark ? "\uFEFF" + edited : edited);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return HarnessWriteOutcome.Failed($"the harness could not write '{request.Path}': {ex.Message}");
        }

        return HarnessWriteOutcome.Ok(normalizedPath);
    }

    /// <summary>
    /// Apply <paramref name="edits"/> to <paramref name="original"/> in order, returning the edited text
    /// or (null, why-it-failed). Purely in memory — the caller writes only on success.
    /// <para>
    /// <b>Matching is VERBATIM and ORDINAL.</b> No regex, no trimming, no whitespace or case
    /// normalization: an anchor matches the file's bytes exactly as written, indentation and blank lines
    /// included. The ONE tolerance is LINE ENDINGS — if a multi-line anchor finds no verbatim match, it is
    /// re-expressed in the file's own newline convention (CRLF↔LF) and matched verbatim again. That
    /// tolerance is required for a cross-platform harness (a git checkout on Windows can hand the agent
    /// CRLF that its JSON anchor carries as LF) and it cannot mis-target: it only changes which newline
    /// bytes the anchor is spelled with, never which region is chosen, and the exactly-once rule below
    /// still applies to whichever spelling matched. The replacement text is re-expressed the same way, so
    /// the file keeps one newline convention.
    /// </para>
    /// <para>
    /// <b>Each anchor must match EXACTLY ONCE.</b> Zero matches fails (the agent's picture of the file is
    /// wrong — guessing would corrupt it); two or more fails as AMBIGUOUS rather than silently taking the
    /// first, which is the difference between "the harness edited the passage you meant" and "the harness
    /// edited a passage that merely looked like it". Occurrences are counted OVERLAPPING (advance by one
    /// character), so an anchor that overlaps itself is ambiguous too.
    /// </para>
    /// <para>
    /// Edits are applied SEQUENTIALLY: edit N+1 is matched against the result of edits 1..N, the way any
    /// editor would. That is fail-safe in both directions — if an earlier edit destroyed a later anchor
    /// the later one finds zero matches, and if it duplicated one the later one finds two; both are
    /// rejected, and neither can half-apply because nothing is written until all of them resolve.
    /// </para>
    /// </summary>
    private static (string? Text, string? Failure) ApplyEdits(string original, IReadOnlyList<HarnessWriteEdit> edits)
    {
        string working = original;
        string fileNewline = DominantNewline(original);

        for (int i = 0; i < edits.Count; i++)
        {
            string anchor = edits[i].Old;
            string replacement = edits[i].New;
            int matches = CountOccurrences(working, anchor);

            // Line-ending tolerance (see the remarks): only ever consulted when the verbatim spelling
            // found nothing, and only for a multi-line anchor.
            if (matches == 0 && anchor.AsSpan().IndexOf('\n') >= 0)
            {
                string respelled = WithNewline(anchor, fileNewline);
                if (!string.Equals(respelled, anchor, StringComparison.Ordinal))
                {
                    int respelledMatches = CountOccurrences(working, respelled);
                    if (respelledMatches > 0)
                    {
                        anchor = respelled;
                        replacement = WithNewline(replacement, fileNewline);
                        matches = respelledMatches;
                    }
                }
            }

            if (matches == 0)
            {
                return (null, $"edits[{i}].old was NOT FOUND in the file. Anchors are matched VERBATIM " +
                              "(exact indentation, punctuation and blank lines; only line endings are tolerated), " +
                              $"so re-read the file and copy the passage exactly. Anchor: \"{Preview(anchor)}\"");
            }

            if (matches > 1)
            {
                return (null, $"edits[{i}].old is AMBIGUOUS — it occurs {matches} times in the file and must " +
                              "occur exactly once. Extend the anchor with enough surrounding context (the line " +
                              "before/after, a nearby heading) to make it unique; the harness will not guess " +
                              $"which occurrence you meant. Anchor: \"{Preview(anchor)}\"");
            }

            int at = working.IndexOf(anchor, StringComparison.Ordinal);
            working = string.Concat(working.AsSpan(0, at), replacement, working.AsSpan(at + anchor.Length));
        }

        return (working, null);
    }

    /// <summary>
    /// The newline convention to re-spell an anchor into: CRLF when the file contains any CRLF, else LF.
    /// A file with mixed endings is treated as CRLF — the re-spelling is only ever a FALLBACK after a
    /// verbatim miss, and it still has to match exactly once, so a wrong guess simply fails the edit.
    /// </summary>
    private static string DominantNewline(string text) =>
        text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

    /// <summary>Re-spell every newline in <paramref name="text"/> as <paramref name="newline"/>.</summary>
    private static string WithNewline(string text, string newline)
    {
        string lf = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        return newline == "\n" ? lf : lf.Replace("\n", newline, StringComparison.Ordinal);
    }

    /// <summary>
    /// Count OVERLAPPING ordinal occurrences of <paramref name="needle"/> (advance by one character, not
    /// by the needle's length). Overlapping is the stricter reading: an anchor that overlaps itself has
    /// more than one valid start position and is genuinely ambiguous, so it must be rejected.
    /// </summary>
    private static int CountOccurrences(string haystack, string needle)
    {
        if (needle.Length == 0)
        {
            return 0;
        }

        int count = 0;
        int from = 0;
        while (from <= haystack.Length - needle.Length)
        {
            int hit = haystack.IndexOf(needle, from, StringComparison.Ordinal);
            if (hit < 0)
            {
                break;
            }

            count++;
            from = hit + 1;
        }

        return count;
    }

    /// <summary>
    /// A single-line, length-capped, ASCII-safe echo of an anchor for a failure message — newlines and
    /// tabs escaped so the anchor stays readable in a one-line journal summary as well as in feedback.md.
    /// </summary>
    private static string Preview(string anchor)
    {
        string oneLine = anchor
            .Replace("\r\n", "\\n", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\r", "\\n", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal);

        return oneLine.Length <= AnchorPreviewChars ? oneLine : oneLine[..AnchorPreviewChars] + "...";
    }

    /// <summary>
    /// True when <paramref name="normalizedPath"/> (workspace-relative, forward-slashed) resolves to a
    /// Claude Code permission-granting settings file — <c>.claude/settings.json</c> or
    /// <c>.claude/settings.local.json</c> — living directly inside a <c>.claude</c> directory at any
    /// depth (the standard location is the repo root, but a nested <c>.claude/</c> is matched too). The
    /// carve-out of issue #321: these files grant tool permissions and are never harness-written on an
    /// agent's behalf. Matched case-insensitively so a trivial casing variant cannot bypass the block.
    /// </summary>
    private static bool IsClaudeSettingsFile(string normalizedPath)
    {
        int lastSlash = normalizedPath.LastIndexOf('/');
        string fileName = lastSlash < 0 ? normalizedPath : normalizedPath[(lastSlash + 1)..];

        bool isSettingsFileName =
            fileName.Equals("settings.json", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("settings.local.json", StringComparison.OrdinalIgnoreCase);
        if (!isSettingsFileName)
        {
            return false;
        }

        // The file must sit DIRECTLY inside a `.claude` directory (its immediate parent) — a stray
        // `settings.json` elsewhere in the tree is not a permission grant and stays writable.
        if (lastSlash < 0)
        {
            return false;
        }

        string parent = normalizedPath[..lastSlash];
        int parentSlash = parent.LastIndexOf('/');
        string parentName = parentSlash < 0 ? parent : parent[(parentSlash + 1)..];
        return parentName.Equals(".claude", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// One anchored replacement in a <c>needsHarnessWrite</c> <c>edits</c> array (issue #437).
/// <see cref="Old"/> is verbatim anchor text that must occur EXACTLY ONCE in the target;
/// <see cref="New"/> replaces it (an empty string deletes the anchored text).
/// </summary>
public sealed record HarnessWriteEdit
{
    /// <summary>Verbatim anchor text; must occur exactly once in the file at the time this edit applies.</summary>
    public required string Old { get; init; }

    /// <summary>Replacement text; may be empty (a deletion).</summary>
    public required string New { get; init; }
}

/// <summary>A parsed <c>needsHarnessWrite</c> request (issues #191, #437).</summary>
public sealed record HarnessWriteRequest
{
    /// <summary>Workspace-relative destination path (same convention as <c>writeScope</c> entries).</summary>
    public required string Path { get; init; }

    /// <summary>
    /// FULL-CONTENT form: the complete file content to write, verbatim. Null in the anchored-edit form.
    /// Mutually exclusive with <see cref="Edits"/>.
    /// </summary>
    public string? Content { get; init; }

    /// <summary>
    /// ANCHORED-EDIT form (issue #437): the ordered old→new replacements to perform against the EXISTING
    /// file. Empty in the full-content form. Mutually exclusive with <see cref="Content"/>.
    /// </summary>
    public IReadOnlyList<HarnessWriteEdit> Edits { get; init; } = [];

    /// <summary>Optional human-readable reason the action could not write this itself.</summary>
    public string? Reason { get; init; }

    /// <summary>
    /// Set when the fragment carried a <c>needsHarnessWrite</c> OBJECT whose payload could not be used
    /// (no <c>path</c>, both or neither of <c>content</c>/<c>edits</c>, a malformed edit entry). The
    /// request is still surfaced — rather than dropped — so the attempt fails with this actionable reason
    /// instead of a generic foreign-key merge error (issue #437).
    /// </summary>
    public string? InvalidReason { get; init; }

    /// <summary>True when this request uses the anchored-edit form.</summary>
    public bool IsEditForm => Edits.Count > 0;

    /// <summary>The path to name in a message, with a placeholder when the payload never supplied one.</summary>
    public string PathForDisplay => string.IsNullOrWhiteSpace(Path) ? "(no path given)" : Path;
}

/// <summary>The outcome of validating + performing a <see cref="HarnessWriteRequest"/>.</summary>
public sealed record HarnessWriteOutcome
{
    /// <summary>True when the write was validated AND performed.</summary>
    public bool Succeeded { get; init; }

    /// <summary>True when the failure was a VALIDATION rejection (out of scope / escapes workspace / policy-denied) rather than an IO failure.</summary>
    public bool WasRejected { get; init; }

    /// <summary>
    /// True when the rejection was a POLICY DENIAL (issue #321 — a permission-granting
    /// <c>.claude/settings*.json</c> the harness refuses to write on an agent's behalf) rather than a
    /// scope/containment rejection. Implies <see cref="WasRejected"/>; drives distinct retry feedback.
    /// </summary>
    public bool IsPolicyDenied { get; init; }

    /// <summary>
    /// True when the request was in bounds and permitted but could NOT BE APPLIED as written (issue
    /// #437): an unusable payload, an anchor that matched zero or several times, `edits` against a file
    /// that does not exist, or full-content mode against a target too large for it. Implies
    /// <see cref="WasRejected"/> (nothing was written, the target is byte-identical); drives feedback that
    /// restates the accepted schema so the agent can re-emit a correct request.
    /// </summary>
    public bool IsNotApplied { get; init; }

    /// <summary>The workspace-relative path written, when <see cref="Succeeded"/>.</summary>
    public string? WrittenPath { get; init; }

    /// <summary>An actionable reason, set when NOT <see cref="Succeeded"/>.</summary>
    public string? FailureReason { get; init; }

    public static HarnessWriteOutcome Ok(string writtenPath) => new() { Succeeded = true, WrittenPath = writtenPath };

    public static HarnessWriteOutcome Rejected(string reason) =>
        new() { Succeeded = false, WasRejected = true, FailureReason = reason };

    /// <summary>A policy denial (issue #321): a permission-granting settings file the harness will not write.</summary>
    public static HarnessWriteOutcome Denied(string reason) =>
        new() { Succeeded = false, WasRejected = true, IsPolicyDenied = true, FailureReason = reason };

    /// <summary>
    /// A permitted request the harness could not apply (issue #437). Nothing was written — the target is
    /// exactly as it was — and the agent can fix it by re-emitting a corrected request.
    /// </summary>
    public static HarnessWriteOutcome NotApplied(string reason) =>
        new() { Succeeded = false, WasRejected = true, IsNotApplied = true, FailureReason = reason };

    public static HarnessWriteOutcome Failed(string reason) =>
        new() { Succeeded = false, WasRejected = false, FailureReason = reason };
}
