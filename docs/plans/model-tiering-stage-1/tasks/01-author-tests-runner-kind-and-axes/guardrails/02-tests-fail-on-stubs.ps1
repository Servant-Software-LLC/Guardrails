# catches: a test file that PASSES against the throwing stubs - i.e. asserts nothing real (a tautology),
#          or the agent implemented the behaviour instead of stubbing it. With the build already green
#          (01), a non-zero test exit here unambiguously means the tests RAN and FAILED = TDD red.
#          SCOPED TO THIS TASK'S OWN TEST CLASS. The bare plan-wide `Category=ModelTieringStage1`
#          trait selects EVERY Stage 1 test across all six task pairs, which broke this two ways: a
#          tests-pass guardrail deadlocked behind a sibling's INTENDED-RED tests that only a DOWNSTREAM
#          task fixes, and a tests-fail-on-stubs guardrail was satisfied by ANY sibling's red tests
#          instead of its own. A task guardrail may only assert what THIS task can fix.
$ErrorActionPreference = 'Stop'
$filter = 'Category=ModelTieringStage1&FullyQualifiedName~PromptRunnerSchemaTests'
$log = & dotnet test tests/Guardrails.Core.Tests/Guardrails.Core.Tests.csproj --filter $filter --nologo -v q 2>&1
$code = $LASTEXITCODE

# Zero-match guard. A --filter selecting NOTHING exits 0 and, under `-v q`, prints NO diagnostic at
# all - not even "No test matches the given testcase filter". Verified against real `dotnet test`
# output on this SDK (issue #248 - never pattern-match a tool's console text you have not observed):
#   zero match  -> exit 0, output ends at "A total of 1 test files matched the specified pattern."
#   >=1 match   -> a summary line "Passed!  - Failed: 0, Passed: 11, Skipped: 0, Total: 11, ..."
# So the discriminator is the PRESENCE of a Total: count >= 1, not any error string. Without this,
# renaming the class or dropping its Category trait silently vacates the check instead of failing it.
$total = 0
$m = [regex]::Match(($log -join "`n"), 'Total:\s*(\d+)')
if ($m.Success) { $total = [int]$m.Groups[1].Value }
if ($total -lt 1) {
    Write-Output "FILTER MATCHED NO TESTS ($filter) - this guardrail verified NOTHING (dotnet test ran 0 tests and exited 0). Was PromptRunnerSchemaTests renamed, or its Category trait dropped?"
    exit 1
}

if ($code -eq 0) {
    Write-Output "The authored tests PASS against the NotImplementedException stubs - they assert nothing real. Encode the plan's behaviours so they fail before the implementation lands."
    exit 1
}
exit 0
