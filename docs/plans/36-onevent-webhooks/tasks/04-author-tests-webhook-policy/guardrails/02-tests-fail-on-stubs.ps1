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
# Measured baseline (authoring time): `WebhookPolicyTests` matches 0 tests in src/ and tests/ today
#          (the class does not exist), and Category=RunEvents in Guardrails.Core.Tests selects 41
#          passing tests. The zero-match precondition below is therefore ARMED, not pre-satisfied.
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
        $hit = $results | Where-Object { $_.testName -like "*$name*" } | Select-Object -First 1
        if (-not $hit) {
            $problems.Add("[$name] NOT BOUND - no test with this method name executed. The prompt pins this name; author it, or this behaviour has no red.")
        }
        elseif ($hit.outcome -eq 'NotExecuted') {
            $problems.Add("[$name] was SKIPPED. A skipped test is invisible evidence loss - it is neither red nor green, and it certifies nothing about the behaviour it names.")
        }
        elseif ($hit.outcome -ne 'Failed') {
            $problems.Add("[$name] outcome '$($hit.outcome)', expected 'Failed'. It passes against a tree where IsRetryable and RedactUrl both throw NotImplementedException, so it is not coupled to the code path it claims to test. The usual causes: the stub returns a default (an IsRetryable written as 'return false') instead of throwing, or the test asserts the value of a constant this task also lands rather than calling the function.")
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
