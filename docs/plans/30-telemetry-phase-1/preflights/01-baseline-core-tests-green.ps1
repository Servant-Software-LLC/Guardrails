# catches: a plan that builds on ALREADY-RED existing tests in Guardrails.Core.Tests. All but two of this
#          plan's writing tasks touch src/Guardrails.Core/** - the bucket classifier (02), the journal
#          record shape (03), the transport carriers (04), the corpus row shape (04a), the serial bucket
#          write (06), the digest capture (08) and its provenance fold (10), the turn count (12), the
#          segment durations (12a), the route-warmth flag (14), the worktree settle carrier (16), the
#          run-environment probe (17/18), the ETL mapping (20) and the attribution census (24) - and each
#          one's tests-pass guardrail would otherwise fail from PRE-EXISTING breakage it cannot fix:
#          misattributed to the task, burning its whole retry budget and ending at needs-human with its
#          own deliverable complete. "Never build on red" (#181).
#
#          It also establishes the one fact this plan's own honesty rests on. The shipped Core telemetry
#          suites - TelemetryIngestTests, TelemetryCorpusStoreTests, TelemetryReportTests and
#          TelemetryFailureClassifierTests - pin the Phase-0 corpus behaviour that every Phase-1 field
#          extends, and all four are deliberately left INSIDE this baseline. If any of them is already
#          red on the starting tree, "the new field reaches the row" cannot be read off a later green,
#          because the row's existing behaviour was never green to begin with.
#
# SCOPE: the EXISTING Core tests only, via an FQN exclusion of every Core test class THIS plan authors.
#        A whole-project `dotnet test` here would hit the #165/#176 compile-coupling trap the moment an
#        author-tests task has landed its intentionally-red tests. The exclusion is written as
#        FullyQualifiedName!~<Class>, NOT as a shared plan-wide trait: this plan introduces no trait at
#        all (shape 3 of the four sanctioned filter forms), following the shipped plan-31 and plan-32
#        precedent, and an FQN list names exactly what it excludes.
#
#        NO METHOD-LEVEL EXCLUSION IS NEEDED HERE, and that is a MEASURED finding rather than an
#        assumption (#574 - a baseline that halted a plan-32 run on a red the plan itself had created).
#        The two shipped assertions this plan could plausibly redden were both checked and neither does:
#          * TelemetryCorpusStoreTests.Append_EveryRowCarriesSchemaVersion asserts
#            `TelemetryRow.CurrentSchemaVersion` SYMBOLICALLY (line 130), not the literal 1, so task 04a's
#            bump to 2 leaves it green. TelemetryCorpusConcurrentAppendTests line 61 hard-codes
#            `SchemaVersion = 1` but only to CONSTRUCT a row, and asserts nothing about the constant.
#          * TelemetryCommandTests.Report_PrintsTheStratifiedTable is in the OTHER project and asserts
#            only that the model tag and the words "insufficient evidence" appear - both survive task
#            22's bucket column, digest fingerprint and era-boundary line.
#        No shipped Core test class therefore has to stay in the baseline with a method carved out. If a
#        later hand-edit changes that, add the method-level `FullyQualifiedName!~<Method>` term here -
#        plan 32's own line 63 is the worked precedent - rather than dropping the whole class.
#
#        Discriminating-substring check (#455 companion (a)), run MECHANICALLY - not by eye - against
#        every existing Core test class name, harvested as
#        `grep -rho "class [A-Za-z0-9_]*Tests" tests/Guardrails.Core.Tests --include=*.cs | sort -u`
#        (195 distinct names on master @d87c766; the method is written out because the number moves and
#        a bare count cannot be re-checked). Each of the fourteen excluded names below matched ZERO of
#        them, so none is excluded by mistake and all four shipped telemetry suites stay IN this
#        baseline - which is the point of the paragraph above. None of the fourteen contains another of
#        the fourteen as a substring either, so there is no silent exclusion fan-out. Both facts were
#        re-checked after the class names moved (task 11 splits its file into AttemptTurnsTests and
#        AttemptSegmentsTests, which are the CLASS names the filter must carry - the file is still named
#        AttemptEnvelopeTests.cs, and a term keyed on the FILE name would exclude nothing at all).
#
# Required-present baseline (#478): this guardrail asserts a POSITIVE precondition on the STARTING tree,
#        so it is green-on-arrival BY DESIGN - the class Step 7.0a exempts, alongside the wave ENTRY gate
#        and the #500 delegated-decisions check. MEASURED on master @d87c766, unfiltered: 2174 passing,
#        0 failing, 0 skipped. The fourteen excluded classes do not exist yet, so the filter drops nothing
#        today and the executed count equals that full count.
$ErrorActionPreference = 'Continue'

# The summary line is LOCALIZED (a German-culture box prints 'gesamt:' and no 'Total:'), which would
# invert the zero-match guard into an unconditional failure. Pin it before the run, not after (#455).
$env:DOTNET_CLI_UI_LANGUAGE = 'en'

$project = 'tests/Guardrails.Core.Tests/Guardrails.Core.Tests.csproj'
if (-not (Test-Path $project)) {
    Write-Output "PRECONDITION: $project not found - this preflight is scoped to the Core test project and cannot run without it."
    exit 1
}

# Every Core test class this plan authors, excluded so the baseline can never go red on a not-yet-written
# test. Task 01 -> TaskFingerprintBucketTests; 03 -> Phase1JournalShapeTests; 04 -> TransportShapeTests;
# 04a -> CorpusRowShapeTests; 05 -> TaskBucketJournalTests; 07 -> ModelDigestCaptureTests;
# 09 -> ModelDigestProvenanceTests; 11 -> AttemptTurnsTests and AttemptSegmentsTests (two classes, one
# file); 13 -> RouteWarmthTests; 15 -> WorktreeSettlePhase1Tests; 17 -> RunEnvironmentTests;
# 19 -> Phase1TelemetryRowTests; 23 -> AttributionCensusTests.
$filter = 'FullyQualifiedName!~TaskFingerprintBucketTests' +
          '&FullyQualifiedName!~Phase1JournalShapeTests' +
          '&FullyQualifiedName!~TransportShapeTests' +
          '&FullyQualifiedName!~CorpusRowShapeTests' +
          '&FullyQualifiedName!~TaskBucketJournalTests' +
          '&FullyQualifiedName!~ModelDigestCaptureTests' +
          '&FullyQualifiedName!~ModelDigestProvenanceTests' +
          '&FullyQualifiedName!~AttemptTurnsTests' +
          '&FullyQualifiedName!~AttemptSegmentsTests' +
          '&FullyQualifiedName!~RouteWarmthTests' +
          '&FullyQualifiedName!~WorktreeSettlePhase1Tests' +
          '&FullyQualifiedName!~RunEnvironmentTests' +
          '&FullyQualifiedName!~Phase1TelemetryRowTests' +
          '&FullyQualifiedName!~AttributionCensusTests'

# NO -v q on a TEST command: it deletes the Error Message/Expected/Actual/Stack Trace block the re-emit
# below exists to surface, defeating #179 by the flag alone.
$log = & dotnet test $project --nologo --filter $filter 2>&1 | Out-String
$code = $LASTEXITCODE

Write-Output $log

# Zero-match guard (#455): keyed on the EXECUTED count (Passed + Failed), never on 'Total:' - which
# counts [Skip]ped tests, so a fully-skipped run would clear a Total-keyed guard and certify "the area is
# green" over nothing. Never on the "no tests matched" STRING either: that is verbosity-dependent and so
# never fires (#248).
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
    Write-Output "=== Pre-existing failures in Guardrails.Core.Tests (detail re-emitted) ==="
    foreach ($line in ($log -split "\r?\n")) {
        if ($line -match '^\s*(\[FAIL\]|Failed\s|Error Message:|Expected:|Actual:|\s+at\s)') {
            Write-Output $line
        }
    }
    Write-Output ""
    Write-Output "The Core area's EXISTING tests ($executed executed, $failed failed) are already failing on the starting code. Fix the pre-existing breakage before this plan builds on it - eleven of this plan's twelve implementation tasks modify src/Guardrails.Core/** and would inherit these failures as their own. If the failures are in TelemetryIngestTests, TelemetryCorpusStoreTests or TelemetryReportTests, stop: this plan's whole claim that a new field REACHES the corpus row is read off those suites."
    exit 1
}

Write-Output "Baseline green: $executed existing Core tests executed, 0 failed."
exit 0
