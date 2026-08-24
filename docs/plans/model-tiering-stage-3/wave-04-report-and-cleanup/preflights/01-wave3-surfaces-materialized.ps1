# catches: this wave's four tasks being pointed at code that MOVED. Every one of them names a member of a
#          named file by hand, and the brief describes the shape that existed when it was WRITTEN. This is
#          the WAVE ENTRY gate (SSOT 14.3): wave 3's HEAD is merged, and what this wave builds ON must be
#          present and real - verified ONCE at the boundary rather than discovered by an agent three
#          attempts in.
#
#          Four clauses carry more weight than the rest.
#
#          (1) The INSERTION POINT. `02-implement-models-used-report`'s whole deliverable is one more line
#          inside `PrintTotalCost`, printed from the SAME `JournalDocument` local the per-tier line already
#          reads. Three clauses pin that: the method exists, `PrintSummary` still CALLS it, and
#          `JournalTierSpend.Render(document)` is still inside it. If the summary surface was restructured,
#          that task's writeScope names the wrong file and the run finds out the expensive way.
#
#          (2) The SIBLING. The brief is explicit that the models-used line "is the same kind of read over
#          the same records, so follow the sibling rather than inventing a path". The sibling is
#          `JournalTierSpend.Render` - a static, null-returning, prefix-less renderer. If its shape changed,
#          the instruction "mirror it" no longer names anything.
#
#          (3) The SENTINEL, and it is the least obvious. `01-author-tests-models-used-report`'s end-to-end
#          test drives a fake-claude plan that configures NO model anywhere, so the only thing its attempt
#          can journal is `PromptExecutionSupport.CliDefaultModelDisplay`. Delete that sentinel and the
#          attempt records a NULL model, the models-used line is correctly suppressed, and the test the
#          brief's central requirement rests on has nothing to assert. It would fail as a mystery.
#
#          (4) The DELETION TARGET. `03-delete-superseded-plan-folder` asserts a folder is GONE. If it is
#          already gone at wave start, that task is a no-op whose guardrail passes vacuously - a green over
#          nothing, which is the failure mode this whole stage is about. Asserting it PRESENT here is the
#          only place that distinction can be drawn.
#
# POSITIVE and MONOTONE-SAFE (SKILL.md 9.2): every clause is assert-PRESENT, including clause (4) - a wave
# ENTRY gate must never carry a "not yet present" assertion, because a segment only grows. The absence
# assertion for the deleted folder lives in the wave EXIT gate (03-wave-deliverables-present.ps1), which is
# the correct home for it.
#
# MEASURED BASELINE 2026-08-23 against the merged wave-3 HEAD (C:\.a\42519044\_integration), each pattern
# run against the exact file that clause scans: all 16 matched exactly 1, and the deletion target was
# present. That nonzero is EXPECTED and NAMED - a wave ENTRY preflight is one of the two legitimate
# green-on-arrival guardrails (#478). A clause that goes RED means the anchor moved and the task pointed at
# it is authored against a tree that no longer exists.
#
# AUTHOR-TIME PROBE (#302): samples/01-wave3-surfaces-materialized.probe.ps1 runs both halves, and its
# INVALID half already caught one real defect here - see the SSOT clause's own note below.
$ErrorActionPreference = 'Continue'
$failures = @()

# FLAT triples, never a path-keyed hashtable of clause lists: PowerShell UNWRAPS a single-element array
# literal, so a file with exactly one clause would iterate as a STRING and `$clause[0]` would become its
# first CHARACTER - silently disabling that clause. Wave 2's entry gate shipped that bug and its
# author-time invalid sample caught it.
$anchors = @(
    # --- the insertion point (`02-implement-models-used-report` edits this file, and this member) ---
    @('src/Guardrails.Cli/Commands/RunCommand.cs', 'static\s+void\s+PrintTotalCost\s*\(',
      'RunCommand.PrintTotalCost - the run-summary printer `02-implement-models-used-report` adds one line to. Its whole deliverable is scoped to this member'),
    @('src/Guardrails.Cli/Commands/RunCommand.cs', 'PrintTotalCost\(planDirectory, output\)',
      'PrintSummary no longer CALLS PrintTotalCost - so a models-used line added inside it would never reach an operator, and every task-level test that drives the real `run` command would be asserting on a method nothing invokes'),
    @('src/Guardrails.Cli/Commands/RunCommand.cs', 'JournalTierSpend\.Render\(document\)',
      'the per-tier line is no longer rendered from a `document` local inside PrintTotalCost - that local is the JournalDocument the models-used line reads, and this call is the exact form the new one mirrors'),

    # --- the sibling the brief says to follow ---
    @('src/Guardrails.Core/Journal/JournalTierSpend.cs', 'public\s+static\s+string\?\s+Render\s*\(',
      'JournalTierSpend.Render - the null-returning, prefix-less renderer JournalModelsUsed is modelled on. The brief says follow the sibling rather than inventing a path; without it there is no sibling'),

    # --- the datum wave 2 landed, which this wave aggregates ---
    @('src/Guardrails.Core/Journal/JournalModel.cs', 'public\s+AttemptProvenance\?\s+Provenance\s*\{',
      'AttemptRecord.Provenance - the per-attempt object every model this wave counts is read from. Absent, there is no path from a journal attempt to a model at all'),
    @('src/Guardrails.Core/Journal/JournalModel.cs', 'public\s+string\?\s+RequestedModel\s*\{',
      'AttemptProvenance.RequestedModel - the mismatch signal, present ONLY on disagreement. The brief names an aggregation that assumes both keys always exist as the specific wrong answer here, so a wave with no second key has no mismatch case to get wrong'),
    @('src/Guardrails.Core/Execution/TaskExecutor.cs', 'RequestedModel\s*=',
      'wave 2''s observed-model FOLD - the ONE site that ever writes RequestedModel. Without it no run can journal a mismatch, and the mismatch half of this wave''s line is untestable against a real run'),
    @('src/Guardrails.Core/Execution/PromptExecutionSupport.cs', 'CliDefaultModelDisplay\s*=\s*"\(cli default\)"',
      'the "(cli default)" sentinel. LOAD-BEARING for `01-author-tests-models-used-report`: its end-to-end test drives a plan that configures no model, so this sentinel is the only value its attempt can journal. Gone, the attempt records a null model, the line is correctly suppressed, and the test has nothing to assert'),

    # --- the test seams this wave's red is authored against ---
    @('tests/Guardrails.Integration.Tests/DryRunCliTests.cs', 'Run_PromptPlan_PrintsTotalCostLine',
      'the shipped end-to-end precedent for a run-summary line: drive the real CLI over a fake-claude plan and assert on captured output. `01-author-tests-models-used-report`''s central test is that test with a different assertion'),
    @('tests/Guardrails.Integration.Tests/DryRunCliTests.cs', 'Run_DeterministicPlan_OmitsTotalCostLine',
      'the shipped SUPPRESSION precedent - a script-only plan prints no cost line. The models-used suppression test mirrors it, and mirroring it is what keeps the two summary lines behaving alike'),
    @('tests/Guardrails.Integration.Tests/FakeClaudePlanBuilder.cs', 'AddPromptTask',
      'FakeClaudePlanBuilder.AddPromptTask - the fixture that builds a runnable plan whose prompt runner is a stub binary. It is how the prompt-attempt half of the end-to-end test gets a journalled model without spending a token'),
    @('tests/Guardrails.Integration.Tests/StatePlanBuilder.cs', 'class\s+StatePlanBuilder',
      'StatePlanBuilder - the script-only plan fixture the suppression half drives'),
    @('tests/Guardrails.Integration.Tests/StringConsoleIo.cs', 'class\s+StringConsoleIo',
      'StringConsoleIo - the per-invocation output capture. Without it the end-to-end tests cannot read the summary they assert on without touching Console.SetOut, which is not parallel-safe'),
    @('tests/Guardrails.Core.Tests/ModelTiering/PerTierSpendTests.cs', 'class\s+PerTierSpendTests',
      'PerTierSpendTests - the Core-side sibling whose journal-fixture shape ModelsUsedSummaryTests mirrors. It is also the precedent for asserting a SUPPRESSION on the rendered string rather than on a structure'),

    # --- the documents `04-update-ssot-and-domain-knowledge` extends ---
    # Anchored on the bullet's own WORKED-EXAMPLE line, not on the bare phrase "Per-tier spend". The bare
    # phrase was this gate's first draft and the author-time probe proved it DEAD: the file also carries a
    # lowercase prose mention 1000 lines away, and a case-insensitive -notmatch is satisfied by that alone,
    # so the clause could never fail however far the bullet moved.
    @('docs/plans/02-schemas-and-contracts.md', 'Per-tier spend: easy:',
      'the SSOT bullet the models-used entry sits beside. Task 04 is told to extend the text that is already there rather than open a new section; if that bullet moved, the instruction points nowhere'),
    @('.claude/skills/guardrails-domain-knowledge/SKILL.md', 'BEST-KNOWN-ACTUAL',
      'the skill''s wave-2/3 bullet family this wave adds a third bullet to. It is the precedent for length, placement and voice')
)

# One read per distinct file, so a 16-clause sweep is 10 reads and a missing file is reported once.
$cache = @{}
foreach ($clause in $anchors) {
    $path = $clause[0]
    if (-not $cache.ContainsKey($path)) {
        if (Test-Path $path -PathType Leaf) {
            $cache[$path] = Get-Content -Raw -Path $path
        }
        else {
            # PRECONDITION for this file: it is gone, so every clause against it would scan a null.
            # Report the file ONCE and skip its clauses; other files still run.
            $cache[$path] = $null
            $failures += "$path does not exist - a task in this wave is authored against it, so this wave is pointed at a tree that no longer matches"
        }
    }

    $content = $cache[$path]
    if ($null -eq $content) { continue }

    if ($content -notmatch $clause[1]) {
        $failures += "$path no longer matches /$($clause[1])/ - $($clause[2])"
    }
}

# --- the deletion target, asserted PRESENT ---------------------------------------------------------
# Not a regex clause: `03-delete-superseded-plan-folder` deletes a DIRECTORY, and what must hold at wave
# start is that the directory is still there. Its own guardrail asserts the folder is GONE, which is
# trivially true of a folder that was already gone - so without this clause that task can settle green
# having done nothing, and the wave exit gate would agree with it.
$doomed = 'docs/plans/pilot-seat-model-provenance'
if (-not (Test-Path $doomed -PathType Container)) {
    $failures += "$doomed does not exist at wave start - 03-delete-superseded-plan-folder has nothing to delete, so it and its guardrail would both go green over a no-op. Either someone removed it out of band (drop that task from the wave and say so) or the path is wrong"
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== wave-4 entry gate: $($failures.Count) precondition(s) this wave is authored against have MOVED ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    Write-Output ""
    Write-Output "This wave's tasks name these members and paths by hand. Re-run /plan-breakdown for wave-04-report-and-cleanup against the current integration worktree rather than letting an agent rediscover the drift one failed attempt at a time."
    exit 1
}
exit 0
