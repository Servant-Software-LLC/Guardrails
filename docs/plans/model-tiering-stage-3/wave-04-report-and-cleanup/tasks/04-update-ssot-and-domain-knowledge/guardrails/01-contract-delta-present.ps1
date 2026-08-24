# catches: a docs task that ran green having recorded NOTHING - the shipped code adds a line to a
#          documented surface (the run summary's bullet list in SSOT section 9) and the next reader of
#          either document would be told the pre-#349 story. It also catches the two half-deliveries:
#          the SSOT updated and the SELF-UPDATING domain-knowledge skill left behind (the more likely of
#          the two, because only one of the files needs the needsHarnessWrite hatch), and the one-line
#          placeholder - `- **Models used.** The run summary prints a line.` - which satisfies a bare
#          label grep while recording none of what a reader needs.
#
#          The two proximity clauses are what close that last hole, and they were VERIFIED to discriminate
#          at authoring: a lazy one-line entry inserted into the real SSOT fails both, and a full entry
#          passes both. They demand the two facts the entry is worthless without - WHAT it aggregates
#          (`provenance.model`, so the line is tied to the wave-2 datum rather than floating free) and that
#          it is OMITTED when nothing recorded a model (the Invariant-7 half, which is what promises every
#          existing single-model user's run report is unchanged).
#
# DOCUMENTATION deliverable: EXEMPT from the two-sided sample pair (#468) - there is no meaningful
# "invalid sample" of a design document. The mandatory substitute is the PRECEDENT check, and every token
# demanded below is a form the target document ALREADY uses:
#   - `Models used` in the SSOT          <- the two sibling bullets quote their own literal labels the same
#                                           way (`Total prompt cost: $X.XXXX`, `Per-tier spend: easy: ...`)
#   - `provenance.model` in the SSOT     <- the per-tier bullet names `provenance.tier` + `costUsd` +
#                                           `usage` in exactly this position
#   - `omitted`/`suppress` in the SSOT   <- the cost bullet says "the line is omitted entirely when no
#                                           attempt recorded a cost"; the per-tier bullet says
#                                           "Invariant 7 suppression". BOTH words are accepted
#   - `Models used` in SKILL.md          <- the sibling bullet quotes the literal `requested model:` key
# Backticked and bare forms are both accepted (the regexes ignore surrounding markup), so neither document
# is pushed away from its own conventions.
#
# MEASURED BASELINE 2026-08-23 against the merged wave-3 HEAD, each pattern run against the exact file
# that clause scans: `Models used` is 0 in BOTH files - and 0 across the whole tree, verified - so all
# four clauses below are 0, every proximity included (a window anchored on a token that does not occur
# cannot match). A fifth candidate clause was DROPPED at authoring for the opposite reason: requiring
# `provenance.model` in the SSOT WITHOUT the proximity window measured nonzero (section 7 documents the
# field already), so it would have been pre-satisfied and certified nothing.
$ErrorActionPreference = 'Continue'
$failures = @()

$ssot  = 'docs/plans/02-schemas-and-contracts.md'
$skill = '.claude/skills/guardrails-domain-knowledge/SKILL.md'

$required = @(
    @($ssot, 'Models used',
      'the SSOT does not name the new run-summary line. Its label is what a reader greps for and what the domain-knowledge skill cross-references; an unrecorded line is one nobody can look up'),
    @($ssot, 'Models used[\s\S]{0,700}provenance\.model',
      'the SSOT names the line but not WHAT IT AGGREGATES. The per-tier bullet immediately above names `provenance.tier` + `costUsd` + `usage` in this exact position; without the equivalent, the entry is a label with no contract behind it and a reader cannot tell what a segment counts'),
    @($ssot, 'Models used[\s\S]{0,700}(?:omitted|suppress)',
      'the SSOT does not record that the line is OMITTED when no attempt recorded a model. That is the Invariant-7 half, and it is the sentence that promises every existing single-model user''s run report is byte-unchanged - the sibling bullets both state their own version of it'),
    @($skill, 'Models used',
      'the domain-knowledge skill does not carry the moved contract. Its frontmatter makes it SELF-UPDATING when a contract moves, and this is the half most likely to be dropped - it is the only one of the two files that needs the needsHarnessWrite hatch')
)

foreach ($clause in $required) {
    $path = $clause[0]
    if (-not (Test-Path $path -PathType Leaf)) {
        # PRECONDITION for this clause only: the file is gone, so the scan below would read a null. Other
        # clauses still run - this is an accumulating gate, not an exit-1 chain.
        $failures += "$path does not exist - a deliverable file of this task is missing entirely"
        continue
    }
    $text = Get-Content -Raw -Path $path
    if ($text -notmatch $clause[1]) {
        $failures += "$path does not match /$($clause[1])/ - $($clause[2])"
    }
}

# --- the skill note must land in the RIGHT SECTION --------------------------------------------------
# A bare `Models used` anywhere in a 1200-line skill is satisfied by a note dropped in an unrelated
# section, where the next reader of the tiering contract will never see it. The prompt names the section
# and the bullet family; this is the clause that holds it to that.
if (Test-Path $skill -PathType Leaf) {
    $skillText = Get-Content -Raw -Path $skill
    $section = [regex]::Match($skillText, '(?ms)^## Model tiering.*?(?=^## |\z)')
    if (-not $section.Success) {
        $failures += "$skill no longer has a '## Model tiering' section - the prompt places the new note inside it, so either the heading was renamed or the section was removed"
    }
    elseif ($section.Value -notmatch 'Models used') {
        $failures += "$skill records the models-used line OUTSIDE the '## Model tiering' section - it belongs with the wave-2/3 bullet family it continues (grep BEST-KNOWN-ACTUAL), where a reader of the tiering contract will actually find it"
    }
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== contract delta: $($failures.Count) of 5 required record(s) missing ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    Write-Output ""
    Write-Output "This is the last task in the wave and the only one permitted to touch these files. Extend the text that is already there - the run-summary bullet list in SSOT section 9 (grep 'Per-tier spend: easy:') and the skill's '## Model tiering -- the SCHEMA half only' section - rather than adding new sections."
    exit 1
}
exit 0
