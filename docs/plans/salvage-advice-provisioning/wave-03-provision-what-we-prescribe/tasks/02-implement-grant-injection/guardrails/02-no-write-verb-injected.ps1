# catches: a write verb sneaking into the injected grants - the decision of record is read-only ONLY,
#          and a git write grant is unnarrowable under a prefix glob.
$f = 'src/Guardrails.Core/Prompts/ClaudePromptRunner.cs'
if (-not (Test-Path $f)) { Write-Output "$f not found"; exit 1 }
$c = Get-Content -Raw -Path $f
$bad = @()
foreach ($verb in @('git restore', 'git checkout', 'git reset', 'git apply', 'git stash')) {
    if ($c -match ('Bash\(' + [regex]::Escape($verb))) { $bad += $verb }
}
if ($bad.Count -gt 0) {
    Write-Output ("a WRITE verb is being injected as a tool grant: " + ($bad -join ', ') + " - only Bash(git show*) may be injected")
    exit 1
}
if ($c -notmatch 'git show') {
    Write-Output "$f does not inject a 'git show' grant - the retry protocol still depends on the plan author granting it"
    exit 1
}
exit 0
