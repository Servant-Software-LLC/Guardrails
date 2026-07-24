# catches: the proceed-unreviewed exit code forgotten, given a colliding value, or declared-but-never-
#          returned — a firstmate consumer would then read a green-but-unreviewed run as clean green (0) or
#          as a plain needs-human (2)/escalation (4), defeating §7.1. Cheap STRUCTURAL fast-fail complement
#          to the composition-root integration proof (guardrail 02): asserts ExitCodes.cs declares
#          ProceededUnreviewed = 5 (the next free value, NOT reusing 0/1/2/3/4) AND RunCommand.cs
#          references it. NOT sufficient alone (a grep cannot prove it is returned on the RIGHT condition —
#          the RunOutcomeWiringTests exit-5 fact in guardrail 02 proves that observably). Scoped to the two
#          files this task owns.
$exitcodes = "src/Guardrails.Cli/ExitCodes.cs"
$runcmd    = "src/Guardrails.Cli/Commands/RunCommand.cs"

if (-not (Test-Path $exitcodes)) { Write-Output "$exitcodes does not exist"; exit 1 }
if (-not (Test-Path $runcmd))    { Write-Output "$runcmd does not exist";    exit 1 }

$ec = Get-Content -Raw -Path $exitcodes
if ($ec -notmatch 'ProceededUnreviewed\s*=\s*5\b') {
    Write-Output "$exitcodes does not declare 'ProceededUnreviewed = 5' — add the distinct exit code (the next free value after Success=0/HarnessError=1/TaskFailed=2/Cancelled=3/EscalationsPending=4). Do NOT reuse an existing value: §7.1 requires a green-but-unreviewed run be distinguishable from green, needs-human, and an escalation halt."
    exit 1
}

$rc = Get-Content -Raw -Path $runcmd
if ($rc -notmatch 'ProceededUnreviewed') {
    Write-Output "$runcmd never references ExitCodes.ProceededUnreviewed — the constant is declared but RunCommand never RETURNS it, so a green-but-unreviewed run still exits 0/2. Map the RunReport unreviewed-wave signal to ExitCodes.ProceededUnreviewed."
    exit 1
}
exit 0
