# catches: a test file that does not COMPILE being passed off as the TDD "red" (#155). A non-compiling
#          test project exits `dotnet test` non-zero for a reason that has nothing to do with the doctrine
#          under test, so the census in 02 would read garbage as a clean red. Build FIRST, cheapest-first.
#
#          There is no stub to write here - the anchors read a markdown document, so nothing they reference
#          is missing by design. A compile error at this task is therefore an ordinary mistake (an
#          unbalanced raw string literal is the likely one: the clauses carry backticks, quotes and em
#          dashes) and not the "the type I need does not exist yet" case.
# MEASURED BASELINE 2026-08-24: tests/Guardrails.Core.Tests builds green on this wave's entry tree, so this
# clause is a REGRESSION guard on this task's own edit.
$ErrorActionPreference = 'Continue'
$env:DOTNET_CLI_UI_LANGUAGE = 'en'

# -v q IS correct on a BUILD (dotnet.md section 4); it is the TEST command that must never carry it (#179).
$out = dotnet build tests/Guardrails.Core.Tests -v q --nologo 2>&1
$buildExit = $LASTEXITCODE                                 # capture BEFORE any other statement
$out | ForEach-Object { Write-Output $_ }

if ($buildExit -ne 0) {
    $detail = $out | Select-String -Pattern 'error [A-Z]{2}\d+|: error|Build FAILED' |
        ForEach-Object { $_.Line } | Select-Object -First 40
    Write-Output ""
    Write-Output "=== build errors (re-emitted so they land in the harness feedback tail) ==="
    if ($detail) { $detail | ForEach-Object { Write-Output $_ } }
    else { Write-Output "(no error lines matched - inspect the full log above)" }
    Write-Output "tests/Guardrails.Core.Tests does not compile. The clauses contain backticks, apostrophes, em dashes and asterisks - hold each one in a raw string literal rather than escaping it by hand, and do NOT alter a clause's characters to make it easier to quote: the skill task is given the identical list and a divergence here dead-ends it."
    exit 1
}
exit 0
