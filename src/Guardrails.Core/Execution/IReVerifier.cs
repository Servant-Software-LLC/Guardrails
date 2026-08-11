using Guardrails.Core.Model;

namespace Guardrails.Core.Execution;

/// <summary>
/// Attempt-decoupled re-verify seam (plan 08 M4 / feasibility-fix-2, SSOT §4.3).
/// Given a worktree path and a guardrail set, runs those guardrails against the bytes
/// currently on disk and returns a pass/fail aggregate. Has no dependence on an attempt
/// logDir, attempt number, or action result — GUARDRAILS_ACTION_* env vars are never
/// injected by this path.
/// </summary>
public interface IReVerifier
{
    Task<ReVerifyResult> ReVerifyAsync(
        string worktreePath,
        IReadOnlyList<GuardrailDefinition> guardrails,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Re-verify with the optional per-evaluation concerns of <see cref="ReVerifyOptions"/> — a liveness
    /// heartbeat (issue #331) and captured-output persistence (issue #432). DEFAULT-IMPLEMENTED to forward
    /// to the plain overload so a test fake that models only pass/fail keeps compiling and behaving
    /// identically; <see cref="GuardrailReVerifier"/> overrides it with the real capture.
    /// </summary>
    Task<ReVerifyResult> ReVerifyAsync(
        string worktreePath,
        IReadOnlyList<GuardrailDefinition> guardrails,
        ReVerifyOptions? options,
        CancellationToken cancellationToken = default)
        => ReVerifyAsync(worktreePath, guardrails, cancellationToken);
}

/// <summary>
/// The optional, cross-cutting concerns of ONE re-verify evaluation. Both are null/absent by default, so
/// the union re-verify path (which wants neither) is unaffected.
/// </summary>
public sealed record ReVerifyOptions
{
    /// <summary>
    /// Per-guardrail liveness sink (issue #331) — announced as each check starts/completes so a long
    /// plan-level gate can surface a wall-clock heartbeat. Null ⇒ no announcements.
    /// </summary>
    public IReVerifyProgress? Progress { get; init; }

    /// <summary>
    /// Absolute directory under <c>logs/&lt;runId&gt;/</c> that each check's captured stdout/stderr is
    /// persisted into (issue #432, SSOT §8) — see <see cref="GateArtifacts"/>. Null ⇒ capture nothing
    /// (the pre-#432 behaviour, still used by the attempt-decoupled union re-verify).
    /// </summary>
    public string? ArtifactDirectory { get; init; }
}

/// <summary>The aggregate pass/fail result returned by <see cref="IReVerifier"/>.</summary>
public sealed record ReVerifyResult
{
    public required bool Passed { get; init; }
    public IReadOnlyList<GuardrailResult> FailedGuardrails { get; init; } = [];
}
