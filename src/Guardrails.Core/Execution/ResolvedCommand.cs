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
}
