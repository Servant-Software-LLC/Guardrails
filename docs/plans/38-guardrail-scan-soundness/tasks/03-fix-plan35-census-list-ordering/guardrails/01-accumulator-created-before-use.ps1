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
$PSNativeCommandUseErrorActionPreference = $false   # it runs `& git show`; a non-zero native exit is DATA here

$subject = if ($env:GR_SUBJECT) { $env:GR_SUBJECT } else { 'docs/plans/35-event-vocabulary/tasks/04-author-tests-event-vocabulary/guardrails/02-tests-fail-on-stubs.ps1' }
if (-not (Test-Path -LiteralPath $subject -PathType Leaf)) {
    Write-Output "PRECONDITION: $subject does not exist - the clause below would be vacuous."
    exit 1
}

$problems = 0
$raw     = Get-Content -Raw -LiteralPath $subject
$creation = $raw.IndexOf('$problems = New-Object System.Collections.Generic.List[string]')
$firstUse = $raw.IndexOf('$problems.Add(')

# The cheapest edit is DUPLICATE, not MOVE: insert a copy above the loop and delete nothing. Both the
# ordering check and the byte-equality check below pass - the latter strips ALL accumulator lines from
# both sides, so 1 copy and 2 reduce identically - while the SECOND `New-Object` re-creates $problems
# after the mustExecute loop has added to it, silently discarding the exempt-row findings. Require one.
$copies = @([regex]::Matches($raw, [regex]::Escape('$problems = New-Object System.Collections.Generic.List[string]'))).Count
if ($copies -gt 1) {
    Write-Output "$subject declares the accumulator $copies times. A DUPLICATE is not a MOVE: the second New-Object re-creates `$problems after the mustExecute loop has added to it, so the exempt-row findings are silently discarded - the same defect this task exists to remove, one line lower. Move the line; do not copy it."
    exit 1
}

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
# git show needs a REPO-RELATIVE path: an absolute one is not a valid "HEAD:<path>" spec. The subject
# IS absolute whenever the pre-DAG samples-verify gate runs this guardrail against its own sample pair,
# so passing it through unmodified made BOTH halves exit 1 and halted the first real run before task 1.
$headCopy = $null
$repoRoot = (& git rev-parse --show-toplevel 2>$null | Out-String).Trim()
if ($LASTEXITCODE -eq 0 -and $repoRoot) {
    $full = Resolve-Path -LiteralPath $subject -ErrorAction SilentlyContinue
    $rel  = if ($full) { $full.Path } else { $subject }
    $rel  = $rel.Replace('\', '/')      # single backslash: '\\' would match a DOUBLE one
    $root = $repoRoot.Replace('\', '/').TrimEnd('/') + '/'
    if ($rel.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) { $rel = $rel.Substring($root.Length) }
    $headCopy = & git show "HEAD:$rel" 2>$null | Out-String
    if ($LASTEXITCODE -ne 0) { $headCopy = $null }
}

if ([string]::IsNullOrWhiteSpace($headCopy)) {
    # D11: do NOT fail open. This is the strongest clause in the file; silently dropping it on any box
    # where git resolution differs would leave only the ordering check, which a full rewrite satisfies.
    Write-Output "PRECONDITION: could not read HEAD:$subject via git, so the no-other-edits check cannot run - and it is the only clause that distinguishes a one-line MOVE from a rewrite. This is a hard failure, not a skip: re-run where `git show` resolves, or the guardrail certifies far less than it claims."
    exit 1
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
