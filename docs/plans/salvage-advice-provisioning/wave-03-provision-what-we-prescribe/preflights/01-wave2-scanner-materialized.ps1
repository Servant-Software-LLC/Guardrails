# catches: wave 3 measuring an injection effect with no instrument - wave 2's Bash-refusal detection
#          did not land, so the refusal count this wave is supposed to drive to zero is unobservable.
$f = 'src/Guardrails.Core/Prompts/ClaudePermissionScanner.cs'
if (-not (Test-Path $f)) { Write-Output "$f is missing on the merged HEAD"; exit 1 }
$c = Get-Content -Raw -Path $f
if ($c -notmatch 'requires approval') {
    Write-Output "$f does not recognise the real refusal text ('requires approval') - wave 2's detection did not materialize"
    exit 1
}
exit 0
