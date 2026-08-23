# catches: a merged wave HEAD from which wave 2's work has VANISHED - and, more likely here, one from
#          which HALF of it has. Both siblings in this folder (a whole-solution build and the two
#          unfiltered suites) pass perfectly on the tree the wave STARTED from, so between them they
#          carry zero positive evidence that anything was delivered. This gate is the additive
#          contribution-present half the catalogue requires on top of them.
#
#          The clause that earns its place is the FOLD. The whole persist design rests on one fact: the
#          observed model is folded onto the single AttemptProvenance object, which already rides
#          PendingAttempt, so Scheduler.RecordSucceededSettle carries it to the worktree path for free
#          (D32). If a merge drops the fold and keeps everything else, the schema member exists, the
#          declaration exists, the tests exist - and the datum reaches nothing. That is precisely the
#          shape of #475, where AttemptRecord.Usage shipped declared, read, and assigned by no
#          construction site at all, with every guardrail green.
#
# LOCAL - no `scope` key (GR2059/#459), like its siblings: a wave-root guardrail runs exactly once, on
# the merged HEAD at its own wave's exit, and the per-union set is the task folders plus the PLAN root.
$ErrorActionPreference = 'Continue'
$failures = @()

# MEASURED BASELINE 2026-08-23 against the wave-2 entry tree, case-sensitively, each against the exact
# file that clause scans: every required clause below is 0, and the one forbidden clause is 0. This gate
# is correctly RED before the wave runs.
# RE-MEASURED at review, 2026-08-23 (#478 A2, fast path): all 12 required clauses fired on the baseline,
# so none is pre-satisfied; the 3 forbidden clauses correctly stayed green, which is what a healthy ban
# looks like before its task runs. Three bare-token clauses were re-anchored on a declaration or an
# assignment in the same pass - see the strip note in the loop for the measurement that forced it.
$required = @(
    # --- surface 1: capture (tasks 01, 02) ---
    @('src/Guardrails.Core/Prompts/ClaudeStreamParser.cs', 'public\s+string\?\s+Model\s*\{',
      'ClaudeResult does not declare Model - task 01''s stub is not on the merged HEAD'),
    @('src/Guardrails.Core/Prompts/ClaudeStreamParser.cs', '"system"',
      'the parser never mentions the "system" line type - the init model is still being DISCARDED by the type != "result" early return, which is the entire gap this wave exists to close. A declared ClaudeResult.Model with nothing populating it is exactly the #475 shape'),
    @('src/Guardrails.Core/Prompts/PromptInvocation.cs', 'public\s+string\?\s+ObservedModel\s*\{',
      'PromptResult does not DECLARE ObservedModel - task 01''s stub is not on the merged HEAD. Anchored on the declaration, not the bare word: task 01''s prompt mandates `public string? ObservedModel { get; init; }`, and matched up to the brace so accessor order is free (#112)'),
    @('src/Guardrails.Core/Prompts/ClaudePromptRunner.cs', 'ObservedModel\s*=',
      'ClaudePromptRunner never ASSIGNS ObservedModel - the runner quarantine does not carry the parsed value out, so PromptResult.ObservedModel is a member nothing populates'),
    # --- surface 2: persist (tasks 03, 04) ---
    @('src/Guardrails.Core/Journal/JournalModel.cs', 'public\s+string\?\s+RequestedModel\s*\{',
      'AttemptProvenance does not DECLARE RequestedModel - task 03''s stub is not on the merged HEAD. Anchored on the declaration: task 03''s prompt mandates `public string? RequestedModel { get; init; }`'),
    @('src/Guardrails.Core/Execution/ActionRunner.cs', 'ObservedModel\s*=\s*result\.ObservedModel',
      'ActionRun.FromPrompt does not carry result.ObservedModel - the datum reaches PromptResult and STOPS, one hop short of the attempt loop'),
    @('src/Guardrails.Core/Execution/TaskExecutor.cs', 'RequestedModel\s*=',
      'TaskExecutor never ASSIGNS RequestedModel - the fold onto the launch-time provenance is missing, so nothing ever becomes best-known-actual and BOTH record paths write the model the harness merely ASKED for. Anchored on the assignment, not the bare word: task 04''s deliverable is that the value is SET, and a mention proves nothing'),
    # --- the proof (tasks 01, 03) ---
    @('tests/Guardrails.Core.Tests/ModelTiering/ObservedModelCaptureTests.cs', 'class\s+ObservedModelCaptureTests\b',
      'the capture test class is not on the merged HEAD - the suites prove the tests PASS, nothing else proves they EXIST, and a suite with the file deleted is green'),
    @('tests/Guardrails.Integration.Tests/ModelTiering/Stage2ConformanceTests.cs', 'ObservedModelProvenance',
      'the both-record-paths tests are not on the merged HEAD - the one proof that this wave delivers to worktree mode and not just to serial mode'),
    # --- the contract (task 05) ---
    @('docs/plans/02-schemas-and-contracts.md', '"requestedModel"\s*:',
      'the SSOT does not document the requestedModel key'),
    @('docs/plans/17-model-tiering.md', 'requestedModel',
      'DoR 9.3 was not amended - it still describes the shape the charter review superseded'),
    @('.claude/skills/guardrails-domain-knowledge/SKILL.md', 'requestedModel',
      'the domain-knowledge skill does not carry the moved contract')
)

foreach ($clause in $required) {
    $path = $clause[0]
    if (-not (Test-Path $path -PathType Leaf)) {
        # PRECONDITION for this clause only: the file is gone, so the scan below would crash. Other
        # clauses still run - this is an accumulating gate, not an exit-1 chain.
        $failures += "$path does not exist on the merged HEAD - a deliverable file of this wave is missing entirely"
        continue
    }
    $text = Get-Content -Raw -Path $path
    # Strip C# comments before a REQUIRED scan, symmetrically with the forbidden block below.
    # MEASURED at review, 2026-08-23: without this, 7 of the 12 clauses were satisfied by a comment
    # ALONE - `// TODO: ObservedModel = result.ObservedModel` matches even the dotted-assignment anchor.
    # That is fatal to this gate specifically: its whole job is catching a hunk that vanished in the wave
    # merge, and every one of the prompts REQUIRES an XML doc comment on the very member it checks - so a
    # member dropped by an AI-merge whose doc comment survived would read here as delivered.
    # Comments ONLY, never string literals - the two-level rule (#470). A required token may legitimately
    # live in a literal (`"system"` below IS one), so literal-stripping here would false-RED a correct
    # implementation; the forbidden block strips literals because its polarity is the opposite.
    if ($path -like '*.cs') {
        $text = ($text -replace '(?m)^\s*///.*$', '') -replace '(?m)//.*$', ''
    }
    if ($text -notmatch $clause[1]) {
        $failures += "$path does not match /$($clause[1])/ - $($clause[2])"
    }
}

# --- the negative assertion: the refused key stays refused ---------------------------------------
# Anchored on a KEY USE (a quoted JSON key + colon) and a C# property DECLARATION, never the bare word:
# JournalModel.cs's doc comment and all three documents legitimately DISCUSS resolvedModel to record why
# it does not exist, and a bare ban would red exactly the prose that carries the decision (#470).
$forbidden = @(
    @('src/Guardrails.Core/Journal/JournalModel.cs', 'string\?\s+ResolvedModel\b', $true),
    @('docs/plans/02-schemas-and-contracts.md',      '"resolvedModel"\s*:',        $false),
    @('docs/plans/17-model-tiering.md',              '"resolvedModel"\s*:',        $false)
)
foreach ($clause in $forbidden) {
    $path = $clause[0]
    if (-not (Test-Path $path -PathType Leaf)) { continue }
    $text = Get-Content -Raw -Path $path
    if ($clause[2]) {
        # Strip XML-doc and line comments before scanning C#, so a comment explaining the refusal
        # cannot trip the ban on the refusal itself.
        $text = ($text -replace '(?m)^\s*///.*$', '') -replace '(?m)//.*$', ''
    }
    if ($text -match $clause[1]) {
        $failures += "$path introduces a resolvedModel field/key. The settled contract REFUSES it - provenance.model is best-known-actual and requestedModel appears only on disagreement. Two fields claiming the same fact is how they drift"
    }
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== wave-2 deliverables: $($failures.Count) problem(s) on the merged HEAD ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    Write-Output ""
    Write-Output "The build and both suites pass on a tree with none of this work in it, so a green from them is not evidence of delivery. Something dropped between the task segments and the wave merge."
    exit 1
}
exit 0
