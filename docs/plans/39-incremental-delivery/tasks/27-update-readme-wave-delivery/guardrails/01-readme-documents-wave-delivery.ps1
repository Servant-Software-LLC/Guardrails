# catches: the delivery contract moving without the operator-facing document that describes it.
#          Each required token below measured ZERO occurrences in README.md on branch
#          plan-breakdown/39-post-40-adjust at authoring time (#478), after this script's own
#          comment strip — the expected answer for a required-present clause, so every one has teeth.
#          The FIRST negative clause targets a claim plan 39 makes FALSE, and measured PRESENT
#          (1 sentence) at authoring time: it is the part appending text cannot satisfy.
#          ROUND 4 (design 39 review, 2026-09-13), MEASURED at cb0a7857 with the same strip:
#          'partially-delivered' 0; the refused-delivery-halt, merge-commit-hooks, ride-along and
#          superseded-interlock sentence clauses each 0 sentences ('refused' and 'hook' are 0x on their
#          own). 'held' (1x) and 'carries' (2x) are ALREADY present, so each is read only inside a
#          sentence clause, never as a token.
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
    $failures += "MISSING 'wave-scoped' in $subject — the README does not say the delivery interlock now applies per delivery point: a delivery is held when any wave it carries recorded a suppressing machine decision"
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

if ($doc -notmatch [regex]::Escape('partially-delivered')) {
    $failures += "MISSING 'partially-delivered' in $subject — run.json's delivery record for a run where an earlier wave landed and later work is held is not documented, so an operator, or a script keyed on delivered: false, reads it as 'nothing shipped'"
}

# SENTENCE CLAUSES (#470): each clause below asserts a MEANING within ONE sentence rather than a bare
# word, because every word it reads is either already present or trivially appended.
$flat = $doc -replace '\s+', ' '
$sentences = [regex]::Split($flat, '(?<=[.!?])\s+')

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
$saysUntouched = '(?i)(?:branch[^.]{0,40}?\b(?:untouched|unchanged|as\s+it\s+was)\b|\b(?:untouched|unchanged)\b[^.]{0,20}?branch|\bnothing\s+(?:is\s+)?merged\b|\bnot\s+merged\b|\bnever\s+merged\b)'
$aboutNotGreen = '(?i)(?:needs-human|failed\s+gate|\bhalts?\b|\bhalted\b|cancel|does\s+[*_`]*not[*_`]*\s+reach\s+green|not\s+green)'
$wavedQualifier = '(?i)(?:\bflat\b|\bwaved?\b|\bwaves\b|earlier|already|unless|except|delivery\s+point|delivers|barrier)'
$falseClaims = @($sentences | Where-Object { $_ -match $saysUntouched -and $_ -match $aboutNotGreen -and $_ -notmatch $wavedQualifier })
if ($falseClaims.Count -gt 0) {
    $failures += "UNQUALIFIED CLAIM STILL PRESENT in ${subject}: '$($falseClaims[0])' — per-wave delivery makes this false: a wave that delivered at its barrier has already landed when a later wave halts. Reword that sentence so it is true for flat AND waved plans; do not leave it standing beside a qualifier elsewhere"
}

# The interlock vocabulary. $carriedMeaning is the ride-along scope stated as MEANING: any wave, every
# (later) wave or delivery, the waves a delivery carries or covers, riding along, until run end. The bare
# label "ride-along" is deliberately absent — a label beside a false rule does not make the rule true.
# The superseded-rule clause below uses this BROAD form as its exemption, so a correct contrast is never
# refused.
$decisionWord = '(?i)(?:\bdecisions?\b|proceeded-best-guess|proceeded-unreviewed)'
$carriedMeaning = '(?i)(?:\bany\s+(?:of\s+the\s+)?waves?\b|\bevery\s+(?:later\s+|following\s+|subsequent\s+)?(?:waves?|deliver(?:y|ies))\b|\bevery\s+later\b|\blater\s+deliver(?:y|ies)\b|\ball\s+(?:the\s+)?waves\b|\bcarr(?:y|ies|ied|ying)\b|\bcovers\b|\brides?\s+along\b|\brode\s+along\b|\buntil\s+(?:the\s+)?run\s+end\b)'

# POSITIVE: the ride-along rule (d39-interlock-ride-along) is STATED — one sentence that names a decision,
# a delivery, holding it back, and the carried scope. It reads a NARROWER vocabulary than the exemption
# above, shared with task 26's guard: the broad form (any hold word, a bare 'carrying') measured 0 here
# but was ALREADY satisfied by a sentence of the domain skill at cb0a7857. MEASURED at cb0a7857 in this
# subject: 0 sentences.
$holdDelivery = '(?i)(?:\bheld\b|\bholds?\s+back\b|\bholding\s+back\b|\bsuppress(?:es|ed|ing)?\b|\bwithh(?:o|e)ld\b)'
$deliverWord = '(?i)\bdeliver(?:y|ies|s|ed|ing)?\b'
$carriedScope = '(?i)(?:\bany\s+(?:of\s+the\s+)?waves?\b|\bevery\s+(?:later\s+|following\s+|subsequent\s+)?(?:waves?|deliver(?:y|ies))\b|\bevery\s+later\b|\blater\s+deliver(?:y|ies)\b|\bwaves?\s+(?:it|the\s+delivery|that\s+delivery|a\s+delivery)\s+(?:carr(?:y|ies)|covers)\b|\bcarr(?:y|ies|ied|ying)\s+(?:its|the|that|a\s+held|every)\s+(?:held\s+)?(?:work|waves?|commits)\b|\bcovers\b|\brides?\s+along\b|\brode\s+along\b|\buntil\s+(?:the\s+)?run\s+end\b)'
$rideAlong = @($sentences | Where-Object { $_ -match $decisionWord -and $_ -match $holdDelivery -and $_ -match $deliverWord -and $_ -match $carriedScope })
if ($rideAlong.Count -eq 0) {
    $failures += "MISSING the ride-along rule in $subject — no sentence says a delivery is held when ANY wave it carries recorded a suppressing decision. A delivery carries every wave since the last one, so an operator told only about the delivering wave expects a held wave's commits to stay off their branch when a later clean wave delivers. State it in one sentence, in terms of the waves the delivery carries"
}

# NEGATIVE: the interlock rule round 4 SUPERSEDED — a wave delivers only if no decision was recorded
# during THAT wave, or a decision holds back its own wave ONLY. Anchored on MEANING (#470), in two forms:
# 'its own wave' with only / alone / solely within 40 characters, or a one-wave scope (during / in /
# within / from that wave) in a sentence that says 'only'. Either form fails only in a sentence that also
# names a decision and holding, suppressing or delivering, and states NO carried scope. An explicit
# negation of the narrow scope ("does not hold back only its own wave") is the correct contrast and does
# not trip it. MEASURED at cb0a7857: 0 sentences in the subject.
$ownWaveOnly = '(?i)(?:\b(?:only|solely|exclusively)\b[^.]{0,40}?\b(?:its|their)\s+own\s+waves?\b|\b(?:its|their)\s+own\s+waves?\b[^.]{0,40}?\b(?:only|alone|solely|exclusively)\b)'
$oneWaveScope = '(?i)\b(?:during|in|within|from)\s+(?:that|this|the\s+same|the\s+delivering|its)\s+wave\b'
$scopeTouch = '(?i)(?:\bheld\b|\bholds?\b|\bholding\b|\bsuppress|\bdeliver|\binterlock\b|\bwithh(?:o|e)ld\b|\bblock)'
$narrowTarget = '(?:\b(?:only|solely|alone|exclusively)\b|\bown\s+wave\b|\b(?:that|this|the\s+same|the\s+delivering)\s+wave\b)'
$negatedScope = '(?i)(?:\b(?:not|never|no\s+longer|nor)\b|n[''' + [char]0x2019 + ']t\b)[^.]{0,30}?' + $narrowTarget
$superseded = @($sentences | Where-Object {
    ($_ -match $ownWaveOnly -or ($_ -match $oneWaveScope -and $_ -match '(?i)\bonly\b')) -and
    $_ -match $decisionWord -and $_ -match $scopeTouch -and
    $_ -notmatch $carriedMeaning -and $_ -notmatch $negatedScope
})
if ($superseded.Count -gt 0) {
    $failures += "SUPERSEDED INTERLOCK RULE PRESENT in ${subject}: '$($superseded[0])' — design 39 round 4 (d39-interlock-ride-along) scopes the interlock to every wave a delivery carries: a delivery is held when ANY wave it carries recorded a suppressing decision, so once a wave is held every later delivery is held too. A rule that consults only the delivering wave tells the operator a held wave's commits stay off their branch when a later clean delivery carries them there. Reword that sentence in terms of the waves the delivery carries"
}

# POSITIVE: a refused wave delivery HALTS the run at that wave (WaveHaltKind.DeliveryRefused). One
# sentence naming a refusal, a halt and a wave. MEASURED at cb0a7857: 0 sentences.
$refusedHalt = @($sentences | Where-Object { $_ -match '(?i)\brefus(?:e|es|ed|al|ing)\b' -and $_ -match '(?i)\bhalt(?:s|ed|ing)?\b' -and $_ -match '(?i)\bwaves?\b' })
if ($refusedHalt.Count -eq 0) {
    $failures += "MISSING the refused-delivery halt in $subject — no sentence says a refused delivery (your branch moved, a conflict, a dirty working tree, a rejecting hook) halts the run at that wave. An operator who does not know that reads the halt as a failed gate, and does not know that resuming re-attempts the delivery"
}

# POSITIVE: the merge commit a delivery creates runs the operator's git hooks (#149, kept for the trial
# merge in round 4). The lookahead keeps the outcome token 'hook-rejected' in a refusal list from
# standing in for the statement. MEASURED at cb0a7857: 0 sentences.
$mergeHooks = @($sentences | Where-Object { $_ -match '(?i)\bhooks?\b(?!-)' -and $_ -match '(?i)(?:\bmerge\b|\bcommits?\b|\bdeliver)' })
if ($mergeHooks.Count -eq 0) {
    $failures += "MISSING the merge-commit hook rule in $subject — no sentence says the merge commit a delivery creates runs your git hooks. An operator who does not know that cannot tell why a delivery was refused as hook-rejected, or that a waved plan still runs their hooks"
}

# Review of 1a809bce (2026-09-13), both lenses. MEASURED at 1a809bce with the same strip: 'delivery-refused'
# 0; the switches, advanced-branch and hooks-path sentence clauses each 0 sentences. 'no-merge-on-success'
# is ALREADY present 6x, and a sentence naming it beside 'wave' already exists in the CLI reference row, so
# the switches clause reads only barrier / delivery point / every or each delivery. The README never
# describes serial mode, so there is no serial clause.
if ($doc -notmatch [regex]::Escape('delivery-refused')) {
    $failures += "MISSING 'delivery-refused' in $subject — the README does not say where run.json records a refused delivery, so an operator whose log site shows a needs-human wave with no cause has nowhere to look"
}

if ($doc -notmatch [regex]::Escape('trial-gate-failed')) {
    $failures += "MISSING 'trial-gate-failed' in $subject — the refusal recorded when a wave's exit gate fails on the trial merge with your new commits is not documented (measured 0 at 1a809bce), so an operator reading run.json cannot tell it from a failure of the wave's own work"
}

$switches = @($sentences | Where-Object { $_ -match '(?i)no-merge-on-success' -and $_ -match '(?i)\bbarrier\b|delivery\s+points?|every\s+deliver|each\s+deliver' })
if ($switches.Count -eq 0) {
    $failures += "MISSING the delivery-point switch in $subject — no sentence says --no-merge-on-success also stops a delivery point from delivering. An operator who uses it to inspect before anything lands would otherwise expect a waved plan to hold every wave"
}

$advanced = @($sentences | Where-Object { $_ -match '(?i)\badvanc(?:e|es|ed|ing)\b|new\s+commits|gained\s+commits|\bcommitted\b|kept\s+working' -and $_ -match '(?i)\btrial\b' })
if ($advanced.Count -eq 0) {
    $failures += "MISSING the advanced-branch refusal in $subject — no sentence says a delivery is refused when your branch gained commits after the trial merge was built. That cause needs only a resume, while a switched checkout needs your branch checked out again first"
}

$hooksPath = @($sentences | Where-Object { $_ -match '(?i)hooksPath|husky' -and $_ -match '(?i)\bhooks?\b' })
if ($hooksPath.Count -eq 0) {
    $failures += "MISSING the relative hooks path in $subject — no sentence says hooks under a relative core.hooksPath (husky's layout) run on the delivery's merge commit, so a husky user has no reason to expect their hooks to gate a waved delivery"
}

# NEGATIVE: a branch-named trial-gate-failed range (verification of the review fixes, 2026-09-14). The README
# gives the operator a range to run; it must be the SHA-keyed git log <plan-tip-sha>..<your-tip-sha>, never one
# keyed on the plan branch's NAME, which lists different commits once either branch moves. Read as a `git log`
# range starting at a plan-branch placeholder, or a plan-branch placeholder ranging to the user's or your tip.
# MEASURED on master with the strip above: 0 matches.
$branchRange = '(?i)(?:git\s+log\s+[`"'']?<?\s*plan[-_ ]?branch\s*>?\s*\.\.|<?\bplan[-_ ]?branch\s*>?\s*\.\.\s*<?\s*(?:user|your))'
if ($doc -match $branchRange) {
    $failures += "BRANCH-NAMED RANGE PRESENT in ${subject}: '$($Matches[0])' — the range that shows which of your commits a failed trial gate merged is the sha-keyed git log <plan-tip-sha>..<your-tip-sha>. A range keyed on the plan branch's name shows different commits once either branch moves"
}

if ($failures.Count -gt 0) {
    Write-Output "=== $($failures.Count) README contract failure(s) in $subject ==="
    $failures | ForEach-Object { Write-Output $_ }
    exit 1
}
exit 0
