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
