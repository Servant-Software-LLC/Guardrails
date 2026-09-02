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
/// same anchor "workspace" means elsewhere in this validator (<c>ValidateWorkspaceIsGitRoot</c>) —
/// regardless of the <c>guardrails</c> process's own current directory. <c>:(literal)</c> also turns off
/// glob interpretation, so a path containing <c>*</c>, <c>?</c>, or <c>[...]</c> is matched exactly.</para>
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

    /// <summary>Probe using the real PATH lookup for git availability.</summary>
    public GitLsFilesProbe() : this(new PathExecutableProbe()) { }

    /// <summary>Probe with an injected PATH lookup, so availability is testable without a real git on PATH.</summary>
    public GitLsFilesProbe(IExecutableProbe probe) => _probe = probe;

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
