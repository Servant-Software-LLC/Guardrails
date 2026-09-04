# catches: a HOLLOW red. A suite-level non-zero exit fires if ANY selected test fails, so an
#          Assert.True(true) body - or a bounds test that only reads the named constants task 04
#          already landed, or a DisposeAsync stubbed as ValueTask.CompletedTask, which makes both
#          "never throws" tests pass - hides behind its genuinely-failing siblings. This is the
#          PER-TEST CENSUS (#375): every enumerated behaviour is bound to a PINNED test method name
#          and its outcome is read out of the runner's own TRX - never stdout (#248), never
#          --list-tests name discovery, which a hollow body satisfies exactly as a comment satisfies
#          a token floor.
# Boundary, stated because a green census must not be over-read: this proves each test is COUPLED to
#          the code path (it fails while the behaviour is absent), NOT that its assertion is correct.
#          An invoking-then-hollow test is red here, green after, and passes.
# DECLARED EXEMPTIONS: NONE - and that is a claim, not an omission. Every one of the sixteen pinned
#          behaviours drives Emit, TryStart, the internal test constructor, DisposeAsync or the two
#          internal readbacks, and this task lands every one of them as
#          `throw new NotImplementedException()`. THREE bounds tests
#          (BackoffScheduleIsOneTwoFourWithJitter, PerRowCeilingIsFortyFiveSeconds,
#          ResponseBodyIsCappedAtEightKilobytes) are the rows that COULD have been green-on-stub,
#          because the constants they read already exist - the prompt requires each to assert the
#          enforced BEHAVIOUR as well as the constant, which is what keeps them red. That is
#          deliberately stronger than exempting them: an exemption would have let a constant-only
#          assertion stand for an unenforced bound. If a later edit makes a row green-against-the-stub
#          AND green-against-a-correct-implementation, ADD it here with its structural reason; never
#          delete the row.
# THE ONE ROW THAT WITNESSES PRODUCTION CONSTRUCTION: TryStartBuildsANonRedirectingClientAtRealTimeScale
#          is the ONLY pinned behaviour that calls the real TryStart - every other test in the file
#          takes the internal constructor and substitutes both the handler and the timeScale away. So
#          it is the single witness for all three of TryStart's production-only decisions:
#          AllowAutoRedirect=false (SS6.5 - a redirect can move the POST, with its Authorization
#          header, to a host the operator never named), timeScale=1.0 (a stray debugging 0.001 ships a
#          10 ms per-attempt timeout), and the fact that TryStart is what sets them. Without this row
#          both values could be wrong in production while every unit test here and every integration
#          test in task 08 stayed green - the unit tests inject their own, and the integration tests
#          would merely run FASTER. Deleting or weakening this row silently restores that hole.
# Measured baseline (authoring time): WebhookEventSinkTests matches 0 tests in src/ and tests/ today
#          (the class does not exist), and Category=RunEvents in Guardrails.Core.Tests selects 41
#          passing tests. The zero-match precondition below is therefore ARMED, not pre-satisfied.
# INVERSE POLARITY: the suite exit code is deliberately never read - red IS the deliverable here, so
#          exit 1 from dotnet test carries no information. The preconditions run FIRST so that a
#          crashed or never-started run is reported as a crash, not certified as TDD red.
$ErrorActionPreference = 'Continue'
$env:DOTNET_CLI_UI_LANGUAGE = 'en'

# Discriminating filter (verified at authoring time): "WebhookEventSinkTests" is not a substring of
# "WebhookPolicyTests" and vice versa, so this selects THIS pair's class and nothing else - task
# 04/05's policy class is not swept in. The Category clause is doubly useful: it also proves the
# [Trait("Category","RunEvents")] that task 07's tests-pass filter depends on was actually applied.
$filter = "Category=RunEvents&FullyQualifiedName~WebhookEventSinkTests"
$trxDir = Join-Path ([System.IO.Path]::GetTempPath()) ("gr36-dispatcher-census-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force $trxDir | Out-Null
try {
    $log = & dotnet test tests/Guardrails.Core.Tests/Guardrails.Core.Tests.csproj `
        --filter $filter --nologo --logger "trx;LogFileName=census.trx" --results-directory $trxDir 2>&1 | Out-String
    Write-Output $log

    $trx = Join-Path $trxDir 'census.trx'
    if (-not (Test-Path -LiteralPath $trx)) {
        Write-Output "PRECONDITION: no TRX at $trx - the test run did not happen (the host failed to start, or the project failed to build). This is NOT a report about unbound behaviours."
        exit 1
    }

    [xml]$xml = Get-Content -LiteralPath $trx -Raw
    # #455: with zero executed tests the TRX carries no <Results>, the dotted navigation yields $null,
    # and @($null).Count is 1 - so filter the nulls out or the guard below can never fire.
    $results = @($xml.TestRun.Results.UnitTestResult | Where-Object { $_ })
    if ($results.Count -lt 1) {
        Write-Output "PRECONDITION: the filter '$filter' executed ZERO tests. Either the class WebhookEventSinkTests was never authored / was renamed, or its methods are missing [Trait(`"Category`",`"RunEvents`")]. A census over an empty set certifies nothing."
        exit 1
    }

    $problems = New-Object System.Collections.Generic.List[string]

    $mustFail = @(
        'BackoffScheduleIsOneTwoFourWithJitter',
        'PerRowCeilingIsFortyFiveSeconds',
        'CircuitOpensAtExactlyFiveConsecutiveFailures',
        'CircuitNeverCloses',
        'FullQueueDropsTheOldestNotTheNewest',
        'EveryDroppedRowIsCounted',
        'DisposeAsyncNeverThrowsWhenTheNoticeSinkThrows',
        'DisposeAsyncNeverThrowsWhenTheTransportThrows',
        'TerminalRowIsAttemptedWithTheCircuitOpen',
        'TerminalRowIsAttemptedWithABacklogPending',
        'CancelledPathUsesTheShortBudget',
        'AFaultedPumpIsReportedNotSummarizedAsZero',
        'NoNoticeTextEverContainsTheAuthValue',
        'NoNoticeTextEverContainsTheUrlPath',
        # The production construction path (SS6.5/SS5.2). Red on the stub because TryStart itself
        # throws; green only when task 07 builds a NON-redirecting client at a scale of 1.0 AND backs
        # the two readbacks with the handler/field the sink actually holds.
        'TryStartBuildsANonRedirectingClientAtRealTimeScale',
        # The one SS5.2 bound with no test above it. Red on the stub because Emit/the internal ctor
        # throw; a constant-only version would be GREEN here, which is why the prompt requires the
        # counted-bytes behaviour too.
        'ResponseBodyIsCappedAtEightKilobytes'
    )

    foreach ($name in $mustFail) {
        $hit = $results | Where-Object { $_.testName -like "*$name*" } | Select-Object -First 1
        if (-not $hit) {
            $problems.Add("[$name] NOT BOUND - no test with this method name executed. The prompt pins this name; author it, or this behaviour has no red.")
        }
        elseif ($hit.outcome -eq 'NotExecuted') {
            $problems.Add("[$name] was SKIPPED. A skipped test is invisible evidence loss - it is neither red nor green, and it certifies nothing about the behaviour it names.")
        }
        elseif ($hit.outcome -ne 'Failed') {
            $problems.Add("[$name] outcome '$($hit.outcome)', expected 'Failed'. It passes against a tree where Emit, TryStart, the internal test constructor, DisposeAsync and the two internal readbacks (HandlerAllowsAutoRedirect, TimeScale) ALL throw NotImplementedException, so it is not coupled to the code path it claims to test. The usual causes: it asserts only the value of a named constant task 04 already landed instead of the behaviour that enforces it (the three bounds rows), a stub returns a default instead of throwing (DisposeAsync as ValueTask.CompletedTask, or a readback written as an auto-property), or it is a 'never contains' assertion over a collection nothing ever filled.")
        }
    }

    if ($problems.Count -gt 0) {
        Write-Output ""
        Write-Output "=== Per-test red census ($($problems.Count) problem(s) of $($results.Count) executed) ==="
        $problems | ForEach-Object { Write-Output $_ }
        exit 1
    }

    Write-Output "Red census: all $($mustFail.Count) pinned behaviours are bound to a test method and observed Failed. $($results.Count) test(s) ran."
    exit 0
}
finally {
    Remove-Item -Recurse -Force $trxDir -ErrorAction SilentlyContinue
}
