# catches: an example that validates but is missing a folder kind, OR still carries a retired construct
#          (integrationGate task / no-op ROOT-END task / scope:"precondition"). Structure + absence grep,
#          scoped to the example folder this task owns.
$ex = "docs/plans/09-preflight-first-class/example"
foreach ($rel in @('preflights', 'guardrails')) {
    $p = Join-Path $ex $rel
    if (-not (Test-Path $p -PathType Container)) {
        Write-Output "$p (plan-level $rel folder) is missing - the example must exercise all four folder kinds"
        exit 1
    }
    if ((Get-ChildItem $p -File | Measure-Object).Count -lt 1) {
        Write-Output "$p is empty - it must carry at least one deterministic check"
        exit 1
    }
}
# a task-level tasks/<id>/preflights/ must exist somewhere
$taskPreflights = Get-ChildItem (Join-Path $ex 'tasks') -Recurse -Directory -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -eq 'preflights' }
if (-not $taskPreflights) {
    Write-Output "no example/tasks/<id>/preflights/ folder - the task-level JIT dependency-delivery illustration is missing"
    exit 1
}
# retired constructs must be ABSENT (negative assertions, #176)
$taskJsons = Get-ChildItem (Join-Path $ex 'tasks') -Recurse -Filter 'task.json' -ErrorAction SilentlyContinue
foreach ($tj in $taskJsons) {
    if ((Get-Content $tj.FullName -Raw) -match '"integrationGate"\s*:\s*true') {
        Write-Output "$($tj.FullName) still declares integrationGate: true - the terminal checks must live in example/guardrails/ (GR2017/integrationGate retired)"
        exit 1
    }
}
$hits = Get-ChildItem $ex -Recurse -File | Where-Object { (Get-Content $_.FullName -Raw) -match '["'']precondition["'']' }
if ($hits) {
    Write-Output "example still uses the retired scope value ""precondition"" in [$(($hits | ForEach-Object Name) -join ', ')]"
    exit 1
}
exit 0
