using Guardrails.Cli;

namespace Guardrails.Integration.Tests;

/// <summary>
/// A <see cref="StringWriter"/>-backed <see cref="IConsoleIo"/> for driving the CLI in tests.
/// Each instance owns its own <see cref="Out"/> / <see cref="Error"/> buffers, so a test
/// captures exactly the output of the command it invoked — no process-global
/// <c>Console.Out</c>, no <c>Console.SetOut</c>, and no cross-test race. Safe to run in
/// parallel: nothing here touches shared state.
/// </summary>
public sealed class StringConsoleIo : IConsoleIo
{
    private readonly StringWriter _out = new();
    private readonly StringWriter _error = new();

    public TextWriter Out => _out;

    public TextWriter Error => _error;

    /// <summary>Everything written to <see cref="Out"/> so far.</summary>
    public string OutText => _out.ToString();

    /// <summary>Everything written to <see cref="Error"/> so far.</summary>
    public string ErrorText => _error.ToString();
}
