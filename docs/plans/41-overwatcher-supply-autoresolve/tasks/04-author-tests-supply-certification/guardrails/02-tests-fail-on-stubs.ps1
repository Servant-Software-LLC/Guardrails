# catches: a hollow test passing off as TDD red. dotnet test exits non-zero if ANY selected test
#          fails, so an Assert.True(true) body hides behind its genuinely-failing siblings and the
#          suite-level exit code certifies nothing about it. This binds every refusal token design 41
#          §3.1 enumerates to a PINNED test method name and requires each to be observed Failed in the
#          runner's own TRX — never stdout, never --list-tests, both of which a hollow body satisfies
#          as well as a real one (#375).
#
#          BOUNDARY: this proves each test is COUPLED TO THE CODE PATH, not that its assertion is
#          correct. An invoking-then-hollow test is red on stubs, green after, and PASSES this.
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

$env:DOTNET_CLI_UI_LANGUAGE = 'en'

# This pair owns TWO test classes: parenthesised alternation, BARE '|'. NEVER '\|' — the backslash is
# VSTest's escape character and that spelling is rejected as "Incorrect format for TestCaseFilter",
# which matches ZERO tests and exits 0: a silent green (#455).
# Substring discrimination checked: 'OverwatchSupplyAutoResolveTests' does not match the integration
# suite's 'OverwatchSupplyAutoResolveWiringTests', and this command runs only the Core project anyway.
$filter = 'Category=OverwatchSupply&(FullyQualifiedName~OverwatchSupplyAutoResolveTests|FullyQualifiedName~OverwatchProposalResourceSupplyTests)'

# THE MANIFEST: each enumerated behaviour -> the test method name the ACTION PROMPT PINNED for it.
$pinned = @(
    # the §3.1 gate, one row per refusal token, in the order the checks run
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
    # the shared effective-threshold rule (§2.1), whose first consumer is the gate above
    'GateThresholdEffective_FallsBackToTheRunWideDial_WhenNoPerGateOverride',
    'GateThresholdEffective_PrefersThePerGateOverride',
    'GateThresholdEffective_ReturnsTheDocumentedDefault_WhenTheAutonomyBlockIsAbsent',
    # the §2.4 parser case
    'TryParse_ParsesAResourceSupplyOpWithItsPath',
    'TryParse_KeepsTheValidResourceSupplyOp_AndDropsTheOneWithNoPath'
)

# DECLARED RED-CENSUS EXEMPTION. Asserted to EXIST below, and task 05's forward census requires it Passed.
#   Classifier_ClassifiesAResourceSupplyOpAsDefault_NeverAllowlist
#     STRUCTURAL REASON: OverwatchFixClassifier.Classify has no case for OverwatchFixKind.ResourceSupply
#     and already falls through to OverwatchAuthorityClass.Default, so a CORRECT test is GREEN on the
#     current tree. That is the property being pinned — design 41 §2.4 says the classifier does NOT
#     change, so adding the parser case must not open a route by which a resource-supply op could be
#     auto-applied. Demanding red here would demand the classifier be broken first.
$mustExist = @('Classifier_ClassifiesAResourceSupplyOpAsDefault_NeverAllowlist')

$results = Join-Path $env:TEMP ("gr41-census-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $results -Force | Out-Null

try {
    & dotnet test "tests/Guardrails.Core.Tests/Guardrails.Core.Tests.csproj" -c Debug --nologo `
        --filter $filter `
        --logger "trx;LogFileName=census.trx" --results-directory $results 2>&1 | Out-String | Write-Output

    $trx = Get-ChildItem -Path $results -Filter '*.trx' -File | Select-Object -First 1
    if (-not $trx) {
        # PRECONDITION, not an unbound-behaviour report — the only legitimate early exit here.
        Write-Output "PRECONDITION: no TRX was produced — the test run did not happen (a build break, or a bad filter). This is NOT a statement about the pinned behaviours."
        exit 1
    }

    [xml]$doc = Get-Content -Raw -LiteralPath $trx.FullName
    $nodes = @($doc.TestRun.Results.UnitTestResult | Where-Object { $_ })

    if ($nodes.Count -lt 1) {
        # @($null).Count is 1, so the Where-Object filter above is what lets this guard fire (#455/#248).
        Write-Output "PRECONDITION: the filter '$filter' matched NO tests. A zero-match filter exits 0 and certifies nothing — check the class names and the Category trait value (it is OverwatchSupply; the rewritten file carried Supply before this task)."
        exit 1
    }

    $failures = @()
    foreach ($name in $pinned) {
        $node = $nodes | Where-Object { $_.testName -like ("*" + $name + "*") } | Select-Object -First 1
        if (-not $node) {
            $failures += "[$name] NOT FOUND in the TRX — the prompt pins this behaviour to a test of that name; it was never executed."
        }
        elseif ($node.outcome -ne 'Failed') {
            $failures += "[$name] outcome was '$($node.outcome)', expected 'Failed'. Certify and Effective throw NotImplementedException and ParseFix has no resource-supply case, so a row that is not red is not reaching its subject."
        }
    }

    # The DECLARED exemption is exempt from the RED requirement, not from EXISTING. A test that is
    # never written is not "green because correct" — it is absent, and absence is how a never-weaker
    # guarantee quietly stops being asserted anywhere.
    foreach ($name in $mustExist) {
        $node = $nodes | Where-Object { $_.testName -like ("*" + $name + "*") } | Select-Object -First 1
        if (-not $node) {
            $failures += "[$name] NOT FOUND in the TRX. It is DECLARED-EXEMPT from the red census (the classifier already returns Default, so a correct test is green), NOT exempt from existing. Write it."
        }
    }

    if ($failures.Count -gt 0) {
        Write-Output ""
        Write-Output "=== Red census FAILED ($($failures.Count) unbound behaviour(s)) ==="
        $failures | ForEach-Object { Write-Output $_ }
        exit 1
    }

    Write-Output "Red census: all $($pinned.Count) pinned behaviour(s) observed Failed; $($mustExist.Count) declared-exempt row(s) present."
    exit 0
}
finally {
    Remove-Item -Recurse -Force -LiteralPath $results -ErrorAction SilentlyContinue
}
