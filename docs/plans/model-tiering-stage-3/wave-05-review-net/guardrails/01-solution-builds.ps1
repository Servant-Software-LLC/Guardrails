# catches: a wave whose per-task guardrails all passed but whose MERGED result does not compile - the
#          classic union defect. This wave's specific exposure is narrow but real:
#          `tests/Guardrails.Core.Tests/ModelTiering/TierClassificationAudit.cs` is the one file two tasks
#          write. 01 creates it as throwing stubs and 02 replaces those bodies. The pair is sequential in
#          the DAG (02 depends on 01), so it is not a true collision - but an AI-merge that keeps BOTH
#          copies of a member produces a duplicate declaration with NO conflict marker (#175), and only a
#          build sees it.
#
#          The whole SOLUTION rather than the one test project, because this wave's two chains land in the
#          same assembly from two independent branches: the audit chain (01, 02) and the doctrine-anchor
#          chain (03, 04) both add files to Guardrails.Core.Tests with no edge between them, so their
#          merge is the first moment anything compiles them together.
# LOCAL - no `scope` key, deliberately (GR2059 / #459). A wave-root guardrail runs EXACTLY ONCE, on the
# merged HEAD at its own wave's exit; the per-union re-verify set is the task guardrails/ folders plus the
# PLAN-root guardrails/ folder and nothing else (SSOT 4.3). Tagging this `scope:"integration"` would buy
# nothing and make the plan merely LOOK protected. It is also a terminal postcondition, so it would
# red-halt a correct partial merge if it ever did run per-union (#125/#165).
$env:DOTNET_CLI_UI_LANGUAGE = 'en'
# -v q IS correct on a BUILD (dotnet.md section 4) - it is the TEST command that must never carry it (#179).
$out = dotnet build Guardrails.sln -v q --nologo 2>&1
$buildExit = $LASTEXITCODE                                 # capture BEFORE any other statement
$out | ForEach-Object { Write-Output $_ }

if ($buildExit -ne 0) {
    $detail = $out |
        Select-String -Pattern 'error [A-Z]{2}\d+|: error|Build FAILED' |
        ForEach-Object { $_.Line } |
        Select-Object -First 40
    Write-Output ""
    Write-Output "=== Build errors (re-emitted so they land in the harness feedback tail) ==="
    if ($detail) { $detail | ForEach-Object { Write-Output $_ } }
    else { Write-Output "(no error lines matched - inspect the full log above)" }
    Write-Output "the merged wave-5 HEAD does not build. Look first at tests/Guardrails.Core.Tests/ModelTiering/TierClassificationAudit.cs (tasks 01+02) - a CS0111 duplicate member there is the AI-merge keeping both copies of a member (#175) - and then at the two new test classes, which arrive from two independent branches into one assembly."
    exit 1
}
exit 0
