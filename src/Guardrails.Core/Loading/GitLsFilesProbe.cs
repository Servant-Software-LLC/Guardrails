using System.Diagnostics;
using System.Text;
using Guardrails.Core.Execution;

namespace Guardrails.Core.Loading;

/// <summary>
/// The real <see cref="IGitTrackedFileProbe"/>: asks git's own index, via <c>git ls-files</c>, whether
/// each candidate path is tracked.
///
/// <para><b>One invocation per validation, not per path.</b> <c>validate</c> runs constantly and in CI,
/// and GR2060 can carry many candidate paths across a plan's guardrails — spawning a process per path
/// would make the check expensive enough to disable. The whole batch goes through a single
/// <c>git ls-files</c> call, mirroring <see cref="InterpreterScriptSyntaxProbe"/>'s one-invocation
/// design.</para>
///
/// <para><b>Anchored to the repository's top level, not to wherever the process happens to be running
/// from.</b> Every path is queried with the <c>:(top,literal)</c> pathspec magic and the result is
/// requested with <c>--full-name</c>, so a path is matched and reported relative to the repo root — the
/// same anchor "workspace" means elsewhere in this validator (<c>ValidateWorkspaceIsGitRoot</c>).
/// <c>:(literal)</c> also turns off glob interpretation, so a path containing <c>*</c>, <c>?</c>, or
/// <c>[...]</c> is matched exactly.</para>
///
/// <para><b>Which REPOSITORY is a separate question from which PATHS, and this doc used to conflate them
/// (#593).</b> The pathspec above is anchored to the repository top level regardless of where the process
/// runs from — but WHICH repository the child discovers came from the ambient environment, because no
/// working directory was ever set. The <c>workingDirectory</c> constructor parameter is how a caller says
/// which repo; leaving it null keeps the ambient behaviour production has always had.</para>
///
/// <para><b>A missing or failing git is NOT-KNOWN for the whole batch, never "untracked".</b> If
/// <c>git</c> is absent, refuses to run outside a repository, times out, or the process cannot even be
/// started, this probe reports nothing rather than guessing — <c>validate</c> must stay runnable outside
/// a git checkout, and the one thing worse than an unanswered question is a wrong answer that looks like
/// one (<see cref="IGitTrackedFileProbe"/>'s silence-is-not-proof contract).</para>
/// </summary>
public sealed class GitLsFilesProbe : IGitTrackedFileProbe
{
    /// <summary>How long the whole batch gets before the probe gives up and reports not-known for everything.</summary>
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    private readonly IExecutableProbe _probe;
    private readonly string? _workingDirectory;

    /// <summary>Probe using the real PATH lookup for git availability.</summary>
    public GitLsFilesProbe() : this(new PathExecutableProbe()) { }

    /// <summary>
    /// Probe with an injected PATH lookup, so availability is testable without a real git on PATH, and an
    /// OPTIONAL repository to run in (#593).
    /// </summary>
    ///
    /// <param name="probe">The PATH lookup used to decide whether <c>git</c> is available at all.</param>
    /// <param name="workingDirectory">
    /// The directory the <c>git</c> child runs in — i.e. WHICH REPOSITORY it discovers. <c>null</c> keeps
    /// the shipped behaviour exactly: the child inherits the harness process's own current directory, which
    /// is what <c>PlanValidator</c> has always relied on and what <c>ProducerCoverageCorpusTests</c> pins
    /// GR2060 against.
    ///
    /// <para>
    /// <b>Why this parameter had to exist.</b> The class doc above claims the probe is anchored to the
    /// repository top level "regardless of the process's own current directory". That is true of the
    /// PATHSPEC (<c>:(top,literal)</c> + <c>--full-name</c>) and was NOT true of repository DISCOVERY: the
    /// child was started with no working directory, so it found its repo from the ambient environment.
    /// </para>
    ///
    /// <para>
    /// With no way for a caller to say <i>which repo</i>, the one test that needed a different one had to
    /// set <c>GIT_DIR</c>/<c>GIT_WORK_TREE</c> with <c>Environment.SetEnvironmentVariable</c> — a
    /// WHOLE-PROCESS mutation, guarded by a lock private to that one class. xUnit runs collections in
    /// parallel, so any git child started elsewhere inside that window inherited the pointer. That is not
    /// theoretical: it burned the v1.15.0 release, where a merge test on <c>windows-latest</c> failed with
    /// <c>fatal: Could not parse object</c> because git was resolving a real sha against another class's
    /// temp repo. <c>publish</c> was correctly skipped, and the version is permanently spent on a defect
    /// that had nothing to do with the release.
    /// </para>
    ///
    /// <para>
    /// PR #592's stopgap — putting the five git-spawning classes in one non-parallel collection — closed
    /// the window by MEMBERSHIP, which decays the moment a sixth class is added and does not join. This
    /// parameter deletes the process-global mutation instead of scheduling around it.
    /// </para>
    /// </param>
    public GitLsFilesProbe(IExecutableProbe probe, string? workingDirectory = null)
    {
        _probe = probe;
        _workingDirectory = workingDirectory;
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, bool?> AreTracked(IReadOnlyList<string> workspaceRelativePaths)
    {
        var result = new Dictionary<string, bool?>(StringComparer.Ordinal);
        foreach (string path in workspaceRelativePaths)
        {
            result[path] = null; // not-known until git proves otherwise
        }

        if (workspaceRelativePaths.Count == 0 || !_probe.Exists("git"))
        {
            return result;
        }

        try
        {
            var psi = new ProcessStartInfo("git")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            };

            // #593: WHICH repository. Left unset when no directory was supplied, so production keeps the
            // exact ambient-cwd behaviour PlanValidator has always had and ProducerCoverageCorpusTests
            // pins GR2060 against — this parameter adds a way to ask a different repo, it does not change
            // the answer for anyone who does not.
            if (_workingDirectory is not null)
            {
                psi.WorkingDirectory = _workingDirectory;
            }

            psi.ArgumentList.Add("ls-files");
            psi.ArgumentList.Add("-z");
            psi.ArgumentList.Add("--full-name");
            psi.ArgumentList.Add("--");
            foreach (string path in workspaceRelativePaths)
            {
                psi.ArgumentList.Add(":(top,literal)" + path.Replace('\\', '/'));
            }

            using Process? process = Process.Start(psi);
            if (process is null)
            {
                return result;
            }

            string stdout = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit((int)Budget.TotalMilliseconds))
            {
                TryKill(process);
                return result;
            }

            if (process.ExitCode != 0)
            {
                // Not a git repo, git not usable here, or a bad invocation — not-known, not "untracked".
                return result;
            }

            var tracked = new HashSet<string>(
                stdout.Split('\0', StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);

            foreach (string path in workspaceRelativePaths)
            {
                result[path] = tracked.Contains(path.Replace('\\', '/'));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            // git would not start, or the pipe broke — leave every entry not-known rather than guess.
        }

        return result;
    }

    private static void TryKill(Process process)
    {
        try { process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { /* already gone */ }
        catch (System.ComponentModel.Win32Exception) { /* cannot signal it; nothing more to do */ }
    }
}
