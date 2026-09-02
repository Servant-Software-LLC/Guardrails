using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Guardrails.Core.Execution;
using Guardrails.Core.Loading;
using Guardrails.Core.Model;

namespace Guardrails.Core.Tests;

/// <summary>
/// TDD-red pins for <b>GR2060 <c>UnproducibleGateRequirement</c></b> (doc 19 §3.1, plan 33 §5) — a script
/// guardrail that requires an exact literal in a TRACKED workspace file which does not contain it, when
/// <b>no task in the plan declares that file in its <c>writeScope</c></b>. Nothing in the plan can make the
/// gate pass, so the run spends its whole DAG and fails at the gate; the measured price of learning that
/// the expensive way was $115.32.
///
/// <para><b>Authored RED, before the check exists.</b> <c>src/Guardrails.Core/Loading/ProducerCoverage.cs</c>
/// does not exist, so this file does not compile — that compilation failure IS the expected signal, and it
/// must not be "fixed" by stubbing the type here. Creating it is the next task's deliverable.</para>
///
/// <para><b>The surface these pins require</b>, mirroring <see cref="HandoffScopeCoverage"/>'s one
/// check-family / one file precedent:
/// <code>
/// internal static class ProducerCoverage
/// {
///     internal static void Validate(PlanDefinition plan, IGitTrackedFileProbe gitTrackedFileProbe, List&lt;Diagnostic&gt; diagnostics);
/// }
/// </code>
/// <see cref="Findings"/> computes every finding TWICE — once by calling that method directly and once
/// through <see cref="PlanValidator.Validate"/> over a real on-disk plan folder — and asserts the two agree.
/// That is plan 33 §8.4's anti-tautology pin and the #382 lesson behind it: a check that is written but not
/// WIRED into the composition root certifies green through fakes while the path the run actually drives is
/// dead.</para>
///
/// <para><b>The code is asserted as the string literal <c>"GR2060"</c></b>, never through a
/// <see cref="DiagnosticCodes"/> constant: allocating that constant is the implementation task's deliverable
/// and does not compile today. Same convention as <c>HandoffScopeCoverageTests</c>.</para>
///
/// <para><b>Three controls are RECOVERED from git rather than hand-built</b> (§8.2, §8.3), and every half of
/// each is read with <c>git show &lt;sha&gt;:&lt;path&gt;</c>. A hand-copied control proves the code matches
/// the copy and nothing about the world. Because they read history, they are SKIPPED where that history is
/// not present — a shallow clone (<c>actions/checkout</c> defaults to <c>fetch-depth: 1</c>) has neither
/// commit. The skip names exactly what could not be read; it never substitutes a fixture for the evidence.</para>
///
/// <para><b>One test may not use a fake probe.</b> <see cref="Silent_WhenTheFileIsNotGitTracked"/> drives the
/// production <see cref="GitLsFilesProbe"/> against a throwaway git repository, faking only the executable
/// lookup underneath it. A fake probe there would prove that <c>ProducerCoverage</c> honours whatever the
/// probe says and nothing about whether the probe says anything true.</para>
/// </summary>
public sealed class ProducerCoverageTests : IDisposable
{
    // ── the recovered pair ────────────────────────────────────────────────────────────────────────────
    // 544f7d5: the SSOT is `tierSource`-free and NO task manifest names it -> GR2060 FIRES.
    // 5bd29da: the SSOT is byte-identical and still `tierSource`-free, but 14-land-ssot-schema-deltas now
    //          declares that exact path -> GR2060 is SILENT. Same script, same witness, same path.
    private const string FiringCommit = "544f7d5";
    private const string SilentCommit = "5bd29da";

    /// <summary>The gate script both halves of the recovered pair are read from.</summary>
    private const string GateFileName = "03-dor-section-6-contract-landed.ps1";

    /// <summary>The workspace file the recovered gate requires content in.</summary>
    private const string SsotPath = "docs/plans/02-schemas-and-contracts.md";

    /// <summary>The exact literal that gate requires — de-regexable, so a witness exists (condition 4).</summary>
    private const string Witness = "tierSource";

    private const string Gr2060 = "GR2060";

    /// <summary>The synthetic gates' workspace file and witness — deliberately nothing like the recovered one.</summary>
    private const string NotesPath = "docs/notes.md";

    private const string NotesWitness = "ProducerCoverageWitness";

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "gr-producer-coverage-" + Guid.NewGuid().ToString("N"));

    public ProducerCoverageTests() => Directory.CreateDirectory(_root);

    public void Dispose() => DeleteTree(_root);

    // ══ 1. The positive control — recovered, and the only one this plan has ═══════════════════════════

    /// <summary>
    /// <b>§8.2's positive control, recovered whole from git.</b> The gate script, the SSOT bytes it requires
    /// a literal in, and every task manifest whose <c>writeScope</c> could have owned that path are all read
    /// at <c>544f7d5</c> — nothing here is hand-written except the plan scaffolding around them.
    ///
    /// <para>The two recovered facts are asserted before the check is asked anything, so a fixture that
    /// drifted out from under the control fails as a broken fixture rather than as a finding about GR2060:
    /// the SSOT at that commit carries ZERO occurrences of <c>tierSource</c>, and NONE of the plan's 19 task
    /// manifests claims the SSOT path under <see cref="WriteScope.IsInScope"/> — the same predicate the
    /// harness enforces at write time, so this test and the check cannot disagree about what coverage means.</para>
    ///
    /// <para><b>The commit moved from <c>1b8e681</c>, and why is the instructive part.</b> GR2060 cannot fire
    /// there: at that commit <c>wave-02-attempt-launch-wiring</c> held zero task manifests, so
    /// <c>planIsClosed</c> is FALSE and condition 10 suppresses — correctly, because a future wave might own
    /// the file, and one later did. Any implementation that fired at <c>1b8e681</c> would have got there by
    /// deleting condition 10.</para>
    /// </summary>
    [Fact]
    public void Fires_OnRecoveredPositiveControl_NamingTierSourceAndTheSsotPath()
    {
        SkipUnlessHistoryIsAvailable();

        string gate = GitShow("544f7d5:docs/plans/model-tiering-stage-2/guardrails/03-dor-section-6-contract-landed.ps1");
        string ssot = GitShow("544f7d5:docs/plans/02-schemas-and-contracts.md");

        // Recovered fact 1: the requirement is unsatisfied at this commit.
        Assert.DoesNotContain(Witness, ssot, StringComparison.Ordinal);

        // Recovered fact 2: nothing in the plan is authorized to write the file the gate names.
        IReadOnlyList<RecoveredManifest> manifests = RecoveredManifests(FiringCommit);
        Assert.Equal(19, manifests.Count);
        Assert.DoesNotContain(manifests, m => WriteScope.IsInScope(SsotPath, m.WriteScope));

        string plan = RecoveredPlan("model-tiering-stage-2", gate, ssot, manifests);

        Diagnostic finding = Assert.Single(Findings(plan, StubGitTrackedFileProbe.Tracked));
        Assert.Equal(Gr2060, finding.Code);
        Assert.Equal(DiagnosticSeverity.Error, finding.Severity);
        Assert.Contains(Witness, finding.Message, StringComparison.Ordinal);
        Assert.Contains(SsotPath, finding.Message, StringComparison.Ordinal);
    }

    // ══ 2. The same script against today's tree ══════════════════════════════════════════════════════

    /// <summary>
    /// <b>The half that proves the check tracks the TREE rather than the string.</b> Today's
    /// <c>03-dor-section-6-contract-landed.ps1</c> is byte-identical to the one that fires at
    /// <c>544f7d5</c> — asserted here, so the claim is measured rather than assumed — and the plan's task
    /// set is held at <c>544f7d5</c> too. The ONLY thing that differs from
    /// <see cref="Fires_OnRecoveredPositiveControl_NamingTierSourceAndTheSsotPath"/> is the SSOT's current
    /// bytes, which now carry <c>tierSource</c>: the requirement is satisfied, so there is nothing to say.
    ///
    /// <para>An implementation that fired on the clause text alone — the shape doc 19 §3.2 calls a wolf —
    /// would pass test 1 and fail here.</para>
    /// </summary>
    [Fact]
    public void Recovered_Silent_OnTheSameScript_AtTodaysCommit()
    {
        SkipUnlessHistoryIsAvailable();

        string gateThen = GitShow("544f7d5:docs/plans/model-tiering-stage-2/guardrails/03-dor-section-6-contract-landed.ps1");
        string gateNow = GitShow("HEAD:docs/plans/model-tiering-stage-2/guardrails/03-dor-section-6-contract-landed.ps1");
        string ssotNow = GitShow("HEAD:docs/plans/02-schemas-and-contracts.md");

        // Same script, still carrying the same requirement clause — otherwise this silence is vacuous.
        Assert.Equal(gateThen, gateNow, ignoreLineEndingDifferences: true);
        Assert.Contains("-cnotmatch 'tierSource'", gateNow, StringComparison.Ordinal);

        // The tree, and only the tree, has moved: the witness is present now.
        Assert.Contains(Witness, ssotNow, StringComparison.Ordinal);

        string plan = RecoveredPlan("model-tiering-stage-2-today", gateNow, ssotNow, RecoveredManifests(FiringCommit));

        Assert.Empty(Findings(plan, StubGitTrackedFileProbe.Tracked));
    }

    // ══ 3. Extractor shape one — the one-hop association ═════════════════════════════════════════════

    /// <summary>
    /// <b>The first of the two ways GR2060 can ship MUTE</b> (§8.3). The measured instance does not write
    /// <c>$v = Get-Content 'X'</c>; it writes
    /// <c>$v = if (Test-Path 'X') { Get-Content -Raw 'X' } else { "" }</c>. A reader that only handles the
    /// direct form misses the artifact the whole check was built from and then silently finds nothing —
    /// which looks exactly like a clean plan.
    ///
    /// <para>The second half pins the OTHER side of doc 19 condition 3: <c>$v</c> must be assigned EXACTLY
    /// once. A variable reassigned later is not a one-hop association to a statically-known file, and
    /// admitting it would be the "widen the extractor until the test passes" move §11 prohibition 4
    /// forbids.</para>
    /// </summary>
    [Fact]
    public void Extracts_OneHopAssociation_TestPathThenGetContentShape()
    {
        string oneHop = SyntheticPlan("one-hop", """
            $notes = if (Test-Path 'docs/notes.md') { Get-Content -Raw 'docs/notes.md' } else { "" }
            if ($notes -cnotmatch 'ProducerCoverageWitness') {
                $failures += "docs/notes.md does not carry the marker this gate requires"
            }
            """);

        Diagnostic finding = Assert.Single(Findings(oneHop, StubGitTrackedFileProbe.Tracked));
        Assert.Equal(Gr2060, finding.Code);
        Assert.Contains(NotesWitness, finding.Message, StringComparison.Ordinal);
        Assert.Contains(NotesPath, finding.Message, StringComparison.Ordinal);

        // Assigned twice: the association is no longer one-hop, so the path is not statically known.
        string reassigned = SyntheticPlan("reassigned", """
            $notes = if (Test-Path 'docs/notes.md') { Get-Content -Raw 'docs/notes.md' } else { "" }
            $notes = $notes + (Get-Content -Raw 'docs/other.md')
            if ($notes -cnotmatch 'ProducerCoverageWitness') {
                $failures += "docs/notes.md does not carry the marker this gate requires"
            }
            """);

        Assert.Empty(Findings(reassigned, StubGitTrackedFileProbe.Tracked));
    }

    // ══ 4. Extractor shape two — the double-quoted path operand ══════════════════════════════════════

    /// <summary>
    /// <b>The second way GR2060 can ship MUTE.</b> Doc 19 condition 2 relaxes PATH operands (never pattern
    /// operands) to double-quoted literals containing no <c>$</c> and no backtick, because the measured
    /// instance needs it: with neither of those characters the string is its own literal content.
    ///
    /// <para>The relaxation's boundary is pinned in the same test, because a relaxation with no boundary is
    /// just a hole: <c>"$dir/notes.md"</c> interpolates, so the path is NOT statically known and the clause
    /// must be dropped. Firing there would mean guessing at a path — the worst outcome a path-coverage check
    /// can have.</para>
    /// </summary>
    [Fact]
    public void Extracts_DoubleQuotedPathOperand_WithNoDollarAndNoBacktick()
    {
        string doubleQuoted = SyntheticPlan("double-quoted", """
            $notes = Get-Content -Raw "docs/notes.md"
            if ($notes -cnotmatch 'ProducerCoverageWitness') {
                $failures += "docs/notes.md does not carry the marker this gate requires"
            }
            """);

        Diagnostic finding = Assert.Single(Findings(doubleQuoted, StubGitTrackedFileProbe.Tracked));
        Assert.Equal(Gr2060, finding.Code);
        Assert.Contains(NotesWitness, finding.Message, StringComparison.Ordinal);
        Assert.Contains(NotesPath, finding.Message, StringComparison.Ordinal);

        // Interpolated: PowerShell expands $dir, so the operand names no statically-known file.
        string interpolated = SyntheticPlan("interpolated", """
            $dir = 'docs'
            $notes = Get-Content -Raw "$dir/notes.md"
            if ($notes -cnotmatch 'ProducerCoverageWitness') {
                $failures += "docs/notes.md does not carry the marker this gate requires"
            }
            """);

        Assert.Empty(Findings(interpolated, StubGitTrackedFileProbe.Tracked));
    }

    // ══ 5. Condition 8 — RECOVERED, and the other half of the pair ═══════════════════════════════════

    /// <summary>
    /// <b>RECOVERED, not constructed — and it completes a fires/silent pair on ONE artifact.</b> An earlier
    /// draft of plan 33 asserted that condition 8 (<i>no task declares the path</i>) had zero exercises in
    /// the corpus and told this task to build a synthetic fixture. That claim was false and has been
    /// withdrawn: the exercise is real, and this test reads both of its halves out of git.
    ///
    /// <para><b>What the <c>544f7d5</c> → <c>5bd29da</c> pair proves.</b> The gate script is byte-identical
    /// at the two commits. The SSOT is byte-identical at the two commits, and carries zero occurrences of
    /// <c>tierSource</c> at both. Every one of the 19 manifests at <c>544f7d5</c> survives into
    /// <c>5bd29da</c>; the ONE difference between the two trees that GR2060 can see is that
    /// <c>14-land-ssot-schema-deltas</c> now exists and declares
    /// <c>["docs/plans/02-schemas-and-contracts.md"]</c> in its <c>writeScope</c>. Same script, same witness,
    /// same path, same bytes — <b>the only difference between the two commits is whether a task owns the
    /// file</b>, which is precisely the discrimination condition 8 exists to make. An implementation that
    /// hard-coded "nothing covers it" would pass every other test in this file and fail exactly here.</para>
    ///
    /// <para>A silence control's label states how its evidence was obtained. Calling a hand-built fixture
    /// <c>Recovered</c> is the lie this plan was rewritten to remove; calling this one <c>Constructed</c>
    /// would understate real evidence. Nothing below is constructed: every byte on both sides of the pair —
    /// gate script, SSOT, and task manifests — is read with <c>git show</c>.</para>
    /// </summary>
    [Fact]
    public void Recovered_Silent_WhenThePathIsCoveredByATaskWriteScope()
    {
        SkipUnlessHistoryIsAvailable();

        string gateWhenFiring = GitShow("544f7d5:docs/plans/model-tiering-stage-2/guardrails/03-dor-section-6-contract-landed.ps1");
        string gateWhenSilent = GitShow("5bd29da:docs/plans/model-tiering-stage-2/guardrails/03-dor-section-6-contract-landed.ps1");
        string ssotWhenFiring = GitShow("544f7d5:docs/plans/02-schemas-and-contracts.md");
        string ssotWhenSilent = GitShow("5bd29da:docs/plans/02-schemas-and-contracts.md");

        // Everything the check reads about the REQUIREMENT is identical across the pair.
        Assert.Equal(gateWhenFiring, gateWhenSilent, ignoreLineEndingDifferences: true);
        Assert.Equal(ssotWhenFiring, ssotWhenSilent, ignoreLineEndingDifferences: true);
        Assert.DoesNotContain(Witness, ssotWhenSilent, StringComparison.Ordinal);

        // The manifest side — the one thing that moved. Condition 8 is a claim about the writeScope union,
        // so the owning task's own task.json is read rather than described.
        string owner = GitShow("5bd29da:docs/plans/model-tiering-stage-2/wave-02-attempt-launch-wiring/tasks/14-land-ssot-schema-deltas/task.json");
        Assert.Contains(SsotPath, owner, StringComparison.Ordinal);

        IReadOnlyList<RecoveredManifest> before = RecoveredManifests(FiringCommit);
        IReadOnlyList<RecoveredManifest> after = RecoveredManifests(SilentCommit);
        Assert.Equal(19, before.Count);
        Assert.Equal(20, after.Count);
        Assert.Contains(after, m => m.Id == "14-land-ssot-schema-deltas" && WriteScope.IsInScope(SsotPath, m.WriteScope));
        Assert.DoesNotContain(before, m => WriteScope.IsInScope(SsotPath, m.WriteScope));

        // The pair, driven through the check.
        string fires = RecoveredPlan("stage-2-unowned", gateWhenFiring, ssotWhenFiring, before);
        string silent = RecoveredPlan("stage-2-owned", gateWhenSilent, ssotWhenSilent, after);

        Diagnostic finding = Assert.Single(Findings(fires, StubGitTrackedFileProbe.Tracked));
        Assert.Equal(Gr2060, finding.Code);
        Assert.Empty(Findings(silent, StubGitTrackedFileProbe.Tracked));
    }

    // ══ 6. Condition 5 — a satisfied requirement is not a finding ════════════════════════════════════

    /// <summary>
    /// The witness is PRESENT in the file's current bytes, so the clause is satisfiable today and there is
    /// nothing to report. Both polarities run against the SAME plan folder, with only the workspace file's
    /// content rewritten between them: that is what proves the silence comes from condition 5 rather than
    /// from the fixture failing to reach the extractor at all.
    /// </summary>
    [Fact]
    public void Silent_WhenTheWitnessIsPresentInTheFile()
    {
        string plan = SyntheticPlan("witness-present", """
            $notes = Get-Content -Raw 'docs/notes.md'
            if ($notes -cnotmatch 'ProducerCoverageWitness') {
                $failures += "docs/notes.md does not carry the marker this gate requires"
            }
            """);

        Assert.Single(Findings(plan, StubGitTrackedFileProbe.Tracked));

        WorkspaceFile(_root, NotesPath, "these notes do carry the ProducerCoverageWitness marker\n");

        Assert.Empty(Findings(plan, StubGitTrackedFileProbe.Tracked));
    }

    // ══ 7. Condition 6 — the REAL-SEAM proof, and the one test that may not fake the probe ═══════════

    /// <summary>
    /// <b>The production adapter, against a real git index (#382).</b> Every other test here substitutes
    /// <see cref="IGitTrackedFileProbe"/>, which is ordinary and correct. This one may not: it is the only
    /// place that proves <see cref="GitLsFilesProbe"/> — the probe the run actually drives — says anything
    /// true. A fake here would prove that <c>ProducerCoverage</c> honours whatever the probe says and
    /// nothing about the seam underneath it, which is exactly how fake-masked unit guardrails certify green
    /// over a broken composition-root path.
    ///
    /// <para>Only the executable LOOKUP is faked, so the assertion never depends on the machine's PATH
    /// state; the real <c>git ls-files</c> child process runs, against a throwaway repository holding one
    /// committed file and one uncommitted one. The probe's own answers are asserted first — <c>true</c> for
    /// the committed path, <c>false</c> for the other — because the two GR2060 verdicts below are only
    /// evidence about condition 6 if the probe genuinely discriminated.</para>
    ///
    /// <para>The temp repository is targeted with <c>GIT_DIR</c>/<c>GIT_WORK_TREE</c> rather than by moving
    /// the process working directory: the CWD is global state that other tests read, while these two
    /// variables are read by nothing else in this suite. The write is still process-wide, so it is held
    /// under a gate and restored in a <c>finally</c>.</para>
    /// </summary>
    [Fact]
    public void Silent_WhenTheFileIsNotGitTracked()
    {
        Assert.SkipUnless(GitIsUsable, "git is not runnable here, so the production tracked-file probe cannot be driven.");

        using var repo = new TempGitRepo();
        repo.CommitFile("docs/tracked-witness.md", "nothing to see here\n", "add a tracked file");
        repo.WriteWorkingFile("docs/untracked-witness.md", "nothing to see here\n");

        var probe = new GitLsFilesProbe(FakeExecutableProbe.With("git"));

        string trackedSubject = SyntheticPlan(repo.RepoPath, "tracked-subject", "docs/tracked-witness.md");
        string untrackedSubject = SyntheticPlan(repo.RepoPath, "untracked-subject", "docs/untracked-witness.md");

        string[] candidates = ["docs/tracked-witness.md", "docs/untracked-witness.md"];

        WithGitPointedAt(repo.RepoPath, () =>
        {
            IReadOnlyDictionary<string, bool?> answers = probe.AreTracked(candidates);

            Assert.True(answers["docs/tracked-witness.md"]);
            Assert.False(answers["docs/untracked-witness.md"]);

            // The tracked control: everything else about the two plans is identical, so the verdict below
            // can only be attributable to what git said about the path.
            Assert.Single(Findings(trackedSubject, probe));

            // Condition 6: an untracked file is something no author would put in a writeScope — a generated
            // artifact, a build output — and must never produce a finding.
            Assert.Empty(Findings(untrackedSubject, probe));
        });
    }

    // ══ 8. Condition 6 — not-known must never be read as "untracked" ═════════════════════════════════

    /// <summary>
    /// <b>This matters more than it looks.</b> <see cref="IGitTrackedFileProbe"/> reports NOT-KNOWN when git
    /// is unavailable, when the call fails, or when the answer cannot otherwise be obtained, and GR2056's
    /// silence-is-not-proof rule says a not-known answer must never be read as "untracked". GR2060 is ERROR
    /// severity and <c>RunCommand</c> refuses to run a plan carrying a validation error, so getting this
    /// backwards would make the check fire on correct plans and block their runs — and their resumes — on
    /// any machine without git.
    ///
    /// <para>The <see cref="NullGitTrackedFileProbe"/> arm is the same condition reached through the
    /// no-git default rather than through a fake, so a check that special-cased one and not the other is
    /// caught.</para>
    /// </summary>
    [Fact]
    public void Silent_WhenTheProbeAnswersNotKnown()
    {
        string plan = SyntheticPlan("not-known", """
            $notes = Get-Content -Raw 'docs/notes.md'
            if ($notes -cnotmatch 'ProducerCoverageWitness') {
                $failures += "docs/notes.md does not carry the marker this gate requires"
            }
            """);

        // The control: with a KNOWN-tracked answer the very same plan is a finding.
        Assert.Single(Findings(plan, StubGitTrackedFileProbe.Tracked));

        Assert.Empty(Findings(plan, StubGitTrackedFileProbe.NotKnown));
        Assert.Empty(Findings(plan, NullGitTrackedFileProbe.Instance));
    }

    // ══ 9. Condition 7 — the plan's own folder is harness-written territory ══════════════════════════

    /// <summary>
    /// A path under the plan folder — <c>state/</c>, <c>logs/</c>, the journal, <c>diagram.md</c> — is
    /// written by the harness itself (invariant 2) and appears in no <c>writeScope</c> by construction, so
    /// "no task declares it" is true of every one of them and would be pure noise.
    ///
    /// <para>The control names a file OUTSIDE the plan folder and fires, so the silence is attributable to
    /// the exclusion rather than to the gate never having been read. It also pins the exclusion's shape: the
    /// plan folder here is <c>docs/plans/&lt;name&gt;</c> and the firing path is <c>docs/notes.md</c>, so an
    /// implementation that excluded "anything under <c>docs/plans/</c>", or anything under the plan's
    /// PARENT, would silence the recovered positive control too.</para>
    /// </summary>
    [Fact]
    public void Silent_WhenThePathIsUnderThePlanFolder()
    {
        const string underPlan = "docs/plans/under-plan/state/answers.json";

        string plan = SyntheticPlan(_root, "under-plan", underPlan);
        WorkspaceFile(_root, underPlan, "{}\n");

        Assert.Empty(Findings(plan, StubGitTrackedFileProbe.Tracked));

        // Same plan folder depth, same everything — but a workspace file a task could legitimately own.
        string outside = SyntheticPlan(_root, "outside-plan", NotesPath);
        Assert.Single(Findings(outside, StubGitTrackedFileProbe.Tracked));
    }

    // ══ 10. Condition 10 — planIsClosed, the empty-stub-wave suppressor ══════════════════════════════

    /// <summary>
    /// <b>The suppressor that moved this plan's own positive control.</b> <c>planIsClosed</c> is false while
    /// any declared wave folder holds zero tasks: the declaration set is incomplete, a future wave may own
    /// the file, and "nothing in this plan can produce it" is simply not provable yet. Doc 19 §3.3's stated
    /// reason is exactly what then happened to <c>model-tiering-stage-2</c> — wave 2 was authored later and
    /// gained the task that owns the SSOT.
    ///
    /// <para>Both polarities, on two fixtures identical but for the stub wave's task set, so the silence is
    /// attributable to condition 10 and to nothing else. Note that this suppressor is NOT the JIT
    /// partial-prefix mitigation: a prefix of 5 authored folders out of an intended 12 has
    /// <c>planIsClosed == true</c>, which is the trap plan 33 §5.3 exists to close, and it is closed on
    /// <c>wavePrefixIsIncomplete</c> instead.</para>
    /// </summary>
    [Fact]
    public void Silent_WhenPlanIsNotClosed()
    {
        string open = PlanFolder(_root, "wave-open");
        PlanGate(open, "01-requires-a-literal.ps1", Gate("""
            $notes = Get-Content -Raw 'docs/notes.md'
            if ($notes -cnotmatch 'ProducerCoverageWitness') {
                $failures += "docs/notes.md does not carry the marker this gate requires"
            }
            """));
        WaveTask(open, "wave-01-work", "01-do-thing", "src/Guardrails.Core/Loading/PlanValidator.cs");
        WaveStub(open, "wave-02-later");
        WorkspaceFile(_root, NotesPath, "nothing to see here\n");

        Assert.Empty(Findings(open, StubGitTrackedFileProbe.Tracked));

        // The same plan once the second wave is authored: every declared wave now holds a task, the
        // declaration set is complete, and the impossibility becomes provable.
        string closed = PlanFolder(_root, "wave-closed");
        PlanGate(closed, "01-requires-a-literal.ps1", Gate("""
            $notes = Get-Content -Raw 'docs/notes.md'
            if ($notes -cnotmatch 'ProducerCoverageWitness') {
                $failures += "docs/notes.md does not carry the marker this gate requires"
            }
            """));
        WaveTask(closed, "wave-01-work", "01-do-thing", "src/Guardrails.Core/Loading/PlanValidator.cs");
        WaveTask(closed, "wave-02-later", "01-do-other-thing", "src/Guardrails.Core/Loading/PlanLoader.cs");

        Diagnostic finding = Assert.Single(Findings(closed, StubGitTrackedFileProbe.Tracked));
        Assert.Equal(Gr2060, finding.Code);
    }

    // ══ the check, driven two ways ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Every GR2060 finding for the plan at <paramref name="planDirectory"/>, computed TWICE and asserted
    /// to agree: once by calling <c>ProducerCoverage.Validate</c> directly, and once through
    /// <see cref="PlanValidator.Validate"/> over the real on-disk plan folder — the same composition root
    /// <c>PlanProbe</c> uses. That is plan 33 §8.4's anti-tautology pin. A check that exists but is not
    /// wired in passes the first and fails the second, which is #382's failure mode in miniature.
    ///
    /// <para>The probes are otherwise fakes so the run is offline and deterministic: the PATH probe resolves
    /// every interpreter and the syntax probe parses nothing, so no assertion can depend on which shells the
    /// machine has. A LOADER error means the FIXTURE is broken rather than the check, and failing loudly
    /// here keeps that from reading as a finding about GR2060.</para>
    /// </summary>
    private static List<Diagnostic> Findings(string planDirectory, IGitTrackedFileProbe gitProbe)
    {
        PlanLoadResult result = new PlanLoader().Load(planDirectory);
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        PlanDefinition? plan = result.Plan;
        Assert.NotNull(plan);

        var direct = new List<Diagnostic>();
        ProducerCoverage.Validate(plan, gitProbe, direct);

        List<Diagnostic> wired = new PlanValidator(
                FakeExecutableProbe.All,
                BannedPatternRegistry.Load(),
                NullScriptSyntaxProbe.Instance,
                gitProbe)
            .Validate(plan)
            .Where(d => d.Code == Gr2060)
            .ToList();

        Assert.Equal(direct, wired);
        return wired;
    }

    // ══ fixtures ═════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A plan folder at <c>&lt;workspaceRoot&gt;/docs/plans/&lt;name&gt;</c> whose <c>workspace</c> resolves
    /// back to <paramref name="workspaceRoot"/> — the real layout, reproduced rather than approximated, so
    /// that condition 7 is exercised against a plan folder which genuinely sits under <c>docs/plans/</c>
    /// alongside the files its gates name.
    /// <para><c>maxParallelism: 1</c> is not incidental: worktree mode makes the temp dir's git-ness
    /// (GR2015) and the terminal-gate obligation (GR2028) part of every fixture's diagnostic list, and the
    /// first of those varies with where TMP happens to live.</para>
    /// </summary>
    private static string PlanFolder(string workspaceRoot, string name)
    {
        string planDirectory = Path.Combine(workspaceRoot, "docs", "plans", name);
        Directory.CreateDirectory(planDirectory);
        File.WriteAllText(Path.Combine(planDirectory, "guardrails.json"), """
            { "version": 1, "maxParallelism": 1, "workspace": "../../.." }
            """);
        return planDirectory;
    }

    /// <summary>
    /// A plan-ROOT guardrail (SSOT §3.3) — the terminal gate, and one of the six folder instances doc 19
    /// condition 1 enumerates. Written verbatim: the recovered script carries its own <c>catches:</c>
    /// declaration and must not be rewritten on the way in.
    /// </summary>
    private static void PlanGate(string planDirectory, string fileName, string body)
    {
        string directory = Path.Combine(planDirectory, "guardrails");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, fileName), body);
    }

    /// <summary>A gate script around <paramref name="clause"/>, in the catalogue's accumulate-then-exit shape.</summary>
    private static string Gate(string clause) =>
        "# catches: a gate requiring content nothing in the plan can produce\n" +
        "$ErrorActionPreference = 'Continue'\n" +
        "$failures = @()\n" +
        clause + "\n" +
        "if ($failures.Count -gt 0) { $failures | ForEach-Object { Write-Output $_ }; exit 1 }\n" +
        "exit 0\n";

    /// <summary>
    /// A one-task plan under <see cref="_root"/> whose terminal gate is <paramref name="clause"/>, with
    /// <see cref="NotesPath"/> present in the workspace and carrying no witness. The task's
    /// <c>writeScope</c> names a file it genuinely could own, so the union is non-empty and condition 8's
    /// answer is a real "no" rather than the degenerate one an empty scope would give.
    /// </summary>
    private string SyntheticPlan(string name, string clause)
    {
        string planDirectory = PlanFolder(_root, name);
        PlanGate(planDirectory, "01-requires-a-literal.ps1", Gate(clause));
        FlatTask(planDirectory, "01-do-thing", "src/Guardrails.Core/Loading/PlanValidator.cs");
        WorkspaceFile(_root, NotesPath, "nothing to see here\n");
        return planDirectory;
    }

    /// <summary>
    /// The same one-task plan, in an explicit workspace and naming an explicit workspace file — used where
    /// the workspace is a throwaway git repository, or where the required path is the thing under test.
    /// </summary>
    private static string SyntheticPlan(string workspaceRoot, string name, string requiredPath)
    {
        string planDirectory = PlanFolder(workspaceRoot, name);
        PlanGate(planDirectory, "01-requires-a-literal.ps1", Gate(
            "$content = Get-Content -Raw '" + requiredPath + "'\n" +
            "if ($content -cnotmatch '" + NotesWitness + "') {\n" +
            "    $failures += \"" + requiredPath + " does not carry the marker this gate requires\"\n" +
            "}"));
        FlatTask(planDirectory, "01-do-thing", "src/Guardrails.Core/Loading/PlanValidator.cs");
        if (!File.Exists(Path.Combine(workspaceRoot, requiredPath.Replace('/', Path.DirectorySeparatorChar))))
        {
            WorkspaceFile(workspaceRoot, requiredPath, "nothing to see here\n");
        }

        return planDirectory;
    }

    /// <summary>
    /// The recovered plan: the real gate script and the real SSOT bytes at a commit, plus one task folder
    /// per recovered manifest carrying that manifest's real <c>writeScope</c>.
    ///
    /// <para><b>Flattened deliberately.</b> The waved layout is reproduced as a flat <c>tasks/</c> set
    /// because the union of every task's <c>writeScope</c> across every wave is precisely what condition 8
    /// resolves against, and flattening preserves that union exactly. <c>planIsClosed</c> is trivially true
    /// for a flat plan and was true at both commits (no wave folder held zero tasks), so condition 10 is not
    /// what either verdict turns on. <c>dependsOn</c> is dropped: the recovered edges are wave-qualified and
    /// carry nothing this check reads.</para>
    /// </summary>
    private string RecoveredPlan(
        string name, string gateScript, string ssotBytes, IReadOnlyList<RecoveredManifest> manifests)
    {
        string planDirectory = PlanFolder(_root, name);
        PlanGate(planDirectory, GateFileName, gateScript);
        foreach (RecoveredManifest manifest in manifests)
        {
            FlatTask(planDirectory, manifest.Id, manifest.WriteScope.ToArray());
        }

        WorkspaceFile(_root, SsotPath, ssotBytes);
        return planDirectory;
    }

    private static void FlatTask(string planDirectory, string id, params string[] writeScope) =>
        WriteTaskFolder(Path.Combine(planDirectory, "tasks", id), id, writeScope);

    private static void WaveTask(string planDirectory, string waveDir, string id, params string[] writeScope) =>
        WriteTaskFolder(Path.Combine(planDirectory, waveDir, "tasks", id), id, writeScope);

    /// <summary>
    /// A not-yet-authored JIT wave STUB: the wave folder with an empty <c>tasks/</c> and nothing else
    /// (SSOT §14.4). It loads as zero tasks with no error, which is what makes <c>planIsClosed</c> false.
    /// </summary>
    private static void WaveStub(string planDirectory, string waveDir) =>
        Directory.CreateDirectory(Path.Combine(planDirectory, waveDir, "tasks"));

    private static void WriteTaskFolder(string taskDirectory, string id, string[] writeScope)
    {
        Directory.CreateDirectory(Path.Combine(taskDirectory, "guardrails"));

        File.WriteAllText(Path.Combine(taskDirectory, "task.json"), $$"""
            {
              "description": "{{id}}",
              "writeScope": {{JsonSerializer.Serialize(writeScope)}}
            }
            """);

        File.WriteAllText(Path.Combine(taskDirectory, "action.sh"), "#!/bin/sh\nexit 0\n");
        File.WriteAllText(Path.Combine(taskDirectory, "guardrails", "01-verifies.sh"),
            "# catches: a change that was never verified\nexit 0\n");
    }

    /// <summary>Write (or overwrite) a workspace file at a '/'-separated workspace-relative path.</summary>
    private static void WorkspaceFile(string workspaceRoot, string relativePath, string content)
    {
        string full = Path.Combine(workspaceRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    /// <summary>A <see cref="IGitTrackedFileProbe"/> that gives every path the same answer.</summary>
    private sealed class StubGitTrackedFileProbe : IGitTrackedFileProbe
    {
        /// <summary>Everything is tracked — the answer the recovered control's real SSOT genuinely has.</summary>
        internal static readonly StubGitTrackedFileProbe Tracked = new(true);

        /// <summary>The answer git cannot give: not-known, which must never be read as "untracked".</summary>
        internal static readonly StubGitTrackedFileProbe NotKnown = new(null);

        private readonly bool? _answer;

        private StubGitTrackedFileProbe(bool? answer) => _answer = answer;

        public IReadOnlyDictionary<string, bool?> AreTracked(IReadOnlyList<string> workspaceRelativePaths)
        {
            var answers = new Dictionary<string, bool?>(StringComparer.Ordinal);
            foreach (string path in workspaceRelativePaths)
            {
                answers[path] = _answer;
            }

            return answers;
        }
    }

    // ══ git ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>One task manifest as it stood at a commit: the folder name, and the scope it declared.</summary>
    private sealed record RecoveredManifest(string Id, IReadOnlyList<string> WriteScope);

    /// <summary>
    /// The repository this test file was compiled from — two levels above the test project. Every git read
    /// runs there rather than in the process working directory, so the reads do not depend on where the
    /// runner happens to have been launched.
    /// </summary>
    private static readonly string RepositoryRoot =
        Path.GetFullPath(Path.Combine(TestPaths.ProjectDir, "..", ".."));

    private static readonly ConcurrentDictionary<string, string> ShowCache = new(StringComparer.Ordinal);

    private static readonly ConcurrentDictionary<string, IReadOnlyList<RecoveredManifest>> ManifestCache =
        new(StringComparer.Ordinal);

    /// <summary>Can git run here at all? Its absence skips the real-seam pin rather than failing it.</summary>
    private static readonly bool GitIsUsable = RunGit(RepositoryRoot, "--version").ExitCode == 0;

    /// <summary>
    /// Are the recovered pair's commits present? A shallow clone — <c>actions/checkout</c> defaults to
    /// <c>fetch-depth: 1</c> — has neither, and the recovered controls then cannot be run at all. They skip
    /// with a reason naming the fix rather than substituting a fixture for the evidence.
    /// </summary>
    private static readonly bool HistoryIsAvailable =
        GitIsUsable && CommitIsPresent(FiringCommit) && CommitIsPresent(SilentCommit);

    private static void SkipUnlessHistoryIsAvailable() =>
        Assert.SkipUnless(HistoryIsAvailable,
            $"the recovered control's commits ({FiringCommit}, {SilentCommit}) are not in this checkout — " +
            "a shallow clone cannot read them. Run `git fetch --unshallow` (or set `fetch-depth: 0`) to " +
            "restore the evidence; this control is never satisfied from a hand-written fixture.");

    private static bool CommitIsPresent(string commit) =>
        RunGit(RepositoryRoot, "cat-file", "-e", commit + "^{commit}").ExitCode == 0;

    /// <summary>
    /// The bytes of one blob at one commit, read as <c>git show &lt;sha&gt;:&lt;path&gt;</c>. Cached because
    /// history is immutable and the SSOT blob runs to hundreds of kilobytes.
    /// </summary>
    private static string GitShow(string revisionAndPath) =>
        ShowCache.GetOrAdd(revisionAndPath, static rev =>
        {
            (int exitCode, string stdout, string stderr) = RunGit(RepositoryRoot, "show", rev);
            Assert.True(exitCode == 0, $"git show {rev} exited {exitCode}: {stderr}");
            return stdout;
        });

    /// <summary>
    /// Every <c>task.json</c> under <c>model-tiering-stage-2</c> at <paramref name="commit"/>, enumerated
    /// from git's own tree rather than from a list written down here — so the count and the scopes are
    /// facts about the repository, and a manifest added or removed shows up as a changed count rather than
    /// as a quietly weaker test.
    /// </summary>
    private static IReadOnlyList<RecoveredManifest> RecoveredManifests(string commit) =>
        ManifestCache.GetOrAdd(commit, static rev =>
        {
            (int exitCode, string stdout, string stderr) =
                RunGit(RepositoryRoot, "ls-tree", "-r", "--name-only", rev, "--", "docs/plans/model-tiering-stage-2");
            Assert.True(exitCode == 0, $"git ls-tree {rev} exited {exitCode}: {stderr}");

            var manifests = new List<RecoveredManifest>();
            foreach (string line in stdout.Split('\n'))
            {
                string path = line.Trim();
                if (!path.EndsWith("/task.json", StringComparison.Ordinal))
                {
                    continue;
                }

                string id = path[..^"/task.json".Length];
                id = id[(id.LastIndexOf('/') + 1)..];
                manifests.Add(new RecoveredManifest(id, WriteScopeOf(GitShow(rev + ":" + path))));
            }

            return manifests;
        });

    /// <summary>The <c>writeScope</c> array of one recovered manifest; an absent key reads as empty.</summary>
    private static IReadOnlyList<string> WriteScopeOf(string manifestJson)
    {
        using JsonDocument document = JsonDocument.Parse(manifestJson, new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        });

        if (!document.RootElement.TryGetProperty("writeScope", out JsonElement scope) ||
            scope.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return [.. scope.EnumerateArray().Select(e => e.GetString() ?? string.Empty)];
    }

    private static (int ExitCode, string Stdout, string Stderr) RunGit(string workingDirectory, params string[] arguments)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (string argument in arguments)
        {
            psi.ArgumentList.Add(argument);
        }

        try
        {
            using Process? process = Process.Start(psi);
            if (process is null)
            {
                return (-1, string.Empty, "git could not be started.");
            }

            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            return (process.ExitCode, stdout, stderr);
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            return (-1, string.Empty, e.Message);
        }
        catch (IOException e)
        {
            return (-1, string.Empty, e.Message);
        }
    }

    /// <summary>
    /// Point every git child process at <paramref name="repoRoot"/> for the duration of
    /// <paramref name="body"/>. <c>GIT_DIR</c>/<c>GIT_WORK_TREE</c> rather than the process working
    /// directory: the CWD is global state that other tests read, these two are read by nothing else in this
    /// suite, and the environment write is still process-wide so it is gated and restored regardless.
    /// </summary>
    private static void WithGitPointedAt(string repoRoot, Action body)
    {
        lock (GitEnvironmentGate)
        {
            string? gitDir = Environment.GetEnvironmentVariable("GIT_DIR");
            string? workTree = Environment.GetEnvironmentVariable("GIT_WORK_TREE");
            try
            {
                Environment.SetEnvironmentVariable("GIT_DIR", Path.Combine(repoRoot, ".git"));
                Environment.SetEnvironmentVariable("GIT_WORK_TREE", repoRoot);
                body();
            }
            finally
            {
                Environment.SetEnvironmentVariable("GIT_DIR", gitDir);
                Environment.SetEnvironmentVariable("GIT_WORK_TREE", workTree);
            }
        }
    }

    private static readonly object GitEnvironmentGate = new();

    /// <summary>
    /// A throwaway single-use git repository in a temp directory, mirroring the one duplicated across
    /// <c>Guardrails.Integration.Tests</c> (there is no shared fixture to reuse from this project).
    ///
    /// <para>Two Windows behaviours it must handle. Git marks loose objects under <c>.git/objects</c>
    /// READ-ONLY, so a recursive delete throws <see cref="UnauthorizedAccessException"/> — not
    /// <see cref="IOException"/> — unless the attributes are cleared first. And <c>core.autocrlf</c> is
    /// forced OFF so fixture content is byte-stable across platforms. Hooks are pointed at an empty
    /// directory inside <c>.git</c> so a machine-global <c>core.hooksPath</c> cannot reach in here.</para>
    /// </summary>
    private sealed class TempGitRepo : IDisposable
    {
        internal string RepoPath { get; }

        internal TempGitRepo()
        {
            RepoPath = Path.Combine(Path.GetTempPath(), "gr-producer-coverage-repo-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(RepoPath);

            Git("init");
            string hooks = Path.Combine(RepoPath, ".git", "no-hooks");
            Directory.CreateDirectory(hooks);
            Git("config", "core.hooksPath", hooks);
            Git("config", "core.autocrlf", "false");
            Git("config", "commit.gpgsign", "false");
            Git("config", "user.email", "test@guardrails.local");
            Git("config", "user.name", "Guardrails Test");

            CommitFile("README.md", "# fixture repo\n", "Initial commit");
        }

        /// <summary>Write a working-tree file without staging it — an UNTRACKED path, by construction.</summary>
        internal void WriteWorkingFile(string relativePath, string content) =>
            WorkspaceFile(RepoPath, relativePath, content);

        internal void CommitFile(string relativePath, string content, string message)
        {
            WriteWorkingFile(relativePath, content);
            Git("add", relativePath);
            Git("commit", "-m", message);
        }

        private void Git(params string[] arguments)
        {
            (int exitCode, _, string stderr) = RunGit(RepoPath, arguments);
            Assert.True(exitCode == 0, $"git {string.Join(' ', arguments)} exited {exitCode}: {stderr}");
        }

        public void Dispose() => DeleteTree(RepoPath);
    }

    /// <summary>
    /// Best-effort recursive delete that first clears read-only attributes — git's loose objects are marked
    /// read-only on Windows and <see cref="Directory.Delete(string, bool)"/> throws
    /// <see cref="UnauthorizedAccessException"/> on them, which is NOT the exception the obvious catch
    /// clause names.
    /// </summary>
    private static void DeleteTree(string root)
    {
        try
        {
            if (!Directory.Exists(root))
            {
                return;
            }

            foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(root, recursive: true);
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort temp cleanup.
        }
        catch (IOException)
        {
            // Best-effort temp cleanup.
        }
    }
}
