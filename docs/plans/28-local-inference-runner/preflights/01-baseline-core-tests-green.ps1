# catches: a plan that builds on ALREADY-RED existing tests in Guardrails.Core.Tests. Every task in this
#          plan modifies src/Guardrails.Core/** (the Role seam, the extractor, the containment primitive,
#          the runner, the loader diagnostics), and each one's tests-pass guardrail would then fail from
#          PRE-EXISTING breakage it cannot fix - misattributed to the task, burning its whole retry budget
#          and ending at needs-human with its own deliverable complete. "Never build on red" (#181).
#
# SCOPE: the EXISTING Core tests only, via an FQN exclusion of every test class THIS plan authors. A
#        whole-project `dotnet test` here would hit the #165/#176 compile-coupling trap once an
#        author-tests task has landed its intentionally-red tests. The exclusion is written as
#        FullyQualifiedName!~<Class> rather than a shared plan-wide trait DELIBERATELY: plan 27 measured
#        the trait form misattributing a sibling task's red tests to this baseline (the #455 shape one
#        level up), and an FQN list names exactly what it excludes.
#
# Required-present baseline (#478): this guardrail asserts a POSITIVE precondition on the STARTING tree,
#        so it is green-on-arrival BY DESIGN - the class Step 7.0a exempts, alongside the wave ENTRY gate.
#        Measured on the starting tree at authoring time: 2026 executed, 0 failed.
$ErrorActionPreference = 'Continue'

# The summary line is LOCALIZED (a German-culture box prints 'gesamt:' and no 'Total:'), which would
# invert the zero-match guard into an unconditional failure. Pin it before the run, not after (#455).
$env:DOTNET_CLI_UI_LANGUAGE = 'en'

$project = 'tests/Guardrails.Core.Tests/Guardrails.Core.Tests.csproj'
if (-not (Test-Path $project)) {
    Write-Output "PRECONDITION: $project not found - this preflight is scoped to the Core test project and cannot run without it."
    exit 1
}

# Every test class this plan authors, excluded so the baseline can never go red on a not-yet-written test.
$filter = 'FullyQualifiedName!~PromptRoleSeamTests' +
          '&FullyQualifiedName!~PromptJsonExtractorTests' +
          '&FullyQualifiedName!~PromptToolContainmentTests' +
          '&FullyQualifiedName!~OpenAiCompatDiagnosticsTests' +
          '&FullyQualifiedName!~ActionReachabilityGateTests' +
          '&FullyQualifiedName!~JudgeSpendRecordingTests'

$log = & dotnet test $project --nologo --filter $filter 2>&1 | Out-String
$code = $LASTEXITCODE

Write-Output $log

# Zero-match guard (#455): keyed on the EXECUTED count (Passed + Failed), never on 'Total:' - which counts
# [Skip]ped tests, so a fully-skipped run would clear a Total-keyed guard. Never on the "no tests matched"
# STRING either: that is verbosity-dependent and so never fires (#248).
$passed = 0
$failed = 0
if ($log -match 'Passed:\s*(\d+)')  { $passed = [int]$Matches[1] }
if ($log -match 'Failed:\s*(\d+)')  { $failed = [int]$Matches[1] }
$executed = $passed + $failed

if ($executed -lt 1) {
    Write-Output "BASELINE FILTER MATCHED NOTHING: 0 tests executed in $project. The exclusion filter is wrong or the test host never ran - this preflight is certifying nothing. Fix the filter before running the plan."
    exit 1
}

if ($code -ne 0) {
    # #179: re-emit the failure DETAIL at the END so the WHY reaches the halt feedback, not just [FAIL] names.
    Write-Output ""
    Write-Output "=== Pre-existing failures in Guardrails.Core.Tests (detail re-emitted) ==="
    foreach ($line in ($log -split "`r?`n")) {
        if ($line -match '^\s*(\[FAIL\]|Failed\s|Error Message:|Expected:|Actual:|\s+at\s)') {
            Write-Output $line
        }
    }
    Write-Output ""
    Write-Output "The Core area's EXISTING tests ($executed executed, $failed failed) are already failing on the starting code. Fix the pre-existing breakage before this plan builds on it - every task here modifies src/Guardrails.Core/** and would inherit these failures as its own."
    exit 1
}

Write-Output "Baseline green: $executed existing Core tests executed, 0 failed."
exit 0
