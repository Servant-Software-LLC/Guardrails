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
/// <para><b>Raw-bytes mode</b> — used with a PRIVATE index (the serial snapshot, <see cref="ScopeDiffBase"/>) and
/// ONLY there: <c>core.autocrlf=false</c> plus <c>--attr-source</c> pointed at the empty tree, so neither the
/// config nor a <c>.gitattributes</c> <c>text</c>/<c>eol</c>/<c>filter</c> attribute converts anything. What is
/// hashed is exactly the bytes on disk, and what a revert writes back is exactly the bytes that were there. Never
/// used for a segment's own index: those blobs are committed, and the repository's own conversion rules must keep
/// applying to them. <c>--attr-source</c> needs git 2.40 or later (<see cref="AttrSourceSupported"/>); on an older
/// git it is omitted, so <c>core.autocrlf</c> is still neutralised but a <c>.gitattributes</c> conversion can apply.</para>
/// </summary>
internal static class ScopeGit
{
    /// <summary>git's well-known empty tree: an attribute source that defines no attributes at all.</summary>
    internal const string EmptyTree = "4b825dc642cb6eb9a060e54bf8d69288fbee4904";

    /// <summary>The bound on any one git call. Generous: a first snapshot hashes every untracked file.</summary>
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Run git and return its stdout, FAILING CLOSED on a non-zero exit, a timeout or a cancel (all throw
    /// <see cref="InvalidOperationException"/>). <paramref name="indexFile"/> non-null selects a private
    /// <c>GIT_INDEX_FILE</c> AND raw-bytes mode (see the type remarks).
    /// </summary>
    public static string Run(
        string workingDir, string? indexFile, IReadOnlyList<string> args,
        CancellationToken cancellationToken = default, TimeSpan? timeout = null)
    {
        (int exit, string stdout, string stderr) = RunUnchecked(workingDir, indexFile, args, cancellationToken, timeout);
        if (exit != 0)
        {
            throw new InvalidOperationException(
                $"git {string.Join(" ", args)} (in {workingDir}) exited {exit}: {stderr.Trim()}");
        }

        return stdout;
    }

    /// <summary>
    /// Run git and return its exit code with both streams — for a call whose non-zero exit is an ANSWER
    /// (<c>cat-file -e</c>). A timeout or a cancel still throws <see cref="InvalidOperationException"/>.
    /// </summary>
    public static (int ExitCode, string StandardOutput, string StandardError) RunUnchecked(
        string workingDir, string? indexFile, IReadOnlyList<string> args,
        CancellationToken cancellationToken = default, TimeSpan? timeout = null)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            // Issue #457: paths may be non-ASCII; git's streams are UTF-8, never the console code page.
            StandardOutputEncoding = ChildProcessEncoding.Utf8NoBom,
            StandardErrorEncoding = ChildProcessEncoding.Utf8NoBom
        };

        foreach (string arg in ConfigArguments(rawBytes: indexFile is not null))
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
            Task<string> stdout = proc.StandardOutput.ReadToEndAsync(CancellationToken.None);
            Task<string> stderr = proc.StandardError.ReadToEndAsync(CancellationToken.None);

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

            return (proc.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
        }
    }

    /// <summary>The <c>-c</c>/global options every call carries, plus the raw-bytes set for a private index.</summary>
    internal static IReadOnlyList<string> ConfigArguments(bool rawBytes)
    {
        var args = new List<string> { "-c", "core.safecrlf=false", "-c", "core.quotePath=false" };
        if (rawBytes)
        {
            if (AttrSourceSupported.Value)
            {
                args.Insert(0, $"--attr-source={EmptyTree}");
            }

            args.AddRange(["-c", "core.autocrlf=false"]);
        }

        return args;
    }

    /// <summary>
    /// Whether this git understands <c>--attr-source</c> (2.40+), asked once per process by running it. An older
    /// git would reject every raw-bytes call outright; without the option, only <c>.gitattributes</c> conversions
    /// (not <c>core.autocrlf</c>) can still apply to the serial snapshot — a narrower gap than no check at all.
    /// </summary>
    internal static readonly Lazy<bool> AttrSourceSupported = new(() =>
    {
        try
        {
            var psi = new ProcessStartInfo("git")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            psi.ArgumentList.Add($"--attr-source={EmptyTree}");
            psi.ArgumentList.Add("version");
            using Process proc = Process.Start(psi)!;
            Task<string> err = proc.StandardError.ReadToEndAsync();
            proc.StandardOutput.ReadToEnd();
            _ = err.GetAwaiter().GetResult();
            return proc.WaitForExit(30_000) && proc.ExitCode == 0;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return false;
        }
    });
}
