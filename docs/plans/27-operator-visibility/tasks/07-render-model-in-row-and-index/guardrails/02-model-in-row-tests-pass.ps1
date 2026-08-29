# catches: an implementation whose behavior deviates from the tests THIS task pair owns - no Model
#          column in the run-level index, a page-wide model value instead of a per-task one, a
#          swallowed route mismatch, a never-run task inheriting its neighbour's model, no named link
#          to attempt-route.log, or ModelCell still throwing.
#          The --filter names this pair's OWN test class, never the plan-wide trait alone - a
#          trait-only filter asserts the state of every test in the plan, so this task could not go
#          green until a task that DEPENDS on it has run (a deadlock validate/graph --check cannot
#          see, #455). It is the SAME $filter string 06-author-tests-model-in-row's red census used,
#          copied verbatim, so
#          the two halves of the pair can never drift apart.
#          Re-emits the assertion/exception lines at the END so they reach the retry-feedback tail (#179).
#          scope: LOCAL (no sidecar) - it asserts "the model IS rendered", which cannot be true before
#          this task's own action has run, so it fails the #125 union-safe test and must not be tagged
#          scope:"integration" (#250).
#
# IT ALSO CARRIES A GREEN-POLARITY CENSUS OVER THE TWO GROUP B PINS, and that is not decoration - it is
# the only place in the plan those two are RUN by name. 06-author-tests-model-in-row authors them as
# regression pins that are GREEN on arrival, which is exactly why its own red census excludes them; the
# consequence, until this clause existed, was that a Group B pin could be authored hollow, or deleted,
# or left red, and NOTHING would ever notice. /guardrails-review found the same shape one level up:
# 05-raise-attempt-route-resolved's decorator guardrail declared a residual and DEFERRED it to
# "the task-06 pin", a pin that appeared in no guardrail script at all. A deferral to something nothing
# runs is not a deferral, it is a gap with a footnote. The suite exit code is NOT sufficient on its own:
# a pin that is absent, renamed or [Skip]ped leaves the suite green.
# The pins, and what each is for:
#   RunLevelIndex_StillCarriesTaskStatusAndDescription_SoTheModelColumnIsAdditive
#       - the index's existing Task/Status/Description columns, its link-vs-plain-text rule and its
#         data-status attribute all still work. This task ADDS a column; it changes none of them.
#   BothDecorators_ForwardAttemptRouteResolved_ToTheirInnerObserver
#       - each transparent decorator still forwards the launch-time route event, in BOTH shapes
#         (requestedTier present, requestedTier null). This is the RUNTIME half of what
#         05-raise-attempt-route-resolved's source grep can only see textually: a decorator forwarding
#         with runner/model transposed or requestedTier hard-coded null compiles, satisfies a
#         call-anchored grep, and destroys the section 6.2 climb signal.
$env:DOTNET_CLI_UI_LANGUAGE = 'en'    # the run summary the guard reads is LOCALIZED (#455)
$filter = 'Category=BacklogSlate&FullyQualifiedName~ModelInRowTests'
$resultsDir = Join-Path ([System.IO.Path]::GetTempPath()) "guardrails-modelinrow-$PID"
Remove-Item $resultsDir -Recurse -Force -ErrorAction SilentlyContinue   # never read a PREVIOUS attempt's TRX
# NO -v q on the TEST command: it suppresses the Error Message/Expected/Actual/Stack Trace block,
# leaving only "[FAIL] <name>" for the re-emit below to find - which defeats #179 by the flag alone.
$out = dotnet test tests/Guardrails.Integration.Tests --filter $filter --no-build --nologo `
       --logger 'trx;LogFileName=modelinrow.trx' --results-directory $resultsDir 2>&1
$testExit = $LASTEXITCODE                                  # capture BEFORE any other statement
$out | ForEach-Object { Write-Output $_ }                  # full log first (for the attempt's saved output)

# EXIT CODE FIRST, guard second (#455): a test host that never ran exits NON-zero with no summary,
# so checking the exit code first reports its real error instead of blaming the filter.
if ($testExit -ne 0) {
    $detail = $out |
        Select-String -Pattern '\[FAIL\]|Error Message:|Assert\.|Exception|Stack Trace:|Expected:|Actual:' |
        ForEach-Object { $_.Line } |
        Select-Object -First 40                            # bound the block so it fits the ~60-line tail
    Write-Output ""
    Write-Output "=== Failure details (re-emitted so they land in the harness feedback tail) ==="
    if ($detail) { $detail | ForEach-Object { Write-Output $_ } }
    else { Write-Output "(no assertion/exception lines matched - inspect the full log above)" }
    Write-Output "ModelInRowTests failing - the model is not rendered per task in the run-level index, a route mismatch is not disclosed, a never-run task's cell is wrong, attempt-route.log is still not LINKED by name with a label, or LiveRunObserver.ModelCell is not implemented (see failure details above)"
    exit 1
}

# ZERO-MATCH GUARD (#455): exit 0 alone does NOT mean tests passed - a --filter that matches nothing,
# or is malformed, also exits 0. Key on the EXECUTED count (Passed+Failed; "Total:" would also count
# [Skip]ped tests), never on "No test matches ..." (verbosity-dependent, so it never fires - #248).
$ran = ([regex]::Matches(($out | Out-String), '(?:Passed|Failed):\s*(\d+)') |
        ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum
if ($ran -lt 1) {
    Write-Output "exit 0 but ZERO tests executed - this guardrail certified nothing. The --filter '$filter' matched no tests, is malformed, or every matched test is [Skip]ped. Check it against the class this task pair owns (ModelInRowTests, trait Category=BacklogSlate, in tests/Guardrails.Integration.Tests/ModelTiering/)."
    exit 1
}

# ─────────────────────────────────────────────────────────────────────────────────────────────────
# GREEN-POLARITY CENSUS over the TWO GROUP B PINS (the mirror of 06's red census, #375).
# A suite exit code fires if ANY selected test fails; it says nothing about whether a NAMED test ran
# at all. So a Group B pin that was never authored, was renamed, or is [Skip]ped leaves this guardrail
# green - which is how a regression pin becomes decoration. Each name below must be present in the
# runner's OWN result file with outcome 'Passed'.
$expectedGreen = [ordered]@{
    'the index still carries Task/Status/Description - the Model column is ADDITIVE' =
        'RunLevelIndex_StillCarriesTaskStatusAndDescription_SoTheModelColumnIsAdditive'
    'both transparent decorators still forward AttemptRouteResolved, in BOTH shapes' =
        'BothDecorators_ForwardAttemptRouteResolved_ToTheirInnerObserver'
}

# PRECONDITION - a missing TRX means the census cannot be evaluated at all. Diagnose THAT rather than
# reporting both pins as absent, which would be a confident wrong message aimed at a file this task may
# not even edit.
$trx = Get-ChildItem $resultsDir -Filter *.trx -Recurse -ErrorAction SilentlyContinue |
       Sort-Object LastWriteTime | Select-Object -Last 1
if (-not $trx) {
    Write-Output "the suite passed but no .trx was written under $resultsDir, so the Group B green census could not run. This is NOT a finding about the implementation: check the --logger argument on the dotnet test line above."
    exit 1
}

# The `| Where-Object { $_ }` is LOAD-BEARING and MEASURED: PowerShell's dotted navigation over a TRX
# with no <UnitTestResult> children yields ONE element that is $null, so a bare @(...) counts 1 and any
# emptiness check silently never fires. MEASURED 2026-08-29 against a real tool-produced zero-result
# TRX (dotnet test writes NO <Results> element at all when the filter matches nothing): @(...).Count = 1
# without the filter, 0 with it.
$xml      = [xml](Get-Content $trx.FullName -Raw)
$recorded = @($xml.TestRun.Results.UnitTestResult | Where-Object { $_ })

# ACCUMULATE (#179): one distinguishable message per pin, so ONE attempt learns every gap.
$censusFailures = @()
foreach ($what in $expectedGreen.Keys) {
    $name = $expectedGreen[$what]
    # -cmatch: C# method names are case-SENSITIVE and PowerShell -match is not.
    # The (\(|$) tail admits a [Theory] row's appended data without admitting a longer sibling name.
    $pattern = '\.' + [regex]::Escape($name) + '(\(|$)'
    $hits    = @($recorded | Where-Object { $_.testName -cmatch $pattern })
    if ($hits.Count -lt 1) {
        $censusFailures += "$what -> no test named '$name' ran. It is one of the two Group B regression pins 06-author-tests-model-in-row was asked to author; it is absent from ModelInRowTests, was renamed, or is not selected by the filter. Do NOT add it here - ModelInRowTests.cs is outside this task's write scope. If it is genuinely missing, that is a needsHuman with the two quotes the harness contract asks for."
        continue
    }
    $notGreen = @($hits | Where-Object { $_.outcome -ne 'Passed' })
    if ($notGreen.Count -gt 0) {
        $seen = (($notGreen | ForEach-Object { $_.outcome } | Sort-Object -Unique) -join '/')
        $censusFailures += "$what -> '$name' is $seen, not Passed. ('NotExecuted' = [Fact(Skip=...)], which makes the pin decoration.) These two pins were GREEN before this task ran - they pin existing behaviour that this task must not break. Fix LiveRunObserver.cs / LogSiteRenderer.cs / ConsoleRunObserver.cs, not the test."
    }
}

if ($censusFailures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== Group B green census: $($censusFailures.Count) of $($expectedGreen.Count) regression pins are not proven Passed ==="
    $censusFailures | ForEach-Object { Write-Output "  - $_" }
    exit 1
}
exit 0
