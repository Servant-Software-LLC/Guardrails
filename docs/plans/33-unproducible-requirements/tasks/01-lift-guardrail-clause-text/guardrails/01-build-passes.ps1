# catches: a refactor that does not compile - a member left half-moved, a call site not repointed, or a
#          missing using. The lift touches two files and every GR2057 call site, so a green build is the
#          cheapest first gate and it runs before the slower test check.
$ErrorActionPreference = 'Continue'
$env:DOTNET_CLI_UI_LANGUAGE = 'en'

$log = & dotnet build Guardrails.sln -v q --nologo 2>&1 | Out-String
$code = $LASTEXITCODE
Write-Output $log

if ($code -ne 0) {
    Write-Output ""
    Write-Output "=== Build failures (detail re-emitted) ==="
    foreach ($line in ($log -split "\r?\n")) {
        if ($line -match 'error [A-Z]{2}\d+|: error ') { Write-Output $line }
    }
    Write-Output ""
    Write-Output "The solution does not build after lifting the clause helpers into GuardrailClauseText. Every member moved must keep its call sites working - PlanValidator still calls all seven through GuardrailClauseText, and StripCommentLines must call GuardrailClauseText.IsCommentLine rather than a local copy."
    exit 1
}
exit 0
