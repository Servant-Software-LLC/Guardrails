# catches: a merged HEAD whose suite is red. Every task's own filtered tests passed in its own
#          segment; this is the only check that runs the WHOLE suite on the merged result. LOCAL,
#          not scope:"integration" (#165).
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

$env:DOTNET_CLI_UI_LANGUAGE = 'en'
$out = & dotnet test Guardrails.sln -c Debug --nologo 2>&1 | Out-String
$code = $LASTEXITCODE

Write-Output $out

if ($code -ne 0) {
    Write-Output ""
    Write-Output "=== Whole-suite FAILURE detail (re-emitted for the retry tail, #179) ==="
    foreach ($line in ($out -split "`r?`n")) {
        if ($line -match '^\s*(Error Message|Expected|Actual|Stack Trace|Assert\.|\s+at )') {
            Write-Output $line
        }
    }
    exit 1
}
exit 0
