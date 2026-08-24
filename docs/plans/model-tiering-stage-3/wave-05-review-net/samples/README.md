# Two-sided samples for this wave's multi-file gates

These are the committed `.valid` / `.invalid` proofs the catalogue requires for a source-shape check over
code (#468/#302) — made durable so the next edit to either script can re-run them, instead of trusting
that someone repeats the pass by hand.

## Why they are probe SCRIPTS and not a `.valid.cs` / `.invalid.cs` pair

The canonical shape is one fixture file per guardrail: `tasks/<id>/samples/NN-check.valid.<ext>` and
`.invalid.<ext>`. That shape assumes a guardrail scanning **one** artifact. Both checks here scan
**many** — the wave-entry preflight reads 11 files for 25 clauses plus two directories, and the exit gate
reads 4 files for 7 required clauses plus two fixture directories and a `src/` sweep — so a single fixture
file cannot represent either input, and one fixture per clause would be dozens of files that drift from
the scripts the moment a clause is edited.

Each probe therefore **lifts the clause list out of the guardrail it tests** (regex over the script's own
`$anchors` / `$required` literals) and synthesizes the tree per clause. It cannot go stale against the
script: add a clause and the probe tests it on the next run without being touched. The exit-gate probe
additionally **self-checks** its hand-written valid tree against the lifted clauses, so a clause this
probe's fixture cannot satisfy is reported as a gap in the probe rather than as a dead clause in the
guardrail.

The single-artifact canonical shape IS used where it fits:
`tasks/02-implement-tier-classification-audit/samples/` carries a real `.valid.cs` / `.invalid.cs` pair
plus a probe that feeds them to the guardrail, and
`tasks/04-add-model-appropriateness-probe/samples/` carries a probe whose valid sample is the real 200 KB
skill file (a hand-written duplicate of a documentation deliverable would be stale on its first edit).

`<wave>/samples/` is a legal, loader-safe home — `guardrails validate` stays clean with it present and
nothing here is enumerated as a guardrail. That it is **undocumented** at wave level, while the obvious
plan-root equivalent is a hard **GR2033** error, is filed as **#509**; nothing verifies a committed pair
actually behaves as claimed, filed as **#510**.

## Running them

```powershell
# The ENTRY gate's anchors are wave 1-4 deliverables, so seed it from the plan branch, not a checkout:
pwsh -NoProfile -File <plan>/wave-05-review-net/samples/01-tier-vocabulary-materialized.probe.ps1 -Repo <worktreeRoot>/<runId>/_integration

# The EXIT gate's input does not exist anywhere yet, so its valid tree is hand-synthesized in the probe
# and it runs from anywhere:
pwsh -NoProfile -File docs/plans/model-tiering-stage-3/wave-05-review-net/samples/03-wave-deliverables-present.probe.ps1

# The two task-level probes:
pwsh -NoProfile -File docs/plans/model-tiering-stage-3/wave-05-review-net/tasks/02-implement-tier-classification-audit/samples/01-no-diagnostic-code-no-validator.probe.ps1
pwsh -NoProfile -File docs/plans/model-tiering-stage-3/wave-05-review-net/tasks/04-add-model-appropriateness-probe/samples/01-review-skill-intact.probe.ps1 -Repo <worktreeRoot>/<runId>/_integration
```

All four are read-only against the repo (they build into `%TEMP%` and mutate the copy) and print a verdict
line. Non-zero exit means a clause is defective.

`guardrails/01-solution-builds.ps1` and `guardrails/02-suites-pass.ps1` have **no probe**: both are single
recognized tool invocations over the merged HEAD (`dotnet build Guardrails.sln`, `dotnet test <proj>`), so
there is no clause list to be dead and no synthesizable input short of the merged tree itself. They got the
`pwsh -NoProfile` parse pass only, and that deferral is stated in the breakdown report rather than left
silent. The four test-invoking task guardrails were instead BASELINE-run against the wave's entry tree
(#479) — each is correctly RED there, and each RED is the zero-match guard firing rather than an incidental
failure.

## What they have already caught

Three real defects during authoring, 2026-08-24. All three were invisible to reading, and two were
invisible to the exit code.

**1. The exit gate's `NOTHING AT ALL` clause was DEAD.** PowerShell's `-match` family is
case-**IN**sensitive, and `.claude/skills/guardrails-review/SKILL.md` already carries the ordinary sentence
*"reports nothing at all"* a thousand lines from anywhere relevant — so the clause guarding this wave's
single most important requirement, the graceful skip, could never have fired however far that sentence
drifted. It was one clause of eight and it hid behind its seven failing siblings: **one exit code, many
clauses**. Every clause in both wave-root gates is now case-sensitive (`-cnotmatch`). Note which half found
it: the **baseline run against the real tree**, not the invalid half — under a tree where everything is
missing, a dead clause and a live one are indistinguishable.

**2. Both per-test censuses had a zero-match guard that could never fire.** `@($doc.TestRun.Results.UnitTestResult)`
is `@($null)` when a TRX carries no results, and `@($null).Count` is **1** in PowerShell — so a run that
selected zero tests presented as one result, sailed past the guard, and was reported as nine missing test
names instead of as a filter that matched nothing. Caught by running the census against the entry tree,
where the classes genuinely do not exist yet. Both now filter nulls before counting. *(The same line shape
appears in wave 4's shipped census; it is harmless there only because its failure mode is a worse message,
not a wrong verdict.)*

**3. A heading clause anchored `^### 6\. Report$` matched ZERO times.** `.claude/skills/**` is not
`eol=lf`-pinned, so the file is checked out CRLF and `$` sits after the `\r`. Every one of the nine heading
clauses in `tasks/04-.../guardrails/01-review-skill-intact.ps1` would have been permanently dead. They now
end `\s*$`. Caught by measurement before the script ever shipped, which is the only reason it is a footnote
rather than a fourth entry in this list.
