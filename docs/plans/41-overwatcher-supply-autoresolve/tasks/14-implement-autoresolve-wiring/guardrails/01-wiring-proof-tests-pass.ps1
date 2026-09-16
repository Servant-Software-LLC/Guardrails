# catches: the #712 shape itself — a missing-resource auto-resolve that is built, unit-tested and
#          completely unreachable from a real run. Every ancestor in this plan is certified in
#          isolation behind an injected seam; this is the ONLY check that drives the production
#          composition root (CommandFactory.BuildRootCommand -> SchedulerFactory.Create ->
#          Scheduler.OnSettledAsync) and asserts an artifact only the WIRED path can produce.
#          A task gated solely by the pure Certify/facts/parser tests is the #382 shape, and the
#          plan 40 run that produced #712 took exactly that route (design §7).
#
# archetype: §10a composition-root wiring. The tests drive the real factory; they never construct a
#          Scheduler or an Overwatch and never call Certify. The test file is NOT in this task's
#          writeScope, so the agent cannot make this pass by editing the proof.
#
# WHY THE TRX AND NOT JUST THE EXIT CODE: design §7's census requires ALL TEN tests (P1, P2, C1-C7,
#          N1) to EXECUTE on all three OS runners, and fails if the results report ANY skipped test —
#          "a control that never ran is evidence silently lost". dotnet test exits 0 over a run that
#          skipped every control, so the exit code alone certifies nothing about that.
#
# No required-present regex clause in this guardrail, so there is no #478 baseline to census.
# Early exits, all preconditions for the census below and each named at its site: a non-zero test
# exit (re-emitted first, #179), a zero-executed filter, and an absent TRX.
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

$env:DOTNET_CLI_UI_LANGUAGE = 'en'   # the run summary the zero-match guard reads is LOCALIZED (#455)

# COPIED VERBATIM from task 01's red census (tasks/01-author-tests-wiring-proof/guardrails/
# 02-tests-fail-on-current-code.ps1) — the two halves of the TDD pair must run the SAME selection or
# they prove nothing about each other.
$filter = 'Category=OverwatchSupply&FullyQualifiedName~OverwatchSupplyAutoResolveWiringTests'

# All ten of design §7's tests, in its own order: P1, P2, C1-C7, N1.
$expected = @(
    'AtCriticalDial_SuppliesTheMissingResource_AndReArmsTheTask',                  # P1 positive path
    'WithMergeOnSuccess_AnAutoSuppliedRun_IsNotDelivered',                         # P2 delivery held
    'BelowCritical_TheOverwatcherIsNotConsulted_AndNothingIsSupplied',             # C1
    'WhenAnotherTaskOwnsThePath_NoBriefIsSent_AndTheStopIsObserved',               # C2
    'WhenTheProposalCarriesNoFix_CertificationRefuses_NoResourceSupplyOp',         # C3
    'WhenTheFileIsUncommittedInTheCheckout_NoBriefIsSent_NotCommittedInCheckout',  # C4
    'WhenTheProposalNamesANonCandidate_CertificationRefuses_NotACandidate',        # C5
    'WhenTheProposalIsDoomed_CertificationRefuses_Doomed',                         # C6
    'WithAPerGateNeedsHumanFloor_TheDialIsNotEngaged_AndNoBriefIsSent',            # C7
    'WhenTheResourceIsAlreadyOnTheRunBase_NothingIsSupplied_AndTheRunIsGreen'      # N1 never-weaker
)

# --results-directory is NOT cleared between runs, so a stale TRX from the PREVIOUS attempt would be
# read as this attempt's evidence. Delete it BEFORE the run, never only after: a cleanup that runs on
# the way out is skipped by every path that does not reach it, which is exactly the failing attempt
# whose TRX must not survive.
$results = Join-Path ([System.IO.Path]::GetTempPath()) "gr41-wiring-$PID"
Remove-Item $results -Recurse -Force -ErrorAction SilentlyContinue

# NO -v q on the TEST command: it suppresses the Error Message/Expected/Actual/Stack Trace block,
# leaving only "[FAIL] <name>" for the re-emit below to find — which defeats #179 by the flag alone.
$out = & dotnet test "tests/Guardrails.Integration.Tests/Guardrails.Integration.Tests.csproj" -c Debug --nologo `
    --filter $filter `
    --logger "trx;LogFileName=wiring.trx" --results-directory $results 2>&1 | Out-String
$testExit = $LASTEXITCODE          # capture BEFORE any other statement

Write-Output $out                  # full log first, for the attempt's saved output

# EXIT CODE FIRST, guard second (#455): a test host that never ran exits NON-zero with no summary,
# so checking the exit code first reports its real error instead of blaming the filter.
if ($testExit -ne 0) {
    Write-Output ""
    Write-Output "=== Wiring-proof FAILURE detail (re-emitted for the retry tail, #179) ==="
    # #608: a BLOCK capture, never a line allowlist. An allowlist drops the `Failed <TestName>`
    # header (so the detail names no test) and the exception payload, which is most of the diagnosis.
    $detail = @()
    $inBlock = $false
    foreach ($line in ($out -split "`r?`n")) {
        if ($line -match '^\s*Failed\s+\S') { $inBlock = $true }
        elseif ($inBlock -and $line -match '^\s*(Passed!|Failed!|Skipped!|Passed\s+\S+\s+\[|Test Run)') { $inBlock = $false }
        if ($inBlock) { $detail += $line }
    }
    $detail = $detail | Select-Object -First 40    # bound so it fits the ~60-line harness tail
    if ($detail) { $detail | ForEach-Object { Write-Output $_ } }
    else { Write-Output "(no per-test failure block in the runner output — the cause is ABOVE and is most likely a BUILD error or a crashed test host, not an assertion)" }
    Write-Output ""
    Write-Output "OverwatchSupplyAutoResolveWiringTests failing — the auto-resolve is not on the real path. Do NOT edit the test file (it is outside this task's writeScope); fix Scheduler.cs / SchedulerFactory.cs."
    exit 1
}

# ZERO-MATCH GUARD (#455): exit 0 alone does NOT mean tests passed — a --filter that matches nothing,
# or is malformed, also exits 0. Key on the EXECUTED count (Passed+Failed); "Total:" would also count
# [Skip]ped tests, so a fully-skipped run would pass.
$ran = ([regex]::Matches($out, '(?:Passed|Failed):\s*(\d+)') |
        ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum
if ($ran -lt 1) {
    Write-Output "exit 0 but ZERO tests executed — this guardrail certified nothing. The --filter '$filter' matched no tests, is malformed, or every matched test is [Skip]ped."
    exit 1
}

# PRECONDITION: the per-test census below needs the runner's own results. Read the TRX, never stdout —
# [FAIL]/skip lines there are verbosity- and culture-dependent (#248/#455).
$trx = Get-ChildItem -Path $results -Filter '*.trx' -File -Recurse -ErrorAction SilentlyContinue |
       Sort-Object LastWriteTime | Select-Object -Last 1
if (-not $trx) {
    Write-Output "PRECONDITION: no TRX under $results — the suite exited 0 but produced no results to census. This is NOT a finding about the wiring."
    exit 1
}

# DOTTED navigation — the TRX has a default xmlns, so SelectNodes('//UnitTestResult') finds nothing.
# The Where-Object is what lets the guard below fire: with zero tests executed the TRX has NO
# <Results> element, the navigation yields $null, and @($null).Count is 1.
[xml]$doc = Get-Content -Raw -LiteralPath $trx.FullName
$recorded = @($doc.TestRun.Results.UnitTestResult | Where-Object { $_ })
if ($recorded.Count -lt 1) {
    Write-Output "PRECONDITION: the TRX records ZERO executed tests. This is NOT a finding about the wiring."
    exit 1
}

# ACCUMULATE (#179): one distinguishable message per gap, dumped once, so ONE attempt learns all.
$failures = @()

foreach ($name in $expected) {
    # -cmatch: C# method names are case-SENSITIVE and PowerShell -match is not (taxonomy 3).
    # The (\(|$) tail admits a [Theory] row's appended data without admitting a longer sibling.
    $pattern = '\.' + [regex]::Escape($name) + '(\(|$)'
    $hits = @($recorded | Where-Object { $_.testName -cmatch $pattern })
    if ($hits.Count -lt 1) {
        $failures += "[$name] did NOT EXECUTE — absent from the TRX. Design §7 requires all ten tests (P1, P2, C1-C7, N1) to run. A control that never ran is evidence silently lost. This test file is outside your writeScope: if it is genuinely absent or renamed, that is a delivery problem from task 01, not something to fix by editing tests — escalate as needsHuman."
        continue
    }
    $notGreen = @($hits | Where-Object { $_.outcome -ne 'Passed' })
    if ($notGreen.Count -gt 0) {
        $seen = (($notGreen | ForEach-Object { $_.outcome } | Sort-Object -Unique) -join '/')
        $failures += "[$name] outcome was '$seen', expected 'Passed'. ('NotExecuted' = [Fact(Skip=...)].)"
    }
}

# ANY skipped test in the selection fails, not only a skipped member of the ten (design §7).
$skipped = @($recorded | Where-Object { $_.outcome -eq 'NotExecuted' -or [string]::IsNullOrEmpty($_.outcome) })
if ($skipped.Count -gt 0) {
    $names = (($skipped | ForEach-Object { $_.testName } | Sort-Object -Unique) -join ', ')
    $failures += "$($skipped.Count) test(s) in this selection were SKIPPED: $names. Design §7's census fails on any skipped test — a control that never ran reads as success while proving nothing, and a run that skips a control is how this feature would silently widen."
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== Real-path wiring census FAILED ($($failures.Count) finding(s); $($expected.Count) tests required, $($recorded.Count) recorded) ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    exit 1
}

Write-Output "Real-path wiring proof: all $($expected.Count) tests executed and passed, none skipped."
exit 0
