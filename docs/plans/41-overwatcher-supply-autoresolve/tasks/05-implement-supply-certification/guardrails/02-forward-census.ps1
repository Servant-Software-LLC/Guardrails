# catches: a pinned behaviour quietly ceasing to RUN, and a DECLARED-EXEMPT row quietly ceasing to
#          exist. Task 04's red census excused one name from being Failed (a correct implementation
#          leaves it green) but NOT from existing; this is the other half of that bargain — every
#          pinned behaviour, exempt or not, observed Passed in the runner's own TRX once the
#          implementation has landed.
#          It matters more than usual here: this task DELETES four members, and the cheapest way to
#          make a refusal row stop failing is to delete the row. A forward census is what makes that
#          route cost the same as not doing the work.
#
#          FORWARD polarity, and its boundary stated: a forward census cannot see a hollow body
#          (a hollow test passes). What it CAN see is a row that was never written, one that no
#          longer runs, and one that is [Skip]ped.
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

$env:DOTNET_CLI_UI_LANGUAGE = 'en'

# Class-scoped, never the bare plan-wide trait (#455).
$filter = 'Category=OverwatchSupply&(FullyQualifiedName~OverwatchSupplyAutoResolveTests|FullyQualifiedName~OverwatchProposalResourceSupplyTests)'

# Task 04's manifest, its declared exemption INCLUDED: every row must now be Passed.
$pinned = @(
    'Certify_RefusesWhenTheDialIsNotCritical',
    'Certify_RefusesWhenThePerGateNeedsHumanThresholdIsBelowCritical',
    'Certify_UsesThePerGateNeedsHumanThreshold_WhenItRaisesToCritical',
    'Certify_RefusesWhenTheReviewGateIsProceedUnreviewed',
    'Certify_RefusesADoomedProposal',
    'Certify_RefusesAProposalWithNoResourceSupplyOp',
    'Certify_RefusesAPathThatIsNotACandidate',
    'Certify_RefusesADuplicatePath',
    'Certify_NormalizesALeadingDotSlashBeforeMatchingACandidate',
    'Certify_CertifiesEveryProposedOpWithItsCandidateSourceSha',
    'GateThresholdEffective_FallsBackToTheRunWideDial_WhenNoPerGateOverride',
    'GateThresholdEffective_PrefersThePerGateOverride',
    'GateThresholdEffective_ReturnsTheDocumentedDefault_WhenTheAutonomyBlockIsAbsent',
    'TryParse_ParsesAResourceSupplyOpWithItsPath',
    'TryParse_KeepsTheValidResourceSupplyOp_AndDropsTheOneWithNoPath',
    # DECLARED-EXEMPT in task 04's RED census (the classifier already returns Default, so a correct
    # test was green on arrival). It is NOT exempt here: design 41 §2.4 says the classifier does not
    # change, and this row is the only thing asserting the new parser case did not open a route by
    # which a resource-supply op could reach the guidance/budget allowlist.
    'Classifier_ClassifiesAResourceSupplyOpAsDefault_NeverAllowlist'
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
            $failures += "[$name] NOT FOUND in the TRX — task 04 pinned this behaviour to a test of that name; it was never executed. Deleting or renaming a row is not a way to make this task green, and the test files are outside this task's writeScope anyway."
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
