# catches: an accumulator used before it is created - the #608 fail-open in its live form. On the branch
#          where the declared exemption fails, `$problems` is $null, `$null.Add(...)` throws, and under
#          ErrorActionPreference=Continue the abort skips the rest of the try (including the seven-name
#          mustFail census AND the guardrail's own `exit 0`), so the process exits 0 and the harness
#          records a PASS. The check is ORDER, not presence: both lines are present today.
# Source-shape, and it survives the #468 demotion gate for a stated reason: the subject is a committed
#          guardrail of a MERGED plan folder. No production code path executes it, so there is no runtime
#          behaviour a test could observe - the ordering is a structural fact about a text file. Sample
#          pair: samples/01-*.valid.ps1 / .invalid.ps1.
# Measured baseline (#478): on the starting tree the creation index is GREATER than the first-use index
#          (verified 2026-09-06 on master a3f3e977) - i.e. this clause is RED before the task runs.
$ErrorActionPreference = 'Stop'

$subject = if ($env:GR_SUBJECT) { $env:GR_SUBJECT } else { 'docs/plans/35-event-vocabulary/tasks/04-author-tests-event-vocabulary/guardrails/02-tests-fail-on-stubs.ps1' }
if (-not (Test-Path -LiteralPath $subject -PathType Leaf)) {
    Write-Output "PRECONDITION: $subject does not exist - the clause below would be vacuous."
    exit 1
}

$problems = 0
$raw     = Get-Content -Raw -LiteralPath $subject
$creation = $raw.IndexOf('$problems = New-Object System.Collections.Generic.List[string]')
$firstUse = $raw.IndexOf('$problems.Add(')

if ($creation -lt 0) {
    Write-Output "PRECONDITION: no '`$problems = New-Object System.Collections.Generic.List[string]' line in $subject. The accumulator was renamed or restructured; this guardrail binds to that spelling, so reconcile the two."
    exit 1
}
if ($firstUse -lt 0) {
    Write-Output "PRECONDITION: no '`$problems.Add(' call in $subject. The census no longer accumulates; this guardrail has nothing to order."
    exit 1
}

# The prompt says: move ONE line, change nothing else. An offset comparison alone is satisfied by a full
# rewrite of the census, which would silently discard the seven-name mustFail list this file exists for.
# So assert byte-equality against HEAD modulo the moved line: strip the accumulator line from both and
# require the remainder to be identical. Skipped (with a stated reason, not silently) when git cannot
# produce the HEAD copy - in a segment worktree that is a real condition, not a defect.
$accumulator = '$problems = New-Object System.Collections.Generic.List[string]'
$headCopy = $null
try { $headCopy = & git show "HEAD:$subject" 2>$null | Out-String } catch { $headCopy = $null }
if ($LASTEXITCODE -ne 0) { $headCopy = $null }

if ([string]::IsNullOrWhiteSpace($headCopy)) {
    Write-Output "NOTE: could not read HEAD:$subject via git, so the no-other-edits check is SKIPPED (the ordering check below still runs). This is expected when the file is newly added; it is not a pass for the rewrite case."
}
else {
    $strip = {
        param($text)
        ($text -split "`r?`n" | Where-Object { $_.Trim() -ne $accumulator }) -join "`n"
    }
    if ((& $strip $raw).TrimEnd() -ne (& $strip $headCopy).TrimEnd()) {
        $problems += 1
        Write-Output "$subject differs from HEAD by more than the moved accumulator line. This task's whole deliverable is to relocate ONE line: the census, its seven-name mustFail list and its messages are a committed artifact of a merged, green run and must survive byte-for-byte. Revert the extra edits."
    }
}

if ($creation -gt $firstUse) {
    Write-Output "the accumulator is created at offset $creation but first used at offset $firstUse - `$problems is `$null on that branch, so `$null.Add(...) throws, the abort skips the rest of the try INCLUDING the mustFail census and the guardrail's own exit, and the process exits 0 = a silent PASS (#608). Move the New-Object line above its first use."
    exit 1
}

if ($problems -gt 0) { exit 1 }
Write-Output "Accumulator created at offset $creation, first used at offset $firstUse - created before use, and nothing else in the file changed."
exit 0
