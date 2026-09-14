# catches: a hollow test passing off as TDD red. dotnet test exits non-zero if ANY selected test
#          fails, so an Assert.True(true) body hides behind its genuinely-failing siblings and the
#          suite-level exit code certifies nothing about it. This binds every enumerated behaviour to a
#          PINNED test method name and requires each to be observed Failed in the runner's own TRX -
#          never stdout, never --list-tests, both of which a hollow body satisfies as well as a real one
#          (#375).
#
#          BOUNDARY: this proves each test is COUPLED TO THE CODE PATH, not that its assertion is
#          correct. An invoking-then-hollow test is red on stubs, green after, and PASSES this.
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

$env:DOTNET_CLI_UI_LANGUAGE = 'en'

$pinned = @(
    'ATrialMergeCommit_RunsTheUsersHooks_SoAHookCanRejectIt',
    'ATrialMergeCommit_RunsHooksFromARelativeUntrackedHooksPath',
    'TheQuietCase_CreatesNoMergeCommit_AndRunsNoHook',
    'UserTipWasAncestor_IsCorrectBothWays',
    'ATrialRebuiltAfterAQuietPromotionLanded_IsAlreadyDelivered',
    'ATrialRebuiltAfterItsPromotionLanded_IsAlreadyDelivered',
    'TheTrialMergeCommit_HasTheUsersTipAsFirstParent',
    'TheMovedCase_KeepsAWorktreeAtTheTrialCommit_UntilDiscarded',
    'ATrialThatConflicts_IsRefusedWithTheConflictingPaths',
    'CreatingATrial_NeverTouchesTheUsersCheckout',
    'Promotion_RefusesWhenTheCheckoutMovedToAnotherBranch',
    'Promotion_RefusesDirtTheFastForwardWouldOverwrite',
    'Promotion_RefusesWhenTheUsersBranchAdvancedAfterTheTrial',
    'Promotion_RefusesWhenTheUsersBranchWasRewoundAfterTheTrial',
    'Promotion_FastForwardsTheUsersBranchToTheTrialCommit',
    'Discard_RemovesTheTrialRef'
)

# NO DECLARED RED-CENSUS EXEMPTIONS. Every pinned behaviour calls CreateTrialDelivery,
#   PromoteTrialDelivery or DiscardTrialDelivery, and on this task's base each of those is a default
#   interface member that throws NotImplementedException, so a correct test is red on arrival for every
#   row. The existence loop below is kept so a later exemption has a place to go.
$mustExist = @()

$results = Join-Path $env:TEMP ("gr39-census-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $results -Force | Out-Null

try {
    & dotnet test "tests/Guardrails.Integration.Tests/Guardrails.Integration.Tests.csproj" -c Debug --nologo `
        --filter "FullyQualifiedName~TrialDeliveryPrimitiveTests" `
        --logger "trx;LogFileName=census.trx" --results-directory $results 2>&1 | Out-String | Write-Output

    $trx = Get-ChildItem -Path $results -Filter '*.trx' -File | Select-Object -First 1
    if (-not $trx) {
        Write-Output "PRECONDITION: no TRX was produced - the test run did not happen (a build break, or a bad filter). This is NOT a statement about the pinned behaviours."
        exit 1
    }

    [xml]$doc = Get-Content -Raw -LiteralPath $trx.FullName
    $nodes = @($doc.TestRun.Results.UnitTestResult | Where-Object { $_ })

    if ($nodes.Count -lt 1) {
        # @($null).Count is 1, so the Where-Object filter above is what lets this guard fire at all.
        Write-Output "PRECONDITION: the filter 'FullyQualifiedName~TrialDeliveryPrimitiveTests' matched NO tests. A zero-match filter exits 0 and certifies nothing."
        exit 1
    }

    $failures = @()
    foreach ($name in $pinned) {
        $node = $nodes | Where-Object { $_.testName -like ("*" + $name + "*") } | Select-Object -First 1
        if (-not $node) {
            $failures += "[$name] NOT FOUND in the TRX - the prompt pins this behaviour to a test of that name; it was never executed."
        }
        elseif ($node.outcome -ne 'Failed') {
            $failures += "[$name] outcome was '$($node.outcome)', expected 'Failed'. A behaviour that passes against the stubs is not TDD red - it is hollow or already implemented."
        }
    }

    # The DECLARED exemptions are exempt from the RED requirement, not from EXISTING. A test that
    # is never written is not "green because correct" - it is absent, and absence is how a
    # never-weaker guarantee quietly stops being asserted anywhere.
    foreach ($name in $mustExist) {
        $node = $nodes | Where-Object { $_.testName -like ("*" + $name + "*") } | Select-Object -First 1
        if (-not $node) {
            $failures += "[$name] NOT FOUND in the TRX. It is DECLARED-EXEMPT from the red census (a correct implementation leaves it green), NOT exempt from existing. Write it."
        }
    }

    if ($failures.Count -gt 0) {
        Write-Output ""
        Write-Output "=== Red census FAILED ($($failures.Count) unbound behaviour(s)) ==="
        $failures | ForEach-Object { Write-Output $_ }
        exit 1
    }

    Write-Output "Red census: all $($pinned.Count) pinned behaviour(s) observed Failed."
    exit 0
}
finally {
    Remove-Item -Recurse -Force -LiteralPath $results -ErrorAction SilentlyContinue
}
