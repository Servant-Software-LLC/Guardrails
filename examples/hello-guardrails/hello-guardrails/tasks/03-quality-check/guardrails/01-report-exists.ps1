# catches: the agent claimed success but never wrote the report, or wrote it
#          without the required sections (a skeleton that quotes nothing)
if (-not (Test-Path "out/report.md")) {
    Write-Output "out/report.md does not exist in the workspace"
    exit 1
}
$content = Get-Content "out/report.md" -Raw
foreach ($required in @('# Greeting Quality Report', '## Greeting', '## Tone assessment')) {
    if ($content -notlike "*$required*") {
        Write-Output "out/report.md is missing required section '$required'"
        exit 1
    }
}
exit 0
