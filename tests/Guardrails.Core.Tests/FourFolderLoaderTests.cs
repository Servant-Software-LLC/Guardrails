using Guardrails.Core.Loading;
using Guardrails.Core.Model;

namespace Guardrails.Core.Tests;

/// <summary>
/// RED tests for preflights-impl deliverable 2 — the four-folder loader/validator (design-of-record
/// 09-preflight-first-class, SSOT §1/§3.3/§4). Preflights and guardrails become first-class at TWO
/// scopes, mirrored by four folders:
///   PLAN-LEVEL:  <c>&lt;plan&gt;/preflights/</c> (Full Flight Checks) + <c>&lt;plan&gt;/guardrails/</c> (Terminal Gate)
///   TASK-LEVEL:  <c>tasks/&lt;id&gt;/preflights/</c> (JIT dependency-delivery) + <c>tasks/&lt;id&gt;/guardrails/</c> (postconditions, exists today)
///
/// Every test drives the EXISTING public load/validate API (<see cref="PlanLoader"/> +
/// <see cref="PlanValidator"/>) over on-disk fixtures and asserts on <see cref="Diagnostic.Code"/> —
/// referencing the GR2027+ constants deliverable 5 allocated in <see cref="DiagnosticCodes"/>. The
/// tests COMPILE against the current surface but FAIL on the current validator, which does not yet
/// parse the four folders nor emit the new diagnostics (TDD red). Two no-regression PINS
/// (<see cref="MalformedDeclaration_TaskGuardrailsFolder_MalformedSidecar_EmitsGr1002"/> and
/// <see cref="ScopeIntegrationTag_StillParses_AndDrivesUnionFormingFanIn"/>) are green on both current
/// and new code by design — they guard behavior the four-folder change must NOT break.
///
/// Tagged Category=Preflights (class-level, inherited by every test case) so the deliverable-2
/// per-project green baseline can exclude these deliberately-red tests via
/// <c>--filter "Category!=Preflights"</c>.
/// </summary>
[Trait("Category", "Preflights")]
public sealed class FourFolderLoaderTests : IDisposable
{
    private readonly string _tempRoot;

    public FourFolderLoaderTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "gr-fourfolder-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);

        // A .git directory so the workspace (each plan folder's parent = _tempRoot, per the default
        // Workspace "..") counts as a git repository. Worktree-mode fixtures (maxParallelism > 1) must
        // not trip GR2015 (workspace-not-a-git-repo). No git process is ever run — the validator only
        // checks for a .git entry on disk (PlanValidator.IsInsideGitRepo).
        Directory.CreateDirectory(Path.Combine(_tempRoot, ".git"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); }
        catch (IOException) { }
    }

    // =========================================================================
    // Scenario 1 — a plan with ALL FOUR folders validates CLEAN
    // =========================================================================

    [Fact]
    public void AllFourFolders_MultiLeafPlan_ValidatesClean()
    {
        // A multi-leaf plan (two leaves) in worktree mode with a git workspace. Under the CURRENT
        // validator this fixture is NOT clean: a multi-leaf worktree plan with no integrationGate:true
        // sink emits GR2017 (MissingIntegrationGate) — the very error the four-folder model retires.
        // Under the new loader GR2017 is gone and the terminal <plan>/guardrails/ folder carries the
        // whole-repo re-run, so a plan with all four folders validates clean (no errors).
        string planDir = NewPlan("all-four-clean", maxParallelism: 3);

        WriteTask(planDir, "01-root");
        WriteTask(planDir, "02-leaf-a", "01-root");
        WriteTask(planDir, "03-leaf-b", "01-root");

        // <plan>/preflights/ — a plan-level "Full Flight Check" (deterministic byte/exit check).
        WriteGuardrail(Path.Combine(planDir, "preflights"), "01-baseline",
            "dotnet test Guardrails.sln --nologo --filter Category=Baseline\nexit $LASTEXITCODE",
            includeCatches: true, scope: null);

        // <plan>/guardrails/ — the terminal gate: a REAL integration-set re-run (whole suite), tagged
        // scope:"integration" as the "counts toward the terminal gate" marker. Non-tautological.
        WriteGuardrail(Path.Combine(planDir, "guardrails"), "01-full-suite",
            "dotnet test Guardrails.sln --nologo\nexit $LASTEXITCODE",
            includeCatches: true, scope: "integration");

        // tasks/<id>/preflights/ — a JIT task-level preflight on a consumer of 01-root.
        WriteGuardrail(Path.Combine(planDir, "tasks", "02-leaf-a", "preflights"), "01-dep-delivered",
            "dotnet build --nologo\nexit $LASTEXITCODE",
            includeCatches: true, scope: null);

        // (tasks/<id>/guardrails/ — the fourth folder — is written for every task by WriteTask.)

        IReadOnlyList<Diagnostic> diagnostics = Diagnostics(planDir);

        // FAILS today: GR2017 (an error) is present. Passes once GR2017 is retired and the four
        // folders are understood by the loader.
        Assert.DoesNotContain(diagnostics, d => d.Severity == DiagnosticSeverity.Error);
    }

    // =========================================================================
    // Scenario 2 — a malformed declaration in EACH of the four folders yields the
    // expected GR2027+ code. The uniform malformation for the three NEW folders is a
    // guardrail file missing its required `catches:` comment (GR2027); each fixture
    // ISOLATES the malformation to exactly one folder so the diagnostic is attributable
    // to that folder being parsed. The fourth (pre-existing) folder is pinned separately.
    // =========================================================================

    [Fact]
    public void MalformedDeclaration_PlanPreflightsFolder_MissingCatches_EmitsGr2027()
    {
        // A <plan>/preflights/ guardrail file with NO `catches:` comment must emit GR2027 under the
        // new loader (SSOT §4). The current loader ignores plan-level folders entirely, so the code is
        // absent today — the Contains fails (TDD red) until deliverable 2 parses the folder.
        string planDir = NewPlan("plan-preflight-nocatches", maxParallelism: 1);
        WriteTask(planDir, "01-only");

        WriteGuardrail(Path.Combine(planDir, "preflights"), "01-preflight-no-catches",
            "dotnet build --nologo\nexit $LASTEXITCODE",
            includeCatches: false, scope: null);

        IReadOnlyList<Diagnostic> diagnostics = Diagnostics(planDir);

        Assert.Contains(diagnostics, d => d.Code == DiagnosticCodes.GuardrailMissingCatches);
    }

    [Fact]
    public void MalformedDeclaration_PlanGuardrailsFolder_MissingCatches_EmitsGr2027()
    {
        // A <plan>/guardrails/ guardrail file with NO `catches:` comment must emit GR2027. Single-leaf
        // plan, so the re-homed content-teeth rule does not apply — the ONLY defect is the missing
        // catches declaration. Absent on current code (folder unparsed) → red.
        string planDir = NewPlan("plan-guardrail-nocatches", maxParallelism: 1);
        WriteTask(planDir, "01-only");

        WriteGuardrail(Path.Combine(planDir, "guardrails"), "01-terminal-no-catches",
            "dotnet test Guardrails.sln --nologo\nexit $LASTEXITCODE",
            includeCatches: false, scope: null);

        IReadOnlyList<Diagnostic> diagnostics = Diagnostics(planDir);

        Assert.Contains(diagnostics, d => d.Code == DiagnosticCodes.GuardrailMissingCatches);
    }

    [Fact]
    public void MalformedDeclaration_TaskPreflightsFolder_MissingCatches_EmitsGr2027()
    {
        // A tasks/<id>/preflights/ guardrail file with NO `catches:` comment must emit GR2027. The
        // current loader reads only tasks/<id>/guardrails/, never the sibling preflights/ folder, so
        // the code is absent today → red until the task-preflight folder is parsed.
        string planDir = NewPlan("task-preflight-nocatches", maxParallelism: 1);
        WriteTask(planDir, "01-only");

        WriteGuardrail(Path.Combine(planDir, "tasks", "01-only", "preflights"), "01-taskpre-no-catches",
            "dotnet build --nologo\nexit $LASTEXITCODE",
            includeCatches: false, scope: null);

        IReadOnlyList<Diagnostic> diagnostics = Diagnostics(planDir);

        Assert.Contains(diagnostics, d => d.Code == DiagnosticCodes.GuardrailMissingCatches);
    }

    [Fact]
    public void MalformedDeclaration_TaskGuardrailsFolder_MalformedSidecar_EmitsGr1002()
    {
        // The fourth folder — the pre-existing tasks/<id>/guardrails/ — is already parsed today; the
        // four-folder loader must NOT regress it. A guardrail whose .json sidecar is invalid JSON
        // yields the existing GR1002 (InvalidJson). No-regression PIN: green on current AND new code,
        // proving all four folders (including the original one) stay validated by the same parser.
        string planDir = NewPlan("task-guardrails-badsidecar", maxParallelism: 1);
        WriteTask(planDir, "01-only");

        // WriteTask already wrote a valid 01-verify.ps1; add a malformed sidecar with the same basename.
        string grDir = Path.Combine(planDir, "tasks", "01-only", "guardrails");
        File.WriteAllText(Path.Combine(grDir, "01-verify.json"), "{ this is : not valid json ");

        IReadOnlyList<Diagnostic> diagnostics = Diagnostics(planDir);

        Assert.Contains(diagnostics, d => d.Code == DiagnosticCodes.InvalidJson);
    }

    // =========================================================================
    // Scenario 3 — a multi-leaf/fan-in plan whose <plan>/guardrails/ folder is EMPTY
    // FAILS validation (the re-homed GR2018 rule).
    // =========================================================================

    [Fact]
    public void MultiLeafPlan_EmptyTerminalFolder_FailsValidation()
    {
        // Re-homed GR2018: a multi-leaf/fan-in plan MUST carry a non-empty <plan>/guardrails/ terminal
        // folder that re-runs the integration set. An EMPTY terminal folder verifies nothing and FAILS
        // validation. Worktree mode + git workspace so the rule fires regardless of any serial/worktree
        // conditioning. The current code has no plan-level folder concept, so GR2028 is absent today → red.
        string planDir = NewPlan("empty-terminal", maxParallelism: 3);
        WriteTask(planDir, "01-root");
        WriteTask(planDir, "02-leaf-a", "01-root");
        WriteTask(planDir, "03-leaf-b", "01-root");

        // The terminal folder exists on disk but is EMPTY.
        Directory.CreateDirectory(Path.Combine(planDir, "guardrails"));

        IReadOnlyList<Diagnostic> diagnostics = Diagnostics(planDir);

        Assert.Contains(diagnostics,
            d => d.Code == DiagnosticCodes.PlanGuardrailsMissingIntegrationReRun
                 && d.Severity == DiagnosticSeverity.Error);
    }

    // =========================================================================
    // Scenario 4 — a multi-leaf/fan-in plan whose <plan>/guardrails/ folder carries
    // ONLY a tautological `exit 0` file FAILS validation (CONTENT teeth, B3 — the most
    // gameable acceptance criterion, covered distinctly from the empty-folder case).
    // =========================================================================

    [Fact]
    public void MultiLeafPlan_TautologicalTerminalFolder_FailsValidation()
    {
        // B3 content teeth: a <plan>/guardrails/ that is NON-EMPTY but carries ONLY a tautological
        // `exit 0` file verifies nothing — it must FAIL validation exactly like the empty folder. The
        // re-homed GR2018 rule is CONTENT teeth, not "non-empty". The tautological file even carries a
        // valid `catches:` comment AND a scope:"integration" marker, so ONLY genuine content inspection
        // (not a presence/marker check) reddens it — the strongest form of this gate.
        string planDir = NewPlan("tautological-terminal", maxParallelism: 3);
        WriteTask(planDir, "01-root");
        WriteTask(planDir, "02-leaf-a", "01-root");
        WriteTask(planDir, "03-leaf-b", "01-root");

        WriteGuardrail(Path.Combine(planDir, "guardrails"), "01-tautological-gate",
            "exit 0",
            includeCatches: true, scope: "integration");

        IReadOnlyList<Diagnostic> diagnostics = Diagnostics(planDir);

        Assert.Contains(diagnostics,
            d => d.Code == DiagnosticCodes.PlanGuardrailsMissingIntegrationReRun
                 && d.Severity == DiagnosticSeverity.Error);
    }

    // =========================================================================
    // Scenario 4b — a multi-leaf/fan-in plan whose <plan>/guardrails/ folder carries ONLY a
    // genuine UNION-INVARIANT check (a git conflict-marker scan, no build/test tool token)
    // validates CLEAN — GR2028's second acceptance form (SSOT §3.3): "a whole-repo build /
    // full suite / a union invariant" are three equally valid forms, but only the first two
    // were implemented until now. Plans with no build/test tool to invoke at all (e.g. a
    // portable, zero-toolchain demo) rely on this form as their only honest integration check.
    // =========================================================================

    [Fact]
    public void MultiLeafPlan_UnionInvariantTerminalFolder_ValidatesClean()
    {
        string planDir = NewPlan("union-invariant-terminal", maxParallelism: 3);
        WriteTask(planDir, "01-root");
        WriteTask(planDir, "02-leaf-a", "01-root");
        WriteTask(planDir, "03-leaf-b", "01-root");

        // The canonical union-invariant shape (matches examples/parallel-hello's reference
        // fixture): no build/test tool anywhere, only a conflict-marker scan over merged output.
        WriteGuardrail(Path.Combine(planDir, "guardrails"), "01-union-clean",
            """
            $out = "out"
            if (-not (Test-Path $out)) { exit 0 }
            foreach ($f in Get-ChildItem -Path $out -Filter *.txt -File) {
                $content = Get-Content -Raw -Path $f.FullName
                if ($content -match '(?m)^<<<<<<<' -or $content -match '(?m)^>>>>>>>') {
                    Write-Output ($f.Name + " contains git conflict markers")
                    exit 1
                }
            }
            exit 0
            """,
            includeCatches: true, scope: "integration");

        IReadOnlyList<Diagnostic> diagnostics = Diagnostics(planDir);

        Assert.DoesNotContain(diagnostics, d => d.Code == DiagnosticCodes.PlanGuardrailsMissingIntegrationReRun);
    }

    // =========================================================================
    // Scenario 4c — a terminal folder that only NAMES conflict markers in a COMMENT (never a
    // real check) still FAILS validation — comment-stripping discipline applies to the
    // union-invariant form exactly as it already does to the build/test-command form.
    // =========================================================================

    [Fact]
    public void MultiLeafPlan_ConflictMarkerOnlyInComment_StillFailsValidation()
    {
        string planDir = NewPlan("conflict-marker-comment-only", maxParallelism: 3);
        WriteTask(planDir, "01-root");
        WriteTask(planDir, "02-leaf-a", "01-root");
        WriteTask(planDir, "03-leaf-b", "01-root");

        WriteGuardrail(Path.Combine(planDir, "guardrails"), "01-fake-union-gate",
            """
            # This guardrail is meant to check for <<<<<<< conflict markers but doesn't yet.
            exit 0
            """,
            includeCatches: true, scope: "integration");

        IReadOnlyList<Diagnostic> diagnostics = Diagnostics(planDir);

        Assert.Contains(diagnostics,
            d => d.Code == DiagnosticCodes.PlanGuardrailsMissingIntegrationReRun
                 && d.Severity == DiagnosticSeverity.Error);
    }

    // =========================================================================
    // Scenario 5 — a plan STILL declaring the old integrationGate:true task kind yields
    // a HARD validation error (the retired-key GR2027+ code).
    // =========================================================================

    [Fact]
    public void LegacyIntegrationGateKey_IsHardValidationError()
    {
        // Retirement: a plan that STILL declares the old `integrationGate: true` task kind gets a HARD
        // validation error (GR2029) — no coexistence window. The current validator parses
        // integrationGate:true benignly (it was the terminal-sink marker), so it emits no such hard
        // error today → red until deliverable 2 rejects the legacy key.
        string planDir = NewPlan("legacy-integrationgate", maxParallelism: 1);
        WriteTask(planDir, "01-work");
        WriteLegacyGateTask(planDir, "02-gate", "01-work"); // task.json carries "integrationGate": true

        IReadOnlyList<Diagnostic> diagnostics = Diagnostics(planDir);

        Assert.Contains(diagnostics,
            d => d.Code == DiagnosticCodes.RetiredIntegrationGateKey
                 && d.Severity == DiagnosticSeverity.Error);
    }

    // =========================================================================
    // Scenario 6 — the existing scope:"integration" per-union tag STILL parses and
    // drives a union-forming fixture (no regression; §4.3 is unchanged).
    // =========================================================================

    [Fact]
    public void ScopeIntegrationTag_StillParses_AndDrivesUnionFormingFanIn()
    {
        // No-regression PIN: only the terminal-SINK obligation moved to the folder; the §4.3
        // scope:"integration" per-union tag is KEPT. A union-forming fan-in fixture (03-fanin has two
        // upstreams) with a scope:"integration" guardrail must still parse — GuardrailDefinition.Scope
        // surfaces "integration" — and must NOT be rejected as an invalid scope value (GR2021). Green
        // on current AND new code by design.
        string planDir = NewPlan("scope-integration-fanin", maxParallelism: 1);
        WriteTask(planDir, "01-a");
        WriteTask(planDir, "02-b");
        WriteTask(planDir, "03-fanin", "01-a", "02-b"); // fan-in: 2 upstreams => a union point

        // The §4.3 per-union integration guardrail on the fan-in task.
        WriteGuardrail(Path.Combine(planDir, "tasks", "03-fanin", "guardrails"), "02-integration-suite",
            "dotnet test Guardrails.sln --nologo\nexit $LASTEXITCODE",
            includeCatches: true, scope: "integration");

        // A terminal folder with teeth so the fan-in plan is otherwise well-formed under the new loader
        // (invisible to the current loader). Keeps this pin focused on the scope tag, not the re-home.
        WriteGuardrail(Path.Combine(planDir, "guardrails"), "01-full-suite",
            "dotnet test Guardrails.sln --nologo\nexit $LASTEXITCODE",
            includeCatches: true, scope: "integration");

        (PlanDefinition? plan, IReadOnlyList<Diagnostic> diagnostics) = LoadAndValidate(planDir);

        Assert.NotNull(plan);
        Assert.Contains(
            plan!.Tasks.SelectMany(t => t.Guardrails),
            g => string.Equals(g.Scope, "integration", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(diagnostics, d => d.Code == DiagnosticCodes.InvalidGuardrailScopeValue);
    }

    // =========================================================================
    // Fixture helpers
    // =========================================================================

    /// <summary>
    /// Load then validate a plan folder, returning the loaded plan plus the COMBINED diagnostics from
    /// both phases. Combining is deliberate: a four-folder defect may surface at load time (structural:
    /// missing catches, bad sidecar) or at validate time (semantic: the re-homed terminal rule, the
    /// legacy key), and a test should not care which phase deliverable 2 chooses. FakeExecutableProbe.All
    /// resolves every interpreter so no interpreter-resolution diagnostics leak into the assertions.
    /// </summary>
    private static (PlanDefinition? Plan, IReadOnlyList<Diagnostic> Diagnostics) LoadAndValidate(string planDir)
    {
        PlanLoadResult load = new PlanLoader().Load(planDir);
        var all = new List<Diagnostic>(load.Diagnostics);
        if (load.Plan is not null)
        {
            all.AddRange(new PlanValidator(FakeExecutableProbe.All).Validate(load.Plan));
        }

        return (load.Plan, all);
    }

    private static IReadOnlyList<Diagnostic> Diagnostics(string planDir) => LoadAndValidate(planDir).Diagnostics;

    /// <summary>Create a plan folder with a minimal <c>guardrails.json</c> (version 1 + maxParallelism).</summary>
    private string NewPlan(string name, int maxParallelism)
    {
        string planDir = Path.Combine(_tempRoot, name);
        Directory.CreateDirectory(planDir);
        File.WriteAllText(Path.Combine(planDir, "guardrails.json"),
            "{ \"version\": 1, \"maxParallelism\": " + maxParallelism + " }");
        return planDir;
    }

    /// <summary>
    /// Write a task folder: <c>task.json</c> (with any dependsOn), a script action, and one valid
    /// script guardrail in <c>tasks/&lt;id&gt;/guardrails/</c> (with a catches comment).
    /// </summary>
    private static void WriteTask(string planDir, string id, params string[] dependsOn)
    {
        WriteTaskManifest(planDir, id, dependsOn, integrationGate: false);
    }

    /// <summary>
    /// Write a task folder that STILL declares the retired <c>integrationGate: true</c> key — the
    /// legacy shape scenario 5 must reject.
    /// </summary>
    private static void WriteLegacyGateTask(string planDir, string id, params string[] dependsOn)
    {
        WriteTaskManifest(planDir, id, dependsOn, integrationGate: true);
    }

    private static void WriteTaskManifest(string planDir, string id, string[] dependsOn, bool integrationGate)
    {
        string taskDir = Path.Combine(planDir, "tasks", id);
        string grDir = Path.Combine(taskDir, "guardrails");
        Directory.CreateDirectory(grDir);

        string deps = string.Join(", ", dependsOn.Select(d => "\"" + d + "\""));
        string gate = integrationGate ? ", \"integrationGate\": true" : string.Empty;
        File.WriteAllText(Path.Combine(taskDir, "task.json"),
            "{ \"description\": \"" + id + "\", \"dependsOn\": [" + deps + "]" + gate + " }");
        File.WriteAllText(Path.Combine(taskDir, "action.ps1"), "exit 0");

        WriteGuardrail(grDir, "01-verify", "dotnet build --nologo\nexit $LASTEXITCODE",
            includeCatches: true, scope: null);
    }

    /// <summary>
    /// Write a script guardrail (<c>&lt;name&gt;.ps1</c>) into <paramref name="folderDir"/>, creating the
    /// folder if needed. When <paramref name="includeCatches"/> is true the file opens with a
    /// <c>catches:</c> comment (SSOT §4); when false the declaration is malformed (GR2027). A non-null
    /// <paramref name="scope"/> writes a metadata sidecar declaring that scope.
    /// </summary>
    private static void WriteGuardrail(string folderDir, string name, string body, bool includeCatches, string? scope)
    {
        Directory.CreateDirectory(folderDir);

        string prefix = includeCatches ? "# catches: " + name + " - what wrong implementation this catches\n" : string.Empty;
        File.WriteAllText(Path.Combine(folderDir, name + ".ps1"), prefix + body + "\n");

        if (scope is not null)
        {
            File.WriteAllText(Path.Combine(folderDir, name + ".json"), "{ \"scope\": \"" + scope + "\" }");
        }
    }
}
