namespace Guardrails.Cli;

/// <summary>
/// The CLI's single output seam. Every command and helper writes user-facing output through
/// the <see cref="Out"/> and <see cref="Error"/> writers rather than the process-global
/// <see cref="System.Console"/>, so output is captured per-invocation and output-capturing
/// tests never race on <c>Console.Out</c>. Production wires this to the real console via
/// <see cref="SystemConsoleIo"/>; tests inject a <see cref="System.IO.StringWriter"/>-backed
/// double. This interface intentionally covers OUTPUT only — interactive input (the reset
/// confirmation prompt) and the UI-capability probes (redirection/interactivity) still talk
/// to <see cref="System.Console"/> directly.
/// </summary>
public interface IConsoleIo
{
    /// <summary>The standard-output writer for user-facing lines.</summary>
    TextWriter Out { get; }

    /// <summary>The standard-error writer for diagnostics and failure notices.</summary>
    TextWriter Error { get; }
}
