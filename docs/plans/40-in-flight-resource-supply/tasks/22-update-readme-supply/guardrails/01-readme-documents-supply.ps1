# catches: the contract moving without the document that describes it. Each token below
#          measured ZERO occurrences in the subject at authoring time (#478) — the expected
#          answer for a required-present clause, so every one of them has teeth.
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

$subject = 'README.md'

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
    $failures += "MISSING 'guardrails supply' in $subject — the verb is not shown as an invocation — an undocumented command is, for practical purposes, an unshipped one"
}

if ($doc -notmatch [regex]::Escape('next task boundary')) {
    $failures += "MISSING 'next task boundary' in $subject — the README does not explain WHICH boundary picks the file up, which is the one thing an operator cannot guess"
}

if ($doc -notmatch [regex]::Escape('run-start boundary')) {
    $failures += "MISSING 'run-start boundary' in $subject — the README names only the task boundary. The HALTED-run case is the one the feature exists for and the one an operator cannot guess: at the moment they have the file in hand there is no live run to have a task boundary"
}

if ($doc -notmatch [regex]::Escape('workspace-relative')) {
    $failures += "MISSING 'workspace-relative' in $subject — the README does not state that the staged layout IS the destination layout — there is no separate destination argument, and an operator who does not know that cannot predict where the file lands"
}

if ($doc -notmatch [regex]::Escape('code artifacts do not')) {
    $failures += "MISSING 'code artifacts do not' in $subject — the README does not name the asymmetry (plan-folder edits reach a running plan; code artifacts do not). That sentence is the charter's d40-asymmetry answer and belongs in the plan-folder section, not the CLI section"
}

if ($failures.Count -gt 0) {
    Write-Output "=== $($failures.Count) missing contract token(s) in $subject ==="
    $failures | ForEach-Object { Write-Output $_ }
    exit 1
}
exit 0
