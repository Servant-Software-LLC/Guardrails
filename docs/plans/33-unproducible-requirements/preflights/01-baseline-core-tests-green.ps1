# catches: a plan that builds on ALREADY-RED existing tests in Guardrails.Core.Tests. Six of this plan's
#          tasks write into src/Guardrails.Core/** - the clause-helper lift (1), the git probe and the
#          fifth PlanValidator overload (2), GR2060 itself (4), the JIT-gate allow-list in Scheduler.cs
#          (6) and the code ladder (8) - and Guardrails.Core.Tests is the ONLY suite that drives those
#          surfaces. Without this baseline a pre-existing red would be misattributed to whichever task
#          happened to run next, burn its whole retry budget, and end at needs-human with its own
#          deliverable complete (#181).
#
# SCOPE: the EXISTING Core tests only, via an FQN exclusion of the three Core test classes THIS plan
#        authors. A whole-project `dotnet test` here would hit the #165/#176 compile-coupling trap the
#        moment task 3 lands its intentionally-red tests. The exclusion is written as
#        FullyQualifiedName!~<Class>, NOT as a shared plan-wide trait: this plan introduces no trait at
#        all, following the shipped plan-30/31/32 precedent.
#
#        Discriminating-substring check (#455 companion (a)), run MECHANICALLY - not by eye - against
#        every existing Core test class name, harvested as
#        `grep -rho "class [A-Za-z0-9_]*Tests" tests/Guardrails.Core.Tests --include=*.cs | sort -u`
#        (209 distinct names on master @67859c7; the method is written out because the number moves and
#        a bare count cannot be re-checked). All three excluded names matched ZERO of them. Checked in
#        the other direction too: no excluded name is a substring of another, so none swallows a sibling.
#        In particular `!~ProducerCoverageTests` does NOT exclude ProducerCoverageCorpusTests - the
#        latter reads ...CoverageCorpusTests, which does not contain the former - which is why both are
#        listed rather than relying on one prefix to cover both.
#
#        ONE METHOD-LEVEL EXCLUSION IS NEEDED, and the claim that none was is a MEASURED CORRECTION
#        (#574 - a baseline that halted a plan-32 run on a red the plan itself had created; this is the
#        same defect, recurring). The original note here reasoned that task 6 "touches exactly one member
#        of Scheduler.cs" and that "no shipped Core assertion pins either". The second half was WRONG, and
#        the run proved it: BreakdownSalvageAllowListTests.TheAllowListIsExactlyOneCode_... is a TRIPWIRE
#        whose stated purpose is to fail the moment a second code joins UnsatisfiableWhileIncomplete -
#        "which is the entire point of an allow-list over a category". Task 6 adds GR2060, so the tripwire
#        fires BY DESIGN. Excluded at METHOD level, not class level, so that class's other shipped facts
#        stay in the baseline.
#
#        THIS EXCLUSION IS NOT A SUPPRESSION. Task 6 now OWNS that test file and must update the
#        assertion carrying section 5.3's argument - the tripwire is demanding a justification and this
#        plan has one. The unfiltered terminal gate (02-core-suite-passes) re-runs the whole suite on the
#        merged HEAD, so a task 6 that merely deletes the assertion is caught there.
#
# Required-present baseline (#478): this guardrail asserts a POSITIVE precondition on the STARTING tree,
#        so it is green-on-arrival BY DESIGN - the class Step 7.0a exempts. MEASURED on master @67859c7,
#        unfiltered: 2260 passing, 0 failing, 0 skipped (Total 2260), in 53 s. The three excluded classes
#        do not exist yet, so the filter drops nothing today and the executed count equals that 2260.
#        The guard below is still keyed on Passed+Failed rather than 'Total:' - today's zero skips are a
#        fact about today's tree, not a property the guard may assume.
$ErrorActionPreference = 'Continue'

# The summary line is LOCALIZED (a German-culture box prints 'gesamt:' and no 'Total:'), which would
# invert the zero-match guard into an unconditional failure. Pin it before the run, not after (#455).
$env:DOTNET_CLI_UI_LANGUAGE = 'en'

$project = 'tests/Guardrails.Core.Tests/Guardrails.Core.Tests.csproj'
if (-not (Test-Path $project)) {
    Write-Output "PRECONDITION: $project not found - this preflight is scoped to the Core test project and cannot run without it."
    exit 1
}

# The three Core test classes this plan authors: task 3 -> ProducerCoverageTests; task 5 ->
# JitPrefixVetoTests; task 9 -> ProducerCoverageCorpusTests.
$filter = 'FullyQualifiedName!~ProducerCoverageTests' +
          '&FullyQualifiedName!~JitPrefixVetoTests' +
          '&FullyQualifiedName!~ProducerCoverageCorpusTests' +
          '&FullyQualifiedName!~TheAllowListIsExactlyOneCode'

# NO -v q on a TEST command: it deletes the Error Message/Expected/Actual/Stack Trace block the re-emit
# below exists to surface, defeating #179 by the flag alone.
$log = & dotnet test $project --nologo --filter $filter 2>&1 | Out-String
$code = $LASTEXITCODE

Write-Output $log

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
    Write-Output "The Core area's EXISTING tests ($executed executed, $failed failed) are already failing on the starting code. Fix the pre-existing breakage before this plan builds on it - tasks 1, 2, 4, 6 and 8 modify src/Guardrails.Core/** and would inherit these failures as their own. Measured green at 2260/2260 on master @67859c7, so a red here is a real regression on the starting tree, not a filter artifact."
    exit 1
}

Write-Output "Baseline green: $executed existing Core tests executed, 0 failed."
exit 0
