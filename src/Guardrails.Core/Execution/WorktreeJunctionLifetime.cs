using System.Runtime.InteropServices;

namespace Guardrails.Core.Execution;

/// <summary>
/// Issue #419 — the PROCESS lifetime of a Windows short-junction LINK. A junction
/// (<c>&lt;drive&gt;:\.a</c>..<c>\.z</c> → the real worktree root) is a per-run child-process cwd ALIAS, not
/// resume state (git canonicalizes it to the real path, so a resume resolves the same segments under any
/// letter — see <see cref="WorktreeJunction"/>). This owns the link's process lifetime and RELEASES it —
/// LINK-ONLY — on every RECOVERABLE process exit:
/// <list type="bullet">
/// <item><see cref="Dispose"/> (via the <c>using</c>/<c>finally</c> around the run body) — the normal
///   green / needs-human / failed / early-return path;</item>
/// <item><see cref="AppDomain.ProcessExit"/> — a normal process teardown that skipped the finally;</item>
/// <item><see cref="Console.CancelKeyPress"/> and <see cref="PosixSignal.SIGINT"/>/<see cref="PosixSignal.SIGTERM"/>
///   — an interactive Ctrl-C or a <c>kill</c>/CI cancellation.</item>
/// </list>
/// <para>
/// <b>The bound (never an absolute).</b> No junction survives any RECOVERABLE exit; a hard-kill
/// (SIGKILL / power loss) that runs NONE of these handlers leaks AT MOST ONE link, reclaimed by the startup
/// GC (<see cref="WorktreeReclaim"/> B). The live-junction count is therefore ≤ the number of concurrently
/// running guardrails processes (normally 1) — exhaustion is structurally bounded, not merely unlikely.
/// </para>
/// <para>
/// <b>Double-fire safety (the Ctrl-C interplay).</b> An interactive Ctrl-C fires BOTH
/// <see cref="Console.CancelKeyPress"/> AND (via System.CommandLine cancelling the token) the run-body
/// unwind into <see cref="Dispose"/>. An <see cref="Interlocked"/> once-guard makes the release AT-MOST-ONCE,
/// and the <see cref="WorktreeJunction.IsJunctionTo"/> guard makes it non-destructive: a late-firing handler
/// can never remove a link a SUCCESSOR run has since re-allocated to a DIFFERENT real root (the once-guard
/// short-circuits first; the target guard is belt-and-suspenders). The <see cref="Console.CancelKeyPress"/>
/// handler NEVER sets <c>e.Cancel</c> — it leaves the CLI's own graceful-cancellation handling untouched and
/// only releases the link.
/// </para>
/// </summary>
public sealed class WorktreeJunctionLifetime : IDisposable
{
    private readonly string _linkPath;
    private readonly string _realRoot;
    private readonly TextWriter _log;

    private readonly ConsoleCancelEventHandler _cancelHandler;
    private readonly EventHandler _processExitHandler;
    private readonly PosixSignalRegistration? _sigint;
    private readonly PosixSignalRegistration? _sigterm;

    private int _released; // 0 = not yet released, 1 = released (Interlocked once-guard)

    /// <summary>
    /// Register the process-exit handlers for the junction <paramref name="linkPath"/> (created for this run,
    /// pointing at <paramref name="realRoot"/>). Constructed by the CLI ONLY when
    /// <see cref="WorktreeJunction.ResolveForRun"/> actually allocated a junction — a lazy-skip / non-Windows
    /// / fallback run constructs none.
    /// </summary>
    public WorktreeJunctionLifetime(string linkPath, string realRoot, TextWriter log)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(linkPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(realRoot);
        _linkPath = linkPath;
        _realRoot = realRoot;
        _log = log;

        _processExitHandler = (_, _) => ReleaseOnce();
        AppDomain.CurrentDomain.ProcessExit += _processExitHandler;

        // Do NOT set e.Cancel — leave the CLI's own Ctrl-C handling to cancel the token / unwind gracefully;
        // this handler only releases the link (the once-guard makes the paired finally-Dispose a no-op).
        _cancelHandler = (_, _) => ReleaseOnce();
        Console.CancelKeyPress += _cancelHandler;

        _sigint = TryRegisterSignal(PosixSignal.SIGINT);
        _sigterm = TryRegisterSignal(PosixSignal.SIGTERM);
    }

    private PosixSignalRegistration? TryRegisterSignal(PosixSignal signal)
    {
        try
        {
            return PosixSignalRegistration.Create(signal, _ => ReleaseOnce());
        }
        catch (Exception ex) when (ex is IOException or PlatformNotSupportedException or ArgumentException)
        {
            return null; // best-effort: a platform that will not register this signal still gets Dispose/ProcessExit.
        }
    }

    /// <summary>
    /// Release the junction LINK at most once (best-effort, LINK-ONLY, target-guarded). The
    /// <see cref="Interlocked"/> exchange makes concurrent / repeated calls (Ctrl-C double-fire, a
    /// ProcessExit after Dispose) safe; the <see cref="WorktreeJunction.IsJunctionTo"/> check ensures a link
    /// a concurrent process has since re-pointed is never removed. <see cref="WorktreeJunction.RemoveJunctionLink"/>
    /// itself only ever deletes a reparse point, never the target's contents (the data-loss guard).
    /// </summary>
    public void ReleaseOnce()
    {
        if (Interlocked.Exchange(ref _released, 1) != 0)
        {
            return; // already released — the once-guard the Ctrl-C double-fire relies on
        }

        if (WorktreeJunction.IsJunctionTo(_linkPath, _realRoot))
        {
            WorktreeJunction.RemoveJunctionLink(_linkPath);
            _log.WriteLine($"[guardrails] worktree junction released on exit: '{_linkPath}' (issue #419).");
        }
    }

    /// <summary>Release the link (once) and unregister every handler — so a repeated in-process run never accumulates handlers.</summary>
    public void Dispose()
    {
        ReleaseOnce();
        AppDomain.CurrentDomain.ProcessExit -= _processExitHandler;
        Console.CancelKeyPress -= _cancelHandler;
        _sigint?.Dispose();
        _sigterm?.Dispose();
    }
}
