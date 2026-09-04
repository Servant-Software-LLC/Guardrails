# catches: an implementation whose behavior deviates from the tests THIS task pair owns - and, at its
#          strongest, the #382 defect the whole pair exists for: a WebhookEventSink that is fully built
#          and fully unit-tested (tasks 06/07, all green) while the CLI composition root never constructs
#          it, so the feature is reachable only from xUnit and inert from `guardrails run`. Every test
#          here drives the REAL CLI against a REAL loopback HttpListener, so only the wired path can
#          satisfy it.
#
#          It is also the forward half of a real TDD pair. Task 08 authored these thirteen methods and
#          proved twelve of them RED against the unwired CLI
#          (tasks/08-author-tests-cli-wiring-and-delivery/guardrails/02-tests-fail-on-current-code.ps1);
#          this census requires all THIRTEEN Passed after the wiring lands - including
#          AReceiverThatNeverBindsLeavesExitCodeUntouched, which is task 08's declared exemption from the
#          red bar (nothing delivers there, so "delivery did not affect the run" is trivially true) and
#          is NOT exempt here: it is the only thing standing between this task and a dispatcher that
#          quietly changes the run's exit code when the endpoint is dead. The two manifests name the same
#          thirteen methods and must stay in lockstep.
#
#          Behaviours 11-13 are the SS6.4/SS6.5 startup-validation surface: a non-http(s) scheme, a
#          repeated --on-event, and CR/LF in GUARDRAILS_ON_EVENT_AUTH. Before they were added, that
#          surface had no guardrail clause and no test name anywhere in this plan, and omitting all of it
#          shipped FULLY GREEN - new Uri("ftp://x") parses, TryStart accepts it, the POST throws
#          NotSupportedException, IsRetryable calls that transient, the sink retries and records a drop,
#          and the exit code is untouched BY CONTRACT. Each of the three requires ExitCodes.HarnessError
#          (1) AND that <plan>/state/run.json was never created, which is SS6.5's real requirement: an
#          invalid endpoint must never surface mid-run. That second clause is what pins the check ABOVE
#          RunJournal.LoadOrCreate rather than merely somewhere in RunAsync.
#
#          The suite exit code alone cannot tell a behaviour that PASSED from one that was never merged
#          in or was [Skip]ped out. This task's writeScope is src/Guardrails.Cli/ only, so its agent
#          cannot drop a test - but a merge can, and a lost test reads as green to an exit code. The
#          per-test census below binds each behaviour to an OBSERVED PASSING test by name.
#
#          scope: LOCAL (no sidecar) - the key is DELIBERATELY OMITTED. This asserts "the component works
#          through the seam the run actually drives", which CANNOT be true before this task's own action
#          has run, so it fails the #125 union-safe test and must NOT be tagged scope:"integration" - that
#          is the #250 mistake, and it would deadlock this task behind its own dependents.
#
#          Re-emits the runner's whole failure BLOCK at the END so it reaches the harness's ~60-line
#          retry-feedback tail (#179), using the block-capture form (#608) rather than a line
#          allowlist - see the comment at the capture itself for what an allowlist drops here.
#
# ORDERING (cheapest-first, #478 rule 4). Guardrail 01 is a ~1s source grep over ONE file; this is a
#          ~2min integration run. Under guardrailMode failFast, 01 has ALREADY PASSED whenever this
#          script executes at all - so by the time anyone reads a failure here, the composition root
#          provably CALLS WebhookEventSink.TryStart and the fault is behavioral rather than a missing
#          construction. The failure message below says exactly that, so the agent does not re-check the
#          thing that is already proven.
#
# Measured baseline (#478): n/a - exit-code + executed-count + per-test outcome census. No required-present
#          source clause. (WebhookDeliveryTests occurs ZERO times across src/ and tests/ on the starting
#          tree; task 08 creates it.)
$env:DOTNET_CLI_UI_LANGUAGE = 'en'   # the run summary the zero-match guard reads is LOCALIZED (#455); the TRX is not
$filter = 'Category=RunEvents&FullyQualifiedName~WebhookDeliveryTests'
# ~WebhookDeliveryTests is DISCRIMINATING (#455/#193) and it was MEASURED: zero pre-existing classes
# anywhere in src/ or tests/ contain that substring, and this plan's other new class,
# WebhookEventSinkTests, lives in a different project and does not contain it.
# NEVER the bare Plan trait in a task-level filter (#455): Plan=36-onevent selects every test this whole
# plan authors, so this task could not settle until tasks that DEPEND on it had run - a deadlock
# `validate` and `graph --check` cannot see.
#
# NO --no-build, deliberately, and it was MEASURED that this matters: with it a test guardrail reads
# whatever is in bin/ rather than the SOURCE tree, and a sibling census in an earlier plan was observed
# exiting 0 over five STALE tests still compiled into the assembly after their source file had been
# deleted. A single-guardrail `revalidate` re-runs this out of order, so it cannot rely on a build
# guardrail having gone first.
# NO -v q on the TEST command (#462): it suppresses the WHOLE failure block - the `Failed <name>` header,
# `Error Message:`, the assertion line, `Stack Trace:` and its frames - leaving only "[FAIL] <name>", so
# the block capture below starts on nothing and re-emits nothing. The guardrail would still fail and
# still name the failing tests; only the WHY would be gone, with nothing in the output saying so. A
# correctly-written re-emit is voided by one flag on the line above it. `validate` rejects the pair
# mechanically (GR2037 entry #462).
$resultsDir = Join-Path ([System.IO.Path]::GetTempPath()) "guardrails-webhook-forward-$PID"
Remove-Item $resultsDir -Recurse -Force -ErrorAction SilentlyContinue   # never read a PREVIOUS attempt's TRX
$out = dotnet test tests/Guardrails.Integration.Tests --filter $filter --nologo `
       --logger 'trx;LogFileName=webhook.trx' --results-directory $resultsDir 2>&1
$testExit = $LASTEXITCODE                                  # capture BEFORE any other statement
$out | ForEach-Object { Write-Output $_ }                  # full log first (for the attempt's saved output)
$text = $out | Out-String

# ACCUMULATE (#478): one distinguishable message per clause, dumped ONCE at the end, so a single attempt
# learns every gap. The two early exits below are PRECONDITIONS - they dump what has accumulated and stop
# because everything after them would report a confident wrong message.
$failures = @()

# EXIT CODE FIRST, guard second (#455, forward polarity): a test host that never ran exits NON-zero with
# no summary, so checking the exit code first reports its real error instead of blaming the filter.
if ($testExit -ne 0) {
    # BLOCK capture, not a line allowlist (#608) - the plan-breakdown SSOT form, references/stacks/
    # dotnet.md 4.2. Start at a failure header, stop at the run summary, take everything between.
    #
    # WHY THIS FILE SPECIFICALLY, and it is not cosmetic. Two re-emit patterns were in circulation and
    # each failed in the OPPOSITE direction: the old allowlist
    # ('\[FAIL\]|Error Message:|Assert\.|Exception|Stack Trace:|Expected:|Actual:') has no `at ` branch,
    # so it kept `Stack Trace:` as a LABEL FOLLOWED BY NOTHING and dropped the String:/Found: payload of
    # a Contains/DoesNotContain failure; a drifted variant kept the frames and dropped the assertion
    # headline plus a thrown test's only detail line. Both look correct on the page, which is how each
    # survived. This guardrail is the REAL-SEAM PROOF for the whole feature, so it is where losing them
    # costs the most: the stack FRAME is what says which of the thirteen integration tests broke and where,
    # and a DeliveredBodiesMatchEventsJsonlLineForLine failure re-emits the String:/Found: payload
    # carrying the actual divergent bytes - the one thing that makes a byte-equality failure diagnosable
    # at all. An allowlist needs maintenance as assertion types change; a block capture does not.
    #
    # `error CS` is folded in as a THIRD start condition rather than dropped, because this same pipeline
    # serves the compile-failure path (no --no-build, so a broken tree surfaces here). A build failure
    # has no `Passed!`/`Failed!` terminator, so emit simply latches on to EOF and the bound below caps
    # it - which is the right shape: the error lines plus the `N Error(s)` summary all reach the tail.
    $detail = @()
    $emit = $false
    foreach ($line in $out) {
        if ($line -match '^\s*Failed\s+\S' -or $line -match '^\s*Error Message:' -or $line -match 'error CS') { $emit = $true }
        elseif ($line -match '^(Passed!|Failed!)') { $emit = $false }
        if ($emit) { $detail += $line }
    }
    $detail = $detail | Select-Object -First 40             # bound the block so it fits the ~60-line tail
    Write-Output ""
    Write-Output "=== Failure details (re-emitted so they land in the harness feedback tail) ==="
    if ($detail) { $detail | ForEach-Object { Write-Output $_ } }
    else { Write-Output "(no failure block matched - the runner's output format may have changed; inspect the full log above)" }
    if ($text -match 'error CS') {
        $failures += "the log contains 'error CS' - this is a COMPILE failure, not a test failure. Fix the compiler errors above; do not touch the tests (they are outside your write scope)."
    }
    $failures += "WebhookDeliveryTests is failing - the CLI does not deliver events to the endpoint the way the tests require (see failure details above). Guardrail 01-composition-root-constructs-the-sink.ps1 ran BEFORE this one and PASSED, so RunCommand.cs provably calls WebhookEventSink.TryStart: do not go looking for a missing construction. The fault is BEHAVIORAL - the sink is built but something about lifetime, teardown order, the onRow/includeDetail threading, the env fallbacks, the startup validation and its PLACEMENT, or the header/detail contract is wrong. Read the per-behaviour findings below: they name which of the thirteen behaviours is not proven."
}

# ZERO-MATCH GUARD (#455). Key on the EXECUTED count (Passed + Failed); 'Total:' would also count
# [Skip]ped tests, so a suite of thirteen skips would read as thirteen. PRECONDITION exit: with zero executed the
# census below would report all thirteen behaviours unbound, a confident wrong message aimed at a file this
# task may not even edit.
$ran = ([regex]::Matches($text, '(?:Passed|Failed):\s*(\d+)') |
        ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum
if ($ran -lt 1) {
    $failures += "ZERO tests executed - this guardrail certified nothing. The --filter '$filter' matched no tests, is malformed, or every matched test is [Skip]ped. WebhookDeliveryTests is authored by task 08 and carries BOTH [Trait(`"Category`",`"RunEvents`")] and [Trait(`"Plan`",`"36-onevent`")]; zero executed means the class did not arrive in this segment, not that it is empty."
    $failures | ForEach-Object { Write-Output "  - $_" }
    exit 1
}

# PRECONDITION: no TRX means the per-test census cannot run at all.
$trx = Get-ChildItem $resultsDir -Filter *.trx -Recurse -ErrorAction SilentlyContinue |
       Sort-Object LastWriteTime | Select-Object -Last 1
if (-not $trx) {
    $failures += "no .trx under $resultsDir - the test run did not produce results (test host failed to start, wrong project path, or a malformed --filter, which exits 0 with no results). This guardrail certified nothing."
    $failures | ForEach-Object { Write-Output "  - $_" }
    exit 1
}

# DOTTED navigation - the TRX has a default xmlns, so SelectNodes('//UnitTestResult') finds nothing.
# The `| Where-Object { $_ }` is LOAD-BEARING: a TRX with no <Results> element yields $null, and
# @($null).Count is 1, so the bare @(...) form can never fire. Measured on PowerShell 7:
#   @($null).Count -> 1 ; @($null | Where-Object { $_ }).Count -> 0
$xml      = [xml](Get-Content $trx.FullName -Raw)
$recorded = @($xml.TestRun.Results.UnitTestResult | Where-Object { $_ })

# THE FORWARD PER-TEST CENSUS (#375, the `-ne 'Passed'` mirror of task 08's red census). Each enumerated
# behaviour -> the test method name task 08's ACTION PROMPT PINS for it. Cross-checked BY HAND against
# tasks/08-author-tests-cli-wiring-and-delivery/action.prompt.md and against that task's own manifest in
# guardrails/02-tests-fail-on-current-code.ps1; the prompt<->manifest agreement is NOT mechanically
# enforced (measured: `validate` exits 0 either way).
$manifest = [ordered]@{
    'rows reach a real loopback receiver at all'                                       = 'RowsArriveAtALoopbackReceiver'
    'the terminal run-finished row arrives (the plan-35 assertion that did not exist)' = 'RunFinishedArrives'
    'run-finished still arrives when the receiver is slow enough to back the pump up'  = 'RunFinishedArrivesWhenTheReceiverIsSlow'
    'delivered bodies match the events.jsonl lines byte-for-byte'                      = 'DeliveredBodiesMatchEventsJsonlLineForLine'
    'the headers are exactly the section 4.3 contract'                                 = 'HeadersAreExactlyTheContract'
    'detail is withheld from the wire without the flag'                                = 'DetailIsWithheldWithoutTheFlag'
    'detail is present on the wire with the flag'                                      = 'DetailIsPresentWithTheFlag'
    'a 500 causes retries then a RECORDED drop, exit code unchanged'                   = 'AFiveHundredCausesRetriesThenARecordedDropWithExitCodeUnchanged'
    'the env fallbacks supply the endpoint and its auth when no flag is passed'        = 'EnvVarSuppliesTheEndpointWhenTheFlagIsAbsent'
    'an endpoint that never binds leaves the exit code untouched'                      = 'AReceiverThatNeverBindsLeavesExitCodeUntouched'
    # SS6.4/SS6.5 startup validation. Each asserts exit 1 AND that <plan>/state/run.json was never
    # created, so each one also pins the CHECK'S PLACEMENT - above RunJournal.LoadOrCreate, beside the
    # --autonomy parse - not merely its existence.
    'a non-http(s) scheme exits 1 before any run state is touched'                     = 'ABadSchemeExitsOneBeforeTheRun'
    'a repeated --on-event is DETECTED, not silently last-wins'                        = 'ARepeatedOnEventFlagIsRejected'
    'CR/LF in GUARDRAILS_ON_EVENT_AUTH is rejected without echoing the secret'         = 'ACrLfAuthValueIsRejected'
}

foreach ($behaviour in $manifest.Keys) {
    $name = $manifest[$behaviour]
    # -cmatch: C# method names are case-SENSITIVE and PowerShell -match is not.
    # The (\(|$) tail admits a [Theory] row's appended data without admitting a longer sibling name.
    $pattern = '\.' + [regex]::Escape($name) + '(\(|$)'
    $hits    = @($recorded | Where-Object { $_.testName -cmatch $pattern })
    if ($hits.Count -lt 1) {
        $failures += "$behaviour -> no test named '$name' ran (absent from this segment, or not selected by the filter). The suite exiting 0 does not mean this behaviour is proven; it means nothing asserted it. This test is OUTSIDE your write scope - if it genuinely did not arrive, that is a delivery problem: escalate with {`"needsHuman`": ...} rather than authoring it here."
        continue
    }
    $notGreen = @($hits | Where-Object { $_.outcome -ne 'Passed' })
    if ($notGreen.Count -gt 0) {
        $seen = (($notGreen | ForEach-Object { $_.outcome } | Sort-Object -Unique) -join '/')
        $failures += "$behaviour -> '$name' is $seen, not Passed. ('NotExecuted' = [Fact(Skip=...)].)"
    }
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== webhook delivery census: $($failures.Count) finding(s) across $($manifest.Count) enumerated behaviours ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    exit 1
}
exit 0
