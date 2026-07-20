# catches: the escalation exit code forgotten, given a colliding value, or declared-but-never-returned —
#          a firstmate consumer would then read an answer-required halt as a plain needs-human (2) or as
#          green (0), defeating §7.1. Cheap STRUCTURAL fast-fail complement to the composition-root
#          integration test (guardrail 02): asserts ExitCodes.cs declares EscalationsPending = 4 (the next
#          free value, NOT reusing 0/1/2/3) AND RunCommand.cs references it. NOT sufficient alone (a grep
#          cannot prove it is returned on the RIGHT condition — the Cli_RunWithUnresolvedEscalation fact in
#          guardrail 02 proves that observably). Scoped to the two files this task owns.
$exitcodes = "src/Guardrails.Cli/ExitCodes.cs"
$runcmd = "src/Guardrails.Cli/Commands/RunCommand.cs"

if (-not (Test-Path $exitcodes)) {
    Write-Output "$exitcodes does not exist"
    exit 1
}
if (-not (Test-Path $runcmd)) {
    Write-Output "$runcmd does not exist"
    exit 1
}

$ec = Get-Content -Raw -Path $exitcodes
if ($ec -notmatch 'EscalationsPending\s*=\s*4\b') {
    Write-Output "$exitcodes does not declare 'EscalationsPending = 4' — add the new distinct exit code (the next free value after Success=0/HarnessError=1/TaskFailed=2/Cancelled=3). Do NOT reuse an existing value: §7.1 requires an answer-required halt be distinguishable from a plain needs-human."
    exit 1
}

$rc = Get-Content -Raw -Path $runcmd
if ($rc -notmatch 'EscalationsPending') {
    Write-Output "$runcmd never references ExitCodes.EscalationsPending — the constant is declared but RunCommand never RETURNS it, so a run ending with unresolved escalations still exits 2/needs-human. Map the unresolved-escalations RunReport signal to ExitCodes.EscalationsPending."
    exit 1
}
exit 0
