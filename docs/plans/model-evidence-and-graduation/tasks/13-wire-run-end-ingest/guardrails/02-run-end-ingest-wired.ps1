# catches: an implementation whose behaviour deviates from the tests THIS task pair owns - a store that
#          rewrites the file instead of appending, forgets the schemaVersion, dedupes only in memory (so
#          a re-ingest after a restart duplicates every row), writes into one unbounded file, or honours
#          the opt-out by writing an empty file rather than no file at all.
#          The --filter names this pair's OWN test class, never the plan-wide trait alone: a trait-only
#          filter asserts the state of every test in the plan, so this task could not go green until a
#          task that DEPENDS on it has run - a deadlock validate and graph --check cannot see (#455).
#          Re-emits the assertion detail at the END so it reaches the retry-feedback tail (#179).
$env:DOTNET_CLI_UI_LANGUAGE = 'en'    # the run summary the guard reads is LOCALIZED (#455)
$filter = 'Category=ModelEvidence&FullyQualifiedName~RunEndTelemetryIngestTests'   # VERBATIM from the pair's inverse half
# NO -v q on the TEST command: it suppresses the Error Message/Expected/Actual block, leaving only
# "[FAIL] <name>" for the re-emit below to find - which defeats #179 by the flag alone.
$out = dotnet test tests/Guardrails.Integration.Tests --filter $filter --no-build --nologo 2>&1
$testExit = $LASTEXITCODE                                  # capture BEFORE any other statement
$out | ForEach-Object { Write-Output $_ }

# EXIT CODE FIRST, guard second (#455): a test host that never ran exits NON-zero with no summary, so
# checking the exit code first reports its real error instead of blaming the filter.
if ($testExit -ne 0) {
    $detail = $out |
        Select-String -Pattern '\[FAIL\]|Error Message:|Assert\.|Exception|Stack Trace:|Expected:|Actual:' |
        ForEach-Object { $_.Line } |
        Select-Object -First 40
    Write-Output ""
    Write-Output "=== Failure details (re-emitted so they land in the harness feedback tail) ==="
    if ($detail) { $detail | ForEach-Object { Write-Output $_ } }
    else { Write-Output "(no assertion/exception lines matched - inspect the full log above)" }
    Write-Output "RunEndTelemetryIngestTests failing - run-end ingest is not wired into RunCommand.Finish to the spec those tests pin (see failure details above)"
    exit 1
}

# ZERO-MATCH GUARD (#455): exit 0 alone does NOT mean tests passed - a --filter that matches nothing, or
# is malformed, also exits 0. Key on the EXECUTED count (Passed+Failed; "Total:" would also count
# [Skip]ped tests), never on "No test matches ..." (verbosity-dependent, so it never fires - #248).
$ran = ([regex]::Matches(($out | Out-String), '(?:Passed|Failed):\s*(\d+)') |
        ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum
if ($ran -lt 1) {
    Write-Output "exit 0 but ZERO tests executed - this guardrail certified nothing. The --filter '$filter' matched no tests, is malformed, or every matched test is [Skip]ped. Check it against the tests this pair owns (class RunEndTelemetryIngestTests, trait Category=ModelEvidence)."
    exit 1
}
exit 0
