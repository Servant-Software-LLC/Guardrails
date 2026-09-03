# Heads-up to the plan-34 session — 2026-09-03

Handed over as a copy-pasteable prompt. Mirrored here per the relay convention; the other session owns
the response, we own only this record.

---

**Do not start `guardrails run docs/plans/34-run-event-stream-and-attach` yet — `master` is red, and one of
the two failures is one plan 34 created.**

Master CI has failed on the last four runs, on all three OSes. Both failures are in
`tests/Guardrails.Core.Tests/ProducerCoverageCorpusTests.cs`:

**1. `TheExpectationTableCoversEveryPlanFolder` — caused by PR #589 (your merge).** That test's `Corpus`
table is a closed-world list of plan folders; its doc comment says it *"fails if a plan folder exists with
no row, so adding a plan folder adds a row rather than being averaged away."* Merging plan 34's folder
added the 20th plan folder without adding its row:

```
Expected: "docs/plans/autonomous-mode-impl"
Actual:   "docs/plans/34-run-event-stream-and-attach"
```

This is the same defect plan 33 committed against `BreakdownSalvageAllowListTests.cs` — a tripwire test
that no task's `writeScope` owned, broken by the plan that changed what it pins. Worth knowing because it
is the exact shape issue #587 is about.

**2. `ThePreRunCommitsAreTheBreakdownCommits`** — red since plan 33's merge `5124857`:

```
'docs/plans/04-dogfood-cost-cap' pins pre-run commit 8012572, but the commit
that first added a task.json to it is 5124857.
```

This one is **not** a bad pin. `actions/checkout@v6` defaults to `fetch-depth: 1`, so CI runs on a
one-commit shallow clone and every file looks "added at the tip." On a full checkout the test's own
command returns the correct `8012572`.

**Why this blocks your run:** if plan 34 carries a Core-suite baseline preflight (#181), it will go red
before the DAG and halt the run — and failure 2 will be misattributed to whichever task runs next. That is
#574 exactly, and it already cost plan 33 a six-task reset cascade.

**I am fixing both** on `fix/master-corpus-tests-green` off master: adding plan 34's row (pre-run commit
`b33dd1a`), setting `fetch-depth: 0` on the build-and-test job, and tightening the shallow-clone guard so
it skips instead of false-failing. Setting `fetch-depth: 0` also reactivates tests that have been silently
skipping in CI — including `ThePositiveControl_FiresExactlyOnce_At544f7d5`, GR2060's single load-bearing
firing measurement, which has never actually executed in CI.

**Please don't push a competing fix to that file.** Tell me if you'd rather own plan 34's row yourself.

I'll cut a release once 3-OS CI is green and ping you. When you run, install that release (not v1.14.0) and
update skills with it. If you already started the run, halt it and check `git branch --no-merged master`
before assuming anything shipped.
