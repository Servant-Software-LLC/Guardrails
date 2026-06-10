namespace Guardrails.Core.Model;

/// <summary>
/// How an action or guardrail is executed. SSOT §3: ".prompt.md" → prompt;
/// anything else → a script/executable resolved through the interpreter map.
/// </summary>
public enum ActionKind
{
    /// <summary>A script or executable run via the interpreter map (SSOT §5.2).</summary>
    Script,

    /// <summary>An LLM prompt action/guardrail (a <c>*.prompt.md</c> file). Not executable until M5.</summary>
    Prompt
}
