namespace Guardrails.Core.Execution;

/// <summary>
/// The caller-scoping rule for <c>guardrails supply</c> — design 40 §5a/§6, DECIDED in review: a task
/// agent may supply only paths inside its own <c>writeScope</c>; an operator invocation is unrestricted.
/// <c>writeScope</c> is what stops a task editing files it does not own, and a task that could call
/// <c>supply</c> unscoped would put any file onto the run's base without that check ever being consulted.
/// <para>
/// The caller is distinguished by whether the harness-owned <c>GUARDRAILS_*</c> namespace is present in
/// its environment: #442 made that namespace hermetic across the process boundary (SSOT §5.1), so a
/// <c>supply</c> invoked from inside a task action reliably sees it and one invoked from an operator's own
/// shell does not. <b>This is a guard against the accidental and naive case, not against an adversary</b> —
/// an agent that can run <c>guardrails supply</c> can also run it with those variables cleared. The
/// provenance record (§4) is the actual defence for that case; it is a different mechanism entirely.
/// </para>
/// <para>
/// Pure decision logic: no process spawning, no git, no plan loading. The caller resolves which task is
/// asking and what that task's own <c>writeScope</c> is (e.g. from <c>GUARDRAILS_TASK_ID</c> plus the
/// loaded plan) and passes it in — this class only decides membership, reusing
/// <see cref="WriteScope.IsInScope"/>, the same rule the harness already enforces at write time. Two
/// mechanisms for one decision is how a file becomes suppliable and unwritable at the same moment.
/// </para>
/// </summary>
public static class SupplyCallerScope
{
    /// <summary>
    /// Decides whether <paramref name="workspaceRelativePath"/> may be supplied, given the calling
    /// process's <paramref name="environment"/> and the calling task's own <paramref name="writeScope"/>
    /// (ignored for an operator invocation).
    /// </summary>
    /// <param name="environment">
    /// The calling process's environment. Presence of any <c>GUARDRAILS_*</c> key marks this as a task
    /// invocation; its complete absence marks it as an operator invocation (design 40 §5a).
    /// </param>
    /// <param name="writeScope">The calling task's own declared <c>writeScope</c> globs.</param>
    /// <param name="workspaceRelativePath">The workspace-relative path the caller wants to supply.</param>
    public static CallerScopeResult Check(
        IReadOnlyDictionary<string, string> environment,
        IReadOnlyList<string> writeScope,
        string workspaceRelativePath)
    {
        throw new NotImplementedException();
    }
}

/// <summary>The outcome of a <see cref="SupplyCallerScope.Check"/> call.</summary>
public sealed record CallerScopeResult
{
    /// <summary>True when the caller is a scoped task invocation and the path fell outside its <c>writeScope</c>.</summary>
    public required bool Refused { get; init; }

    /// <summary>A human-readable reason for the refusal, or null when not <see cref="Refused"/>.</summary>
    public string? RefusalReason { get; init; }
}
