# catches: a hollow test passing off as TDD red. dotnet test exits non-zero if ANY selected test
#          fails, so an Assert.True(true) body hides behind its genuinely-failing siblings and the
#          suite-level exit code certifies nothing about it. This binds every behaviour the action
#          prompt enumerated to a PINNED test method name and requires each to be observed Failed in
#          the runner's own TRX — never stdout, never --list-tests, both of which a hollow body
#          satisfies as well as a real one (#375).
#
#          BOUNDARY: this proves each test is COUPLED TO THE CODE PATH, not that its assertion is
#          correct. An invoking-then-hollow test is red on stubs, green after, and PASSES this. The
#          reason TOKENS each row must assert are checked separately, by 03-tokens-and-fixture.ps1.
#
#          NO DECLARED EXEMPTIONS in this task: every pinned row invokes MissingResourceSignal.PathsIn
#          or MissingResourceFacts.Compute, and both stubs throw NotImplementedException, so a correct
#          test is red on arrival. A row that is green here is not reaching its subject.
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

$env:DOTNET_CLI_UI_LANGUAGE = 'en'

# This pair owns TWO test classes, so the class terms are a parenthesised alternation with a BARE '|'.
# NEVER '\|': the backslash is VSTest's escape character and the markdown-table spelling is rejected as
# "Incorrect format for TestCaseFilter" — which matches ZERO tests and exits 0, a silent green (#455).
$filter = 'Category=OverwatchSupply&(FullyQualifiedName~MissingResourceSignalTests|FullyQualifiedName~MissingResourceFactsTests)'

# THE MANIFEST: each enumerated behaviour -> the test method name the ACTION PROMPT PINNED for it.
$pinned = @(
    # MissingResourceSignal — the three §2.1 widenings, plus the property that must NOT change
    'PathsIn_ReturnsEveryPathToken_NotOnlyTheFirst',
    'PathsIn_MatchesAScopedNodeModulesPathWhole_BecauseASegmentMayContainAnAtSign',
    'PathsIn_NormalizesALeadingDotSlash_SoARootLevelFileIsNamed',
    'PathsIn_IgnoresProseWithNoSlash_SoNodeJsAndEgNeverMatch',
    # MissingResourceFacts — the two run-level lineage facts
    'Compute_RefusesEveryPath_WhenTheCheckoutIsNotOnTheRunBranch',
    'Compute_RefusesEveryPath_WhenTheCheckoutHasLeftTheRunsStartingHistory',
    # MissingResourceFacts — the ten per-path facts (check 4 carries TWO tokens, so it has two rows)
    'Compute_RefusesAPathThatEscapesTheWorkspace',
    'Compute_RefusesAProtectedPath',
    'Compute_RefusesAPathUnderThePlanFolder',
    'Compute_FailsClosed_WhenAnotherTaskDeclaresNoWriteScope',
    'Compute_RefusesAPathAnotherTaskMayProduce',
    # ADDED AT REVIEW - the ONLY row that separates the DECIDED d41-candidate-scope option from the
    # rejected one. The review chose "every OTHER task declares a writeScope and none covers it" over
    # "NO task in the plan, INCLUDING the halted one". Both options pass every other row here, and both
    # pass the wiring proof too, because that fixture's halted task declares writeScope ["02-done.txt"]
    # and so does not own vendor/resource.js. Without this row an implementation written
    # `plan.Tasks.Any(...)` instead of `plan.Tasks.Where(t => t.Id != haltedTask.Id).Any(...)` ships
    # green - and it refuses exactly the vendoring-task case the decision exists to serve.
    'Compute_ReturnsACandidate_WhenTheHaltedTasksOwnWriteScopeCoversThePath',
    'Compute_RefusesAPathAlreadyPresentOnTheRunBase',
    'Compute_RefusesAPathWithACaseOnlyTwinOnTheRunBase',
    'Compute_RefusesAPathThisRunDeleted',
    'Compute_RefusesAPathNotCommittedInTheCheckout',
    'Compute_RefusesAPathThatIsNotABlob',
    'Compute_RefusesAPathModifiedInTheCheckoutsWorkingTree',
    # The tri-state stop, and the positive
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
        # PRECONDITION, not an unbound-behaviour report: "the run did not happen" is a different fact
        # from "the tests did not behave as required". This is the ONE legitimate early exit here.
        Write-Output "PRECONDITION: no TRX was produced — the test run did not happen (a build break, or a bad filter). This is NOT a statement about the pinned behaviours."
        exit 1
    }

    [xml]$doc = Get-Content -Raw -LiteralPath $trx.FullName
    $nodes = @($doc.TestRun.Results.UnitTestResult | Where-Object { $_ })

    if ($nodes.Count -lt 1) {
        # The zero-match hole (#455/#248): with nothing executed the TRX carries no <Results> element,
        # so the dotted navigation yields $null and @($null).Count is 1 — an unfiltered .Count check
        # would evaluate 1 -lt 1 and never fire. The Where-Object above is what lets this guard work.
        Write-Output "PRECONDITION: the filter '$filter' matched NO tests. A zero-match filter exits 0 and certifies nothing — check the class names and the Category trait value (it is OverwatchSupply, NOT Supply)."
        exit 1
    }

    $failures = @()
    foreach ($name in $pinned) {
        $node = $nodes | Where-Object { $_.testName -like ("*" + $name + "*") } | Select-Object -First 1
        if (-not $node) {
            $failures += "[$name] NOT FOUND in the TRX — the prompt pins this behaviour to a test of that name; it was never executed."
        }
        elseif ($node.outcome -ne 'Failed') {
            $failures += "[$name] outcome was '$($node.outcome)', expected 'Failed'. Both stubs throw NotImplementedException, so a row that is not red is not reaching its subject — it is hollow, or it wraps the call in Assert.Throws."
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
