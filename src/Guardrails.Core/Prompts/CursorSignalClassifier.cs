using System.Text.RegularExpressions;

namespace Guardrails.Core.Prompts;

/// <summary>
/// Cursor-specific recognition of a run the CLI refused because of its OWN CONFIGURATION (#767, SSOT §9.9) — the
/// sole home of that fragile vendor wording, inside the Cursor quarantine exactly as
/// <see cref="ClaudeSignalClassifier"/> holds Claude's, so a Cursor wording change is a one-line edit here with
/// a failing test pointing at it. The harness routes on <see cref="PromptFailureKind.RunnerConfiguration"/>
/// only, never on this text.
///
/// <para><b>The one shape today</b>, measured on an enterprise account (Cursor <c>agent</c> 2026.09.23):
/// launched with <c>--force</c>, the CLI exits 1 before any stream with
/// <c>Error: Your team administrator has disabled the 'Run Everything' option. Please run without '--force' …</c>
/// on stderr. No retry under <c>--force</c> can succeed and no wait helps, so it is neither
/// <see cref="PromptFailureKind.Error"/> (which would burn every retry in seconds) nor
/// <see cref="PromptFailureKind.Transient"/>.</para>
///
/// <para>Anchored on the administrator sentence rather than the bare words "Run Everything", which an agent's
/// own output could mention; the quote characters around the option name are optional and may be curly.</para>
/// </summary>
internal static class CursorSignalClassifier
{
    private static readonly Regex RunEverythingDisabled = new(
        @"administrator\s+has\s+disabled\s+the\s+['""‘’“”]?Run\s+Everything",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>True when <paramref name="text"/> is Cursor refusing <c>--force</c> because the team disabled Run Everything.</summary>
    public static bool IsRunEverythingDisabled(string? text) =>
        !string.IsNullOrWhiteSpace(text) && RunEverythingDisabled.IsMatch(text);
}
