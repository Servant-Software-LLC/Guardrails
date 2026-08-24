# catches: a wave whose per-task guardrails all passed but whose MERGED result does not compile - the
#          classic union defect. This wave's specific exposure is JournalModelsUsed.cs, the one file two
#          tasks write: 01-author-tests-models-used-report creates it as throwing stubs, and
#          02-implement-models-used-report replaces those bodies. The pair is sequential in the DAG (02
#          depends on 01), so it is not a true collision - but an AI-merge that keeps BOTH copies of an
#          appended member produces a duplicate declaration with NO conflict marker (#175), and only a
#          build sees it. RunCommand.cs carries the second risk: it is the entry assembly's largest file
#          and no task-level guardrail in this wave compiles the Cli project except through the
#          integration test project.
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
    Write-Output "the merged wave-4 HEAD does not build. Look first at src/Guardrails.Core/Journal/JournalModelsUsed.cs (tasks 01+02) - a CS0111 duplicate member there is the AI-merge keeping both copies of an appended member (#175) - and then at src/Guardrails.Cli/Commands/RunCommand.cs, where task 02 inserted a line into PrintTotalCost."
    exit 1
}
exit 0
