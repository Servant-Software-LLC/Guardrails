# catches: a HOLLOW red. A suite-level non-zero exit fires if ANY selected test fails, so an
#          Assert.True(true) body - or an IsRetryable stub written as `=> false`, which makes the two
#          negative rows of the truth table pass - hides behind its genuinely-failing siblings. This
#          is the PER-TEST CENSUS (#375): every enumerated behaviour is bound to a PINNED test method
#          name and its outcome is read out of the runner's own TRX - never stdout (#248), never
#          --list-tests name discovery, which a hollow body satisfies exactly as a comment satisfies
#          a token floor.
# Boundary, stated because a green census must not be over-read: this proves each test is COUPLED to
#          the code path (it fails while the behaviour is absent), NOT that its assertion is correct.
#          An invoking-then-hollow test is red here, green after, and passes.
# DECLARED EXEMPTIONS: NONE - and that is a claim, not an omission. Every one of the nine pinned
#          behaviours calls IsRetryable or RedactUrl, and this task lands both as
#          `throw new NotImplementedException()`, so every one is red on the stub and green only once
#          task 05 implements it. No pinned test is green-against-the-stub AND green-against-a-
#          correct-implementation, so no row needed an Expect='Executed' exemption. If a later edit
#          makes one so, ADD it here with its structural reason; never delete the row.
# Measured baseline (#478), re-measured on this branch 2026-09-04: `WebhookPolicyTests` matches 0
#          tests in src/ and tests/ today (the class does not exist), and Category=RunEvents in
#          Guardrails.Core.Tests selects 49 passing tests (the 41 this comment carried was a copied
#          plan-35 figure; master has moved). The zero-match precondition below is therefore ARMED,
#          not pre-satisfied.
# THE MATCHER IS ANCHORED AND GRADES EVERY HIT - it is `Get-CensusHits`, copied verbatim from
#          tasks/02-author-tests-bracket-and-wire-copy/guardrails/02-tests-fail-on-stubs.ps1 so this
#          plan carries ONE census matcher, not four. It replaces a `-like "*$name*"` +
#          `Select-Object -First 1` pair that was weak in two measured ways. (1) The name was not
#          actually PINNED: a substring match means a test called CircuitNeverClosesAfterACooldown
#          satisfies the census row for CircuitNeverCloses, so a renamed-and-widened test silently
#          stands in for the behaviour it no longer covers. (2) Only the FIRST TRX record was graded,
#          and a [Theory] emits ONE RECORD PER DATA ROW - so a theory whose first row is Failed and
#          whose remaining rows Pass satisfied the row. That is not hypothetical here:
#          IsRetryableIsTrueForEvery5xx is naturally a [Theory] over the 5xx band, and a stub that
#          returns a default for all but one status is exactly the hollow red this file exists to
#          catch. Every hit is now graded; do not reintroduce -First.
# INVERSE POLARITY: the suite exit code is deliberately never read - red IS the deliverable here, so
#          exit 1 from dotnet test carries no information. The preconditions run FIRST so that a
#          crashed or never-started run is reported as a crash, not certified as TDD red.
$ErrorActionPreference = 'Continue'
$env:DOTNET_CLI_UI_LANGUAGE = 'en'

# Discriminating filter (verified at authoring time): "WebhookPolicyTests" is not a substring of
# "WebhookEventSinkTests" and vice versa, so this selects THIS pair's class and nothing else. The
# Category clause is doubly useful - it also proves the [Trait("Category","RunEvents")] the pair's
# tests-pass guardrail depends on was actually applied.
$filter = "Category=RunEvents&FullyQualifiedName~WebhookPolicyTests"
$trxDir = Join-Path ([System.IO.Path]::GetTempPath()) ("gr36-policy-census-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force $trxDir | Out-Null

# Anchored so a test named <pinned>AndSomethingElse cannot satisfy the census for <pinned>. Matches
# "Namespace.Class.Method", "Class+Nested.Method" and a theory's "Method(arg: 1)".
function Get-CensusHits {
    param([object[]]$Results, [string]$Name)
    $pattern = '(^|[.+])' + [regex]::Escape($Name) + '($|\()'
    # @(...) around the pipeline: with no match this yields an EMPTY array, whereas a bare $null
    # would make .Count report 1 and every guard below unfireable (#455).
    return @($Results | Where-Object { $_.testName -match $pattern })
}

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
        Write-Output "PRECONDITION: the filter '$filter' executed ZERO tests. Either the class WebhookPolicyTests was never authored / was renamed, or its methods are missing [Trait(`"Category`",`"RunEvents`")]. A census over an empty set certifies nothing."
        exit 1
    }

    $problems = New-Object System.Collections.Generic.List[string]

    $mustFail = @(
        'IsRetryableIsTrueFor408And429',
        'IsRetryableIsTrueForEvery5xx',
        'IsRetryableIsFalseFor3xx',
        'IsRetryableIsFalseForOtherFourXx',
        'IsRetryableIsTrueForTransportExceptions',
        'IsRetryableIsTrueForPerAttemptTimeout',
        'RedactedUrlKeepsSchemeHostAndPort',
        'RedactedUrlNeverContainsThePath',
        'RedactedUrlNeverContainsTheQuery'
    )

    foreach ($name in $mustFail) {
        $hits = Get-CensusHits -Results $results -Name $name
        if ($hits.Count -lt 1) {
            $problems.Add("[$name] NOT BOUND - no test with this method name executed. The prompt pins this name; author it, or this behaviour has no red. (The match is ANCHORED on the method name: a test called '${name}Something' does NOT satisfy this row.)")
            continue
        }
        # EVERY record is graded, never the first. A [Theory] contributes one record per data row, so
        # a partial red - one failing row carrying several passing ones - is a partial coupling and is
        # reported as such.
        $skipped = @($hits | Where-Object { $_.outcome -eq 'NotExecuted' })
        if ($skipped.Count -gt 0) {
            $problems.Add("[$name] was SKIPPED ($($skipped.Count) of $($hits.Count) record(s) NotExecuted). A skipped test is invisible evidence loss - it is neither red nor green, and it certifies nothing about the behaviour it names. On a [Theory], a skipped data row is a hole in the truth table.")
        }
        $notFailed = @($hits | Where-Object { $_.outcome -ne 'Failed' -and $_.outcome -ne 'NotExecuted' })
        if ($notFailed.Count -gt 0) {
            $seen = (($notFailed | ForEach-Object { $_.outcome } | Sort-Object -Unique) -join '/')
            $problems.Add("[$name] $($notFailed.Count) of $($hits.Count) record(s) reported '$seen', expected 'Failed'. Those rows pass against a tree where IsRetryable and RedactUrl both throw NotImplementedException, so they are not coupled to the code path they claim to test. The usual causes: the stub returns a default (an IsRetryable written as 'return false') instead of throwing, the test asserts the value of a constant this task also lands rather than calling the function, or - on a [Theory] - only some data rows actually reach the stub. EVERY row must be red here; a theory whose first row fails and whose rest pass is a partial census, not a red one.")
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
