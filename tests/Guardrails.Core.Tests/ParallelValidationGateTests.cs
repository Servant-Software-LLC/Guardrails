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
    public void NonGitWorkspace_ProducesGr2015_Error()
    {
        // SSOT §1, plan 08 §1: the workspace must be a git repository top-level so the
        // harness can create worktrees (plan branch, segment worktrees). A workspace with
        // no .git must be rejected at validate time (and run pre-flight) → GR2015 error.
        // _tempRoot is a freshly created directory with no .git — guaranteed non-git root.
        PlanDefinition plan = InMemoryPlan(workspace: _tempRoot, ScriptTask("01-a"));

        IReadOnlyList<Diagnostic> diagnostics = Validate(plan);

        // FAILS on current code: ValidateWorkspaceIsGitRoot is not yet implemented.
        Assert.Contains(diagnostics, d => d.Code == Gr2015 && d.Severity == DiagnosticSeverity.Error);
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
    public void MultiLeafPlan_WithoutIntegrationGateSink_ProducesGr2017_Error()
    {
        // SSOT §3.3, plan 08 §3: a plan with ≥2 leaf tasks (tasks no other task depends on)
        // MUST declare exactly one integrationGate:true sink. The terminal gate is the sole
        // whole-repo soundness boundary for FF chains (per-hop re-verify is replaced by it);
        // missing it on a multi-leaf plan → GR2017 error.
        //
        // DAG:  01-root → 02-leaf-a  (leaf — no task depends on it)
        //               → 03-leaf-b  (leaf — no task depends on it)
        // Two leaves, no integrationGate task → GR2017.
        PlanDefinition plan = InMemoryPlan(
            ScriptTask("01-root"),
            ScriptTask("02-leaf-a", "01-root"),
            ScriptTask("03-leaf-b", "01-root"));

        IReadOnlyList<Diagnostic> diagnostics = Validate(plan);

        // FAILS on current code: ValidateIntegrationGatePresent is not yet implemented.
        Assert.Contains(diagnostics, d => d.Code == Gr2017 && d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void FanInPlan_WithoutIntegrationGateSink_ProducesGr2017_Error()
    {
        // SSOT §3.3, plan 08 §3: a plan with any fan-in task (a task with ≥2 upstreams,
        // which triggers a real union merge + re-verify path) MUST have an
        // integrationGate:true sink. The fan-in's per-union re-verify is not the whole-repo
        // check; the terminal gate must additionally exist.
        //
        // DAG:  01-a → 03-fanin  (2 upstreams → fan-in)
        //       02-b ↗
        // One leaf but it IS the fan-in; no integrationGate task → GR2017.
        PlanDefinition plan = InMemoryPlan(
            ScriptTask("01-a"),
            ScriptTask("02-b"),
            ScriptTask("03-fanin", "01-a", "02-b"));

        IReadOnlyList<Diagnostic> diagnostics = Validate(plan);

        // FAILS on current code: ValidateIntegrationGatePresent is not yet implemented.
        Assert.Contains(diagnostics, d => d.Code == Gr2017 && d.Severity == DiagnosticSeverity.Error);
    }

    // =========================================================================
    // GR2018 — integrationGate sink carries no scope:"integration" guardrail
    // =========================================================================

    [Fact]
    public void IntegrationGateSink_WithoutScopeIntegrationGuardrail_ProducesGr2018_Error()
    {
        // SSOT §3.3/§4.3, plan 08 §3: the integrationGate:true sink MUST carry at least
        // one guardrail declared scope:"integration". Without it the terminal gate verifies
        // nothing — an empty integration-guardrail set is not a sound soundness boundary → GR2018.
        //
        // Uses disk loading because task.json carries the integrationGate:true field, which
        // is currently unknown to the loader (RawTask/TaskNode). M2 adds the field; until
        // then the current loader silently ignores it and the validator cannot emit GR2018.
        // The gate task's guardrail has no scope:"integration" sidecar (M2 will also add
        // the scope field to RawGuardrailSidecar/GuardrailDefinition for the validator to read).
        string planDir = Path.Combine(_tempRoot, "gr2018-no-scope");
        WriteDiskPlan(
            planDir,
            guardrailsJson: "{ \"version\": 1 }",
            tasks:
            [
                ("01-a", "{ \"description\": \"task a\", \"dependsOn\": [] }"),
                ("02-b", "{ \"description\": \"task b\", \"dependsOn\": [] }"),
                // integrationGate:true — currently ignored by the loader; the guardrail
                // written by WriteDiskPlan has no scope:"integration" in any sidecar.
                ("03-gate",
                    "{ \"description\": \"integration gate\", \"dependsOn\": [\"01-a\", \"02-b\"], \"integrationGate\": true }")
            ]);

        PlanDefinition plan = LoadPlan(planDir);
        IReadOnlyList<Diagnostic> diagnostics = Validate(plan);

        // FAILS on current code: integrationGate is not exposed on TaskNode (loader ignores
        // it) and ValidateIntegrationGateNonEmpty is not yet implemented.
        Assert.Contains(diagnostics, d => d.Code == Gr2018 && d.Severity == DiagnosticSeverity.Error);
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
        new()
        {
            PlanDirectory = workspace,
            Workspace = workspace,
            Config = new RunConfig { Version = 1 },
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
