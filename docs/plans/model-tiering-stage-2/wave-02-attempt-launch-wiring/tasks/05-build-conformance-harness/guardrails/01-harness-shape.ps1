# catches: a "harness" that compiles but cannot host the conformance suite - the two failure shapes
#          being (a) it never drives the REAL Scheduler/TaskExecutor (so every clause task 06 writes
#          on top of it would prove a fake, the #382 passing-but-blind trap), and (b) it exposes no
#          way to reach an attempt's LOG DIR, which task 09's clauses must read to assert the route
#          disclosure and the D28 warning. Both are cheap to check structurally and expensive to
#          discover from inside task 06.
#          Scoped to the ONE file this task owns.
$ErrorActionPreference = 'Continue'
$file = 'tests/Guardrails.Integration.Tests/ModelTiering/Stage2PlanHarness.cs'
$failures = @()

if (-not (Test-Path $file)) {
    Write-Output "$file does not exist - the shared conformance harness this task owes was never written. Task 06 and wave 3 both build on it."
    exit 1
}
$content = Get-Content -Raw $file

if ($content -notmatch '(?m)class\s+Stage2PlanHarness\b') {
    $failures += 'no `class Stage2PlanHarness` declaration - task 06''s prompt and its guardrails reference that exact type name'
}

# (a) The REAL in-process seam is driven. Faking the PROCESS boundary (IPromptRunner) is sanctioned;
# faking the seam under test is not.
foreach ($seam in @(
    @{ Pattern = 'new\s+PlanLoader\s*\(';  What = 'the real PlanLoader - the tier/TierOrigin collapse under test happens AT LOAD' },
    @{ Pattern = 'new\s+TaskExecutor\s*\('; What = 'the real TaskExecutor - the attempt-launch path under test' },
    @{ Pattern = 'new\s+Scheduler\s*\(';    What = 'the real Scheduler - so attempts and RE-attempts are driven the way a run drives them' },
    @{ Pattern = 'IPromptRunner';           What = 'a fake IPromptRunner - the process/CLI boundary, the ONE thing a real-seam test may fake' },
    @{ Pattern = 'JournalReader|JournalDocument'; What = 'a read of the persisted journal - the surface the route must actually reach' })) {
    if ($content -notmatch $seam.Pattern) {
        $failures += "the harness never references $($seam.What) (pattern: $($seam.Pattern))"
    }
}

# (b) The attempt LOG DIR is reachable. Task 09's clauses assert on the route-disclosure file the
# executor writes into the attempt log dir; a harness with no accessor forces task 06 to reconstruct
# the layout by hand, which then breaks the first time the harness changes it.
if ($content -notmatch '(?i)LogDir') {
    $failures += 'the harness exposes no way to reach an attempt LOG DIR (no reference to LogDir) - task 09''s route-disclosure and D28-warning clauses read a file from there, and AttemptRecord.LogDir is the plan-relative path to use rather than a hand-rebuilt layout'
}

# NEGATIVE ASSERTION (#176): the prompt FORBIDS the harness touching the resolver. A harness that
# called TierResolver would let every clause built on it prove the resolver against itself instead of
# proving the WIRING - the exact "green light over a broken wire" #382 describes.
if ($content -match 'TierResolver|TierResolution') {
    $failures += 'the harness references TierResolver/TierResolution - forbidden. It must observe the route through the JOURNAL and the captured invocation, never by consulting the resolver: a harness that calls the resolver makes every conformance clause a test of the resolver rather than of the wiring'
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== Stage2PlanHarness shape: $($failures.Count) finding(s) ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    exit 1
}
exit 0
