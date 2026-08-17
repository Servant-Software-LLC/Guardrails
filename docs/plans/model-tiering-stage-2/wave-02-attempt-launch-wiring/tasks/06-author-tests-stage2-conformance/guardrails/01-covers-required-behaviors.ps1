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
