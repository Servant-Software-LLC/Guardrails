# catches: a hollow test that is red only because its SIBLINGS are red (#375). A suite-level non-zero
#          exit fires if ANY selected test fails, so an Assert.True(true) placeholder passes it while
#          proving nothing. This binds every enumerated behaviour to a PINNED test method name and
#          requires each one to be observed Failed in the runner's OWN result file (TRX, never stdout),
#          accumulating one message per unbound behaviour.
#          Boundary, stated so a green reading is not over-read: this proves each test is COUPLED to the
#          code path (it fails while the implementation is absent), NOT that its assertion is correct.
#          An invoking-then-hollow test - var r = store.Append(x); Assert.NotNull(r); - is red here,
#          green after, and PASSES. Closing that needs mutation testing (#480).
$env:DOTNET_CLI_UI_LANGUAGE = 'en'
$filter = 'Category=ModelEvidence&FullyQualifiedName~TelemetryCorpusStoreTests'
$behaviours = @(
    @{ Name = 'one JSON object per line, appended';        Test = 'Append_WritesOneJsonLinePerRow' },
    @{ Name = 'idempotent on (runId, taskId, attempt)';    Test = 'Append_SameRunTaskAttemptTwice_WritesOnlyOneRow' },
    @{ Name = 'month-rotated file name';                   Test = 'Append_WritesIntoAMonthRotatedFile' },
    @{ Name = 'schemaVersion on every row';                Test = 'Append_EveryRowCarriesSchemaVersion' },
    @{ Name = 'opt-out writes nothing at all';             Test = 'Append_WhenCollectionDisabled_WritesNothing' },
    @{ Name = 'purge removes every row';                   Test = 'Purge_RemovesEveryRowUnderTheCorpusRoot' },
    @{ Name = 'an unrecognized kind round-trips verbatim'; Test = 'Row_UnrecognizedKind_RoundTripsVerbatim' }
)

$trxDir = Join-Path ([System.IO.Path]::GetTempPath()) ("gr-census-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $trxDir -Force | Out-Null
try {
    $out = dotnet test tests/Guardrails.Core.Tests --filter $filter --no-build --nologo `
        --logger "trx;LogFileName=census.trx" --results-directory $trxDir 2>&1
    $out | ForEach-Object { Write-Output $_ }

    $trx = Join-Path $trxDir 'census.trx'
    if (-not (Test-Path -LiteralPath $trx)) {
        Write-Output "PRECONDITION: no TRX result file was produced at $trx - the test RUN did not happen (a build failure, a bad --filter, or a crashed host). That is not evidence about the behaviours below; fix the run first."
        exit 1
    }

    [xml]$doc = Get-Content -LiteralPath $trx -Raw
    $failedNames = @()
    foreach ($r in $doc.TestRun.Results.UnitTestResult) {
        if ($r.outcome -eq 'Failed') { $failedNames += [string]$r.testName }
    }

    $problems = New-Object System.Collections.Generic.List[string]
    foreach ($b in $behaviours) {
        $hit = $failedNames | Where-Object { $_ -like ('*' + $b.Test + '*') }
        if (-not $hit) {
            $problems.Add("[$($b.Name)] NOT RED - no test whose name contains '$($b.Test)' was observed Failed in the TRX. Either it was never authored under that exact pinned name, or it PASSES against the stubs - which means it asserts nothing about the behaviour.")
        }
    }

    if ($failedNames.Count -lt 1) {
        $problems.Add("ZERO tests were observed Failed at all - the filter '$filter' matched nothing, or every test passed against NotImplementedException stubs. Either way there is no TDD red here.")
    }

    if ($problems.Count -gt 0) {
        Write-Output ""
        Write-Output "=== Red census: $($problems.Count) behaviour(s) not proven red ==="
        $problems | ForEach-Object { Write-Output $_ }
        Write-Output ""
        Write-Output "Every behaviour this task enumerates must be a test that COMPILES and FAILS against the stubs, under the pinned method name."
        exit 1
    }
}
finally {
    Remove-Item -Recurse -Force -LiteralPath $trxDir -ErrorAction SilentlyContinue
}
exit 0
