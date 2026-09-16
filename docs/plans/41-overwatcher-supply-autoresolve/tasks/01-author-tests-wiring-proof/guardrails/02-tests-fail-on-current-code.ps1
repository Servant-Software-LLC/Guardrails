# catches: a hollow test passing off as TDD red. dotnet test exits non-zero if ANY selected test
#          fails, so an Assert.True(true) body hides behind its genuinely-failing siblings and the
#          suite-level exit code certifies nothing about it. This binds every enumerated behaviour to a
#          PINNED test method name and requires each to be observed Failed in the runner's own TRX —
#          never stdout, never --list-tests, both of which a hollow body satisfies as well as a real one
#          (#375).
#
#          BOUNDARY: this proves each test is COUPLED TO THE CODE PATH, not that its assertion is
#          correct. An invoking-then-hollow test is red on current code, green after, and PASSES this.
#          It also cannot see a test that can NEVER pass (#530): red is this gate's success condition,
#          so an unpassable test reads here exactly like one red for the right reason, and the bill
#          arrives on task 14. What covers that is design §7's own two-sided construction (P1 asserts
#          the brief WAS sent; C1/C7/N1 assert it was not), not this census.
#
#          No required-present regex clause in this guardrail, so there is no #478 baseline to census.
#          Subject: tests/Guardrails.Integration.Tests/Supply/OverwatchSupplyAutoResolveWiringTests.cs
#          — n/a, file created by this task.
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

$env:DOTNET_CLI_UI_LANGUAGE = 'en'

# The SAME string task 14's forward census uses — copied verbatim, so the two halves of the TDD pair
# can never drift apart. It names this pair's OWN test class, never the plan-wide trait alone (#455).
$filter = 'Category=OverwatchSupply&FullyQualifiedName~OverwatchSupplyAutoResolveWiringTests'

# The seven tests design §7 requires to FAIL against master.
$pinned = @(
    'AtCriticalDial_SuppliesTheMissingResource_AndReArmsTheTask',
    'WithMergeOnSuccess_AnAutoSuppliedRun_IsNotDelivered',
    'WhenAnotherTaskOwnsThePath_NoBriefIsSent_AndTheStopIsObserved',
    'WhenTheProposalCarriesNoFix_CertificationRefuses_NoResourceSupplyOp',
    'WhenTheFileIsUncommittedInTheCheckout_NoBriefIsSent_NotCommittedInCheckout',
    'WhenTheProposalNamesANonCandidate_CertificationRefuses_NotACandidate',
    'WhenTheProposalIsDoomed_CertificationRefuses_Doomed'
)

# DECLARED RED-CENSUS EXEMPTIONS (design §7: "C1, C7 and N1 pass on master by construction").
# Each is asserted to EXIST and to have EXECUTED below, and task 14's forward census requires each
# observed Passed.
#   BelowCritical_TheOverwatcherIsNotConsulted_AndNothingIsSupplied (C1)
#     STRUCTURAL REASON: at escalationThreshold "high" the dial is not engaged, so design §2.1 tier 0
#     writes NO record at all. On master nothing is consulted anywhere, so "no brief, no auto-supplied,
#     no observed, no advisory" is already true. Demanding Failed would demand a CORRECT test fail.
#   WithAPerGateNeedsHumanFloor_TheDialIsNotEngaged_AndNoBriefIsSent (C7)
#     STRUCTURAL REASON: the same tier-0 argument, reached through gateThresholds.needs-human: "high"
#     under a run-wide critical. The effective-threshold rule it pins does not exist on master, so the
#     assertion ("nothing happened") is satisfied for the trivial reason.
#   WhenTheResourceIsAlreadyOnTheRunBase_NothingIsSupplied_AndTheRunIsGreen (N1)
#     STRUCTURAL REASON: it IS the never-weaker property — a run with no missing-resource halt must be
#     byte-identical before and after this feature, so a correct test has nothing to be red about.
# Never DROP such a row instead: an undeclared omission and an oversight look identical, and a
# never-weaker guarantee that stops being asserted anywhere is how this feature would quietly widen.
$mustExist = @(
    'BelowCritical_TheOverwatcherIsNotConsulted_AndNothingIsSupplied',
    'WithAPerGateNeedsHumanFloor_TheDialIsNotEngaged_AndNoBriefIsSent',
    'WhenTheResourceIsAlreadyOnTheRunBase_NothingIsSupplied_AndTheRunIsGreen'
)

# --results-directory is NOT cleared between runs, so a stale TRX from the PREVIOUS attempt would be
# read as this attempt's evidence. Delete it BEFORE the run, never only after: a cleanup that runs on
# the way out is skipped by every path that does not reach it, which is exactly the failing attempt
# whose TRX must not survive.
$results = Join-Path ([System.IO.Path]::GetTempPath()) "gr41-census-$PID"
Remove-Item $results -Recurse -Force -ErrorAction SilentlyContinue

# No -v q: it is pointless here (nothing is re-emitted) and propagates onto forward checks by
# cloning (#462).
& dotnet test "tests/Guardrails.Integration.Tests/Guardrails.Integration.Tests.csproj" -c Debug --nologo `
    --filter $filter `
    --logger "trx;LogFileName=census.trx" --results-directory $results 2>&1 | Out-String | Write-Output

# PRECONDITION — the ONE legitimate early exit. No TRX means the run never happened (a build break, a
# wrong project path, or a malformed --filter, which exits 0 SILENTLY). Diagnose THAT: falling through
# would print "every behaviour unbound", a confident wrong message aimed at the one artifact the retry
# agent is allowed to edit.
$trx = Get-ChildItem -Path $results -Filter '*.trx' -File -Recurse -ErrorAction SilentlyContinue |
       Sort-Object LastWriteTime | Select-Object -Last 1
if (-not $trx) {
    Write-Output "PRECONDITION: no TRX was produced — the test run did not happen (a build break, a wrong project path, or a bad filter). This is NOT a statement about the pinned behaviours: do NOT rewrite the tests."
    exit 1
}

# DOTTED navigation — the TRX has a default xmlns, so SelectNodes('//UnitTestResult') finds nothing.
[xml]$doc = Get-Content -Raw -LiteralPath $trx.FullName
$recorded = @($doc.TestRun.Results.UnitTestResult | Where-Object { $_ })

if ($recorded.Count -lt 1) {
    # The Where-Object is NOT decoration: with zero tests executed the TRX has NO <Results> element,
    # the navigation yields $null, and @($null).Count is 1 — so the bare @(...) form would evaluate
    # 1 -lt 1 and NEVER FIRE. Measured on PowerShell 7: @($null).Count -> 1,
    # @($null | Where-Object { $_ }).Count -> 0.
    Write-Output "PRECONDITION: the filter '$filter' matched NO tests, or every match is [Skip]ped out of execution. A zero-match filter exits 0 and certifies nothing. This is NOT a finding about the tests: do NOT rewrite them."
    exit 1
}

# ACCUMULATE (#179): one distinguishable message per unbound behaviour, so ONE attempt learns every
# gap instead of discovering them one retry at a time.
$failures = @()

foreach ($name in $pinned) {
    # -cmatch: C# method names are case-SENSITIVE and PowerShell -match is not (taxonomy 3).
    # The (\(|$) tail admits a [Theory] row's appended data without admitting a longer sibling name.
    $pattern = '\.' + [regex]::Escape($name) + '(\(|$)'
    $hits = @($recorded | Where-Object { $_.testName -cmatch $pattern })
    if ($hits.Count -lt 1) {
        $failures += "[$name] NOT FOUND in the TRX — the prompt pins this behaviour to a test of that name; it was never executed. Check the spelling against the prompt's pinned list."
        continue
    }
    $notRed = @($hits | Where-Object { $_.outcome -ne 'Failed' })
    if ($notRed.Count -gt 0) {
        $seen = (($notRed | ForEach-Object { $_.outcome } | Sort-Object -Unique) -join '/')
        $failures += "[$name] outcome was '$seen', expected 'Failed'. A behaviour that passes against the CURRENT code is not TDD red — it is hollow, or it asserts something already true on master. Drive the real run and assert the wired-only artifact. ('NotExecuted' = [Fact(Skip=...)].)"
    }
}

# The DECLARED exemptions are exempt from the RED requirement, not from EXISTING and not from RUNNING.
# A test that is never written is not "green because correct" — it is absent, and absence is how a
# never-weaker guarantee quietly stops being asserted anywhere.
foreach ($name in $mustExist) {
    $pattern = '\.' + [regex]::Escape($name) + '(\(|$)'
    $hits = @($recorded | Where-Object { $_.testName -cmatch $pattern })
    if ($hits.Count -lt 1) {
        $failures += "[$name] NOT FOUND in the TRX. It is DECLARED-EXEMPT from the red census (see this file's header for why a correct test is green on master), NOT exempt from existing. Write it."
        continue
    }
    # REVIEW FIX: a declared exemption is green BY CONSTRUCTION, so it must be REQUIRED green. The first
    # spelling rejected only NotExecuted/empty, so an exempt row that came back FAILED passed this census -
    # proven by running this script over a synthesized TRX. A fixture broken outright could then leave
    # every row red and still satisfy the gate, with the bill landing one task later as a misattributed
    # needs-human rather than here as a clear red.
    $notRun = @($hits | Where-Object { $_.outcome -ne 'Passed' })
    if ($notRun.Count -gt 0) {
        $failures += "[$name] is a DECLARED EXEMPTION and its outcome was not 'Passed'. An exemption is green BY CONSTRUCTION, so it is REQUIRED green here: 'NotExecuted' means [Fact(Skip=...)], and 'Failed' means the fixture itself is broken. An exempt row still has to run AND pass; skipping it turns the exemption into no coverage at all, and task 14's forward census requires all ten executed."
    }
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== Red census FAILED ($($failures.Count) unbound behaviour(s) of $($pinned.Count + $mustExist.Count)) ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    exit 1
}

Write-Output "Red census: all $($pinned.Count) pinned behaviour(s) observed Failed, and all $($mustExist.Count) declared-exempt row(s) executed."
exit 0
