# catches: the datum landing in ONE of the two AttemptRecord construction paths. Miss the Scheduler
#          one and judge provenance appears in serial runs and silently VANISHES in worktree runs -
#          which is the default mode, so the field would look implemented and be empty in practice.
#          This is the #475 shape: a schema member nothing populates, one hop short of the journal.
#
# SOUND ABSENCE ONLY (#468): a file that never names the symbol cannot be carrying it. Presence is
# not proof - the sibling conformance guardrail drives the real seam.
$ErrorActionPreference = 'Continue'
$failures = @()

function Read-Code([string]$path) {
    if (-not (Test-Path $path)) { return $null }
    $t = Get-Content -Raw $path
    $t = [regex]::Replace($t, '/\*[\s\S]*?\*/', '')
    return [regex]::Replace($t, '(?m)//.*$', '')     # comment-blind probes are the #97/#98 defect
}

# Every file on the traced path: GuardrailRunner -> TaskExecutor -> {AttemptJournaler, Scheduler}.
# The sibling datum FailedGuardrails already makes this exact trip; these are the same surfaces.
$hops = @(
    @{ Path = 'src/Guardrails.Core/Execution/GuardrailRunner.cs';  What = 'produces the judge datum and puts it on the result it returns' }
    @{ Path = 'src/Guardrails.Core/Execution/TaskExecutor.cs';     What = 'carries it from the guardrail result toward the journaler' }
    @{ Path = 'src/Guardrails.Core/Execution/AttemptJournaler.cs'; What = 'writes it onto the AttemptRecord (the SERIAL path)' }
    @{ Path = 'src/Guardrails.Core/Execution/Scheduler.cs';        What = 'writes it in RecordSucceededSettle, which builds its OWN AttemptRecord from a PendingAttempt and bypasses AttemptJournaler entirely (the WORKTREE path - the default)' }
)

foreach ($hop in $hops) {
    $code = Read-Code $hop.Path
    if ($null -eq $code) {
        $failures += "$($hop.Path) does not exist"
    }
    elseif ($code -cnotmatch 'Judge') {
        $failures += "[$($hop.Path)] never mentions Judge in real code - this hop $($hop.What). A gap at ANY hop leaves the field null in run.json"
    }
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== judge provenance carry: $($failures.Count) finding(s) ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    Write-Output ""
    Write-Output "Grep FailedGuardrails across src/Guardrails.Core/Execution/ - it is the sibling datum that already makes this exact trip, and its call sites are the ones yours belongs beside."
    exit 1
}
exit 0
