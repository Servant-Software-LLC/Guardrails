# catches: a HOLLOW test - named for the behaviour, body a tautology (Assert.True(true), an assertion on
#          an object the test itself constructed, a duration test that asserts a value it hard-coded). It
#          PASSES on today's tree and hides behind its genuinely-failing siblings, so a suite-level
#          non-zero exit would certify the file honest (#375). One entry per enumerated behaviour, each
#          observed Failed in the runner's OWN TRX - never merely discovered by name, which a hollow body
#          satisfies exactly as a comment satisfies a token floor.
#
#          The sharpest hollow shape HERE is a test that hand-builds an ActionRun carrying Turns or
#          ActionMs and asserts a journaller method copied it. That proves the journaller and says
#          nothing about ActionRun.FromPrompt, which is where the turn count is dropped today - the exact
#          way AttemptRecord.Usage shipped structurally dead with every guardrail green (#475). The
#          prompt pins a real serial run whose only fake is a stub IPromptRunner.
#
# PER-OUTCOME COVERAGE is why this census carries sixteen rows and not six. AttemptJournaler has NINE
#          independent `new AttemptRecord` sites, one per outcome, each called DIRECTLY from
#          TaskExecutor - nothing funnels through FailedAttempt. A suite proving the envelope survives
#          guardrail-failed therefore proves nothing about the other seven failure outcomes, and
#          `needs-human` is not hypothetical: real run.json rows in the corpus carry
#          "outcome":"needs-human". The needs-human, permission-denied and mid-attempt-cancelled rows are
#          pinned in BOTH classes on purpose - tasks 12 and 12a filter on their own class alone, so an
#          outcome pinned only in AttemptTurnsTests leaves that outcome's SEGMENTS unbound, and vice
#          versa. That asymmetry is the silent half-fix these rows exist to prevent.
#
# TWO RECORDERS THE SIX-ROW SHAPE MISSED, and both are shapes an implementer gets HALF right, which is why
#          each is bound rather than left to the prompt's prose (#221: a prohibition with no guardrail
#          behind it is a suggestion).
#
#            - 'AStructuralWallHalt_RecordsBothSegments' - AttemptJournaler.StructuralWallHalt takes an
#              ActionRun but NO GuardrailRunResult, although its call site holds one (it is reached only
#              when guardrails RAN and FAILED and a structural .claude/ wall coincided). Copying task 12's
#              "read it inside the method" shape therefore ships GuardrailMs = null on a path where the
#              guardrails demonstrably ran - compile-clean, test-green, half a fix. This row asserts BOTH
#              members. NOTE for a reviewer, since this census cannot see it: StructuralWallHalt records
#              outcome `guardrail-failed`, the SAME string the ordinary FailedAttempt records, so an
#              outcome assertion alone cannot tell the two roads apart. The prompt pins the discriminator -
#              a defaultRetries:2 fixture that recorded EXACTLY ONE attempt (the #326 one-attempt halt).
#            - 'AMidAttemptCancel_StillRecordsItsTurnCount' / '...ItsActionSegment' - Cancelled is SPLIT
#              across its three TaskExecutor call sites: two mid-attempt (the action returned, so the facts
#              are in hand) and one PRE-attempt. Task 12a's own prompt calls it "the row most likely to be
#              got wrong". These two bind the carrying half; the exempt row below binds the other.
#
# FOUR DECLARED EXEMPTIONS, stated here because the census's own failure text points a retry agent back
#          at this header. All four assert a DELIBERATE NULL, and all four are GREEN on today's tree
#          because nothing populates AttemptRecord.Turns or .Segments yet - so a CORRECT null assertion
#          already holds, and demanding red would demand that a correct test fail. Each asserts
#          Expect='Executed' (it ran, and was not [Skip]ped) and stays IN the manifest: a dropped row and
#          an oversight look identical.
#
#            - 'AScriptAction_RecordsNoTurnCount' - a SCRIPT attempt records a null turn count. Its job is
#              to STAY green through task 12, which threads the count and could just as easily default a
#              script attempt to 0.
#            - 'ATaskPreflightFailure_RecordsNoTurnCount' and 'ATaskPreflightFailure_RecordsNoSegments' -
#              AttemptJournaler.TaskPreflightFailed fires BEFORE the attempt loop and its caller holds no
#              ActionRun at all, so null is the honest record. Without these two rows the census would bind
#              only the CARRYING half, leaving tasks 12/12a free to satisfy every green check by defaulting
#              the uninstructed recorders to 0 (or to an AttemptSegments with both members null, which is a
#              CLAIM that a measurement was taken and came back empty). These bind the other half so the
#              implementation cannot silently choose.
#            - 'APreAttemptCancel_RecordsNoSegments' - the third of Cancelled's call sites, and the sharpest
#              null in the plan. TaskExecutor hands it a SYNTHESIZED ProcessResult whose Duration is
#              TimeSpan.Zero, so an implementation that derives actionMs from the ProcessResult it was
#              handed - inside the method, rather than at each call site - records a FABRICATED 0. That is
#              strictly worse than a null: 0 is a positive claim that the action ran and took no time, on a
#              path where no action ran at all.
#
#              STRUCTURAL NOTE, because this row's SHAPE contradicts the hollow-shape rule above and a
#              reviewer must see that the contradiction is deliberate. This is the ONE row in the file
#              written at the journaller seam (a direct AttemptJournaler.Cancelled call - Guardrails.Core
#              grants InternalsVisibleTo to Guardrails.Core.Tests) instead of through a real run, because
#              the pre-attempt site is NOT reachable from one: it fires only when cancellation lands in the
#              window between the mid-attempt cancellation check and the attempt's transient-pause return,
#              and a token already cancelled when the next attempt starts settles at the MID-attempt site
#              instead. The alternative was a race-timed fixture, i.e. a flaky guardrail, which teaches an
#              agent to re-run rather than to fix. Do NOT rewrite the other rows to match this one.
#
#          0 is a CLAIM that a model was invoked and took no turns - the same null-versus-zero line
#          TelemetryRow.CostUsd draws in its own doc-comment.
#
#          The exempt rows have their own hollow shape, sharper than the general one above: a null read
#          off an attempt that never happened passes vacuously. The prompt pins the fix - assert the
#          attempt EXISTS and its Outcome is 'task-preflight-failed' BEFORE asserting the null - but this
#          census cannot see that, which is why the shape is named here for a reviewer.
#
#          WHAT 'Executed' CAN AND CANNOT SEE - read this before mistaking an exemption for full
#          coverage. Expect='Executed' is DELIBERATELY WEAKER than Expect='Failed'. 'Failed' is a claim
#          about the test's BODY: it could only have been red if the assertion actually bit. 'Executed'
#          is a claim about the test's EXISTENCE only - a method of that name ran and was not [Skip]ped.
#          So a tautological body - Assert.True(true) - inside a method named
#          'APreAttemptCancel_RecordsNoSegments' satisfies THIS census and then passes at task 12a too,
#          which is exactly the coverage that row exists to deny (it is the row guarding against a
#          FABRICATED ZERO on the pre-attempt cancellation path). Nothing mechanical can close that gap
#          from here: the assertion lives in a C# body and this guardrail reads a TRX.
#
#          WHAT COMPENSATES - all three at instruction level, in this task's action.prompt.md:
#            (a) each exempt row is REQUIRED to carry an Assert.Null on the member its NAME claims -
#                Turns for the two turn-count rows; Segments (the member ITSELF, never its ActionMs,
#                which an AttemptSegments of two nulls would satisfy) for the two segment rows;
#            (b) each asserts its POSITIVE CONTROL first - the attempt exists and its Outcome is the
#                expected token - so the null cannot pass vacuously off a record that never landed;
#            (c) all four STAY in this manifest through tasks 12 and 12a, so a later deletion surfaces
#                as a census failure rather than as silence.
#          A REVIEWER of this file must therefore read the four exempt bodies by eye. That is the trade
#          this exemption buys, and it is worth it: the alternative was demanding red from a test that
#          is CORRECTLY green, which teaches an agent to contrive a failure.
#
#          The other twelve rows are red on today's tree: ActionRun.FromPrompt discards
#          PromptResult.NumTurns, GuardrailRunner has no stopwatch at all, and nothing constructs an
#          AttemptSegments anywhere - so any test asserting a turn count or a segment ARRIVED on the
#          journalled record must fail until tasks 12 and 12a land, whichever outcome's record it reads.
#
# TWO RECORDERS DELIBERATELY LEFT UNBOUND - a named trade, not an omission. Of AttemptJournaler's nine
#          recorders this census binds seven: the serial success settle, guardrail-failed, needs-human,
#          permission-denied, structural-wall-halt, task-preflight-failed and both halves of the split
#          Cancelled. RateLimitExhausted and NoRoute are NOT pinned. Both settle where no action ran at
#          all, so their null is structurally forced rather than chosen, and both cost more fixture than
#          they buy: RateLimitExhausted needs the whole-task transient-pause budget exhausted, and NoRoute
#          needs a routing configuration whose rung resolves to no candidate at or above it. The TURN half
#          of StructuralWallHalt is likewise unbound: the method takes an ActionRun, so task 12 reads the
#          count inside it exactly as it does for needs-human and permission-denied, and only the GUARDRAIL
#          half (which must arrive from the caller) carries real risk. Written down so a later reader can
#          re-litigate the trade - an unbound recorder that is named is a decision; one that is merely
#          absent is indistinguishable from an oversight.
#
# TWO CLASSES, one file, one census. The turn count and the segment durations are implemented by two
#          DIFFERENT tasks, each filtering on its own class, so the pinned names are split across
#          AttemptTurnsTests and AttemptSegmentsTests. This census is the only check that sees both, so
#          the filter below is the two-class ALTERNATION - a bare `|` inside the parenthesised term,
#          never `\|`, which dotnet test reads as a literal backslash and matches nothing while exiting 0.
#
# The prompt<->manifest agreement is NOT mechanically enforced (GR2026 is blind to a hashtable read
#          through Where-Object). The sixteen names below were read side by side with this task's
#          action.prompt.md tables, which pin each one VERBATIM.
#
# Culture pin: this census reads the TRX (schema tokens, NOT localized), so the guard does not depend on
#          it - kept anyway so the logged summary is readable.
$ErrorActionPreference = 'Continue'
$env:DOTNET_CLI_UI_LANGUAGE = 'en'

# This pair's OWN test classes, never a plan-wide trait (#455). This plan introduces no trait at all, so
# this is shape 3 with an alternation - the two class terms and nothing else. 'AttemptTurnsTests' and
# 'AttemptSegmentsTests' were both checked against all 195 existing Core test class names and every other
# class this plan authors: each is a substring of none of them, and neither contains the other, so the
# filter is discriminating.
$filter = '(FullyQualifiedName~AttemptTurnsTests|FullyQualifiedName~AttemptSegmentsTests)'

# THE MANIFEST: each enumerated behaviour -> the test method name the ACTION PROMPT PINNED for it.
$manifest = [ordered]@{
    # --- class AttemptTurnsTests (task 12 filters on this class alone) ---------------------------------
    'a prompt action turn count reaches the attempt record'      = 'APromptActionsTurnCount_ReachesTheAttemptRecord'
    'a failed attempt still records its turn count'              = 'AFailedAttempt_StillRecordsItsTurnCount'
    'a needs-human attempt still records its turn count'         = 'ANeedsHumanAttempt_StillRecordsItsTurnCount'
    'a permission-wall attempt still records its turn count'     = 'APermissionWallAttempt_StillRecordsItsTurnCount'
    'a mid-attempt cancel still records its turn count'          = 'AMidAttemptCancel_StillRecordsItsTurnCount'
    # --- class AttemptSegmentsTests (task 12a filters on this class alone) -----------------------------
    'the action elapsed time reaches the attempt segments'       = 'TheActionsElapsedTime_ReachesTheAttemptSegments'
    'the guardrail elapsed time reaches the attempt segments'    = 'TheGuardrailsElapsedTime_ReachesTheAttemptSegments'
    'a failed attempt still records both segments'               = 'AFailedAttempt_StillRecordsItsSegments'
    'a needs-human attempt still records its action segment'     = 'ANeedsHumanAttempt_StillRecordsItsActionSegment'
    'a permission-wall attempt still records its action segment' = 'APermissionWallAttempt_StillRecordsItsActionSegment'
    # The GUARDRAIL half of the pair, on the one carrying recorder whose call site holds a
    # GuardrailRunResult it is not handed - see this file's header.
    'a structural-wall halt records BOTH segments'               = 'AStructuralWallHalt_RecordsBothSegments'
    'a mid-attempt cancel still records its action segment'      = 'AMidAttemptCancel_StillRecordsItsActionSegment'
    # --- the four DELIBERATE-NULL rows - see this file's header ------------------------------------
    # Nothing sets Turns or Segments today, so these nulls are already true and a CORRECT test is green.
    # Assert each RAN, never that it failed. They bind the honest-null half of the contract, without which
    # tasks 12/12a could satisfy every green check by defaulting the uninstructed recorders to 0.
    'a script action records no turn count (null, never 0)'      = @{ Name = 'AScriptAction_RecordsNoTurnCount'; Expect = 'Executed' }
    'a task-preflight failure records no turn count'             = @{ Name = 'ATaskPreflightFailure_RecordsNoTurnCount'; Expect = 'Executed' }
    'a task-preflight failure records no segments at all'        = @{ Name = 'ATaskPreflightFailure_RecordsNoSegments'; Expect = 'Executed' }
    'the pre-attempt cancel records no segments'                 = @{ Name = 'APreAttemptCancel_RecordsNoSegments'; Expect = 'Executed' }
}

$resultsDir = Join-Path ([System.IO.Path]::GetTempPath()) "guardrails-census-$PID"
Remove-Item $resultsDir -Recurse -Force -ErrorAction SilentlyContinue   # never read a PREVIOUS attempt's TRX

$out = dotnet test tests/Guardrails.Core.Tests/Guardrails.Core.Tests.csproj --filter $filter --nologo `
       --logger 'trx;LogFileName=census.trx' --results-directory $resultsDir 2>&1
$out | ForEach-Object { Write-Output $_ }

# PRECONDITION - the ONE legitimate early exit. No TRX means the run never happened (host failed to
# start, wrong project path, or a malformed --filter, which exits 0 SILENTLY). Diagnose THAT. Falling
# through would print "every behaviour unbound", a confident wrong message aimed at the one artifact a
# retry agent is allowed to edit.
$trx = Get-ChildItem $resultsDir -Filter *.trx -Recurse -ErrorAction SilentlyContinue |
       Sort-Object LastWriteTime | Select-Object -Last 1
if (-not $trx) {
    Write-Output "no .trx under $resultsDir - the test run did not happen (test host failed to start, wrong project path, or a malformed --filter, which exits 0 with no results). This is NOT a finding about the tests: do NOT rewrite them."
    exit 1
}

# DOTTED navigation - the TRX has a default xmlns, so SelectNodes('//UnitTestResult') finds nothing.
# The Where-Object is NOT decoration: with zero tests executed the TRX has NO <Results> element, the
# navigation yields $null, and @($null).Count is 1 - so the bare @(...) form would make the guard below
# evaluate 1 -lt 1 and NEVER FIRE.
$xml      = [xml](Get-Content $trx.FullName -Raw)
$recorded = @($xml.TestRun.Results.UnitTestResult | Where-Object { $_ })
if ($recorded.Count -lt 1) {
    Write-Output "the TRX records ZERO executed tests - the --filter '$filter' matched nothing, or every match is [Skip]ped out of execution. This is NOT a finding about the tests: do NOT rewrite them."
    exit 1
}

# ACCUMULATE (#179): one distinguishable message per unbound behaviour, so ONE attempt learns every gap.
$failures = @()
foreach ($behaviour in $manifest.Keys) {
    $entry  = $manifest[$behaviour]
    $name   = if ($entry -is [string]) { $entry }   else { $entry.Name }
    $expect = if ($entry -is [string]) { 'Failed' } else { $entry.Expect }
    # -cmatch: C# method names are case-SENSITIVE and PowerShell -match is not.
    # The (\(|$) tail admits a [Theory] row's appended data without admitting a longer sibling name.
    $pattern = '\.' + [regex]::Escape($name) + '(\(|$)'
    $hits    = @($recorded | Where-Object { $_.testName -cmatch $pattern })
    if ($hits.Count -lt 1) {
        $failures += "$behaviour -> no test named '$name' ran (absent from the file, in the wrong one of the two classes, or not selected by the filter)"
        continue
    }
    if ($expect -eq 'Executed') {
        # DECLARED EXEMPTION: assert the row RAN, not that it was red. An absent outcome attribute is
        # treated as not-executed - never let a missing value read as satisfied.
        $notRun = @($hits | Where-Object { $_.outcome -eq 'NotExecuted' -or [string]::IsNullOrEmpty($_.outcome) })
        if ($notRun.Count -gt 0) {
            $failures += "$behaviour -> '$name' is a DECLARED EXEMPTION (Expect='Executed' - see this file's header for why a correct test is green before the capture lands) and did NOT execute. 'NotExecuted' means [Fact(Skip=...)]. An exempt row still has to run; skipping it turns the exemption into no coverage at all."
        }
        continue
    }
    $notRed = @($hits | Where-Object { $_.outcome -ne 'Failed' })
    if ($notRed.Count -gt 0) {
        $seen = (($notRed | ForEach-Object { $_.outcome } | Sort-Object -Unique) -join '/')
        $failures += "$behaviour -> '$name' is $seen on today's tree, not Failed. ActionRun.FromPrompt discards PromptResult.NumTurns, GuardrailRunner has no stopwatch, and nothing constructs an AttemptSegments - so a test asserting either datum REACHED the journalled record cannot pass. Two shapes make one pass anyway. (a) It asserts on a hand-built ActionRun or AttemptRecord instead of on the journal a real serial run produced - run the executor with a stub IPromptRunner and assert on RunJournal.Document. (b) Its fixture never reached the recorder it names, so every assertion read a record that was never there: the structural-wall and mid-attempt-cancel rows need their positive control asserted FIRST (see the action prompt - the structural-wall halt records the SAME 'guardrail-failed' outcome string as the ordinary failed attempt, so its discriminator is the one-attempt halt on a defaultRetries:2 fixture, not the outcome). ('NotExecuted' = [Fact(Skip=...)].)"
    }
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== per-test red census: $($failures.Count) of $($manifest.Count) enumerated behaviours are not proven RED on today's tree ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    exit 1
}

Write-Output "Per-test red census: all $($manifest.Count) enumerated behaviours are bound to a pinned test observed at its declared outcome."
# Said on the GREEN path on purpose. A red row proves an assertion failed; it does NOT prove the
# fixture reached the recorder the row names - a fixture that settled down another road is red for the
# wrong reason and reads identically here. That is the action prompt's POSITIVE-CONTROL requirement,
# which this census cannot see, and it is the one gap a passing census must not be read as covering.
Write-Output "NOTE: red here means an assertion failed - NOT that the fixture reached the recorder its row names. Whether each red fired on the Turns/Segments assertion rather than on a positive control is the action prompt's requirement, and is not observable from a TRX."
exit 0
