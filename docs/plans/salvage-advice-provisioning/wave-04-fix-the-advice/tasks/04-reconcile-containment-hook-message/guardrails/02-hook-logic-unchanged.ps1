# catches: a message-only task quietly changing the hook's BLOCKING logic - the containment guarantee
#          must survive a wording fix untouched.
$f = 'src/Guardrails.Core/Prompts/WorktreeContainmentHook.cs'
if (-not (Test-Path $f)) { Write-Output "$f not found"; exit 1 }
$c = Get-Content -Raw -Path $f
$missing = @()
foreach ($marker in @('git stash', 'git worktree add')) {
    if ($c -notmatch [regex]::Escape($marker)) { $missing += $marker }
}
if ($missing.Count -gt 0) {
    Write-Output ("the hook no longer matches these blocked operations: " + ($missing -join ', ') + " - this task may change MESSAGE TEXT only, never the blocking logic")
    exit 1
}
exit 0
