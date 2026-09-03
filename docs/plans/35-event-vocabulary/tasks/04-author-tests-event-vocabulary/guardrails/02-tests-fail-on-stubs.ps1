# catches: a HOLLOW red. A suite-level non-zero exit fires if ANY selected test fails, so an
#          Assert.True(true) body passes on the current tree hiding behind its genuinely-failing
#          siblings. This is the PER-TEST CENSUS (#375): every enumerated behaviour is bound to a
#          PINNED test method name and its outcome is read out of the runner's own TRX - never stdout
#          (#248), never --list-tests name discovery, which a hollow body satisfies exactly as a
#          comment satisfies a token floor.
# Boundary, stated because a green census must not be over-read: this proves each test is COUPLED to
#          the code path (it fails while the behaviour is absent), NOT that its assertion is correct.
#          An invoking-then-hollow test is red here, green after, and passes. 
$ErrorActionPreference = 'Continue'
$env:DOTNET_CLI_UI_LANGUAGE = 'en'

$filter = "FullyQualifiedName~RunEventVocabularyTests"
$trxDir = Join-Path ([System.IO.Path]::GetTempPath()) ("gr35-census-" + [guid]::NewGuid().ToString("N"))
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
        Write-Output "PRECONDITION: the filter '$filter' executed ZERO tests. The class was never authored, or its name does not match. A census over an empty set certifies nothing."
        exit 1
    }

    $mustFail = @(
        'RunFinished_AppendsARunFinishedRow_CarryingExitCode',
        'RunFinishedRow_HasNoTaskId_BecauseItIsRunScoped',
        'RunFinishedRow_CarriesFaultKindButNeverAMessage',
        'EveryRow_CarriesAStrictlyIncreasingSeq',
        'Seq_IsUniqueAndOrdered_UnderConcurrentWriters',
        'AttemptFinishedRow_CarriesTheFieldsThatDecideAResponse',
        'AttemptFinishedRow_OmitsFieldsTheRecordDoesNotHold'
    )
    # DECLARED EXEMPTION (Step 2 rule 5): task 01 already landed the runId constructor parameter and
    # wired it at the composition root, so a CORRECT tree leaves this test GREEN the moment it is
    # authored. Demanding it be red would demand a correct implementation fail. It must EXECUTE and PASS.
    $mustExecute = @(
        'RunIdComesFromTheConstructor_NotTheDirectoryName'
    )
    foreach ($name in $mustExecute) {
        $hit = $results | Where-Object { $_.testName -like "*$name*" } | Select-Object -First 1
        if (-not $hit) {
            $problems.Add("[$name] NOT EXECUTED - declared-exempt from the red census but still required to RUN. No test with this method name is in the TRX.")
        }
        elseif ($hit.outcome -ne 'Passed') {
            $problems.Add("[$name] outcome '$($hit.outcome)', expected 'Passed'. Task 01 already landed the runId constructor parameter, so a correct tree leaves this GREEN - a failure means that wiring regressed.")
        }
    }
    $problems = New-Object System.Collections.Generic.List[string]

    foreach ($name in $mustFail) {
        $hit = $results | Where-Object { $_.testName -like "*$name*" } | Select-Object -First 1
        if (-not $hit) {
            $problems.Add("[$name] NOT BOUND - no test with this method name executed. The prompt pins this name; author it, or this behaviour has no red.")
        }
        elseif ($hit.outcome -ne 'Failed') {
            $problems.Add("[$name] outcome '$($hit.outcome)', expected 'Failed'. It passes against a tree where the behaviour does not exist yet, so it is not coupled to the code path it claims to test.")
        }
    }

    if ($problems.Count -gt 0) {
        Write-Output ""
        Write-Output "=== Per-test red census ($($problems.Count) problem(s) of $($results.Count) executed) ==="
        $problems | ForEach-Object { Write-Output $_ }
        exit 1
    }

    Write-Output "Red census: every enumerated behaviour is bound to a pinned test and observed Failed. $($results.Count) test(s) ran."
    exit 0
}
finally {
    Remove-Item -Recurse -Force $trxDir -ErrorAction SilentlyContinue
}
