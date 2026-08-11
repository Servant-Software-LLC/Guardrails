# catches: the domain-knowledge skill was not updated to reflect the new model-provenance observability.
$ws = $env:GUARDRAILS_WORKSPACE; if ([string]::IsNullOrEmpty($ws)) { $ws = (Get-Location).Path }
$path = Join-Path $ws '.claude/skills/guardrails-domain-knowledge/SKILL.md'
if (-not (Test-Path $path)) {
    Write-Output '.claude/skills/guardrails-domain-knowledge/SKILL.md does not exist.'
    exit 1
}
$content = Get-Content -Raw -Path $path
if (($content -notmatch 'resolvedModel|resolved model')) {
    Write-Output 'guardrails-domain-knowledge SKILL.md does not mention the resolved model - update its provenance/observability note.'
    exit 1
}
exit 0
