# catches: a wave whose per-task guardrails all passed but whose MERGED result does not compile - the
#          classic union defect, and the one wave 1 is most exposed to because tasks 03 and 05 both add
#          methods to PlanValidator.cs. Each task's own filtered test run can be green while the merged
#          file has a duplicate member or a broken call site (#175). This is a whole-solution build on
#          the merged wave HEAD.
# LOCAL - no `scope` key, deliberately (GR2059 / #459). A wave-root guardrail runs EXACTLY ONCE, on the
# merged HEAD at its own wave's exit; the per-union re-verify set is the task guardrails/ folders plus
# the PLAN-root guardrails/ folder and nothing else (SSOT §4.3). Tagging this `scope:"integration"`
# would buy nothing and make the plan merely LOOK protected. It is also a terminal postcondition, so it
# would red-halt a correct partial merge if it ever did run per-union (#125/#165).
$env:DOTNET_CLI_UI_LANGUAGE = 'en'
# -v q IS correct on a BUILD (dotnet.md §4) - it is the TEST command that must never carry it (#179).
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
    Write-Output "the merged wave-1 HEAD does not build - most likely a duplicate member or a broken call site where tasks 03 and 05 both edited PlanValidator.cs (#175)"
    exit 1
}
exit 0
