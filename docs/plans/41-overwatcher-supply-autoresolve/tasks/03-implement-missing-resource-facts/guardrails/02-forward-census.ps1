# catches: a pinned behaviour quietly ceasing to RUN. Its sibling red census (task 02) proved each
#          row was red against the stubs; this is the other half of that bargain — every pinned
#          behaviour observed Passed in the runner's own TRX once the implementation has landed.
#          Without it, an implementation that turns one fact green by deleting or renaming the row
#          that asserted it reads identically to one that implemented the fact.
#
#          FORWARD polarity, and its boundary stated: a forward census cannot see a hollow body
#          (a hollow test passes). What it CAN see is a row that was never written, one that no
#          longer runs, and one that is [Skip]ped.
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

$env:DOTNET_CLI_UI_LANGUAGE = 'en'

# Class-scoped, never the bare plan-wide trait (#455).
$filter = 'Category=OverwatchSupply&(FullyQualifiedName~MissingResourceSignalTests|FullyQualifiedName~MissingResourceFactsTests)'

# The SAME manifest as task 02's red census. Task 02 declared NO exemptions, so every row here is
# required Passed — there is no "exists but may be green" tier in this pair.
$pinned = @(
    'PathsIn_ReturnsEveryPathToken_NotOnlyTheFirst',
    'PathsIn_MatchesAScopedNodeModulesPathWhole_BecauseASegmentMayContainAnAtSign',
    'PathsIn_NormalizesALeadingDotSlash_SoARootLevelFileIsNamed',
    'PathsIn_IgnoresProseWithNoSlash_SoNodeJsAndEgNeverMatch',
    'Compute_RefusesEveryPath_WhenTheCheckoutIsNotOnTheRunBranch',
    'Compute_RefusesEveryPath_WhenTheCheckoutHasLeftTheRunsStartingHistory',
    'Compute_RefusesAPathThatEscapesTheWorkspace',
    'Compute_RefusesAProtectedPath',
    'Compute_RefusesAPathUnderThePlanFolder',
    'Compute_FailsClosed_WhenAnotherTaskDeclaresNoWriteScope',
    'Compute_RefusesAPathAnotherTaskMayProduce',
    # ADDED AT REVIEW - the discriminating row for the DECIDED d41-candidate-scope option. See task 02's
    # red census for why every other row is green under the REJECTED option too.
    'Compute_ReturnsACandidate_WhenTheHaltedTasksOwnWriteScopeCoversThePath',
    'Compute_RefusesAPathAlreadyPresentOnTheRunBase',
    'Compute_RefusesAPathWithACaseOnlyTwinOnTheRunBase',
    'Compute_RefusesAPathThisRunDeleted',
    'Compute_RefusesAPathNotCommittedInTheCheckout',
    'Compute_RefusesAPathThatIsNotABlob',
    'Compute_RefusesAPathModifiedInTheCheckoutsWorkingTree',
    'Compute_StopsWithFactsUnavailable_WhenAGitCallErrors_NeverReadingItAsAbsent',
    'Compute_ReturnsACandidateCarryingItsCheckoutSha_ForACommittedUnownedPathMissingFromTheRunBase'
)

$results = Join-Path $env:TEMP ("gr41-census-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $results -Force | Out-Null

try {
    & dotnet test "tests/Guardrails.Core.Tests/Guardrails.Core.Tests.csproj" -c Debug --nologo `
        --filter $filter `
        --logger "trx;LogFileName=census.trx" --results-directory $results 2>&1 | Out-String | Write-Output

    $trx = Get-ChildItem -Path $results -Filter '*.trx' -File | Select-Object -First 1
    if (-not $trx) {
        # PRECONDITION, not an unbound-behaviour report — the only legitimate early exit here.
        Write-Output "PRECONDITION: no TRX was produced — the test run did not happen (a build break, or a bad filter). Fix that first; this is NOT a statement about the pinned behaviours."
        exit 1
    }

    [xml]$doc = Get-Content -Raw -LiteralPath $trx.FullName
    $nodes = @($doc.TestRun.Results.UnitTestResult | Where-Object { $_ })

    if ($nodes.Count -lt 1) {
        # @($null).Count is 1, so the Where-Object filter above is what lets this guard fire (#455/#248).
        Write-Output "PRECONDITION: the filter '$filter' matched NO tests. A zero-match filter exits 0 and certifies nothing."
        exit 1
    }

    $failures = @()
    foreach ($name in $pinned) {
        $node = $nodes | Where-Object { $_.testName -like ("*" + $name + "*") } | Select-Object -First 1
        if (-not $node) {
            $failures += "[$name] NOT FOUND in the TRX — task 02 pinned this behaviour to a test of that name; it was never executed. It must not be renamed or deleted to make this task green."
        }
        elseif ($node.outcome -ne 'Passed') {
            $failures += "[$name] outcome was '$($node.outcome)', expected 'Passed'."
        }
    }

    if ($failures.Count -gt 0) {
        Write-Output ""
        Write-Output "=== Census FAILED ($($failures.Count) unbound behaviour(s)) ==="
        $failures | ForEach-Object { Write-Output $_ }
        exit 1
    }

    Write-Output "Census: all $($pinned.Count) pinned behaviour(s) observed Passed."
    exit 0
}
finally {
    Remove-Item -Recurse -Force -LiteralPath $results -ErrorAction SilentlyContinue
}
