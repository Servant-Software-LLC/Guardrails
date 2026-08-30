# catches: a test-author task whose tests do not actually go RED - the strongest anti-tautology check
#          this plan has. A file that does not COMPILE exits non-zero identically to one that compiles
#          and fails, so garbage would pass; 01-build-passes runs first and closes that, which makes a
#          non-zero exit HERE mean the tests ran and FAILED against the stubs (#155).
#
# SCOPE (#455): filtered to THIS task pair's OWN test class. A plan-wide trait here would let ANY
#          sibling's intended-red tests satisfy the check, degrading the TDD-red proof into merge-order
#          luck - the worse half of the #455 defect, because it fails silently.
$ErrorActionPreference = 'Continue'

$env:DOTNET_CLI_UI_LANGUAGE = 'en'

$project = 'tests/Guardrails.Integration.Tests/Guardrails.Integration.Tests.csproj'
$filter = 'FullyQualifiedName~OpenAiCompatTransportTests'

$log = & dotnet test $project --nologo --filter $filter 2>&1 | Out-String
$code = $LASTEXITCODE

Write-Output $log

# INVERSE polarity: the zero-match guard runs FIRST. A crash or an empty selection also exits
# non-zero, and without this guard that would be certified as "TDD red" - the exact tautology this
# guardrail exists to prevent.
$passed = 0
$failed = 0
if ($log -match 'Passed:\s*(\d+)') { $passed = [int]$Matches[1] }
if ($log -match 'Failed:\s*(\d+)') { $failed = [int]$Matches[1] }
$executed = $passed + $failed

if ($executed -lt 1) {
    Write-Output "FILTER MATCHED NOTHING: 0 tests executed for '$filter'. A non-zero exit over an empty selection is not TDD red - the class was never authored, or is named differently than the plan pinned."
    exit 1
}

if ($failed -lt 6) {
    Write-Output "EXPECTED AT LEAST 6 FAILING TEST(S), saw $failed (of $executed executed). These tests are supposed to FAIL against the stubs - a green suite here means they are not bound to the not-yet-written behaviour, which is a tautology that would let the implementation task pass by doing nothing."
    exit 1
}

Write-Output "TDD red confirmed: $executed executed, $failed failed against the stubs."
exit 0
