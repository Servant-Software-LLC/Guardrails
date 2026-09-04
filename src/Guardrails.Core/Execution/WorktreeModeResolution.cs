using System.Diagnostics;

namespace Guardrails.Core.Execution;

/// <summary>
/// WHY a run resolved the worktree-mode question the way it did (issue #596). Carried alongside
/// <see cref="WorktreeModeResolution.Enabled"/> so a demotion to serial is never an unattributed
/// <c>false</c>: the three reasons have three different operator responses, and collapsing them into a
/// bare boolean is exactly what made the #596 disagreement invisible.
/// </summary>
public enum WorktreeModeReason
{
    /// <summary>
    /// <c>maxParallelism &gt; 1</c> AND the workspace is a git working tree: the run gets a plan branch,
    /// per-segment worktrees, per-union re-verify and the terminal gate (plan 08 §1).
    /// </summary>
    WorktreeMode,

    /// <summary>
    /// <c>maxParallelism &lt;= 1</c> — worktree mode was never requested, so the shared-workspace serial
    /// path is the CORRECT answer, not a demotion. The git probe is never run in this case (the
    /// short-circuit is deliberate: a serial run has no git dependency at all, SSOT §1).
    /// </summary>
    SerialByConfiguration,

    /// <summary>
    /// <c>maxParallelism &gt; 1</c> but git ANSWERED that the workspace is not inside a working tree.
    /// Production blocks this at validation (GR2015); a caller that bypassed validate reaches the
    /// Scheduler's F7 clamp, which demotes to serial and tells the observer.
    /// </summary>
    WorkspaceNotAGitRepository
}

/// <summary>
/// What a git work-tree probe found — a TRI-STATE, which is the whole point of issue #596.
/// <para>
/// The predecessor (<c>SchedulerFactory.IsGitRepository</c>) ended <c>catch { return false; }</c>, so
/// <b>"git answered: this is not a repository"</b> and <b>"git could not be run at all"</b> produced the
/// SAME answer. The first is a fact; the second is an unknown being reported as a fact — and it silently
/// downgraded a parallel run to serial, a change of the run's whole isolation model that the operator
/// never saw. <see cref="InsideWorkTree"/> is therefore nullable: <c>null</c> means the probe never got
/// an answer, and <see cref="Failure"/> says why.
/// </para>
/// </summary>
public sealed record GitWorkTreeProbeResult
{
    /// <summary>
    /// <c>true</c>/<c>false</c> when git RAN and answered; <c>null</c> when git could not be run at all
    /// (absent executable, a blocked or contended spawn, a denied working directory) — an UNKNOWN, never
    /// a "no".
    /// </summary>
    public bool? InsideWorkTree { get; init; }

    /// <summary>Why the probe could not run; non-null exactly when <see cref="InsideWorkTree"/> is null.</summary>
    public string? Failure { get; init; }

    /// <summary>git ran and reported the workspace IS inside a working tree.</summary>
    public static GitWorkTreeProbeResult Inside { get; } = new() { InsideWorkTree = true };

    /// <summary>git ran and reported the workspace is NOT inside a working tree.</summary>
    public static GitWorkTreeProbeResult Outside { get; } = new() { InsideWorkTree = false };

    /// <summary>git could not be run — an unknown, carrying <paramref name="failure"/> as the reason.</summary>
    public static GitWorkTreeProbeResult CouldNotRun(string failure) =>
        new() { InsideWorkTree = null, Failure = failure };
}

/// <summary>
/// The injectable seam over "is this directory inside a git working tree?" — the harness discipline of an
/// injectable probe rather than a hard dependency on machine state (the same rationale as
/// <c>IExecutableProbe</c>). Exists so the <b>unavailable-git</b> branch of
/// <see cref="SchedulerFactory.ResolveWorktreeMode"/> is testable without uninstalling git.
/// </summary>
public interface IGitWorkTreeProbe
{
    /// <summary>Probe <paramref name="workspace"/>; see <see cref="GitWorkTreeProbeResult"/> for the tri-state.</summary>
    GitWorkTreeProbeResult Probe(string workspace);
}

/// <summary>
/// The production <see cref="IGitWorkTreeProbe"/>: <c>git rev-parse --is-inside-work-tree</c> in the
/// workspace. Distinguishes an ANSWER from a FAILURE TO ASK — if the process ran to completion, git's
/// verdict is taken (exit 0 + <c>true</c> ⇒ inside, anything else ⇒ outside, which covers git's own
/// locale-independent exit 128 "not a git repository"); if it could not be started or could not be waited
/// on, the result is <see cref="GitWorkTreeProbeResult.CouldNotRun"/>.
/// </summary>
public sealed class ProcessGitWorkTreeProbe : IGitWorkTreeProbe
{
    /// <summary>The shared instance — the probe is stateless.</summary>
    public static ProcessGitWorkTreeProbe Instance { get; } = new();

    /// <inheritdoc />
    public GitWorkTreeProbeResult Probe(string workspace)
    {
        // A directory that is not there is an ANSWER, not a probe failure: it cannot be inside a working
        // tree. Checked first because handing a nonexistent WorkingDirectory to Process.Start throws, which
        // would otherwise be misreported as "git could not be run".
        if (!Directory.Exists(workspace))
        {
            return GitWorkTreeProbeResult.Outside;
        }

        Process? proc;
        try
        {
            var psi = new ProcessStartInfo("git")
            {
                WorkingDirectory = workspace,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                // Issue #457: pin UTF-8 on every git stream (this one is ASCII-only, kept uniform).
                StandardOutputEncoding = ChildProcessEncoding.Utf8NoBom,
                StandardErrorEncoding = ChildProcessEncoding.Utf8NoBom
            };
            psi.ArgumentList.Add("rev-parse");
            psi.ArgumentList.Add("--is-inside-work-tree");
            proc = Process.Start(psi);
        }
        catch (Exception ex)
        {
            return GitWorkTreeProbeResult.CouldNotRun($"{ex.GetType().Name}: {ex.Message}");
        }

        if (proc is null)
        {
            return GitWorkTreeProbeResult.CouldNotRun("Process.Start returned no process for 'git'.");
        }

        using (proc)
        {
            try
            {
                string stdout = proc.StandardOutput.ReadToEnd();
                proc.StandardError.ReadToEnd();
                proc.WaitForExit();

                // git RAN, so its verdict stands — including the non-zero "not a git repository" exit,
                // matched on the exit code rather than on stderr text (which is localized).
                return proc.ExitCode == 0 && stdout.Trim().Equals("true", StringComparison.OrdinalIgnoreCase)
                    ? GitWorkTreeProbeResult.Inside
                    : GitWorkTreeProbeResult.Outside;
            }
            catch (Exception ex)
            {
                return GitWorkTreeProbeResult.CouldNotRun($"{ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}

/// <summary>
/// The ONE worktree-mode answer for a run (issue #596), produced by
/// <see cref="SchedulerFactory.ResolveWorktreeMode"/> and HANDED DOWN to every consumer rather than
/// re-derived at each of them.
/// <para>
/// <b>The defect this closes.</b> The predicate <c>maxParallelism &gt; 1 &amp;&amp; IsGitRepository(...)</c>
/// used to be spelled twice in <c>SchedulerFactory</c> (the provider wiring, and the public
/// <c>WouldUseWorktreeMode</c>) and re-evaluated — with a fresh git subprocess each time — at six load-bearing
/// sites: the provider wiring, the Windows junction setup, the MAX_PATH preflight, the end-of-run reclaim,
/// the effective <c>maxParallelism</c> stamped into <c>run.json</c>, the wave-brief prompt gate, and
/// <c>PlanPhaseWorkspace</c>. Two evaluations could disagree WITHIN ONE RUN, in both directions, silently:
/// a run could wire worktree mode while journaling itself serial (allocating no junction, and running the
/// end-of-run reclaim against the wrong predicate) with nothing on stdout and nothing to any observer.
/// Folding the fact ONCE and threading it is the D22a discipline the rest of the harness follows.
/// </para>
/// </summary>
public sealed record WorktreeModeResolution
{
    /// <summary>Whether this run uses worktree mode (a plan branch + per-segment worktrees).</summary>
    public required bool Enabled { get; init; }

    /// <summary>Why — see <see cref="WorktreeModeReason"/>.</summary>
    public required WorktreeModeReason Reason { get; init; }

    /// <summary>
    /// Non-null when the git work-tree probe could not RUN (issue #596): the run did not get an answer
    /// from git, and <see cref="Enabled"/> reflects the plan's REQUEST rather than a probe result. The CLI
    /// renders this loudly at run start — an unavailable git must never be a silent serial downgrade.
    /// </summary>
    public string? GitProbeFailure { get; init; }
}
