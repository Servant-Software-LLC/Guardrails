using Guardrails.Core.Loading;

namespace Guardrails.Core.Execution;

/// <summary>
/// Issue #383 run-start path-length preflight (SSOT §2, diagnostic <see cref="DiagnosticCodes.WorktreePathTooLong"/>
/// = GR2038). In WORKTREE mode each task builds its OWN artefacts under a segment worktree at
/// <c>&lt;root&gt;/&lt;runId&gt;/&lt;taskId&gt;/attempt-N/</c>; a task's built test-exe path measured
/// <b>264</b> chars (&gt; Windows MAX_PATH 260) so CreateProcessW failed with Win32 206
/// (ERROR_FILENAME_EXCED_RANGE) while a sibling task at 259 passed. Windows <c>LongPathsEnabled</c> does
/// NOT lift CreateProcess's application-name ceiling, so a short root is the durable fix — the harness
/// FAILS FAST before executing any task when a segment base + the reserved build-output budget would
/// exceed MAX_PATH, and points the operator at the <c>GUARDRAILS_WORKTREE_ROOT</c> short-root override.
/// </summary>
/// <remarks>
/// The core length computation (<see cref="OffendingTasks"/>) is PURE and OS-agnostic: a path segment is
/// one character whichever separator the OS uses, so for a given root string the measured length is
/// identical on every platform. That keeps it unit-testable on Linux / macOS CI. The Windows-only +
/// worktree-only GATE lives at the caller (the CLI run-start path) — non-Windows and serial / in-place
/// (non-worktree) mode never invoke it, per the deliverable.
/// </remarks>
public static class WorktreePathPreflight
{
    /// <summary>Windows MAX_PATH — the ceiling CreateProcessW enforces on a spawned process's application name.</summary>
    public const int MaxPathLimit = 260;

    /// <summary>
    /// Reserved build-output budget (chars) the harness's OWN generated artefacts consume BEYOND a task's
    /// segment BASE path before the OS ever sees the longest path — sized for the deepest foreseeable
    /// in-segment build output <c>\&lt;project-subpath&gt;\bin\Debug\net8.0\&lt;assembly&gt;.exe</c>.
    /// Justified by the issue #383 real case: a segment base + this reserve reproduces the 264-char
    /// test-exe that broke CreateProcessW (a sibling at 259 passed), so <c>base + reserve &gt; 260</c> is
    /// the run-start proxy for "this task's build output will blow MAX_PATH".
    /// </summary>
    public const int BuildOutputReserve = 90;

    /// <summary>
    /// The tasks whose segment base (<c>Path.Combine(worktreeRoot, runId, taskId, "attempt-1")</c>, the
    /// EXACT shape <see cref="GitWorktreeProvider.CreateSegment"/> builds) + <see cref="BuildOutputReserve"/>
    /// would exceed <see cref="MaxPathLimit"/>. Pure + OS-agnostic (see the type remarks). Returns each
    /// offender as <c>(taskId, computed base length)</c>, in the input order; empty when every task fits.
    /// </summary>
    public static IReadOnlyList<(string TaskId, int BaseLength)> OffendingTasks(
        string worktreeRoot, string runId, IEnumerable<string> taskIds)
    {
        var offenders = new List<(string, int)>();
        foreach (string taskId in taskIds)
        {
            int baseLength = Path.Combine(worktreeRoot, runId, taskId, "attempt-1").Length;
            if (baseLength + BuildOutputReserve > MaxPathLimit)
            {
                offenders.Add((taskId, baseLength));
            }
        }

        return offenders;
    }

    /// <summary>
    /// The GR2038 hard-halt <see cref="Diagnostic"/> when ≥1 task would exceed Windows MAX_PATH, else
    /// <c>null</c> (every task fits — the run proceeds). The message names each offending task and its
    /// computed base length, states the Windows MAX_PATH cause, and suggests pointing
    /// <see cref="SchedulerFactory.WorktreeRootEnvVar"/> at a short path (e.g. <c>C:\gw</c>).
    /// </summary>
    public static Diagnostic? Check(string worktreeRoot, string runId, IEnumerable<string> taskIds)
    {
        IReadOnlyList<(string TaskId, int BaseLength)> offenders = OffendingTasks(worktreeRoot, runId, taskIds);
        if (offenders.Count == 0)
        {
            return null;
        }

        string offenderList = string.Join(
            "\n",
            offenders.Select(o =>
                $"  - {o.TaskId} (segment base {o.BaseLength} chars + {BuildOutputReserve} reserved build output > {MaxPathLimit})"));

        string message =
            $"{offenders.Count} task(s) would exceed the Windows MAX_PATH limit ({MaxPathLimit} chars) in worktree mode. " +
            $"Each task builds under a segment worktree at " +
            $"'{worktreeRoot}{Path.DirectorySeparatorChar}{runId}{Path.DirectorySeparatorChar}<taskId>{Path.DirectorySeparatorChar}attempt-N', " +
            $"and its build output (e.g. \\bin\\Debug\\net8.0\\<assembly>.exe, ~{BuildOutputReserve} chars) would push a " +
            $"child-process path past {MaxPathLimit} — CreateProcessW then fails with Win32 206 " +
            "(ERROR_FILENAME_EXCED_RANGE), which Windows LongPathsEnabled does NOT prevent.\n" +
            "Offending task(s):\n" + offenderList + "\n" +
            $"Fix: set the {SchedulerFactory.WorktreeRootEnvVar} environment variable to a SHORT path " +
            "(e.g. C:\\gw) and re-run — the harness roots all worktrees there instead of the deep default.";

        return new Diagnostic
        {
            Code = DiagnosticCodes.WorktreePathTooLong,
            Severity = DiagnosticSeverity.Error,
            Path = worktreeRoot,
            Message = message
        };
    }
}
