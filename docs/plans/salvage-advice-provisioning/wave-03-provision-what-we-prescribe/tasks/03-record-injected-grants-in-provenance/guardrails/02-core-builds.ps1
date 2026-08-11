# catches: the provenance / log-header change breaking compilation of the harness core.
$log = dotnet build src/Guardrails.Core/Guardrails.Core.csproj -c Release 2>&1 | Out-String
if ($LASTEXITCODE -ne 0) {
    Write-Output $log
    foreach ($line in ($log -split "`r?`n")) { if ($line -match ': error ') { Write-Output $line } }
    Write-Output "Guardrails.Core does not compile after the provenance change"
    exit 1
}
exit 0
