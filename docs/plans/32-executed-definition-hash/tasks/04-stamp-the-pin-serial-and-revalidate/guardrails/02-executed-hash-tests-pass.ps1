# catches: a substitution that compiles and does not actually fix the defect. Stage 1's four pins are the
#          behavioural half of this stage's verdict, and two of them were RED before it:
#            P1  the recorded hash is the PRE-EDIT pin when task.json is edited mid-run (serial, W1);
#            P14 the pin is captured at LOAD, not at attempt start - the discriminator against an
#                implementation that captures per attempt, which passes P1, P2, P3, P4, P5, P9, P11 and
#                P13 because a single mid-run edit lands after both.
#          The other two - P5 (an unedited run records a byte-identical hash) and P8 (Compute's output
#          has not moved) - were GREEN before and must STAY green: they are the no-migration claim
#          (section 5.5) and the tripwire on any later "simplification" of the hashed file set.
#
#          Re-emits the assertion/exception lines at the END so they reach the harness retry-feedback
#          tail; default dotnet test prints them mid-run and ends with only the [FAIL] name (#179).
$ErrorActionPreference = 'Continue'

# The summary line the zero-match guard reads is LOCALIZED - pin the culture BEFORE the run (#455).
$env:DOTNET_CLI_UI_LANGUAGE = 'en'

# NAMESPACE-QUALIFIED, and it is not decoration (#455 companion (a)): stage 8's class
# 'WaveExecutedDefinitionHashTests' CONTAINS the substring 'ExecutedDefinitionHashTests', so a BARE class
# term would silently widen to select stage 8's file too once it lands. The FQN prefix is what breaks the
# containment - 'Guardrails.Core.Tests.WaveExecutedDefinitionHashTests' does NOT contain
# 'Guardrails.Core.Tests.ExecutedDefinitionHashTests', because the '.' separator sits between the prefix
# and the 'Wave'. Verified against every other class this plan authors and every existing class in the
# project.
#
# THE PREFIX IS 'Guardrails.Core.Tests', NOT 'Guardrails.Core.Tests.Journal' - and that is a CORRECTION
# made after a run halted here with a defective-guardrail escalation. The file lives in the Journal/
# FOLDER, but declaring `namespace Guardrails.Core.Tests.Journal` anywhere in this assembly introduces a
# `Journal` member under `Guardrails.Core.Tests`, which then WINS the enclosing-namespace walk over the
# production `Guardrails.Core.Journal` for every unqualified `Journal.X` reference in the assembly. Three
# files break with CS0234, all outside task 01's write scope: OverwatchNoVerdictTests.cs:355
# (`Journal.TaskStatus.Running`), the shared helper WavePlanBuilder.cs, and - the part that makes this
# unarguable - Journal/JudgeSpendRecordingTests.cs, the sibling task 01's prompt says to MIRROR, whose
# own header comment at :9-14 documents this exact hazard verbatim and names OverwatchNoVerdictTests.cs.
# Folder and namespace are deliberately decoupled in that folder; this filter follows the namespace.
#
# Stage 8's tests are legitimately RED until stage 9, so widening onto them would fail THIS stage for a
# reason it cannot fix. This is the SAME string stage 1's inverse census uses; copy it verbatim, never
# re-derive it.
$filter = 'FullyQualifiedName~Guardrails.Core.Tests.ExecutedDefinitionHashTests'

# NO -v q on a TEST command: it suppresses the Error Message/Expected/Actual/Stack Trace block, leaving
# only the [FAIL] line for the re-emit below to find - defeating #179 by the flag alone (#462).
$out = & dotnet test tests/Guardrails.Core.Tests --filter $filter --nologo 2>&1
$testExit = $LASTEXITCODE                                  # capture BEFORE any other statement
$out | ForEach-Object { Write-Output $_ }

# EXIT CODE FIRST, guard second (#455 forward polarity): a test host that never ran exits NON-zero with
# no summary at all, so checking the exit code first reports its real error instead of blaming the filter.
if ($testExit -ne 0) {
    $detail = $out |
        Select-String -Pattern '\[FAIL\]|Error Message:|Assert\.|Exception|Stack Trace:|Expected:|Actual:' |
        ForEach-Object { $_.Line } |
        Select-Object -First 40                            # bound the block so it fits the ~60-line tail
    Write-Output ""
    Write-Output "=== Failure details (re-emitted so they land in the harness feedback tail) ==="
    if ($detail) { $detail | ForEach-Object { Write-Output $_ } }
    else { Write-Output "(no assertion/exception lines matched - inspect the full log above)" }
    Write-Output "ExecutedDefinitionHashTests is red after stage 4. If P1 failed, the serial settle is still recording the POST-edit hash - the substitution did not land at AttemptJournaler.CompleteSucceededOrInvalidFragment. If P14 failed, the capture is happening at ATTEMPT START rather than at plan load, which is candidate (2) in disguise (section 5.7) - the fix is in stage 3's loader capture, not here, so escalate rather than working around it. If P5 or P8 failed, a recorded hash MOVED and the plan's whole no-migration claim is broken. The test file is outside your writeScope: fix the implementation, never the assertion."
    exit 1
}

# ZERO-MATCH GUARD (#455): exit 0 alone does NOT mean tests passed - a --filter that matches nothing, or
# is malformed, also exits 0. Key on the EXECUTED count (Passed+Failed); 'Total:' would also count
# [Skip]ped tests, so a fully-skipped class would clear a Total-keyed guard.
$ran = ([regex]::Matches(($out | Out-String), '(?:Passed|Failed):\s*(\d+)') |
        ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum
if ($ran -lt 1) {
    Write-Output "exit 0 but ZERO tests executed - this guardrail certified nothing. The filter '$filter' matched no tests, is malformed, or every match is [Skip]ped. Check it against the class stage 1 actually authored: namespace Guardrails.Core.Tests (FLAT - the file is in the Journal/ folder but the namespace is not nested, because nesting it shadows the production Guardrails.Core.Journal for the whole assembly), class ExecutedDefinitionHashTests."
    exit 1
}
Write-Output "Serial write sites verified: $ran pins executed, none failed."
exit 0
