using System.ComponentModel;
using System.Diagnostics;

namespace Guardrails.Core.Execution;

/// <summary>
/// The ONE git runner behind the write-scope machinery (issue #816 review): <see cref="SegmentStaging.StageAll"/>,
/// <see cref="WriteScopeCheck"/> and <see cref="ScopeDiffBase"/> all route through it, so three properties hold at
/// every call site instead of at whichever one remembered them.
/// <list type="bullet">
///   <item><b>No pipe deadlock.</b> stdout and stderr are drained CONCURRENTLY. Reading one stream to its end
///     before the other deadlocks as soon as git fills the other pipe — measured: under Git for Windows' system
///     <c>core.autocrlf=true</c>, <c>git add</c> prints a ~110-byte "LF will be replaced by CRLF" warning per file,
///     and ~35 files filled the stderr pipe while the harness sat reading stdout, forever.</item>
///   <item><b>Bounded.</b> Every call has a timeout and honours a cancellation token; either kills the process tree
///     and throws <see cref="InvalidOperationException"/>, which every caller already treats as a git failure. A
///     hung git can never silently wedge a run.</item>
///   <item><b>Quiet and literal.</b> <c>core.safecrlf=false</c> suppresses the per-file line-ending warnings at the
///     source, and <c>core.quotePath=false</c> keeps a non-ASCII path from being C-quoted in any listing.</item>
/// </list>
/// <para><b>Normal mode only.</b> The repository's own conversion rules (<c>core.autocrlf</c>,
/// <c>.gitattributes</c>) apply to every call, in both modes, exactly as they do to the operator's own git. A
/// "raw bytes" mode for the serial snapshot was tried and REMOVED (#816 second review): seeding the private index
/// from the real one left normalised blobs for every stat-clean file, so the snapshot mixed normalised and raw
/// blobs — a revert then silently de-CRLF'd tracked files and an identical-bytes re-save read as a change.
/// Byte-exactness for files git does not track is handled by <see cref="ScopeDiffBase"/> instead.</para>
/// </summary>
internal static class ScopeGit
{
    /// <summary>The bound on any one git call. Generous: a first snapshot hashes every untracked file.</summary>
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Run git and return its stdout, FAILING CLOSED on a non-zero exit, a timeout or a cancel (all throw
    /// <see cref="InvalidOperationException"/>). <paramref name="indexFile"/> non-null selects a private
    /// <c>GIT_INDEX_FILE</c>.
    /// </summary>
    public static string Run(
        string workingDir, string? indexFile, IReadOnlyList<string> args,
        CancellationToken cancellationToken = default, TimeSpan? timeout = null, string? standardInput = null)
    {
        (int exit, byte[] stdout, string stderr) = RunCore(workingDir, indexFile, args, cancellationToken, timeout, standardInput);
        if (exit != 0)
        {
            throw new InvalidOperationException(
                $"git {string.Join(" ", args)} (in {workingDir}) exited {exit}: {stderr.Trim()}");
        }

        return ChildProcessEncoding.Utf8NoBom.GetString(stdout);
    }

    /// <summary>
    /// <see cref="Run"/>, returning stdout as BYTES — for <c>cat-file blob</c>, whose output is file content and
    /// must reach the disk unchanged.
    /// </summary>
    public static byte[] RunBytes(
        string workingDir, string? indexFile, IReadOnlyList<string> args, CancellationToken cancellationToken = default)
    {
        (int exit, byte[] stdout, string stderr) = RunCore(workingDir, indexFile, args, cancellationToken, null, null);
        if (exit != 0)
        {
            throw new InvalidOperationException(
                $"git {string.Join(" ", args)} (in {workingDir}) exited {exit}: {stderr.Trim()}");
        }

        return stdout;
    }

    private static (int ExitCode, byte[] StandardOutput, string StandardError) RunCore(
        string workingDir, string? indexFile, IReadOnlyList<string> args,
        CancellationToken cancellationToken, TimeSpan? timeout, string? standardInput)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = standardInput is not null,
            UseShellExecute = false,
            // Issue #457: git's stderr (and any path it prints) is UTF-8, never the console code page.
            StandardErrorEncoding = ChildProcessEncoding.Utf8NoBom
        };
        if (standardInput is not null)
        {
            psi.StandardInputEncoding = ChildProcessEncoding.Utf8NoBom;
        }

        foreach (string arg in ConfigArguments())
        {
            psi.ArgumentList.Add(arg);
        }

        foreach (string arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        if (indexFile is not null)
        {
            psi.Environment["GIT_INDEX_FILE"] = indexFile;
        }

        Process proc;
        try
        {
            proc = Process.Start(psi)!;
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException($"git could not be started in {workingDir}: {ex.Message}", ex);
        }

        using (proc)
        {
            // BOTH streams drained concurrently — the deadlock this class exists to prevent.
            var stdoutBuffer = new MemoryStream();
            Task stdout = proc.StandardOutput.BaseStream.CopyToAsync(stdoutBuffer, CancellationToken.None);
            Task<string> stderr = proc.StandardError.ReadToEndAsync(CancellationToken.None);
            if (standardInput is not null)
            {
                proc.StandardInput.Write(standardInput);
                proc.StandardInput.Close();
            }

            using var bound = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            bound.CancelAfter(timeout ?? DefaultTimeout);
            try
            {
                proc.WaitForExitAsync(bound.Token).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                try
                {
                    proc.Kill(entireProcessTree: true);
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
                {
                    // Already gone.
                }

                string why = cancellationToken.IsCancellationRequested
                    ? "was cancelled"
                    : $"did not finish within {(timeout ?? DefaultTimeout).TotalSeconds:0}s and was killed";
                throw new InvalidOperationException($"git {string.Join(" ", args)} (in {workingDir}) {why}");
            }

            stdout.GetAwaiter().GetResult();
            return (proc.ExitCode, stdoutBuffer.ToArray(), stderr.GetAwaiter().GetResult());
        }
    }

    /// <summary>The <c>-c</c> options every call carries: quiet about line endings, literal about paths.</summary>
    internal static IReadOnlyList<string> ConfigArguments() =>
        ["-c", "core.safecrlf=false", "-c", "core.quotePath=false"];
}
