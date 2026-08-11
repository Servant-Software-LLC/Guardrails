# catches: the containment hook's own block message still recommending a recipe the same hook blocks -
#          the most self-defeating instance of the defect, present in BOTH the bash and PowerShell arms.
$f = 'src/Guardrails.Core/Prompts/WorktreeContainmentHook.cs'
if (-not (Test-Path $f)) { Write-Output "$f not found"; exit 1 }
$c = Get-Content -Raw -Path $f
$bad = @()
if ($c -match 'mine\.patch')      { $bad += "the block message still names mine.patch - the hook's own redirect check blocks writing it outside the worktree" }
if ($c -match 'git checkout -- ') { $bad += "the block message still recommends 'git checkout -- <files>' - ungranted on a clean box" }
if ($bad.Count -gt 0) { Write-Output ($bad -join '; '); exit 1 }
exit 0
