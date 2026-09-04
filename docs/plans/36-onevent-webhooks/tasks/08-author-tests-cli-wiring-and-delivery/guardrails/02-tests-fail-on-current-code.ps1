# catches: a HOLLOW test - named for the behaviour, body a tautology (Assert.True(true), Assert.NotNull
#          on a value the test itself constructed, a foreach over a collection the test never proved
#          non-empty). It PASSES against the unwired CLI and hides behind its genuinely-failing siblings,
#          so a suite-level non-zero exit certifies the file honest (#375). One entry per enumerated
#          behaviour, each observed Failed in the runner's OWN TRX - never merely discovered by name,
#          which a hollow body satisfies exactly as a comment satisfies a token floor.
#
#          The vacuity hazard is unusually live in THIS file. Five of these ten behaviours are naturally
#          written as "every delivered body has property P" or "no delivered body contains X" - both of
#          which are TRUE over the empty set, and the empty set is precisely the state of the tree this
#          task authors against (the options are declared, nothing delivers). The action prompt therefore
#          requires a non-empty assertion FIRST in each of those; this census is what proves the agent
#          actually wrote it.
#
# DECLARED EXEMPTION: 'AReceiverThatNeverBindsLeavesExitCodeUntouched' - its entire content is that the
#          delivery mechanism does NOT affect the run, and a mechanism that does nothing at all satisfies
#          that exactly. It is green on this stub tree AND green on a correct implementation, so no
#          honest version of it can be RED here. The row asserts Expect='Executed' (it ran, and was not
#          [Skip]ped) rather than Failed; it stays IN the manifest because a dropped row and an oversight
#          look identical, and this is the contrast case that makes "delivery never affects the verdict"
#          a real assertion rather than an untested claim.
#
# NOT exempt, and worth stating because it looks like it should be:
#          'AFiveHundredCausesRetriesThenARecordedDropWithExitCodeUnchanged' also carries an
#          exit-code-unchanged clause that is trivially true here - but its other two clauses (retries
#          observed on one delivery id, a Webhook: summary line reporting a nonzero drop count) cannot be
#          satisfied by a CLI that never POSTs. It is required RED.
#
# Culture pin: this census reads the TRX (schema tokens, NOT localized), so the guard does not depend on
# it - kept so the logged summary is readable and the pair stays copy-pasteable with the forward half in
# tasks/09-implement-cli-wiring/guardrails/02-webhook-delivery-tests-pass.ps1.
# Measured baseline (#478): n/a - a per-test outcome census over a TRX, no required-present source clause.
$env:DOTNET_CLI_UI_LANGUAGE = 'en'
$filter = 'Category=RunEvents&FullyQualifiedName~WebhookDeliveryTests'   # SAME string as the pair's forward half
# ~WebhookDeliveryTests is DISCRIMINATING and it was MEASURED, not assumed: "WebhookDeliveryTests" occurs
# ZERO times across src/ and tests/ on the starting tree. The nearest sibling this plan creates,
# WebhookEventSinkTests (task 06), lives in a DIFFERENT project (Guardrails.Core.Tests) and does not
# contain this substring in either direction.
# NEVER the bare Plan trait in a task-level filter (#455): Plan=36-onevent selects every test this whole
# plan authors, so this task could not settle until tasks that DEPEND on it had run.

# THE MANIFEST: each enumerated behaviour -> the test method name the ACTION PROMPT PINS for it.
# A BARE STRING means Expect='Failed'. A HASHTABLE declares an EXEMPTION (Expect='Executed') for a row a
# CORRECT implementation leaves GREEN on the stub tree - never DROP such a row, an undeclared omission is
# indistinguishable from an oversight.
$manifest = [ordered]@{
    'rows reach a real loopback receiver at all'                                = 'RowsArriveAtALoopbackReceiver'
    'the terminal run-finished row arrives (the plan-35 assertion that did not exist)' = 'RunFinishedArrives'
    'run-finished still arrives when the receiver is slow enough to back the pump up'  = 'RunFinishedArrivesWhenTheReceiverIsSlow'
    'delivered bodies match the events.jsonl lines byte-for-byte'               = 'DeliveredBodiesMatchEventsJsonlLineForLine'
    'the headers are exactly the section 4.3 contract'                          = 'HeadersAreExactlyTheContract'
    'detail is withheld from the wire without the flag'                         = 'DetailIsWithheldWithoutTheFlag'
    'detail is present on the wire with the flag'                               = 'DetailIsPresentWithTheFlag'
    'a 500 causes retries then a RECORDED drop, exit code unchanged'            = 'AFiveHundredCausesRetriesThenARecordedDropWithExitCodeUnchanged'
    'the env fallbacks supply the endpoint and its auth when no flag is passed' = 'EnvVarSuppliesTheEndpointWhenTheFlagIsAbsent'
    'an endpoint that never binds leaves the exit code untouched (contrast case)' = @{ Name = 'AReceiverThatNeverBindsLeavesExitCodeUntouched'; Expect = 'Executed' }
}

$resultsDir = Join-Path ([System.IO.Path]::GetTempPath()) "guardrails-webhook-census-$PID"
Remove-Item $resultsDir -Recurse -Force -ErrorAction SilentlyContinue   # never read a PREVIOUS attempt's TRX
# NO --no-build, deliberately: with it a census reads whatever is in bin/ rather than the SOURCE tree,
# and a census that can certify a deleted or stale file certifies nothing. Guardrail 01 normally
# refreshes it first, but a single-guardrail `revalidate` re-runs this out of order.
# NO -v q: it suppresses the Error Message / Expected / Actual / Stack Trace block, which is what makes a
# red census actionable rather than a list of names.
$out = dotnet test tests/Guardrails.Integration.Tests --filter $filter --nologo `
       --logger 'trx;LogFileName=census.trx' --results-directory $resultsDir 2>&1
$out | ForEach-Object { Write-Output $_ }

# PRECONDITION - the ONE legitimate early exit, and it comes FIRST because this guardrail has INVERSE
# polarity (#455): a non-zero exit is the EXPECTED state here, so the exit code cannot distinguish "nine
# tests failed as designed" from "the host crashed". Only the TRX can. No TRX means the run never happened
# (host failed to start, wrong project path, or a malformed --filter, which exits 0 SILENTLY). Diagnose
# THAT; falling through would print "every behaviour unbound", a confident wrong message aimed at the one
# artifact a retry agent may edit.
$trx = Get-ChildItem $resultsDir -Filter *.trx -Recurse -ErrorAction SilentlyContinue |
       Sort-Object LastWriteTime | Select-Object -Last 1
if (-not $trx) {
    Write-Output "no .trx under $resultsDir - the test run did not happen (test host failed to start, wrong project path, or a malformed --filter, which exits 0 with no results). This is NOT a finding about the tests: do NOT rewrite them. If the log above shows compiler errors, fix those."
    exit 1
}

# DOTTED navigation - the TRX has a default xmlns, so SelectNodes('//UnitTestResult') finds nothing.
# The Where-Object is NOT decoration: with zero tests executed the TRX has NO <Results> element, the
# navigation yields $null, and @($null).Count is 1 - so the bare @(...) form makes this guard evaluate
# 1 -lt 1 and NEVER FIRE. Measured on PowerShell 7: @($null).Count -> 1, @($null | Where-Object { $_ }).Count -> 0.
$xml      = [xml](Get-Content $trx.FullName -Raw)
$recorded = @($xml.TestRun.Results.UnitTestResult | Where-Object { $_ })
if ($recorded.Count -lt 1) {
    Write-Output "the TRX records ZERO executed tests - the --filter '$filter' matched nothing, or every match is [Skip]ped out of execution. The class name, one of the two required traits, or the test project path is wrong. This is NOT a finding about the tests' CONTENT: do NOT rewrite their bodies."
    exit 1
}

# ACCUMULATE (#478): one distinguishable message per unbound behaviour, dumped ONCE below, so a single
# attempt learns every gap instead of discovering them one retry at a time.
$failures = @()
foreach ($behaviour in $manifest.Keys) {
    $entry   = $manifest[$behaviour]
    $name    = if ($entry -is [string]) { $entry }   else { $entry.Name }
    $expect  = if ($entry -is [string]) { 'Failed' } else { $entry.Expect }
    # -cmatch: C# method names are case-SENSITIVE and PowerShell -match is not.
    # The (\(|$) tail admits a [Theory] row's appended data without admitting a longer sibling name.
    $pattern = '\.' + [regex]::Escape($name) + '(\(|$)'
    $hits    = @($recorded | Where-Object { $_.testName -cmatch $pattern })
    if ($hits.Count -lt 1) {
        $failures += "$behaviour -> no test named '$name' ran (absent from the file, or not selected by the filter). Both traits are required: [Trait(`"Category`",`"RunEvents`")] AND [Trait(`"Plan`",`"36-onevent`")]."
        continue
    }
    if ($expect -eq 'Executed') {
        $notRun = @($hits | Where-Object { $_.outcome -eq 'NotExecuted' -or [string]::IsNullOrEmpty($_.outcome) })
        if ($notRun.Count -gt 0) {
            $failures += "$behaviour -> '$name' is a DECLARED EXEMPTION (Expect='Executed' - see this file's header for why no honest version of it can be red here) and did NOT execute. 'NotExecuted' means [Fact(Skip=...)]. An exempt row still has to run; skipping it turns the exemption into no coverage at all."
        }
        continue
    }
    $notRed = @($hits | Where-Object { $_.outcome -ne 'Failed' })
    if ($notRed.Count -gt 0) {
        $seen = (($notRed | ForEach-Object { $_.outcome } | Sort-Object -Unique) -join '/')
        $failures += "$behaviour -> '$name' is $seen on the UNWIRED tree, not Failed. Nothing POSTs anywhere yet, so a test that passes here never observed a delivery: it is asserting over an empty set (a foreach with no non-empty check, or a 'no body contains X' negative), or it never drove the CLI at all. Assert FIRST that at least one request arrived, then assert the property. ('NotExecuted' = [Fact(Skip=...)].)"
    }
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== per-test red census: $($failures.Count) of $($manifest.Count) enumerated behaviours are not proven RED on the unwired CLI ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    exit 1
}
exit 0
