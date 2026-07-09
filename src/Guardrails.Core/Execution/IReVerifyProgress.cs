using Guardrails.Core.Model;

namespace Guardrails.Core.Execution;

/// <summary>
/// Optional per-guardrail liveness seam for a long-running re-verify pass (issue #331). The
/// <see cref="GuardrailReVerifier"/> runs its guardrail set sequentially and, when a progress sink is
/// supplied, announces each guardrail as it STARTS and COMPLETES. A UI-free contract: Core reports
/// only which guardrail is running (and its optional expected duration, read off the
/// <see cref="GuardrailDefinition"/>); the CLI layer turns those signals into a wall-clock heartbeat
/// on the surface that renders the phase's progress line (#145-safe — never a raw console write into
/// an active live region). Implementations MUST be cheap and non-throwing — they are invoked on the
/// re-verify loop, and a progress hiccup must never change a gate's verdict.
/// </summary>
public interface IReVerifyProgress
{
    /// <summary>A guardrail is about to run. Called once per guardrail, in order.</summary>
    void GuardrailStarting(GuardrailDefinition guardrail);

    /// <summary>
    /// The guardrail just finished (pass or fail). Always paired with a prior
    /// <see cref="GuardrailStarting"/>, including when the guardrail threw or timed out.
    /// </summary>
    void GuardrailCompleted(GuardrailDefinition guardrail);
}
