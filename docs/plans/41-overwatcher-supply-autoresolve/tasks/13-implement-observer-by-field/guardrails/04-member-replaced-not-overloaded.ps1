# catches: the cheap route through this task — ADDING the three-argument member beside the
#          two-argument one and updating only the implementers that happen to be convenient. While both
#          members exist, a decorator that kept the old one still compiles, the three-argument call
#          lands on the interface's empty default body, and the event silently disappears in every mode
#          (design 41 §6: "The member is replaced, never overloaded"). It also catches the two stale
#          claims in this member's own doc comment, which no test can see: the Supplied-By-Operator
#          constant (no drain writes it) and "raised at the resume-path boundary" (the one place it is
#          NOT raised — design 41 §8, fact 7).
#
# Two of the four clauses read the RAW file and two read the comment-stripped code, deliberately — and
# that split is the whole reason this file is not one loop. The doc-comment clauses would be UNFIRABLE
# against stripped code (the tokens live only in /// comments, the mirror of the §11a dead-end), and the
# member-shape clauses would false-fire against raw text (a doc comment legitimately spells the old
# signature out when explaining the change).
#
# Author-time smoke test (#302), re-runnable (#468):
#   $env:GR_SUBJECT='docs/plans/41-overwatcher-supply-autoresolve/tasks/13-implement-observer-by-field/samples/04-member-replaced-not-overloaded.valid.cs';   ./04-member-replaced-not-overloaded.ps1  # expect 0
#   $env:GR_SUBJECT='docs/plans/41-overwatcher-supply-autoresolve/tasks/13-implement-observer-by-field/samples/04-member-replaced-not-overloaded.invalid.cs'; ./04-member-replaced-not-overloaded.ps1  # expect 1
#
# baseline counts on src/Guardrails.Core/Execution/IRunObserver.cs — MEASURED with
# Select-String -CaseSensitive (matching the -cnotmatch/-cmatch operators), against the UNTOUCHED tree,
# each hit then re-read to confirm whether it lives in code or in a comment:
#   SuppliedResourcesCommitted(IReadOnlyList<string> _, string _, string _)   0  (required-present)
#   paramref name="by"                                                       0  (required-present, RAW)
#   design 41                                                                0  (required-present, RAW)
#   SuppliedResourcesCommitted(IReadOnlyList<string> _, string _)            1  — the member this task
#     DELETES. A forbidden-present clause red on arrival is a removal deliverable, the one nonzero §478
#     permits with a named reason; it is not censused as a floor.
#   Supplied-By-Operator     1  (RAW, in the doc comment) — same, a removal deliverable.
#   resume-path boundary     1  (RAW, in the doc comment) — same.
#   Task 12 writes this subject before this task and ADDS the three-argument member, so the first clause
#   is pre-satisfied by design; it is kept because this task could otherwise delete the wrong member.
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

$f = if ($env:GR_SUBJECT) { $env:GR_SUBJECT } else { "src/Guardrails.Core/Execution/IRunObserver.cs" }

# PRECONDITION — the only early exit: every clause below would crash on a missing subject.
if (-not (Test-Path $f)) {
    Write-Output "$f does not exist"
    exit 1
}

$raw  = Get-Content $f -Raw                                  # doc-comment clauses read THIS
# #561 / GR2037 — neutralize string literals BEFORE stripping comments. Deriving $code by stripping
# comments straight from $raw lets a literal that spells '/*' forge a delimiter and blank real code before
# any literal pass runs. Only / and * INSIDE literals are replaced, so a literal's CONTENT still satisfies
# a required-present clause. ($raw stays untouched above: the doc-comment clauses deliberately read it.)
$safe = [regex]::Replace($raw,  '"""[\s\S]*?"""', { $args[0].Value -replace '[/*]', '#' })   # raw strings
$safe = [regex]::Replace($safe, '@"(?:[^"]|"")*"', { $args[0].Value -replace '[/*]', '#' })  # verbatim
$safe = [regex]::Replace($safe, '"(\\.|[^"\\])*"', { $args[0].Value -replace '[/*]', '#' })  # ordinary
$code = [regex]::Replace($safe, '/\*[\s\S]*?\*/', '')        # /* */ block comments
$code = [regex]::Replace($code, '(?m)//.*$', '')             # // and /// line comments

# ACCUMULATE, never exit 1 per clause (#478).
$failures = @()

# --- member shape, over CODE ---------------------------------------------------------------------
if ($code -cnotmatch 'SuppliedResourcesCommitted\(\s*IReadOnlyList<string>\s+\w+\s*,\s*string\s+\w+\s*,\s*string\s+\w+\s*\)') {
    $failures += "$f does not declare SuppliedResourcesCommitted(IReadOnlyList<string>, string, string) — the three-argument member is the whole deliverable (design 41 §6)."
}

if ($code -cmatch 'SuppliedResourcesCommitted\(\s*IReadOnlyList<string>\s+\w+\s*,\s*string\s+\w+\s*\)') {
    $failures += "$f STILL declares the two-argument SuppliedResourcesCommitted(IReadOnlyList<string>, string) alongside the new one. Design 41 §6: the member is replaced, never overloaded — while both exist, a decorator that kept the old one compiles, the three-argument call lands on the empty default body, and the event silently disappears in every mode. Delete the old member; the compiler will then name every implementer that has not moved."
}

# --- doc comment currency, over RAW (the tokens live ONLY in /// comments) -------------------------
if ($raw -cnotmatch 'paramref name="by"') {
    $failures += "$f never documents the `by` parameter — design 41 §8 replaces this member's first paragraph, and <paramref name=`"by`"/> is where a reader learns the value is one of operator / overwatcher / task:<folder>."
}

if ($raw -cnotmatch 'design 41') {
    $failures += "$f does not cite design 41 — §8's replacement paragraph names it as the design of record for the supplier argument and for the auto-resolve that raises this event."
}

if ($raw -cmatch 'Supplied-By-Operator') {
    $failures += "$f still claims the commit carries the `Supplied-By-Operator` trailer. No drain writes that constant — the trailer is `Supplied-By: <by>` (design 40 §4), and naming a fixed operator is exactly the false provenance design 41 exists to close. Remove the stale claim."
}

if ($raw -cmatch 'resume-path boundary') {
    $failures += "$f still claims the event is raised `at the resume-path boundary, BEFORE the first task is scheduled`. That is the one place it is NOT raised (design 41 fact 7: the run-start drain in RunCommand raises nothing); it fires at the task boundary and, after the wiring task, from the auto-resolve. Replace the paragraph with §8's text."
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Output $_ }
    exit 1
}
exit 0
