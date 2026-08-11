# catches: the worktree-safety advisory still recommending commands the harness itself blocks or does
#          not grant - it ships in EVERY worktree prompt, so it is the widest instance of the defect.
$f = 'src/Guardrails.Core/Prompts/PromptComposer.cs'
if (-not (Test-Path $f)) { Write-Output "$f not found"; exit 1 }
$c = Get-Content -Raw -Path $f
$bad = @()
if ($c -match '/tmp/mine\.patch')  { $bad += "still writes /tmp/mine.patch - a redirect outside the worktree, blocked by the containment hook" }
if ($c -match 'git checkout -- ')  { $bad += "still recommends 'git checkout -- <files>' - ungranted on a clean box" }
if ($c -match 'git apply /tmp')    { $bad += "still recommends 'git apply /tmp/...' - ungranted, and the path is outside the worktree" }
if ($bad.Count -gt 0) { Write-Output ($bad -join '; '); exit 1 }
exit 0
