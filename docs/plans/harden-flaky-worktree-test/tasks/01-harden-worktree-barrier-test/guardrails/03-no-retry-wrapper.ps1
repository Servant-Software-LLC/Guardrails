# catches: a "fix" that makes guardrail 04's 20x-repeat loop pass by wrapping the test body in an
#          INNER retry-until-pass loop (e.g. re-running RunAsync + the assertions up to N times,
#          swallowing the assertion failure until one attempt succeeds) instead of fixing the
#          actual race in Scheduler.cs/BarrierExecutor. The action prompt explicitly forbids this,
#          but nothing structural enforced it - and it is the textbook way to brute-force a ~50%
#          per-try race into a high per-outer-iteration pass rate without changing anything real
#          (found by an adversarial guardrails-review pass, not guessed). The original method has
#          exactly one call to RunAsync and no catch/loop constructs around the assert block - this
#          check locks that shape in.
$ErrorActionPreference = 'Stop'
$path = "tests/Guardrails.Core.Tests/WorktreeProviderSeamTests.cs"
$content = Get-Content -Raw -Path $path

$marker = 'Scheduler_DrivesThreeIndependentTasks_WithWorktreeHandles_OverlapProvenByBarrier'
$idx = $content.IndexOf($marker)
if ($idx -lt 0) {
    Write-Output "could not find $marker in $path - has the test been renamed or removed? re-add it or update this guardrail"
    exit 1
}
$body = $content.Substring($idx)

$runAsyncCalls = ([regex]::Matches($body, '\.RunAsync\(')).Count
if ($runAsyncCalls -ne 1) {
    Write-Output "expected exactly 1 call to .RunAsync( in the barrier test, found $runAsyncCalls - a retry-until-pass wrapper re-runs the scheduler multiple times inside one test method, which is explicitly forbidden; fix the actual race in Scheduler.cs/BarrierExecutor instead"
    exit 1
}

foreach ($construct in @('catch\s*\(', '\bfor\s*\(', '\bwhile\s*\(', '\bdo\s*\{')) {
    if ($body -match $construct) {
        Write-Output "found a '$construct' construct in the barrier test method - this reads as a retry/catch-swallow wrapper around the assertions, which is explicitly forbidden; fix the actual race in Scheduler.cs/BarrierExecutor instead, do not brute-force the race with an inner retry loop"
        exit 1
    }
}

exit 0
