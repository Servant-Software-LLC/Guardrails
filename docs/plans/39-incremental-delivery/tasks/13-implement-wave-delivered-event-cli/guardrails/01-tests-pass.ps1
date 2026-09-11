# catches: an implementation that does not satisfy the tests authored upstream — and, via the
#          zero-match guard, a filter that silently selects nothing (which exits 0 and would certify
#          the task on an empty set, #455).
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

$env:DOTNET_CLI_UI_LANGUAGE = 'en'

$out = & dotnet test "tests/Guardrails.Core.Tests/Guardrails.Core.Tests.csproj" -c Debug --nologo --filter "FullyQualifiedName~WaveDeliveredCliForwardingTests" 2>&1 | Out-String
$code = $LASTEXITCODE

Write-Output $out

# Exit-code check FIRST on the forward polarity: a test host that never ran must not be misreported
# as a bad filter.
if ($code -ne 0) {
    Write-Output ""
    Write-Output "=== FAILURE detail (re-emitted at the END for the ~60-line retry tail, #179) ==="
    foreach ($line in ($out -split "`r?`n")) {
        if ($line -match '^\s*(Error Message|Expected|Actual|Stack Trace|Assert\.|\s+at |String:|Found:)') {
            Write-Output $line
        }
    }
    exit 1
}

$passed = 0
$failed = 0
if ($out -match 'Passed:\s+(\d+)') { $passed = [int]$Matches[1] }
if ($out -match 'Failed:\s+(\d+)') { $failed = [int]$Matches[1] }
if (($passed + $failed) -lt 1) {
    Write-Output "ZERO-MATCH: the filter 'FullyQualifiedName~WaveDeliveredCliForwardingTests' executed no tests — that exits 0 and would certify this task on an empty set."
    exit 1
}

exit 0
