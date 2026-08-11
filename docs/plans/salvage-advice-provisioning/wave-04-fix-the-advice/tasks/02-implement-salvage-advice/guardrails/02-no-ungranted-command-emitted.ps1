# catches: the advice still naming a command outside the injected grant - the exact #374 defect. The
#          ungranted 'git diff <taskBase>' inspection alternative is the one that survived PR #426.
$f = 'src/Guardrails.Core/Execution/RetryPolicy.cs'
if (-not (Test-Path $f)) { Write-Output "$f not found"; exit 1 }
$c = Get-Content -Raw -Path $f
$bad = @()
if ($c -match 'git diff <taskBase>') { $bad += "the ungranted 'git diff <taskBase>' inspection alternative is still emitted - git show --stat already covers inspection" }
foreach ($verb in @('git checkout "', 'git restore ', 'git reset ', 'git stash ')) {
    if ($c -match [regex]::Escape($verb)) { $bad += ("a runnable ungranted command is emitted: " + $verb) }
}
if ($bad.Count -gt 0) { Write-Output ($bad -join '; '); exit 1 }
exit 0
