# catches: a vacuous ReVerifierSeamTests that compiles-fails (passing tests-fail-on-current-code
#          trivially on, say, only the IReVerifier reference) while never encoding the load-bearing
#          §4.3 scenario - the ATTEMPT-DECOUPLING point: the re-verify seam runs where no action ran, so
#          a guardrail set under it must NOT see GUARDRAILS_ACTION_STDOUT/_STDERR/_RESULT. A harness
#          that skipped that negative assertion would let an attempt-bound re-verify slip through. Assert
#          both the seam reference AND the GUARDRAILS_ACTION_* attempt-env negative are present. Scoped
#          to the one file this task owns (grep-scope rule - no project-tree greps).
$file = "tests/Guardrails.Core.Tests/ReVerifierSeamTests.cs"
if (-not (Test-Path $file)) {
    Write-Output "$file does not exist - the re-verifier seam test file was not authored"
    exit 1
}
$text = Get-Content $file -Raw
if ($text -notmatch 'IReVerifier') {
    Write-Output "ReVerifierSeamTests does not reference IReVerifier - the seam under test is missing"
    exit 1
}
# The attempt-decoupling negative, pinned to a NAMED method (a bare 'GUARDRAILS_ACTION_' string is
# satisfied by a comment - the 2nd review proved that gap). The test method must exist and assert the
# re-verify context does NOT expose GUARDRAILS_ACTION_STDOUT/_STDERR/_RESULT.
if ($text -notmatch 'ReVerify_DoesNotReadActionEnv') {
    Write-Output "ReVerifierSeamTests is missing the named method 'ReVerify_DoesNotReadActionEnv' - the attempt-decoupling negative (the re-verify seam must NOT read the attempt action env GUARDRAILS_ACTION_STDOUT/_STDERR/_RESULT) is not pinned structurally. A bare 'GUARDRAILS_ACTION_' string is satisfied by a comment."
    exit 1
}
exit 0
