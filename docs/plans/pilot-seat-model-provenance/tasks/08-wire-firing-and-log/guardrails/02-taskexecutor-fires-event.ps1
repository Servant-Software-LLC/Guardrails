# catches: a TaskExecutor that passes the test by an incidental path but never invokes the observer event in production - structural proof it references AttemptModelResolved. Scoped to the one file.
$ws = $env:GUARDRAILS_WORKSPACE; if ([string]::IsNullOrEmpty($ws)) { $ws = (Get-Location).Path }
$path = Join-Path $ws 'src/Guardrails.Core/Execution/TaskExecutor.cs'
if (-not (Test-Path $path)) {
    Write-Output 'src/Guardrails.Core/Execution/TaskExecutor.cs does not exist.'
    exit 1
}
$content = Get-Content -Raw -Path $path
if (($content -notmatch 'AttemptModelResolved')) {
    Write-Output 'TaskExecutor.cs does not invoke AttemptModelResolved - the event is never fired in production.'
    exit 1
}
exit 0
