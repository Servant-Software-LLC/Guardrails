using Guardrails.Core.Loading;
using Guardrails.Core.Model;

namespace Guardrails.Core.Tests;

/// <summary>
/// Tests for plan 08 M2 validation gates, all exercised through the existing
/// public <see cref="PlanValidator"/> / plan-load path.
///
/// Encoded gates (SSOT §1/§2/§3.3/§4.3, plan 08 §1/§3):
///   GR2015 — workspace is NOT a git repository top-level (error)
///   GR2016 — deep worktreeRoot + deep source tree on Windows risks MAX_PATH (warning)
///   GR2017 — multi-leaf or fan-in plan with no integrationGate sink (error)
///   GR2018 — integrationGate sink has no scope:"integration" guardrail (error, empty set)
/// </summary>
public sealed class ParallelValidationGateTests : IDisposable
{
    // Diagnostic codes allocated for plan 08 M2 (not yet in DiagnosticCodes.cs).
    private const string Gr2015 = "GR2015"; // workspace not a git repository top-level (error)
    private const string Gr2016 = "GR2016"; // deep worktreeRoot + source risks MAX_PATH, Windows (warning)
    private const string Gr2017 = "GR2017"; // multi-leaf / fan-in plan missing integrationGate sink (error)
    private const string Gr2018 = "GR2018"; // integrationGate sink has no scope:"integration" guardrail (error)

    // Freshly created temp directory — exists on disk but has no .git inside it.
    // Used as a non-git workspace (GR2015) and as the root for disk-based plan fixtures.
    private readonly string _tempRoot;

    public ParallelValidationGateTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "gr-m2-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); }
        catch (IOException) { }
    }

    // =========================================================================
    // GR2015 — workspace is NOT a git repository top-level
    // =========================================================================

    [Fact]
    public void NonGitWorkspace_WorktreeMode_ProducesGr2015_Error()
    {
        // SSOT §1, plan 08 §1, PO decision: git is required ONLY in worktree mode
        // (maxParallelism > 1) — parallel tasks need per-segment worktree isolation, which
        // needs a git repository (plan branch, segment worktrees). A non-git workspace in
        // worktree mode must be rejected at validate time → GR2015 error.
        // _tempRoot is a freshly created directory with no .git — guaranteed non-git root.
        PlanDefinition plan = InMemoryPlan(workspace: _tempRoot, maxParallelism: 3, ScriptTask("01-a"));

        IReadOnlyList<Diagnostic> diagnostics = Validate(plan);

        Assert.Contains(diagnostics, d => d.Code == Gr2015 && d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void NonGitWorkspace_SerialMode_ProducesNoGr2015()
    {
        // PO decision: a SERIAL run (maxParallelism == 1) uses the shared-workspace model —
        // no worktrees, no concurrency, no isolation/corruption risk — so git is NOT required.
        // The same non-git workspace that fails in worktree mode must produce NO GR2015 here.
        PlanDefinition plan = InMemoryPlan(workspace: _tempRoot, maxParallelism: 1, ScriptTask("01-a"));

        IReadOnlyList<Diagnostic> diagnostics = Validate(plan);

        Assert.DoesNotContain(diagnostics, d => d.Code == Gr2015);
    }

    // =========================================================================
    // GR2016 — deep worktreeRoot + source tree risks MAX_PATH on Windows
    // =========================================================================

    [Fact]
    public void DeepWorktreeRoot_OnWindows_ProducesGr2016_Warning()
    {
        // SSOT §2, plan 08 §1: when the configured worktreeRoot is deep AND the source tree
        // is also deep, harness-managed paths (segment worktrees, task subdirs, guardrail
        // files) can exceed Windows MAX_PATH (260 chars). GR2016 warns about this risk and
        // documents core.longpaths as the mitigation.
        // Gate: this is Windows-specific — POSIX file-systems have no 260-char path limit.
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // Create a disk plan whose guardrails.json declares a deep worktreeRoot.
        // The current loader silently ignores "worktreeRoot" (not in RawRunConfig); M2 adds
        // the field and the validator checks combined path length. This test compiles against
        // current code but fails because the validator does not yet emit GR2016.
        string planDir = Path.Combine(_tempRoot, "gr2016-deep-root");
        string deepRoot = @"C:\" + new string('a', 220); // 223 chars → typical suffix pushes past 260
        WriteDiskPlan(
            planDir,
            guardrailsJson: $"{{ \"version\": 1, \"worktreeRoot\": \"{deepRoot.Replace(@"\", @"\\")}\" }}",
            tasks: [("01-a", "{ \"description\": \"a\", \"dependsOn\": [] }")]);

        PlanDefinition plan = LoadPlan(planDir);
        IReadOnlyList<Diagnostic> diagnostics = Validate(plan);

        // FAILS on current code: ValidateMaxPathRisk is not yet implemented.
        Assert.Contains(diagnostics, d => d.Code == Gr2016 && d.Severity == DiagnosticSeverity.Warning);
    }

    // =========================================================================
    // GR2017 — multi-leaf or fan-in plan missing the integrationGate sink
    // =========================================================================

    [Fact]
    public void MultiLeafPlan_WorktreeMode_WithoutIntegrationGateSink_ProducesGr2017_Error()
    {
        // SSOT §3.3, plan 08 §3, PO decision: the integration-gate requirement fires ONLY in
        // worktree mode (maxParallelism > 1). The terminal gate verifies the merged union of the
        // parallel branches; a multi-leaf plan run in parallel with no integrationGate:true sink
        // leaves those branches unverified at the integration level → GR2017 error.
        //
        // DAG:  01-root → 02-leaf-a  (leaf — no task depends on it)
        //               → 03-leaf-b  (leaf — no task depends on it)
        // Two leaves, no integrationGate task, maxParallelism>1 → GR2017.
        PlanDefinition plan = InMemoryPlan(
            "/fake/workspace",
            maxParallelism: 3,
            ScriptTask("01-root"),
            ScriptTask("02-leaf-a", "01-root"),
            ScriptTask("03-leaf-b", "01-root"));

        IReadOnlyList<Diagnostic> diagnostics = Validate(plan);

        Assert.Contains(diagnostics, d => d.Code == Gr2017 && d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void MultiLeafPlan_SerialMode_WithoutIntegrationGateSink_ProducesNoGr2017()
    {
        // PO decision: a SERIAL run (maxParallelism == 1) uses the shared-workspace model — there
        // are no parallel branches to merge, so the integration gate has nothing to verify and the
        // hard requirement does not apply. The same multi-leaf no-gate plan that fails in worktree
        // mode must produce NO GR2017 here.
        PlanDefinition plan = InMemoryPlan(
            "/fake/workspace",
            maxParallelism: 1,
            ScriptTask("01-root"),
            ScriptTask("02-leaf-a", "01-root"),
            ScriptTask("03-leaf-b", "01-root"));

        IReadOnlyList<Diagnostic> diagnostics = Validate(plan);

        Assert.DoesNotContain(diagnostics, d => d.Code == Gr2017);
    }

    [Fact]
    public void FanInPlan_WorktreeMode_WithoutIntegrationGateSink_ProducesGr2017_Error()
    {
        // SSOT §3.3, plan 08 §3, PO decision: in worktree mode (maxParallelism > 1) a plan with any
        // fan-in task (≥2 upstreams → a real union merge + re-verify path) MUST have an
        // integrationGate:true sink. The fan-in's per-union re-verify is not the whole-repo check;
        // the terminal gate must additionally exist.
        //
        // DAG:  01-a → 03-fanin  (2 upstreams → fan-in)
        //       02-b ↗
        // One leaf but it IS the fan-in; no integrationGate task, maxParallelism>1 → GR2017.
        PlanDefinition plan = InMemoryPlan(
            "/fake/workspace",
            maxParallelism: 3,
            ScriptTask("01-a"),
            ScriptTask("02-b"),
            ScriptTask("03-fanin", "01-a", "02-b"));

        IReadOnlyList<Diagnostic> diagnostics = Validate(plan);

        Assert.Contains(diagnostics, d => d.Code == Gr2017 && d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void FanInPlan_SerialMode_WithoutIntegrationGateSink_ProducesNoGr2017()
    {
        // PO decision: a SERIAL run (maxParallelism == 1) never executes branches in parallel, so
        // even a fan-in topology merges no concurrent worktrees — the integration gate has nothing
        // to verify. The same fan-in no-gate plan that fails in worktree mode produces NO GR2017 here.
        PlanDefinition plan = InMemoryPlan(
            "/fake/workspace",
            maxParallelism: 1,
            ScriptTask("01-a"),
            ScriptTask("02-b"),
            ScriptTask("03-fanin", "01-a", "02-b"));

        IReadOnlyList<Diagnostic> diagnostics = Validate(plan);

        Assert.DoesNotContain(diagnostics, d => d.Code == Gr2017);
    }

    // =========================================================================
    // GR2018 — integrationGate sink carries no scope:"integration" guardrail
    // =========================================================================

    [Fact]
    public void IntegrationGateSink_WorktreeMode_WithoutScopeIntegrationGuardrail_ProducesGr2018_Error()
    {
        // SSOT §3.3/§4.3, plan 08 §3, PO decision: in worktree mode (maxParallelism > 1) the
        // integrationGate:true sink MUST carry at least one guardrail declared scope:"integration".
        // Without it the terminal gate verifies nothing — an empty integration-guardrail set is not
        // a sound soundness boundary → GR2018.
        //
        // Uses disk loading because task.json carries the integrationGate:true field. The gate
        // task's guardrail has no scope:"integration" sidecar. guardrails.json declares
        // maxParallelism>1 to put the plan in worktree mode where the gate requirement applies.
        string planDir = Path.Combine(_tempRoot, "gr2018-no-scope");
        WriteDiskPlan(
            planDir,
            guardrailsJson: "{ \"version\": 1, \"maxParallelism\": 3 }",
            tasks:
            [
                ("01-a", "{ \"description\": \"task a\", \"dependsOn\": [] }"),
                ("02-b", "{ \"description\": \"task b\", \"dependsOn\": [] }"),
                // integrationGate:true; the guardrail written by WriteDiskPlan has no
                // scope:"integration" in any sidecar.
                ("03-gate",
                    "{ \"description\": \"integration gate\", \"dependsOn\": [\"01-a\", \"02-b\"], \"integrationGate\": true }")
            ]);

        PlanDefinition plan = LoadPlan(planDir);
        IReadOnlyList<Diagnostic> diagnostics = Validate(plan);

        Assert.Contains(diagnostics, d => d.Code == Gr2018 && d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void IntegrationGateSink_SerialMode_WithoutScopeIntegrationGuardrail_ProducesNoGr2018()
    {
        // PO decision: a SERIAL run (maxParallelism == 1) merges no parallel branches, so the
        // integration gate has nothing to verify and the scope:"integration" requirement does not
        // apply. The same integrationGate:true sink with no integration-scoped guardrail that fails
        // in worktree mode must produce NO GR2018 here.
        string planDir = Path.Combine(_tempRoot, "gr2018-serial-no-scope");
        WriteDiskPlan(
            planDir,
            guardrailsJson: "{ \"version\": 1, \"maxParallelism\": 1 }",
            tasks:
            [
                ("01-a", "{ \"description\": \"task a\", \"dependsOn\": [] }"),
                ("02-b", "{ \"description\": \"task b\", \"dependsOn\": [] }"),
                ("03-gate",
                    "{ \"description\": \"integration gate\", \"dependsOn\": [\"01-a\", \"02-b\"], \"integrationGate\": true }")
            ]);

        PlanDefinition plan = LoadPlan(planDir);
        IReadOnlyList<Diagnostic> diagnostics = Validate(plan);

        Assert.DoesNotContain(diagnostics, d => d.Code == Gr2018);
    }

    // =========================================================================
    // Private helpers
    // =========================================================================

    private static IReadOnlyList<Diagnostic> Validate(PlanDefinition plan) =>
        new PlanValidator(FakeExecutableProbe.All).Validate(plan);

    /// <summary>
    /// Build an in-memory plan with the given workspace root and tasks. No disk I/O.
    /// </summary>
    private static PlanDefinition InMemoryPlan(string workspace, params TaskNode[] tasks) =>
        InMemoryPlan(workspace, maxParallelism: 3, tasks);

    /// <summary>
    /// Build an in-memory plan with an explicit workspace root and maxParallelism. The
    /// maxParallelism overload exists for the GR2015 gate, which is conditional on worktree
    /// mode (maxParallelism > 1) per the PO decision; serial runs (== 1) skip the git check.
    /// </summary>
    private static PlanDefinition InMemoryPlan(string workspace, int maxParallelism, params TaskNode[] tasks) =>
        new()
        {
            PlanDirectory = workspace,
            Workspace = workspace,
            Config = new RunConfig { Version = 1, MaxParallelism = maxParallelism },
            Tasks = tasks
        };

    private static PlanDefinition InMemoryPlan(params TaskNode[] tasks) =>
        InMemoryPlan("/fake/workspace", tasks);

    /// <summary>
    /// A single-guardrail script-only task for DAG fixtures (mirrors PlanFixtures.Task).
    /// </summary>
    private static TaskNode ScriptTask(string id, params string[] dependsOn) => new()
    {
        Id = id,
        Directory = $"/fake/tasks/{id}",
        Description = $"fixture task {id}",
        DependsOn = dependsOn,
        Action = new ActionDefinition
        {
            Path = $"/fake/tasks/{id}/action.sh",
            Kind = ActionKind.Script
        },
        Guardrails =
        [
            new GuardrailDefinition
            {
                Name = "01-check",
                Path = $"/fake/tasks/{id}/guardrails/01-check.sh",
                Kind = ActionKind.Script
            }
        ]
    };

    /// <summary>
    /// Write a minimal disk plan folder. Each task entry is a <c>(id, taskJson)</c> pair;
    /// <c>taskJson</c> is written verbatim so callers can include future fields such as
    /// <c>integrationGate</c> that the current loader silently ignores.
    /// Each task gets one script action file and one script guardrail file (no sidecar).
    /// </summary>
    private static void WriteDiskPlan(
        string dir,
        string guardrailsJson,
        (string id, string taskJson)[] tasks)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "guardrails.json"), guardrailsJson);

        foreach ((string id, string taskJson) in tasks)
        {
            string taskDir = Path.Combine(dir, "tasks", id);
            Directory.CreateDirectory(Path.Combine(taskDir, "guardrails"));
            File.WriteAllText(Path.Combine(taskDir, "task.json"), taskJson);
            File.WriteAllText(Path.Combine(taskDir, "action.sh"), "#!/usr/bin/env bash\nexit 0");
            File.WriteAllText(Path.Combine(taskDir, "guardrails", "01-build.sh"), "exit 0");
        }
    }

    private static PlanDefinition LoadPlan(string dir)
    {
        PlanLoadResult result = new PlanLoader().Load(dir);
        Assert.NotNull(result.Plan);
        return result.Plan!;
    }
}
