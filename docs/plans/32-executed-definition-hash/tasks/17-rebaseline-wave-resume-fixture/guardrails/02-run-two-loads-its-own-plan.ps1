# catches: a re-baseline that reached green some way OTHER than making run 2 load its own plan - and,
#          just as important, one that reached it by deleting or weakening what these two tests assert.
#          The shapes it closes:
#            1. the `File.WriteAllText` edit removed, or moved before run 1. Both make the divergence go
#               away and both delete the thing under test - the same rule section 11 states for every
#               timing fixture in this plan;
#            2. an assertion dropped or softened so the test passes under either semantics. Both methods
#               make real product claims (an auto-policy wave rewind re-runs the drifted wave and stays
#               green; an edit to an all-pending future wave is not drift), and neither claim is what
#               changed;
#            3. a test deleted or [Fact(Skip=...)]d outright;
#            4. run 2 still reusing run 1's in-memory plan - the defect itself, untouched.
#
# WHY A SOURCE-SHAPE CHECK OUTRANKS A TEST HERE, stated plainly because this plan is otherwise strict
#          about the #468 demotion order and this is the one row that inverts it:
#
#              Both tests PASS today and PASS after the fix.
#
#          The behavioural difference exists only once stage 13's gate lands, and stage 13 dependsOn THIS
#          task - so at the moment this guardrail runs there is no runtime signal to assert on at all.
#          Guardrail 03 runs the two tests anyway, but as a declared REGRESSION clause (green before,
#          green after); it is not the load-bearing check and its own header says so. The load-bearing
#          property is the fixture's SHAPE - that run 2 re-reads the folder, as a real resume does - and
#          the only artifact that can carry it right now is the source. It ships with a committed
#          .valid/.invalid pair in ../samples/, and the INVALID half is the file exactly as it stands
#          today, which makes the pair genuinely two-sided rather than synthetic.
#
# TWO-LEVEL STRIP (section 11a): $raw is NEVER matched against and never reassigned. Every clause reads
#          $code (comments gone, string literals INTACT - load-bearing, because the fixture's on-disk edit
#          is identified by the file path it writes, which is a string literal).
#
# MEASURED BASELINES on the shipped file at plan-authoring time, case-SENSITIVE (#478). These are the
#          BEFORE state: this is a shipped file this stage EDITS, so the retention clauses are expected
#          nonzero and the one deliverable clause is expected BELOW its floor:
#            ^\s*\[Fact                                              14   EXPECTED nonzero - a retention
#                                                                         clause. 14 before, 14 after;
#                                                                         this stage edits two methods,
#                                                                         not the test set.
#            b.Load() in AutoPolicy method                            1   THE DELIVERABLE. Must become 2.
#            b.Load() in PendingFutureWaveEdit method                 1   THE DELIVERABLE. Must become 2.
#            Assert. in AutoPolicy method                             4   EXPECTED nonzero - retention.
#            Assert. in PendingFutureWaveEdit method                  3   EXPECTED nonzero - retention.
#            File.WriteAllText in each of the two methods             1   EXPECTED nonzero - retention.
#                                                                         The edit IS the fixture.
#            (?i)\[Fact\s*\(\s*Skip                                   0   forbidden-present.
$ErrorActionPreference = 'Continue'

$ws = $env:GUARDRAILS_WORKSPACE
if ([string]::IsNullOrEmpty($ws)) { $ws = (Get-Location).Path }

# GR_SUBJECT is the `guardrails samples verify` contract (Samples/SampleVerifier.cs): the sample path
# arrives as argv[0] AND in $env:GR_SUBJECT, ABSOLUTE. Joining it to the workspace would yield a nonsense
# path and PRECONDITION-fail, which reads exactly like a real finding.
#   $env:GR_SUBJECT='<plan>/tasks/17-rebaseline-wave-resume-fixture/samples/02-run-two-loads-its-own-plan.valid.cs'   -> expect 0
#   $env:GR_SUBJECT='<plan>/tasks/17-rebaseline-wave-resume-fixture/samples/02-run-two-loads-its-own-plan.invalid.cs' -> expect 1
# RE-RUN EVERY case after ANY edit to this file, not just the clause you touched.
$rel  = if ($env:GR_SUBJECT) { $env:GR_SUBJECT } else { 'tests/Guardrails.Core.Tests/SchedulerWaveExecutionTests.cs' }
$full = if ([System.IO.Path]::IsPathRooted($rel)) { $rel } else { Join-Path $ws $rel }

# PRECONDITION - the one legitimate early exit: without the subject every clause below is meaningless.
if (-not (Test-Path -LiteralPath $full -PathType Leaf)) {
    Write-Output "PRECONDITION: $rel does not exist. It is a SHIPPED file this stage edits in place - if it is gone it was deleted rather than re-baselined, which this stage forbids outright."
    exit 1
}

$raw  = Get-Content -Raw -LiteralPath $full                  # NEVER matched against, never reassigned
$code = [regex]::Replace($raw,  '/\*[\s\S]*?\*/', ' ')       # /* */ block comments
$code = [regex]::Replace($code, '(?m)//[^\r\n]*', ' ')       # // and /// line comments

# ACCUMULATE (#478): one distinguishable message per clause, dumped once.
$failures = @()

# --- Member regions -------------------------------------------------------------------------------
# Same cutter as stage 5's and stage 13's siblings, including the reason it cuts each region at its
# OPENING BRACE rather than at a fixed character window: measured at author time in stage 5, a head
# window matched a CALL inside another member's body and returned the wrong region entirely.
$declStarts = [regex]::Matches($code, '(?m)^    (?:public|private|internal|protected)\b')
$regions    = @()
for ($i = 0; $i -lt $declStarts.Count; $i++) {
    $start = $declStarts[$i].Index
    $end   = if ($i + 1 -lt $declStarts.Count) { $declStarts[$i + 1].Index } else { $code.Length }
    $regions += ,$code.Substring($start, $end - $start)
}

function Get-MemberRegion {
    param([string[]] $Regions, [string] $Member)
    foreach ($region in $Regions) {
        $brace = [regex]::Match($region, '(?m)^    \{')
        $sig   = if ($brace.Success) { $region.Substring(0, $brace.Index) } else { $region }
        # -cmatch: C# identifiers are case-SENSITIVE and PowerShell -match is not (taxonomy 3).
        if ($sig -cmatch ('\b' + [regex]::Escape($Member) + '\s*[(<]')) { return $region }
    }
    return $null
}

# --- The two methods, and what each must look like afterwards ---------------------------------------
$methods = @(
    @{ Name     = 'WaveDrift_CompletedWaveChanged_AutoPolicy_RewindsAndReRuns_WithWaveBoundaryDecision'
       Asserts  = 4
       Claim    = 'an auto-policy wave rewind re-runs the drifted wave and stays green' },
    @{ Name     = 'PendingFutureWaveEdit_IsNotDrift_RunsNormally'
       Asserts  = 3
       Claim    = 'an edit to an all-pending future wave is not drift' }
)

foreach ($m in $methods) {
    $body = Get-MemberRegion -Regions $regions -Member $m.Name
    if ($null -eq $body) {
        $failures += "$rel no longer declares $($m.Name). This stage edits TWO METHODS in place; it renames nothing and deletes nothing. That method carries a shipped product claim - $($m.Claim) - which this plan does not change and is not authorised to drop."
        continue
    }

    # THE DELIVERABLE: run 1 loads, and run 2 loads AGAIN. A floor of 2, measured at 1 today.
    $loads = [regex]::Matches($body, '\bb\s*\.\s*Load\s*\(\s*\)').Count
    if ($loads -lt 2) {
        $failures += "$($m.Name) calls b.Load() $loads time(s); it needs TWO - one per run. Run 2 currently reuses run 1's in-memory PlanDefinition, whose TaskNodes carry pins captured BEFORE the on-disk edit, so the settle-time gate correctly reports a divergence. A real resume is a FRESH PROCESS: PlanLoader re-reads the folder, the pin IS the edited bytes, and the gate is silent. The fixture models something production never does. WavePlanBuilder.Load() is a pure re-read (new PlanLoader().Load(PlanDir)) with no rebuild and no side effects, and b.Load().Plan! is the house idiom - it appears 14 times in this file already."
    }

    # RETENTION: every assertion survives. The claim is not what changed.
    $asserts = [regex]::Matches($body, '\bAssert\s*\.').Count
    if ($asserts -ne $m.Asserts) {
        $failures += "$($m.Name) makes $asserts Assert call(s); it shipped with $($m.Asserts) and this stage changes WHERE RUN 2 GETS ITS PLAN, not what the test claims. It must still assert $($m.Claim). Softening or dropping an assertion makes the test pass under either semantics, which is the cheapest wrong way to close this row."
    }

    # RETENTION: the on-disk edit IS the fixture (section 11).
    if ([regex]::Matches($body, '\bFile\s*\.\s*WriteAllText\s*\(').Count -lt 1) {
        $failures += "$($m.Name) no longer writes to disk between the two runs. That edit IS the fixture - the same rule section 11 states for every timing fixture in this plan: 'a task that stabilizes a flaky timing test by deleting the thing under test has deleted the plan.' Removing it makes the divergence go away by removing the scenario, not by fixing the model."
    }
}

# --- RETENTION: no test deleted, none skipped -------------------------------------------------------
# '\[Fact(\]|\s*\()' - NOT '\[Fact\]': measured elsewhere in this plan, the bare form drops a
# [Fact(Skip = "...")] OUT of the count, so a skipped test trips the count clause as well as the skip
# clause and the operator is told the file lost a test when it did not.
$facts = [regex]::Matches($code, '(?m)^\s*\[Fact(\]|\s*\()').Count
if ($facts -ne 14) {
    $failures += "the file declares $facts [Fact] method(s); it shipped with 14 and this stage edits two of them in place. Deleting a test to make the gate quiet is the cheapest wrong close for this row, and it would take a shipped wave-resume claim with it."
}
if ($code -cmatch '\[Fact\s*\(\s*Skip') {
    $failures += "a [Fact(Skip = ...)] appears in the file. A skipped test is counted by every runner-level instrument and asserts nothing - it is a deleted test that still shows up in the census. Fix the fixture, not the attribute."
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== wave-resume fixture: $($failures.Count) problem(s) in $rel ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    Write-Output ""
    Write-Output "Two methods, one change each: run 2 loads its own plan from disk AFTER the edit, exactly as a real resume does. Every assertion, the File.WriteAllText edit, and all 14 facts stay exactly as they are."
    exit 1
}
Write-Output "Fixture re-baselined: both methods load run 2's plan from disk, all assertions and both on-disk edits intact, 14 facts, none skipped."
exit 0
