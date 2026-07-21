using Guardrails.Core.Execution;
using Guardrails.Core.Loading;
using Guardrails.Core.Model;

namespace Guardrails.Core.Tests;

/// <summary>
/// Issue #383 (Windows MAX_PATH): the harness's deep worktree layout broke <c>dotnet test</c>'s
/// out-of-process launcher (a built test-exe hit 264 chars &gt; 260 → CreateProcessW Win32 206). Two
/// remedies, both tested here:
/// <list type="number">
/// <item><see cref="SchedulerFactory.WorktreeRootFor"/> — a <c>GUARDRAILS_WORKTREE_ROOT</c> env override
///   plus a SHORTER default (<c>&lt;temp&gt;/gr-wt/&lt;hash&gt;</c>), both keeping the SAME stable
///   per-plan hash subdir so resume / prune still key on one root per plan dir.</item>
/// <item><see cref="WorktreePathPreflight"/> — the run-start GR2038 hard halt when a task's segment base
///   + the reserved build-output budget would exceed Windows MAX_PATH.</item>
/// </list>
/// The preflight length maths is PURE + OS-invariant (a separator is one char on every platform), so its
/// tests run unguarded on all three CI OSes; the env-override tests SAVE/RESTORE the env var around each
/// call so it never leaks.
/// </summary>
public sealed class WorktreeRootAndPathPreflightTests
{
    private const string EnvVar = "GUARDRAILS_WORKTREE_ROOT";

    private static PlanDefinition PlanAt(string planDirectory) => new()
    {
        PlanDirectory = planDirectory,
        Workspace = planDirectory,
        Config = new RunConfig { Version = 1 },
        Tasks = []
    };

    /// <summary>Invoke <see cref="SchedulerFactory.WorktreeRootFor"/> with the env var forced to
    /// <paramref name="value"/> (null = unset) for exactly the one call, then restore whatever was set
    /// before — so a parallel test never sees a leaked value.</summary>
    private static string RootWithEnv(PlanDefinition plan, string? value)
    {
        string? original = Environment.GetEnvironmentVariable(EnvVar);
        try
        {
            Environment.SetEnvironmentVariable(EnvVar, value);
            return SchedulerFactory.WorktreeRootFor(plan);
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnvVar, original);
        }
    }

    // ── WorktreeRootFor: default shape ────────────────────────────────────────────────────────

    [Fact]
    public void WorktreeRootFor_EnvUnset_UsesShortGrWtDefault()
    {
        // Issue #383: the default drops "guardrails-worktrees"→"gr-wt" and the "<planName>-" prefix.
        PlanDefinition plan = PlanAt(Path.Combine(Path.GetTempPath(), "some-plan-folder"));

        string root = RootWithEnv(plan, null);

        Assert.Equal(Path.Combine(Path.GetTempPath(), "gr-wt"), Path.GetDirectoryName(root));
        Assert.DoesNotContain("guardrails-worktrees", root, StringComparison.Ordinal);
        // The leaf is the 8-char lowercase plan-directory hash (unchanged derivation).
        string leaf = Path.GetFileName(root);
        Assert.Equal(8, leaf.Length);
        Assert.Matches("^[0-9a-f]{8}$", leaf);
    }

    // ── WorktreeRootFor: env override ─────────────────────────────────────────────────────────

    [Fact]
    public void WorktreeRootFor_EnvSet_UsesEnvRootWithSameHashSubdir()
    {
        PlanDefinition plan = PlanAt(Path.Combine(Path.GetTempPath(), "some-plan-folder"));
        string envRoot = Path.Combine(Path.GetTempPath(), "gr383-env-" + Guid.NewGuid().ToString("N"));

        string overridden = RootWithEnv(plan, envRoot);
        string defaulted = RootWithEnv(plan, null);

        // root = <envValue>/<shortHash>: the parent is exactly the override, the leaf is the hash subdir.
        Assert.Equal(envRoot, Path.GetDirectoryName(overridden));
        // The per-plan hash subdir is IDENTICAL whether overridden or defaulted — only the parent moves.
        // This is the resume/prune key: distinct plans differ, the same plan is stable across roots.
        Assert.Equal(Path.GetFileName(defaulted), Path.GetFileName(overridden));
    }

    [Fact]
    public void WorktreeRootFor_EnvSetButBlank_FallsBackToDefault()
    {
        // A set-but-empty/whitespace override is treated as "not set" (IsNullOrWhiteSpace), so a stray
        // empty env var never roots worktrees at a bare "/<hash>".
        PlanDefinition plan = PlanAt(Path.Combine(Path.GetTempPath(), "some-plan-folder"));

        string blank = RootWithEnv(plan, "   ");
        string defaulted = RootWithEnv(plan, null);

        Assert.Equal(defaulted, blank);
    }

    // ── WorktreeRootFor: guardrails.json config override (env > config > default) ──────────────

    private static PlanDefinition PlanWithConfigRoot(string planDirectory, string? configRoot) => new()
    {
        PlanDirectory = planDirectory,
        Workspace = planDirectory,
        Config = new RunConfig { Version = 1, WorktreeRoot = configRoot },
        Tasks = []
    };

    [Fact]
    public void WorktreeRootFor_ConfigWorktreeRoot_UsedWhenEnvUnset()
    {
        // The guardrails.json `worktreeRoot` override is honored (below the env var) — the documented key
        // is REAL, not dead (#383 fixed the pre-existing gap). root = <configValue>/<shortHash>.
        string configRoot = Path.Combine(Path.GetTempPath(), "gr383-cfg-" + Guid.NewGuid().ToString("N"));
        PlanDefinition plan = PlanWithConfigRoot(Path.Combine(Path.GetTempPath(), "plan-cfg"), configRoot);

        string root = RootWithEnv(plan, null); // env UNSET → config wins over the default

        Assert.Equal(configRoot, Path.GetDirectoryName(root));
        // Same 8-char plan-dir hash subdir as every other shape (resume/prune stability).
        Assert.Equal(Path.GetFileName(RootWithEnv(PlanAt(plan.PlanDirectory), null)), Path.GetFileName(root));
    }

    [Fact]
    public void WorktreeRootFor_EnvBeatsConfig_WhenBothSet()
    {
        // Precedence: the machine-level env var wins over a per-plan config root (a plan-committed root is
        // non-portable, so the machine override takes it).
        string configRoot = Path.Combine(Path.GetTempPath(), "gr383-cfg-" + Guid.NewGuid().ToString("N"));
        string envRoot = Path.Combine(Path.GetTempPath(), "gr383-env-" + Guid.NewGuid().ToString("N"));
        PlanDefinition plan = PlanWithConfigRoot(Path.Combine(Path.GetTempPath(), "plan-cfg"), configRoot);

        string root = RootWithEnv(plan, envRoot); // both set → env wins

        Assert.Equal(envRoot, Path.GetDirectoryName(root));
    }

    // ── WorktreeRootFor: stability + collision-freedom (the method's contract) ─────────────────

    [Fact]
    public void WorktreeRootFor_SamePlanDir_IdenticalRoot_ForResumeStability()
    {
        PlanDefinition plan = PlanAt(Path.Combine(Path.GetTempPath(), "plan-x"));

        Assert.Equal(RootWithEnv(plan, null), RootWithEnv(PlanAt(plan.PlanDirectory), null));
    }

    [Fact]
    public void WorktreeRootFor_DifferentPlanDirs_DifferentRoots()
    {
        PlanDefinition a = PlanAt(Path.Combine(Path.GetTempPath(), "plan-a"));
        PlanDefinition b = PlanAt(Path.Combine(Path.GetTempPath(), "plan-b"));

        Assert.NotEqual(RootWithEnv(a, null), RootWithEnv(b, null));
    }

    [Fact]
    public void WorktreeRootFor_EnvSet_SamePlanDir_StableAcrossCalls()
    {
        // Resume/prune with the override set must resolve the SAME root on every call.
        PlanDefinition plan = PlanAt(Path.Combine(Path.GetTempPath(), "plan-x"));
        string envRoot = Path.Combine(Path.GetTempPath(), "gr383-stable-" + Guid.NewGuid().ToString("N"));

        Assert.Equal(RootWithEnv(plan, envRoot), RootWithEnv(plan, envRoot));
    }

    // ── WorktreePathPreflight: the task-17 shape (SSOT §2, GR2038) ─────────────────────────────

    // A realistic wave-qualified task id (SSOT §14 identity) — the shape that dominates the segment path.
    private const string RunId = "abcd1234"; // the Guid.ToString("N")[..8] runId shape (always 8 chars)
    private const string Task17 = "wave-03-classify-and-escalate/17-wire-classifier-into-executor";

    // Reproduces the #383 prefix scale: <temp-ish>\guardrails-worktrees\<planName>-<hash>. Combined with
    // RunId + Task17 + attempt-1 the segment base is 174 chars → 174 + 90 reserve = 264, EXACTLY the real
    // failing test-exe length.
    private const string LongOldStyleRoot =
        @"C:\Users\SomeDeveloper\AppData\Local\Temp\guardrails-worktrees\autonomous-mode-impl-a1b2c3d4";

    // The GUARDRAILS_WORKTREE_ROOT short-root remedy the diagnostic recommends.
    private const string ShortRoot = @"C:\gw";

    [Fact]
    public void Preflight_Task17UnderLongRoot_ExceedsMaxPath_AndIsReported()
    {
        IReadOnlyList<(string TaskId, int BaseLength)> offenders =
            WorktreePathPreflight.OffendingTasks(LongOldStyleRoot, RunId, [Task17]);

        (string taskId, int baseLength) = Assert.Single(offenders);
        Assert.Equal(Task17, taskId);
        // Documents the real-world numbers: base + reserved build output reproduces the #383 264-char case.
        Assert.True(baseLength + WorktreePathPreflight.BuildOutputReserve > WorktreePathPreflight.MaxPathLimit,
            $"expected base({baseLength}) + reserve({WorktreePathPreflight.BuildOutputReserve}) > {WorktreePathPreflight.MaxPathLimit}");
    }

    [Fact]
    public void Preflight_Check_LongRoot_ProducesGr2038_WithActionableMessage()
    {
        Diagnostic? halt = WorktreePathPreflight.Check(LongOldStyleRoot, RunId, [Task17]);

        Assert.NotNull(halt);
        Diagnostic diag = halt!;
        Assert.Equal(DiagnosticCodes.WorktreePathTooLong, diag.Code); // GR2038
        Assert.Equal(DiagnosticSeverity.Error, diag.Severity);
        // (a) names the offending task; (b) states the Windows MAX_PATH cause; (c) suggests the env var.
        Assert.Contains(Task17, diag.Message, StringComparison.Ordinal);
        Assert.Contains("MAX_PATH", diag.Message, StringComparison.Ordinal);
        Assert.Contains("260", diag.Message, StringComparison.Ordinal);
        Assert.Contains("GUARDRAILS_WORKTREE_ROOT", diag.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Preflight_Check_SameTaskUnderShortRoot_Passes()
    {
        // The SAME task-17 shape under the short GUARDRAILS_WORKTREE_ROOT remedy fits → no halt.
        Assert.Empty(WorktreePathPreflight.OffendingTasks(ShortRoot, RunId, [Task17]));
        Assert.Null(WorktreePathPreflight.Check(ShortRoot, RunId, [Task17]));
    }

    [Fact]
    public void Preflight_Check_ReportsOnlyOffendingTasks()
    {
        // Under the long root, a deep wave task offends but a shallow one fits — only the offender is named.
        const string shallow = "01-init";
        Diagnostic? halt = WorktreePathPreflight.Check(LongOldStyleRoot, RunId, [shallow, Task17]);

        Assert.NotNull(halt);
        Diagnostic diag = halt!;
        Assert.Contains(Task17, diag.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(shallow, diag.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Preflight_Check_AllTasksFit_ReturnsNull()
    {
        Assert.Null(WorktreePathPreflight.Check(ShortRoot, RunId, ["01-a", "02-b", "03-c"]));
    }
}
