# catches: this wave's tasks being pointed at code that MOVED. Every task below edits a named member of
#          a named file, and the brief's own orientation says so in the imperative: "Stage 2 restructured
#          AttemptProvenance ... do not trust this brief's description of any signature - verify it."
#          This gate is that verification, performed ONCE before the DAG rather than discovered by an
#          agent three attempts in. It is the WAVE ENTRY gate (SSOT 14.3): the prior wave's HEAD is
#          merged, and what this wave builds ON must be present and real on it.
#
#          The load-bearing clause is the LAST one. The whole persist design rests on a fact the harness
#          already establishes for the judge object (D32): the observed model is folded onto the ONE
#          AttemptProvenance object, and the worktree settle path then carries it for free because
#          Scheduler.RecordSucceededSettle copies `Provenance = pending.Provenance`. Delete that one line
#          and tasks 03/04 still go green in serial mode and deliver NOTHING in the mode almost every run
#          uses - the exact half-failure the brief calls out ("both record paths must carry it").
#
# POSITIVE and MONOTONE-SAFE (SKILL.md 9.2): every clause is assert-PRESENT. A wave-entry preflight runs
# against a segment that only grows, so a negative "not yet present" clause would flip false the moment
# an unrelated file landed.
#
# FLAT triples on purpose, not a path-keyed hashtable of clause lists. The first draft used the latter,
# and PowerShell UNWRAPS a single-element array literal: for every file with exactly one clause, the
# iteration variable became a STRING and `$clause[0]` became its first CHARACTER, so four clauses -
# including the Scheduler one above - could never fail. The author-time invalid sample caught it; the
# valid sample did not, because under an all-present tree everything passes either way.
#
# MEASURED BASELINE 2026-08-23 against the merged wave-1 HEAD: every clause below is PRESENT (>= 1). That
# nonzero is EXPECTED and named - a wave ENTRY preflight is one of the two legitimate green-on-arrival
# guardrails (#478). A clause that goes RED means the anchor moved and the task pointed at it is wrong.
$ErrorActionPreference = 'Continue'
$failures = @()

$anchors = @(
    @('src/Guardrails.Core/Prompts/ClaudeStreamParser.cs', 'record\s+ClaudeResult\b',
      'the ClaudeResult record (task 01 declares Model on it)'),
    @('src/Guardrails.Core/Prompts/ClaudeStreamParser.cs', 'class\s+ClaudeStreamParser\b',
      'the ClaudeStreamParser class'),
    @('src/Guardrails.Core/Prompts/ClaudeStreamParser.cs', 'public\s+void\s+Feed\s*\(',
      'ClaudeStreamParser.Feed - the line-by-line reader task 02 widens'),
    @('src/Guardrails.Core/Prompts/PromptInvocation.cs', 'record\s+PromptResult\b',
      'the PromptResult record (task 01 declares ObservedModel on it)'),
    @('src/Guardrails.Core/Prompts/ClaudePromptRunner.cs', 'new\s+PromptResult\b',
      'at least one PromptResult construction site (task 02 populates them)'),
    @('src/Guardrails.Core/Execution/ActionRunner.cs', 'record\s+ActionRun\b',
      'the ActionRun record (task 03 declares ObservedModel on it)'),
    @('src/Guardrails.Core/Execution/ActionRunner.cs', 'static\s+ActionRun\s+FromPrompt\s*\(',
      'ActionRun.FromPrompt - the one restatement point task 04 extends'),
    @('src/Guardrails.Core/Execution/TaskExecutor.cs', 'BuildProvenance\s*\(',
      'TaskExecutor.BuildProvenance - the launch-time provenance task 04 folds onto'),
    @('src/Guardrails.Core/Execution/TaskExecutor.cs', 'provenance\s+with\s*\{\s*Judge',
      'the D32 judge fold - the precedent task 04 copies, and proof the fold point still exists'),
    @('src/Guardrails.Core/Journal/JournalModel.cs', 'record\s+AttemptProvenance\b',
      'the AttemptProvenance record (task 03 declares RequestedModel on it)'),
    @('src/Guardrails.Core/Journal/JournalModel.cs', 'public\s+string\?\s+Model\s*\{',
      'AttemptProvenance.Model - the field whose MEANING this wave changes'),
    @('src/Guardrails.Core/Execution/Scheduler.cs', 'Provenance\s*=\s*pending\.Provenance',
      'the worktree settle carrying provenance - WITHOUT this line the whole wave delivers to serial mode only'),
    @('tests/Guardrails.Integration.Tests/ModelTiering/Stage2ConformanceTests.cs', 'class\s+Stage2DeferredSettleRun\b',
      'the deferred-settle harness task 03 reuses to prove BOTH record paths')
)

# One read per distinct file, so a 13-clause sweep is 8 reads and a missing file is reported once.
$cache = @{}
foreach ($clause in $anchors) {
    $path = $clause[0]
    if (-not $cache.ContainsKey($path)) {
        if (Test-Path $path -PathType Leaf) {
            $cache[$path] = Get-Content -Raw -Path $path
        }
        else {
            # PRECONDITION for this file: it is gone, so every clause against it would crash on a null
            # read. Report the file ONCE and skip its clauses; other files still run.
            $cache[$path] = $null
            $failures += "$path does not exist - a task in this wave is scoped to write it, so the wave is authored against a tree that no longer matches"
        }
    }

    $content = $cache[$path]
    if ($null -eq $content) { continue }

    if ($content -notmatch $clause[1]) {
        $failures += "$path no longer matches /$($clause[1])/ - $($clause[2])"
    }
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== wave-2 entry gate: $($failures.Count) anchor(s) this wave is authored against have MOVED ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    Write-Output ""
    Write-Output "This wave's tasks name these members by hand. Re-run /plan-breakdown for wave-02-capture-and-persist against the current integration worktree rather than letting an agent rediscover the drift one failed attempt at a time."
    exit 1
}
exit 0
