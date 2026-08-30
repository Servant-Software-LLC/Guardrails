# catches: the HELPFUL failure - setting each site's CORRECT role instead of the uniform Action stub.
#          Guardrails 01/02/03 all pass over that tree: it compiles, Role is still required and
#          un-defaulted, and every fixture still sets Role. It looks like a better job than the one
#          asked for. It is a cross-task DEADLOCK.
#
#          Task 01 authors a per-test census that requires the four non-Action sites to be observed
#          FAILED against this stub. If the roles are already correct those tests PASS, task 01's
#          census reports "NOT RED", and task 01 cannot repair it - its writeScope is one test file and
#          it has no write access to src at all. The plan would then be stuck at task 01 with no task
#          able to fix the cause, which is exactly how plan 28's first run stranded 19 tasks behind a
#          scope boundary nobody could cross.
#
#          So the uniform stub is not a stylistic preference - it is the precondition of the next
#          task's red bar, and it needs a gate rather than a sentence in a prompt.
$ErrorActionPreference = 'Continue'

$sites = @(
    'src/Guardrails.Core/Execution/ActionRunner.cs',
    'src/Guardrails.Core/Execution/WaveBreakdownInvoker.cs',
    'src/Guardrails.Core/Execution/AiMergeResolver.cs',
    'src/Guardrails.Core/Execution/GuardrailRunner.cs',
    'src/Guardrails.Core/Execution/Overwatch.cs',
    'src/Guardrails.Core/Execution/NeedsHumanTriage.cs',
    'src/Guardrails.Core/Execution/CriticalityJudge.cs'
)

$missing = @()
$notStub = @()

foreach ($relative in $sites) {
    $path = Join-Path $env:GUARDRAILS_WORKSPACE $relative
    if (-not (Test-Path $path)) {
        $missing += $relative
        continue
    }

    $text = Get-Content $path -Raw

    # Must set Role at all...
    if ($text -notmatch 'Role\s*=\s*PromptRole\.') {
        $missing += "$relative (sets no Role at all)"
        continue
    }

    # ...and every Role it sets must be Action. Checked as "no non-Action value present" rather than
    # "an Action value present", because a file could carry both and the wrong one is what matters.
    if ($text -match 'Role\s*=\s*PromptRole\.(Guardrail|Advisory)') {
        $notStub += $relative
    }
}

if ($missing.Count -gt 0 -or $notStub.Count -gt 0) {
    Write-Output "=== The seven src sites are not the uniform Action stub ==="
    foreach ($m in $missing)  { Write-Output "  NO Role SET        : $m" }
    foreach ($n in $notStub)  { Write-Output "  ALREADY CORRECTED  : $n  (sets Guardrail or Advisory)" }
    Write-Output ""
    Write-Output "Every one of the seven src construction sites must be Role = PromptRole.Action at the END of THIS task -"
    Write-Output "including the four where Action is the WRONG answer (GuardrailRunner, Overwatch, NeedsHumanTriage, CriticalityJudge)."
    Write-Output "Assigning the correct roles is task 02's deliverable. Doing it here passes this task and then DEADLOCKS task 01,"
    Write-Output "whose tests must be observed FAILING against this stub and which has no write access to src to undo it."
    exit 1
}

Write-Output "All 7 src sites carry the uniform Role = PromptRole.Action stub; task 01's red bar is intact."
exit 0
