# catches: a revert that passes its unit tests but leaves the --fresh/reset wipe unwired - the two
#          live in different files and the revert tests may not exercise RunReset. Assert the
#          enforcer declares RevertOutOfScope AND RunReset.cs references the scope-baseline directory.
#          Scoped to the two files this task owns (grep-scope rule, stacks/dotnet.md §5).
$enforcer = "src/Guardrails.Core/Execution/WorkspaceScopeEnforcer.cs"
if ((Get-Content $enforcer -Raw) -notmatch '(?m)\bRevertOutOfScope\b') {
    Write-Output "$enforcer does not declare RevertOutOfScope - the M5 revert method is missing or named differently than the design requires."
    exit 1
}
$reset = "src/Guardrails.Core/State/RunReset.cs"
if ((Get-Content $reset -Raw) -notmatch 'scope-baseline') {
    Write-Output "$reset does not reference 'scope-baseline' - the --fresh/reset wipe of state/scope-baseline/ was not wired (untracked-file baselines would leak across fresh runs)."
    exit 1
}
exit 0
