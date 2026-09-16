# catches: a pathspec test whose FIXTURE never creates the precondition it is named for — a
#          "…WhenAnUnrelatedFileIsStaged" test that stages nothing unrelated. That test is red on the
#          stub (so the census passes it) and green afterwards (so task 11's forward check passes it),
#          while proving NOTHING about the one property CommitPaths exists for: that an operator's
#          half-staged work never rides along on a supplier's commit. The red census can see a hollow
#          BODY; it cannot see a missing fixture.
#          It also pins the plan trait onto the class, which is what keeps the five shipped Drain_*
#          tests inside task 11's filter (design 41 §5 — "Drain's behaviour is unchanged").
#
# Source-shape check over the TEST file, and NOT demotable to a test (#468): no test can assert what
# another test's body sets up. This is a LOWER BOUND (#99): a token in a comment still matches, so it
# proves the file NAMES the fixture, not that it asserts on it. The census (02) is the gate that makes
# this worth having.
#
# Author-time smoke test (#302), re-runnable (#468):
#   $env:GR_SUBJECT='docs/plans/41-overwatcher-supply-autoresolve/tasks/10-author-tests-commit-paths/samples/03-covers-key-behaviors.valid.cs';   ./03-covers-key-behaviors.ps1  # expect 0
#   $env:GR_SUBJECT='docs/plans/41-overwatcher-supply-autoresolve/tasks/10-author-tests-commit-paths/samples/03-covers-key-behaviors.invalid.cs'; ./03-covers-key-behaviors.ps1  # expect 1
#
# baseline counts on tests/Guardrails.Core.Tests/Supply/SuppliedDrainTests.cs, the untouched tree —
# MEASURED with Select-String -CaseSensitive (same case sensitivity as the -cnotmatch operator), not assumed:
#   CommitPaths              0
#   OverwatchSupply          0   (the class carries [Trait("Category", "Supply")] today; 11 files do)
#   unrelated-staged\.txt    0
#   No other task in this plan writes this subject — measured across all 20 task.json writeScopes:
#   only 10-author-tests-commit-paths lists it, and this task has no ancestors (dependsOn: []).
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

$f = if ($env:GR_SUBJECT) { $env:GR_SUBJECT } else { "tests/Guardrails.Core.Tests/Supply/SuppliedDrainTests.cs" }

# PRECONDITION — the only early exit: every clause below would crash on a missing subject.
if (-not (Test-Path $f)) {
    Write-Output "$f does not exist — the tests this task owns were never written"
    exit 1
}

# The clauses read the RAW file deliberately: two of the three are tokens a correct file may legitimately
# carry inside a string literal or an attribute, and the third ([Trait]) is an attribute, not a comment
# hazard. Comment-stripping would buy nothing here and would make the trait clause unfirable.
$raw = Get-Content $f -Raw

# ACCUMULATE, never exit 1 per clause (#478): one attempt reports every gap instead of one gap per attempt.
$failures = @()

# -cnotmatch on every required-present clause: these are C# identifiers and an attribute value, and a
# case-insensitive clause false-GREENS on text C# would never compile (taxonomy 3).
if ($raw -cnotmatch 'CommitPaths') {
    $failures += "$f never mentions CommitPaths — the member this task exists to pin red is untested (design 41 §5)"
}

if ($raw -cnotmatch '\[Trait\("Category",\s*"OverwatchSupply"\)\]') {
    $failures += "$f does not carry [Trait(`"Category`", `"OverwatchSupply`")] — without the plan trait this class falls outside the filter 'Category=OverwatchSupply&FullyQualifiedName~SuppliedDrainTests', so BOTH halves of this pair would certify an empty set, and the five shipped Drain_* tests would never run in the only place that proves Drain's behaviour survived the refactor."
}

if ($raw -cnotmatch 'unrelated-staged\.txt') {
    $failures += "$f never stages the unrelated file 'unrelated-staged.txt' — CommitPaths_CommitsOnlyItsPathspec_WhenAnUnrelatedFileIsStaged and Drain_CommitsOnlyTheStagedFiles_WhenAnUnrelatedFileIsStaged both need something ELSE sitting in the index, or they assert an explicit pathspec against an index that has nothing to leak. Name the file exactly 'unrelated-staged.txt' (the prompt pins it)."
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Output $_ }
    exit 1
}
exit 0
