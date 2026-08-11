# catches: a hardening edit that "fixes" the flake by weakening the assertions (e.g. tolerating
#          fewer than 3 arrivals) or by retrying-until-pass inside the test itself - either would
#          make a single run look green while proving nothing. Running the SAME unmodified test
#          N times back-to-back in fresh processes is a deterministic, repeatable proxy for
#          "meaningfully more robust against CI-load-induced timing variance": a fix that only
#          moved the failure from a 3/5 rate to, say, a 1/50 rate would still fail this loop
#          often enough to be caught here, while a genuine fix (thread-pool floor raised, a
#          real rendezvous mechanism, a wider deadline matched to the diagnosed cause) should
#          clear it consistently.
#
# HONEST LIMIT (state this plainly - do not oversell it): this raises empirical confidence, it
# does NOT prove the absence of a race. 20 consecutive local passes is evidence, not a proof -
# a timing race can in principle still surface under a CI runner more loaded than this box. The
# terminal gate's own re-run of the whole suite (below) and ordinary CI observation over time
# are the backstop; this guardrail's job is to catch a fix that is clearly still flaky, not to
# certify the fix is flake-free forever.
$ErrorActionPreference = 'Stop'
$iterations = 20
$failures = 0
$lastFailureLog = $null

for ($i = 1; $i -le $iterations; $i++) {
    $out = dotnet test tests/Guardrails.Core.Tests/Guardrails.Core.Tests.csproj `
        --filter "FullyQualifiedName~WorktreeProviderSeamTests.Scheduler_DrivesThreeIndependentTasks_WithWorktreeHandles_OverlapProvenByBarrier" `
        --nologo 2>&1
    if ($LASTEXITCODE -ne 0) {
        $failures++
        $lastFailureLog = $out
        Write-Output "iteration $i/$iterations : FAILED"
    } else {
        Write-Output "iteration $i/$iterations : passed"
    }
}

if ($failures -gt 0) {
    Write-Output ""
    Write-Output "=== Failure details from the last failing iteration (re-emitted so they land in the harness feedback tail) ==="
    $detail = $lastFailureLog |
        Select-String -Pattern '\[FAIL\]|Error Message:|Assert\.|Exception|Stack Trace:|Expected:|Actual:' |
        ForEach-Object { $_.Line } |
        Select-Object -First 40
    if ($detail) { $detail | ForEach-Object { Write-Output $_ } }
    else { Write-Output "(no assertion/exception lines matched - inspect the full log above)" }
    Write-Output "the barrier test failed $failures of $iterations consecutive runs - still flaky; the hardening fix does not yet address the actual race (do not weaken the assertions or add a retry-until-pass - fix the diagnosed root cause instead)"
    exit 1
}

Write-Output "the barrier test passed all $iterations consecutive runs"
exit 0
