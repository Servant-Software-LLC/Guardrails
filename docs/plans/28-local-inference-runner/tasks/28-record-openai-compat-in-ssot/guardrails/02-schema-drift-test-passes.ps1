# catches: a canonical block edited into MALFORMED JSON, or edited in a way that makes it stop
#          validating. The block is not prose - `SchemaDriftTests` PARSES it and loads it as a real
#          config (`CanonicalPromptRunnersBlock_ValidatesClean_AndConfiguresNoTiering`), so a stray
#          comma there is a broken contract, not a typo. Without this the breakage would surface two
#          tasks later at the terminal gate, in a task that cannot fix it.
#
# This is the repo's OWN test, not a re-implementation. Running it here rather than re-deriving the
# parse in PowerShell is deliberate: two parsers on one property is the drift trap this plan is about.
#
# SCOPE (#455) - the filter is METHOD-scoped, and must NOT be widened back to the class. A
#          `~SchemaDriftTests` filter also selects PromptRunnersSkillCopyMatchesSsot, which this task
#          CANNOT satisfy: the moment task 28 correctly adds the openai-compat keys to the canonical
#          block, the skill copy diverges, and only task 29 is scoped to mirror it - and task 29
#          dependsOn task 28. That is a circular deadlock, and it is exactly the "failing forward"
#          trap #455 names. It halted this task once, with the content already complete and correct.
#          The skill-copy assertion is NOT lost: task 29's own guardrail runs the whole class, which is
#          right for 29 because by then both halves can pass. Coverage moves to the task that can
#          satisfy it rather than disappearing.
$ErrorActionPreference = 'Continue'

$env:DOTNET_CLI_UI_LANGUAGE = 'en'

$project = 'tests/Guardrails.Core.Tests/Guardrails.Core.Tests.csproj'
$filter = 'FullyQualifiedName~SchemaDriftTests.CanonicalPromptRunnersBlock_ValidatesClean_AndConfiguresNoTiering'

$log = & dotnet test $project --nologo --filter $filter 2>&1 | Out-String
$code = $LASTEXITCODE

Write-Output $log

if ($code -ne 0) {
    Write-Output ""
    Write-Output "=== Failure detail (re-emitted at the END so the WHY reaches the retry feedback, #179) ==="
    foreach ($line in ($log -split "`r?`n")) {
        if ($line -match '^\s*(\[FAIL\]|Error Message:|Expected:|Actual:|\s+at\s)') { Write-Output $line }
    }
    Write-Output ""
    Write-Output "The canonical block no longer parses or validates. The JSON you wrote is broken and only you can fix it."
    exit 1
}

$passed = 0
$failed = 0
if ($log -match 'Passed:\s*(\d+)') { $passed = [int]$Matches[1] }
if ($log -match 'Failed:\s*(\d+)') { $failed = [int]$Matches[1] }
$executed = $passed + $failed

if ($executed -lt 1) {
    Write-Output "FILTER MATCHED NOTHING: 0 tests executed for '$filter'. SchemaDriftTests was renamed or removed - this check is certifying nothing."
    exit 1
}

Write-Output "Canonical block parses and validates: $executed drift test(s) executed, 0 failed."
exit 0
