using System.Diagnostics;

namespace Guardrails.Core.Execution;

/// <summary>
/// THE ONE DEFECT THIS SAMPLE CARRIES: Drain never calls CommitPaths. It keeps its own inline
/// add-and-commit with a pathspec bolted on, so the file owns TWO commit mechanisms. Every authored
/// test passes against this — Drain commits only the staged files, CommitPaths commits only its
/// pathspec — and the next change to the trailer, the rollback or the --no-verify decision lands on
/// one of them while the other silently keeps the old behaviour. CommitPaths itself is complete here
/// (declaration, rollback, reset --hard), so the valid/invalid diff is exactly the delegation.
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

        long totalBytes = 0;
        var committedPaths = new List<string>();
        foreach (SuppliedFile file in staged)
        {
            string destination = Path.Combine(
                workspace, file.DestinationPath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file.AbsoluteStagedPath, destination, overwrite: true);
            totalBytes += new FileInfo(file.AbsoluteStagedPath).Length;
            committedPaths.Add(file.DestinationPath);
        }

        var addArgs = new List<string> { "add", "--" };
        addArgs.AddRange(committedPaths);
        GitIn(workspace, addArgs.ToArray());

        var commitArgs = new List<string>
        {
            "commit", "--no-verify", "-m", $"Supplied-By: {by}\nGuardrails-Run: {runId}", "--"
        };
        commitArgs.AddRange(committedPaths);
        GitIn(workspace, commitArgs.ToArray());
        string commitSha = GitIn(workspace, "rev-parse", "HEAD").Trim();

        string suppliedRoot = Path.Combine(planDirectory, "logs", runId, SuppliedStagingTree.SuppliedFolder);
        Directory.Delete(suppliedRoot, recursive: true);

        return new SuppliedDrainResult
        {
            CommittedPaths = committedPaths,
            TotalBytes = totalBytes,
            CommitSha = commitSha
        };
    }

    /// <summary>
    /// Commit exactly <paramref name="paths"/> with the §4 trailer, rolling back to the pre-commit HEAD
    /// on any failure. Correct in itself — but Drain above never calls it.
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
