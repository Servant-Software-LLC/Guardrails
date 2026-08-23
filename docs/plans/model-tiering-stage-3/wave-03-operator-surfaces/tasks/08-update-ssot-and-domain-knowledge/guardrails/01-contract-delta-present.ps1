# catches: a docs task that ran green having recorded NOTHING - the shipped code moves two documented
#          contracts (the attempt-route.log field set in SSOT sections 8 and 9.6, and the IRunObserver
#          event surface) and the next reader of either document would be told the pre-#349 story. It also
#          catches the half-delivery: the SSOT updated and the SELF-UPDATING domain-knowledge skill left
#          behind, which is the more likely of the two because only one of the files needs the
#          needsHarnessWrite hatch.
#
# DOCUMENTATION deliverable: EXEMPT from the two-sided sample pair (#468) - there is no meaningful
# "invalid sample" of a design document. The mandatory substitute is the PRECEDENT check, and every token
# demanded below is a form the target document ALREADY uses:
#   - `AttemptModelResolved` in the SSOT   <- the file names 6 other members as `IRunObserver.<Member>`
#   - `requested model` in the SSOT        <- the file quotes route-log literal keys already, e.g.
#                                             `tierSource` appears backticked 6 times
#   - `AttemptModelResolved` in SKILL.md   <- the skill already names IRunObserver.DecisionRecorded and
#                                             IRunObserver.WaveStarting/WaveFinished inline
# Both backticked and bare forms are accepted (the regexes ignore surrounding markup), so neither
# document is pushed away from its own conventions.
#
# MEASURED BASELINE 2026-08-23 against the merged wave-2 HEAD, each pattern run against the exact file
# that clause scans: all three are 0. A fourth candidate clause was DROPPED at authoring for exactly this
# reason - a proximity check `attempt-route\.log[\s\S]{0,400}#349` measured 1 on the baseline (the
# adjacent attempt-provenance.json entry already cites #349), so it was pre-satisfied and would have
# certified nothing.
$ErrorActionPreference = 'Continue'
$failures = @()

$required = @(
    @('docs/plans/02-schemas-and-contracts.md', 'AttemptModelResolved',
      'the SSOT does not name the new IRunObserver member. It is a default-method event: an undocumented one is invisible to the next author of a decorator, which is the precise failure this wave exists to prevent'),
    @('docs/plans/02-schemas-and-contracts.md', 'requested model',
      'the SSOT does not name the literal `requested model:` key the attempt preamble now writes. Section 8 still describes the pre-#349 field set, so a reader parsing attempt-route.log has no way to know the mismatch line exists'),
    @('.claude/skills/guardrails-domain-knowledge/SKILL.md', 'AttemptModelResolved',
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

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== contract delta: $($failures.Count) of $($required.Count) required record(s) missing ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    Write-Output ""
    Write-Output "This is the last task in the wave and the only one permitted to touch these files. Extend the text that is already there - section 8's attempt-route.log entry, the section 9.6 disclosure paragraph, and the skill's '## Model tiering -- the SCHEMA half only' section - rather than adding new sections."
    exit 1
}
exit 0
