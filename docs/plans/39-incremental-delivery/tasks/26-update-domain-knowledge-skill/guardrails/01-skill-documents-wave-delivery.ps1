# catches: the domain model moving without the document every AGENT reads. Each required token below
#          measured ZERO occurrences in the subject at authoring time (#478), with this script's own
#          strip logic, on branch plan-breakdown/39-post-40-adjust at c01bd60d — the expected answer
#          for a required-present clause, so every one has teeth.
#          MEASURED: 'wave-scoped' alone was already present 2x (it names wave-scoped RESET) — green
#          on arrival and toothless. Replaced with 'wave-scoped interlock', measured 0.
#          MEASURED: 'WaveDelivered' would also be satisfied by a mention of WaveDeliveredRecord, so the
#          observer clause uses 'IRunObserver.WaveDelivered'.
#          The NEGATIVE clause is the part appending cannot satisfy: it refuses the model design 39
#          REJECTED (a refresh recorded as a supply, with a false Supplied-By: trailer).
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
    $failures += "MISSING 'wave-scoped interlock' in $subject — the skill does not say a machine decision now holds back only its own wave, so an agent will reason from the old run-wide interlock"
}

if ($doc -notmatch [regex]::Escape('refreshed[]')) {
    $failures += "MISSING 'refreshed[]' in $subject — the refresh provenance section is not described, so content no task authored stays invisible to the reader of this skill"
}

if ($doc -notmatch [regex]::Escape('UnauthoredContentNote')) {
    $failures += "MISSING 'UnauthoredContentNote' in $subject — the single reader of supplied[] and refreshed[] is not named, which invites the next feature to read only one section"
}

if ($doc -match 'Supplied-By:\s*refresh') {
    $failures += "REJECTED MODEL PRESENT: 'Supplied-By: refresh' in $subject — design 39 §1c records a refresh in its own refreshed[] section with a Refreshed-From: trailer. A refresh has no supplier, so a Supplied-By: trailer on it is a false statement, and describing it that way teaches every agent the model the design rejected"
}

if ($failures.Count -gt 0) {
    Write-Output "=== $($failures.Count) contract clause(s) failed in $subject ==="
    $failures | ForEach-Object { Write-Output $_ }
    exit 1
}
exit 0
