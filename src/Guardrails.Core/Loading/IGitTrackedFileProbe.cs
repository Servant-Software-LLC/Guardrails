namespace Guardrails.Core.Loading;

/// <summary>
/// Answers whether a workspace-relative path is tracked by git — GR2060's producer-coverage check
/// needs this to distinguish "the plan will create this file" from "this file already exists and the
/// plan just never says so."
///
/// <para><b>Silence is not proof</b>, exactly as <see cref="IScriptSyntaxProbe"/>'s contract reads: when
/// git is unavailable or the answer cannot be obtained, the probe reports NOT-KNOWN, and a not-known
/// answer must never be read as "untracked". GR2060 is an ERROR-severity check — a probe that guessed
/// "untracked" when it simply could not ask would make GR2060 fire on a correct plan, which is the one
/// failure mode this design cannot afford.</para>
///
/// <para>Injected rather than called directly, mirroring <see cref="IScriptSyntaxProbe"/> and
/// <see cref="Execution.IExecutableProbe"/>: the validator stays a near-pure function over a
/// <c>PlanDefinition</c>, and the check is unit-testable without a git checkout on disk.</para>
/// </summary>
public interface IGitTrackedFileProbe
{
    /// <summary>
    /// Check each of <paramref name="workspaceRelativePaths"/> against git's index and return one entry
    /// per path, keyed by the exact string given: <c>true</c> when tracked, <c>false</c> when
    /// known-untracked, <c>null</c> when NOT KNOWN (git absent, the command failed, or the answer could
    /// not otherwise be obtained). <c>null</c> must never be read as <c>false</c> — silence is not proof
    /// of anything, least of all "untracked".
    /// </summary>
    IReadOnlyDictionary<string, bool?> AreTracked(IReadOnlyList<string> workspaceRelativePaths);
}

/// <summary>An <see cref="IGitTrackedFileProbe"/> that knows nothing — the no-git default.</summary>
public sealed class NullGitTrackedFileProbe : IGitTrackedFileProbe
{
    /// <summary>The shared instance; the probe is stateless.</summary>
    public static readonly NullGitTrackedFileProbe Instance = new();

    /// <inheritdoc />
    public IReadOnlyDictionary<string, bool?> AreTracked(IReadOnlyList<string> workspaceRelativePaths) =>
        workspaceRelativePaths.ToDictionary(p => p, static _ => (bool?)null, StringComparer.Ordinal);
}
