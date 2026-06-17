namespace Guardrails.Core.Execution;

/// <summary>
/// The single OS-correct definition of "does this path stay inside the workspace?" — shared by
/// <see cref="Loading.PlanValidator"/> (the GR2013 <c>captureHashes</c> check) and
/// <see cref="CapturedFileStore"/> (the FIX B / issue #51 defense-in-depth assert before any
/// workspace write). Keeping ONE implementation means the harness's only workspace-writing code
/// uses exactly the boundary the validator enforced, so the two can never drift.
/// </summary>
internal static class WorkspaceContainment
{
    /// <summary>
    /// True when <paramref name="relativeEntry"/> is NOT safely contained by
    /// <paramref name="workspace"/>: it is rooted (absolute or drive-rooted such as <c>/etc</c> or
    /// <c>C:\x</c>), or its normalized resolution against the workspace leaves the workspace root.
    /// Comparison is on a directory boundary (<c>workspaceRoot + DirectorySeparatorChar</c>) so a
    /// sibling like <c>workspace-evil</c> never counts as inside <c>workspace</c>. The workspace
    /// root itself (e.g. the entry resolves to <c>.</c>) does not escape — it is contained.
    /// </summary>
    public static bool Escapes(string workspace, string relativeEntry)
    {
        // A rooted entry ignores the workspace base entirely under Path.Combine — reject outright.
        if (Path.IsPathRooted(relativeEntry))
        {
            return true;
        }

        string workspaceRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace));
        string resolved = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(Path.Combine(workspaceRoot, relativeEntry)));

        if (string.Equals(resolved, workspaceRoot, PathComparison))
        {
            return false; // resolves to the workspace root itself — contained, not an escape.
        }

        string prefix = workspaceRoot + Path.DirectorySeparatorChar;
        return !resolved.StartsWith(prefix, PathComparison);
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
