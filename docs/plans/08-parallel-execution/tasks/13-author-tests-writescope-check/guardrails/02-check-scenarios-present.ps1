# catches: a vacuous WriteScopeCheckTests that compiles-fails (passing tests-fail-on-current-code
#          trivially) but never encodes the load-bearing scenarios - the out-of-scope-fails case and
#          the TDD test-exclusion case (an implementation scope that excludes test files catching an
#          edit to a test file, the triad replacement). Scoped to the one file this task owns.
$file = "tests/Guardrails.Integration.Tests/WriteScopeCheckTests.cs"
$text = Get-Content $file -Raw
if ($text -notmatch 'writeScope|WriteScope') {
    Write-Output "WriteScopeCheckTests does not reference writeScope - the check scenarios are not encoded"
    exit 1
}
# The TDD test-exclusion scenario is the load-bearing one: the file must mention a test path being
# excluded from scope and an out-of-scope outcome.
if ($text -notmatch '(?i)(test|\.cs).*scope' -and $text -notmatch '(?i)scope.*(test|\.cs)') {
    Write-Output "WriteScopeCheckTests does not encode a test-file-vs-scope scenario - the TDD test-exclusion case (the triad replacement) is missing"
    exit 1
}
exit 0
