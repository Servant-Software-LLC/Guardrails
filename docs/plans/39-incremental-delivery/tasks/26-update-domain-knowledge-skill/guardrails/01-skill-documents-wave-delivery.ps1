# catches: the domain model moving without the document every AGENT reads. Each required token below
#          measured ZERO occurrences in the subject at authoring time (#478), with this script's own
#          strip logic, on branch plan-breakdown/39-post-40-adjust at c01bd60d — the expected answer
#          for a required-present clause, so every one has teeth.
#          MEASURED: 'wave-scoped' alone was already present 2x (it names wave-scoped RESET) — green
#          on arrival and toothless. Replaced with 'wave-scoped interlock', measured 0.
#          MEASURED: 'WaveDelivered' would also be satisfied by a mention of WaveDeliveredRecord, so the
#          observer clause uses 'IRunObserver.WaveDelivered'.
#          ROUND 4 (design 39 review, 2026-09-13), MEASURED at cb0a7857 with the same strip:
#          'DeliveryRefused' 0 and 'partially-delivered' 0; the ride-along, trial-merge-hooks and
#          superseded-interlock sentence clauses each 0 sentences. 'refused' (8x), 'held' (8x),
#          'carries' (16x), 'covers' (7x) and 'hook' (18x) are ALREADY present, so none is a token on its
#          own: each is read only inside a sentence clause that measured 0.
#          The NEGATIVE clauses are the part appending cannot satisfy. One refuses the model design 39
#          REJECTED (a refresh recorded as a supply, with a false Supplied-By: trailer); the other refuses
#          the interlock rule round 4 SUPERSEDED (a decision holds back only its own wave).
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

$subject = '.claude/skills/guardrails-domain-knowledge/SKILL.md'

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
# form removes 169,334 bytes, 44.8% of the file, in ONE span. The old unterminated-'<!--'
# precondition reported HEALTHY throughout, because a '-->' does exist later: the guard was
# written, executed, and could not fire on the actual defect. This subject is not affected today
# (measured: 0 bytes stripped), but the idiom is the same and the next prose mention would
# reproduce it exactly.
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
    $failures += "MISSING 'delivers: true' in $subject — the per-wave delivers flag (brief.md front matter) is not described, so an agent cannot tell a delivering wave from any other"
}

if ($doc -notmatch [regex]::Escape('waves.<dir>.delivered')) {
    $failures += "MISSING 'waves.<dir>.delivered' in $subject — the journal record of a per-wave delivery is not described, so a post-mortem reader does not know where to look for what shipped"
}

if ($doc -notmatch [regex]::Escape('IRunObserver.WaveDelivered')) {
    $failures += "MISSING 'IRunObserver.WaveDelivered' in $subject — the observer event is not described beside the other wave events"
}

if ($doc -notmatch [regex]::Escape('GR2078')) {
    $failures += "MISSING 'GR2078' in $subject — the missing-entry-preflight warning for a wave that follows a delivery point is not recorded"
}

if ($doc -notmatch [regex]::Escape('GR2079')) {
    $failures += "MISSING 'GR2079' in $subject — the delivering-wave-without-an-exit-gate warning is not recorded"
}

if ($doc -notmatch [regex]::Escape('wave-scoped interlock')) {
    $failures += "MISSING 'wave-scoped interlock' in $subject — the skill does not name the interlock that is checked per delivery against the waves that delivery carries, so an agent will reason from the old run-wide interlock"
}

if ($doc -notmatch [regex]::Escape('refreshed[]')) {
    $failures += "MISSING 'refreshed[]' in $subject — the refresh provenance section is not described, so content no task authored stays invisible to the reader of this skill"
}

if ($doc -notmatch [regex]::Escape('UnauthoredContentNote')) {
    $failures += "MISSING 'UnauthoredContentNote' in $subject — the single reader of supplied[] and refreshed[] is not named, which invites the next feature to read only one section"
}

if ($doc -notmatch [regex]::Escape('DeliveryRefused')) {
    $failures += "MISSING 'DeliveryRefused' in $subject — the skill does not say a refused wave delivery (branch-moved, conflict, dirty-working-tree, hook-rejected) halts the run at that wave under WaveHaltKind.DeliveryRefused, so an agent reading a barrier refusal looks for a gate failure or a run-level halt that is never written"
}

if ($doc -notmatch [regex]::Escape('partially-delivered')) {
    $failures += "MISSING 'partially-delivered' in $subject — run.json's delivery outcome for a run where some waves reached the user's branch and verified work is still held (delivered: false) is not described, so an agent reads delivered: false as 'nothing shipped'"
}

# NEGATIVE: the model design 39 §1c REJECTED — a refresh described as a supply. Anchored on MEANING
# (#470, review W3), not on one literal: a refresh given a `by` value, a Supplied-By trailer naming a
# refresh, a refresh recorded as or in supplied[], or a refresh commit carrying Supplied-By. An explicit
# negation in between ("not", "never", "rather than", "instead of", "unlike") is the CORRECT contrast and
# does not trip it, and neither does naming both sections side by side ("reads supplied[] and
# refreshed[]"). MEASURED at 9598c1d7: 0 matches in the subject.
# (The typographic apostrophe U+2019 is spliced in with [char]0x2019: PowerShell treats that literal
# character as a string delimiter, so it cannot appear inside a quoted pattern.)
$negationFree = '(?:(?!\bnot\b|\bnever\b|n[''' + [char]0x2019 + ']t\b|rather\s+than|instead\s+of|unlike)[^.])'
$rejectedModel = @(
    '(?i)\bby\s*[:=]\s*[`"'']?\s*refresh',
    '(?i)Supplied-By:\s*[`"'']?\s*refresh',
    ('(?i)\brefresh(?:es|ed)?\b' + $negationFree + '{0,24}?\b(?:recorded|stored|written|journaled|logged|appended|kept)\s+(?:as|in|into|to|under)\s+(?:an?\s+|the\s+)?[`"'']?supplied\b'),
    ('(?i)\brefresh(?:[''' + [char]0x2019 + ']s)?\s+(?:merge\s+)?commits?\b' + $negationFree + '{0,40}?Supplied-By')
)
foreach ($p in $rejectedModel) {
    if ($doc -match $p) {
        $failures += "REJECTED MODEL PRESENT in $subject — '$($Matches[0])': a refresh is described as a supply. Design 39 §1c records a refresh in its own refreshed[] section with a Refreshed-From: trailer; a refresh has no supplier, so a by value, a supplied[] entry or a Supplied-By trailer for it is a false statement. Say what a refresh IS, and make any contrast with a supply an explicit negation"
        break
    }
}

# SENTENCE CLAUSES (#470): each clause below asserts a MEANING within ONE sentence, never a bare word —
# every word it reads is already present in the subject on its own (see header).
$flat = $doc -replace '\s+', ' '
$sentences = [regex]::Split($flat, '(?<=[.!?])\s+')

# The interlock vocabulary. $carriedMeaning is the ride-along scope stated as MEANING: any wave, every
# (later) wave or delivery, the waves a delivery carries or covers, riding along, until run end. The bare
# label "ride-along" is deliberately absent — a label beside a false rule does not make the rule true.
# The superseded-rule clause below uses this BROAD form as its exemption, so a correct contrast is never
# refused.
$decisionWord = '(?i)(?:\bdecisions?\b|proceeded-best-guess|proceeded-unreviewed)'
$carriedMeaning = '(?i)(?:\bany\s+(?:of\s+the\s+)?waves?\b|\bevery\s+(?:later\s+|following\s+|subsequent\s+)?(?:waves?|deliver(?:y|ies))\b|\bevery\s+later\b|\blater\s+deliver(?:y|ies)\b|\ball\s+(?:the\s+)?waves\b|\bcarr(?:y|ies|ied|ying)\b|\bcovers\b|\brides?\s+along\b|\brode\s+along\b|\buntil\s+(?:the\s+)?run\s+end\b)'

# POSITIVE: the ride-along rule (d39-interlock-ride-along) is STATED — one sentence that names a decision,
# a delivery, holding it back, and the carried scope. It reads a NARROWER vocabulary than the exemption
# above. MEASURED at cb0a7857: the broad form (any hold word, a bare 'carrying') was ALREADY satisfied by
# one sentence of this subject — the wave-loop bullet's "Decision ... blocked ... a next-wave stub carrying
# a brief.md" — green on arrival and toothless. With the hold words limited to held / holds back /
# suppress / withheld, a delivery word required, and 'carry' only as "carries its work / the wave", it
# measures 0 sentences here and in README.md, and still accepts six independently worded statements.
$holdDelivery = '(?i)(?:\bheld\b|\bholds?\s+back\b|\bholding\s+back\b|\bsuppress(?:es|ed|ing)?\b|\bwithh(?:o|e)ld\b)'
$deliverWord = '(?i)\bdeliver(?:y|ies|s|ed|ing)?\b'
$carriedScope = '(?i)(?:\bany\s+(?:of\s+the\s+)?waves?\b|\bevery\s+(?:later\s+|following\s+|subsequent\s+)?(?:waves?|deliver(?:y|ies))\b|\bevery\s+later\b|\blater\s+deliver(?:y|ies)\b|\bwaves?\s+(?:it|the\s+delivery|that\s+delivery|a\s+delivery)\s+(?:carr(?:y|ies)|covers)\b|\bcarr(?:y|ies|ied|ying)\s+(?:its|the|that|a\s+held|every)\s+(?:held\s+)?(?:work|waves?|commits)\b|\bcovers\b|\brides?\s+along\b|\brode\s+along\b|\buntil\s+(?:the\s+)?run\s+end\b)'
$rideAlong = @($sentences | Where-Object { $_ -match $decisionWord -and $_ -match $holdDelivery -and $_ -match $deliverWord -and $_ -match $carriedScope })
if ($rideAlong.Count -eq 0) {
    $failures += "MISSING the ride-along rule in $subject — no sentence says a delivery is held when ANY wave it carries recorded a suppressing decision. A delivery carries every wave since the last one, so a decision recorded in a held non-delivering wave still reaches the user's branch unless every later delivery is held too. State it in one sentence, in terms of the waves the delivery carries"
}

# NEGATIVE: the interlock rule round 4 SUPERSEDED — a decision holds back its own wave ONLY, or a wave
# delivers only if no decision was recorded during THAT wave. Anchored on MEANING (#470), in two forms:
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
    $failures += "SUPERSEDED INTERLOCK RULE PRESENT in ${subject}: '$($superseded[0])' — design 39 round 4 (d39-interlock-ride-along) scopes the interlock to every wave a delivery carries: a delivery is held when ANY wave it carries recorded a suppressing decision, so once a wave is held every later delivery is held too. A rule that consults only the delivering wave lets a held wave's commits ride a later clean delivery onto the user's branch. Reword that sentence in terms of the waves the delivery carries"
}

# POSITIVE: the trial merge commit keeps the user's git hooks (d39-trial-delivery-primitive, #149). A
# promotion is a fast-forward, and a fast-forward runs no hook, so the trial merge commit is where
# hook-rejected can still happen for a waved plan. One sentence naming the trial and a hook; the
# lookahead keeps the outcome token 'hook-rejected' from standing in for the statement.
# MEASURED at cb0a7857: 0 sentences.
$trialHooks = @($sentences | Where-Object { $_ -match '(?i)\btrial\b' -and $_ -match '(?i)\bhooks?\b(?!-)' })
if ($trialHooks.Count -eq 0) {
    $failures += "MISSING the trial-merge hook rule in $subject — no sentence says the trial merge commit is created with the user's git hooks. A promotion is a fast-forward, which runs no hook, so an agent that does not know the trial merge keeps them will reason that hook-rejected cannot happen for a waved plan"
}

if ($failures.Count -gt 0) {
    Write-Output "=== $($failures.Count) contract clause(s) failed in $subject ==="
    $failures | ForEach-Object { Write-Output $_ }
    exit 1
}
exit 0
