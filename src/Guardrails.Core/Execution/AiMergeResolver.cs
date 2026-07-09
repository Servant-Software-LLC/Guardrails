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
/// <see cref="Prompts.PromptResult.IsError"/> is NEVER the verdict — only the three deterministic
/// gates certify:
///   (i)   non-degenerate: GUARDRAILS_MERGE_OUT is not empty/whitespace (an empty resolution would
///         otherwise pass gates ii/iii vacuously and silently blank the conflicted file)
///   (ii)  git diff --check — no conflict markers remain in the staged resolution
///   (iii) git status --porcelain — blast-radius: the AI touched ONLY the git-reported-conflicted files
///
/// The three-way input files + the resolution target live in a harness temp dir granted to the
/// runner's sandbox via <c>--add-dir</c> (the runner's cwd is the worktree, so the temp dir is
/// otherwise unreachable), and their absolute paths are embedded in the prompt body (SSOT §9.1).
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
        ISchedulerJournal journal,
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

            bool ok = await AttemptAsync(worktreePath, planDirectory, journal, ct).ConfigureAwait(false);
            if (ok) return true;

            // Failure path: reset worktree to pre-merge HEAD, remove untracked files.
            GitIn(worktreePath, "reset", "--hard", preMergeHead);
            GitIn(worktreePath, "clean", "-fd");
        }

        // Budget exhausted; worktree is already at preMergeHead.
        return false;
    }

    private async Task<bool> AttemptAsync(
        string worktreePath, string planDirectory, ISchedulerJournal journal, CancellationToken ct)
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

            // Sandbox reachability (defect #120-followup / SSOT §9.1): the runner's cwd is the
            // worktree and its only granted extra dir is the plan directory (--add-dir <planDir>).
            // The MERGE_* files live under tmpDir (system TEMP), OUTSIDE both — so the runner's
            // sandbox could not read base/ours/theirs nor WRITE the resolution to MERGE_OUT, leaving
            // it empty and the two deterministic gates passing vacuously. Grant tmpDir to the runner
            // via --add-dir (ClaudePromptRunner appends ExtraArgs verbatim; a fake runner ignores
            // them). tmpDir stays under system TEMP — never inside the worktree — so it never
            // pollutes git status or the merge commit.
            var settings = new PromptRunnerSettings
            {
                ExtraArgs = ["--add-dir", tmpDir],
            };

            var invocation = new PromptInvocation
            {
                // Embed the resolved ABSOLUTE paths in the prompt body so an agent that does not (or
                // cannot) read process env vars still gets the three-way inputs and the write target.
                ComposedPrompt   = BuildPrompt(conflictFile, basePath, oursPath, theirsPath, outPath),
                WorkingDirectory = worktreePath,
                PlanDirectory    = planDirectory,
                Environment      = env,
                Settings         = settings,
                Timeout          = TimeSpan.FromMinutes(30),
                StreamLogPath    = Path.Combine(tmpDir, "ai-merge-stream.jsonl"),
            };

            // PromptResult.IsError is NEVER the verdict (SSOT §9.1) — the deterministic gates below are.
            PromptResult result = await _runner.RunAsync(invocation, ct).ConfigureAwait(false);

            // Charge the merge-prompt spend to the run's cumulative cost via the shared overhead sink (SSOT
            // §7/§9.1, #314): the spend is REAL regardless of the gate verdict (pass/fail/retry), so it is
            // charged here — BEFORE the gates read GUARDRAILS_MERGE_OUT — so it BOTH counts toward the
            // maxCostUsd gate AND appears in the reported total. A null CostUsd is a no-op.
            journal.AddOverheadCost(result.CostUsd);

            if (!File.Exists(outPath)) return false;
            string mergedContent = File.ReadAllText(outPath);

            // Third deterministic gate (defect #120-followup): a degenerate resolution — an empty or
            // whitespace-only MERGE_OUT — is a FAILED attempt, never a pass. Without this an
            // unreachable-sandbox (or a no-op agent) leaves MERGE_OUT at its pre-created empty bytes;
            // overwriting the conflicted file with "" then yields no markers (gate i) and no
            // out-of-bounds write (gate ii), so both prior gates pass on nothing. Reject it here so
            // the conflict halts to needs-human instead of silently deleting the file's content.
            if (string.IsNullOrWhiteSpace(mergedContent)) return false;

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

    /// <summary>
    /// Compose the merge prompt with the resolved ABSOLUTE paths embedded in the body (not just the
    /// env-var names) so an agent that reads instructions rather than the process environment still
    /// receives the three-way inputs and the exact write target (SSOT §5.1: "the same information is
    /// embedded in the composed prompt").
    /// </summary>
    private static string BuildPrompt(
        string conflictFile, string basePath, string oursPath, string theirsPath, string outPath) =>
        $"Resolve the git merge conflict in `{conflictFile}` by combining BOTH sides' intended " +
        "changes. Preserve every edit from both sides; do NOT drop either side's contribution.\n\n" +
        "Three-way inputs (absolute paths on disk — read these):\n" +
        $"  - merge base (common ancestor): {basePath}\n" +
        $"  - ours (current plan-branch version): {oursPath}\n" +
        $"  - theirs (incoming segment version): {theirsPath}\n\n" +
        "Write the FULLY RESOLVED file content — with NO conflict markers " +
        "(`<<<<<<<`, `=======`, `>>>>>>>`) — to this absolute path:\n" +
        $"  {outPath}\n\n" +
        "(This path is also available in the GUARDRAILS_MERGE_OUT environment variable.) " +
        "Write ONLY that one file; do not modify any other file in the working tree.";

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
