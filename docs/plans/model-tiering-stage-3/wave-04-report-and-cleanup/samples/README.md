# Two-sided samples for this wave's multi-file gates

These are the committed `.valid` / `.invalid` proofs the catalogue requires for a source-shape check over
code (#468/#302) — made durable so the next edit to either script can re-run them, instead of trusting
that someone repeats the pass by hand.

## Why they are probe SCRIPTS and not a `.valid.cs` / `.invalid.cs` pair

The canonical shape is one fixture file per guardrail: `tasks/<id>/samples/NN-check.valid.<ext>` and
`.invalid.<ext>`. That shape assumes a guardrail scanning **one** artifact. Both checks here scan
**many** — the wave-entry preflight reads 12 files for 16 clauses plus one directory, and the exit gate
reads 6 files for 8 required clauses plus three filesystem clauses — so a single fixture file cannot
represent either input, and one fixture per clause would be dozens of files that drift from the scripts the
moment a clause is edited.

Each probe therefore **lifts the clause list out of the guardrail it tests** (regex over the script's own
`$anchors` / `$required` / `$doomed` literals) and synthesizes the tree per clause. It cannot go stale
against the script: add a clause and the probe tests it on the next run without being touched.

`<wave>/samples/` is a legal, loader-safe home — verified: `guardrails validate` stays clean with it
present, and nothing here is enumerated as a guardrail. That it is **undocumented** at wave level, while
the obvious plan-root equivalent is a hard **GR2033** error, is filed as **#509**; nothing verifies a
committed pair actually behaves as claimed, filed as **#510**.

## Running them

```powershell
# The ENTRY gate's anchors are wave 1-3 deliverables, so seed it from the plan branch, not a checkout:
cd <worktreeRoot>/<runId>/_integration
pwsh -NoProfile -File <plan>/wave-04-report-and-cleanup/samples/01-wave3-surfaces-materialized.probe.ps1

# The EXIT gate's input does not exist anywhere yet, so its valid tree is hand-synthesized in the probe
# and it runs from anywhere:
pwsh -NoProfile -File docs/plans/model-tiering-stage-3/wave-04-report-and-cleanup/samples/03-wave-deliverables-present.probe.ps1
```

Both are read-only against the repo (they build into `%TEMP%` and mutate the copy) and print a verdict
line. Non-zero exit means a clause is defective.

`guardrails/01-solution-builds.ps1` and `guardrails/02-suites-pass.ps1` have **no probe**: both are single
recognized tool invocations over the merged HEAD (`dotnet build Guardrails.sln`, `dotnet test <proj>`), so
there is no clause list to be dead and no synthesizable input short of the merged tree itself. They got the
`pwsh -NoProfile` parse pass only, and that deferral is stated in the breakdown report rather than left
silent.

## What they have already caught

**The entry-gate probe caught a real defect during authoring, 2026-08-23.** The SSOT clause was first
anchored on the bare phrase `Per-tier spend`. The invalid half proved it **DEAD**: the same file carries a
lowercase prose mention of "per-tier spend" a thousand lines away from the bullet, and the gate's
`-notmatch` is case-insensitive — so the clause was satisfied by that unrelated sentence and could never
fire however far the real bullet moved. It is now anchored on the bullet's own worked-example line
(`Per-tier spend: easy:`), which occurs exactly once. Note which half found it: **the invalid one. The
valid half passed either way**, because under an all-present tree a dead clause and a live one are
indistinguishable.

**The exit-gate probe carries wave 2's lesson forward as a standing mutation family.** Wave 2's gate probe
found live that `03-wave-deliverables-present` did not strip comments before its *required* scans, so 7 of
its 12 required clauses were satisfied by a comment alone. This wave's probe therefore runs a dedicated
**comment-only** family: every `.cs` clause is re-tested against a file whose entire content is commented
out, and must still fire. All 5 do.
