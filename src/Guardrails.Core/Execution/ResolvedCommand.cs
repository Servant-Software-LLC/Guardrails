namespace Guardrails.Core.Execution;

/// <summary>
/// A concrete command to spawn: the executable plus its full argument list, ready for
/// <c>ProcessStartInfo.ArgumentList</c> (never a concatenated shell string). The
/// script path and any script args are already substituted in.
/// </summary>
public sealed record ResolvedCommand
{
    /// <summary>The executable to launch (e.g. "pwsh", "bash", or the script itself for direct spawn).</summary>
    public required string Executable { get; init; }

    /// <summary>Arguments passed verbatim via ArgumentList.</summary>
    public required IReadOnlyList<string> Arguments { get; init; }

    /// <summary>
    /// An optional predicate over INHERITED environment-variable names: every inherited variable it matches is
    /// removed from the child's environment before the overlay is applied (#782 §1.2 step 1 — the gateway scrub of
    /// <c>ANTHROPIC_*</c>, <c>CLAUDE_CODE_USE_*</c> and friends). Names are handed to it as the OS spells them; the
    /// predicate compares with <see cref="ProcessRunner.EnvironmentNameComparison"/>. Null = no scrub, the pre-#782
    /// behavior every existing caller keeps.
    /// </summary>
    public Func<string, bool>? ScrubInheritedEnvironment { get; init; }
}
