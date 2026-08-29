# catches: a component built, unit-tested and CLI-reachable but never reached by the RUN path -
#          SampleVerifier green in isolation and green under `guardrails samples verify`, while
#          PlanPreflightPhase never calls it, so a reversed sample pair still costs a full run's tokens
#          and the whole gate is inert where it was supposed to matter (#120). This is the
#          composition-root guardrail in its strongest form: SampleVerifierWiringTests drives the REAL
#          EvaluateAsync with no manual injection (guardrail 01 is what stops it injecting the seam) and
#          the real CLI run entry, and asserts what only the wired path can produce.
#
#          It also catches the PLACEMENT defect, which the suite exit code alone cannot: the sample step
#          added AFTER `if (plan.PlanPreflights.Count == 0) return true;` leaves every plan without a
#          preflights/ folder - which is most plans - unprotected, while every other test here stays
#          green. The per-test census below binds that one behaviour to an OBSERVED PASSING test by
#          name, so quietly dropping it is a named finding rather than a silent -1 on a count.
#
#          POST-SPLIT: this is the FORWARD half of a real TDD pair. Task 04 authored these five tests
#          and proved four of them RED against the unwired phase
#          (tasks/04-.../guardrails/03-tests-fail-on-unwired-phase.ps1); this census requires all five
#          PASSED after the wiring lands. The `-ne 'Passed'` manifest and task 04's `Expect='Failed'`
#          manifest name the same five methods and must stay in lockstep - including
#          EvaluateAsync_ReturnsTrue_WhenEverySamplePairIsSound, which is task 04's declared exemption
#          from the red bar (the unwired phase returns true at its first line for a plan with no
#          preflights/ folder) and is NOT exempt here: it is the only test standing between this task
#          and a phase that returns false unconditionally.
#
#          scope: LOCAL (no sidecar) - it asserts "the verifier IS wired", which cannot be true before
#          this task's own action has run, so it fails the #125 union-safe test and must not be tagged
#          scope:"integration" (#250).
#          Re-emits the assertion/exception lines at the END so they reach the retry-feedback tail (#179).
$env:DOTNET_CLI_UI_LANGUAGE = 'en'    # the run summary is LOCALIZED (#455); the TRX below is not
$filter = 'Category=BacklogSlate&FullyQualifiedName~SampleVerifierWiringTests'
# ~SampleVerifierWiringTests is DISCRIMINATING (#455/#193): the only sibling sharing the prefix is
# SampleVerifierTests (tasks 01/02), which lives in a DIFFERENT project and does not contain this
# substring in either direction. Measured: zero pre-existing classes anywhere in src/ or tests/ contain
# "SampleVerifier".
# NO -v q on the TEST command: it suppresses the Error Message/Expected/Actual/Stack Trace block,
# leaving only "[FAIL] <name>" for the re-emit below to find - which defeats #179 by the flag alone.
# NO --no-build either, deliberately, and it was MEASURED that this matters (2026-08-29): with it, a
# census reads whatever is in bin/ rather than the SOURCE tree - the task 04 census was observed exiting
# 0 over five STALE tests still compiled into the assembly after their source file had been deleted.
# 02-build-passes normally refreshes it first, so the window is narrow, but a single-guardrail
# `revalidate` re-runs this out of order and a census that can certify a deleted file certifies nothing.
$resultsDir = Join-Path ([System.IO.Path]::GetTempPath()) "guardrails-wiring-census-$PID"
Remove-Item $resultsDir -Recurse -Force -ErrorAction SilentlyContinue   # never read a PREVIOUS attempt's TRX
$out = dotnet test tests/Guardrails.Integration.Tests --filter $filter --nologo `
       --logger 'trx;LogFileName=wiring.trx' --results-directory $resultsDir 2>&1
$testExit = $LASTEXITCODE                                  # capture BEFORE any other statement
$out | ForEach-Object { Write-Output $_ }                  # full log first (for the attempt's saved output)

# EXIT CODE FIRST (#455): a test host that never ran exits NON-zero with no summary, so checking the
# exit code first reports its real error instead of blaming the filter or the census.
if ($testExit -ne 0) {
    $detail = $out |
        Select-String -Pattern '\[FAIL\]|Error Message:|Assert\.|Exception|Stack Trace:|Expected:|Actual:' |
        ForEach-Object { $_.Line } |
        Select-Object -First 40                            # bound the block so it fits the ~60-line tail
    Write-Output ""
    Write-Output "=== Failure details (re-emitted so they land in the harness feedback tail) ==="
    if ($detail) { $detail | ForEach-Object { Write-Output $_ } }
    else { Write-Output "(no assertion/exception lines matched - inspect the full log above)" }
    Write-Output "SampleVerifierWiringTests failing - PlanPreflightPhase does not halt on a bad sample pair, halts a sound one, does not halt a plan that declares no preflights/ folder, does not journal which pair halted, or the run does not stop before scheduling any task (see failure details above)"
    exit 1
}

# PRECONDITION - the ONE legitimate early exit past this point. No TRX means the run never happened
# (host failed to start, wrong project path, malformed --filter which exits 0 SILENTLY). Diagnose THAT.
# It also subsumes the #455 zero-match guard: a filter that matched nothing produces a TRX with zero
# results, reported below as its own diagnosis rather than as "every behaviour unbound".
$trx = Get-ChildItem $resultsDir -Filter *.trx -Recurse -ErrorAction SilentlyContinue |
       Sort-Object LastWriteTime | Select-Object -Last 1
if (-not $trx) {
    Write-Output "exit 0 but no .trx under $resultsDir - the test run did not happen (test host failed to start, wrong project path, or a malformed --filter, which exits 0 with no results). This guardrail certified nothing. This is NOT a finding about the tests: do NOT rewrite them."
    exit 1
}

# DOTTED navigation - the TRX has a default xmlns, so SelectNodes('//UnitTestResult') finds nothing.
$xml = [xml](Get-Content $trx.FullName -Raw)
# The `| Where-Object { $_ }` is LOAD-BEARING. Without it this guard is INERT. MEASURED on this box,
# 2026-08-29 (pwsh 7):
#     @($null).Count                                      -> 1
#     @([xml]'<TestRun/>').TestRun.Results.UnitTestResult  -> $null, and @($null) is a ONE-element array
#     @(...same... | Where-Object { $_ }).Count            -> 0
#   A TRX with no <Results> element (or an empty one) yields $null, so the bare
#   `@($xml.TestRun.Results.UnitTestResult).Count -lt 1` form can NEVER fire and a zero-test run would
#   fall through to the census below, which would then report all five behaviours "unbound" - a
#   confident wrong message aimed at the one artifact a retry agent is allowed to edit. The filtered
#   form fires.
$recorded = @($xml.TestRun.Results.UnitTestResult | Where-Object { $_ })
if ($recorded.Count -lt 1) {
    Write-Output "exit 0 but the TRX records ZERO executed tests - the --filter '$filter' matched nothing, or every match is [Skip]ped out of execution. This guardrail certified nothing. Check the filter against the class this task owns (SampleVerifierWiringTests, trait Category=BacklogSlate)."
    exit 1
}

# THE FORWARD PER-TEST CENSUS (#375, the `-ne 'Passed'` mirror). The suite exit code cannot tell a
# behaviour that PASSED from one that was never authored or was [Skip]ped out. Task 04's red census
# would catch a MISSING test at authoring time, but this task's segment could still merge a tree where
# one was lost, and a [Skip] added later reads as green to the suite exit code. Each enumerated
# behaviour -> the test method name the ACTION PROMPT PINNED for it. Cross-checked BY HAND against
# tasks/04-author-tests-verifier-wiring/action.prompt.md (which pins the names) and
# tasks/05-wire-verifier-into-preflight/action.prompt.md (which restates them); the prompt<->manifest
# agreement is NOT mechanically enforced (measured: validate exits 0 either way).
$manifest = [ordered]@{
    'a reversed committed pair HALTS the pre-DAG phase'              = 'EvaluateAsync_ReturnsFalse_WhenACommittedSamplePairIsReversed'
    'it halts even for a plan with NO preflights/ folder (placement)' = 'EvaluateAsync_HaltsOnABadSamplePair_EvenWhenThePlanDeclaresNoPreflightsFolder'
    'a SOUND pair does not halt (the step is not unconditional)'      = 'EvaluateAsync_ReturnsTrue_WhenEverySamplePairIsSound'
    'the halt names the failing pair in the journal (#432)'           = 'EvaluateAsync_JournalsTheFailingPair_SoAPostMortemReaderCanSeeWhichPairHalted'
    'the RUN stops before scheduling any task - zero attempts'        = 'Run_HaltsBeforeSchedulingAnyTask_WhenAPlansCommittedSamplePairIsReversed'
}

# ACCUMULATE (#179): one distinguishable message per unbound behaviour, so ONE attempt learns every gap.
$failures = @()
foreach ($behaviour in $manifest.Keys) {
    $name = $manifest[$behaviour]
    # -cmatch: C# method names are case-SENSITIVE and PowerShell -match is not.
    # The (\(|$) tail admits a [Theory] row's appended data without admitting a longer sibling name.
    $pattern = '\.' + [regex]::Escape($name) + '(\(|$)'
    $hits    = @($recorded | Where-Object { $_.testName -cmatch $pattern })
    if ($hits.Count -lt 1) {
        $failures += "$behaviour -> no test named '$name' ran (absent from the file, or not selected by the filter). The suite exiting 0 does not mean this behaviour is proven; it means nothing asserted it."
        continue
    }
    $notGreen = @($hits | Where-Object { $_.outcome -ne 'Passed' })
    if ($notGreen.Count -gt 0) {
        $seen = (($notGreen | ForEach-Object { $_.outcome } | Sort-Object -Unique) -join '/')
        $failures += "$behaviour -> '$name' is $seen, not Passed. ('NotExecuted' = [Fact(Skip=...)] - skipping the placement test is exactly how the sample step ends up after the 'PlanPreflights.Count == 0' early return with every other test still green.)"
    }
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== wiring census: $($failures.Count) of $($manifest.Count) enumerated behaviours are not proven by a PASSING test ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    exit 1
}
exit 0
