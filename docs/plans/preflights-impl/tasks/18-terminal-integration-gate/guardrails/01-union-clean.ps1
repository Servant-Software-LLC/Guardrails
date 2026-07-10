# catches: a union that left git conflict markers in a SHARED multi-writer file - the SSOT doc (edited by
#          the loader/outcomes/pre-DAG/terminal/renderer tasks) and Scheduler.cs (edited by both phase tasks)
#          are the genuine overlapping-writeScope surfaces (#132). The deterministic verdict on EVERY union's
#          bytes, re-run at each non-FF integration and the terminal HEAD.
# scope:"integration" -> MUST be union-safe (#125): gate on each file being present, THEN verify it - never
# "require a contribution present", so a partial merge passes trivially.
$ws = $env:GUARDRAILS_WORKSPACE
if ([string]::IsNullOrEmpty($ws)) { $ws = (Get-Location).Path }
$shared = @(
    'docs/plans/02-schemas-and-contracts.md',
    'src/Guardrails.Core/Execution/Scheduler.cs',
    'src/Guardrails.Core/Execution/SchedulerFactory.cs',
    'src/Guardrails.Core/Execution/TaskExecutor.cs',
    'src/Guardrails.Core/Loading/DiagnosticCodes.cs'
)
foreach ($rel in $shared) {
    $p = Join-Path $ws $rel
    if (-not (Test-Path $p)) { continue }   # not present at this union yet - fine
    $content = Get-Content -Raw -Path $p
    if ([string]::IsNullOrWhiteSpace($content)) {
        Write-Output "$rel is empty on the merged bytes - the union dropped its content"
        exit 1
    }
    # Line-anchored ours/theirs markers only; no bare '=======' (unanchored it false-fires on a
    # '====' banner / setext header — retired by #187, banned by GR2037).
    if ($content -match '(?m)^<<<<<<<' -or $content -match '(?m)^>>>>>>>') {
        Write-Output "$rel contains git conflict markers - the union did not cleanly integrate the overlapping writeScopes"
        exit 1
    }
}
exit 0
