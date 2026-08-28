# catches: an SSOT updated and the domain-knowledge skill left stale - the two halves of the same
#          obligation delivered apart. Every agent that works in this repo loads
#          guardrails-domain-knowledge; the SSOT is what it points AT. A skill that does not mention the
#          artifact means the next breakdown/review agent reasons about a state/ folder that no longer
#          matches reality, and the skill's own frontmatter SELF-UPDATING clause is the standing
#          instruction this check enforces.
#
# DOCUMENTATION target - EXEMPT from the committed .valid/.invalid sample pair (#468/#302). The
# PRECEDENT check is the mandatory substitute and is applied per clause below: each demanded token has
# a sibling precedent in this exact file, so the task is asked for the form the skill already uses.
#
# baseline counts on the untouched tree - MEASURED with Select-String over this exact subject, with
# this clause's own case sensitivity (-cnotmatch -> -CaseSensitive), not assumed:
#   plan-source\.json           0
#   declaredDelegatedDecisions  0
#   No ancestor task's prompt or writeScope writes these tokens into this subject - this task is the
#   only one whose writeScope names this file.
$f = if ($env:GR_SUBJECT) { $env:GR_SUBJECT } else { ".claude/skills/guardrails-domain-knowledge/SKILL.md" }

# PRECONDITION - the only early exit: every clause below would crash on a missing subject.
if (-not (Test-Path $f)) {
    Write-Output "$f does not exist - the domain-knowledge skill is what every agent in this repo loads, and its SELF-UPDATING clause makes this update part of the same change-set"
    exit 1
}

$doc = Get-Content $f -Raw
$failures = @()

# PRECEDENT: 'breakdown-intent.json' is already named in this file as a harness-written state/ artifact,
# with its shape shown inline. Same form asked for - a short entry, not a chapter.
if ($doc -cnotmatch 'plan-source\.json') {
    $failures += "$f never names 'plan-source.json' - the domain-knowledge skill still describes a harness that reads the source plan and forgets it. Add a short entry in the form the existing 'breakdown-intent.json' entry already uses."
}

# PRECEDENT: the same 'breakdown-intent.json' entry names its camelCase fields inline
# ('{ version, declaredAt, tasks: [...] }'). Same form asked for.
if ($doc -cnotmatch 'declaredDelegatedDecisions') {
    $failures += "$f never names 'declaredDelegatedDecisions' - it is the field the declared-count gate reads, so an entry without it describes the artifact but not what it is FOR. Name the field inline, the way the 'breakdown-intent.json' entry names 'version' and 'declaredAt'."
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Output $_ }
    exit 1
}
exit 0
