# catches: a merged wave HEAD from which wave 3's work has VANISHED - and, more likely here, one from
#          which HALF of it has. Both siblings in this folder (a whole-solution build and the two
#          unfiltered suites) pass perfectly on the tree the wave STARTED from, so between them they carry
#          zero positive evidence that anything was delivered. This gate is the additive
#          contribution-present half the catalogue requires on top of them.
#
#          Two clauses earn their place beyond bookkeeping.
#
#          (1) The RAISE. Both operator surfaces are downstream of one call. If a merge drops
#          `_observer.AttemptModelResolved` and keeps everything else, the interface member exists, both
#          renderers render, both decorators forward - and the event is never raised, so every one of
#          them is dead code. That is precisely the shape of #475, where AttemptRecord.Usage shipped
#          declared, read, and assigned by no construction site at all, with every guardrail green.
#
#          (2) The DECORATOR SWEEP at the bottom. It is the POSITIVE form of the brief's "enumerate every
#          IRunObserver implementation before writing the writeScope": it derives the decorator set from
#          the merged tree rather than from a list this wave wrote down, so a THIRD decorator that landed
#          during the wave is caught here. Its predicate (implements IRunObserver AND holds an
#          IRunObserver field) is deliberately DIFFERENT from the reflection test's in
#          AttemptModelForwardingTests (implements IRunObserver AND has a constructor taking one), so the
#          two cover each other's blind spots - a decorator wired through a property or a factory escapes
#          the reflection predicate and is caught by this one, and vice versa.
#
# LOCAL - no `scope` key (GR2059/#459), like its siblings: a wave-root guardrail runs exactly once, on the
# merged HEAD at its own wave's exit, and the per-union set is the task folders plus the PLAN root.
$ErrorActionPreference = 'Continue'
$failures = @()

# MEASURED BASELINE 2026-08-23 against the merged wave-2 HEAD, case-sensitively, each pattern run against
# the exact file that clause scans: every required clause below is 0, and the decorator sweep reports 2
# offenders (both on-the-fly decorators). This gate is correctly RED before the wave runs.
# NOTE on scoping: the strings `AttemptModelResolved` and `AttemptModelSummary` DO already appear under
# docs/plans/pilot-seat-model-provenance/ - the SUPERSEDED plan folder wave 4 deletes. Every clause here
# is therefore scoped to a NAMED file; a tree-wide grep would have been satisfied by that stale folder.
$required = @(
    # --- surface 3: the log preamble (task 02) ---
    @('src/Guardrails.Core/Execution/TaskExecutor.cs', 'requested model',
      'TaskExecutor never writes a `requested model:` line - the attempt preamble still discloses one string, so an operator cannot see that the runner ran something other than the route asked for. That is the whole of surface 3'),
    @('src/Guardrails.Core/Execution/TaskExecutor.cs', '_observer\.AttemptModelResolved',
      'TaskExecutor never RAISES AttemptModelResolved - the interface member, both renderers and both decorator forwards are all downstream of this one call, so without it every other clause in this gate is green over dead code'),
    # --- surface 4: the event, its declaration and its two renderers (tasks 01, 03) ---
    @('src/Guardrails.Core/Execution/IRunObserver.cs', 'void\s+AttemptModelResolved\s*\(',
      'IRunObserver does not DECLARE the new member - task 01''s stub is not on the merged HEAD'),
    @('src/Guardrails.Cli/Ui/LiveRunObserver.cs', 'void\s+AttemptModelResolved\s*\(',
      'LiveRunObserver does not declare the member. A class implementing an interface gets NO member for a default method, so it is silently inheriting the empty body and the live UI renders nothing'),
    @('src/Guardrails.Cli/ConsoleRunObserver.cs', 'AttemptModelSummary\s*\(',
      'ConsoleRunObserver does not call the shared AttemptModelSummary formatter - either it does not render the event at all, or it grew a second inlined copy of the wording that will drift from the live surface'),
    # --- surface 4: the forwarding, the part the brief says a weak guardrail misses (task 04) ---
    @('src/Guardrails.Cli/Ui/OnTheFlyLogSiteObserver.cs', '_inner\.AttemptModelResolved',
      'the log-site decorator does not forward the event. It compiles, it satisfies the interface, and it swallows the event in every mode - the exact failure this wave was written to prevent'),
    @('src/Guardrails.Cli/Ui/OnTheFlyDiagramObserver.cs', '_inner\.AttemptModelResolved',
      'the diagram decorator does not forward the event - same swallow, second decorator'),
    # --- the proof (task 01) ---
    @('tests/Guardrails.Integration.Tests/ModelTiering/AttemptModelDisclosureTests.cs', 'class\s+AttemptModelDisclosureTests\b',
      'the disclosure test class is not on the merged HEAD - the suites prove the tests PASS, nothing else proves they EXIST, and a suite with the file deleted is green'),
    @('tests/Guardrails.Integration.Tests/ModelTiering/AttemptModelRenderingTests.cs', 'class\s+AttemptModelRenderingTests\b',
      'the rendering test class is not on the merged HEAD'),
    @('tests/Guardrails.Integration.Tests/ModelTiering/AttemptModelForwardingTests.cs', 'class\s+AttemptModelForwardingTests\b',
      'the forwarding test class is not on the merged HEAD - the one proof that both decorators relay the event'),
    # --- the contract (task 05) ---
    @('docs/plans/02-schemas-and-contracts.md', 'AttemptModelResolved',
      'the SSOT does not name the new IRunObserver member'),
    @('.claude/skills/guardrails-domain-knowledge/SKILL.md', 'AttemptModelResolved',
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
    # ALONE - `// TODO: _observer.AttemptModelResolved(...)` matches even the dotted anchor - and that is
    # fatal to this gate specifically, because its whole job is catching a hunk that vanished in the wave
    # merge, and every prompt in this wave MANDATES a doc comment on the member it checks. A member
    # dropped by an AI-merge whose doc comment survived would otherwise read here as delivered.
    # Comments ONLY, never string literals (#470, the two-level rule): `requested model` is a required
    # token that legitimately lives inside a string literal, so stripping literals would false-RED a
    # correct implementation. The forbidden-polarity sweep below has the opposite need and is not a scan
    # over literals at all.
    if ($path -like '*.cs') {
        $text = ($text -replace '(?m)^\s*///.*$', '') -replace '(?m)//.*$', ''
    }
    if ($text -notmatch $clause[1]) {
        $failures += "$path does not match /$($clause[1])/ - $($clause[2])"
    }
}

# --- the positive decorator sweep -----------------------------------------------------------------
# Derived from the merged tree, not from a list: EVERY .cs under src/ that both implements IRunObserver
# and HOLDS one (the structural signature of a transparent decorator) must declare the new member. On the
# wave-2 HEAD this predicate selects exactly OnTheFlyLogSiteObserver.cs and OnTheFlyDiagramObserver.cs -
# verified 2026-08-23 - and IRunObserver.cs is correctly excluded (its nested NullObserver implements the
# interface but holds no observer field).
$sources = Get-ChildItem -Path 'src' -Filter '*.cs' -File -Recurse -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' }
foreach ($file in $sources) {
    $text = Get-Content -Raw -Path $file.FullName
    $text = ($text -replace '(?m)^\s*///.*$', '') -replace '(?m)//.*$', ''
    if (($text -match ':\s*IRunObserver\b') -and ($text -match 'readonly\s+IRunObserver\b')) {
        if ($text -notmatch 'AttemptModelResolved') {
            $rel = $file.FullName.Substring($file.FullName.IndexOf('src'))
            $failures += "$rel is an IRunObserver DECORATOR (it implements the interface and holds one) but never mentions AttemptModelResolved - a decorator that omits a default-method member compiles, satisfies the interface, and silently swallows the event. If this file is new since the wave was authored, it is outside every task's writeScope and a human must decide who owns it"
        }
    }
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== wave-3 deliverables: $($failures.Count) problem(s) on the merged HEAD ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    Write-Output ""
    Write-Output "The build and both suites pass on a tree with none of this work in it, so a green from them is not evidence of delivery. Something dropped between the task segments and the wave merge."
    exit 1
}
exit 0
