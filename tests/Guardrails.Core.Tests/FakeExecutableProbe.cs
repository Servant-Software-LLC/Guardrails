using Guardrails.Core.Execution;

namespace Guardrails.Core.Tests;

/// <summary>
/// A test double for <see cref="IExecutableProbe"/> with an explicit allow-list of
/// resolvable executables — so interpreter-resolution tests never depend on the
/// machine's real PATH state.
/// </summary>
public sealed class FakeExecutableProbe : IExecutableProbe
{
    private readonly HashSet<string>? _available;

    private FakeExecutableProbe(HashSet<string>? available) => _available = available;

    /// <summary>A probe resolving exactly the named commands.</summary>
    public static FakeExecutableProbe With(params string[] available) =>
        new(new HashSet<string>(available, StringComparer.OrdinalIgnoreCase));

    /// <summary>A probe where every queried command resolves.</summary>
    public static FakeExecutableProbe All { get; } = new(available: null);

    /// <summary>A probe where nothing resolves.</summary>
    public static FakeExecutableProbe None { get; } = With();

    public bool Exists(string command) => _available is null || _available.Contains(command);
}
