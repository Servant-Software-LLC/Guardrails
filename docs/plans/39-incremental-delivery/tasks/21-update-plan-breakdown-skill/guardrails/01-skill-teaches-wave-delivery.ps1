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

# An HTML comment RENDERS AS NOTHING, so a token that only appears inside one is invisible text
# and must not satisfy a required-present clause (#468 operator 2).
#
# But a bare lazy `(?s)<!--.*?-->` is WRONG on any document that MENTIONS `<!--` in prose: the
# mention opens a match that runs to the next real `-->` anywhere later. MEASURED on
# .claude/skills/plan-breakdown/SKILL.md — whose line 636 documents this very idiom — the lazy
# form removes 169,334 bytes, 44.8% of the file, in ONE span, taking the Step 5 anchor with it.
# The old unterminated-'<!--' precondition reported HEALTHY throughout, because a '-->' does
# exist later: the guard was written, executed, and could not fire on the actual defect.
#
# Inside a fenced block or an inline code span, `<!--` is literal text a reader SEES — Markdown
# renders it — so it cannot be an opener. Mask those regions, strip, then UNMASK, which also
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

if ($doc -notmatch [regex]::Escape('delivers: true')) {
    $failures += "MISSING 'delivers: true' in $subject — the per-wave delivers flag is not taught, so no breakdown will ever emit one"
}

if ($doc -notmatch [regex]::Escape('GR2078')) {
    $failures += "MISSING 'GR2078' in $subject — the post-delivery entry-preflight warning is not taught"
}

if ($doc -notmatch [regex]::Escape('GR2079')) {
    $failures += "MISSING 'GR2079' in $subject — the delivering-wave-without-an-exit-gate warning is not taught, so an author learns about it from a validate run instead of from the skill that told them to wave"
}

if ($doc -notmatch [regex]::Escape('for parallelism')) {
    $failures += "MISSING 'for parallelism' in $subject — the DECIDED doctrine rewording is absent. d39-wave-doctrine is the only decision in this design that changes what plan-breakdown tells every future author"
}

if ($doc -notmatch [regex]::Escape('delivery point')) {
    $failures += "MISSING 'delivery point' in $subject — the Step 7 report does not name which waves are delivery points. §1b asks for it by name, and it is what makes a wave marked delivers-by-habit visible to its author"
}

if ($doc -match [regex]::Escape('**Do NOT wave a flat plan** —')) {
    $failures += "STALE TEXT STILL PRESENT: '**Do NOT wave a flat plan** —' in $subject — the OLD unqualified doctrine sentence is still there. The ruling REWORDS it to `"do not wave a flat plan FOR PARALLELISM`"; leaving the unqualified form standing beside the new text is exactly the append-instead-of-edit this clause exists to catch"
}

if ($failures.Count -gt 0) {
    Write-Output "=== $($failures.Count) missing contract token(s) in $subject ==="
    $failures | ForEach-Object { Write-Output $_ }
    exit 1
}
exit 0
