# catches: this wave's tasks being pointed at code that MOVED. Every task below edits a named member of a
#          named file, and the brief is explicit that the shape it describes is the shape that existed
#          when it was WRITTEN: "the decorator pair named above is what existed when this brief was
#          written, and wave 2 has landed since". This gate is that verification, performed ONCE at the
#          wave boundary rather than discovered by an agent three attempts in. It is the WAVE ENTRY gate
#          (SSOT 14.3): wave 2's HEAD is merged, and what this wave builds ON must be present and real.
#
#          Two clauses carry more weight than the rest.
#
#          (1) The FOLD POINT. Wave 3's whole log-header deliverable is "re-disclose attempt-route.log
#          AFTER the observed model is known". That requires two things wave 2 landed to still be
#          adjacent in TaskExecutor.cs: the `RequestedModel =` fold, and `WriteRouteDisclosure` itself.
#          If wave 2's fold moved to another file, `05-implement-route-log-and-observer-raise`'s writeScope is wrong and the run discovers it
#          the expensive way.
#
#          (2) The DECORATOR-FORWARDS-DEFAULTS PRECEDENT. Both decorators are asserted here to carry
#          `_inner.VerifierAdvisoryFound` - not because this wave touches that event, but because it is
#          the PROOF that each of these types is still a forwarding decorator that explicitly relays
#          default-method members. A decorator refactored into something else (a base class, an event
#          aggregator) would silently change what `07-forward-attempt-model-in-decorators`'s deliverable even means, and its tests would
#          be asserting a shape that no longer exists.
#
# POSITIVE and MONOTONE-SAFE (SKILL.md 9.2): every clause is assert-PRESENT. Deliberately NO "the
# IRunObserver implementation set has not grown" clause here, however tempting the brief's "enumerate
# every implementation" makes it - that is a negative assertion, and a wave-entry gate must never carry
# one. It lives in the wave EXIT gate instead (03-wave-deliverables-present.ps1), in its POSITIVE form:
# every implementation present on the merged HEAD declares the new member.
#
# MEASURED BASELINE 2026-08-23 against the merged wave-2 HEAD (C:\.a\42519044\_integration), each pattern
# run against the exact file that clause scans: every clause below matched >= 1 (counts 1,1,1,1,1,1,1,1,
# 1,1,1,2,2,1). That nonzero is EXPECTED and NAMED - a wave ENTRY preflight is one of the two legitimate
# green-on-arrival guardrails (#478). A clause that goes RED means the anchor moved and the task pointed
# at it is authored against a tree that no longer exists.
$ErrorActionPreference = 'Continue'
$failures = @()

# FLAT triples, never a path-keyed hashtable of clause lists: PowerShell UNWRAPS a single-element array
# literal, so a file with exactly one clause would iterate as a STRING and `$clause[0]` would become its
# first CHARACTER - silently disabling that clause. Wave 2's entry gate shipped that bug and its
# author-time invalid sample caught it.
$anchors = @(
    # --- surface 3: the log header (`05-implement-route-log-and-observer-raise` edits this file, and only this file) ---
    @('src/Guardrails.Core/Execution/TaskExecutor.cs', 'static\s+void\s+WriteRouteDisclosure\s*\(',
      'TaskExecutor.WriteRouteDisclosure - the preamble writer `05-implement-route-log-and-observer-raise` must render best-known-actual and re-invoke after the fold'),
    @('src/Guardrails.Core/Execution/TaskExecutor.cs', 'RequestedModel\s*=',
      'wave 2''s observed-model FOLD onto the launch-time provenance - the point `05-implement-route-log-and-observer-raise` re-discloses immediately after. Without it there is no best-known-actual value for the preamble to print'),
    @('src/Guardrails.Core/Execution/TaskExecutor.cs', 'action\.ObservedModel',
      'the fold''s guard on the runner-reported model - proof the observed value reaches the attempt loop, which is what both of this wave''s surfaces consume'),
    @('src/Guardrails.Core/Execution/TaskExecutor.cs', '_observer\.AttemptStarting',
      'the attempt loop''s observer field and an existing per-attempt raise - the precedent and the call site for `05-implement-route-log-and-observer-raise`''s new raise'),

    # --- surface 4: the observer event ---
    @('src/Guardrails.Core/Execution/IRunObserver.cs', 'interface\s+IRunObserver',
      'the IRunObserver interface - task 01 adds one default-method member to it'),
    @('src/Guardrails.Core/Execution/IRunObserver.cs', 'void\s+VerifierAdvisoryFound\s*\(',
      'VerifierAdvisoryFound - the default-method member whose doc comment states the decorator rule this wave re-applies ("an unforwarded call resolves to this empty body and the advisory is swallowed silently"). Task 01 mirrors its shape'),
    @('src/Guardrails.Cli/Ui/LiveRunObserver.cs', 'class\s+LiveRunObserver',
      'LiveRunObserver - one of `06-render-attempt-model-in-live-and-console`''s two renderers'),
    @('src/Guardrails.Cli/ConsoleRunObserver.cs', 'class\s+ConsoleRunObserver',
      'ConsoleRunObserver - `06-render-attempt-model-in-live-and-console`''s other renderer, and the only one a headless test can read output from'),
    @('src/Guardrails.Cli/Ui/OnTheFlyLogSiteObserver.cs', '_inner\.VerifierAdvisoryFound',
      'the log-site decorator still EXPLICITLY forwards a default-method member to its inner observer - proof it is still the forwarding decorator `07-forward-attempt-model-in-decorators` extends'),
    @('src/Guardrails.Cli/Ui/OnTheFlyDiagramObserver.cs', '_inner\.VerifierAdvisoryFound',
      'the diagram decorator still EXPLICITLY forwards a default-method member - same proof, for `07-forward-attempt-model-in-decorators`''s second target'),

    # --- the test seam this wave's red is authored against ---
    @('tests/Guardrails.Integration.Tests/ModelTiering/Stage2PlanHarness.cs', 'class\s+Stage2PlanHarness',
      'the public Stage2PlanHarness - the in-process real-seam harness `02-author-tests-disclosure`''s tests drive'),
    @('tests/Guardrails.Integration.Tests/ModelTiering/Stage2PlanHarness.cs', 'IRunObserver\.Null',
      'the harness''s hardcoded null observer - the exact site task 01 widens into an optional injection point so a recording observer can prove the raise'),
    @('tests/Guardrails.Integration.Tests/ModelTiering/Stage2PlanHarness.cs', 'AttemptLogDir',
      'Stage2RunResult.AttemptLogDir - how `02-author-tests-disclosure`''s tests locate the attempt-route.log they read'),

    # --- the datum both surfaces consume (wave 2's deliverable) ---
    @('src/Guardrails.Core/Journal/JournalModel.cs', 'public\s+string\?\s+RequestedModel\s*\{',
      'AttemptProvenance.RequestedModel - the mismatch signal BOTH of this wave''s surfaces render. Absent, wave 2 did not land and nothing in wave 3 has a two-string case to display')
)

# One read per distinct file, so a 14-clause sweep is 8 reads and a missing file is reported once.
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
    Write-Output "=== wave-3 entry gate: $($failures.Count) anchor(s) this wave is authored against have MOVED ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    Write-Output ""
    Write-Output "This wave's tasks name these members by hand. Re-run /plan-breakdown for wave-03-operator-surfaces against the current integration worktree rather than letting an agent rediscover the drift one failed attempt at a time."
    exit 1
}
exit 0
