# catches: a wave whose per-task guardrails all passed but whose MERGED result does not compile - the
#          classic union defect. This wave's specific exposure is LiveRunObserver.cs, the one file two
#          tasks write: task 01 adds the throwing `AttemptModelSummary` stub, task 03 replaces its body
#          and adds `AttemptModelResolved`. The pair is sequential in the DAG (03 depends on 01), so it
#          is not a true collision - but an AI-merge that keeps BOTH copies of an appended member
#          produces a duplicate declaration with no conflict marker (#175), which only a build sees.
#          IRunObserver.cs carries the same risk from the other direction: task 01 appends one member to
#          an interface every observer in the solution implements, so a mangled signature there breaks
#          four types at once and no task-level filtered test run compiles the CLI project at all.
# LOCAL - no `scope` key, deliberately (GR2059 / #459). A wave-root guardrail runs EXACTLY ONCE, on the
# merged HEAD at its own wave's exit; the per-union re-verify set is the task guardrails/ folders plus
# the PLAN-root guardrails/ folder and nothing else (SSOT 4.3). Tagging this `scope:"integration"` would
# buy nothing and make the plan merely LOOK protected. It is also a terminal postcondition, so it would
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
    Write-Output "the merged wave-3 HEAD does not build. Look first at LiveRunObserver.cs (tasks 01+03) - a CS0111 duplicate AttemptModelSummary there is the AI-merge keeping both copies of an appended member (#175) - and then at IRunObserver.cs, where a mangled new member breaks every implementation in the solution at once."
    exit 1
}
exit 0
