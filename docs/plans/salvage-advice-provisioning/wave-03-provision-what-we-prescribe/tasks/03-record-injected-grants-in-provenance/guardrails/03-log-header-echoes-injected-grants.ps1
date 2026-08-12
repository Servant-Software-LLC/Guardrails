# catches: deliverable 2 shipping as a no-op. The task's prompt demands BOTH channels - the provenance
#          record AND the attempt log header - but only the record was ever guarded, so a task that
#          extended JournalModel.cs and never touched TaskExecutor.cs went fully green.
# Comment-stripped and PascalCase-anchored for the same reason as guardrail 01: a TODO comment naming
# the grants must not satisfy a check that the echo is actually wired.
$f = 'src/Guardrails.Core/Execution/TaskExecutor.cs'
if (-not (Test-Path $f)) { Write-Output "$f not found"; exit 1 }
$c = Get-Content -Raw -Path $f

$stripped = [regex]::Replace($c, '(?s)/\*.*?\*/', ' ')
$stripped = [regex]::Replace($stripped, '(?m)//.*$', '')

if ($stripped -notmatch 'Injected\w*Grants') {
    Write-Output "$f never references the injected tool grants - the attempt log header does not echo them, so a human reading logs still cannot see the effective permission set without querying. A comment does not count."
    exit 1
}
exit 0
