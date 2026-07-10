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
///   GR2028 — multi-leaf or fan-in plan whose &lt;plan&gt;/guardrails/ folder is empty or holds only
///            a tautological check that does not re-run the integration set (error; re-homed GR2018,
///            SSOT §3.3, design-of-record 09-preflight-first-class)
///   GR2029 — a task still declares the retired integrationGate: true task kind (error, unconditional)
/// </summary>
public sealed class ParallelValidationGateTests : IDisposable
{
    // Diagnostic codes allocated for plan 08 M2 (not yet in DiagnosticCodes.cs).
    private const string Gr2015 = "GR2015"; // workspace not a git repository top-level (error)
    private const string Gr2016 = "GR2016"; // deep worktreeRoot + source risks MAX_PATH, Windows (warning)
    private const string Gr2028 = "GR2028"; // <plan>/guardrails/ folder empty/tautological on parallel topology (error)
    private const string Gr2029 = "GR2029"; // task declares the retired integrationGate: true key (error)

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
    // GR2028 — re-homed terminal-gate content rule: a multi-leaf/fan-in plan's
    // <plan>/guardrails/ folder must carry a real integration-set re-run
    // =========================================================================

    [Fact]
    public void MultiLeafPlan_WorktreeMode_NoGuardrailsFolder_ProducesGr2028_Error()
    {
        // SSOT §3.3, design-of-record 09-preflight-first-class, PO decision: the terminal-gate
        // obligation re-homed off the retired integrationGate task onto the plan-level
        // <plan>/guardrails/ folder, but the CONTENT teeth survive — a missing/empty folder still
        // fails. Fires ONLY in worktree mode (maxParallelism > 1), matching the retired
        // GR2017/GR2018's exact firing conditions.
        //
        // DAG:  01-root → 02-leaf-a  (leaf — no task depends on it)
        //               → 03-leaf-b  (leaf — no task depends on it)
        // Two leaves, no <plan>/guardrails/ folder at all, maxParallelism>1 → GR2028.
        string planDir = Path.Combine(_tempRoot, "gr2028-no-folder");
        WriteDiskPlan(
            planDir,
            guardrailsJson: "{ \"version\": 1, \"maxParallelism\": 3 }",
            tasks:
            [
                ("01-root", "{ \"description\": \"root\", \"dependsOn\": [] }"),
                ("02-leaf-a", "{ \"description\": \"leaf a\", \"dependsOn\": [\"01-root\"] }"),
                ("03-leaf-b", "{ \"description\": \"leaf b\", \"dependsOn\": [\"01-root\"] }")
            ]);

        PlanDefinition plan = LoadPlan(planDir);
        IReadOnlyList<Diagnostic> diagnostics = Validate(plan);

        Assert.Contains(diagnostics, d => d.Code == Gr2028 && d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void MultiLeafPlan_WorktreeMode_TautologicalGuardrailsFolder_ProducesGr2028_Error()
    {
        // A present <plan>/guardrails/ folder holding only a tautological "exit 0" check still fails
        // — GR2028 tests CONTENT, not mere folder non-emptiness (the precise gap GR2018 existed to
        // close before the re-home).
        string planDir = Path.Combine(_tempRoot, "gr2028-tautological-folder");
        WriteDiskPlan(
            planDir,
            guardrailsJson: "{ \"version\": 1, \"maxParallelism\": 3 }",
            tasks:
            [
                ("01-root", "{ \"description\": \"root\", \"dependsOn\": [] }"),
                ("02-leaf-a", "{ \"description\": \"leaf a\", \"dependsOn\": [\"01-root\"] }"),
                ("03-leaf-b", "{ \"description\": \"leaf b\", \"dependsOn\": [\"01-root\"] }")
            ],
            planGuardrailFiles:
            [
                ("01-noop.sh", "# catches: nothing — a tautological placeholder that verifies nothing\nexit 0\n")
            ]);

        PlanDefinition plan = LoadPlan(planDir);
        IReadOnlyList<Diagnostic> diagnostics = Validate(plan);

        Assert.Contains(diagnostics, d => d.Code == Gr2028 && d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void MultiLeafPlan_WorktreeMode_WithRealIntegrationReRun_ProducesNoGr2028()
    {
        // A <plan>/guardrails/ folder carrying a real whole-repo re-run (here: "dotnet test")
        // satisfies the content teeth — no GR2028.
        string planDir = Path.Combine(_tempRoot, "gr2028-real-check");
        WriteDiskPlan(
            planDir,
            guardrailsJson: "{ \"version\": 1, \"maxParallelism\": 3 }",
            tasks:
            [
                ("01-root", "{ \"description\": \"root\", \"dependsOn\": [] }"),
                ("02-leaf-a", "{ \"description\": \"leaf a\", \"dependsOn\": [\"01-root\"] }"),
                ("03-leaf-b", "{ \"description\": \"leaf b\", \"dependsOn\": [\"01-root\"] }")
            ],
            planGuardrailFiles:
            [
                ("01-build.sh", "# catches: the merged HEAD fails to build/test\ndotnet test\n")
            ]);

        PlanDefinition plan = LoadPlan(planDir);
        IReadOnlyList<Diagnostic> diagnostics = Validate(plan);

        Assert.DoesNotContain(diagnostics, d => d.Code == Gr2028);
    }

    [Fact]
    public void MultiLeafPlan_WorktreeMode_EchoStringMentioningBuildCommand_ProducesGr2028_Error()
    {
        // Issue #207 — the invocation-shape teeth. A terminal check that only `exit 0`s but carries a
        // NON-comment line merely MENTIONING a build command inside a string —
        // `echo "reminder: dotnet test should pass"` — invokes NOTHING, yet the pre-#207 bare-keyword
        // match credited it (the keyword survived comment-stripping because it was not a comment). The
        // tightened rule requires a real invocation shape (a command at a statement position, not an
        // echo/quoted-string argument), so this now correctly FAILS GR2028.
        string planDir = Path.Combine(_tempRoot, "gr2028-echo-string-bypass");
        WriteDiskPlan(
            planDir,
            guardrailsJson: "{ \"version\": 1, \"maxParallelism\": 3 }",
            tasks:
            [
                ("01-root", "{ \"description\": \"root\", \"dependsOn\": [] }"),
                ("02-leaf-a", "{ \"description\": \"leaf a\", \"dependsOn\": [\"01-root\"] }"),
                ("03-leaf-b", "{ \"description\": \"leaf b\", \"dependsOn\": [\"01-root\"] }")
            ],
            planGuardrailFiles:
            [
                ("01-fake.sh",
                    "# catches: nothing real — a gameable placeholder that only MENTIONS a build command\n" +
                    "echo \"reminder: dotnet test should pass\"\n" +
                    "exit 0\n")
            ]);

        PlanDefinition plan = LoadPlan(planDir);
        IReadOnlyList<Diagnostic> diagnostics = Validate(plan);

        Assert.Contains(diagnostics, d => d.Code == Gr2028 && d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void MultiLeafPlan_WorktreeMode_PipedBuildInvocation_ProducesNoGr2028()
    {
        // Issue #207 companion: a REAL invocation that is not at column-0 line-start — piped/chained —
        // still counts. `dotnet build && dotnet test 2>&1 | tee log` runs the command at a statement
        // position within the pipeline, so the tightened rule credits it (no GR2028). This pins that
        // the #207 hardening rejects the echo-mention bypass WITHOUT rejecting legitimate shell shapes.
        string planDir = Path.Combine(_tempRoot, "gr2028-piped-invocation");
        WriteDiskPlan(
            planDir,
            guardrailsJson: "{ \"version\": 1, \"maxParallelism\": 3 }",
            tasks:
            [
                ("01-root", "{ \"description\": \"root\", \"dependsOn\": [] }"),
                ("02-leaf-a", "{ \"description\": \"leaf a\", \"dependsOn\": [\"01-root\"] }"),
                ("03-leaf-b", "{ \"description\": \"leaf b\", \"dependsOn\": [\"01-root\"] }")
            ],
            planGuardrailFiles:
            [
                ("01-build.sh", "# catches: the merged HEAD fails to build/test\ndotnet build && dotnet test 2>&1 | tee build.log\n")
            ]);

        PlanDefinition plan = LoadPlan(planDir);
        IReadOnlyList<Diagnostic> diagnostics = Validate(plan);

        Assert.DoesNotContain(diagnostics, d => d.Code == Gr2028);
    }

    [Fact]
    public void MultiLeafPlan_SerialMode_NoGuardrailsFolder_ProducesNoGr2028()
    {
        // PO decision: a SERIAL run (maxParallelism == 1) merges no parallel branches, so the
        // terminal gate has nothing to verify and the content-teeth requirement does not apply. The
        // same no-folder multi-leaf plan that fails in worktree mode must produce NO GR2028 here.
        string planDir = Path.Combine(_tempRoot, "gr2028-serial-no-folder");
        WriteDiskPlan(
            planDir,
            guardrailsJson: "{ \"version\": 1, \"maxParallelism\": 1 }",
            tasks:
            [
                ("01-root", "{ \"description\": \"root\", \"dependsOn\": [] }"),
                ("02-leaf-a", "{ \"description\": \"leaf a\", \"dependsOn\": [\"01-root\"] }"),
                ("03-leaf-b", "{ \"description\": \"leaf b\", \"dependsOn\": [\"01-root\"] }")
            ]);

        PlanDefinition plan = LoadPlan(planDir);
        IReadOnlyList<Diagnostic> diagnostics = Validate(plan);

        Assert.DoesNotContain(diagnostics, d => d.Code == Gr2028);
    }

    [Fact]
    public void FanInPlan_WorktreeMode_NoGuardrailsFolder_ProducesGr2028_Error()
    {
        // SSOT §3.3, PO decision: a fan-in task (≥2 upstreams → a real union merge) also carries the
        // terminal-gate obligation, matching the retired GR2017's fan-in firing condition.
        //
        // DAG:  01-a → 03-fanin  (2 upstreams → fan-in)
        //       02-b ↗
        // One leaf but it IS the fan-in; no <plan>/guardrails/ folder, maxParallelism>1 → GR2028.
        string planDir = Path.Combine(_tempRoot, "gr2028-fanin-no-folder");
        WriteDiskPlan(
            planDir,
            guardrailsJson: "{ \"version\": 1, \"maxParallelism\": 3 }",
            tasks:
            [
                ("01-a", "{ \"description\": \"task a\", \"dependsOn\": [] }"),
                ("02-b", "{ \"description\": \"task b\", \"dependsOn\": [] }"),
                ("03-fanin", "{ \"description\": \"fan-in\", \"dependsOn\": [\"01-a\", \"02-b\"] }")
            ]);

        PlanDefinition plan = LoadPlan(planDir);
        IReadOnlyList<Diagnostic> diagnostics = Validate(plan);

        Assert.Contains(diagnostics, d => d.Code == Gr2028 && d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void FanInPlan_SerialMode_NoGuardrailsFolder_ProducesNoGr2028()
    {
        // PO decision: a SERIAL run never executes branches in parallel, so even a fan-in topology
        // merges no concurrent worktrees. The same fan-in no-folder plan that fails in worktree mode
        // must produce NO GR2028 here.
        string planDir = Path.Combine(_tempRoot, "gr2028-fanin-serial-no-folder");
        WriteDiskPlan(
            planDir,
            guardrailsJson: "{ \"version\": 1, \"maxParallelism\": 1 }",
            tasks:
            [
                ("01-a", "{ \"description\": \"task a\", \"dependsOn\": [] }"),
                ("02-b", "{ \"description\": \"task b\", \"dependsOn\": [] }"),
                ("03-fanin", "{ \"description\": \"fan-in\", \"dependsOn\": [\"01-a\", \"02-b\"] }")
            ]);

        PlanDefinition plan = LoadPlan(planDir);
        IReadOnlyList<Diagnostic> diagnostics = Validate(plan);

        Assert.DoesNotContain(diagnostics, d => d.Code == Gr2028);
    }

    [Fact]
    public void MultiLeafPlan_WorktreeMode_ContentTopicOnlyUnionCheck_ProducesGr2028_Error()
    {
        // Issue #343 — the doctrine-tightening. A terminal <plan>/guardrails/ check that greps ONLY a
        // content topic ("if the shared file mentions <topic>, verify it's real") — with NO git
        // conflict-marker token and NO build/test invocation — is a textbook union-SAFE conditional (the
        // exact shape the SKILL.md doctrine used to present as an equally-valid standalone GR2028 shape),
        // yet it does NOT satisfy GR2028: the union-safe conditional can never FAIL when a merge DROPPED a
        // contribution entirely, so it certifies nothing about union soundness on its own. GR2028 must
        // reject it, and the improved message (D2) must teach the two accepted forms.
        string planDir = Path.Combine(_tempRoot, "gr2028-content-only-union");
        WriteDiskPlan(
            planDir,
            guardrailsJson: "{ \"version\": 1, \"maxParallelism\": 3 }",
            tasks:
            [
                ("01-root", "{ \"description\": \"root\", \"dependsOn\": [] }"),
                ("02-leaf-a", "{ \"description\": \"leaf a\", \"dependsOn\": [\"01-root\"] }"),
                ("03-leaf-b", "{ \"description\": \"leaf b\", \"dependsOn\": [\"01-root\"] }")
            ],
            planGuardrailFiles:
            [
                ("01-contribution-present.ps1",
                    "# catches: the shared file dropped its risk_tracking contribution\n" +
                    "$p = Join-Path $env:GUARDRAILS_WORKSPACE 'out/shared.md'\n" +
                    "if (-not (Test-Path $p)) { exit 0 }\n" +
                    "$content = Get-Content -Raw -Path $p\n" +
                    "if ($content -match 'risk_tracking') {\n" +
                    "    if ($content -notmatch 'risk_tracking:\\s*enabled') { Write-Output 'risk_tracking present only as mention'; exit 1 }\n" +
                    "}\n" +
                    "exit 0\n")
            ]);

        PlanDefinition plan = LoadPlan(planDir);
        IReadOnlyList<Diagnostic> diagnostics = Validate(plan);

        Diagnostic gr2028 = Assert.Single(
            diagnostics, d => d.Code == Gr2028 && d.Severity == DiagnosticSeverity.Error);
        // The improved reason (issue #343, D2) names the two accepted forms and calls a content grep additive.
        Assert.Contains("does NOT satisfy GR2028", gr2028.Message, StringComparison.Ordinal);
        Assert.Contains("additive", gr2028.Message, StringComparison.Ordinal);
        Assert.Contains("conflict-marker-freedom", gr2028.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MultiLeafPlan_WorktreeMode_AnchoredConflictMarkerCheck_ProducesNoGr2028()
    {
        // Issue #343 — form (2) that DOES satisfy GR2028: a git-conflict-marker-freedom check. The
        // canonical line-anchored ours/theirs scan (examples/parallel-hello's shape) is the zero-toolchain
        // union-soundness proof; the credit regex `<{7}|>{7}` still matches its labelled tokens.
        string planDir = Path.Combine(_tempRoot, "gr2028-anchored-conflict-marker");
        WriteDiskPlan(
            planDir,
            guardrailsJson: "{ \"version\": 1, \"maxParallelism\": 3 }",
            tasks:
            [
                ("01-root", "{ \"description\": \"root\", \"dependsOn\": [] }"),
                ("02-leaf-a", "{ \"description\": \"leaf a\", \"dependsOn\": [\"01-root\"] }"),
                ("03-leaf-b", "{ \"description\": \"leaf b\", \"dependsOn\": [\"01-root\"] }")
            ],
            planGuardrailFiles:
            [
                ("01-union-clean.ps1",
                    "# catches: a union that left git conflict markers in the merged bytes\n" +
                    "$out = Join-Path $env:GUARDRAILS_WORKSPACE 'out'\n" +
                    "if (-not (Test-Path $out)) { exit 0 }\n" +
                    "foreach ($f in Get-ChildItem -Path $out -Filter *.txt -File) {\n" +
                    "    $content = Get-Content -Raw -Path $f.FullName\n" +
                    "    if ($content -match '(?m)^<<<<<<<' -or $content -match '(?m)^>>>>>>>') { Write-Output 'conflict markers'; exit 1 }\n" +
                    "}\n" +
                    "exit 0\n")
            ]);

        PlanDefinition plan = LoadPlan(planDir);
        IReadOnlyList<Diagnostic> diagnostics = Validate(plan);

        Assert.DoesNotContain(diagnostics, d => d.Code == Gr2028);
    }

    [Fact]
    public void MultiLeafPlan_WorktreeMode_BareEqualsOnlyConflictCheck_ProducesGr2028_Error()
    {
        // Issue #343 / #187 alignment — the regression guard for the ={7} drop. A guardrail whose ONLY
        // conflict evidence is the bare `=======` middle marker (no labelled ours/theirs token, no
        // build/test invocation) WAS credited before this change (the credit regex was `<{7}|={7}|>{7}`).
        // #187 retired the bare `=======` (it collides with setext underlines / `====` banners), so GR2028
        // dropped `={7}` from its credit regex to align validator with doctrine — this guardrail is now
        // (correctly) NOT credited and GR2028 fires. This test would FAIL on the pre-#343 code (the bare
        // `=======` was accepted), pinning the behavioural change.
        string planDir = Path.Combine(_tempRoot, "gr2028-bare-equals-only");
        WriteDiskPlan(
            planDir,
            guardrailsJson: "{ \"version\": 1, \"maxParallelism\": 3 }",
            tasks:
            [
                ("01-root", "{ \"description\": \"root\", \"dependsOn\": [] }"),
                ("02-leaf-a", "{ \"description\": \"leaf a\", \"dependsOn\": [\"01-root\"] }"),
                ("03-leaf-b", "{ \"description\": \"leaf b\", \"dependsOn\": [\"01-root\"] }")
            ],
            planGuardrailFiles:
            [
                ("01-bare-equals.ps1",
                    "# catches: a union that left conflict markers (bare separator only — retired by #187)\n" +
                    "$out = Join-Path $env:GUARDRAILS_WORKSPACE 'out'\n" +
                    "if (-not (Test-Path $out)) { exit 0 }\n" +
                    "foreach ($f in Get-ChildItem -Path $out -Filter *.txt -File) {\n" +
                    "    $content = Get-Content -Raw -Path $f.FullName\n" +
                    "    if ($content -match '=======') { Write-Output 'separator found'; exit 1 }\n" +
                    "}\n" +
                    "exit 0\n")
            ]);

        PlanDefinition plan = LoadPlan(planDir);
        IReadOnlyList<Diagnostic> diagnostics = Validate(plan);

        Assert.Contains(diagnostics, d => d.Code == Gr2028 && d.Severity == DiagnosticSeverity.Error);
    }

    // =========================================================================
    // GR2029 — a task still declares the retired integrationGate: true key
    // =========================================================================

    [Fact]
    public void LegacyIntegrationGateKey_WorktreeMode_ProducesGr2029_Error()
    {
        // SSOT §3.3, design-of-record 09-preflight-first-class: integrationGate:true is RETIRED with
        // no coexistence window. A plan that still declares it gets a HARD validation error — honest
        // over silent — even though the plan also has a valid multi-leaf topology.
        string planDir = Path.Combine(_tempRoot, "gr2029-legacy-key-worktree");
        WriteDiskPlan(
            planDir,
            guardrailsJson: "{ \"version\": 1, \"maxParallelism\": 3 }",
            tasks:
            [
                ("01-a", "{ \"description\": \"task a\", \"dependsOn\": [] }"),
                ("02-b", "{ \"description\": \"task b\", \"dependsOn\": [] }"),
                ("03-gate",
                    "{ \"description\": \"integration gate\", \"dependsOn\": [\"01-a\", \"02-b\"], \"integrationGate\": true }")
            ]);

        PlanDefinition plan = LoadPlan(planDir);
        IReadOnlyList<Diagnostic> diagnostics = Validate(plan);

        Assert.Contains(diagnostics, d => d.Code == Gr2029 && d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void LegacyIntegrationGateKey_SerialMode_ProducesGr2029_Error()
    {
        // Unlike the retired worktree-only GR2017/GR2018 rules it replaces, GR2029 fires
        // UNCONDITIONALLY — the retired key is a hard error regardless of maxParallelism, because the
        // key itself (not its runtime effect) is what's retired.
        string planDir = Path.Combine(_tempRoot, "gr2029-legacy-key-serial");
        WriteDiskPlan(
            planDir,
            guardrailsJson: "{ \"version\": 1, \"maxParallelism\": 1 }",
            tasks:
            [
                ("01-a", "{ \"description\": \"task a\", \"dependsOn\": [] }"),
                ("02-gate",
                    "{ \"description\": \"integration gate\", \"dependsOn\": [\"01-a\"], \"integrationGate\": true }")
            ]);

        PlanDefinition plan = LoadPlan(planDir);
        IReadOnlyList<Diagnostic> diagnostics = Validate(plan);

        Assert.Contains(diagnostics, d => d.Code == Gr2029 && d.Severity == DiagnosticSeverity.Error);
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
    /// <paramref name="planGuardrailFiles"/>, when non-null, materialises the plan-level
    /// <c>&lt;plan&gt;/guardrails/</c> folder (siblings of <c>tasks/</c>) with the given
    /// <c>(fileName, content)</c> entries — pass <c>[]</c> for an explicitly empty folder, or
    /// omit/pass <c>null</c> to leave the folder absent entirely.
    /// </summary>
    private static void WriteDiskPlan(
        string dir,
        string guardrailsJson,
        (string id, string taskJson)[] tasks,
        (string fileName, string content)[]? planGuardrailFiles = null)
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

        if (planGuardrailFiles is not null)
        {
            string planGuardrailsDir = Path.Combine(dir, "guardrails");
            Directory.CreateDirectory(planGuardrailsDir);
            foreach ((string fileName, string content) in planGuardrailFiles)
            {
                File.WriteAllText(Path.Combine(planGuardrailsDir, fileName), content);
            }
        }
    }

    private static PlanDefinition LoadPlan(string dir)
    {
        PlanLoadResult result = new PlanLoader().Load(dir);
        Assert.NotNull(result.Plan);
        return result.Plan!;
    }
}
