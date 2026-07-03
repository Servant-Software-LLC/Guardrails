namespace Guardrails.Core.Journal;

/// <summary>
/// The status of a whole-plan phase result recorded OUTSIDE <c>tasks{}</c> in the top-level journal
/// sections (SSOT §7, the two-scope preflights F9 split): <c>planPreflights</c> (the pre-DAG phase) and
/// <c>planGuardrails</c> (the terminal <c>&lt;plan&gt;/guardrails/</c> gate on the merged HEAD). Both
/// sections carry a status; a passing phase records <see cref="Passed"/>, and each phase has its own
/// distinct FAILURE token so a human sees WHICH plan-scoped gate halted the run (both exit 2).
///
/// These are plan-scoped phase outcomes, DISTINCT from the per-task <see cref="AttemptOutcome"/> journaled
/// inside <c>tasks{}</c> (including the per-task <see cref="AttemptOutcome.TaskPreflightFailed"/>).
/// </summary>
public enum PlanPhaseStatus
{
    /// <summary>The plan-scoped phase passed (scheduling proceeded / the terminal gate was green).</summary>
    Passed,

    /// <summary>
    /// The pre-DAG <c>planPreflights</c> phase failed → the harness halts BEFORE scheduling any task
    /// (exit 2). Journaled as <c>planPreflights.status</c>.
    /// </summary>
    PlanPreflightFailed,

    /// <summary>
    /// The terminal <c>&lt;plan&gt;/guardrails/</c> gate failed on the merged plan-branch HEAD → the run
    /// halts (exit 2). Journaled as <c>planGuardrails.status</c>.
    /// </summary>
    PlanGuardrailFailed
}
