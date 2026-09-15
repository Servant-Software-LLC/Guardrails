using Guardrails.Core.Execution;

namespace Guardrails.Core.Journal;

/// <summary>What <c>guardrails status</c> can say about whether a run is alive (issue #704, SSOT §7 <c>owner</c>).</summary>
public enum RunLivenessState
{
    /// <summary>
    /// The journal names no owner process — written before #704, or by a run that could not read its own identity —
    /// so a live run and a dead one look the same.
    /// </summary>
    NotRecorded,

    /// <summary>The owner process is running now: the same pid, with the start identity it recorded.</summary>
    Running,

    /// <summary>
    /// The owner process is gone — no process has its pid, the pid now belongs to a later process, or the machine has
    /// rebooted — and it never recorded an end.
    /// </summary>
    ExitedWithoutFinishing,

    /// <summary>The owner process recorded the end of its run, whatever the outcome: green, halted, cancelled, or a fault it surfaced.</summary>
    Ended,

    /// <summary>
    /// The owner ran on a different host and is not a running process here, so its liveness cannot be checked from this
    /// machine. Never reported as exited: that would be a claim about a process table this machine cannot see.
    /// </summary>
    OnAnotherHost,

    /// <summary>
    /// The process table cannot say: something holds the pid and its start identity is unreadable here, or the owner
    /// was recorded without the identity this OS compares. Never reported as exited, and never a reason to refuse a run.
    /// </summary>
    CannotCheck
}

/// <summary>
/// Issue #704 — decides whether a run is alive from FACTS: did its owner process record an end, and is that process —
/// the recorded pid WITH its recorded start identity — still in the process table.
/// <para>
/// <b>No clock takes part in the verdict, deliberately.</b> The tempting rule is "no journal progress for N minutes
/// means dead", and it is wrong on exactly the machines this exists for: a suspend advances the wall clock, so a laptop
/// that slept for twelve hours under a perfectly healthy run would read as stalled on waking. On Windows neither
/// <c>TickCount64</c> nor <c>Stopwatch</c> excludes suspend, so no cheap clock dodges it. Whether a process exists, and
/// whether it wrote down its own end, are facts a suspend cannot move. <c>guardrails status</c> still SHOWS the run's
/// last activity, as an observation for the operator — never as an input here.
/// </para>
/// </summary>
public static class RunLiveness
{
    /// <summary>
    /// The verdict for <paramref name="owner"/>, checked from <paramref name="currentHost"/> against
    /// <paramref name="probe"/>. In precedence order:
    /// <list type="number">
    /// <item>no owner → <see cref="RunLivenessState.NotRecorded"/>;</item>
    /// <item>a recorded end → <see cref="RunLivenessState.Ended"/>: the process wrote it down itself, so it outranks the
    ///   process table, which may still show that process (a tool lingering after its run, or every in-process test
    ///   run);</item>
    /// <item>the probe says running → <see cref="RunLivenessState.Running"/>. Asked BEFORE any host comparison: a
    ///   laptop's host name can change under a live run, and a matching live process is the owner whatever the name now
    ///   says;</item>
    /// <item>the probe cannot tell → <see cref="RunLivenessState.CannotCheck"/>;</item>
    /// <item>otherwise, recorded on a different host → <see cref="RunLivenessState.OnAnotherHost"/>: not running HERE says
    ///   nothing about a process table this machine cannot see;</item>
    /// <item>otherwise → <see cref="RunLivenessState.ExitedWithoutFinishing"/>.</item>
    /// </list>
    /// </summary>
    public static RunLivenessState Assess(RunOwner? owner, string? currentHost, IProcessProbe probe)
    {
        if (owner is null)
        {
            return RunLivenessState.NotRecorded;
        }

        if (owner.FinishedAt is not null)
        {
            return RunLivenessState.Ended;
        }

        return probe.Check(owner) switch
        {
            ProcessCheck.Running => RunLivenessState.Running,
            ProcessCheck.CannotTell => RunLivenessState.CannotCheck,
            _ => owner.Host is { } recordedHost
                 && currentHost is { } here
                 && !string.Equals(recordedHost, here, StringComparison.OrdinalIgnoreCase)
                ? RunLivenessState.OnAnotherHost
                : RunLivenessState.ExitedWithoutFinishing
        };
    }

    /// <summary>
    /// The owner record THIS process stamps when <c>guardrails run</c> claims a journal — its pid, its start identity as
    /// <see cref="SystemProcessProbe"/> will later compare it, and its host. Null when this platform will not give the
    /// identity; see <see cref="RunJournal.LoadOrCreateForRun"/> for what the claim does then.
    /// </summary>
    public static RunOwner? OwnerForThisProcess() => SystemProcessProbe.IdentityOfThisProcess(ThisHost());

    /// <summary>
    /// This machine's host name, or null when the OS will not give it — so a sandbox that refuses the call costs the
    /// host comparison, never the verdict.
    /// </summary>
    public static string? ThisHost()
    {
        try
        {
            return Environment.MachineName;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
