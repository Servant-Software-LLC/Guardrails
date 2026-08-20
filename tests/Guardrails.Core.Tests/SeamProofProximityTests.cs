using Guardrails.Core.Loading;
using Guardrails.Core.Model;

namespace Guardrails.Core.Tests;

/// <summary>
/// <b>#382 M3 — the golden-folder round-trip for integration-proof proximity</b>
/// (<c>docs/plans/18-integration-proof-proximity.md</c> §13 M3).
///
/// <para><b>Why this test exists.</b> §3 of the design ships #382 v1 as SKILL TEXT ONLY — decision
/// <b>D1</b>: no <c>validate</c> code, no GR code, <c>GR2061</c> reserved behind §3.4's evidence gate.
/// So M1's authoring rule and M2's review audit have exactly one durable regression signal, and this is
/// it. Invariant 5 does not permit a v1 nobody can verify.</para>
///
/// <para><b>What it asserts — and the one thing it deliberately does NOT.</b> Ruling <b>D13</b>: the
/// seam ledger has <b>no home on disk</b>. M1 prints it in the plan-breakdown Step 7.4 <i>report</i>,
/// which is conversation output, and a plan folder has no persisted-report convention
/// (<c>diagram.*</c>, <c>guardrails.json</c>, <c>state/</c>, task and wave folders — nothing else).
/// Inventing a file to assert against is precisely the declared-field change §3.4 defers. So this test
/// asserts the <b>FOLDER-OBSERVABLE HALF</b> only: <i>a real-seam guardrail lands on T\*, not on the
/// terminal task.</i> There is no ledger-row assertion here, and its absence is a design decision, not
/// an omission.</para>
///
/// <para><b>Two-sided by construction.</b> A check that only ever sees the correct form proves nothing
/// — the same doctrine this design applies to guardrails. The two committed fixtures are
/// <b>byte-identical except for which folder holds the proof</b>
/// (<see cref="TheTwoFixturesDifferOnlyInWhereTheProofSits"/> pins that), so the audit demonstrably
/// keys on placement and on nothing else. Two further cases run off temp copies: a proof placed
/// EARLIER than T\* (which a naive "is it the last task?" check would wave through), and a proof whose
/// seam the audit cannot resolve (which must be REPORTED, never silently passed).</para>
///
/// <para><b>What this test cannot do.</b> It cannot run <c>/plan-breakdown</c> — that needs real Claude
/// and a token spend, and is the manual dogfood half, exactly as
/// <see cref="GoldenRoundTripTests"/> records for the golden example. It proves the rule is mechanically
/// checkable over emitted folders and that the fixtures discriminate. The doctrine that MAKES a skill
/// emit that shape is pinned separately, in <see cref="SeamDoctrineAnchorTests"/> — that is the half
/// that goes red when someone guts the skills.</para>
/// </summary>
public sealed class SeamProofProximityTests
{
    /// <summary>The correct shape: the E-bucket proof sits at T\*, the task that implements the component.</summary>
    private const string ProofAtTStar = "seam-proof-at-tstar";

    /// <summary>The defect: the same proof, byte-identical, deferred to the terminal wiring task.</summary>
    private const string ProofInSink = "seam-proof-in-sink";

    /// <summary>T\* for the <c>CriticalityJudge → IPromptRunner</c> seam in both fixtures.</summary>
    private const string TStar = "03-implement-judge";

    /// <summary>The terminal join-check task — where the deferred fixture wrongly parks the proof.</summary>
    private const string TerminalTask = "04-wire-composition-root";

    /// <summary>The TDD pair's red half — before the component's production type exists.</summary>
    private const string TestAuthorTask = "02-author-judge-tests";

    private const string ProofFile = "03-real-seam-tests-pass.ps1";

    // ---- the folder-observable rule, both sides --------------------------------------------------

    [Fact]
    public void ProofAtTStar_IsClean_AndTheAuditActuallySawIt()
    {
        PlanDefinition plan = Load(ProofAtTStar);

        // Non-vacuity FIRST. An audit that reports nothing because it recognised no proof at all would
        // be green for the wrong reason — the exact passing-but-blind shape #382 is about.
        string proof = Assert.Single(SeamProofPlacement.RealSeamProofs(plan));
        Assert.Equal($"{TStar}/03-real-seam-tests-pass", proof);

        Assert.Empty(SeamProofPlacement.Audit(plan));
    }

    [Fact]
    public void ProofDeferredToTheTerminalTask_IsAFindingThatNamesTStar()
    {
        PlanDefinition plan = Load(ProofInSink);

        Assert.Equal(
            $"{TerminalTask}/03-real-seam-tests-pass",
            Assert.Single(SeamProofPlacement.RealSeamProofs(plan)));

        SeamProofFinding finding = Assert.Single(SeamProofPlacement.Audit(plan));

        Assert.Equal(SeamProofFindingKind.LaterThanTStar, finding.Kind);
        Assert.Equal(TerminalTask, finding.OwningTaskId);

        // "The report must NAME T*" (§1.4). A finding that says "this is wrong" without saying where it
        // belongs sends the author hunting, which is how the rule stops being applied.
        Assert.Equal(TStar, finding.ExpectedTaskId);

        // The seam was recovered from the folder, not assumed: both production types, closed-vocabulary
        // matched against the DAG's own writeScope declarations.
        Assert.Equal("ClaudePromptRunner, CriticalityJudge", string.Join(", ", finding.SeamTypes));
        Assert.Contains(TStar, finding.Detail);
    }

    /// <summary>
    /// The two-sidedness proof. If the fixtures differed in anything else — a description, a writeScope
    /// entry, a second guardrail — the audit could be keying on that instead of on placement, and the
    /// pair would be a snapshot rather than a discriminator.
    /// </summary>
    [Fact]
    public void TheTwoFixturesDifferOnlyInWhereTheProofSits()
    {
        Dictionary<string, string> clean = FixtureContents(ProofAtTStar);
        Dictionary<string, string> defect = FixtureContents(ProofInSink);

        string cleanOnly = Assert.Single(clean.Keys.Except(defect.Keys));
        string defectOnly = Assert.Single(defect.Keys.Except(clean.Keys));

        Assert.Equal($"tasks/{TStar}/guardrails/{ProofFile}", cleanOnly);
        Assert.Equal($"tasks/{TerminalTask}/guardrails/{ProofFile}", defectOnly);

        // Same bytes, different folder: the ONLY variable is placement.
        Assert.Equal(clean[cleanOnly], defect[defectOnly]);

        foreach (string shared in clean.Keys.Intersect(defect.Keys))
        {
            Assert.True(clean[shared] == defect[shared],
                $"The two #382 fixtures have drifted at '{shared}'. They must differ ONLY in which task " +
                "folder holds the real-seam proof — any other difference lets the placement audit pass " +
                "for a reason other than placement, which turns this pair back into a snapshot.");
        }
    }

    /// <summary>
    /// A proof placed BEFORE T\* is also wrong, and a naive "the proof must not be on the last task"
    /// check would wave it through — <c>02-author-judge-tests</c> is neither T\* nor terminal. This is
    /// the test that proves the audit computes T\* from <c>writeScope</c> + <c>dependsOn</c> rather than
    /// pattern-matching the end of the DAG.
    /// </summary>
    [Fact]
    public void ProofBeforeItsComponentExists_IsAFindingToo_AndNamesTStar()
    {
        RunOnMutatedCopy(ProofAtTStar, root =>
        {
            MoveProof(root, from: TStar, to: TestAuthorTask);

            SeamProofFinding finding = Assert.Single(SeamProofPlacement.Audit(LoadFrom(root)));
            Assert.Equal(SeamProofFindingKind.EarlierThanTStar, finding.Kind);
            Assert.Equal(TestAuthorTask, finding.OwningTaskId);
            Assert.Equal(TStar, finding.ExpectedTaskId);
        });
    }

    /// <summary>
    /// The audit's own passing-but-blind hunt. D13 leaves the folder with no authoritative statement of
    /// which seam a guardrail proves, so the audit recovers it from the <c>catches:</c> declaration —
    /// and when it cannot, it must SAY SO. Reporting "no findings" over a proof it could not read would
    /// be the same false green the whole design exists to remove.
    /// </summary>
    [Fact]
    public void AProofWhoseSeamCannotBeResolved_IsReported_NeverSilentlyPassed()
    {
        RunOnMutatedCopy(ProofAtTStar, root =>
        {
            string proof = Path.Combine(root, "tasks", TStar, "guardrails", ProofFile);
            File.WriteAllText(proof,
                "# catches: a component that is broken through the real adapter (passing-but-blind),\n" +
                "#          but this declaration names no production type the DAG declares.\n" +
                "exit 0\n");

            SeamProofFinding finding = Assert.Single(SeamProofPlacement.Audit(LoadFrom(root)));
            Assert.Equal(SeamProofFindingKind.SeamNotResolvable, finding.Kind);
            Assert.Equal(TStar, finding.OwningTaskId);
            Assert.Null(finding.ExpectedTaskId);
        });
    }

    /// <summary>
    /// §1.5 names TWO terminal objects, not one: the #120 wiring task and the plan-level
    /// <c>&lt;plan&gt;/guardrails/</c> folder, which is evaluated once on the merged HEAD at run end. A
    /// proof parked there is later than every T\* by construction, so the audit rejects it without
    /// needing to reason about the DAG at all. Without this case the rule would read as
    /// "not on the last TASK" and the plan-root sink would be a legal hiding place for the same defect.
    /// </summary>
    [Fact]
    public void ProofParkedInThePlanRootSink_IsAFinding()
    {
        RunOnMutatedCopy(ProofAtTStar, root =>
        {
            string planRoot = Path.Combine(root, "guardrails");
            Directory.CreateDirectory(planRoot);
            File.Move(
                Path.Combine(root, "tasks", TStar, "guardrails", ProofFile),
                Path.Combine(planRoot, ProofFile));

            SeamProofFinding finding = Assert.Single(SeamProofPlacement.Audit(LoadFrom(root)));
            Assert.Equal(SeamProofFindingKind.InPlanRootSink, finding.Kind);
            Assert.Equal(SeamProofPlacement.PlanRootOwner, finding.OwningTaskId);
        });
    }

    // ---- the deterministic surround --------------------------------------------------------------

    /// <summary>
    /// <b>D1 / §3.1, made executable.</b> The mis-placed fixture is a plan that <c>validate</c> reports
    /// as entirely clean — no error, no warning, and specifically no GR2042: its terminal task has one
    /// <c>writeScope</c> entry and one dependency, so #378's structural over-scope lint is silent by
    /// design. That is the whole reason #382 could not be shipped as a lint, and the reason the review
    /// pass is the only gate.
    ///
    /// <para><b>If this ever goes red, read it before "fixing" it.</b> A new diagnostic that fires here
    /// is either an unrelated lint that needs the fixture adjusted, or it is <c>GR2061</c> arriving —
    /// in which case §3.4's evidence gate has opened and this test's premise, not the fixture, is what
    /// changed.</para>
    /// </summary>
    [Theory]
    [InlineData(ProofAtTStar)]
    [InlineData(ProofInSink)]
    public void BothFixturesValidateClean_BecauseValidateCannotSeeThisDefect(string fixture)
    {
        IReadOnlyList<Diagnostic> diagnostics =
            new PlanValidator(FakeExecutableProbe.All).Validate(Load(fixture));

        Assert.True(diagnostics.Count == 0,
            $"'{fixture}' is meant to be a plan `validate` has NOTHING to say about — that is D1 " +
            "(#382 ships no validate lint) made executable. Diagnostics:\n" +
            string.Join("\n", diagnostics.Select(d => $"  {d.Severity} {d.Code}: {d.Message}")));
    }

    /// <summary>
    /// <c>scope: "local"</c> — the key omitted (catalogue "drive-the-real-seam", the #250 conclusion).
    /// A real-seam proof cannot pass before its implement task's action has run, so it fails the #125
    /// union-safe decision test and must never be tagged <c>integration</c>. Pinned on the fixture so a
    /// well-meaning "make the terminal check re-run at unions" edit cannot quietly reintroduce the
    /// rollback that cost two unrelated parallel siblings a retry.
    /// </summary>
    [Theory]
    [InlineData(ProofAtTStar, TStar)]
    [InlineData(ProofInSink, TerminalTask)]
    public void TheRealSeamProof_IsNeverTaggedIntegrationScope(string fixture, string owningTask)
    {
        TaskNode task = Assert.Single(Load(fixture).Tasks, t => t.Id == owningTask);
        GuardrailDefinition proof = Assert.Single(task.Guardrails, SeamProofPlacement.IsRealSeamProof);

        Assert.Null(proof.Scope);
    }

    // ---- helpers ---------------------------------------------------------------------------------

    private static PlanDefinition Load(string fixture) => LoadFrom(TestPaths.Fixture(fixture));

    private static PlanDefinition LoadFrom(string planDir)
    {
        PlanLoadResult result = new PlanLoader().Load(planDir);
        Assert.False(result.HasErrors,
            string.Join("\n", result.Diagnostics.Select(d => d.ToString())));
        Assert.NotNull(result.Plan);
        return result.Plan!;
    }

    /// <summary>Every fixture file, keyed by forward-slashed relative path, line-endings normalized.</summary>
    private static Dictionary<string, string> FixtureContents(string fixture)
    {
        string root = TestPaths.Fixture(fixture);
        return Directory
            .EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(
                path => Path.GetRelativePath(root, path).Replace('\\', '/'),
                path => File.ReadAllText(path).Replace("\r\n", "\n").Replace("\r", "\n"),
                StringComparer.Ordinal);
    }

    /// <summary>
    /// Copies a committed fixture to a temp folder, hands the copy to <paramref name="body"/>, and
    /// always cleans up. The committed fixtures stay pristine; the mutants are generated, so they can
    /// never drift out of sync with the shape they mutate.
    /// </summary>
    private static void RunOnMutatedCopy(string fixture, Action<string> body)
    {
        string root = Path.Combine(Path.GetTempPath(), "gr-seam-" + Guid.NewGuid().ToString("N"));
        try
        {
            CopyTree(TestPaths.Fixture(fixture), root);
            body(root);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { /* best-effort */ }
        }
    }

    private static void CopyTree(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, dir)));
        }

        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, Path.Combine(destination, Path.GetRelativePath(source, file)));
        }
    }

    private static void MoveProof(string root, string from, string to)
    {
        File.Move(
            Path.Combine(root, "tasks", from, "guardrails", ProofFile),
            Path.Combine(root, "tasks", to, "guardrails", ProofFile));
    }
}
