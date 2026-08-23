# Two-sided samples for this wave's multi-file gates

These are the committed `.valid` / `.invalid` proofs the catalogue requires for a source-shape check over
code (#468/#302) — made durable so the next edit to either script can re-run them, instead of trusting
that someone repeats the pass by hand.

## Why they are probe SCRIPTS and not a `.valid.cs` / `.invalid.cs` pair

The canonical shape is one fixture file per guardrail: `tasks/<id>/samples/NN-check.valid.<ext>` and
`.invalid.<ext>`. That shape assumes a guardrail scanning **one** artifact. Both checks here scan
**many** — the wave-entry preflight reads 8 files for 13 clauses, and the exit gate reads 10 files for 12
required plus 3 forbidden clauses — so a single fixture file cannot represent either input, and one
fixture per clause would be 25 files that drift from the scripts the moment a clause is edited.

Each probe therefore **lifts the clause list out of the guardrail it tests** (regex over the script's own
`$anchors` / `$required` literal) and synthesizes the tree per clause. It cannot go stale against the
script: add a clause and the probe tests it on the next run without being touched.

`<wave>/samples/` is a legal, loader-safe home — verified: `guardrails validate` stays clean with it
present, and nothing here is enumerated as a guardrail. That it is **undocumented** at wave level, while
the obvious plan-root equivalent is a hard **GR2033** error, is filed as **#509**; nothing verifies a
committed pair actually behaves as claimed, filed as **#510**.

## Running them

```powershell
pwsh -NoProfile -File docs/plans/model-tiering-stage-3/wave-02-capture-and-persist/samples/01-stage2-anchors-materialized.probe.ps1
pwsh -NoProfile -File docs/plans/model-tiering-stage-3/wave-02-capture-and-persist/samples/03-wave-deliverables-present.probe.ps1
```

Both are read-only against the repo (they copy into `%TEMP%` and mutate the copy) and print a verdict
line. Non-zero exit means a clause is defective.

## What they have already caught

**The preflight probe caught a real defect during authoring** — one this review then re-confirmed. The
first draft keyed the clauses in a path-indexed hashtable of clause *lists*, and PowerShell **unwraps a
single-element array literal**: for every file carrying exactly one clause the loop variable became a
`string`, `$clause[0]` became its first *character*, and **four clauses could never fail** — including the
`Scheduler.RecordSucceededSettle` anchor the script's own header calls load-bearing. The flat-triple form
in the script today is the fix. Note which half found it: **the invalid sample. The valid half passed
either way**, because under an all-present tree a dead clause and a live one are indistinguishable.

**The gate probe caught a live one at review, 2026-08-23**: `03-wave-deliverables-present` did not strip
comments before its *required* scans (it always did before its *forbidden* ones), so **7 of its 12
required clauses were satisfied by a comment alone** — `// TODO: ObservedModel = result.ObservedModel`
matches even the dotted-assignment anchor. That is fatal to this gate in particular, because its stated
job is catching a hunk that vanished in the wave merge and every task prompt *mandates an XML doc comment*
on the member it checks. Fixed by the strip plus three declaration/assignment anchors; the probe now
reports 0 of 12 clearable by comment.
