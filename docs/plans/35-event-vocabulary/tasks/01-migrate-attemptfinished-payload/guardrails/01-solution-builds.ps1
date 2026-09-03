# catches: a PARTIAL migration - the whole reason this task is atomic. The signature change breaks
#          every declaration and raise site at once, so a compile failure here means a site was missed,
#          and the compiler names it. This is the PRIMARY proof for this task.
$ErrorActionPreference = 'Continue'
$log = & dotnet build Guardrails.sln -c Debug -v q --nologo 2>&1 | Out-String
$code = $LASTEXITCODE
Write-Output $log
if ($code -ne 0) {
    Write-Output ""
    Write-Output "=== Unmigrated sites (compiler errors) ==="
    $log -split "`r?`n" | Where-Object { $_ -match 'error [A-Z]{2}\d+' } | ForEach-Object { Write-Output $_ }
    Write-Output ""
    Write-Output "The solution does not compile: at least one AttemptFinished declaration or raise site still uses the old (task, attempt, outcome) shape. Migrate every site the errors above name."
    exit 1
}
Write-Output "Solution builds - every AttemptFinished site migrated."
exit 0
