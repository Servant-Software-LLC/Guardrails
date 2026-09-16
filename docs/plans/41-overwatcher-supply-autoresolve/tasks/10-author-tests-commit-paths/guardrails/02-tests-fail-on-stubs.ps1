# catches: a HOLLOW test — named for the behaviour, body a tautology (Assert.True(true), an assertion
#          on a value the test itself constructed, anything that never invokes the subject). It PASSES
#          against the NotImplementedException stub and hides behind its genuinely-failing siblings, so
#          a suite-level non-zero exit certifies the whole file honest (#375). One entry per enumerated
#          behaviour, each observed Failed in the runner's OWN TRX — never merely discovered by name,
#          which a hollow body satisfies just as well.
#
# does NOT catch: a test that can NEVER pass (#530). Red is this gate's success condition, so a test
#          red because no implementation could make it green reads here exactly like one red for the
#          right reason, and the bill arrives on task 11 as a needs-human after its retries are spent.
#
# INVERSE polarity: a NON-zero exit is this check's success, so it does NOT re-emit failure detail
# (#179/§4.2 — re-emitting here would surface the EXPECTED red as if it were a problem), and the
# zero-match guard runs FIRST (§4.3): a test host that never started ALSO exits non-zero, and
# guard-second would certify "TDD red" over a run that executed nothing.
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

$env:DOTNET_CLI_UI_LANGUAGE = 'en'   # the run summary the guard below reads is LOCALIZED (#455)

# The class term is MANDATORY; the plan-wide trait is the optional conjunct (#455). 'SuppliedDrainTests'
# is discriminating — measured: exactly ONE class declaration in tests/** matches it, and no sibling
# class name contains it. Task 11 copies this $filter VERBATIM.
$filter = 'Category=OverwatchSupply&FullyQualifiedName~SuppliedDrainTests'

$pinned = @(
    'CommitPaths_CommitsOnlyItsPathspec_WhenAnUnrelatedFileIsStaged',
    'CommitPaths_CommitsWithTheSuppliedByTrailer',
    'CommitPaths_ReturnsTheCommittedPathsBytesAndCommitSha',
    'CommitPaths_OnAnEmptyPathList_MakesNoCommit',
    'CommitPaths_WhenTheCommitFails_ResetsHardToThePreCommitHead',
    'Drain_CommitsOnlyTheStagedFiles_WhenAnUnrelatedFileIsStaged'
)

# DECLARED RED-CENSUS EXEMPTIONS. Each is asserted to EXIST below, and task 11's forward census
# requires each observed Passed.
#   The five shipped Drain_* tests (design 41 §5: "Drain is refactored ... the operator path gains the
#   same protection", i.e. its OBSERVABLE behaviour must not change).
#     STRUCTURAL REASON: they pin behaviour that must NOT change and they are GREEN on this task's base,
#     so a correct test has nothing to be red about. Demanding Failed would force a test coupled to a
#     stub instead of to the never-weaker property, and task 11 cannot edit tests to undo that.
$mustExist = @(
    'Drain_OnAnEmptyStagingTree_DoesNothingAndMakesNoCommit',
    'Drain_CopiesEveryStagedFileToItsWorkspacePath',
    'Drain_CommitsWithTheSuppliedByTrailer',
    'Drain_DeletesTheStagingTreeAfterCommitting',
    'Drain_ReturnsTheCommittedPathsAndByteCount'
)

$results = Join-Path $env:TEMP ("gr41-t10-census-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $results -Force | Out-Null   # --results-directory is NOT cleared between runs

try {
    # No -v q on a test command (#462): it deletes the failure block, and the two halves of this pair
    # stay copy-pasteable only if neither carries it.
    $out = & dotnet test "tests/Guardrails.Core.Tests/Guardrails.Core.Tests.csproj" -c Debug --nologo `
        --filter $filter `
        --logger "trx;LogFileName=census.trx" --results-directory $results 2>&1 | Out-String
    $testExit = $LASTEXITCODE
    Write-Output $out

    # GUARD FIRST on the inverse polarity (#455). Key on the EXECUTED count (Passed + Failed); "Total:"
    # would also count [Skip]ped tests, so a fully-skipped run would read as red.
    $ran = ([regex]::Matches($out, '(?:Passed|Failed):\s*(\d+)') |
            ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum
    if ($ran -lt 1) {
        Write-Output "ZERO tests executed — the TDD-red proof certified nothing. The --filter '$filter' matched no tests, is malformed, every matched test is [Skip]ped, or the test host failed to start (read the log above). This is NOT a tautology finding: do NOT rewrite the tests."
        exit 1
    }

    $trx = Get-ChildItem -Path $results -Filter '*.trx' -File | Select-Object -First 1
    if (-not $trx) {
        Write-Output "PRECONDITION: no TRX was produced — the test run did not happen (a build break, or a bad filter). This is NOT a statement about the pinned behaviours."
        exit 1
    }

    # Dotted navigation, never SelectNodes('//UnitTestResult'): the TRX carries a default xmlns, and
    # an XPath query without a namespace manager returns NOTHING (a silent zero-result census).
    [xml]$doc = Get-Content -Raw -LiteralPath $trx.FullName
    $nodes = @($doc.TestRun.Results.UnitTestResult | Where-Object { $_ })

    if ($nodes.Count -lt 1) {
        # @($null).Count is 1 — when zero tests ran the TRX has no <Results> element at all, the dotted
        # navigation yields $null, and an unfiltered .Count check evaluates 1 -lt 1 and can NEVER fire.
        # The Where-Object above is the whole reason this guard is alive.
        Write-Output "PRECONDITION: the filter '$filter' matched NO tests in the TRX. A zero-match filter exits 0 and certifies nothing — check the class name and the Category=OverwatchSupply trait."
        exit 1
    }

    $failures = @()
    foreach ($name in $pinned) {
        # Anchored on '.<Name>' followed by '(' or end-of-string: testName is fully qualified and a
        # [Theory] row appends its data (…CommitsWithTheSuppliedByTrailer(by: "operator")). -cmatch, not
        # -match: a differently-cased sibling must not satisfy the entry.
        $matching = @($nodes | Where-Object { $_.testName -cmatch ('\.' + [regex]::Escape($name) + '(\(|$)') })
        if ($matching.Count -lt 1) {
            $failures += "[$name] NOT FOUND in the TRX — the prompt pins this behaviour to a test of that name; it was never executed."
            continue
        }
        # EVERY matching row, not just the first: a [Theory] whose rows disagree (one Failed, one Passed)
        # is a half-bound behaviour, and -First 1 would report whichever row the runner happened to list.
        foreach ($node in $matching) {
            if ($node.outcome -ne 'Failed') {
                # 'NotExecuted' is how a skipped test spells itself — -ne 'Failed' catches it too.
                $failures += "[$($node.testName)] outcome was '$($node.outcome)', expected 'Failed'. A behaviour that passes against the CommitPaths stub is not TDD red — it is hollow, or it never invokes the subject."
            }
        }
    }

    # The DECLARED exemptions are exempt from the RED requirement, not from EXISTING. A test that is
    # never written is not "green because correct" — it is absent, and absence is how a never-weaker
    # guarantee quietly stops being asserted anywhere.
    foreach ($name in $mustExist) {
        $matching = @($nodes | Where-Object { $_.testName -cmatch ('\.' + [regex]::Escape($name) + '(\(|$)') })
        if ($matching.Count -lt 1) {
            $failures += "[$name] NOT FOUND in the TRX. It is DECLARED-EXEMPT from the red census (it pins behaviour that must not change, so a correct test is green), NOT exempt from existing. Keep it — and keep it inside the Category=OverwatchSupply filter."
        }
    }

    if ($failures.Count -gt 0) {
        Write-Output ""
        Write-Output "=== Red census FAILED ($($failures.Count) unbound behaviour(s)) ==="
        $failures | ForEach-Object { Write-Output $_ }
        exit 1
    }

    if ($testExit -eq 0) {
        Write-Output "dotnet test exited 0 — with every pinned behaviour observed Failed this should be impossible; read the log above before trusting either signal."
        exit 1
    }

    Write-Output "Red census: all $($pinned.Count) pinned behaviour(s) observed Failed; all $($mustExist.Count) declared-exempt test(s) present."
    exit 0
}
finally {
    Remove-Item -Recurse -Force -LiteralPath $results -ErrorAction SilentlyContinue
}
