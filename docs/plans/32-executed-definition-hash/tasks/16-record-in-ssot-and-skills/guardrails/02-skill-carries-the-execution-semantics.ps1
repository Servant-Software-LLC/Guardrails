# catches: the SSOT updated and the skill left describing the old behaviour. That matters more than it
#          sounds: guardrails-domain-knowledge is what an AGENT reads INSTEAD of the 6,900-line SSOT, so a
#          stale skill is not a documentation lag - it is the operative description for every future agent
#          working in this repo. An agent reading only this file must be able to recognise all three facts
#          without opening the SSOT.
#
#          Three specific losses, in the order they would hurt an agent:
#            1. the two LIVENESS CLASSES absent, so the next agent to reason about a mid-run edit has no
#               way to know that task.json is held from load while the action file is not - which is the
#               asymmetry the entire defect lives in;
#            2. the reads-recompute/writes-read-the-pin RULE absent, so an agent editing any of the
#               remaining eight call sites has no way to tell which side it is on. Section 4.3 records an
#               earlier draft of this plan getting that split wrong in three places;
#            3. the DELIVERY consequence absent, so an agent meeting an exit-2 divergence halt reads it as
#               an infrastructure fault. Exit 2 is actionable/needs-human, never 1.
#
# DOCUMENTATION TARGET - EXEMPT from the two-sided sample pair, and the exemption is NAMED rather than
#          taken silently (#468/#302): there is no meaningful INVALID sample of a skill document. The
#          mandatory substitute is the PRECEDENT check, and every token below has a sibling precedent in
#          this same file:
#            'definitionHash'          - 3 occurrences today; the field's own spelling, already in use here.
#            'definitionHashAtSettle'  - the same camelCase wire-key convention as its neighbour.
#            'DefinitionHashAtLoad'    - PascalCase because it names a C# member, matching how this file
#                                        already refers to harness types.
#          The prose clauses below are matched case-INSENSITIVELY and as PHRASES rather than sentences,
#          because this file has its own voice and section 14 item 8 asks for the facts, not for wording:
#          a clause that demanded a sentence would be asking the document to speak the plan's language
#          instead of its own, which is exactly what the harness contract tells every agent not to do.
#
# HTML COMMENTS ARE STRIPPED BEFORE MATCHING (#97/#98 inverted for a doc target): over SOURCE the
#          comment-blind failure is a false RED; over a DOCUMENT it is a false GREEN, because a clause
#          over a doc is almost always required-present. An HTML comment renders as NOTHING - invisible
#          text, not thin prose - so a '<!-- TODO: document the divergence gate here -->' would otherwise
#          discharge this entire check. FENCED CODE BLOCKS ARE NOT STRIPPED: a fence renders, and a token
#          documented inside a usage fence is legitimate house style.
#
# MEASURED BASELINES on design/32-executed-definition-hash @4a308ab, against
#          .claude/skills/guardrails-domain-knowledge/SKILL.md, with each clause's own case sensitivity
#          (#478):
#            DefinitionHashAtLoad            0   this stage's deliverable (case-SENSITIVE)
#            definitionHashAtSettle          0   this stage's deliverable (case-SENSITIVE)
#            held from load                  0   this stage's deliverable (case-INSENSITIVE: prose)
#            re-?read per attempt            0   this stage's deliverable (case-INSENSITIVE: prose)
#            reads recompute from disk       0   this stage's deliverable (case-INSENSITIVE: prose)
#            does not deliver | delivery is
#            blocked | blocks delivery       0   this stage's deliverable (case-INSENSITIVE: prose)
#          EVERY ROW IS ZERO, and that is worth stating rather than leaving implicit: this skill carries
#          three occurrences of 'definitionHash' today but says nothing at all about WHEN the value is
#          captured, which is the whole content of section 14 item 8. There are no retention clauses in
#          this file - nothing here is pre-satisfied, so every failure is a real gap.
$ErrorActionPreference = 'Continue'

$ws = $env:GUARDRAILS_WORKSPACE
if ([string]::IsNullOrEmpty($ws)) { $ws = (Get-Location).Path }

$rel  = '.claude/skills/guardrails-domain-knowledge/SKILL.md'
$full = Join-Path $ws $rel

# PRECONDITION - the one legitimate early exit.
if (-not (Test-Path -LiteralPath $full -PathType Leaf)) {
    Write-Output "PRECONDITION: $rel does not exist. It is the skill an agent reads instead of the SSOT, and this task updates it in place. If the harness write did not land, re-emit the needsHarnessWrite request - a Claude Code subprocess cannot write under .claude/ directly."
    exit 1
}

$raw = Get-Content -Raw -LiteralPath $full

# Strip HTML comments; FAIL on a residual unterminated opener rather than stripping to EOF, which would
# delete the rest of the document over one stray token and turn every clause below into a false red.
$doc = [regex]::Replace($raw, '(?s)<!--.*?-->', ' ')
if ($doc -match '<!--') {
    Write-Output "PRECONDITION: $rel contains an UNTERMINATED HTML comment opener. Everything after it renders as nothing, so no clause below can be trusted. Close the comment."
    exit 1
}

# ACCUMULATE (#478): one distinguishable message per clause, dumped once.
$failures = @()

# --- REQUIRED: the two new names, case-SENSITIVE (they are identifiers) ------------------------------
if ($doc -cnotmatch 'DefinitionHashAtLoad') {
    $failures += "$rel does not name DefinitionHashAtLoad. Section 14 item 8 asks for the reads-recompute/writes-read-the-pin rule, and the rule is unusable without the name of the thing the writes read. This is the member every WRITE site now stamps - the journal entry, the Guardrails-Task-Hash trailer, and via the wave fold the wave record."
}
if ($doc -cnotmatch 'definitionHashAtSettle') {
    $failures += "$rel does not name definitionHashAtSettle. It is the durable record of a divergence and the condition the drift-accept refusal keys on: a task whose journal entry carries it is by construction one that ran a definition it does not match. An agent that does not know the key exists cannot recognise the state."
}

# --- REQUIRED: the two liveness classes, as PHRASES rather than sentences -----------------------------
# Case-insensitive: this is prose in a document with its own voice.
if ($doc -notmatch '(?i)held\s+from\s+load') {
    $failures += "$rel does not say that task.json and the DAG are HELD FROM LOAD. That is half of the liveness asymmetry the whole defect lives in, and the half an agent gets wrong: a mid-run edit to task.json does NOT apply to the run in flight."
}
if ($doc -notmatch '(?i)re-?read\s+per\s+attempt') {
    $failures += "$rel does not say that the action file and the guardrail/preflight scripts are RE-READ PER ATTEMPT. That is the other half, and it is why a mid-run edit leaves the attempt verified under a MIXED definition corresponding to no version of the folder that ever existed on disk - for which no single hash is true."
}

# --- REQUIRED: the rule, and the delivery consequence -------------------------------------------------
if ($doc -notmatch '(?i)reads\s+recompute\s+from\s+disk') {
    $failures += "$rel does not carry the rule 'reads recompute from disk; writes read the pin'. It is the one sentence that tells a future agent which side of the split any given call site is on - and section 4.3 records an earlier draft of this plan misclassifying three sites, which is the mistake the rule exists to prevent."
}
if ($doc -notmatch '(?i)(does\s+not\s+deliver|delivery\s+is\s+blocked|blocks\s+delivery)') {
    $failures += "$rel does not state that a divergence BLOCKS DELIVERY. That is the observable behaviour change: the run still records succeeded with the pin - the settle is never refused, because refusing would discard paid work and leave an uncorroborated plan-branch commit - but nothing is merged, the run is not reported green, and the CLI exits 2 (actionable/needs-human, never 1)."
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== domain-knowledge skill: $($failures.Count) of section 14 item 8's facts are missing from $rel ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    Write-Output ""
    Write-Output "Affected sections only, and CITE the SSOT rather than restating it. Every clause above is matched as a phrase, not a sentence: write it in this file's own voice."
    exit 1
}
Write-Output "Domain-knowledge skill carries the semantics: both new names, both liveness classes, the reads-recompute rule, and the delivery consequence."
exit 0
