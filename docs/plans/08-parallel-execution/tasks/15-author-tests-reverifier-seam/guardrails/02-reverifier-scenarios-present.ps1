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
# The attempt-decoupling negative: the test must reference the GUARDRAILS_ACTION_* attempt env to assert
# it is absent/empty in the re-verify context. Without this, the attempt-decoupling point is unverified.
if ($text -notmatch 'GUARDRAILS_ACTION_') {
    Write-Output "ReVerifierSeamTests does not reference GUARDRAILS_ACTION_* - the attempt-decoupling negative (the re-verify seam must NOT read the attempt action env) is not encoded"
    exit 1
}
exit 0
