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
}

/// <summary>The aggregate pass/fail result returned by <see cref="IReVerifier"/>.</summary>
public sealed record ReVerifyResult
{
    public required bool Passed { get; init; }
    public IReadOnlyList<GuardrailResult> FailedGuardrails { get; init; } = [];
}
