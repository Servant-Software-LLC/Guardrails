namespace Guardrails.Core.Execution;

/// <summary>
/// A preserved retry-salvage snapshot (issue #195): the git ref an about-to-be-rolled-back attempt's
/// working tree was committed to, plus a <c>git diff --stat</c> summary of what it changed relative to
/// the task's <c>taskBase</c>. Produced by <see cref="TaskExecutor"/> immediately before the F2 rollback
/// discards the attempt's uncommitted writes, and consumed by <see cref="RetryPolicy"/> to compose the
/// next attempt's feedback — naming the ref and the diff-stat so the agent can selectively
/// <c>git checkout &lt;ref&gt; -- &lt;path&gt;</c> the good parts instead of re-deriving everything from scratch.
/// </summary>
/// <param name="RefName">
/// The ref the attempt was preserved to, e.g. <c>refs/guardrails/&lt;taskId&gt;/attempt-3</c>.
/// </param>
/// <param name="DiffStat">
/// A <c>git diff --stat</c> summary of <see cref="RefName"/> against <c>taskBase</c> — what the
/// salvaged attempt actually changed. May be empty when the diff could not be computed (best-effort).
/// </param>
/// <param name="Attempt">The 1-based attempt number this snapshot was taken from.</param>
public sealed record SalvageRef(string RefName, string DiffStat, int Attempt);
