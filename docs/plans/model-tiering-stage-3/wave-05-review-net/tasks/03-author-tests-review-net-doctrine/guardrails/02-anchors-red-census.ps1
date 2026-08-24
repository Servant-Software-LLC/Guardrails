# catches: an anchor row that pins NOTHING - the doctrine-anchor version of the hollow test (#375), and the
#          one failure mode an exit-code check cannot see. `dotnet test` exits non-zero if ANY selected test
#          fails, so a row softened to a phrase the skill ALREADY carries ("do not re-report what validate
#          already says" is right there in the #224 probe today) is GREEN on this tree and hides behind its
#          thirteen genuinely-failing siblings. It would then look like coverage forever, while pinning a
#          sentence this wave never wrote.
#
#          The predicate is therefore not "the suite is red" but "NO anchor row is green", read from the
#          runner's own TRX result file - never stdout (#248), never `--list-tests` name discovery.
#
#          THREE groups, three required outcomes.
#
#          (A) Every `TheSkillStillCarriesTheClause` row must be observed FAILED, and there must be at least
#          14 of them. Zero-green is the teeth; the floor is the manifest CARDINALITY - the prompt pins
#          exactly fourteen clauses - and it catches a row list quietly shortened to the easy ones. It is
#          not an adequacy floor (#468): it does not claim fourteen rows are enough doctrine, only that the
#          declared fourteen are all present and all red.
#
#          (B) `TheThreeInsertionsLandInTheirOwnSections` must be observed FAILED. It is the fact the clause
#          theory cannot carry: a probe pasted at the END of the skill satisfies all fourteen clauses.
#
#          (C) `TheAnchorSetIsEvidence_NotCeremony` must be observed PASSED. It reads no skill text, so it
#          is green as soon as the row list is well-formed - and requiring it closes the hole this census
#          would otherwise leave open, where a row list that failed to load at all was counted as a clean
#          red. It is also what makes group A's "at least 14" mean fourteen DISTINCT rows: the hygiene fact
#          is what rejects a duplicate.
#
#          What it does NOT prove: that each row pins the clause the prompt intended it to. The TRX display
#          name of a theory row is not a stable contract - xUnit truncates long string arguments - so this
#          census deliberately does not try to bind row to clause by name. That binding is a human read, and
#          04-add-model-appropriateness-probe is where a wrong clause surfaces, as a row that stays red
#          after the skill was written.
#
# SCOPE (#455): ONE class, in one project. `ModelAppropriatenessDoctrineAnchorTests` is a substring of no
# other test class anywhere under tests/ (verified 2026-08-24: it occurs nowhere in the tree on this wave's
# entry tree). Every row is made green by 04-add-model-appropriateness-probe, a task DOWNSTREAM of this one,
# so no sibling's tests could satisfy this red for us and this check waits on no descendant.
#
# INVERSE polarity for groups A and B: non-zero from `dotnet test` is SUCCESS here, so the zero-match guard
# runs FIRST - a crash, or a filter that selected nothing, must never be certified as TDD red (#455).
$ErrorActionPreference = 'Continue'
$env:DOTNET_CLI_UI_LANGUAGE = 'en'

$clauseFact = 'TheSkillStillCarriesTheClause'
$placementFact = 'TheThreeInsertionsLandInTheirOwnSections'
$hygieneFact = 'TheAnchorSetIsEvidence_NotCeremony'
$expectedRows = 14

$failures = @()
$filter = 'FullyQualifiedName~ModelAppropriatenessDoctrineAnchorTests'
$tmp = Join-Path ([System.IO.Path]::GetTempPath()) ("gr-anchors-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tmp -Force | Out-Null

try {
    # NO -v q on a TEST command (#179).
    $out = dotnet test tests/Guardrails.Core.Tests --nologo --filter $filter `
        --logger "trx;LogFileName=anchors.trx" --results-directory $tmp 2>&1
    $out | ForEach-Object { Write-Output $_ }

    $trx = Get-ChildItem -Path $tmp -Filter '*.trx' -File -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $trx) {
        # PRECONDITION: with no result file every clause below would report "not executed" and blame the
        # tests for a run that never happened.
        Write-Output ""
        Write-Output "no .trx was produced - the test RUN did not happen (build failure, host crash, or a malformed --filter). This is not a verdict about the anchors; read the log above."
        exit 1
    }

    [xml]$doc = Get-Content -Raw -Path $trx.FullName
    # `Where-Object { $_ }` is LOAD-BEARING, not tidiness. On a TRX with no results at all,
    # `$doc.TestRun.Results.UnitTestResult` is $null, and `@($null)` has Count 1 in PowerShell - so the
    # zero-match guard below would see one "result", never fire, and an empty run would be reported as
    # missing anchor rows instead of as a filter that selected nothing. Measured against the entry tree on
    # 2026-08-24 on this file's sibling census: that is exactly what the first draft did.
    $results = @($doc.TestRun.Results.UnitTestResult | Where-Object { $_ })

    # ZERO-MATCH GUARD (#455), FIRST because the polarity is inverse.
    if ($results.Count -lt 1) {
        Write-Output ""
        Write-Output "the filter $filter selected ZERO tests - the class is missing, empty, or named differently. Nothing was measured, so nothing is proven red."
        exit 1
    }

    # --- (A) every clause row red, and the set not shortened ---------------------------------------
    $rows = @($results | Where-Object { $_.testName -like "*$clauseFact*" })
    if ($rows.Count -lt $expectedRows) {
        $failures += "only $($rows.Count) '$clauseFact' row(s) executed, expected at least $expectedRows - the prompt pins fourteen clauses and this census reads them out of the run. A shortened row list is coverage that was quietly dropped"
    }

    $green = @($rows | Where-Object { $_.outcome -ne 'Failed' })
    if ($green.Count -gt 0) {
        $names = ($green | ForEach-Object { "$($_.testName) [$($_.outcome)]" }) -join '; '
        $failures += "$($green.Count) of $($rows.Count) anchor row(s) did NOT fail on this tree: $names - a row that is already green pins a sentence the skill ALREADY carries, so it can never detect the doctrine being lost. Re-point it at the clause the prompt actually lists"
    }

    # --- (B) the placement fact, red ---------------------------------------------------------------
    $placement = @($results | Where-Object { $_.testName -like "*$placementFact*" })
    if ($placement.Count -lt 1) {
        $failures += "'$placementFact' was not executed at all - the prompt pins this method name and this census reads it. Without it a probe appended to the END of the skill satisfies every clause row"
    }
    elseif (@($placement | Where-Object { $_.outcome -eq 'Failed' }).Count -lt 1) {
        $failures += "'$placementFact' ran but did NOT fail (outcome: $(($placement | ForEach-Object { $_.outcome }) -join ', ')) - the probe does not exist yet, so its three insertion points cannot be in their sections. A green here means the fact asserts nothing about placement"
    }

    # --- (C) the hygiene fact, green ---------------------------------------------------------------
    $hygiene = @($results | Where-Object { $_.testName -like "*$hygieneFact*" })
    if ($hygiene.Count -lt 1) {
        $failures += "'$hygieneFact' was not executed at all - it is the anchor set's own hygiene check (minimum clause length, no duplicate rows) and this census requires it. Without it, a row list that failed to load would be counted as a clean red, and 'at least $expectedRows rows' would not mean $expectedRows DISTINCT ones"
    }
    elseif (@($hygiene | Where-Object { $_.outcome -ne 'Passed' }).Count -gt 0) {
        $failures += "'$hygieneFact' did not PASS (outcome: $(($hygiene | ForEach-Object { $_.outcome }) -join ', ')) - it reads no skill text, so it is green as soon as the row list is well-formed. A red here means a clause is under 19 characters or two rows pin the same one"
    }
}
finally {
    Remove-Item -Path $tmp -Recurse -Force -ErrorAction SilentlyContinue
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== anchor red census: $($failures.Count) problem(s) ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    Write-Output ""
    Write-Output "Every anchor must be RED against the skill as it stands today, and none of them may be softened to something already in it. The clause list in this task's prompt is verbatim and shared with 04-add-model-appropriateness-probe - change a clause here and that task can never satisfy it."
    exit 1
}
exit 0
