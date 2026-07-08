namespace Guardrails.Core.Execution;

/// <summary>
/// A preserved retry-salvage snapshot (issues #195 / #306): the git ref an about-to-be-rolled-back
/// attempt's working tree was committed to, plus a <c>git diff --stat</c> summary of what it changed
/// relative to the task's <c>taskBase</c>, plus (issue #306) the path to a directly-readable, applyable
/// patch file. Produced by <see cref="TaskExecutor"/> immediately before the F2 rollback discards the
/// attempt's uncommitted writes, and consumed by <see cref="RetryPolicy"/> to compose the next attempt's
/// feedback so the retry agent can, at its own discretion, pull ALL of the prior work (<c>git apply</c>
/// the patch), SOME of it (<c>git checkout &lt;ref&gt; -- &lt;path&gt;</c> per file), or NONE (re-author) —
/// instead of re-deriving everything from a summary. Issue #306 extends this beyond #195's non-logic
/// outcomes to EVERY non-final worktree failure (guardrail-fail, action-fail, timeout, max-turns,
/// output-cap, write-scope, …); the clean-slate reset to <c>taskBase</c> remains the DEFAULT starting
/// point and the stash is opt-in for the agent.
/// </summary>
/// <param name="RefName">
/// The ref the attempt was preserved to, e.g. <c>refs/guardrails/&lt;taskId&gt;/attempt-3</c>.
/// </param>
/// <param name="DiffStat">
/// A <c>git diff --stat</c> summary of <see cref="RefName"/> against <c>taskBase</c> — what the
/// salvaged attempt actually changed. May be empty when the diff could not be computed (best-effort).
/// </param>
/// <param name="Attempt">The 1-based attempt number this snapshot was taken from.</param>
/// <param name="PatchPath">
/// Absolute path to a directly-readable, applyable unified-diff patch of the salvaged attempt vs
/// <c>taskBase</c> (issue #306), written into the attempt's log dir. The retry agent can
/// <c>git apply &lt;PatchPath&gt;</c> to pull ALL the prior work, or read it to cherry-pick by hand. Null
/// when the patch could not be produced (empty diff or a best-effort git failure) — the ref is still
/// offered for per-file <c>git checkout</c>.
/// </param>
public sealed record SalvageRef(string RefName, string DiffStat, int Attempt, string? PatchPath = null);
