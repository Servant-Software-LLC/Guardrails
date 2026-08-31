namespace Guardrails.Core.Prompts;

/// <summary>
/// What a <see cref="PromptInvocation"/> is FOR (plan 28 §3.4). Set by the harness at every
/// construction site; a runner may refuse a role its class cannot honestly serve (SSOT §9).
///
/// <para>The classification rule, so a future call site does not have to guess: does this prompt
/// write anything other than its own verdict file? Yes ⇒ <see cref="Action"/>. No, and its output
/// is a pass/fail ⇒ <see cref="Guardrail"/>. No, and its output is advice ⇒ <see cref="Advisory"/>.</para>
/// </summary>
public enum PromptRole
{
    /// <summary>Produces work: writes files other than its own verdict, or runs commands.</summary>
    Action,

    /// <summary>Renders a verdict on work. Reads; writes only the verdict file.</summary>
    Guardrail,

    /// <summary>Renders an opinion the harness may not treat as a verdict. Reads; writes nothing.</summary>
    Advisory
}
