# catches: a pinned or DECLARED-EXEMPT census row quietly ceasing to exist. Task 10's red census
#          excuses the five shipped Drain_* tests from being Failed (a correct base leaves them green)
#          but NOT from existing; this is the other half of that bargain — every pinned behaviour,
#          exempt or not, observed Passed in the runner's OWN TRX now that the implementation landed.
#          Without it, a task-11 attempt could turn 02 green by deleting the rows it cannot satisfy:
#          02 only requires that the tests it DOES run pass.
#
#          FORWARD polarity, and its boundary stated: a forward census cannot see a hollow body (a
#          hollow test passes here too). What it CAN see is a test that was never written, one that was
#          deleted, one that no longer carries the plan trait, and one left [Skip]ped.
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

$env:DOTNET_CLI_UI_LANGUAGE = 'en'

$filter = 'Category=OverwatchSupply&FullyQualifiedName~SuppliedDrainTests'

$pinned = @(
    'CommitPaths_CommitsOnlyItsPathspec_WhenAnUnrelatedFileIsStaged',
    'CommitPaths_CommitsWithTheSuppliedByTrailer',
    'CommitPaths_ReturnsTheCommittedPathsBytesAndCommitSha',
    'CommitPaths_OnAnEmptyPathList_MakesNoCommit',
    'CommitPaths_WhenTheCommitFails_ResetsHardToThePreCommitHead',
    'Drain_CommitsOnlyTheStagedFiles_WhenAnUnrelatedFileIsStaged',
    # Task 10's declared red-census exemptions — the never-weaker rows. They were green before this
    # task and must still be green after it: that IS "Drain's behaviour is unchanged" (design 41 §5).
    'Drain_OnAnEmptyStagingTree_DoesNothingAndMakesNoCommit',
    'Drain_CopiesEveryStagedFileToItsWorkspacePath',
    'Drain_CommitsWithTheSuppliedByTrailer',
    'Drain_DeletesTheStagingTreeAfterCommitting',
    'Drain_ReturnsTheCommittedPathsAndByteCount'
)

$results = Join-Path $env:TEMP ("gr41-t11-census-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $results -Force | Out-Null   # --results-directory is NOT cleared between runs

try {
    & dotnet test "tests/Guardrails.Core.Tests/Guardrails.Core.Tests.csproj" -c Debug --nologo `
        --filter $filter `
        --logger "trx;LogFileName=census.trx" --results-directory $results 2>&1 | Out-String | Write-Output

    $trx = Get-ChildItem -Path $results -Filter '*.trx' -File | Select-Object -First 1
    if (-not $trx) {
        # PRECONDITION, not an unbound-behaviour report: "the run did not happen" is a different fact
        # from "the tests did not behave as required".
        Write-Output "PRECONDITION: no TRX was produced — the test run did not happen (a build break, or a bad filter). Fix that first; this is NOT a statement about the pinned behaviours."
        exit 1
    }

    # Dotted navigation: the TRX carries a default xmlns, so SelectNodes('//UnitTestResult') returns
    # nothing without a namespace manager — a silent zero-result census.
    [xml]$doc = Get-Content -Raw -LiteralPath $trx.FullName
    $nodes = @($doc.TestRun.Results.UnitTestResult | Where-Object { $_ })

    if ($nodes.Count -lt 1) {
        # @($null).Count is 1 (no <Results> element when nothing ran), so the Where-Object above is what
        # lets this guard fire at all.
        Write-Output "PRECONDITION: the filter '$filter' matched NO tests. A zero-match filter exits 0 and certifies nothing — check the class name and the Category=OverwatchSupply trait."
        exit 1
    }

    $failures = @()
    foreach ($name in $pinned) {
        $matching = @($nodes | Where-Object { $_.testName -cmatch ('\.' + [regex]::Escape($name) + '(\(|$)') })
        if ($matching.Count -lt 1) {
            $failures += "[$name] NOT FOUND in the TRX — task 10 pinned this behaviour to a test of that name. It was deleted, renamed, or it lost the Category=OverwatchSupply trait. Restore it; do not re-point this census."
            continue
        }
        # EVERY matching row: a [Theory] with one green and one red row is not a passed behaviour, and
        # -First 1 would report whichever the runner listed first. 'NotExecuted' (a skip) fails here too.
        foreach ($node in $matching) {
            if ($node.outcome -ne 'Passed') {
                $failures += "[$($node.testName)] outcome was '$($node.outcome)', expected 'Passed'."
            }
        }
    }

    if ($failures.Count -gt 0) {
        Write-Output ""
        Write-Output "=== Forward census FAILED ($($failures.Count) unbound behaviour(s)) ==="
        $failures | ForEach-Object { Write-Output $_ }
        exit 1
    }

    Write-Output "Forward census: all $($pinned.Count) pinned behaviour(s) observed Passed."
    exit 0
}
finally {
    Remove-Item -Recurse -Force -LiteralPath $results -ErrorAction SilentlyContinue
}
