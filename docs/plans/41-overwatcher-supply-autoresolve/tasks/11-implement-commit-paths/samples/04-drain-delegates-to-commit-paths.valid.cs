using System.Diagnostics;

namespace Guardrails.Core.Execution;

/// <summary>
/// The CORRECT shape: ONE commit mechanism. Drain copies the staged files and then delegates to
/// CommitPaths, which owns the explicit pathspec, the trailer and the reset --hard rollback — so the
/// operator path and the overwatcher path can never drift apart.
/// </summary>
public static class SuppliedDrain
{
    public static SuppliedDrainResult Drain(string workspace, string planDirectory, string runId, string by)
    {
        IReadOnlyList<SuppliedFile> staged = SuppliedStagingTree.DrainableFiles(planDirectory, runId);
        if (staged.Count == 0)
        {
            return new SuppliedDrainResult { CommittedPaths = [], TotalBytes = 0L, CommitSha = null };
        }

        var committedPaths = new List<string>();
        foreach (SuppliedFile file in staged)
        {
            string destination = Path.Combine(
                workspace, file.DestinationPath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file.AbsoluteStagedPath, destination, overwrite: true);
            committedPaths.Add(file.DestinationPath);
        }

        SuppliedDrainResult result = CommitPaths(workspace, runId, by, committedPaths);

        string suppliedRoot = Path.Combine(planDirectory, "logs", runId, SuppliedStagingTree.SuppliedFolder);
        Directory.Delete(suppliedRoot, recursive: true);

        return result;
    }

    /// <summary>
    /// Commit exactly <paramref name="paths"/> with the §4 trailer. The explicit pathspec means nothing
    /// else left in the index rides along; on any failure the pre-commit HEAD is restored.
    /// </summary>
    public static SuppliedDrainResult CommitPaths(
        string workspace, string runId, string by, IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
        {
            return new SuppliedDrainResult { CommittedPaths = [], TotalBytes = 0L, CommitSha = null };
        }

        string preHead = GitIn(workspace, "rev-parse", "HEAD").Trim();
        try
        {
            var addArgs = new List<string> { "add", "--" };
            addArgs.AddRange(paths);
            GitIn(workspace, addArgs.ToArray());

            var commitArgs = new List<string>
            {
                "commit", "--no-verify", "-m", $"Supplied-By: {by}\nGuardrails-Run: {runId}", "--"
            };
            commitArgs.AddRange(paths);
            GitIn(workspace, commitArgs.ToArray());
        }
        catch
        {
            GitIn(workspace, "reset", "--hard", preHead);
            throw;
        }

        long totalBytes = 0;
        foreach (string path in paths)
        {
            totalBytes += new FileInfo(
                Path.Combine(workspace, path.Replace('/', Path.DirectorySeparatorChar))).Length;
        }

        return new SuppliedDrainResult
        {
            CommittedPaths = [.. paths],
            TotalBytes = totalBytes,
            CommitSha = GitIn(workspace, "rev-parse", "HEAD").Trim()
        };
    }

    private static string GitIn(string workingDir, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (string arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var proc = Process.Start(psi)!;
        string stdout = proc.StandardOutput.ReadToEnd();
        string stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        if (proc.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"git {string.Join(" ", args)} (in {workingDir}) exited {proc.ExitCode}: {stderr.Trim()}");
        }

        return stdout;
    }
}
