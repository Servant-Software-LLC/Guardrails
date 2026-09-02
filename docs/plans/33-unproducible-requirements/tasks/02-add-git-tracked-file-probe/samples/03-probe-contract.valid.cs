namespace Guardrails.Core.Loading;

/// <summary>
/// Answers whether a workspace-relative path is tracked by git.
///
/// <para><b>Silence is not proof.</b> When git is unavailable or the answer cannot be obtained the
/// probe reports NOT-KNOWN, and a not-known answer must never be read as "untracked". GR2060 is an
/// ERROR-severity check: a probe that guessed "untracked" would make it fire on a correct plan, and an
/// ERROR blocks the run and the resume.</para>
/// </summary>
public interface IGitTrackedFileProbe
{
    /// <summary>True when tracked, false when known-untracked, null when NOT KNOWN.</summary>
    IReadOnlyDictionary<string, bool?> AreTracked(IReadOnlyList<string> workspaceRelativePaths);
}

/// <summary>An <see cref="IGitTrackedFileProbe"/> that knows nothing — the no-git default.</summary>
public sealed class NullGitTrackedFileProbe : IGitTrackedFileProbe
{
    /// <summary>The shared instance; the probe is stateless.</summary>
    public static readonly NullGitTrackedFileProbe Instance = new();

    /// <inheritdoc />
    public IReadOnlyDictionary<string, bool?> AreTracked(IReadOnlyList<string> workspaceRelativePaths) =>
        workspaceRelativePaths.ToDictionary(p => p, _ => (bool?)null);
}
