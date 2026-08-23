# catches: a HOLLOW test passing itself off as TDD red by hiding behind a genuinely-failing sibling
#          (#375). `dotnet test` exits non-zero if ANY selected test fails, so an `Assert.True(true)`
#          pinned to one of the twelve enumerated behaviours passes a suite-level exit-code check while
#          proving nothing. This is the PER-TEST CENSUS: every enumerated behaviour is bound to a pinned
#          method name and must be observed `Failed` in the runner's own TRX result file - never stdout
#          (#248), never `--list-tests` name discovery (a hollow body satisfies both).
#
#          The census earns its place most on the four FORWARDING/DECLARATION behaviours. Their cheapest
#          wrong form is a test that asserts the decorator "did not throw" - which is true of the empty
#          interface default, i.e. true of exactly the bug this wave exists to prevent.
#
#          What it does NOT prove: it proves each test is COUPLED to the code path (it fails while the
#          surface is unimplemented), not that its assertion is CORRECT. An invoking-then-hollow test
#          (`var s = AttemptModelSummary(m, r); Assert.NotNull(s);`) is red here, green after task 03,
#          and PASSES this census. Closing that needs mutation testing; until then the wrong-assertion
#          residual is a human read.
#
# INVERSE polarity: non-zero from `dotnet test` is SUCCESS here, so the zero-match guard runs FIRST - a
# crash, or a filter that selected nothing, must never be certified as TDD red (#455).
$ErrorActionPreference = 'Continue'
$env:DOTNET_CLI_UI_LANGUAGE = 'en'

# The twelve behaviours the action prompt enumerates, grouped by the class that owns them. Every one is
# expected RED: nothing raises the event, nothing re-discloses the log after the fold, AttemptModelSummary
# throws, and neither renderer nor decorator declares the member.
# MEASURED BASELINE 2026-08-23 against the merged wave-2 HEAD: zero occurrences of any of these twelve
# names, and zero occurrences of the three class names, anywhere under src/ or tests/. (The strings
# `AttemptModelResolved` DO appear under docs/plans/pilot-seat-model-provenance/ - the SUPERSEDED folder
# wave 4 deletes - which is why every clause in this wave is scoped to a named file or to a TRX result
# set, and never to a tree-wide grep.)
$required = @(
    # AttemptModelDisclosureTests - task 02
    'RouteLog_NamesTheObservedModel_NotTheRequestedOne',
    'RouteLog_CarriesARequestedModelLine_WhenTheObservedDiffersFromTheRoute',
    'RouteLog_CarriesNoRequestedModelLine_WhenTheObservedMatchesTheRoute',
    'AttemptLoop_RaisesAttemptModelResolved_WithBothStrings_OnMismatch',
    'AttemptLoop_RaisesAttemptModelResolved_WithNoRequestedModel_WhenTheRunnerEchoedTheRoute',
    # AttemptModelRenderingTests - task 03
    'Summary_NamesBothModels_WhenTheRequestedModelIsPresent',
    'Summary_OmitsTheRequestedModel_WhenItIsAbsent',
    'ConsoleObserver_WritesTheSharedSummary_ForAttemptModelResolved',
    'LiveObserver_DeclaresAttemptModelResolved_RatherThanInheritingTheEmptyDefault',
    # AttemptModelForwardingTests - task 04
    'LogSiteDecorator_ForwardsAttemptModelResolved_ToItsInnerObserver',
    'DiagramDecorator_ForwardsAttemptModelResolved_ToItsInnerObserver',
    'EveryForwardingObserverInTheCliAssembly_DeclaresAttemptModelResolved'
)

# The three NEW classes by name (dotnet.md 4.3 alternation form). A class filter is safe here precisely
# because all three files are new: there are no pre-existing green assertions inside them whose PASSES
# this census could misread as this task's failure to be red. A plan-wide trait would be the #455 defect.
$filter = 'FullyQualifiedName~AttemptModelDisclosureTests|FullyQualifiedName~AttemptModelRenderingTests|FullyQualifiedName~AttemptModelForwardingTests'

$tmp = Join-Path ([System.IO.Path]::GetTempPath()) ("gr-census-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tmp -Force | Out-Null
try {
    # NO -v q on a TEST command (#179).
    $out = dotnet test tests/Guardrails.Integration.Tests --nologo `
        --filter $filter `
        --logger "trx;LogFileName=census.trx" --results-directory $tmp 2>&1
    $out | ForEach-Object { Write-Output $_ }

    $trx = Get-ChildItem -Path $tmp -Filter '*.trx' -File -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $trx) {
        # PRECONDITION, not a census failure: with no result file every clause below would report
        # "unbound" and blame the tests for a run that never happened.
        Write-Output "no .trx was produced under $tmp - the test RUN did not happen (build failure, host crash, or a malformed --filter). This is not a verdict about the tests; read the log above."
        exit 1
    }

    [xml]$doc = Get-Content -Raw -Path $trx.FullName
    $results = @($doc.TestRun.Results.UnitTestResult)

    # ZERO-MATCH GUARD (#455), FIRST because the polarity is inverse.
    if ($results.Count -lt 1) {
        Write-Output "the class filter selected ZERO tests - one or more of AttemptModelDisclosureTests / AttemptModelRenderingTests / AttemptModelForwardingTests is missing, empty, or named differently. Nothing was measured, so nothing is proven red."
        exit 1
    }

    $failures = @()
    foreach ($name in $required) {
        $matched = @($results | Where-Object { $_.testName -like "*$name*" })
        if ($matched.Count -lt 1) {
            $failures += "'$name' was not executed at all - the prompt pins this method name and the census reads it. Either the test is missing, or it is in a class the filter does not select"
        }
        elseif (@($matched | Where-Object { $_.outcome -eq 'Failed' }).Count -lt 1) {
            $failures += "'$name' ran but did NOT fail (outcome: $(($matched | ForEach-Object { $_.outcome }) -join ', ')) - a TDD red must FAIL while nothing re-discloses attempt-route.log after the fold, nothing raises AttemptModelResolved, AttemptModelSummary throws, and neither renderer nor decorator declares the member. Either the assertion is hollow, or it asserts something already true"
        }
    }

    if ($failures.Count -gt 0) {
        Write-Output ""
        Write-Output "=== per-test red census: $($failures.Count) of $($required.Count) enumerated behaviours are not proven red ==="
        $failures | ForEach-Object { Write-Output "  - $_" }
        Write-Output "Every behaviour the prompt enumerates must be a NAMED test that FAILS against the merged wave-2 HEAD - where the attempt preamble is written BEFORE the observed model is known and no observer has ever heard of it."
        exit 1
    }
    exit 0
}
finally {
    Remove-Item -Path $tmp -Recurse -Force -ErrorAction SilentlyContinue
}
