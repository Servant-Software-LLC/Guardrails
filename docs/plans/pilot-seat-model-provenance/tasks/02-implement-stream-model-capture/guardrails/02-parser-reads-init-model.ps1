# catches: a parser that satisfies the tests by an incidental path but never reads the CLI system/init model line - structural proof it references the system message type and the model field. Scoped to the one file this task owns.
$ws = $env:GUARDRAILS_WORKSPACE; if ([string]::IsNullOrEmpty($ws)) { $ws = (Get-Location).Path }
$path = Join-Path $ws 'src/Guardrails.Core/Prompts/ClaudeStreamParser.cs'
if (-not (Test-Path $path)) {
    Write-Output 'src/Guardrails.Core/Prompts/ClaudeStreamParser.cs does not exist.'
    exit 1
}
$content = Get-Content -Raw -Path $path
if (($content -notmatch 'system') -or ($content -notmatch 'model')) {
    Write-Output 'ClaudeStreamParser.cs must reference the system message type and the model field to read the CLI-echoed model from the init line.'
    exit 1
}
exit 0
