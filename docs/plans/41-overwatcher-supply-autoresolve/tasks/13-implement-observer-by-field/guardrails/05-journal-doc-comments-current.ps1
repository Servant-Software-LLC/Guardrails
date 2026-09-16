# catches: a half-done doc-comment correction. Both JournalDocument.Supplied and JournalDocument.Refreshed
#          still describe themselves as "the STUB half" of a task that shipped long ago, and the second one is
#          NOT in issue #712's list — so the obvious reading of the issue fixes one lie and leaves its
#          twin sitting three lines below it (design 41 §8, fact 10). Nothing else can see this: a doc
#          comment compiles, runs and passes every test whatever it says, and a reader who catches the
#          file lying once stops trusting the rest of it.
#
# The clauses read the RAW file, DELIBERATELY and against the §11a default: every token here lives ONLY
# inside /// doc comments, so stripping comments first would leave the forbidden clause unfirable and the
# two required clauses unsatisfiable — the dead-guard failure §478 and §11a both warn about, wearing
# both polarities at once. There is no code in this subject for these clauses to false-fire on.
#
# GR2074 — DISPOSITIONED, not silenced. The lint flags that the two required clauses below are DOTTED and
# carry no `\(`, which is normally the mention-vs-use defect (#76): a dotted name without the trailing
# paren also matches `nameof(Type.Member)`, certifying vocabulary rather than any real use. That rule does
# not apply here, and the reason is the SUBJECT: this guardrail checks a DOC COMMENT, and what design 41
# §8 requires there is a `<see cref="RunJournal.RecordSupplied"/>` cross-reference. A MENTION is exactly
# and only what belongs in one — a C# doc comment can do nothing else — so demanding `RecordSupplied\(`
# would make both clauses unsatisfiable by ANY correct edit: the #479 shape (constraining the form of
# correct work) wearing a lint's clothing.
#
# HOW THE LINT DECIDES — read from src/Guardrails.Core/Loading/PlanValidator.cs:3025-3057, not guessed.
# ClaimsAnInvocation scans the LEADING `#` block (it stops at the first non-comment line, so it is not
# only the `# catches:` line) for a small word-anchored vocabulary, and stays silent unless one of those
# words appears. An earlier revision of this header tripped it twice: once in the `# catches:` line, which
# used one of those verbs in the sense of NAMING ("the two properties still ... themselves 'the STUB
# half'"), and once in THIS paragraph, where the argument for the disposition restated the very
# vocabulary it was arguing about. The explanation set off the rule it explained.
#
# Both are reworded. The CLAUSES are untouched, no `# guardrails: ignore` is used, and the wording now
# says what the sentences always meant. Re-measured outcome is recorded on the line below.
#
# RE-MEASURED: `validate` now reports GR2074 zero times for this file — the plan's warning census went
# GR2074 x2 -> x0 — while the two required clauses below are byte-for-byte what they were. The lint went
# quiet because the header stopped claiming something it never meant, not because a check got weaker.
#
# Author-time smoke test (#302), re-runnable (#468):
#   $env:GR_SUBJECT='docs/plans/41-overwatcher-supply-autoresolve/tasks/13-implement-observer-by-field/samples/05-journal-doc-comments-current.valid.cs';   ./05-journal-doc-comments-current.ps1  # expect 0
#   $env:GR_SUBJECT='docs/plans/41-overwatcher-supply-autoresolve/tasks/13-implement-observer-by-field/samples/05-journal-doc-comments-current.invalid.cs'; ./05-journal-doc-comments-current.ps1  # expect 1
#
# baseline counts on src/Guardrails.Core/Journal/JournalModel.cs — MEASURED with
# Select-String -CaseSensitive against the UNTOUCHED tree, each hit re-read to confirm where it lives:
#   RunJournal\.RecordSupplied    0  (required-present; §8's replacement paragraph names it)
#   RunJournal\.RecordRefreshed   0  (required-present; §8's replacement paragraph names it)
#   STUB half                     2  — one on Supplied, one on Refreshed: BOTH paragraphs this task
#     deletes. A forbidden-present clause red on arrival is a removal deliverable, the one nonzero §478
#     permits with a named reason; it is not censused as a floor. The count is 2 and not 1 precisely
#     because the second is the one #712 forgot.
#   Only task 13 writes this subject (measured across all 20 task.json writeScopes).
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

$f = if ($env:GR_SUBJECT) { $env:GR_SUBJECT } else { "src/Guardrails.Core/Journal/JournalModel.cs" }

# PRECONDITION — the only early exit: every clause below would crash on a missing subject.
if (-not (Test-Path $f)) {
    Write-Output "$f does not exist"
    exit 1
}

$raw = Get-Content $f -Raw

# ACCUMULATE, never exit 1 per clause (#478): the two paragraphs are one deliverable, and reporting them
# one attempt at a time is how the second one gets forgotten again.
$failures = @()

$stub = ([regex]::Matches($raw, 'STUB half')).Count
if ($stub -gt 0) {
    # NOTE: no backticks in this message. PowerShell's escape character is the BACKTICK, so the original
    # wording `the STUB half` rendered as a literal TAB followed by "he STUB half" — corrupting the one
    # line the harness feeds back as retry feedback (#179). Single quotes carry the emphasis safely.
    $failures += "$f still calls a shipped property 'the STUB half' of an authoring task, $stub time(s). Both JournalDocument.Supplied and JournalDocument.Refreshed shipped long ago; design 41 §8 replaces BOTH paragraphs. Fixing only the one issue #712 lists leaves its twin three lines below it."
}

if ($raw -cnotmatch 'RunJournal\.RecordSupplied') {
    $failures += "$f does not name RunJournal.RecordSupplied as the writer of supplied[] — design 41 §8's replacement paragraph says who writes the section and when (the run-start drain, the task-boundary drain, and the missing-resource auto-resolve with by: `overwatcher`). A reader needs the writer, not a stub notice."
}

if ($raw -cnotmatch 'RunJournal\.RecordRefreshed') {
    $failures += "$f does not name RunJournal.RecordRefreshed as the writer of refreshed[] — the same correction, on the sibling property design 41 §8 flags as the same class of staleness (it is NOT in issue #712's list, and it is fixed here anyway)."
}

# ADDED AT REVIEW — a FOURTH doc comment, and a §6 item rather than one of §8's three. Design line 563
# requires ForcedDeliveryRecord.Decision to stop naming only the two tokens. Design §11 row 7 assigns it
# to THIS task (it is why JournalModel.cs is in the writeScope), and no other task can reach it: task 16
# owns only RunCommand.cs. Measured across the plan, NO task pinned it — it would have shipped still
# asserting there are exactly two suppressing tokens, while task 07 lands a third.
#
# baselines on the UNTOUCHED tree, MEASURED with Select-String -CaseSensitive, raw AND whitespace-flattened
# (equal in both, so neither clause straddles a line break, #428):
#   two <c>decisions\[\]</c> tokens   1  — DECLARED NONZERO: a REMOVAL deliverable, red on arrival by
#                                         design. This is the strong clause: appending cannot satisfy it.
#   auto-supplied                     0  — required-present, honest.
# NOT PINNED, and the reason is the whole of #478: 'machine decision' already appears TWICE in this file
# (lines 242 and 265, both in UNRELATED ForcedPastDecision prose), so requiring the design's own wording
# would have been GREEN ON ARRIVAL and certified nothing. The two clauses below discriminate instead.
if ($raw -cmatch 'two <c>decisions\[\]</c> tokens') {
    $failures += "$f still says ForcedDeliveryRecord.Decision is one of the TWO decisions[] tokens that suppress delivery. Design section 6 adds a third, auto-supplied, so that sentence asserts a cardinality that is false the moment task 07 lands. Generalize it to a machine decision naming all three; do not append the new token and leave the word two standing."
}

if ($raw -cnotmatch 'auto-supplied') {
    $failures += "$f never names auto-supplied. ForcedDeliveryRecord.Decision records WHICH suppressing decision an operator override delivered past, and after design 41 that can be auto-supplied — a record type that cannot describe one of its own legal values is the same class of stale comment as the two STUB paragraphs above."
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Output $_ }
    exit 1
}
exit 0
