namespace Guardrails.Core.Execution;

/// <summary>
/// Probes whether an executable (a command name or absolute path) is resolvable on
/// the current machine — the "which"/"where" lookup. Injected so interpreter
/// resolution can be unit-tested without depending on machine-specific PATH state.
/// </summary>
public interface IExecutableProbe
{
    /// <summary>True if <paramref name="command"/> resolves to a runnable executable on PATH (or is an existing file).</summary>
    bool Exists(string command);
}
