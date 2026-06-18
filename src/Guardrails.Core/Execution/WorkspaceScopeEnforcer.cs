using System.Diagnostics;
using System.Security.Cryptography;

namespace Guardrails.Core.Execution;

/// <summary>
/// Pre/post workspace snapshot, out-of-scope-write detection (M4), and revert (M5).
/// This type owns the snapshot/diff/revert logic; <see cref="TaskExecutor"/> is the orchestrator.
/// </summary>
internal static class WorkspaceScopeEnforcer
{
    /// <summary>
    /// M4 snapshot (detect-only path): hashes all non-ignored workspace files.
    /// </summary>
    public static WorkspaceScopeSnapshot Snapshot(
        string workspace,
        WriteScope writeScope,
        IReadOnlyList<string> enforcementIgnore)
    {
        var normalizedIgnore = NormalizeIgnorePatterns(enforcementIgnore);
        var hashes = CollectHashes(workspace, normalizedIgnore);
        return new WorkspaceScopeSnapshot(hashes, normalizedIgnore);
    }

    /// <summary>
    /// M5 snapshot (revert path): hashes all non-ignored workspace files AND saves out-of-scope
    /// file bytes to <c>state/scope-baseline/</c> so untracked files can be restored on revert.
    /// </summary>
    public static WorkspaceScopeSnapshot Snapshot(
        string workspace,
        WriteScope writeScope,
        IReadOnlyList<string> enforcementIgnore,
        string planDir)
    {
        var normalizedIgnore = NormalizeIgnorePatterns(enforcementIgnore);
        var hashes = CollectHashes(workspace, normalizedIgnore);
        SaveOutOfScopeBytesToBaseline(workspace, writeScope, hashes, planDir);
        return new WorkspaceScopeSnapshot(hashes, normalizedIgnore);
    }

    public static ScopeViolationResult DetectOutOfScopeWrites(
        string workspace,
        WriteScope writeScope,
        WorkspaceScopeSnapshot preImage)
    {
        var current = CollectHashes(workspace, preImage.NormalizedIgnorePatterns);
        var violations = new List<string>();

        // Deletions: in pre-image, gone now, and not in scope
        foreach (var (relPath, _) in preImage.Hashes)
        {
            if (!current.ContainsKey(relPath) && !writeScope.IsInScope(relPath))
                violations.Add(relPath);
        }

        // Modifications and new files
        foreach (var (relPath, currentHash) in current)
        {
            if (preImage.Hashes.TryGetValue(relPath, out string? preHash))
            {
                if (!string.Equals(preHash, currentHash, StringComparison.Ordinal) &&
                    !writeScope.IsInScope(relPath))
                    violations.Add(relPath);
            }
            else if (!writeScope.IsInScope(relPath))
            {
                violations.Add(relPath);
            }
        }

        return new ScopeViolationResult(violations.Count > 0, violations);
    }

    /// <summary>
    /// M5 revert: detects out-of-scope writes and reverts them, then returns the violation result.
    /// Created files are deleted; modified/deleted files are restored via git checkout (tracked) or
    /// the <c>state/scope-baseline/</c> snapshot (untracked). In-scope changes are left untouched.
    /// </summary>
    public static ScopeViolationResult RevertOutOfScope(
        string workspace,
        WriteScope writeScope,
        WorkspaceScopeSnapshot preImage,
        string planDir)
    {
        string root = Path.GetFullPath(workspace);
        var current = CollectHashes(workspace, preImage.NormalizedIgnorePatterns);
        var violations = new List<string>();

        // Deletions: in pre-image, gone now, and not in scope
        foreach (var (relPath, _) in preImage.Hashes)
        {
            if (!current.ContainsKey(relPath) && !writeScope.IsInScope(relPath))
                violations.Add(relPath);
        }

        // Modifications and new files
        foreach (var (relPath, currentHash) in current)
        {
            if (preImage.Hashes.TryGetValue(relPath, out string? preHash))
            {
                if (!string.Equals(preHash, currentHash, StringComparison.Ordinal) &&
                    !writeScope.IsInScope(relPath))
                    violations.Add(relPath);
            }
            else if (!writeScope.IsInScope(relPath))
            {
                violations.Add(relPath);
            }
        }

        // Revert each violation
        foreach (string relPath in violations)
        {
            string nativePath = relPath.Replace('/', Path.DirectorySeparatorChar);
            if (WorkspaceContainment.Escapes(workspace, nativePath))
                continue;

            string fullPath = Path.Combine(root, nativePath);

            if (!preImage.Hashes.ContainsKey(relPath))
            {
                // Created out-of-scope: delete it
                if (File.Exists(fullPath))
                    File.Delete(fullPath);
            }
            else
            {
                // Modified or deleted: git checkout first (tracked), then scope-baseline (untracked)
                if (!TryRestoreFromGit(root, relPath))
                    RestoreFromBaseline(relPath, fullPath, planDir);
            }
        }

        return new ScopeViolationResult(violations.Count > 0, violations);
    }

    private static void SaveOutOfScopeBytesToBaseline(
        string workspace,
        WriteScope writeScope,
        IReadOnlyDictionary<string, string> hashes,
        string planDir)
    {
        string baselineDir = Path.Combine(planDir, "state", "scope-baseline");
        string root = Path.GetFullPath(workspace);

        foreach (string relPath in hashes.Keys)
        {
            if (writeScope.IsInScope(relPath))
                continue;

            string nativePath = relPath.Replace('/', Path.DirectorySeparatorChar);
            string srcPath = Path.Combine(root, nativePath);
            string dstPath = Path.Combine(baselineDir, nativePath);

            Directory.CreateDirectory(Path.GetDirectoryName(dstPath)!);
            File.Copy(srcPath, dstPath, overwrite: true);
        }
    }

    private static bool TryRestoreFromGit(string workspace, string relPath)
    {
        try
        {
            var psi = new ProcessStartInfo("git")
            {
                WorkingDirectory = workspace,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            psi.ArgumentList.Add("checkout");
            psi.ArgumentList.Add("--");
            psi.ArgumentList.Add(relPath);

            using var proc = Process.Start(psi)!;
            proc.WaitForExit();
            if (proc.ExitCode != 0)
                return false;

            // On Windows, git creates pack files and loose objects as read-only (0444).
            // Normalize them to writable so workspace cleanup (Directory.Delete recursive)
            // can proceed — git correctness is unaffected since object content is immutable.
            if (OperatingSystem.IsWindows())
                NormalizeGitObjectPermissions(workspace);

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void NormalizeGitObjectPermissions(string workspace)
    {
        string gitDir = Path.Combine(workspace, ".git");
        if (!Directory.Exists(gitDir))
            return;
        foreach (string file in Directory.EnumerateFiles(gitDir, "*", SearchOption.AllDirectories))
        {
            try { File.SetAttributes(file, FileAttributes.Normal); } catch { }
        }
    }

    private static void RestoreFromBaseline(string relPath, string fullPath, string planDir)
    {
        string nativePath = relPath.Replace('/', Path.DirectorySeparatorChar);
        string baselinePath = Path.Combine(planDir, "state", "scope-baseline", nativePath);

        if (File.Exists(baselinePath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.Copy(baselinePath, fullPath, overwrite: true);
        }
    }

    private static IReadOnlyDictionary<string, string> CollectHashes(
        string workspace,
        IReadOnlyList<string> normalizedIgnore)
    {
        string root = Path.GetFullPath(workspace);
        if (!Directory.Exists(root))
            return new Dictionary<string, string>();

        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            string relPath = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (!IsIgnored(relPath, normalizedIgnore))
                hashes[relPath] = ComputeHash(file);
        }
        return hashes;
    }

    private static string ComputeHash(string path)
        => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static bool IsIgnored(string relPath, IReadOnlyList<string> normalizedPatterns)
    {
        string[] pathSegs = relPath.Split('/');
        foreach (string pattern in normalizedPatterns)
        {
            if (MatchGlob(pattern.Split('/'), 0, pathSegs, 0))
                return true;
        }
        return false;
    }

    private static IReadOnlyList<string> NormalizeIgnorePatterns(IReadOnlyList<string> patterns)
    {
        var result = new string[patterns.Count];
        for (int i = 0; i < patterns.Count; i++)
        {
            string p = patterns[i].TrimEnd('/');
            result[i] = p.Contains('*') ? p : p + "/**";
        }
        return result;
    }

    private static bool MatchGlob(string[] pattern, int pi, string[] path, int si)
    {
        if (pi == pattern.Length && si == path.Length) return true;
        if (pi == pattern.Length) return false;
        string seg = pattern[pi];
        if (seg == "**")
        {
            for (int n = si; n <= path.Length; n++)
                if (MatchGlob(pattern, pi + 1, path, n))
                    return true;
            return false;
        }
        if (si == path.Length) return false;
        if (seg.Contains('*')) return MatchGlob(pattern, pi + 1, path, si + 1);
        if (!string.Equals(seg, path[si], PathComparison)) return false;
        return MatchGlob(pattern, pi + 1, path, si + 1);
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}

/// <summary>
/// Opaque pre-action workspace snapshot: file hashes (relPath → SHA-256 hex) for all
/// non-ignored files, plus the normalized ignore patterns for the post-action diff.
/// </summary>
internal sealed class WorkspaceScopeSnapshot
{
    internal IReadOnlyDictionary<string, string> Hashes { get; }
    internal IReadOnlyList<string> NormalizedIgnorePatterns { get; }

    internal WorkspaceScopeSnapshot(
        IReadOnlyDictionary<string, string> hashes,
        IReadOnlyList<string> normalizedIgnorePatterns)
    {
        Hashes = hashes;
        NormalizedIgnorePatterns = normalizedIgnorePatterns;
    }
}

/// <summary>Result of <see cref="WorkspaceScopeEnforcer.DetectOutOfScopeWrites"/> or <see cref="WorkspaceScopeEnforcer.RevertOutOfScope"/>.</summary>
internal sealed record ScopeViolationResult(bool HasOutOfScopeWrites, IReadOnlyList<string> ViolatingPaths);
