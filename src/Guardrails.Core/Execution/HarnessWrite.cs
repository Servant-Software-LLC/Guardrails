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
/// Wire shape (a root fragment key, parsed the SAME way as <c>needsHuman</c> — see
/// <see cref="ActionRunner"/>):
/// <code>{ "needsHarnessWrite": { "path": ".claude/skills/foo/SKILL.md", "content": "...", "reason": "..." } }</code>
/// Singular only in v1 (one harness-write per attempt) — an action needing multiple <c>.claude/</c>
/// files touched does so across multiple attempts/retries. This is a documented v1 limitation, not
/// something this issue solves.
/// </para>
/// </summary>
public static class HarnessWrite
{
    /// <summary>
    /// Read the (already-written) action fragment and, if its root is an object with a
    /// <c>needsHarnessWrite</c> key whose value is an object carrying string <c>path</c>/<c>content</c>
    /// (and optionally <c>reason</c>), return the parsed request. Anything else — key absent, wrong
    /// shape, unparseable JSON — returns null (never throws; an invalid shape here is reported the
    /// same way an invalid fragment shape always is, by the normal fragment-validation path once this
    /// key is stripped).
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

            if (!request.TryGetProperty("path", out JsonElement pathEl) || pathEl.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            if (!request.TryGetProperty("content", out JsonElement contentEl) || contentEl.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            string? reason = request.TryGetProperty("reason", out JsonElement reasonEl) && reasonEl.ValueKind == JsonValueKind.String
                ? reasonEl.GetString()
                : null;

            return new HarnessWriteRequest
            {
                Path = pathEl.GetString() ?? "",
                Content = contentEl.GetString() ?? "",
                Reason = reason
            };
        }
        catch (JsonException)
        {
            // Not parseable JSON → not a needsHarnessWrite signal; the normal fragment validation
            // path (MergeFragment / ValidateFragmentForSettle) reports the malformed JSON as usual.
            return null;
        }
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
    /// Two INDEPENDENT checks, both load-bearing (a security boundary, SSOT §9):
    /// <list type="bullet">
    /// <item><b>Workspace-escape check (always runs, regardless of <c>writeScope</c>).</b> Reuses
    /// <see cref="WorkspaceContainment.Escapes"/> — the same "does this path escape the boundary"
    /// predicate the worktree-containment hook's design note points at — so a request like
    /// <c>../../etc/passwd</c> or an absolute path is rejected before it is even a writeScope
    /// question. This protects a task with NO declared writeScope too (the segment-worktree
    /// containment is the boundary in that case).</item>
    /// <item><b>writeScope-membership check (only when the task DECLARES a writeScope).</b> Reuses
    /// <see cref="WriteScope.IsInScope"/> — the SAME scope-matching predicate the POST-HOC
    /// write-scope CHECK (SSOT §3.4) uses, so the two enforcement points can never drift. A task
    /// with NO writeScope declared allows the write unconditionally (mirroring "absent ⇒ no check"
    /// for the existing retrospective check, SSOT §3.4) — the segment-worktree containment + the
    /// worktree-containment hook (#199/#192) are the backstops in that case.</item>
    /// </list>
    /// </remarks>
    public static HarnessWriteOutcome Validate(
        HarnessWriteRequest request, string workspaceRoot, IReadOnlyList<string>? writeScope)
    {
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

        // writeScope-membership check: only when the task declares one. Absent ⇒ allowed (mirrors the
        // retrospective write-scope check's "Absent ⇒ no check", SSOT §3.4 — the segment-worktree
        // containment + the worktree-containment hook are the backstops in that case).
        if (writeScope is { Count: > 0 } scope && !WriteScope.IsInScope(normalizedPath, scope))
        {
            return HarnessWriteOutcome.Rejected(
                $"path '{request.Path}' is outside this task's declared writeScope");
        }

        string fullPath = Path.GetFullPath(Path.Combine(workspaceRoot, normalizedPath));

        try
        {
            AtomicFile.WriteAllText(fullPath, request.Content);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return HarnessWriteOutcome.Failed($"the harness could not write '{request.Path}': {ex.Message}");
        }

        return HarnessWriteOutcome.Ok(normalizedPath);
    }
}

/// <summary>A parsed <c>needsHarnessWrite</c> request (issue #191).</summary>
public sealed record HarnessWriteRequest
{
    /// <summary>Workspace-relative destination path (same convention as <c>writeScope</c> entries).</summary>
    public required string Path { get; init; }

    /// <summary>The file content to write, verbatim.</summary>
    public required string Content { get; init; }

    /// <summary>Optional human-readable reason the action could not write this itself.</summary>
    public string? Reason { get; init; }
}

/// <summary>The outcome of validating + performing a <see cref="HarnessWriteRequest"/>.</summary>
public sealed record HarnessWriteOutcome
{
    /// <summary>True when the write was validated AND performed.</summary>
    public bool Succeeded { get; init; }

    /// <summary>True when the failure was a VALIDATION rejection (out of scope / escapes workspace) rather than an IO failure.</summary>
    public bool WasRejected { get; init; }

    /// <summary>The workspace-relative path written, when <see cref="Succeeded"/>.</summary>
    public string? WrittenPath { get; init; }

    /// <summary>An actionable reason, set when NOT <see cref="Succeeded"/>.</summary>
    public string? FailureReason { get; init; }

    public static HarnessWriteOutcome Ok(string writtenPath) => new() { Succeeded = true, WrittenPath = writtenPath };

    public static HarnessWriteOutcome Rejected(string reason) =>
        new() { Succeeded = false, WasRejected = true, FailureReason = reason };

    public static HarnessWriteOutcome Failed(string reason) =>
        new() { Succeeded = false, WasRejected = false, FailureReason = reason };
}
