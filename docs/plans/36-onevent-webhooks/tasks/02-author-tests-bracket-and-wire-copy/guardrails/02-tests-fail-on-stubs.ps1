# catches: a HOLLOW red. A suite-level non-zero exit fires if ANY selected test fails, so an
#          Assert.True(true) body - or a test that was simply never written - passes on the current
#          tree hiding behind its genuinely-failing siblings. This is the PER-TEST CENSUS (#375):
#          every enumerated behaviour is bound to a PINNED test method name and its outcome is read
#          out of the runner's own TRX - never stdout (#248), never --list-tests name discovery,
#          which a hollow body satisfies exactly as a comment satisfies a token floor.
# Boundary, stated because a green census must not be over-read: this proves each test is COUPLED to
#          the code path (it fails while the behaviour is absent), NOT that its assertion is correct.
#          An invoking-then-hollow test is red here, green after, and passes.
# DECLARED EXEMPTION - AThrowingOnRowCallbackDoesNotPropagate, Expect='Executed' rather than 'Failed'.
#          The reason is STRUCTURAL, not a concession: the stub this task authors accepts `onRow` and
#          never invokes it, so nothing can throw and the test is green against the stub - and a
#          CORRECT implementation is green too, because design section 3.1's try/catch is precisely
#          what the test pins. Neither Failed nor Passed distinguishes a good test from a bad one
#          here, so the only honest requirement is that it EXIST and RUN: present in the TRX and not
#          skipped. It is enumerated below rather than dropped, because a census that silently omits
#          a behaviour is how a regression guard goes missing.
# Measured baseline (#478): every one of the eleven names below (the class and its ten methods)
#          greps to 0 occurrences across src/ and tests/ at authoring time - each is a genuine
#          required-present target, none is pre-satisfied by existing code.
$ErrorActionPreference = 'Continue'
$env:DOTNET_CLI_UI_LANGUAGE = 'en'

# Task-level filters name their own test CLASS; the plan-wide Plan trait is never used alone (#455).
$filter = "Category=RunEvents&FullyQualifiedName~RunEventBracketTests"
$trxDir = Join-Path ([System.IO.Path]::GetTempPath()) ("gr36-bracket-census-" + [guid]::NewGuid().ToString("N"))
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
        --filter $filter --nologo --logger "trx;LogFileName=bracket-red.trx" --results-directory $trxDir 2>&1 | Out-String
    Write-Output $log

    # INVERSE POLARITY: every precondition runs BEFORE any red is interpreted, so a crashed or
    # never-started run is never certified as TDD red.
    $trx = Join-Path $trxDir 'bracket-red.trx'
    if (-not (Test-Path -LiteralPath $trx)) {
        Write-Output "PRECONDITION: no TRX at $trx - the test run did not happen (the host failed to start, or the project failed to build). This is NOT a report about unbound behaviours."
        exit 1
    }

    [xml]$xml = Get-Content -LiteralPath $trx -Raw
    $results = @($xml.TestRun.Results.UnitTestResult | Where-Object { $_ })
    if ($results.Count -lt 1) {
        Write-Output "PRECONDITION: the filter '$filter' produced ZERO test records. The class RunEventBracketTests was never authored, or its name does not match. A census over an empty set certifies nothing."
        exit 1
    }

    # Zero-match guard keyed on the EXECUTED count, never the total: a TRX full of NotExecuted
    # (skipped) records would otherwise read as a populated census.
    $executed = @($results | Where-Object { $_.outcome -ne 'NotExecuted' })
    if ($executed.Count -lt 1) {
        Write-Output "PRECONDITION: the filter '$filter' matched $($results.Count) test(s) but EXECUTED none - every record is NotExecuted (skipped). A census over a skipped set certifies nothing."
        exit 1
    }

    # Nine behaviours that do not exist until task 03 lands. Each MUST be observed Failed.
    $mustFail = @(
        'BracketIsPresentOnEveryRow',
        'BracketMatchesUnixMillisAndFourHex',
        'BracketIsStableAcrossRowsInOneStream',
        'BracketDiffersAcrossTwoStreams',
        'WireLineEqualsFileLineWhenDetailIsNull',
        'WireLineEqualsFileLineForPassingGuardrailFinished',
        'WireLineCarriesWithheldMarkerWhenDetailPresent',
        'WireLineCapsDetailAtMaxCharsWhenIncludeDetailIsTrue',
        'SeqAndBracketStayConsistentUnderConcurrentWriters'
    )

    # The declared exemption - see the header. Expect='Executed': present and not skipped.
    $mustExecute = @(
        'AThrowingOnRowCallbackDoesNotPropagate'
    )

    $problems = New-Object System.Collections.Generic.List[string]

    foreach ($name in $mustFail) {
        $hits = Get-CensusHits -Results $results -Name $name
        if ($hits.Count -lt 1) {
            $problems.Add("[$name] NOT BOUND - no test with this method name is in the TRX. The prompt pins this name; author it, or this behaviour has no red.")
        }
        else {
            $notFailed = @($hits | Where-Object { $_.outcome -ne 'Failed' })
            if ($notFailed.Count -gt 0) {
                $problems.Add("[$name] outcome '$($notFailed[0].outcome)', expected 'Failed'. It passes against a tree where bracket and the wire copy do not exist yet, so it is not coupled to the code path it claims to test - or this task implemented the behaviour, which belongs to task 03.")
            }
        }
    }

    foreach ($name in $mustExecute) {
        $hits = Get-CensusHits -Results $results -Name $name
        if ($hits.Count -lt 1) {
            $problems.Add("[$name] NOT EXECUTED - declared-exempt from the red census (the stub never invokes onRow, so nothing can throw) but still REQUIRED to exist and run. No test with this method name is in the TRX.")
        }
        else {
            $ran = @($hits | Where-Object { $_.outcome -ne 'NotExecuted' })
            if ($ran.Count -lt 1) {
                $problems.Add("[$name] SKIPPED - every record for it is NotExecuted. The declared exemption covers its OUTCOME, not its absence: a skipped regression guard guards nothing.")
            }
        }
    }

    if ($problems.Count -gt 0) {
        Write-Output ""
        Write-Output "=== Per-test red census ($($problems.Count) problem(s) of $($executed.Count) executed) ==="
        $problems | ForEach-Object { Write-Output $_ }
        exit 1
    }

    Write-Output "Red census: nine enumerated behaviours are bound to pinned tests and observed Failed; the declared-exempt AThrowingOnRowCallbackDoesNotPropagate executed. $($executed.Count) test(s) ran."
    exit 0
}
finally {
    Remove-Item -Recurse -Force $trxDir -ErrorAction SilentlyContinue
}
