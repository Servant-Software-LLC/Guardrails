# catches: the datum stopping one hop short of the journal - the #475 shape, where a schema member is
#          declared, is READ by a consumer, and is assigned by nothing. Wave 2 shipped that exact bug
#          14/14 green, which is why this wave gets a dedicated arrival check.
#
# TOKEN BASELINES ARE MEASURED, NOT ASSUMED (the lesson of this wave's own review). Against the
# integration tree as of authoring:
#     Judge            -> 0 occurrences in TaskExecutor.cs, 0 in GuardrailRunner.cs
#     JudgeResolution  -> 0 in both
# so every clause below starts RED and can only go green on new work. Note what the count is NOT: in
# Scheduler.cs the bare token `Judge` already appears 5 times (CriticalityJudge), which is precisely
# why the judge datum was placed where Scheduler.cs never needs editing - and why a clause keyed on
# `Judge` would have been pre-satisfied there.
#
# SOUND ABSENCE ONLY (#468). A failure here is conclusive; a pass is not proof the value is CORRECT.
# The sibling 02-conformance-suite-passes guardrail drives the real seam and reads the real journal.
$ErrorActionPreference = 'Continue'
$failures = @()

function Get-StrippedCode {
    param([string]$Path)
    $raw = Get-Content -Raw $Path
    # Comments stripped (#97/#98): a comment naming the member is not an assignment. Strings are NOT
    # stripped here - none of the clauses below can be satisfied by a string literal.
    $c = [regex]::Replace($raw, '/\*[\s\S]*?\*/', '')
    return [regex]::Replace($c, '(?m)//.*$', '')
}

$runner = 'src/Guardrails.Core/Execution/GuardrailRunner.cs'
$exec   = 'src/Guardrails.Core/Execution/TaskExecutor.cs'

foreach ($f in @($runner, $exec)) {
    if (-not (Test-Path $f)) {
        Write-Output "$f does not exist"
        exit 1
    }
}

# --- hop 1: GuardrailRunner EXPOSES the resolved judge on what it returns ------------------------
$runnerCode = Get-StrippedCode $runner
if ($runnerCode -cnotmatch 'Judge') {
    $failures += 'GuardrailRunner never names Judge in real code - task 07 was to expose the resolved judge on GuardrailRunResult, and this task cannot carry what is not exposed'
}

# --- hop 2: TaskExecutor FOLDS it onto the attempt provenance ------------------------------------
# The `with` expression is the fold. Keyed on an assignment to Judge INSIDE a with-block, so a stray
# local named judge, a parameter, or a mention cannot satisfy it. [\s\S] not . so the with-block may
# span lines; [^{}]* keeps it to the one initializer and stops it swallowing a whole method body.
$execCode = Get-StrippedCode $exec
if ($execCode -cnotmatch 'with\s*\{[^{}]*\bJudge\s*=') {
    $failures += 'TaskExecutor never folds Judge into a with-expression - the judge is resolved during guardrail evaluation, so it must be recorded onto the attempt provenance AFTER the guardrail call and BEFORE the journaller is called. Grep BuildProvenance(task, worktree, route) for where that object is built.'
}

# The fold must land on the PROVENANCE object specifically - that is the member that rides
# PendingAttempt and therefore reaches the worktree settle path as well as the serial one.
if ($execCode -cnotmatch '(?m)provenance\s*=\s*provenance') {
    $failures += 'TaskExecutor never reassigns the `provenance` local - a with-expression whose result is discarded changes nothing (records are immutable), and a judge folded onto a DIFFERENT object does not reach Scheduler.RecordSucceededSettle, where the record is built from pending.Provenance in worktree mode (the default). Assign the result back.'
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== judge provenance arrival: $($failures.Count) finding(s) ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    Write-Output ""
    Write-Output "Placement is SETTLED (DoR 12.4 / D32): the judge object hangs off AttemptProvenance, beside the actor's Tier/TierSource that wave 2 already put there. That is what makes BOTH record paths work with zero edits to Scheduler.cs, AttemptJournaler.cs or RunReport.cs - none of which are in this task's write scope. Needing one of them means the datum went on the wrong record."
    exit 1
}
exit 0
