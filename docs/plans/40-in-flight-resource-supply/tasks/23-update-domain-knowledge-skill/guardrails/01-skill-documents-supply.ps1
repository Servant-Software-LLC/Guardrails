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

# An HTML comment RENDERS AS NOTHING, so a token that only appears inside one is invisible text
# and must not satisfy a required-present clause (#468 operator 2).
#
# But a bare lazy `(?s)<!--.*?-->` is WRONG on any document that MENTIONS `<!--` in prose: the
# mention opens a match that runs to the next real `-->` anywhere later. MEASURED on
# .claude/skills/plan-breakdown/SKILL.md - whose line 636 documents this very idiom - the lazy
# form removes 169,334 bytes, 44.8% of the file, in ONE span. The old unterminated-'<!--'
# precondition reported HEALTHY throughout, because a '-->' does exist later: the guard was
# written, executed, and could not fire on the actual defect. This subject is not affected today
# (measured), but the idiom is the same and the next prose mention would reproduce it exactly.
#
# Inside a fenced block or an inline code span, `<!--` is literal text a reader SEES - Markdown
# renders it - so it cannot be an opener. Mask those regions, strip, then UNMASK, which also
# preserves fence content in $doc and so honours the counter-rule that a token documented in a
# usage fence legitimately satisfies a clause.
$maskOpen = [string][char]0x01
$maskClose = [string][char]0x02
$masked = [regex]::Replace($raw, '(?s)```.*?```|`[^`\r\n]*`', {
    param($m) $m.Value.Replace('<!--', $maskOpen).Replace('-->', $maskClose)
})

# Belt and braces: after masking, an opener with no closer really is unterminated.
if ($masked -match '<!--(?![\s\S]*?-->)') {
    Write-Output "PRECONDITION: $subject contains an unterminated '<!--' outside any code span. Refusing to strip to EOF, which would delete the rest of the document over one stray token."
    exit 1
}

$doc = ([regex]::Replace($masked, '(?s)<!--.*?-->', '')).Replace($maskOpen, '<!--').Replace($maskClose, '-->')

$failures = @()

if ($doc -notmatch [regex]::Escape('guardrails supply')) {
    $failures += "MISSING 'guardrails supply' in $subject — the verb is absent from the skill — the surface an AGENT reads, and the one nothing else tests"
}

if ($doc -notmatch [regex]::Escape('own writeScope')) {
    $failures += "MISSING 'own writeScope' in $subject — the caller-scoping rule is not stated, so an agent cannot know what it may supply"
}

if ($doc -notmatch [regex]::Escape('run-start boundary')) {
    $failures += "MISSING 'run-start boundary' in $subject — the skill names only one boundary. An agent that does not know about the run-start boundary cannot tell a supply that will be picked up from one that will not"
}

if ($doc -notmatch [regex]::Escape('code artifacts do not')) {
    $failures += "MISSING 'code artifacts do not' in $subject — the skill does not state the asymmetry as a contract. This is surface 2 of the three the charter's d40-asymmetry answer named, and the one nothing else tests"
}

if ($failures.Count -gt 0) {
    Write-Output "=== $($failures.Count) missing contract token(s) in $subject ==="
    $failures | ForEach-Object { Write-Output $_ }
    exit 1
}
exit 0
