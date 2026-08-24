# catches: a test file that does not COMPILE being passed off as the TDD "red" (#155). A non-compiling
#          test project exits `dotnet test` non-zero for a reason that has nothing to do with the behaviour
#          under test, so the census in 02 would read garbage as a clean red. Build FIRST, cheapest-first.
#
#          BOTH test projects, because this task authors into both: the aggregation class lands in
#          Guardrails.Core.Tests (beside its PerTierSpendTests sibling) and the end-to-end report class in
#          Guardrails.Integration.Tests (the only project that references Guardrails.Cli, and therefore the
#          only place the real `run` command can be driven). Building only one would leave half this task's
#          deliverable uncompiled while the census blamed the tests.
# MEASURED BASELINE 2026-08-23: both test projects build green on this wave's entry tree, so both clauses
# are REGRESSION guards on this task's own edits.
$ErrorActionPreference = 'Continue'
$env:DOTNET_CLI_UI_LANGUAGE = 'en'
$failures = @()

foreach ($proj in @('tests/Guardrails.Core.Tests', 'tests/Guardrails.Integration.Tests')) {
    # -v q IS correct on a BUILD (dotnet.md section 4); it is the TEST command that must never carry it (#179).
    $out = dotnet build $proj -v q --nologo 2>&1
    $buildExit = $LASTEXITCODE                            # capture BEFORE any other statement
    $out | ForEach-Object { Write-Output $_ }

    if ($buildExit -ne 0) {
        $detail = $out | Select-String -Pattern 'error [A-Z]{2}\d+|: error|Build FAILED' |
            ForEach-Object { $_.Line } | Select-Object -First 40
        Write-Output ""
        Write-Output "=== $proj build errors (re-emitted so they land in the harness feedback tail) ==="
        if ($detail) { $detail | ForEach-Object { Write-Output $_ } }
        else { Write-Output "(no error lines matched - inspect the full log above)" }
        $failures += "$proj does not compile - the new tests must COMPILE and FAIL. A missing symbol here is most likely JournalModelsUsed / ModelUsage: that stub is YOURS to write (src/Guardrails.Core/Journal/JournalModelsUsed.cs is in this task's writeScope), so add the declaration rather than deleting the assertion that needs it"
    }
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== tests build: $($failures.Count) of 2 project(s) failed ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    exit 1
}
exit 0
