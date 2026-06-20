# catches: a vacuous MergeLockAndSettleTests that compiles-fails (passing tests-fail-on-current-code
#          trivially) but never encodes the load-bearing scenarios. The OLD gate pinned only
#          ff-only/preHead/trailer - so a settle suite that tested ONLY the FF-is-free path (and never
#          the non-FF B1 four-effect rollback or the fragment-before-commit ORDERING) passed it, leaving
#          a journal-before-commit / commit-before-fragment implementation (the B1 split-brain) and a
#          missing four-effect rollback unverified. We now also require the four-effect rollback +
#          ordering vocabulary:
#            - mergeSequence  (the B1 effect that must NOT be consumed on rollback);
#            - fragment       (the state-fragment effect - written before the commit, absent on rollback);
#            - a needs-human term (the honest-halt verdict on a failed re-verify);
#            - the ordering needle Fragment_Written_Before_Commit (a NAMED method pinning fragment-before
#              -commit, so a journal-before-commit impl cannot pass).
#          Scoped to the one file this task owns.
$file = "tests/Guardrails.Integration.Tests/MergeLockAndSettleTests.cs"
$text = Get-Content $file -Raw

# (1) The FF-is-free + trailer vocabulary (literal-substring needles).
$needles = @('ff-only', 'preHead', 'trailer')
$missing = @()
foreach ($n in $needles) {
    if ($text -notmatch [regex]::Escape($n)) { $missing += $n }
}
if ($missing.Count -gt 0) {
    Write-Output "MergeLockAndSettleTests is missing the load-bearing FF settle scenario term(s) [$($missing -join ', ')] - FF-is-free / trailer-on-FF-commit not encoded"
    exit 1
}

# (2) The B1 four-effect rollback vocabulary so a journal-before-commit / FF-only suite can't pass.
#     mergeSequence + fragment are literal; the needs-human term tolerates the hyphen/camel spellings.
foreach ($n in @('mergeSequence', 'fragment')) {
    if ($text -notmatch [regex]::Escape($n)) {
        Write-Output "MergeLockAndSettleTests is missing the B1 four-effect rollback term '$n' - the non-FF union rollback (reset --hard preHead; NO fragment; mergeSequence NOT consumed; needs-human) is not encoded; a FF-only suite would pass without it"
        exit 1
    }
}
if ($text -notmatch '(?i)needs-?human') {
    Write-Output "MergeLockAndSettleTests does not reference needs-human - the honest-halt verdict on a failed non-FF re-verify (the B1 four-effect rollback) is not encoded"
    exit 1
}

# (3) The settle ORDERING, pinned to a NAMED method - fragment BEFORE the git commit (reversing it is the
#     B1 split-brain). A bare 'fragment' mention does not prove the ordering is asserted.
if ($text -notmatch 'Fragment_Written_Before_Commit') {
    Write-Output "MergeLockAndSettleTests is missing the named method 'Fragment_Written_Before_Commit' - the B1 ordering (state fragment written BEFORE the git integration commit) is not pinned, so a journal-before-commit / commit-before-fragment implementation (the B1 split-brain) would pass."
    exit 1
}

exit 0
