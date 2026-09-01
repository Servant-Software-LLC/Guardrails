# catches: a plan that builds on ALREADY-RED existing tests in Guardrails.Integration.Tests. Three of
#          this plan's tasks write into src/Guardrails.Cli/** - the run-environment wiring (18), the
#          report's bucket, digest and era-boundary rendering (22) and the census verb (24) - and the
#          ONLY suites that drive those surfaces live in this project: TelemetryCommandTests (whose
#          Report_PrintsTheStratifiedTable pins the report's rendered wording), TelemetryCommandWiring
#          Tests (verb registration through CommandFactory) and RunEndTelemetryIngestTests (the run-end
#          ingest path task 18 edits alongside). No Core test sees any of them. Without this baseline a
#          pre-existing red in one of those three would be misattributed to task 22 or 24, burn its
#          whole retry budget, and end at needs-human with its own deliverable complete (#181).
#
# SCOPE: the EXISTING Integration tests only, via an FQN exclusion of the three Integration test classes
#        THIS plan authors. A whole-project `dotnet test` here would hit the #165/#176 compile-coupling
#        trap the moment task 21 lands its intentionally-red tests. The exclusion is written as
#        FullyQualifiedName!~<Class>, NOT as a shared plan-wide trait: this plan introduces no trait at
#        all (shape 3 of the four sanctioned filter forms), following the shipped plan-31 and plan-32
#        precedent.
#
#        NO METHOD-LEVEL EXCLUSION IS NEEDED HERE, and that is a MEASURED finding rather than an
#        assumption (#574 - a baseline that halted a plan-32 run on a red the plan itself had created).
#        The one shipped Integration assertion this plan could plausibly redden was checked and does
#        not: TelemetryCommandTests.Report_PrintsTheStratifiedTable asserts only that the model tag and
#        the words "insufficient evidence" appear in the rendered output. Task 22 adds a BUCKET value
#        where "(unbucketed)" stood, folds the digest into the model fingerprint (the tag survives as a
#        substring) and prints an era-boundary line beside the existing legend - none of which removes
#        either asserted string, and the plan's section 5 forbids weakening a legend sentence anyway.
#        Its fixture rows are written at DateTimeOffset.UtcNow, so the era-boundary filter cannot drop
#        them either. If a later hand-edit changes that, add a method-level
#        `FullyQualifiedName!~Report_PrintsTheStratifiedTable` term here - plan 32's own integration
#        preflight carries exactly that shape as its third term - rather than dropping the whole class,
#        which would take TelemetryCommandTests' four other shipped facts out of the baseline with it.
#
#        Discriminating-substring check (#455 companion (a)), run MECHANICALLY - not by eye - against
#        every existing Integration test class name, harvested as
#        `grep -rho "class [A-Za-z0-9_]*Tests" tests/Guardrails.Integration.Tests --include=*.cs | sort -u`
#        (135 distinct names on master @d87c766; the method is written out because the number moves and
#        a bare count cannot be re-checked). All three excluded names matched ZERO of them. In particular
#        neither swallows the shipped `TelemetryCommandTests` - `!~` excludes names CONTAINING the term,
#        and `TelemetryCommandTests` does not contain `TelemetryCensusCommandTests` - so all three
#        shipped telemetry suites stay IN this baseline, which is the point of the paragraph above.
#
# Required-present baseline (#478): this guardrail asserts a POSITIVE precondition on the STARTING tree,
#        so it is green-on-arrival BY DESIGN - the class Step 7.0a exempts. MEASURED on master @d87c766,
#        unfiltered: 1062 passing, 0 failing, 4 SKIPPED (Total 1066), in 10m24s. The four skipped tests
#        are why the guard below is keyed on Passed+Failed and never on 'Total:' - a Total-keyed guard
#        would count them and could certify a fully-skipped run as green. The three excluded classes do
#        not exist yet, so the filter drops nothing today and the executed count equals that 1062.
#        Note the runtime: this preflight costs about ten minutes before the DAG starts, and the
#        terminal 03-integration-suite-passes gate costs the same again at the end. That is the price of
#        the only coverage the three CLI-writing tasks have, and it is paid once at each end rather than
#        per task.
$ErrorActionPreference = 'Continue'

# The summary line is LOCALIZED (a German-culture box prints 'gesamt:' and no 'Total:'), which would
# invert the zero-match guard into an unconditional failure. Pin it before the run, not after (#455).
$env:DOTNET_CLI_UI_LANGUAGE = 'en'

$project = 'tests/Guardrails.Integration.Tests/Guardrails.Integration.Tests.csproj'
if (-not (Test-Path $project)) {
    Write-Output "PRECONDITION: $project not found - this preflight is scoped to the Integration test project and cannot run without it."
    exit 1
}

# The three Integration test classes this plan authors: task 21 -> TelemetryReportPhase1Tests;
# task 23 -> TelemetryCensusCommandTests; task 17 -> RunEnvironmentJournalTests.
$filter = 'FullyQualifiedName!~TelemetryReportPhase1Tests' +
          '&FullyQualifiedName!~TelemetryCensusCommandTests' +
          '&FullyQualifiedName!~RunEnvironmentJournalTests'

# NO -v q on a TEST command: it deletes the Error Message/Expected/Actual/Stack Trace block the re-emit
# below exists to surface, defeating #179 by the flag alone.
$log = & dotnet test $project --nologo --filter $filter 2>&1 | Out-String
$code = $LASTEXITCODE

Write-Output $log

# Zero-match guard (#455): keyed on the EXECUTED count (Passed + Failed), never on 'Total:' - which
# counts the four [Skip]ped tests this project really has, so a Total-keyed guard would clear on a run
# that executed nothing. Never on the "no tests matched" STRING either: verbosity-dependent (#248).
$passed = 0
$failed = 0
if ($log -match 'Passed:\s*(\d+)') { $passed = [int]$Matches[1] }
if ($log -match 'Failed:\s*(\d+)') { $failed = [int]$Matches[1] }
$executed = $passed + $failed

if ($executed -lt 1) {
    Write-Output "BASELINE FILTER MATCHED NOTHING: 0 tests executed in $project. The exclusion filter is malformed or the test host never ran - this preflight is certifying nothing. Fix the filter before running the plan."
    exit 1
}

if ($code -ne 0) {
    # #179: re-emit the failure DETAIL at the END so the WHY reaches the halt feedback, not just [FAIL] names.
    Write-Output ""
    Write-Output "=== Pre-existing failures in Guardrails.Integration.Tests (detail re-emitted) ==="
    foreach ($line in ($log -split "\r?\n")) {
        if ($line -match '^\s*(\[FAIL\]|Failed\s|Error Message:|Expected:|Actual:|\s+at\s)') {
            Write-Output $line
        }
    }
    Write-Output ""
    Write-Output "The Integration area's EXISTING tests ($executed executed, $failed failed) are already failing on the starting code. Fix the pre-existing breakage before this plan builds on it - tasks 18, 22 and 24 modify src/Guardrails.Cli/** and would inherit these failures as their own. If the failures are in TelemetryCommandTests, TelemetryCommandWiringTests or RunEndTelemetryIngestTests, stop: those three suites are the only ones that drive the CLI surfaces this plan changes."
    exit 1
}

Write-Output "Baseline green: $executed existing Integration tests executed, 0 failed."
exit 0
