# catches: an SSOT updated and the domain-knowledge skill left stale - the two halves of the same
#          obligation delivered apart. Every agent that works in this repo loads
#          guardrails-domain-knowledge; the SSOT is what it points AT. A skill that does not mention
#          the new verb or the pre-DAG sample-pair check means the next breakdown/review agent reasons
#          about a harness that no longer matches reality - and here that is worse than silence,
#          because this skill is precisely where the author-time smoke-test doctrine lives. It
#          currently tells an author to run a guardrail against a valid and an invalid sample BY HAND,
#          as advice; after this plan the harness executes that pair itself, before the DAG. An agent
#          reading the un-updated skill will keep treating a committed pair as a claim nobody checks.
#          The skill's own frontmatter SELF-UPDATING clause is the standing instruction this check
#          enforces.
#
# DOCUMENTATION target - EXEMPT from the committed .valid/.invalid sample pair (#468/#302). The
# PRECEDENT check is the mandatory substitute and is applied per clause below: each demanded token has
# a sibling precedent in this exact file, so the task is asked for the form the skill already uses.
#
# As in guardrail 01, the PREFLIGHT STEP is deliberately not token-checked: every candidate token is
# either ambient here or a precedent-free coinage. Clause 2 is its closest honest proxy.
#
# baseline counts on the untouched tree - MEASURED with grep over this exact subject, with this
# clause's own case sensitivity (-cnotmatch -> case-sensitive), not assumed (2026-08-29):
#   samples verify                                                   0
#   guardrails samples verify                                        0   (both spellings measured; the
#                                                                        clause takes the bare one)
#   SampleVerifier                                                   0
#   Positive control for those zeroes (#500): the same invocation over the same file for a literal
#   known to be present, `PlanDefinition`, returns 8 - so the search reached the document rather than
#   silently skipping it.
#   NOTE the tokens NOT used here because they are already present and would be pre-satisfied (#478):
#   'GR2055' (3x), 'preflights/' (9x), 'Full Flight Checks' (1x), 'two-sided' (1x). None can carry a
#   required-present clause. 'sample pair' measures 0 here but 1 in the SSOT, so it is not used in
#   EITHER guardrail - a clause that works in one subject and is pre-satisfied in the other invites
#   the next editor to copy the wrong half.
#   No ancestor task's prompt or writeScope writes the demanded tokens into this subject - this task is
#   the only one in plan 26 whose writeScope names this file.
$f = if ($env:GR_SUBJECT) { $env:GR_SUBJECT } else { ".claude/skills/guardrails-domain-knowledge/SKILL.md" }

# PRECONDITION - the only early exit: every clause below would crash on a missing subject.
if (-not (Test-Path $f)) {
    Write-Output "$f does not exist - the domain-knowledge skill is what every agent in this repo loads, and its SELF-UPDATING clause makes this update part of the same change-set"
    exit 1
}

$doc = Get-Content $f -Raw
$failures = @()

# PRECEDENT: this skill already names CLI verbs as literal tokens - 'graph --check' (3x) - and, like
# the SSOT, it spells them BARE rather than 'guardrails'-prefixed. So the clause takes the bare form,
# which is a substring of both spellings and therefore accepts either (see the fuller note in
# guardrail 01: requiring the prefix would false-red a document written in its own dominant style).
if ($doc -cnotmatch 'samples verify') {
    $failures += "$f never names the 'samples verify' verb - the skill still describes a harness in which a committed sample pair is a claim nothing executes, so the next agent authoring guardrails will not know the pair is now RUN, that the .invalid half is the can-never-FAIL detector GR2055's mirror leaves uncovered, or that 'validate' deliberately still does not run it. Name it the way 'graph --check' is already named in this file (either spelling - bare or 'guardrails'-prefixed - satisfies this check), and update the author-time smoke-test material in place rather than adding a new section - that is the paragraph this plan turns from advice into an executed check."
}

# PRECEDENT: this skill names harness types inline - 'PlanDefinition' (8x), 'RunJournal' (5x),
# 'IRunObserver' (5x), 'ProcessRunner' (2x), 'PlanLoader' (1x). Same form asked for. This clause is
# also the closest available proxy for the preflight STEP (see the residual note in the header).
if ($doc -cnotmatch 'SampleVerifier') {
    $failures += "$f never names 'SampleVerifier' - so the skill records neither the shared verifier nor the fact that the verb and the pre-DAG preflight step drive the SAME one. An agent that does not know there is one verifier behind two entry points is exactly the agent who will add a second copy of the pair-verification policy, and the two disagreeing is the failure this feature exists to detect. Name the type the way 'PlanDefinition' and 'RunJournal' are already named in this file."
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Output $_ }
    exit 1
}
exit 0
