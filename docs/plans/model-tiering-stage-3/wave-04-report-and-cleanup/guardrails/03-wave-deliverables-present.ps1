# catches: a merged wave HEAD from which wave 4's work has VANISHED - and, more likely here, one from
#          which HALF of it has. Both siblings in this folder (a whole-solution build and the two
#          unfiltered suites) pass perfectly on the tree the wave STARTED from, so between them they carry
#          zero positive evidence that anything was delivered. This gate is the additive
#          contribution-present half the catalogue requires on top of them.
#
#          Three clauses earn their place beyond bookkeeping.
#
#          (1) The CALL. The whole surface is downstream of one line. If a merge drops
#          `JournalModelsUsed.Render(document)` from RunCommand and keeps everything else, the aggregator
#          exists, its unit tests pass, and no operator ever sees a models-used line - the aggregator is
#          dead code with every guardrail green. That is precisely the shape of #475, where
#          AttemptRecord.Usage shipped declared, read, and assigned by no construction site at all.
#
#          (2) The DELETION, which no other check in this wave can see. A deletion leaves no artifact to
#          assert on, so a merge that silently restores the folder - or a task that settled without ever
#          removing it - is invisible to a build and to a test suite. This is the only place the absence is
#          a checked fact.
#
#          (3) The two OVER-DELETION clauses beside it. `docs/plans/pilot-seat-model-provenance.md` is a
#          sibling PATH PREFIX of the folder being deleted: `Remove-Item docs/plans/pilot-seat-model-provenance*`
#          takes both, and the charter scopes only the FOLDER. `docs/plans/model-tiering-stage-3` is this
#          plan's own folder, which a broader sweep of docs/plans/ would take with it. Neither loss would
#          fail a build or a test, and the second would delete the plan currently executing.
#
# LOCAL - no `scope` key (GR2059/#459), like its siblings: a wave-root guardrail runs exactly once, on the
# merged HEAD at its own wave's exit, and the per-union set is the task folders plus the PLAN root.
$ErrorActionPreference = 'Continue'
$failures = @()

# MEASURED BASELINE 2026-08-23 against the merged wave-3 HEAD, each pattern run against the exact file that
# clause scans with this script's own (case-insensitive) operator: every required clause below is 0 or its
# file is absent entirely. This gate is correctly RED before the wave runs.
# NOTE on scoping: `Models used` and `JournalModelsUsed` appear NOWHERE in the tree at wave start - verified
# tree-wide, not merely in these files - so no stale copy can satisfy a clause. Every clause is scoped to a
# NAMED file regardless, because the folder task 03 deletes is itself full of plan text about this feature.
$required = @(
    # --- the aggregator (`01-author-tests-models-used-report` stubs it, `02-implement-models-used-report` fills it) ---
    @('src/Guardrails.Core/Journal/JournalModelsUsed.cs', 'class\s+JournalModelsUsed',
      'the models-used aggregator is not on the merged HEAD at all - the file itself is missing or no longer declares the type'),
    @('src/Guardrails.Core/Journal/JournalModelsUsed.cs', 'public\s+static\s+string\?\s+Render\s*\(',
      'JournalModelsUsed has no null-returning Render - the brief says follow the sibling (JournalTierSpend.Render), and the null return is what lets the caller spell suppression as `is { }` instead of testing a rendered string for emptiness'),
    # --- the call, without which every other clause is green over dead code ---
    @('src/Guardrails.Cli/Commands/RunCommand.cs', 'JournalModelsUsed\.Render\(document\)',
      'RunCommand never CALLS the aggregator - the type, its tests and the SSOT entry are all downstream of this one line, so without it the wave delivered an unreachable helper and an operator sees nothing'),
    @('src/Guardrails.Cli/Commands/RunCommand.cs', 'Models used',
      'RunCommand does not carry the operator-facing label - either the line is not printed, or it is printed under a different name than the SSOT and the domain-knowledge skill record'),
    # --- the proof (`01-author-tests-models-used-report`) ---
    @('tests/Guardrails.Core.Tests/ModelTiering/ModelsUsedSummaryTests.cs', 'class\s+ModelsUsedSummaryTests\b',
      'the aggregation test class is not on the merged HEAD - the suites prove the tests PASS, nothing else proves they EXIST, and a suite with the file deleted is green'),
    @('tests/Guardrails.Integration.Tests/ModelTiering/ModelsUsedReportTests.cs', 'class\s+ModelsUsedReportTests\b',
      'the end-to-end report test class is not on the merged HEAD - it is the ONLY proof that drives the real `run` command, so its loss turns the whole wave back into an untested helper'),
    # --- the contract (`04-update-ssot-and-domain-knowledge`) ---
    @('docs/plans/02-schemas-and-contracts.md', 'Models used',
      'the SSOT does not name the new run-summary line'),
    @('.claude/skills/guardrails-domain-knowledge/SKILL.md', 'Models used',
      'the domain-knowledge skill does not carry the moved contract')
)

foreach ($clause in $required) {
    $path = $clause[0]
    if (-not (Test-Path $path -PathType Leaf)) {
        # PRECONDITION for this clause only: the file is gone, so the scan below would read a null. Other
        # clauses still run - this is an accumulating gate, not an exit-1 chain.
        $failures += "$path does not exist on the merged HEAD - a deliverable file of this wave is missing entirely"
        continue
    }
    $text = Get-Content -Raw -Path $path
    # Strip C# comments before a REQUIRED scan. Without it, several clauses are satisfied by a comment
    # ALONE - `// TODO: JournalModelsUsed.Render(document)` matches even the dotted call - and that is fatal
    # to this gate specifically, because its whole job is catching a hunk that vanished in the wave merge,
    # and every prompt in this wave MANDATES a doc comment on the member it checks. A member dropped by an
    # AI-merge whose doc comment survived would otherwise read here as delivered.
    # Comments ONLY, never string literals (#470, the two-level rule): `Models used` is a required token
    # that legitimately lives inside a string literal (it IS the printed label), so stripping literals would
    # false-RED a correct implementation.
    if ($path -like '*.cs') {
        $text = ($text -replace '(?m)^\s*///.*$', '') -replace '(?m)//.*$', ''
    }
    if ($text -notmatch $clause[1]) {
        $failures += "$path does not match /$($clause[1])/ - $($clause[2])"
    }
}

# --- the deletion, and the two things it must NOT have taken with it ------------------------------
# A deletion leaves nothing to grep, so it is asserted directly on the filesystem. The two over-deletion
# clauses are the mirror: they are the only checks in this wave that can fail because task 03 did TOO MUCH,
# and neither loss would trouble a build or a test run.
$doomed = 'docs/plans/pilot-seat-model-provenance'
if (Test-Path $doomed -PathType Container) {
    $remaining = @(Get-ChildItem -Path $doomed -Recurse -File -ErrorAction SilentlyContinue).Count
    $failures += "$doomed still exists on the merged HEAD ($remaining file(s) under it) - the superseded 12-task folder was NOT deleted, or the deletion was undone by the wave merge. It still looks runnable and targets the pre-Stage-2 provenance contract, which is exactly the hazard the charter scoped its removal in for"
}

foreach ($keep in @(
    @('docs/plans/pilot-seat-model-provenance.md',
      'the superseded plan DOCUMENT was deleted too. The charter scopes only the FOLDER; the .md is a path PREFIX of it, so `Remove-Item docs/plans/pilot-seat-model-provenance*` takes both. Restore it'),
    @('docs/plans/model-tiering-stage-3',
      'this plan''s OWN folder was deleted - a sweep of docs/plans/ took the plan that is currently executing'))) {
    if (-not (Test-Path $keep[0])) {
        $failures += "$($keep[0]) no longer exists - $($keep[1])"
    }
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== wave-4 deliverables: $($failures.Count) problem(s) on the merged HEAD ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    Write-Output ""
    Write-Output "The build and both suites pass on a tree with none of this work in it, so a green from them is not evidence of delivery. Something dropped between the task segments and the wave merge."
    exit 1
}
exit 0
