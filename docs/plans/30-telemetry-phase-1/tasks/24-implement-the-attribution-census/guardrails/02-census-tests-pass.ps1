# catches: an implementation whose behaviour deviates from the nine behaviours THIS task pair owns -
#          most sharply, the three that decide whether the census answers a real question at all:
#          folding a task-grain sentinel row or a script attempt into the recording gap (which would
#          hand #577 a scope that is mostly not a defect), breaking the
#          TaskGrain+ScriptAction+RecordingGap == TotalRowsNamingNoModel identity by booking an
#          unreadable task.json somewhere, and letting one malformed task.json abort the scan. It also
#          catches the CLI half: a census verb registered somewhere the shipped root command never
#          reaches, which a source grep for the registration call cannot tell from a real one.
#
# TWO INVOCATIONS, ONE FAILURE LIST, and the reason is structural rather than stylistic: this pair owns
#          test classes in TWO DIFFERENT PROJECTS - AttributionCensusTests in Guardrails.Core.Tests
#          (task 23's, made green here) and TelemetryCensusCommandTests in Guardrails.Integration.Tests
#          (authored by this task). A single `dotnet test` over the solution would run every other suite
#          in both projects and turn an unrelated red into this task's failure; a single project run
#          would silently certify half the pair. So each project is run with its OWN class-scoped filter,
#          its OWN culture pin and its OWN executed-count guard, and the results ACCUMULATE (#179) into
#          one list dumped at the end - so ONE attempt learns about both halves instead of discovering
#          the second only after fixing the first.
#
#          Each --filter names one of THIS pair's OWN test classes, never a plan-wide trait - a
#          trait-only filter asserts the state of every test in the plan, so this task could not go green
#          until a task that DEPENDS on it had run (a deadlock validate and graph --check cannot see,
#          #455). This plan introduces no trait at all, so both are shape 3: the class term alone.
#          'AttributionCensusTests' was checked against all 197 existing Core test class names and
#          'TelemetryCensusCommandTests' against all 150 existing Integration ones, plus every other
#          class this plan authors: each is a substring of none of them, so both filters are
#          discriminating. In particular TelemetryCensusCommandTests does not overlap the shipped
#          TelemetryCommandTests / TelemetryCommandWiringTests, which are longer-established names that
#          do not CONTAIN it.
#
#          Re-emits the assertion/exception lines at the END so they reach the harness retry-feedback
#          tail (#179): default `dotnet test` prints them mid-run and ends with only [FAIL] <name>. With
#          two invocations the first run's raw log would otherwise bury the detail, so the re-emitted
#          lines are COLLECTED and printed once, after both runs, immediately before the failure list.
#
# NO -v q on either TEST command (#179/#462): it suppresses the entire Error Message / Expected /
#          Actual / Stack Trace block, leaving only "[FAIL] <name>" for the re-emit below to find.
$ErrorActionPreference = 'Continue'

# The summary line is LOCALIZED (a German-culture box prints 'gesamt:'), which would invert the
# zero-match guards into unconditional failures. Pinned once here and again inside the loop, before each
# invocation, so neither run can inherit a culture the other set (#455).
$env:DOTNET_CLI_UI_LANGUAGE = 'en'

$runs = @(
    [ordered]@{
        Project = 'tests/Guardrails.Core.Tests/Guardrails.Core.Tests.csproj'
        Filter  = 'FullyQualifiedName~AttributionCensusTests'
        Fix     = "Fix src/Guardrails.Core/Telemetry/TelemetryAttributionCensus.cs - do NOT edit tests/Guardrails.Core.Tests/Telemetry/AttributionCensusTests.cs, which is outside this task's writeScope and would fail the write-scope check. If TheThreeCategoriesSumToTheTotalNamingNoModel is the failure, an unreadable task.json is being counted somewhere: it belongs in UnreadableDefinitions and in none of the four counts."
    }
    [ordered]@{
        Project = 'tests/Guardrails.Integration.Tests/Guardrails.Integration.Tests.csproj'
        Filter  = 'FullyQualifiedName~TelemetryCensusCommandTests'
        Fix     = "Fix the verb registration in src/Guardrails.Cli/Commands/TelemetryCommand.cs (or the test file, which IS in this task's writeScope). If TelemetryVerbCensus_IsReachableFromTheCommandFactory is the failure, the leaf is not reachable from the root CommandFactory.BuildRootCommand builds: add it inside TelemetryCommand.Create beside BuildIngestLeaf/BuildReportLeaf/BuildPurgeLeaf, located by that TEXT rather than by a line number - task 22 edited this file before you."
    }
)

# ACCUMULATE (#179): one distinguishable message per invocation, plus the detail lines, dumped once.
$failures = @()
$detail   = @()

foreach ($run in $runs) {
    $project = $run.Project
    $filter  = $run.Filter

    # PRECONDITION - the one legitimate early exit. A missing project is not a finding about the
    # implementation, and continuing would report the other half as if this half had been checked.
    if (-not (Test-Path $project)) {
        Write-Output "PRECONDITION: $project not found - this guardrail runs this pair's tests in BOTH projects and cannot run without it."
        exit 1
    }

    # Per-invocation culture pin (#455) - never inherited from the previous iteration.
    $env:DOTNET_CLI_UI_LANGUAGE = 'en'

    Write-Output ""
    Write-Output "=== $project --filter $filter ==="
    $log = & dotnet test $project --nologo --filter $filter 2>&1 | Out-String
    $code = $LASTEXITCODE

    Write-Output $log

    # EXIT CODE FIRST on a forward (assert-pass) check (#455): a test host that never ran exits NON-zero
    # with no summary at all, so checking the exit code first reports its real error instead of blaming
    # the filter.
    if ($code -ne 0) {
        $detail += "--- $filter ---"
        foreach ($line in ($log -split "\r?\n")) {
            if ($line -match '^\s*(\[FAIL\]|Failed\s|Error Message:|Expected:|Actual:|\s+at\s)') {
                $detail += $line
            }
        }
        $failures += "$filter is RED in $project. $($run.Fix)"
        continue
    }

    # ZERO-MATCH GUARD (#455): exit 0 alone does NOT mean tests passed - a --filter that matches nothing,
    # or is malformed, also exits 0. Key on the EXECUTED count (Passed + Failed); 'Total:' would also
    # count [Skip]ped tests, and the Integration suite carries skipped tests today, so a fully-skipped
    # class would clear a Total-keyed guard while certifying nothing.
    $passed = 0
    $failed = 0
    if ($log -match 'Passed:\s*(\d+)') { $passed = [int]$Matches[1] }
    if ($log -match 'Failed:\s*(\d+)') { $failed = [int]$Matches[1] }
    $executed = $passed + $failed

    if ($executed -lt 1) {
        $failures += "FILTER MATCHED NOTHING: 0 tests executed for '$filter' in $project. The class was not found, or the filter is malformed - this half of the guardrail is certifying nothing. This is NOT a finding about the implementation."
        continue
    }

    Write-Output "$filter green: $executed tests executed, 0 failed."
}

if ($failures.Count -gt 0) {
    if ($detail.Count -gt 0) {
        Write-Output ""
        Write-Output "=== Failure details (re-emitted so they land in the harness feedback tail) ==="
        $detail | ForEach-Object { Write-Output $_ }
    }
    Write-Output ""
    Write-Output "=== census tests: $($failures.Count) of $($runs.Count) project runs did not go green ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    exit 1
}

Write-Output ""
Write-Output "Census tests green in both projects: AttributionCensusTests (Core) and TelemetryCensusCommandTests (Integration)."
exit 0
