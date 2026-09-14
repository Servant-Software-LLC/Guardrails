# catches: the delivery contract moving without the operator-facing document that describes it.
#          Each required token below measured ZERO occurrences in README.md on branch
#          plan-breakdown/39-post-40-adjust at authoring time (#478), after this script's own
#          comment strip — the expected answer for a required-present clause, so every one has teeth.
#          The NEGATIVE clause at the end targets a claim plan 39 makes FALSE, and measured
#          PRESENT (1 sentence) at authoring time: it is the part appending text cannot satisfy.
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
# .claude/skills/plan-breakdown/SKILL.md — whose line 636 documents this very idiom — the lazy
# form removes 169,334 bytes, 44.8% of the file, in ONE span. The old unterminated-'<!--'
# precondition reported HEALTHY throughout, because a '-->' does exist later: the guard was
# written, executed, and could not fire on the actual defect.
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
    $failures += "MISSING 'delivers: true' in $subject — the per-wave opt-in is not shown as written, so an operator cannot turn the feature on"
}

if ($doc -notmatch [regex]::Escape('brief.md')) {
    $failures += "MISSING 'brief.md' in $subject — the README does not say WHERE the flag lives. There is no per-wave config file, and the obvious guess (a guardrails.json in the wave directory) is silently ignored: the plan stays waved and validate does not warn"
}

if ($doc -notmatch [regex]::Escape('delivery point')) {
    $failures += "MISSING 'delivery point' in $subject — the README does not name the concept that separates ordering from delivery: every wave is an ordering unit, only some are delivery points"
}

if ($doc -notmatch [regex]::Escape('wave-scoped')) {
    $failures += "MISSING 'wave-scoped' in $subject — the README does not say the delivery interlock now applies per wave: a wave delivers only if no suppressing machine decision was recorded during that wave"
}

if ($doc -notmatch [regex]::Escape('refreshed[]')) {
    $failures += "MISSING 'refreshed[]' in $subject — the post-delivery refresh admits content no task authored, and the README does not say where run.json records it"
}

if ($doc -notmatch [regex]::Escape('GR2078')) {
    $failures += "MISSING 'GR2078' in $subject — the warning for a wave that follows a delivery point with no entry preflight is not documented"
}

if ($doc -notmatch [regex]::Escape('GR2079')) {
    $failures += "MISSING 'GR2079' in $subject — the warning for a delivering wave with no exit gate is not documented, so an operator learns it only from a validate run"
}

# NEGATIVE: plan 39 makes the UNQUALIFIED claim false that a run which does not reach green leaves
# the user's branch untouched — a wave that delivered at its barrier has already landed when a later
# wave halts. Anchored on MEANING (#470, review W3), sentence by sentence, not on one wording. A
# sentence fails when it (1) says nothing is merged or the branch stays untouched, (2) is about a
# halt, a failed gate, a cancellation or a run that does not reach green, and (3) carries NO
# waved-plan qualifier (flat, waved, wave(s), earlier, already, unless, except, delivery point,
# delivers, barrier). Dropping "either way", reordering the halt list or removing the bold does not
# escape it; a rewrite that scopes the claim to flat plans, or says an earlier delivery stays, does.
# MEASURED at 9598c1d7: exactly 1 matching sentence in the subject (the one under "Delivery on
# success"); 0 against a correct flat/waved rewrite.
$flat = $doc -replace '\s+', ' '
$sentences = [regex]::Split($flat, '(?<=[.!?])\s+')
$saysUntouched = '(?i)(?:branch[^.]{0,40}?\b(?:untouched|unchanged|as\s+it\s+was)\b|\b(?:untouched|unchanged)\b[^.]{0,20}?branch|\bnothing\s+(?:is\s+)?merged\b|\bnot\s+merged\b|\bnever\s+merged\b)'
$aboutNotGreen = '(?i)(?:needs-human|failed\s+gate|\bhalts?\b|\bhalted\b|cancel|does\s+[*_`]*not[*_`]*\s+reach\s+green|not\s+green)'
$wavedQualifier = '(?i)(?:\bflat\b|\bwaved?\b|\bwaves\b|earlier|already|unless|except|delivery\s+point|delivers|barrier)'
$falseClaims = @($sentences | Where-Object { $_ -match $saysUntouched -and $_ -match $aboutNotGreen -and $_ -notmatch $wavedQualifier })
if ($falseClaims.Count -gt 0) {
    $failures += "UNQUALIFIED CLAIM STILL PRESENT in ${subject}: '$($falseClaims[0])' — per-wave delivery makes this false: a wave that delivered at its barrier has already landed when a later wave halts. Reword that sentence so it is true for flat AND waved plans; do not leave it standing beside a qualifier elsewhere"
}

if ($failures.Count -gt 0) {
    Write-Output "=== $($failures.Count) README contract failure(s) in $subject ==="
    $failures | ForEach-Object { Write-Output $_ }
    exit 1
}
exit 0
