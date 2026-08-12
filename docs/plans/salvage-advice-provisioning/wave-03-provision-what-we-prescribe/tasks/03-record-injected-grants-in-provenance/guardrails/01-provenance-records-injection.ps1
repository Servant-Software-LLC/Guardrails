# catches: injection landing with no audit trail - the effective permission set silently diverges from
#          the declared allowedTools and nothing records what the harness added.
# The previous form grepped the raw file for '(?i)injected', which a single COMMENT satisfied (proved by
# execution: a JournalModel.cs whose only content was '// TODO: record the injected grants' exited 0).
# This form strips comments first, requires PascalCase MEMBER identifiers for BOTH channels the prompt
# demands (what the HARNESS ADDED vs what the PLAN DECLARED), and anchors them to the AttemptProvenance
# declaration. It is deliberately agnostic to declaration form - a positional record parameter and a
# braced property both live inside the captured region, so neither is false-RED'd.
$f = 'src/Guardrails.Core/Journal/JournalModel.cs'
if (-not (Test-Path $f)) { Write-Output "$f not found"; exit 1 }
$c = Get-Content -Raw -Path $f

$stripped = [regex]::Replace($c, '(?s)/\*.*?\*/', ' ')
$stripped = [regex]::Replace($stripped, '(?m)//.*$', '')

$pattern = '(?s)(record|class)\s+AttemptProvenance\b(?<body>.*?)(?=\r?\n\s*(public|internal|file|sealed)\s+(sealed\s+)?(record|class)\s|\z)'
if ($stripped -notmatch $pattern) {
    Write-Output "could not locate an AttemptProvenance record/class declaration in $f"
    exit 1
}
$body = $Matches['body']

if ($body -notmatch 'Injected\w*Grants') {
    Write-Output "AttemptProvenance has no member recording the harness-INJECTED tool grants (expected a PascalCase member like InjectedToolGrants) - the effective permission set is unauditable. A comment naming 'injected' does not count."
    exit 1
}
if ($body -notmatch 'Declared\w*Grants') {
    Write-Output "AttemptProvenance records the injected grants but not the DECLARED ones - the prompt requires BOTH channels so a reader can tell what the harness added from what the plan declared."
    exit 1
}
exit 0
