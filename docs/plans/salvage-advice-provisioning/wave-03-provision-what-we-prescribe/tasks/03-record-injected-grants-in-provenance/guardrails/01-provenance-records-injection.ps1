# catches: injection landing with no audit trail - the effective permission set silently diverges from
#          the declared allowedTools and nothing records what the harness added.
$f = 'src/Guardrails.Core/Journal/JournalModel.cs'
if (-not (Test-Path $f)) { Write-Output "$f not found"; exit 1 }
if ((Get-Content -Raw -Path $f) -notmatch '(?i)injected') {
    Write-Output "$f has no field recording the harness-INJECTED tool grants - the effective permission set is unauditable"
    exit 1
}
exit 0
