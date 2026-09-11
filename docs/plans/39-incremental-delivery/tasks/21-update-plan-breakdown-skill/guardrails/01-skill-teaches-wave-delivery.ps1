# catches: the contract moving without the document that describes it. Each token below measured
#          ZERO occurrences in the subject at authoring time (#478) — the expected answer for a
#          MEASURED: 'delivers' was already present 4x in .claude/skills/plan-breakdown/SKILL.md — green on
#          arrival and therefore toothless, hidden behind its siblings' failure. Replaced
#          with '"delivers": true', measured 0.
#          required-present clause, so every one has teeth.
#          This is the AUTHORING surface: a harness capability no skill teaches is a
#          capability no plan will ever use.
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

$subject = '.claude/skills/plan-breakdown/SKILL.md'

if (-not (Test-Path -LiteralPath $subject)) {
    Write-Output "PRECONDITION: $subject does not exist."
    exit 1
}

$raw = Get-Content -Raw -LiteralPath $subject

# An HTML comment RENDERS AS NOTHING, so a token only inside one is invisible text and must not
# satisfy a required-present clause. Fences are NOT stripped — a fence renders, so a token in a
# usage fence is legitimate house style.
if ($raw -match '<!--(?![\s\S]*?-->)') {
    Write-Output "PRECONDITION: $subject has an unterminated '<!--'. Refusing to strip to EOF."
    exit 1
}
$doc = [regex]::Replace($raw, '(?s)<!--.*?-->', '')

$failures = @()

if ($doc -notmatch [regex]::Escape('"delivers": true')) {
    $failures += "MISSING '`"delivers`": true' in $subject — the per-wave delivers flag is not taught, so no breakdown will ever emit one"
}

if ($doc -notmatch [regex]::Escape('GR2078')) {
    $failures += "MISSING 'GR2078' in $subject — the post-delivery entry-preflight warning is not taught"
}

if ($failures.Count -gt 0) {
    Write-Output "=== $($failures.Count) missing contract token(s) in $subject ==="
    $failures | ForEach-Object { Write-Output $_ }
    exit 1
}
exit 0
