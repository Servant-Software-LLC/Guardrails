using System.Diagnostics;
using System.Globalization;
using System.Text;
using Guardrails.Core.Execution;

namespace Guardrails.Core.Loading;

/// <summary>
/// The real <see cref="IPlanFolderDriftProbe"/>: asks git, via
/// <c>git rev-list --count &lt;planBranch&gt;..HEAD -- &lt;planFolder&gt;</c>, how many plan-folder commits
/// the operator's branch carries that the plan branch does not (issue #576).
///
/// <para><b>Why <c>rev-list --count</c> and not a diff.</b> The question is not "do the two trees differ"
/// — they always differ, because the plan branch also carries the run's own task commits. It is
/// specifically "are there plan-folder edits on MY branch that merging the plan branch alone would leave
/// behind", which is a reachability question about commits, and the one git answers directly.</para>
///
/// <para><b>Anchored to a supplied repository, never to the ambient current directory (#593).</b> The
/// working directory is required rather than optional here: this probe is constructed at the end of a run
/// that already knows its workspace, and inheriting the harness process's cwd is exactly the
/// process-global coupling that burned the v1.15.0 release. There is no reason to repeat it in a class
/// written after that was understood.</para>
///
/// <para><b>Every failure is NOT-KNOWN.</b> A missing git, a plan branch that does not exist (a run that
/// never used worktree mode has none), a detached HEAD, a timeout, a non-zero exit or unparseable output
/// all report <c>null</c>. The banner then says nothing about drift, which is correct: this feeds an
/// operator who is about to merge, and a wrong "you are in sync" is worse than an absent line.</para>
/// </summary>
public sealed class GitRevListDriftProbe : IPlanFolderDriftProbe
{
    /// <summary>How long git gets before the probe gives up and reports not-known.</summary>
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(15);

    private readonly IExecutableProbe _probe;
    private readonly string _workingDirectory;

    /// <summary>Probe the repository at <paramref name="workingDirectory"/>, using the real PATH lookup.</summary>
    public GitRevListDriftProbe(string workingDirectory)
        : this(new PathExecutableProbe(), workingDirectory) { }

    /// <summary>Probe with an injected PATH lookup, so availability is testable without a real git.</summary>
    public GitRevListDriftProbe(IExecutableProbe probe, string workingDirectory)
    {
        _probe = probe;
        _workingDirectory = workingDirectory;
    }

    /// <inheritdoc />
    public int? CommitsNotOnPlanBranch(string planBranch, string planFolderPath)
    {
        if (string.IsNullOrWhiteSpace(planBranch)
            || string.IsNullOrWhiteSpace(planFolderPath)
            || !_probe.Exists("git"))
        {
            return null;
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
                WorkingDirectory = _workingDirectory,
            };

            psi.ArgumentList.Add("rev-list");
            psi.ArgumentList.Add("--count");
            psi.ArgumentList.Add(planBranch + "..HEAD");
            psi.ArgumentList.Add("--");
            psi.ArgumentList.Add(planFolderPath.Replace('\\', '/'));

            using Process? process = Process.Start(psi);
            if (process is null)
            {
                return null;
            }

            string stdout = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit((int)Budget.TotalMilliseconds))
            {
                TryKill(process);
                return null;
            }

            // A missing plan branch exits non-zero ("unknown revision"), which is a NOT-KNOWN, not a zero.
            // A run that never used worktree mode legitimately has no plan branch, and telling that
            // operator their folder is in sync would be inventing an answer to a question git refused.
            if (process.ExitCode != 0)
            {
                return null;
            }

            return int.TryParse(stdout.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int count)
                ? count
                : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static void TryKill(Process process)
    {
        try { process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { /* already gone */ }
        catch (System.ComponentModel.Win32Exception) { /* cannot signal it; nothing more to do */ }
    }
}
