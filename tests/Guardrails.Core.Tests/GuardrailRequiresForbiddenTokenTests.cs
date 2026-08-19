using Guardrails.Core.Loading;
using Guardrails.Core.Model;

namespace Guardrails.Core.Tests;

/// <summary>
/// GR2057 (issue #470 ask 1) — a guardrail that REQUIRES a token it also FORBIDS. A required-present clause
/// and a forbidden-present clause collide on the same character sequence, so the guardrail is satisfiable by
/// NO file at all: removing the text fails the first clause, keeping it fails the second.
///
/// <para>The motivating case was authored unattended by the JIT auto-breakdown and found by EXECUTING the
/// guardrail during <c>/guardrails-review</c> — reading it did not reveal it, because the two clauses sat 40
/// lines apart and each was individually correct. Its task authored a wave's conformance suite that three
/// downstream tasks depended on, so one unsatisfiable regex would have dead-ended the whole chain after
/// paying the task's full retry budget.</para>
///
/// <para>Invisible to the #479 execution probes for the same reason as GR2055 and GR2056: such a guardrail is
/// red before the task runs, which is correct, and red forever, which is not — and a baseline probe cannot
/// distinguish those.</para>
///
/// <para>MOST of these tests are FALSE-POSITIVE guards, and the load-bearing one is
/// <see cref="PrescribedTwoVariableFix_StaysSilent"/>: a lint that fires on the very remedy its own message
/// recommends is worse than no lint. A validator that cries wolf gets ignored, and its true positives are
/// lost with it.</para>
/// </summary>
public sealed class GuardrailRequiresForbiddenTokenTests : IDisposable
{
    private readonly string _tempRoot = Directory.CreateTempSubdirectory("gr2057-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); }
        catch (IOException) { /* best-effort temp cleanup */ }
        catch (UnauthorizedAccessException) { /* best-effort temp cleanup */ }
    }

    /// <summary>
    /// THE MEASURED INSTANCE, verbatim: the guardrail as it was committed to
    /// <c>model-tiering-stage-2/wave-02-attempt-launch-wiring/tasks/06-author-tests-stage2-conformance/</c>
    /// (recovered from git). Line 25 requires a <c>[Trait("Category", "TierResolution")]</c> attribute whose
    /// own STRING LITERAL carries the token line 66 forbids. Nothing else in the file collides, so the real
    /// artifact must produce EXACTLY ONE finding — the same bar GR2055/GR2056 were held to.
    /// </summary>
    [Fact]
    public void RealHistoricalDefect_Wave02Task06_FiresOnceAndNamesBothSides()
    {
        GuardrailDefinition guardrail = WriteScript("01-covers-required-behaviors", HistoricalTask06Guardrail);

        Diagnostic d = Assert.Single(Validate(guardrail),
            x => x.Code == DiagnosticCodes.GuardrailRequiresForbiddenToken);

        // Both COLLIDING SIDES must be named — a reader who is told only "unsatisfiable" still has to find
        // the pair themselves, and finding the pair is the entire difficulty (40 lines apart, each correct).
        Assert.Contains("line 25", d.Message, StringComparison.Ordinal);
        Assert.Contains("line 66", d.Message, StringComparison.Ordinal);
        Assert.Contains("""[Trait("Category","TierResolution")]""", d.Message, StringComparison.Ordinal);
        Assert.Contains("TierResolver|TierResolution", d.Message, StringComparison.Ordinal);
        Assert.Contains("$content", d.Message, StringComparison.Ordinal);
        Assert.Equal(DiagnosticSeverity.Error, d.Severity);
    }

    /// <summary>The same collision reduced to its two clauses — the shape, stripped of the surrounding suite.</summary>
    [Fact]
    public void RequiredLiteralTrippingItsOwnForbiddenPattern_Fires()
    {
        GuardrailDefinition guardrail = WriteScript("01-collide",
            """
            $content = Get-Content -Raw $file
            $failures = @()
            if ($content -notmatch '\[Trait\s*\(\s*"Category"\s*,\s*"TierResolution"\s*\)\s*\]') {
                $failures += 'the class carries no [Trait("Category", "TierResolution")]'
            }
            if ($content -match 'TierResolver|TierResolution') {
                $failures += 'the suite references TierResolver/TierResolution - FORBIDDEN'
            }
            if ($failures.Count -gt 0) { exit 1 }
            exit 0
            """);

        Assert.Single(Validate(guardrail), x => x.Code == DiagnosticCodes.GuardrailRequiresForbiddenToken);
    }

    /// <summary>A required literal is not a token: an ordinary word ban colliding with a required phrase fires too.</summary>
    [Fact]
    public void ForbiddenBareWordInsideARequiredPhrase_Fires()
    {
        GuardrailDefinition guardrail = WriteScript("01-phrase",
            """
            if ($src -notmatch 'using Guardrails\.Core\.Execution;') { Write-Output 'no using'; exit 1 }
            if ($src -match 'Execution') { Write-Output 'FORBIDDEN'; exit 1 }
            exit 0
            """);

        Assert.Single(Validate(guardrail), x => x.Code == DiagnosticCodes.GuardrailRequiresForbiddenToken);
    }

    // ============================================================================================
    // FALSE-POSITIVE GUARDS
    // ============================================================================================

    /// <summary>
    /// THE LOAD-BEARING GUARD — the catalogue's prescribed fix for this exact defect (#470). The two-variable
    /// rule: the REQUIRED clause reads <c>$code</c> (comments stripped, so the trait's own literal survives)
    /// and the FORBIDDEN clause reads <c>$scan</c> (comments AND string literals stripped), anchored on a USE
    /// rather than a mention. Two different TEXTS, so nothing here is proven unsatisfiable. GR2057 must be
    /// silent on the remedy its own message recommends.
    /// </summary>
    [Fact]
    public void PrescribedTwoVariableFix_StaysSilent()
    {
        GuardrailDefinition guardrail = WriteScript("01-fixed",
            """
            $raw  = Get-Content $f -Raw
            $code = [regex]::Replace($raw,  '/\*[\s\S]*?\*/', '')
            $scan = [regex]::Replace($code, '"(\\.|[^"\\])*"', '""')
            if ($code -notmatch '\[Trait\s*\(\s*"Category"\s*,\s*"TierResolution"\s*\)\s*\]') {
                Write-Output 'no trait'; exit 1
            }
            if ($scan -match 'TierResolver\s*\.|(?<![\w.])TierResolution(?![\w"])') {
                Write-Output 'USES the forbidden resolver'; exit 1
            }
            exit 0
            """);

        AssertSilent(guardrail);
    }

    /// <summary>
    /// The anchor-on-a-USE half of the fix, applied WITHOUT the second variable. The lookarounds see the
    /// witness's real neighbouring characters — the trait's own quotes — so the ban does not trip and the two
    /// clauses genuinely coexist. Lookarounds are therefore deliberately honoured, not skipped.
    /// </summary>
    [Fact]
    public void ForbiddenPatternAnchoredOnAUse_CoexistsWithTheRequiredTrait_StaysSilent()
    {
        GuardrailDefinition guardrail = WriteScript("01-anchored",
            """
            if ($content -notmatch '\[Trait\s*\(\s*"Category"\s*,\s*"TierResolution"\s*\)\s*\]') {
                $failures += 'no trait'
            }
            if ($content -match 'TierResolver\s*\.|(?<![\w.])TierResolution(?![\w"])') {
                $failures += 'USES the forbidden resolver'
            }
            if ($failures.Count -gt 0) { exit 1 }
            exit 0
            """);

        AssertSilent(guardrail);
    }

    /// <summary>
    /// A required pattern that does not pin ONE exact string — <c>\[(Fact|Theory)\]</c> — yields no witness,
    /// so it is dropped rather than guessed at. This clause is a real neighbour of the measured one.
    /// </summary>
    [Fact]
    public void RequiredPatternWithAlternation_YieldsNoWitness_StaysSilent()
    {
        GuardrailDefinition guardrail = WriteScript("01-alternation",
            """
            if ($content -notmatch '\[(Fact|Theory)\]') { $failures += 'no test attribute' }
            if ($content -match 'Fact') { $failures += 'FORBIDDEN' }
            if ($failures.Count -gt 0) { exit 1 }
            exit 0
            """);

        AssertSilent(guardrail);
    }

    /// <summary>
    /// A composed operand — the measured file's own per-method loop. The pattern is not statically known, so
    /// no clause is extracted from it at all.
    /// </summary>
    [Fact]
    public void ComposedNonLiteralOperand_StaysSilent()
    {
        GuardrailDefinition guardrail = WriteScript("01-composed",
            """
            foreach ($m in $methods) {
                if ($content -notmatch ("(?m)\b" + [regex]::Escape($m) + "\s*\(")) { $failures += "no $m" }
            }
            if ($content -match 'Resolution_RunsPerAttempt') { $failures += 'FORBIDDEN' }
            if ($failures.Count -gt 0) { exit 1 }
            exit 0
            """);

        AssertSilent(guardrail);
    }

    /// <summary>
    /// POLARITY: a <c>-match</c> branch that RECORDS rather than fails is a REQUIREMENT wearing the other
    /// operator. Reading polarity off the operator alone would invert it and report a collision that is not
    /// one — the richest false-positive source available here.
    /// </summary>
    [Fact]
    public void MatchBranchThatRecordsRatherThanFails_IsNotAProhibition_StaysSilent()
    {
        GuardrailDefinition guardrail = WriteScript("01-records",
            """
            $sawTrait = $false
            if ($content -notmatch 'namespace Guardrails\.Tests;') { $failures += 'no namespace' }
            if ($content -match 'Guardrails') { $sawTrait = $true }
            if (-not $sawTrait) { $failures += 'no marker' }
            if ($failures.Count -gt 0) { exit 1 }
            exit 0
            """);

        AssertSilent(guardrail);
    }

    /// <summary>
    /// Different SUBJECT variables scan different text — the general form of the two-variable rule. A
    /// collision between them is not proven by anything in the script.
    /// </summary>
    [Fact]
    public void ClausesOverDifferentSubjectVariables_StaySilent()
    {
        GuardrailDefinition guardrail = WriteScript("01-two-subjects",
            """
            if ($header -notmatch 'TierResolution Category') { $failures += 'no header' }
            if ($body -match 'TierResolution') { $failures += 'FORBIDDEN' }
            if ($failures.Count -gt 0) { exit 1 }
            exit 0
            """);

        AssertSilent(guardrail);
    }

    /// <summary>
    /// A COMPOUND condition is a verdict on the conjunction, not on this pattern, so taking the branch does
    /// not prove the pattern is required.
    /// </summary>
    [Fact]
    public void CompoundCondition_StaysSilent()
    {
        GuardrailDefinition guardrail = WriteScript("01-compound",
            """
            if (($content -notmatch 'TierResolution Category') -and $strict) { $failures += 'no header' }
            if ($content -match 'TierResolution') { $failures += 'FORBIDDEN' }
            if ($failures.Count -gt 0) { exit 1 }
            exit 0
            """);

        AssertSilent(guardrail);
    }

    /// <summary>
    /// #97: a header comment DESCRIBING the collision — precisely what a fixed guardrail's <c>catches:</c>
    /// block should say — must never be what reports it.
    /// </summary>
    [Fact]
    public void CollisionDescribedOnlyInAComment_StaysSilent()
    {
        GuardrailDefinition guardrail = WriteScript("01-comment",
            """
            # An earlier revision paired
            #   if ($content -notmatch '\[Trait\s*\(\s*"Category"\s*,\s*"TierResolution"\s*\)\s*\]') { $failures += 'x' }
            # with
            #   if ($content -match 'TierResolver|TierResolution') { $failures += 'y' }
            # which no file could satisfy. Recorded so the next author does not reintroduce it (#470).
            if ($content -notmatch 'Stage2PlanHarness') { $failures += 'no harness' }
            if ($failures.Count -gt 0) { exit 1 }
            exit 0
            """);

        AssertSilent(guardrail);
    }

    /// <summary>
    /// An ANCHORED forbidden pattern cannot be soundly tested against a standalone witness: in a real file
    /// the required text is embedded, so a line anchor that looks satisfied here need not be there.
    /// </summary>
    [Fact]
    public void AnchoredForbiddenPattern_StaysSilent()
    {
        GuardrailDefinition guardrail = WriteScript("01-anchored-ban",
            """
            if ($content -notmatch '\[Trait\s*\(\s*"Category"\s*,\s*"TierResolution"\s*\)\s*\]') {
                $failures += 'no trait'
            }
            if ($content -match '(?m)^TierResolution') { $failures += 'FORBIDDEN' }
            if ($failures.Count -gt 0) { exit 1 }
            exit 0
            """);

        AssertSilent(guardrail);
    }

    /// <summary>The ordinary healthy case: a required clause and a forbidden clause that simply do not collide.</summary>
    [Fact]
    public void RequiredAndForbiddenThatDoNotCollide_StaySilent()
    {
        GuardrailDefinition guardrail = WriteScript("01-healthy",
            """
            if ($content -notmatch '\[Trait\s*\(\s*"Category"\s*,\s*"Conformance"\s*\)\s*\]') {
                $failures += 'no trait'
            }
            if ($content -match 'TierResolver|TierResolution') { $failures += 'FORBIDDEN' }
            if ($failures.Count -gt 0) { exit 1 }
            exit 0
            """);

        AssertSilent(guardrail);
    }

    /// <summary>A witness too short to mean anything must not be reconciled against a ban.</summary>
    [Fact]
    public void TinyRequiredLiteral_StaysSilent()
    {
        GuardrailDefinition guardrail = WriteScript("01-tiny",
            """
            if ($content -notmatch '\{') { $failures += 'no brace' }
            if ($content -match '\{') { $failures += 'FORBIDDEN' }
            if ($failures.Count -gt 0) { exit 1 }
            exit 0
            """);

        AssertSilent(guardrail);
    }

    // ============================================================================================
    // Helpers
    // ============================================================================================

    /// <summary>
    /// The measured instance as committed (git <c>71a298d</c>). Kept VERBATIM — a reduced copy would not
    /// prove the lint survives the 78 lines of correct neighbouring clauses that hid it.
    /// </summary>
    private const string HistoricalTask06Guardrail =
        """
        # catches: the two ways this suite silently stops being the wave's proof.
        #          (1) A RENAMED or MISSING clause. The plan terminal gate discovers this suite by class name
        #              and matches each required behaviour against discovered TEST NAMES, and tasks 07/08/09
        #              each select their own subset by method name. A rename is invisible until the terminal
        #              gate - where there is no retry budget and mergeOnSuccess withholds delivery.
        #          (2) A suite that proves the RESOLVER instead of the WIRING (#382). Asking TierResolver what
        #              it would have chosen and asserting the answer passes over a completely UNWIRED
        #              executor - a green light over a broken wire, and precisely the failure this wave
        #              exists to close.
        #          Structural, scoped to the ONE file this task owns; it runs BEFORE the expensive test
        #          execution in the sibling guardrails.
        $ErrorActionPreference = 'Continue'
        $file = 'tests/Guardrails.Integration.Tests/ModelTiering/Stage2ConformanceTests.cs'
        $failures = @()

        if (-not (Test-Path $file)) {
            Write-Output "$file does not exist - this suite IS the wave's deliverable, and the plan terminal gate fails every required-behaviour clause at once without it"
            exit 1
        }
        $content = Get-Content -Raw $file

        if ($content -notmatch '(?m)class\s+Stage2ConformanceTests\b') {
            $failures += 'no `class Stage2ConformanceTests` declaration - the terminal gate and every downstream filter discover the suite by that exact name'
        }
        if ($content -notmatch '\[Trait\s*\(\s*"Category"\s*,\s*"TierResolution"\s*\)\s*\]') {
            $failures += 'the class carries no [Trait("Category", "TierResolution")] - required so the plan-root Integration baseline preflight (Category!=TierResolution) excludes this plan''s own intentionally-red suite'
        }
        if ($content -notmatch '\[(Fact|Theory)\]') {
            $failures += 'no [Fact]/[Theory] attribute - the file declares no executable test'
        }

        # The nine pinned method names. These ARE the contract: tasks 07/08/09 select their subsets by these
        # strings, and the terminal gate matches its behaviour manifest against them.
        $methods = @(
            'Resolution_RunsPerAttempt_AndReachesAttemptProvenance',
            'ResolverCandidacy_AgreesWith_ServesTier_Predicate',
            'Invariant7_RoutingEnabledConfig_ZeroTagPlan_UsesLegacyPath_WithNoTierActivity',
            'D30_TieredPlan_ClimbsToStrongerRung_AndNeverFallsBackToLegacy',
            'D31_FullPin_RecordsTierSourceOverride_WithProvenanceTierAbsent',
            'Climb_ToStrongerRung_IsRecordedInProvenance',
            'NoCandidateAtOrAboveRung_SettlesNoRoute_AsNeedsHuman',
            'Reattempt_BoundByCostlyCeiling_WarnsNamingTheExcludedOnlyForCostBlock',
            'Climb_ToStrongerRung_EmitsLoudWarningLine'
        )
        foreach ($m in $methods) {
            # Declaration form, not a bare mention: `... MethodName(` - a name in a comment must not satisfy it.
            if ($content -notmatch ("(?m)\b" + [regex]::Escape($m) + "\s*\(")) {
                $failures += "no method declaration named '$m' - the task prompt pins these nine names verbatim; a rename is not selected by its owning task's --filter (its zero-match guard then fires) and fails the terminal gate's behaviour manifest"
            }
        }

        # The real-seam surfaces every clause observes the route through.
        if ($content -notmatch 'Stage2PlanHarness') {
            $failures += 'the suite does not use Stage2PlanHarness - task 05 authored it precisely so this suite drives the REAL PlanLoader/Scheduler/TaskExecutor rather than a second, weaker host'
        }
        if ($content -notmatch '(?i)Provenance') {
            $failures += 'no assertion touches per-attempt Provenance - it is the machine-readable copy of the route and where every non-log clause must be made'
        }
        if ($content -notmatch 'attempt-route\.log') {
            $failures += 'nothing reads attempt-route.log - clauses 8 and 9 assert the D28 ceiling warning and the climb warning from the attempt log dir, and that file name is the surface tasks 07/09 implement to'
        }

        # NEGATIVE ASSERTION (#176) - the prohibition the whole suite's worth rests on. GR2026 stays
        # correctly silent on a fail-on-present keyword (it flags only POSITIVE coverage tokens), so do not
        # weaken this to quiet a lint.
        if ($content -match 'TierResolver|TierResolution') {
            $failures += 'the suite references TierResolver/TierResolution - FORBIDDEN. Asking the resolver what it would have chosen and asserting the answer PASSES against a completely unwired executor: it proves the resolver (wave 1 already did) and says nothing about whether anything calls it. Observe the route through the journal, the captured PromptInvocation, and attempt-route.log'
        }

        if ($failures.Count -gt 0) {
            Write-Output ""
            Write-Output "=== Stage2ConformanceTests contract: $($failures.Count) finding(s) ==="
            $failures | ForEach-Object { Write-Output "  - $_" }
            Write-Output ""
            Write-Output "this suite is the wave's real-seam proof and the plan terminal gate's behaviour manifest reads it by NAME. Fix the findings above before the wiring tasks build on it."
            exit 1
        }
        exit 0
        """;

    private IReadOnlyList<Diagnostic> Validate(GuardrailDefinition guardrail) =>
        new PlanValidator(FakeExecutableProbe.All, new BannedPatternRegistry([]))
            .Validate(PlanWithTaskGuardrail(guardrail));

    private void AssertSilent(GuardrailDefinition guardrail) =>
        Assert.DoesNotContain(Validate(guardrail),
            d => d.Code == DiagnosticCodes.GuardrailRequiresForbiddenToken);

    private GuardrailDefinition WriteScript(string name, string body)
    {
        string dir = Path.Combine(_tempRoot, "tasks", "01-a", "guardrails");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, name + ".ps1");
        File.WriteAllText(path, body);
        return new GuardrailDefinition { Name = name, Path = path, Kind = ActionKind.Script };
    }

    private PlanDefinition PlanWithTaskGuardrail(GuardrailDefinition guardrail)
    {
        TaskNode task = new()
        {
            Id = "01-a",
            Directory = Path.Combine(_tempRoot, "tasks", "01-a"),
            Description = "task 01-a",
            Action = new ActionDefinition { Path = Path.Combine(_tempRoot, "tasks", "01-a", "action.ps1"), Kind = ActionKind.Script },
            Guardrails = [guardrail],
            Preflights = [],
        };

        return new PlanDefinition
        {
            PlanDirectory = _tempRoot,
            Workspace = _tempRoot,
            // Serial so the worktree-mode git-root/terminal-gate checks stay silent; GR2057 is the rule under test.
            Config = new RunConfig { Version = 1, MaxParallelism = 1 },
            Tasks = [task],
            PlanPreflights = [],
            PlanGuardrails = [],
        };
    }
}
