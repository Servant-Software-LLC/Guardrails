# catches: building this plan on an ALREADY-RED area. Every task here modifies code the integration
#          suite covers, so a pre-existing failure would be misattributed to the first task that
#          runs, burn its retries, and surface as a late needs-human (#181).
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

$env:DOTNET_CLI_UI_LANGUAGE = 'en'

# The plan-wide trait belongs in exactly ONE place — this != exclusion (#455) — never in a
# task-level filter.
$out = & dotnet test "tests/Guardrails.Integration.Tests/Guardrails.Integration.Tests.csproj" -c Debug --nologo --filter "Category!=WaveDelivery" 2>&1 | Out-String
$code = $LASTEXITCODE

Write-Output $out

if ($code -ne 0) {
    Write-Output ""
    Write-Output "=== The integration area is ALREADY RED before this plan runs ==="
    foreach ($line in ($out -split "`r?`n")) {
        if ($line -match '^\s*(Error Message|Expected|Actual|Stack Trace|Assert\.|\s+at )') {
            Write-Output $line
        }
    }
    Write-Output ""
    Write-Output "Fix the pre-existing breakage before this plan builds on it — no task here can."
    exit 1
}
exit 0
