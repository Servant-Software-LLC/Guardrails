# catches: a plan-edit watch that is IMPLEMENTED but never WIRED - the #120 composition-root
#          false-green, in the shape this plan can actually produce. LivePlanEditWatchTests (Core)
#          drives the watch directly and goes fully green over a Scheduler that never constructs it,
#          never polls it, and never re-baselines it. Only P1/P2/P3 in the Integration suite touch the
#          real path, and each of those exercises ONE boundary - so the second poll site, the
#          RunReport sibling and the rendered text can all be absent while eleven of thirteen pins
#          pass. This is the structural floor under them.
#
# WHAT THIS DOES *NOT* PROVE, stated plainly because a green here reads stronger than it is:
#          section 5.3 names FIVE harness writers that must each be followed by a plan-wide
#          Rebaseline() - the JIT wave breakdown, BreakdownInventory.Revert,
#          SweepIncompleteTrailingTaskFolders, QuarantineWholeTasksFolder, and a TryResolveDrift that
#          RESOLVED. Only the FIRST has a pin (P2). The other four are UNPINNED in plan 31 and
#          UNGUARDED here, and no mechanical check can bind a Rebaseline call to the writer it is
#          meant to follow: all five writer symbols already appear in Scheduler.cs (measured: 4, 5, 2,
#          2 and 2 occurrences), so a presence clause is vacuous, and a proximity window was measured
#          false-redding a correct implementation elsewhere in this plan. Whether each hook sits at
#          the right writer is a HUMAN read and a /guardrails-review probe. Treat this guardrail as
#          "the wiring exists", never as "the wiring is complete".
#
#          A COUNT FLOOR of five Rebaseline call sites was considered and REJECTED: a legitimate
#          implementation may route all five writers through one helper, and a floor that dead-ends a
#          correct implementation is worse than a named gap (that is the same judgement the
#          second-glob-matcher guardrail reached after its proximity clause was measured false-redding
#          its own valid sample).
#
# TWO-LEVEL STRIP (section 11a): $raw is NEVER matched against and never reassigned. All clauses here
#          are REQUIRED-present, so they read $code (comments gone, string literals INTACT) - the two
#          token clauses are ABOUT string literals and would be unsatisfiable over a literal-stripped
#          copy.
#
# MEASURED BASELINES on master @1490d2a, against the exact subject each clause scans (#478) - every
#          one is 0, which is the correct shape for a task that has not run:
#          Scheduler.cs 'LivePlanEditWatch' 0 | 'Poll(' 0 | 'Rebaseline(' 0
#          DecisionEntry.cs 'plan-edit' 0 | '"observed"' 0
#          RunReport.cs 'Observations' 0
$ErrorActionPreference = 'Continue'

$ws = $env:GUARDRAILS_WORKSPACE
if ([string]::IsNullOrEmpty($ws)) { $ws = (Get-Location).Path }

function Get-Code([string]$rel) {
    $full = Join-Path $ws $rel
    if (-not (Test-Path -LiteralPath $full -PathType Leaf)) { return $null }
    $raw  = Get-Content -Raw -LiteralPath $full                # NEVER matched against, never reassigned
    $code = [regex]::Replace($raw,  '/\*[\s\S]*?\*/', ' ')     # /* */ block comments
    return   [regex]::Replace($code, '(?m)//[^\r\n]*', ' ')    # // and /// line comments
}

# ACCUMULATE (#478): one distinguishable message per clause, dumped once - never an exit-1 chain that
# reports one gap per attempt.
$failures = @()

# --- Scheduler.cs: the watch is constructed, polled at two boundaries, and re-baselined -----------
$schedRel = 'src/Guardrails.Core/Execution/Scheduler.cs'
$sched    = Get-Code $schedRel
if ($null -eq $sched) {
    $failures += "PRECONDITION: $schedRel does not exist."
}
else {
    # -cnotmatch on every required clause: C# identifiers are case-SENSITIVE, and a case-insensitive
    # require-present clause false-GREENS on text C# would never compile (taxonomy entry 3).
    if ($sched -cnotmatch 'new\s+LivePlanEditWatch\s*\(') {
        $failures += "$schedRel never constructs a LivePlanEditWatch. The Core unit tests drive the watch directly and go fully green over a Scheduler that has never heard of it - so this is the only place 'the feature is reachable from a real run' is asserted structurally."
    }
    $polls = [regex]::Matches($sched, '\.\s*Poll\s*\(').Count
    if ($polls -lt 2) {
        $failures += "$schedRel calls .Poll( at $polls site(s); section 5.2 requires TWO, on the scheduler's own thread at boundaries that already exist: task DISPATCH and task SETTLE. With one, an edit landing between dispatch and settle is invisible until the next task, and there is no pin for the missing boundary. (No new thread, no lock, no daemon, and no FileSystemWatcher - section 5.2 rejects it.)"
    }
    if ($sched -cnotmatch '\.\s*Rebaseline\s*\(') {
        $failures += "$schedRel never calls .Rebaseline(. Section 5.3's five harness writers each need a PLAN-WIDE re-baseline (no task ids) after them, or the watch reports the harness's own writes as operator edits and the advisory gets muted (#229). NOTE this clause only proves the hook EXISTS - only the JIT-breakdown writer has a pin (P2); the other four are a human read."
    }
}

# --- DecisionEntry.cs: the two additive tokens ----------------------------------------------------
$deRel = 'src/Guardrails.Core/Execution/DecisionEntry.cs'
$de    = Get-Code $deRel
if ($null -eq $de) {
    $failures += "PRECONDITION: $deRel does not exist."
}
else {
    if ($de -cnotmatch '"plan-edit"') {
        $failures += "$deRel does not carry the literal ""plan-edit"". It is a NEW boundary token, additive alongside drift / wave / task. Section 5.4 rejects reusing boundary:""drift"" - a consumer filtering on it would start counting observations as drift decisions, and the drift boundary means a gate was reached and RESOLVED, which nothing here was."
    }
    if ($de -cnotmatch '"observed"') {
        $failures += "$deRel does not carry the literal ""observed"". It is the NEW decision token - the harness noticed and reported at this boundary; nothing was decided and nothing changed. It is also what makes the entry outcome-INERT: RunOutcomePolicy branches on the DECISION token only (SuppressesDelivery and ProceededUnreviewedWaveCount) and never reads Boundary, so ""observed"" cannot suppress mergeOnSuccess or reach exit code 5."
    }
}

# --- RunReport.cs: the Observations sibling -------------------------------------------------------
$rrRel = 'src/Guardrails.Core/Execution/RunReport.cs'
$rr    = Get-Code $rrRel
if ($null -eq $rr) {
    $failures += "PRECONDITION: $rrRel does not exist."
}
elseif ($rr -cnotmatch 'IReadOnlyList\s*<\s*DecisionEntry\s*>\s+Observations') {
    $failures += "$rrRel does not declare: public IReadOnlyList<DecisionEntry> Observations. RunReport.Decision is SINGULAR and means the pre-DAG drift decision this run took; a run can produce N plan-edit observations. Section 5.4 adds a defaulted sibling rather than widening Decision, so no existing consumer changes and the shipped drift renderer is not touched for a reason unrelated to drift."
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== watch wiring: $($failures.Count) problem(s) ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    Write-Output ""
    Write-Output "Every file named above is inside this task's writeScope. Do NOT reach for an observer or a decorator: section 5.4 reuses the shipped DecisionRecorded event precisely so none of the five renderers and neither transparent decorator has to change - and IRunObserver's default no-op bodies mean a decorator missed in the wiring would compile, pass every test that does not exercise it, and drop the warning silently."
    exit 1
}
Write-Output "Wiring present: the Scheduler constructs the watch, polls it at 2+ boundaries and re-baselines it; the plan-edit and observed tokens exist; RunReport carries Observations. (Whether each of section 5.3's five re-baseline hooks sits at the right writer is NOT proven here - see this file's header.)"
exit 0
