using Guardrails.Core.Execution;
using Guardrails.Core.Loading;
using Guardrails.Core.Model;

namespace Guardrails.Core.Tests;

/// <summary>
/// RED tests for plan 08 §4.3 / Decision 2 / Stage-2: the guardrail <c>scope</c> field and the
/// scope filter at union re-verify points, encoded BEFORE the scope filter is implemented.
///
/// Compile-error tests (reference <see cref="GuardrailScopeFilter"/>, which does not yet exist):
///   - IntegrationSet extraction (§4.3 integration-guardrail set)
///   - Scope filter at union: integration always runs, distant-local conditional, colliding-sibling unconditional
///   - B-3 split: a touched-files local-skip wrongly applied to a colliding sibling must make this test FAIL
///   - Terminal integrationGate sink runs the same integration set as intermediate union points
///
/// Runtime-failure test (compiles against current code, but unimplemented):
///   - Prompt-guardrail frontmatter scope (§4.2): scope not yet read from .prompt.md YAML frontmatter;
///     only the deterministic-sidecar path (ApplySidecar) is implemented.
/// </summary>
public sealed class GuardrailScopeTests : IDisposable
{
    private readonly string _tempRoot;

    public GuardrailScopeTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "gr-scope-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); }
        catch (IOException) { }
    }

    // =========================================================================
    // §4.1 — Sidecar scope key (deterministic guardrails)
    //
    // These compile AND pass against current code (sidecar scope was implemented
    // as part of plan 08 M2). They pin §4.1 as a regression floor.
    // =========================================================================

    [Fact]
    public void SidecarScope_Integration_SurfacedOnGuardrailDefinition()
    {
        // A deterministic guardrail's metadata sidecar declares scope:"integration".
        // The loader must surface it as GuardrailDefinition.Scope (SSOT §4.1/§4.3).
        string planDir = BuildDiskPlanWithSidecar(sidecarJson: """{ "scope": "integration" }""");

        PlanDefinition plan = LoadPlan(planDir);

        GuardrailDefinition g = Assert.Single(plan.Tasks[0].Guardrails);
        Assert.Equal("integration", g.Scope, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void SidecarScope_Local_SurfacedOnGuardrailDefinition()
    {
        // Explicit scope:"local" in the sidecar is preserved (lowercase-normalised).
        string planDir = BuildDiskPlanWithSidecar(sidecarJson: """{ "scope": "local" }""");

        PlanDefinition plan = LoadPlan(planDir);

        GuardrailDefinition g = Assert.Single(plan.Tasks[0].Guardrails);
        Assert.Equal("local", g.Scope, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void SidecarScope_Absent_IsNull()
    {
        // When the sidecar omits scope (or there is no sidecar), GuardrailDefinition.Scope
        // is null. Null is the "local" SEMANTIC default per SSOT §4.3; null is the
        // REPRESENTATION when neither the sidecar nor frontmatter declares a scope.
        string planDir = BuildDiskPlanWithSidecar(sidecarJson: null);

        PlanDefinition plan = LoadPlan(planDir);

        GuardrailDefinition g = Assert.Single(plan.Tasks[0].Guardrails);
        Assert.Null(g.Scope);
    }

    // =========================================================================
    // §4.2 — Prompt-guardrail frontmatter scope
    //
    // Compiles against current code but FAILS at runtime: the loader's LoadGuardrails
    // path does not yet parse YAML frontmatter for script guardrails (only ApplySidecar,
    // the deterministic-only sidecar path, is implemented). This test encodes the §4.2
    // contract BEFORE the frontmatter-parsing path is built.
    // =========================================================================

    [Fact]
    public void PromptFrontmatter_ScopeIntegration_SurfacedOnGuardrailDefinition()
    {
        // A prompt guardrail (.prompt.md) with YAML frontmatter declaring scope: integration
        // must surface GuardrailDefinition.Scope == "integration" (SSOT §4.2/§4.3).
        //
        // FAILS on current code: PlanLoader does not yet parse scope from .prompt.md frontmatter.
        // The ApplySidecar sidecar path (called only for ActionKind.Script) is the only scope
        // source implemented; prompt guardrails get no frontmatter parsing → Scope stays null.
        string planDir = BuildDiskPlanWithPromptGuardrail(promptContent: """
            ---
            description: full-suite integration check
            scope: integration
            ---
            Verify the test suite is green across the fully merged workspace.
            """);

        PlanDefinition plan = LoadPlan(planDir);

        GuardrailDefinition g = Assert.Single(plan.Tasks[0].Guardrails);
        // FAILS: g.Scope is currently null because frontmatter scope is not parsed.
        Assert.Equal("integration", g.Scope, StringComparer.OrdinalIgnoreCase);
    }

    // =========================================================================
    // Integration-guardrail set — GuardrailScopeFilter.IntegrationSet
    //
    // COMPILE ERROR: GuardrailScopeFilter does not yet exist (plan 08 §4.3 / Decision 2).
    // That compile failure IS the "fails on current code" signal for this section.
    // =========================================================================

    [Fact]
    public void IntegrationSet_MixedScopes_ReturnsOnlyIntegrationScopedGuardrails()
    {
        // GuardrailScopeFilter.IntegrationSet() must extract all guardrails declared
        // scope:"integration", excluding "local" and null-scope guardrails
        // (SSOT §4.3: "the run's integration-guardrail set = the union of all guardrails
        // declared scope:'integration' across the plan").
        //
        // COMPILE ERROR: GuardrailScopeFilter does not yet exist.
        var localG = MakeGuardrail("01-local", scope: "local");
        var integG = MakeGuardrail("02-integ", scope: "integration");
        var nullG  = MakeGuardrail("03-null",  scope: null);

        IReadOnlyList<GuardrailDefinition> set =
            GuardrailScopeFilter.IntegrationSet([localG, integG, nullG]);

        GuardrailDefinition only = Assert.Single(set);
        Assert.Same(integG, only);
    }

    [Fact]
    public void IntegrationSet_MultipleIntegrationGuardrails_ReturnsAllOfThem()
    {
        // When several guardrails are integration-scoped, the set must contain all of them.
        // Typical integration set: whole-repo build + whole test suite (SSOT §4.3).
        //
        // COMPILE ERROR: GuardrailScopeFilter does not yet exist.
        var buildG = MakeGuardrail("01-build", scope: "integration");
        var testG  = MakeGuardrail("02-tests", scope: "integration");
        var localG = MakeGuardrail("03-local", scope: null);

        IReadOnlyList<GuardrailDefinition> set =
            GuardrailScopeFilter.IntegrationSet([buildG, testG, localG]);

        Assert.Equal(2, set.Count);
        Assert.All(set, g => Assert.Equal("integration", g.Scope, StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void IntegrationSet_EmptyInput_ReturnsEmpty()
    {
        // An empty guardrail collection produces an empty integration set.
        // COMPILE ERROR: GuardrailScopeFilter does not yet exist.
        IReadOnlyList<GuardrailDefinition> set =
            GuardrailScopeFilter.IntegrationSet([]);

        Assert.Empty(set);
    }

    // =========================================================================
    // Scope filter at union points — GuardrailScopeFilter.ShouldRunAtUnion
    //
    // touchedByMerge: the CALLER pre-filters this to only those files the merge
    // touched that belong to the task owning this guardrail. The filter then:
    //   - integration → always include
    //   - local/null (colliding sibling) → always include (B-3, no touched-files skip)
    //   - local/null (distant, non-colliding) → include only if touchedByMerge is non-empty
    //
    // COMPILE ERROR on all of these: GuardrailScopeFilter does not yet exist.
    // =========================================================================

    [Fact]
    public void IntegrationScoped_AlwaysRunsAtUnion_RegardlessOfTouchedFiles()
    {
        // integration-scoped guardrails re-run at EVERY union point; the touched-files set
        // and the colliding-sibling flag are irrelevant for this scope (SSOT §4.3).
        //
        // COMPILE ERROR: GuardrailScopeFilter does not yet exist.
        var g    = MakeGuardrail("01-build", scope: "integration");
        var none = EmptyFiles();

        Assert.True(
            GuardrailScopeFilter.ShouldRunAtUnion(g, isCollidingSibling: false, touchedByMerge: none),
            "integration guardrail must run even on a distant task with no touched files.");
        Assert.True(
            GuardrailScopeFilter.ShouldRunAtUnion(g, isCollidingSibling: true,  touchedByMerge: none),
            "integration guardrail must run even on a colliding sibling with no touched files.");
    }

    [Fact]
    public void LocalScoped_DistantTask_SkippedWhenNoTouchedFiles()
    {
        // A local-scoped guardrail on a DISTANT, NON-COLLIDING task must be SKIPPED at a
        // union when the merge did not touch any files belonging to that task
        // (SSOT §4.3: "the touched-files local-skip applies ONLY to a distant, non-colliding
        // task's local guardrails — re-run only if the merge touched its files").
        //
        // COMPILE ERROR: GuardrailScopeFilter does not yet exist.
        var g = MakeGuardrail("01-check", scope: "local");

        bool shouldRun = GuardrailScopeFilter.ShouldRunAtUnion(
            g,
            isCollidingSibling: false,
            touchedByMerge: EmptyFiles());

        Assert.False(shouldRun,
            "A distant task's local guardrail must be skipped when the merge touched none of its files.");
    }

    [Fact]
    public void NullScope_TreatedAsDefaultLocal_DistantTask_SkippedWhenNoTouchedFiles()
    {
        // scope:null is the representation of the "local" default (SSOT §4.3).
        // A null-scope guardrail on a distant task must behave identically to scope:"local":
        // skipped when no touched files.
        //
        // COMPILE ERROR: GuardrailScopeFilter does not yet exist.
        var g = MakeGuardrail("01-check", scope: null);

        bool shouldRun = GuardrailScopeFilter.ShouldRunAtUnion(
            g,
            isCollidingSibling: false,
            touchedByMerge: EmptyFiles());

        Assert.False(shouldRun,
            "Null scope (= default local) on a distant task must be skipped when no files are touched.");
    }

    [Fact]
    public void LocalScoped_DistantTask_RunsWhenMergeTouchedItsFiles()
    {
        // A local-scoped guardrail on a distant task MUST re-run when the merge touched at
        // least one file belonging to that task (SSOT §4.3).
        //
        // COMPILE ERROR: GuardrailScopeFilter does not yet exist.
        var g = MakeGuardrail("01-check", scope: "local");

        bool shouldRun = GuardrailScopeFilter.ShouldRunAtUnion(
            g,
            isCollidingSibling: false,
            touchedByMerge: Files("src/DistantTask/Foo.cs"));

        Assert.True(shouldRun,
            "A distant task's local guardrail must run when the merge touched that task's files.");
    }

    [Fact]
    public void LocalScoped_CollidingSibling_AlwaysRuns_EvenWithNoTouchedFiles_B3()
    {
        // A local-scoped guardrail on a COLLIDING SIBLING must ALWAYS re-run at the union,
        // even when the merge diff does not contain any file from that sibling's task.
        //
        // Why (SSOT §4.3 / plan 08 §4 step 3 / B-3): the AI may have DELETED the sibling's
        // source hunk while resolving the merge conflict, keeping only the union task's side.
        // The sibling's TEST file is then untouched by the merge diff. A naive touched-files
        // check on the colliding sibling would see an empty set and skip the sibling's guardrail
        // — letting the AI's hunk-drop go undetected. B-3 closes this hole: colliding siblings'
        // guardrails run UNCONDITIONALLY, no touched-files optimization applied.
        //
        // COMPILE ERROR: GuardrailScopeFilter does not yet exist.
        var siblingGuardrail = MakeGuardrail("02-sibling-tests", scope: "local");

        // The AI dropped the sibling's source hunk → sibling's test file is untouched in
        // the merge diff → pre-filtered touchedByMerge for the sibling's task is empty.
        bool shouldRun = GuardrailScopeFilter.ShouldRunAtUnion(
            siblingGuardrail,
            isCollidingSibling: true,
            touchedByMerge: EmptyFiles());

        Assert.True(shouldRun,
            "Colliding sibling's local guardrail must run UNCONDITIONALLY — the AI may have " +
            "dropped the sibling's source hunk, leaving its test file untouched in the merge diff. " +
            "Applying a touched-files skip here re-opens the B-3 hole.");
    }

    [Fact]
    public void B3_Split_TouchedFilesSkip_WronglyAppliedToCollidingSibling_MakesTestFail()
    {
        // B-3 SPLIT: assert that a touched-files local-skip WRONGLY applied to a COLLIDING
        // SIBLING would give the WRONG answer, and that the correct answer is always TRUE.
        //
        // Scenario: the AI resolved a merge conflict by dropping the colliding sibling's source
        // hunk (keeping only the union task's side). The merge diff shows only the union task's
        // files; the sibling's test file is untouched because its corresponding source was silently
        // dropped. The pre-filtered touchedByMerge for the sibling's task is therefore EMPTY.
        //
        // Wrong implementation: treat the colliding sibling like a distant task → apply the
        //   touched-files check → touchedByMerge is empty → returns false → B-3 hole reopened.
        //   (A wrong ShouldRunAtUnion that ignores isCollidingSibling would return false here.)
        //
        // Correct implementation: detect isCollidingSibling=true → return true unconditionally.
        //
        // This Assert.True FAILS if the implementation wrongly applies the distant-task
        // optimization to a colliding sibling, because it would return false when expected true.
        // That is the encoded B-3 split gate.
        //
        // COMPILE ERROR: GuardrailScopeFilter does not yet exist.
        var siblingTestGuardrail = MakeGuardrail("02-sibling-tests", scope: "local");

        // Pre-filtered touchedByMerge for the sibling's task: empty, because the AI dropped
        // the sibling's source hunk and the sibling's test file was not in the merge diff.
        bool correctAnswer = GuardrailScopeFilter.ShouldRunAtUnion(
            siblingTestGuardrail,
            isCollidingSibling: true,
            touchedByMerge: EmptyFiles());

        // If the implementation WRONGLY applies the touched-files skip to the colliding sibling
        // (isCollidingSibling ignored; only EmptyFiles() checked), it returns false here.
        // This Assert.True catches that: the test FAILS when the B-3 split is missing.
        Assert.True(correctAnswer,
            "B-3 split: a colliding sibling's local guardrail must run at the union regardless of " +
            "touched files. This Assert.True fails if the implementation applies the distant-task " +
            "touched-files optimization to a colliding sibling — re-opening the hole where an " +
            "AI-dropped sibling hunk goes undetected because the sibling's test file is untouched.");
    }

    // =========================================================================
    // Terminal integrationGate sink — same integration set on the final HEAD
    // =========================================================================

    [Fact]
    public void TerminalGateSink_RunsSameIntegrationSet_AsIntermediateUnionPoints()
    {
        // The terminal integrationGate sink runs the SAME integration-guardrail set on the
        // final merged HEAD that the per-union re-verify uses at each intermediate union point
        // (SSOT §4.3/§3.3: "the terminal gate and the per-union re-verify are one mechanism
        // at two scopes"). GuardrailScopeFilter.IntegrationSet() is therefore the same call
        // for both the terminal gate and each union point.
        //
        // COMPILE ERROR: GuardrailScopeFilter does not yet exist.
        var buildG = MakeGuardrail("01-build", scope: "integration");
        var testG  = MakeGuardrail("02-tests", scope: "integration");
        var localG = MakeGuardrail("03-local", scope: null);
        GuardrailDefinition[] allGuardrails = [buildG, testG, localG];

        IReadOnlyList<GuardrailDefinition> forUnion    = GuardrailScopeFilter.IntegrationSet(allGuardrails);
        IReadOnlyList<GuardrailDefinition> forTerminal = GuardrailScopeFilter.IntegrationSet(allGuardrails);

        Assert.Equal(forUnion.Count, forTerminal.Count);
        Assert.Equal(2, forTerminal.Count);
        Assert.All(forTerminal, g =>
            Assert.Equal("integration", g.Scope, StringComparer.OrdinalIgnoreCase));
    }

    // =========================================================================
    // Private helpers
    // =========================================================================

    private static GuardrailDefinition MakeGuardrail(string name, string? scope) =>
        new() { Name = name, Path = $"/fake/guardrails/{name}.sh", Kind = ActionKind.Script, Scope = scope };

    private static IReadOnlySet<string> EmptyFiles() =>
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    private static IReadOnlySet<string> Files(params string[] paths) =>
        new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);

    private PlanDefinition LoadPlan(string dir)
    {
        PlanLoadResult result = new PlanLoader().Load(dir);
        Assert.NotNull(result.Plan);
        return result.Plan!;
    }

    // Writes a minimal one-task disk plan whose single guardrail is a script (01-build.sh)
    // with an optional metadata sidecar. Used for §4.1 sidecar-scope tests.
    private string BuildDiskPlanWithSidecar(string? sidecarJson)
    {
        string planDir = Path.Combine(_tempRoot, "plan-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(planDir);
        File.WriteAllText(Path.Combine(planDir, "guardrails.json"),
            """{ "version": 1, "maxParallelism": 1 }""");

        string taskDir = Path.Combine(planDir, "tasks", "01-task");
        string grDir   = Path.Combine(taskDir, "guardrails");
        Directory.CreateDirectory(grDir);
        File.WriteAllText(Path.Combine(taskDir, "task.json"),
            """{ "description": "task", "dependsOn": [] }""");
        File.WriteAllText(Path.Combine(taskDir, "action.sh"), "exit 0");
        File.WriteAllText(Path.Combine(grDir, "01-build.sh"), "exit 0");

        if (sidecarJson is not null)
        {
            File.WriteAllText(Path.Combine(grDir, "01-build.json"), sidecarJson);
        }

        return planDir;
    }

    // Writes a disk plan whose single task has a prompt action and one prompt guardrail
    // with the supplied content (typically YAML frontmatter + body). Used for §4.2 tests.
    private string BuildDiskPlanWithPromptGuardrail(string promptContent)
    {
        string planDir = Path.Combine(_tempRoot, "plan-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(planDir);
        File.WriteAllText(Path.Combine(planDir, "guardrails.json"),
            """
            {
              "version": 1,
              "maxParallelism": 1,
              "promptRunners": {
                "default": "claude",
                "claude": { "command": "claude" }
              }
            }
            """);

        string taskDir = Path.Combine(planDir, "tasks", "01-task");
        string grDir   = Path.Combine(taskDir, "guardrails");
        Directory.CreateDirectory(grDir);
        File.WriteAllText(Path.Combine(taskDir, "task.json"),
            """{ "description": "task", "dependsOn": [] }""");
        File.WriteAllText(Path.Combine(taskDir, "action.prompt.md"), "Write a greeting.");
        File.WriteAllText(Path.Combine(grDir, "01-verify.prompt.md"), promptContent);

        return planDir;
    }
}
