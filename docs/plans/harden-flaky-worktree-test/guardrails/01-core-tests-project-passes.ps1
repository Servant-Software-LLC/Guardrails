# catches: a hardening edit that fixes the barrier test's flakiness in isolation but regresses
#          something else in Guardrails.Core.Tests - e.g. a ThreadPool.SetMinThreads change that
#          leaks across the test process and perturbs timing-sensitive assertions elsewhere in
#          the same project, or an unrelated typo introduced while editing the file. This is the
#          terminal, whole-project postcondition (#165: LOCAL, no `scope` key - it runs once,
#          here, after the one work task has merged; it is not re-run at any intermediate union
#          because this plan has none).
#
# Re-emits failure detail at the end so it lands in the harness retry-feedback tail (#179, §4.2).
$ErrorActionPreference = 'Stop'
$out = dotnet test tests/Guardrails.Core.Tests/Guardrails.Core.Tests.csproj --nologo 2>&1
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
    Write-Output "Guardrails.Core.Tests has failures after the barrier-test hardening - the rest of the project must stay green (issue #214 acceptance)"
    exit 1
}
exit 0
