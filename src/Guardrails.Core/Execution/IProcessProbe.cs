namespace Guardrails.Core.Execution;

/// <summary>
/// Issue #704 — asks the OS whether ONE SPECIFIC process is still running: the pid AND the start time that
/// process recorded for itself, so a pid the OS has since handed to an unrelated process does not count.
/// Injected so the run-liveness verdict (<see cref="Journal.RunLiveness"/>) is testable without real processes,
/// real sleep, or a real suspend: the verdict is a fact about the process table, and a test supplies the table.
/// </summary>
public interface IProcessProbe
{
    /// <summary>
    /// True when a process with <paramref name="pid"/> is running now AND is the process that started at
    /// <paramref name="startedAt"/>. False when no process has that pid, when it has exited, when the pid now
    /// belongs to a different process, or when its start time cannot be read.
    /// </summary>
    bool IsRunning(int pid, DateTimeOffset startedAt);
}
