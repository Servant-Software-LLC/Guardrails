namespace Guardrails.Cli;

/// <summary>
/// The production <see cref="IConsoleIo"/>: <see cref="Out"/> and <see cref="Error"/> return
/// the live <see cref="System.Console.Out"/> / <see cref="System.Console.Error"/>. This is the
/// ONLY place in the CLI that touches the process-global console for output. A single shared
/// <see cref="Instance"/> is fine — it holds no state; the writers are resolved on each access
/// so a caller that redirected the console still sees the redirection.
/// </summary>
public sealed class SystemConsoleIo : IConsoleIo
{
    /// <summary>The shared production instance, wired in <c>Program.cs</c>.</summary>
    public static SystemConsoleIo Instance { get; } = new();

    private SystemConsoleIo()
    {
    }

    public TextWriter Out => Console.Out;

    public TextWriter Error => Console.Error;
}
