# catches: a broken starting point -- some OTHER test in Guardrails.Core.Tests is already red
#          before this plan's task runs, which would let 01-harden-worktree-barrier-test's
#          terminal-gate guardrail (the whole-project test run) misattribute a pre-existing
#          failure to the hardening edit.
#
# POLARITY: positive -- exit 0 when the existing checks PASS, exit 1 when they fail. Runs ONCE,
# plan-wide, before the task's first wave.
#
# SCOPE NOTE: this plan's own task modifies
# WorktreeProviderSeamTests.Scheduler_DrivesThreeIndependentTasks_WithWorktreeHandles_OverlapProvenByBarrier
# -- the very test this plan exists to harden -- so it is EXCLUDED here rather than asserted as a
# stable precondition; its pre-existing flakiness (3 failures in 5 local runs, see the action
# prompt) is the reason this plan exists, not a regression this preflight should catch.
$ErrorActionPreference = 'Stop'
$out = dotnet test tests/Guardrails.Core.Tests/Guardrails.Core.Tests.csproj `
    --filter "FullyQualifiedName!~WorktreeProviderSeamTests.Scheduler_DrivesThreeIndependentTasks_WithWorktreeHandles_OverlapProvenByBarrier" `
    --nologo 2>&1
$out | ForEach-Object { Write-Output $_ }
if ($LASTEXITCODE -ne 0) {
    $detail = $out |
        Select-String -Pattern '\[FAIL\]|Error Message:|Assert\.|Exception|Stack Trace:|Expected:|Actual:' |
        ForEach-Object { $_.Line } |
        Select-Object -First 40
    Write-Output ""
    Write-Output "=== Failure details (re-emitted so they land in the harness feedback tail) ==="
    if ($detail) { $detail | ForEach-Object { Write-Output $_ } }
    else { Write-Output "(no assertion/exception lines matched - inspect the full log above)" }
    Write-Output "Guardrails.Core.Tests has PRE-EXISTING failures unrelated to the barrier test -- fix those before this plan builds on it"
    exit 1
}
exit 0
