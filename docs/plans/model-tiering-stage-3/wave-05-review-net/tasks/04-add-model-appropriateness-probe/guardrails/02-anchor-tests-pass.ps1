# catches: a probe that was written but does not carry the doctrine, or carries it in the wrong place.
#          `03-author-tests-review-net-doctrine` pinned fourteen clauses and three insertion points and
#          proved every one of them RED against the skill as it stood; this is where they go green, and it
#          is the only guardrail in the wave that reads the delivered prose at all.
#
#          Two failure modes it exists for. (1) A paraphrase: the probe says the right thing in the wrong
#          words, which reads perfectly to a human and leaves the anchor set pinning sentences the skill
#          does not contain - so the next edit that guts the doctrine goes undetected forever, which is the
#          exact reason a test reads markdown here at all. (2) Placement: a probe appended after the
#          Quality bar satisfies all fourteen clauses and belongs to no section, which
#          TheThreeInsertionsLandInTheirOwnSections is the only thing that catches.
#
#          Re-emits the failure DETAIL at the END so the WHY reaches the retry tail (#179, dotnet.md 4.2).
#          On a paraphrase the assertion message names the doctrine and quotes the clause, which is exactly
#          what the next attempt needs.
#
# SCOPE (#455): ONE class, in one project - `ModelAppropriatenessDoctrineAnchorTests`, which is a substring
# of no other test class anywhere under tests/ (verified 2026-08-24: it occurs nowhere in the tree on this
# wave's entry tree). It is authored by this task's only ancestor, so it is present in this tree, and it is
# made green by this task alone - no sibling's tests can satisfy it and it waits on no descendant.
#
#          It deliberately does NOT run SeamDoctrineAnchorTests, which pins other clauses in this same
#          skill file: that is the wave exit gate's job, on the merged HEAD, where an unfiltered suite can
#          see collateral damage a task-level filter never will.
#
# LOCAL - no `scope` key. This asserts "the probe is written", which cannot be true before this task's own
# action has run, so it fails the #125 union-safe test and must never be tagged integration (#250).
#
# FORWARD polarity: the exit-code check runs FIRST, so a test host that never started is reported as a run
# failure rather than mis-diagnosed as a bad filter (#455).
$ErrorActionPreference = 'Continue'
$env:DOTNET_CLI_UI_LANGUAGE = 'en'    # the summary line the zero-match guard reads is LOCALIZED (#455)

$filter = 'FullyQualifiedName~ModelAppropriatenessDoctrineAnchorTests'

# NO -v q on a TEST command (#179) - it suppresses the entire Error Message / Expected / Actual block, and
# on this guardrail that block IS the feedback: it is where the lost clause is quoted.
$out = dotnet test tests/Guardrails.Core.Tests --nologo --filter $filter 2>&1
$testExit = $LASTEXITCODE                                  # capture BEFORE any other statement
$out | ForEach-Object { Write-Output $_ }

if ($testExit -ne 0) {
    $detail = $out |
        Select-String -Pattern '\[FAIL\]|Error Message:|Assert\.|Exception|Stack Trace:|Expected:|Actual:|DOCTRINE|clause' |
        ForEach-Object { $_.Line } |
        Select-Object -First 60
    Write-Output ""
    Write-Output "=== ModelAppropriatenessDoctrineAnchorTests failure details (re-emitted so they land in the harness feedback tail) ==="
    if ($detail) { $detail | ForEach-Object { Write-Output $_ } }
    else { Write-Output "(no assertion/exception lines matched - inspect the full log above)" }
    Write-Output ""
    Write-Output "The skill does not carry the doctrine as pinned. Each failure names the clause verbatim - copy it character-for-character rather than rewording it; the anchor matches after whitespace normalization and nothing else. If TheThreeInsertionsLandInTheirOwnSections is in the list, the text landed but in the wrong section. The anchor test file is outside this task's writeScope: do not edit it."
    exit 1
}

# ZERO-MATCH GUARD (#455): a --filter that matches nothing exits 0 and certifies nothing. Keyed on the
# EXECUTED count (Passed + Failed), never Total, which counts [Skip]ped tests - so a fully-skipped class
# cannot pass this. Never on the "no tests matched" STRING, which is verbosity-dependent (#248).
$ran = ([regex]::Matches(($out | Out-String), '(?:Passed|Failed):\s*(\d+)') |
        ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum
if ($ran -lt 1) {
    Write-Output ""
    Write-Output "the filter $filter executed ZERO tests - ModelAppropriatenessDoctrineAnchorTests is missing, empty, or named differently, so this guardrail certified nothing about the prose that was just written. The class is authored by 03-author-tests-review-net-doctrine, this task's only ancestor; if it is absent from your tree that is a delivery problem, not a filter to widen."
    exit 1
}
exit 0
