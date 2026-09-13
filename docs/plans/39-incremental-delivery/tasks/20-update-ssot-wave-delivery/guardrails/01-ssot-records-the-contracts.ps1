# catches: the contract moving without the document that describes it. Each token below measured
#          ZERO occurrences in the subject at authoring time (#478) — the expected answer for a
#          MEASURED: 'delivers' was already present 7x in docs/plans/02-schemas-and-contracts.md — green on
#          arrival and therefore toothless, hidden behind its siblings' failure. Replaced
#          with '"delivers"', measured 0.
#          MEASURED: 'covers' was already present 28x in docs/plans/02-schemas-and-contracts.md — green on
#          arrival and therefore toothless, hidden behind its siblings' failure. Replaced
#          with 'covers: [', measured 0.
#          required-present clause, so every one has teeth.
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

$subject = 'docs/plans/02-schemas-and-contracts.md'

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
    $failures += "MISSING 'delivers: true' in $subject — the per-wave delivers flag is not recorded"
}

if ($doc -notmatch [regex]::Escape('WaveDelivered')) {
    $failures += "MISSING 'WaveDelivered' in $subject — the observer event is not recorded"
}

if ($doc -notmatch [regex]::Escape('covers: [')) {
    $failures += "MISSING 'covers: [' in $subject — the covers[] field is not recorded, so a reader cannot tell what a merge carried"
}

if ($doc -notmatch [regex]::Escape('GR2079')) {
    $failures += "MISSING 'GR2079' in $subject — the second new diagnostic is not recorded. Design §5 said `"one, not four`" and §1c had already added the second; both codes belong in the registry section"
}

if ($doc -notmatch [regex]::Escape('refs/guardrails/trial/')) {
    $failures += "MISSING 'refs/guardrails/trial/' in $subject — the trial-merge ref is not recorded. §1 DECIDED that a delivering wave gates against a TRIAL MERGE on a scratch ref and only promotes on green; that changes §14.3's exit-gate contract and a reader cannot infer it"
}

if ($doc -match [regex]::Escape('should take **`GR2078`**')) {
    $failures += "STALE TEXT STILL PRESENT: 'should take **`GR2078`**' in $subject — the registry ladder still says an unrelated new code should take GR2078. This plan TAKES GR2078 and GR2079, so that sentence is now false and must read GR2080. A positive clause cannot catch this - only requiring the stale text to be GONE proves the correction happened rather than being appended beside it"
}

# Post-plan-40 refinement (design 39 §1c, "How a refresh is recorded"). Each token below MEASURED 0 in the
# comment-stripped subject, with the strip above, on branch plan-breakdown/39-post-40-adjust (2026-09-13)
# before it was added here.
if ($doc -notmatch [regex]::Escape('"refreshed": [')) {
    $failures += 'MISSING ''"refreshed": ['' in ' + $subject + ' — the refreshed[] provenance section is not recorded in §7, so a reader cannot find what a refresh admitted into the tree'
}

if ($doc -notmatch [regex]::Escape('Refreshed-From:')) {
    $failures += 'MISSING ''Refreshed-From:'' in ' + $subject + ' — the refresh commit''s trailer is not recorded in §5.3'
}

if ($doc -notmatch [regex]::Escape('"deliveredWave"')) {
    $failures += 'MISSING ''"deliveredWave"'' in ' + $subject + ' — the refreshed[] record''s deliveredWave field is not recorded, so a reader cannot tell which delivery triggered a refresh'
}

if ($doc -notmatch [regex]::Escape('"upstream"')) {
    $failures += 'MISSING ''"upstream"'' in ' + $subject + ' — the refreshed[] record''s upstream field is not recorded, so a reader cannot tell which sha was merged'
}

if ($doc -notmatch [regex]::Escape('--no-ff')) {
    $failures += 'MISSING ''--no-ff'' in ' + $subject + ' — the refresh commit''s shape is not recorded in §5.3; without --no-ff and the plan tip as first parent, a later rewind can miss earlier task commits'
}

if ($doc -notmatch [regex]::Escape('UnauthoredContentNote')) {
    $failures += 'MISSING ''UnauthoredContentNote'' in ' + $subject + ' — the single-reader rule is not recorded, so nothing says which code may read supplied[] and refreshed[]'
}

if ($doc -notmatch [regex]::Escape('refresh from ''')) {
    $failures += 'MISSING ''refresh from '''' in ' + $subject + ' — the gate-halt disclosure is not recorded in §14.3'
}

if ($failures.Count -gt 0) {
    Write-Output "=== $($failures.Count) missing contract token(s) in $subject ==="
    $failures | ForEach-Object { Write-Output $_ }
    exit 1
}
exit 0
