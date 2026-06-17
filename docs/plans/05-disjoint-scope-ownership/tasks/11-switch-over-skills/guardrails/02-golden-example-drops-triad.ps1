# catches: the regenerated golden example still carrying the retired triad - the switch-over is
#          only real if the few-shot reference itself stops emitting captureHashes/restoreOnRetry
#          and tests-untouched. Scoped to the committed golden example folder this task regenerates
#          (grep-scope rule). Greps the task.json + guardrail files under the example.
$exampleRoot = "examples/hello-guardrails/hello-guardrails"
if (-not (Test-Path $exampleRoot)) {
    Write-Output "$exampleRoot does not exist - the golden example was not regenerated."
    exit 1
}
$offenders = Get-ChildItem -Path $exampleRoot -Recurse -File -Include *.json, *.ps1, *.sh, *.md |
    Where-Object { (Get-Content $_.FullName -Raw) -match 'captureHashes|restoreOnRetry|tests-untouched' }
if ($offenders) {
    $names = ($offenders | ForEach-Object { $_.Name }) -join ', '
    Write-Output "The golden example still references the retired triad (captureHashes/restoreOnRetry/tests-untouched) in: $names - regenerate it to the writeScope pattern."
    exit 1
}
$hasWriteScope = Get-ChildItem -Path $exampleRoot -Recurse -File -Filter task.json |
    Where-Object { (Get-Content $_.FullName -Raw) -match 'writeScope' }
if (-not $hasWriteScope) {
    Write-Output "No task.json under $exampleRoot declares writeScope - the regenerated example does not demonstrate the new ownership model."
    exit 1
}
exit 0
