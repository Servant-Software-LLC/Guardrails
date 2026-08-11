# catches: wave 4 rewriting the salvage advice to rely on a grant the harness does not actually inject
#          - wave 3's provisioning did not land, so the new advice would name an ungranted command again.
$f = 'src/Guardrails.Core/Prompts/ClaudePromptRunner.cs'
if (-not (Test-Path $f)) { Write-Output "$f is missing on the merged HEAD"; exit 1 }
if ((Get-Content -Raw -Path $f) -notmatch 'git show') {
    Write-Output "$f does not inject a 'git show' grant - wave 3's provisioning did not materialize, so wave 4 must not assume it"
    exit 1
}
exit 0
