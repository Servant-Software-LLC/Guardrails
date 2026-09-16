using Guardrails.Core.Journal;

namespace Guardrails.Core.Execution;

/// <summary>
/// The three answers a process table can give about a run's recorded owner (issue #704).
/// <see cref="CannotTell"/> is not a polite <see cref="NotRunning"/>: it is the answer when something holds that
/// pid whose identity cannot be read from here (access denied), or when the recorded identity is not one this OS
/// can compare — and calling either of those "gone" would print a resume command for a run that may be alive.
/// </summary>
public enum ProcessCheck
{
    /// <summary>The recorded owner is running now: the same pid, with the same start identity.</summary>
    Running,

    /// <summary>
    /// The recorded owner is not running: no process has that pid, the pid now belongs to a different process, or
    /// the machine has rebooted since the owner recorded itself.
    /// </summary>
    NotRunning,

    /// <summary>It cannot be decided from here.</summary>
    CannotTell
}

/// <summary>
/// Issue #704 — asks the OS whether ONE SPECIFIC process is still running: the recorded pid AND the start identity
/// that process recorded for itself, so a pid the OS has since handed to an unrelated process does not count.
/// Injected so the run-liveness verdict (<see cref="RunLiveness"/>) is testable without real processes, real sleep,
/// or a real suspend: the verdict is a fact about the process table, and a test supplies the table.
/// </summary>
public interface IProcessProbe
{
    /// <summary>Whether <paramref name="owner"/> — its pid, with its recorded start identity — is running now.</summary>
    ProcessCheck Check(RunOwner owner);
}
