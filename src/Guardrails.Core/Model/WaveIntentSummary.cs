namespace Guardrails.Core.Model;

/// <summary>
/// The one-line wave-intent report that <c>guardrails validate</c> and <c>guardrails plan</c> both print on
/// a waved plan (issue #477, doc 19 §3.2 — "#477's explicit floor"): <c>Waves: 3 intended, 2 declared (1 not
/// yet created)</c>.
///
/// <para><b>Why it is shared rather than written twice.</b> The point of the line is that a plan can be
/// ASKED how many waves it was supposed to have — a question that had no answer anywhere before
/// <see cref="RunConfig.IntendedWaves"/> existed. Two surfaces answering it in two spellings would
/// reintroduce, in miniature, the disagreement the field exists to make impossible.</para>
///
/// <para><b>It reports; it never judges.</b> The verdict is
/// <see cref="Loading.DiagnosticCodes.IntendedWaveNotDeclared"/>'s, and that warning is gated on
/// <c>planIsClosed</c>. This line is unconditional on a waved plan precisely so the healthy JIT mid-plan
/// state — where GR2062 is correctly silent — still SHOWS its shortfall rather than looking like agreement.
/// </para>
/// </summary>
public static class WaveIntentSummary
{
    /// <summary>
    /// The line for <paramref name="plan"/>, or <c>null</c> when there is nothing to say — a FLAT plan,
    /// which declares no waves and for which the field is not defined (SSOT §2). Never ends in a newline.
    /// </summary>
    public static string? Describe(PlanDefinition plan)
    {
        if (!plan.IsWaved)
        {
            return null;
        }

        int declared = plan.Waves.Count;
        if (plan.Config.IntendedWaves is not { } intended)
        {
            return $"Waves: {declared} declared (intent not recorded)";
        }

        string qualifier =
            intended > declared ? $" ({intended - declared} not yet created)"
            : intended < declared ? $" ({declared - intended} beyond the stated intent)"
            : "";

        return $"Waves: {intended} intended, {declared} declared{qualifier}";
    }
}
