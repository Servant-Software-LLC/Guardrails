# catches: the agent claimed success but never wrote the report, or wrote it
#          without the required sections (a skeleton that quotes nothing)
#
# Three clauses, so it ACCUMULATES (#478): every missing section is named in ONE run. An `exit 1`
# inside the loop would report only the FIRST gap and cost one attempt per section. The Test-Path
# check is the one legitimate early exit - every clause below reads a file that would not be there.
#
# Baseline: `out/` does not exist before the run, so all three clauses are red on arrival by
# construction. Against a BROWNFIELD target, measure each required literal on the real file first
# and record the count here - a clause already satisfied on arrival certifies nothing.
if (-not (Test-Path "out/report.md")) {
    Write-Output "out/report.md does not exist in the workspace"
    exit 1
}
$content = Get-Content "out/report.md" -Raw
$failures = @()
foreach ($required in @('# Greeting Quality Report', '## Greeting', '## Tone assessment')) {
    if ($content -notlike "*$required*") {
        $failures += "out/report.md is missing required section '$required'"
    }
}
if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Output $_ }
    exit 1
}
exit 0
