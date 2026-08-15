using System.Diagnostics;
using Guardrails.Core.Execution;

namespace Guardrails.Core.Io;

/// <summary>
/// Two cheap, best-effort questions about a directory's relationship to git (issue #461): does the
/// repository TRACK anything under it, and does anything under it have UNCOMMITTED changes.
/// <para>
/// The distinction matters because the harness copies reconstructable payload (the bundled skills)
/// into directories a user may ALSO author in. A directory git tracks is <b>source</b>: overwriting
/// it is a different operation from installing into an empty target, and a dirty tracked directory
/// holds work that no <c>git checkout</c> can bring back. Both callers — the <c>--version</c> drift
/// report (a tracked skills root is source, not a stale install) and <c>skills install --force</c>
/// (refuse to clobber dirty tracked source) — need exactly these two facts.
/// </para>
/// <para>
/// <b>Failure direction: "no".</b> git missing, off PATH, the path outside any repository, a
/// permission fault — every one of them answers <c>false</c>. The overwhelmingly common destination
/// (an install directory in no repository at all) is indistinguishable from an unanswerable probe,
/// and the pre-#461 behaviour is what <c>false</c> preserves: the version warning still fires and
/// <c>--force</c> still installs. A probe failure therefore never breaks a working install; it only
/// declines to add the new protection.
/// </para>
/// </summary>
public static class GitWorkingTree
{
    /// <summary>
    /// True when the repository containing <paramref name="path"/> tracks at least one file at or
    /// under it — i.e. the directory is authored SOURCE, under version control. False when the path
    /// does not exist, lies outside a repository, holds only untracked/ignored files, or git cannot
    /// be run at all.
    /// </summary>
    public static bool TracksAnyFileUnder(string path) =>
        !string.IsNullOrWhiteSpace(RunGit(path, "ls-files", "--", "."));

    /// <summary>
    /// True when git reports ANY pending change at or under <paramref name="path"/> — modified,
    /// staged, deleted, or untracked-but-not-ignored (<c>git status --porcelain</c> reports all of
    /// them). Untracked files count deliberately: a <c>--force</c> install deletes the whole
    /// destination skill folder, so a never-committed file inside it is the one thing git could not
    /// restore. Scoped by pathspec, so unrelated dirt elsewhere in the repository is not this
    /// directory's problem.
    /// </summary>
    public static bool HasLocalChangesUnder(string path) =>
        !string.IsNullOrWhiteSpace(RunGit(path, "status", "--porcelain", "--", "."));

    /// <summary>
    /// Run <c>git</c> with <paramref name="workingDir"/> as its cwd and return stdout, or
    /// <c>null</c> on ANY failure (missing directory, git absent, non-zero exit — e.g. exit 128 for
    /// "not a git repository"). Never throws. Streams are UTF-8-pinned like every other git
    /// invocation in the harness (issue #457) — <c>ls-files</c> output is repository paths, which
    /// can be non-ASCII.
    /// </summary>
    private static string? RunGit(string workingDir, params string[] args)
    {
        if (string.IsNullOrWhiteSpace(workingDir) || !Directory.Exists(workingDir))
        {
            return null;
        }

        try
        {
            var psi = new ProcessStartInfo("git")
            {
                WorkingDirectory = workingDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                StandardOutputEncoding = ChildProcessEncoding.Utf8NoBom,
                StandardErrorEncoding = ChildProcessEncoding.Utf8NoBom
            };
            foreach (string arg in args)
            {
                psi.ArgumentList.Add(arg);
            }

            using Process? proc = Process.Start(psi);
            if (proc is null)
            {
                return null;
            }

            string stdout = proc.StandardOutput.ReadToEnd();
            proc.StandardError.ReadToEnd();
            proc.WaitForExit();
            return proc.ExitCode == 0 ? stdout : null;
        }
        catch (Exception ex) when (
            ex is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException
                or InvalidOperationException or PlatformNotSupportedException)
        {
            return null; // git unavailable / unusable ⇒ "no" (see the type remarks)
        }
    }
}
