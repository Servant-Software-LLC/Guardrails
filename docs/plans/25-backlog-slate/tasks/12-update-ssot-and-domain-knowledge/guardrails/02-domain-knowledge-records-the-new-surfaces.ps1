# catches: an SSOT updated and the domain-knowledge skill left stale - the two halves of the same
#          obligation delivered apart. Every agent that works in this repo loads
#          guardrails-domain-knowledge; the SSOT is what it points AT. A skill that does not mention
#          the new verb, the barrier wait or the model surface means the next breakdown/review agent
#          reasons about a harness that no longer matches reality - and in this case it is worse than
#          silence: the skill's model-tiering section currently ASSERTS that the model pair "reaches
#          the live table and the --no-ui stream", a claim #524 measured to be false of anything that
#          persists. The skill's own frontmatter SELF-UPDATING clause is the standing instruction this
#          check enforces.
#
# DOCUMENTATION target - EXEMPT from the committed .valid/.invalid sample pair (#468/#302). The
# PRECEDENT check is the mandatory substitute and is applied per clause below: each demanded token has
# a sibling precedent in this exact file, so the task is asked for the form the skill already uses.
#
# As in guardrail 01, the sample-verify PREFLIGHT STEP is deliberately not token-checked: every
# candidate token is either ambient here or a precedent-free coinage. The verb clause is its proxy.
#
# baseline counts on the untouched tree - MEASURED with Select-String over this exact subject, with
# this clause's own case sensitivity (-cnotmatch -> -CaseSensitive), not assumed:
#   guardrails samples verify                                        0
#   (?:nextProbe|probeInterval)                                      0   (both alternatives measured 0 separately)
#   (?:[Mm]odel column|model per task|per-task model)                0   (all four alternatives measured 0 separately)
#   NOTE the two tokens NOT used here because they are already present and would be pre-satisfied
#   (#478): 'attempt-route.log' (1x) and 'barrier' (6x). Neither can carry a required-present clause.
#   No ancestor task's prompt or writeScope writes the demanded tokens into this subject - this task is
#   the only one in plan 25 whose writeScope names this file.
$f = if ($env:GR_SUBJECT) { $env:GR_SUBJECT } else { ".claude/skills/guardrails-domain-knowledge/SKILL.md" }

# PRECONDITION - the only early exit: every clause below would crash on a missing subject.
if (-not (Test-Path $f)) {
    Write-Output "$f does not exist - the domain-knowledge skill is what every agent in this repo loads, and its SELF-UPDATING clause makes this update part of the same change-set"
    exit 1
}

$doc = Get-Content $f -Raw
$failures = @()

# PRECEDENT: this skill already names CLI verbs as literal tokens - 'graph --check' (3x). Same form.
if ($doc -cnotmatch 'guardrails samples verify') {
    $failures += "$f never names 'guardrails samples verify' - the skill still describes a harness in which a committed sample pair is a claim nothing executes, so the next agent authoring guardrails will not know the pair is now RUN (and that the .invalid half is the can-never-FAIL detector). Name it the way 'graph --check' is already named in this file."
}

# PRECEDENT: this skill names camelCase identifiers inline - 'mergeOnSuccess' (5x). Same form asked
# for. The alternation is deliberate (catalogue: accept both forms when both are legitimate) - write
# whichever name the landed code actually uses.
if ($doc -cnotmatch '(?:nextProbe|probeInterval)') {
    $failures += "$f never names 'nextProbe' or 'probeInterval' - the skill's execution-semantics still say a provider limit at a wave barrier ends the run, which is exactly the behaviour this plan replaced with a bounded wait-and-poll. Name the knob the way 'mergeOnSuccess' is already named in this file."
}

# PRECEDENT: the model-tiering section already describes the operator-facing model surfaces in prose -
# the paragraph beginning "Both are now IN FRONT OF THE OPERATOR (#349, Stage 3)", which names
# 'attempt-route.log', the literal 'requested model:' key and 'IRunObserver.AttemptModelResolved'
# inline. Same form asked for, in that same paragraph - which is also the one this plan makes stale.
if ($doc -cnotmatch '(?:[Mm]odel column|model per task|per-task model)') {
    $failures += "$f never records that the model now appears in the task ROW and per task on the run-level log index - so the skill's '#349 Stage 3' paragraph still claims the pair reaches the live table, a claim #524 measured to be false of anything that persists after a task finishes. Update that paragraph in place, in the prose form it already uses; do not add a new section."
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Output $_ }
    exit 1
}
exit 0
