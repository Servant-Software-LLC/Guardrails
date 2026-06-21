using System.Diagnostics;
using Guardrails.Core.Model;
using Guardrails.Core.Prompts;

namespace Guardrails.Core.Execution;

/// <summary>
/// Core resolver for AI-assisted git merge conflicts (plan 08 §9.1).
/// Wraps an <see cref="IPromptRunner"/> and presents each conflicted file via the three-way
/// on-disk env contract: GUARDRAILS_MERGE_BASE / GUARDRAILS_MERGE_OURS / GUARDRAILS_MERGE_THEIRS
/// (inputs) and GUARDRAILS_MERGE_OUT (the AI writes the resolution; the harness reads it).
///
/// <see cref="Prompts.PromptResult.IsError"/> is NEVER the verdict — only the two deterministic
/// gates certify:
///   (i)  git diff --check — no conflict markers remain in the staged resolution
///   (ii) git status --porcelain — blast-radius: the AI touched ONLY the git-reported-conflicted files
///
/// 1-retry budget (2 total attempts). On any failure the integration worktree is reset to the
/// pre-merge HEAD before returning false.
/// </summary>
internal sealed class AiMergeResolver
{
    private readonly IPromptRunner _runner;
    private const int MaxAttempts = 2;

    private static readonly HashSet<string> ConflictCodes = new(StringComparer.Ordinal)
        { "AA", "UU", "DD", "AU", "UA", "DU", "UD" };

    internal AiMergeResolver(IPromptRunner runner) => _runner = runner;

    internal async Task<bool> TryResolveAsync(
        string worktreePath,
        string segmentBranch,
        string planDirectory,
        CancellationToken ct)
    {
        string preMergeHead = GitIn(worktreePath, "rev-parse", "HEAD").Trim();

        for (int attempt = 0; attempt < MaxAttempts; attempt++)
        {
            if (attempt > 0)
            {
                // Re-establish the merge conflict for the retry after the previous reset.
                TryGitIn(worktreePath, "merge", "--no-commit", "--no-ff", segmentBranch);
            }

            bool ok = await AttemptAsync(worktreePath, planDirectory, ct).ConfigureAwait(false);
            if (ok) return true;

            // Failure path: reset worktree to pre-merge HEAD, remove untracked files.
            GitIn(worktreePath, "reset", "--hard", preMergeHead);
            GitIn(worktreePath, "clean", "-fd");
        }

        // Budget exhausted; worktree is already at preMergeHead.
        return false;
    }

    private async Task<bool> AttemptAsync(string worktreePath, string planDirectory, CancellationToken ct)
    {
        // Gate (ii) baseline: git status --porcelain before the runner so we can compare afterward.
        string statusBefore = GitIn(worktreePath, "status", "--porcelain");
        List<string> conflictedFiles = ParseConflictedFiles(statusBefore);
        if (conflictedFiles.Count == 0) return false;

        HashSet<string> preRunnerFiles = ParseStatusFiles(statusBefore);

        string tmpDir = Path.Combine(Path.GetTempPath(), "gr-aimerge-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpDir);
        try
        {
            string conflictFile = conflictedFiles[0];

            string basePath   = Path.Combine(tmpDir, "MERGE_BASE");
            string oursPath   = Path.Combine(tmpDir, "MERGE_OURS");
            string theirsPath = Path.Combine(tmpDir, "MERGE_THEIRS");
            // GUARDRAILS_MERGE_OUT: the only byte channel — AI writes the resolution here;
            // harness reads it. PromptResult carries no bytes (SSOT §9.1).
            string outPath    = Path.Combine(tmpDir, "MERGE_OUT");

            var (mergeBaseSha, _) = TryGitIn(worktreePath, "merge-base", "HEAD", "MERGE_HEAD");
            mergeBaseSha = mergeBaseSha.Trim();

            File.WriteAllText(basePath, !string.IsNullOrEmpty(mergeBaseSha)
                ? TryGitShow(worktreePath, $"{mergeBaseSha}:{conflictFile}")
                : "");
            File.WriteAllText(oursPath,   TryGitShow(worktreePath, $"HEAD:{conflictFile}"));
            File.WriteAllText(theirsPath, TryGitShow(worktreePath, $"MERGE_HEAD:{conflictFile}"));
            File.WriteAllText(outPath, "");

            var env = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["GUARDRAILS_MERGE_BASE"]   = basePath,
                ["GUARDRAILS_MERGE_OURS"]   = oursPath,
                ["GUARDRAILS_MERGE_THEIRS"] = theirsPath,
                ["GUARDRAILS_MERGE_OUT"]    = outPath,
            };

            var invocation = new PromptInvocation
            {
                ComposedPrompt   = $"Resolve merge conflict in {conflictFile}. " +
                                   "Write the fully resolved file content (no conflict markers) " +
                                   "to the path in GUARDRAILS_MERGE_OUT.",
                WorkingDirectory = worktreePath,
                PlanDirectory    = planDirectory,
                Environment      = env,
                Settings         = new PromptRunnerSettings(),
                Timeout          = TimeSpan.FromMinutes(30),
                StreamLogPath    = Path.Combine(tmpDir, "ai-merge-stream.jsonl"),
            };

            // PromptResult.IsError is NEVER the verdict (SSOT §9.1) — ignore the result entirely.
            await _runner.RunAsync(invocation, ct).ConfigureAwait(false);

            if (!File.Exists(outPath)) return false;
            string mergedContent = File.ReadAllText(outPath);

            // Apply AI's resolution: overwrite the conflict file, then stage it.
            string fullPath = Path.Combine(
                worktreePath, conflictFile.Replace('/', Path.DirectorySeparatorChar));
            File.WriteAllText(fullPath, mergedContent);
            GitIn(worktreePath, "add", conflictFile);

            // Gate (i): git diff --check — detect conflict markers in the staged AI resolution.
            // Runs as: git diff --cached --check (staged vs HEAD).
            var (_, markerExitCode) = TryGitIn(worktreePath, "diff", "--cached", "--check");
            if (markerExitCode != 0) return false;

            // Gate (ii): git status --porcelain — blast-radius check.
            // The AI must have written ONLY to the git-reported-conflicted files; any file that
            // appears in the post-runner status but was absent before the runner is an out-of-bounds
            // write that violates the blast-radius invariant (SSOT §9.1).
            string statusAfter = GitIn(worktreePath, "status", "--porcelain");
            HashSet<string> postRunnerFiles = ParseStatusFiles(statusAfter);
            if (postRunnerFiles.Any(f => !preRunnerFiles.Contains(f))) return false;

            return true;
        }
        finally
        {
            try { Directory.Delete(tmpDir, recursive: true); } catch { /* best-effort teardown */ }
        }
    }

    private static List<string> ParseConflictedFiles(string porcelain)
    {
        var result = new List<string>();
        foreach (string line in porcelain.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length < 4) continue;
            if (ConflictCodes.Contains(line[..2]))
                result.Add(line[3..].Trim());
        }
        return result;
    }

    private static HashSet<string> ParseStatusFiles(string porcelain)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in porcelain.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length < 4) continue;
            files.Add(line[3..].Trim());
        }
        return files;
    }

    private static string TryGitShow(string workingDir, string gitRef)
    {
        var (stdout, exitCode) = TryGitIn(workingDir, "show", gitRef);
        return exitCode == 0 ? stdout : "";
    }

    private static string GitIn(string workingDir, params string[] args)
    {
        var (stdout, exitCode, stderr) = RunGit(workingDir, args);
        if (exitCode != 0)
            throw new InvalidOperationException(
                $"git {string.Join(" ", args)} (in {workingDir}) exited {exitCode}: {stderr.Trim()}");
        return stdout;
    }

    private static (string stdout, int exitCode) TryGitIn(string workingDir, params string[] args)
    {
        var (stdout, exitCode, _) = RunGit(workingDir, args);
        return (stdout, exitCode);
    }

    private static (string stdout, int exitCode, string stderr) RunGit(string workingDir, string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute = false
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        using var proc = Process.Start(psi)!;
        string stdout = proc.StandardOutput.ReadToEnd();
        string stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        return (stdout, proc.ExitCode, stderr);
    }
}
