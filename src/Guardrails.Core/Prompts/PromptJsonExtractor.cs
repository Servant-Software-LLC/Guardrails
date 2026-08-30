namespace Guardrails.Core.Prompts;

/// <summary>
/// Recovers a JSON candidate from a model's raw final message (plan 28 §3.3/§6.4): the last fenced
/// <c>```json</c> block if one exists, else the last top-level JSON object. The candidate must parse
/// as JSON or nothing is extracted — fail closed, never a partial or a guess.
/// <para>
/// Shared by three consumers so their leniency cannot drift apart: the guardrail verdict
/// transcription path, <c>OverwatchProposal.TryParse</c>, and the needs-human triage sidecar writer.
/// </para>
/// </summary>
public static class PromptJsonExtractor
{
    /// <summary>
    /// Extract the JSON candidate from <paramref name="text"/>, or <c>null</c> when none is found or
    /// the candidate does not parse.
    /// </summary>
    public static string? Extract(string? text) => throw new NotImplementedException();
}
