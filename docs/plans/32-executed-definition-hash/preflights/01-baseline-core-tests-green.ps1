# catches: a plan that builds on ALREADY-RED existing tests in Guardrails.Core.Tests. Seven of this
#          plan's sixteen stages write under src/Guardrails.Core/** - the load-time pin (03), the two
#          serial write sites (04), the two worktree write sites plus the shared ignore predicate (05),
#          the wave twin (09), the journal record (12) and the divergence gate (13) - and each one's
#          tests-pass guardrail would otherwise fail from PRE-EXISTING breakage it cannot fix:
#          misattributed to the task, burning its whole retry budget and ending at needs-human with its
#          own deliverable complete. "Never build on red" (#181).
#
#          It also establishes the ONE fact this plan's section 5.5 no-op property rests on. Four shipped
#          Core suites hard-pin definition-hash behaviour - TaskDefinitionHashTests (the byte-pin P8
#          protects), WaveDefinitionHashTests, RunJournalDefinitionHashTests and PlanDefinitionHashWave
#          Tests - and all four are deliberately left INSIDE this baseline. If any of them is already red
#          on the starting tree, the "an unedited run records a byte-identical hash" claim cannot be read
#          off a later green, because it was never green to begin with.
#
# SCOPE: the EXISTING Core tests only, via an FQN exclusion of every Core test class THIS plan authors.
#        A whole-project `dotnet test` here would hit the #165/#176 compile-coupling trap the moment an
#        author-tests stage has landed its intentionally-red tests. The exclusion is written as
#        FullyQualifiedName!~<Class>, NOT as a shared plan-wide trait: this plan introduces no trait at
#        all (shape 3 of the four sanctioned filter forms), following the shipped plan-31 precedent,
#        and an FQN list names exactly what it excludes.
#
#        Discriminating-substring check (#455 companion (a)), run against every existing test class in
#        this project. The four shipped classes carrying 'DefinitionHash' are TaskDefinitionHashTests,
#        WaveDefinitionHashTests, RunJournalDefinitionHashTests and PlanDefinitionHashWaveTests. NONE of
#        them contains 'ExecutedDefinitionHashTests', 'ExecutedDefinitionHashAnchorTests' or
#        'ExecutedDefinitionDivergenceTests' as a substring, so none is excluded by mistake and all four
#        stay IN this baseline - which is the point of the paragraph above.
#
#        Note the deliberate substring fan-out: 'WaveExecutedDefinitionHashTests' CONTAINS
#        'ExecutedDefinitionHashTests', so the first term below already excludes stage 8's class. The
#        explicit fourth term is redundant and kept anyway, so a reader can see all four excluded
#        classes without having to work out the containment. (That same containment is why the TASK-level
#        filters for stages 1/4 and 8/9 are NAMESPACE-QUALIFIED - see those guardrails.)
#
# Required-present baseline (#478): this guardrail asserts a POSITIVE precondition on the STARTING tree,
#        so it is green-on-arrival BY DESIGN - the class Step 7.0a exempts, alongside the wave ENTRY
#        gate and the #500 delegated-decisions check. Measured on design/32-executed-definition-hash
#        @1f6d54c: 2144 passing, 0 failing, 0 skipped, unfiltered. The four excluded classes do not
#        exist yet, so the filter drops nothing today and the executed count equals that full count.
$ErrorActionPreference = 'Continue'

# The summary line is LOCALIZED (a German-culture box prints 'gesamt:' and no 'Total:'), which would
# invert the zero-match guard into an unconditional failure. Pin it before the run, not after (#455).
$env:DOTNET_CLI_UI_LANGUAGE = 'en'

$project = 'tests/Guardrails.Core.Tests/Guardrails.Core.Tests.csproj'
if (-not (Test-Path $project)) {
    Write-Output "PRECONDITION: $project not found - this preflight is scoped to the Core test project and cannot run without it."
    exit 1
}

# Every Core test class this plan authors, excluded so the baseline can never go red on a not-yet-
# written test. Stage 1 -> ExecutedDefinitionHashTests; stage 6 -> ExecutedDefinitionHashAnchorTests;
# stage 8 -> WaveExecutedDefinitionHashTests; stage 10 -> ExecutedDefinitionDivergenceTests.
$filter = 'FullyQualifiedName!~ExecutedDefinitionHashTests' +
          '&FullyQualifiedName!~ExecutedDefinitionHashAnchorTests' +
          '&FullyQualifiedName!~ExecutedDefinitionDivergenceTests' +
          '&FullyQualifiedName!~WaveExecutedDefinitionHashTests'

# NO -v q on a TEST command: it deletes the Error Message/Expected/Actual/Stack Trace block the
# re-emit below exists to surface, defeating #179 by the flag alone (#462).
$log = & dotnet test $project --nologo --filter $filter 2>&1 | Out-String
$code = $LASTEXITCODE

Write-Output $log

# Zero-match guard (#455): keyed on the EXECUTED count (Passed + Failed), never on 'Total:' - which
# counts [Skip]ped tests, so a fully-skipped run would clear a Total-keyed guard and certify "the area
# is green" over nothing. Never on the "no tests matched" STRING either: that is verbosity-dependent
# and so never fires (#248).
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
    Write-Output "The Core area's EXISTING tests ($executed executed, $failed failed) are already failing on the starting code. Fix the pre-existing breakage before this plan builds on it - seven of sixteen stages modify src/Guardrails.Core/** and would inherit these failures as their own. If the failures are in TaskDefinitionHashTests or WaveDefinitionHashTests, stop: this plan's whole no-op claim (section 5.5) is read off those suites."
    exit 1
}

Write-Output "Baseline green: $executed existing Core tests executed, 0 failed."
exit 0
