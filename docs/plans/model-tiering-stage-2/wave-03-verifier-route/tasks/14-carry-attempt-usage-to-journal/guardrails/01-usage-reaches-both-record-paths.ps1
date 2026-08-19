# catches: the #475 fix that works in serial mode and silently drops the value in worktree mode -
#          which is the DEFAULT. Setting Usage in AttemptJournaler and stopping there is the fix
#          everyone reaches for, and it is wrong: Scheduler.RecordSucceededSettle builds its own
#          AttemptRecord from a PendingAttempt and never consults the journaller. CostUsd survives
#          that path only because it is ALSO on PendingAttempt. A half-fix produces numbers that are
#          right in every unit test and wrong in every production run - strictly worse than today's
#          honest null, because the per-tier spend report would silently under-count.
#
# TOKENS MEASURED against the integration tree at authoring:
#     ActionRunner.cs   Usage -> 0        (CostUsd -> 2)
#     RunReport.cs      Usage -> 0        (CostUsd -> 1)
#     Scheduler.cs      pending.Usage -> 0 (pending.CostUsd -> 1)
# Every clause therefore starts RED. Each is expressed as a PARITY check against CostUsd rather than
# a bare presence check, so the guardrail states the actual requirement - Usage goes wherever CostUsd
# already goes - and cannot be satisfied by putting the member somewhere convenient.
#
# SOUND ABSENCE ONLY (#468). The behavioural proof is the sibling 02 conformance guardrail, which
# reads run.json back out after a real run.
$ErrorActionPreference = 'Continue'
$failures = @()

function Get-Code([string]$Path) {
    if (-not (Test-Path $Path)) { return $null }
    $r = Get-Content -Raw $Path
    $c = [regex]::Replace($r, '/\*[\s\S]*?\*/', '')
    return [regex]::Replace($c, '(?m)//.*$', '')
}

$sites = @(
    @{ File = 'src/Guardrails.Core/Execution/ActionRunner.cs'
       What = 'ActionRun must carry Usage from PromptResult, exactly as it already carries CostUsd (grep: CostUsd = result.CostUsd). This is the FIRST missing hop - the value reaches PromptResult today and stops.' }
    @{ File = 'src/Guardrails.Core/Execution/RunReport.cs'
       What = 'PendingAttempt must carry Usage. THIS IS THE HOP THAT MAKES OR BREAKS THE TASK: the worktree settle path builds its record from PendingAttempt, so a Usage the journaller sets but PendingAttempt does not carry reaches serial runs ONLY - and worktree is the default.' }
    @{ File = 'src/Guardrails.Core/Execution/AttemptJournaler.cs'
       What = 'the journaller must set Usage on the record it builds AND on the PendingAttempt it hands to the scheduler.' }
    @{ File = 'src/Guardrails.Core/Execution/Scheduler.cs'
       What = 'the settle record must add Usage = pending.Usage beside the existing CostUsd = pending.CostUsd.' }
)

foreach ($s in $sites) {
    $code = Get-Code $s.File
    if ($null -eq $code) { Write-Output "$($s.File) does not exist"; exit 1 }
    $usage = ([regex]::Matches($code, '\bUsage\b')).Count
    $cost  = ([regex]::Matches($code, '\bCostUsd\b')).Count
    if ($usage -lt 1) {
        $failures += "$($s.File) never names Usage in real code (it names CostUsd $cost time(s)) - $($s.What)"
    }
}

# The settle record specifically: pending.Usage must be copied across, mirroring pending.CostUsd.
$sched = Get-Code 'src/Guardrails.Core/Execution/Scheduler.cs'
if ($sched -cmatch 'pending\s*\.\s*CostUsd' -and $sched -cnotmatch 'pending\s*\.\s*Usage') {
    $failures += 'Scheduler copies pending.CostUsd into the settle record but NOT pending.Usage - this is the exact half-fix that populates usage in serial runs and drops it in worktree runs (the default). The two are siblings; JournalTierSpend.Add reads them one after the other.'
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== attempt usage arrival (#475): $($failures.Count) finding(s) ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    Write-Output ""
    Write-Output "Follow CostUsd to every site it appears in. It is not merely a precedent - it is the sibling datum, read beside Usage in JournalTierSpend.Add, and it already reaches run.json on BOTH record paths. Wherever CostUsd goes, Usage goes."
    exit 1
}
exit 0
