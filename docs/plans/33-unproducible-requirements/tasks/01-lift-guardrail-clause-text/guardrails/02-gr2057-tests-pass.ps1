# catches: a lift that changed GR2057's BEHAVIOUR while still compiling - a regex reflowed, an
#          alternation reordered, a character class retightened. The existing suite is the property;
#          this task's whole contract is that it still passes with the helpers living somewhere else.
#          The suite file itself is OUT of this task's writeScope, so "unedited" is enforced
#          deterministically by the harness's write-scope check (#155) - this guardrail proves the
#          BEHAVIOUR, not the bytes.
$ErrorActionPreference = 'Continue'
# The summary line is LOCALIZED (a German-culture box prints 'gesamt:' and no 'Total:'), which would
# invert the zero-match guard into an unconditional failure. Pin it before the run (#455).
$env:DOTNET_CLI_UI_LANGUAGE = 'en'

$project = 'tests/Guardrails.Core.Tests/Guardrails.Core.Tests.csproj'
$filter  = 'FullyQualifiedName~GuardrailRequiresForbiddenTokenTests'

# NO -v q on a TEST command: it deletes the Error Message/Expected/Actual/Stack Trace block the re-emit
# below exists to surface, defeating #179 by the flag alone.
$log = & dotnet test $project --nologo --filter $filter 2>&1 | Out-String
$code = $LASTEXITCODE
Write-Output $log

# Forward polarity: exit-code check FIRST, so a test host that never ran is not misreported as a bad
# filter. Then the zero-match guard, keyed on the EXECUTED count (Passed + Failed), never on 'Total:'
# (which counts [Skip]ped tests) and never on the "no tests matched" string (verbosity-dependent, #248).
if ($code -ne 0) {
    Write-Output ""
    Write-Output "=== GR2057 failures (detail re-emitted) ==="
    foreach ($line in ($log -split "\r?\n")) {
        if ($line -match '^\s*(\[FAIL\]|Failed\s|Error Message:|Expected:|Actual:|\s+at\s)') { Write-Output $line }
    }
    Write-Output ""
    Write-Output "The existing GR2057 suite (GuardrailRequiresForbiddenTokenTests) fails after the lift. This task is a PURE REFACTOR: the six helpers plus IsCommentLine move to GuardrailClauseText unchanged. Do not adjust the tests - they are out of your writeScope - and do not 'fix' a regex to make one pass; restore the moved member to its original text instead."
    exit 1
}

$passed = 0; $failed = 0
if ($log -match 'Passed:\s*(\d+)') { $passed = [int]$Matches[1] }
if ($log -match 'Failed:\s*(\d+)') { $failed = [int]$Matches[1] }
$executed = $passed + $failed
if ($executed -lt 1) {
    Write-Output "FILTER MATCHED NOTHING: 0 tests executed for '$filter'. The GR2057 suite is the only proof this refactor preserved behaviour, so a filter that selects nothing certifies nothing. Fix the filter or the test class name."
    exit 1
}

Write-Output "GR2057 behaviour preserved: $executed tests executed, 0 failed."
exit 0
