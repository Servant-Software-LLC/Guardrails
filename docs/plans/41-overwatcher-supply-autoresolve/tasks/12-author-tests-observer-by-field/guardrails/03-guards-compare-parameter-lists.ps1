# catches: a forwarding guard that still matches by member NAME alone. This is the exact defect design
#          41 §6 is written against: "a default interface member hides a missed implementer" — once the
#          three-argument member exists, a decorator that kept the two-argument one satisfies a
#          name-only census, so the Scheduler's three-argument call lands on the empty default body and
#          the event silently disappears. The guard reads as if it is checking the thing; it is not.
#          Sibling of the red census, not a duplicate of it: the census sees a name-only guard only
#          because such a guard happens to be GREEN on arrival, and only for the ONE member this plan
#          adds. This clause states the property directly, for all three files, for every member.
#
# Source-shape check over TEST files, and NOT demotable to a test (#468): no test can assert how another
# test's own reflection helper is written. LOWER BOUND (#99): the token could sit in a comment, so it
# proves the file REACHES for parameter types, not that every clause uses them. The census is the gate.
#
# Author-time smoke test (#302), re-runnable (#468):
#   $env:GR_SUBJECT='docs/plans/41-overwatcher-supply-autoresolve/tasks/12-author-tests-observer-by-field/samples/03-guards-compare-parameter-lists.valid.cs';   ./03-guards-compare-parameter-lists.ps1  # expect 0
#   $env:GR_SUBJECT='docs/plans/41-overwatcher-supply-autoresolve/tasks/12-author-tests-observer-by-field/samples/03-guards-compare-parameter-lists.invalid.cs'; ./03-guards-compare-parameter-lists.ps1  # expect 1
# GR_SUBJECT narrows the subject list to ONE file so the pair is runnable; the default is all three.
#
# baseline counts on the untouched tree — MEASURED with Select-String -CaseSensitive (same case
# sensitivity as the -cnotmatch operator below), per subject:
#   GetParameters   0  in tests/Guardrails.Core.Tests/Supply/SuppliedObserverEventTests.cs
#   GetParameters   0  in tests/Guardrails.Integration.Tests/Supply/SuppliedObserverCliForwardingTests.cs
#   GetParameters   0  in tests/Guardrails.Integration.Tests/RunEvents/ObserverForwardingSweepTests.cs
#   OverwatchSupply 0  in all three (two carry [Trait("Category", "Supply")]; the third traits per method)
#   Only task 12 writes these three subjects (measured across all 20 task.json writeScopes), and it has
#   no ancestors (dependsOn: []), so nothing can pre-satisfy either clause.
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

$subjects = if ($env:GR_SUBJECT) {
    @($env:GR_SUBJECT)
} else {
    @(
        'tests/Guardrails.Core.Tests/Supply/SuppliedObserverEventTests.cs',
        'tests/Guardrails.Integration.Tests/Supply/SuppliedObserverCliForwardingTests.cs',
        'tests/Guardrails.Integration.Tests/RunEvents/ObserverForwardingSweepTests.cs'
    )
}

# ACCUMULATE, never exit 1 per clause (#478): one attempt reports every gap.
$failures = @()

foreach ($f in $subjects) {
    # PRECONDITION — the only early exit: the clauses below would crash on a missing subject.
    if (-not (Test-Path $f)) {
        Write-Output "$f does not exist — this task rewrites all three forwarding guards"
        exit 1
    }

    # The RAW file is read deliberately. Both tokens are C# identifiers that a correct file carries in
    # CODE; stripping comments would change neither verdict, and the measured baselines above were taken
    # raw, so raw is what the numbers describe.
    $raw = Get-Content $f -Raw

    # -cnotmatch, always, for a C# identifier: a case-insensitive required-present clause false-GREENS
    # on 'getparameters' text C# would never compile (taxonomy 3).
    if ($raw -cnotmatch 'GetParameters') {
        $failures += "$f never calls GetParameters — its forwarding guard still matches by member NAME alone. With the three-argument member added, a decorator that kept the two-argument one passes that guard while silently swallowing the event (design 41 §6). Compare the parameter TYPES."
    }

    if ($raw -cnotmatch 'OverwatchSupply') {
        $failures += "$f carries no [Trait(`"Category`", `"OverwatchSupply`")] — without the plan trait these tests fall outside the filters both halves of this pair run, so every census would certify an empty set."
    }
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Output $_ }
    exit 1
}
exit 0
