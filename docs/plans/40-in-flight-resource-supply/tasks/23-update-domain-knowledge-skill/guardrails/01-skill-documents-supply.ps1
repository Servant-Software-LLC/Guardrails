# catches: the contract moving without the document that describes it. Each token below
#          measured ZERO occurrences in the subject at authoring time (#478) — the expected
#          answer for a required-present clause, so every one of them has teeth.
#          This is the UNENFORCED surface of the three (#600 covers the README, nothing
#          covers this one), which is why it gets its own check.
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

$subject = '.claude/skills/guardrails-domain-knowledge/SKILL.md'

if (-not (Test-Path -LiteralPath $subject)) {
    # PRECONDITION: every clause below would crash on a missing subject.
    Write-Output "PRECONDITION: $subject does not exist."
    exit 1
}

$raw = Get-Content -Raw -LiteralPath $subject

# An HTML comment RENDERS AS NOTHING, so a token that only appears inside one is invisible
# text and must not satisfy a required-present clause.
if ($raw -match '<!--(?![\s\S]*?-->)') {
    Write-Output "PRECONDITION: $subject contains an unterminated '<!--'. Refusing to strip to EOF, which would delete the rest of the document over one stray token."
    exit 1
}
$doc = [regex]::Replace($raw, '(?s)<!--.*?-->', '')

$failures = @()

if ($doc -notmatch [regex]::Escape('guardrails supply')) {
    $failures += "MISSING 'guardrails supply' in $subject — the verb is absent from the skill — the surface an AGENT reads, and the one nothing else tests"
}

if ($doc -notmatch [regex]::Escape('writeScope')) {
    $failures += "MISSING 'writeScope' in $subject — the caller-scoping rule is not stated, so an agent cannot know what it may supply"
}

if ($failures.Count -gt 0) {
    Write-Output "=== $($failures.Count) missing contract token(s) in $subject ==="
    $failures | ForEach-Object { Write-Output $_ }
    exit 1
}
exit 0
