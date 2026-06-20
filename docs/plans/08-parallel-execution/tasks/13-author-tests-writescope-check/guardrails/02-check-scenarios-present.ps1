# catches: a vacuous WriteScopeCheckTests that compiles-fails (passing tests-fail-on-current-code
#          trivially) but never encodes the load-bearing scenarios. THREE concerns, each pinned to a
#          NAMED test method (a bare keyword like "(?i)(test|.cs).*scope" matched a comment or an
#          unrelated mention - the 2nd review proved that gap):
#            - writeScope referenced at all;
#            - the TDD test-exclusion case (the triad replacement) via method TestFileExcludedFromScope -
#              an implementation scope that excludes test files catching an edit to a test file;
#            - the scoped-revert "fix, don't restart" property via method ScopedRevert_KeepsInScopeWip -
#              after a scope-violating attempt the IN-scope file KEEPS its attempt content while ONLY the
#              out-of-scope file is restored to taskBase. Without this, an OVER-reverting implementation
#              (git checkout <taskBase> -- .) that throws away in-scope WIP passes - the PO's
#              "fix, don't restart" requirement would be unverified.
#          Scoped to the one file this task owns (grep-scope rule).
$file = "tests/Guardrails.Integration.Tests/WriteScopeCheckTests.cs"
$text = Get-Content $file -Raw
if ($text -notmatch 'writeScope|WriteScope') {
    Write-Output "WriteScopeCheckTests does not reference writeScope - the check scenarios are not encoded"
    exit 1
}
# The TDD test-exclusion scenario, pinned to a NAMED method (not a bare keyword that a comment satisfies).
if ($text -notmatch 'TestFileExcludedFromScope') {
    Write-Output "WriteScopeCheckTests is missing the named method 'TestFileExcludedFromScope' - the TDD test-exclusion case (the triad replacement: an implementation scope that excludes test files must catch an edit to a test file) is not pinned structurally. A bare 'test/scope' keyword is no longer sufficient."
    exit 1
}
# The scoped-revert keeps-in-scope-WIP property, pinned to a NAMED method.
if ($text -notmatch 'ScopedRevert_KeepsInScopeWip') {
    Write-Output "WriteScopeCheckTests is missing the named method 'ScopedRevert_KeepsInScopeWip' - the scoped-revert 'fix, don't restart' property is unverified, so an OVER-reverting implementation (git checkout <taskBase> -- .) that discards the task's in-scope WIP would pass. The test must assert that after a scope-violating attempt the in-scope file KEEPS its attempt content while ONLY the out-of-scope file is restored to taskBase."
    exit 1
}
exit 0
