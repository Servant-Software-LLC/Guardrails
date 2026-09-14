# catches: the contract moving without the document that describes it. Each token below measured
#          ZERO occurrences in the subject at authoring time (#478) — the expected answer for a
#          MEASURED: 'delivers' was already present 7x in docs/plans/02-schemas-and-contracts.md — green on
#          arrival and therefore toothless, hidden behind its siblings' failure. Replaced
#          with '"delivers"', measured 0.
#          MEASURED: 'covers' was already present 28x in docs/plans/02-schemas-and-contracts.md — green on
#          arrival and therefore toothless, hidden behind its siblings' failure. Replaced
#          with 'covers: [', measured 0 — but 'covers: [' can never match the JSON form the SSOT
#          records a run.json field in, so a correct edit stayed red (review 2026-09-13). Replaced
#          again with '"covers": [', measured 0.
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

if ($doc -notmatch [regex]::Escape('"covers": [')) {
    $failures += 'MISSING ''"covers": ['' in ' + $subject + ' — the delivery record''s covers list is not recorded in its JSON form, so a reader cannot tell what a merge carried'
}

if ($doc -notmatch [regex]::Escape('GR2079')) {
    $failures += "MISSING 'GR2079' in $subject — the second new diagnostic is not recorded. Design §5 said `"one, not four`" and §1c had already added the second; both codes belong in the registry section"
}

if ($doc -notmatch [regex]::Escape('refs/guardrails/trial/')) {
    $failures += "MISSING 'refs/guardrails/trial/' in $subject — the trial-merge ref is not recorded. §1 DECIDED that a delivering wave gates against a TRIAL MERGE on a scratch ref and only promotes on green; that changes §14.3's exit-gate contract and a reader cannot infer it"
}

# NEGATIVE: the registry ladder's stale "an unrelated new code should take GR2078". This plan TAKES
# GR2078 and GR2079, so that claim is now false and must name GR2080. Anchored on MEANING, not on
# formatting (#470, review W3): bold, backticks and line wraps are ignored, and "the next free code is
# GR2078" is the same claim in other words. MEASURED at 9598c1d7: exactly 1 match in the subject (the
# ladder sentence); 0 against a rewrite naming GR2080.
$staleNextCode = @(
    '(?i)\b(?:should|would|will|must)\s+(?:take|use|get|claim)\s+[*_`\s]*GR2078\b',
    '(?i)\bnext\s+free\s+(?:code\s+)?(?:is\s+)?[*_`\s]*GR2078\b'
)
foreach ($p in $staleNextCode) {
    if ($doc -match $p) {
        $failures += "STALE CLAIM STILL PRESENT in $subject — '$($Matches[0])': the registry ladder still says a new code should take GR2078. This plan TAKES GR2078 and GR2079, so the next free code is GR2080. A positive clause cannot catch this - only requiring the stale claim to be GONE proves the correction happened rather than being appended beside it"
        break
    }
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

# The pinned delivery record (design 39 §4) and the review-round-4 answers (2026-09-13). Each token below
# MEASURED 0 in the comment-stripped subject, with the strip above, on branch plan-breakdown/39-post-40-adjust
# at cb0a7857 before it was added here. ('"running"' and '"startedAt"' were rejected: each is already
# present 2x, so neither would have teeth.)
if ($doc -notmatch [regex]::Escape('"refused"')) {
    $failures += 'MISSING ''"refused"'' in ' + $subject + ' — the delivery record''s refused status is not recorded, so a reader cannot tell a refused delivery from one not reached'
}

if ($doc -notmatch [regex]::Escape('"suppressed"')) {
    $failures += 'MISSING ''"suppressed"'' in ' + $subject + ' — the delivery record''s suppressed status is not recorded, so a delivery the interlock held reads as missing'
}

if ($doc -notmatch [regex]::Escape('DeliveryRefused')) {
    $failures += 'MISSING ''DeliveryRefused'' in ' + $subject + ' — the refused-delivery halt kind is not recorded, so a refusal reads as a gate failure over a wave whose every check passed'
}

if ($doc -notmatch [regex]::Escape('partially-delivered')) {
    $failures += 'MISSING ''partially-delivered'' in ' + $subject + ' — the #542 delivery record''s outcome for a partly delivered run is not recorded, so an unattended consumer cannot tell held work from shipped work'
}

if ($doc -notmatch [regex]::Escape('any wave it carries')) {
    $failures += 'MISSING ''any wave it carries'' in ' + $subject + ' — the ride-along interlock rule is not recorded, so nothing says a clean wave''s delivery is held when it carries a held wave''s machine-decided commits'
}

if ($doc -notmatch [regex]::Escape('hook-checked')) {
    $failures += 'MISSING ''hook-checked'' in ' + $subject + ' — the trial merge commit running the user''s git hooks (#149) is not recorded, so nothing says the commit that lands on the user''s branch was hook-checked'
}

if ($failures.Count -gt 0) {
    Write-Output "=== $($failures.Count) missing contract token(s) in $subject ==="
    $failures | ForEach-Object { Write-Output $_ }
    exit 1
}
exit 0
