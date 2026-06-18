# catches: the dogfood's own plan (docs/plans/04-dogfood-cost-cap) left on the retired triad
#          pattern - the prompt says regenerate it to writeScope, but nothing else proves it did.
#          (No `guardrails validate` here: the installed preview.19 validator predates writeScope;
#          these greps are the robust deterministic check.)
$planRoot = "docs/plans/04-dogfood-cost-cap"
if (-not (Test-Path $planRoot)) {
    Write-Output "$planRoot does not exist - the dogfood plan was not regenerated to the writeScope pattern."
    exit 1
}
$taskJsons = Get-ChildItem -Path $planRoot -Recurse -File -Filter task.json
$hasWriteScope = $taskJsons | Where-Object { (Get-Content $_.FullName -Raw) -match 'writeScope' }
if (-not $hasWriteScope) {
    Write-Output "No task.json under $planRoot declares writeScope - regenerate the dogfood plan to the writeScope ownership model."
    exit 1
}
$triadTasks = $taskJsons | Where-Object { (Get-Content $_.FullName -Raw) -match 'captureHashes|restoreOnRetry' }
if ($triadTasks) {
    $names = ($triadTasks | ForEach-Object { $_.FullName.Substring($_.FullName.IndexOf('docs')) }) -join ', '
    Write-Output "task.json under $planRoot still declares the retired triad (captureHashes/restoreOnRetry): $names - regenerate to writeScope."
    exit 1
}
$triadFiles = Get-ChildItem -Path $planRoot -Recurse -File -Include *.ps1, *.sh |
    Where-Object { $_.Name -match 'tests-untouched' }
if ($triadFiles) {
    $names = ($triadFiles | ForEach-Object { $_.Name }) -join ', '
    Write-Output "Impl/guardrail files under $planRoot still carry a tests-untouched filename: $names - the regenerated dogfood plan must drop the triad guardrail."
    exit 1
}
exit 0
