## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key - the name of the directory this task.json lives in, NOT the stableId. The
  harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "08-implement-the-plan-edit-watch": { "someKey": "someValue" } }`.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- Guardrails constrain the OUTCOME, never HOW you implement it. Never reshape working
  code - or reword a document away from its own conventions - to match a check's
  pattern.
- If you cannot proceed without a human decision, write
  {"needsHuman": {"question": "<question>", "kind": "blocked-work"}} to the
  state-out path and stop. If instead a guardrail reports something ABSENT that you
  can see is PRESENT, that guardrail is defective: use "kind": "defective-guardrail"
  and quote (a) the guardrail's exact claim and (b) the file:line that refutes it.
  If you cannot produce BOTH quotes it is not a defective guardrail - retry the work,
  or escalate as "blocked-work". Difficulty is never "defective-guardrail".

## Plan of record

This task implements the FIRST HALF of stage 8 of `docs/plans/31-unattended-run-hardening.md` - the
watch itself. The WIRING is task 09's. Read **sections 5.1 and 5.2 in full**; section 5.3 is 09's and
you do not need it. Where this prompt and the plan disagree, the plan is authoritative and you should
say so in your summary.

Read: **sections 5.1 and 5.2**.

## Your scope is ONE file, and that is the whole point

Plan section 13 originally handed stage 8 five files - the watch AND its wiring - as one row. That task
carried the structural over-scope fingerprint (`GR2042`): a fan-in sink whose every guardrail miss
re-runs the entire five-file change. It was split by collaborator. You own the watch; task 09 owns
every seam that consumes it. Your verdict comes from the Core unit suite, which drives the watch
directly and needs no run at all - so your retry is cheap and your failures are local.

## What stage 6 left you

`src/Guardrails.Core/Execution/LivePlanEditWatch.cs` declares the section 5.2 surface with `Poll()` and
`Rebaseline()` throwing `NotImplementedException`, and a constructor that does NOT throw. **This
describes the state at plan-authoring time, before stage 6 had actually run - verify it before assuming
this shape.** Find the type by symbol; do not rely on any line number.

## Task

Fill real logic over those stubs so `tests/Guardrails.Core.Tests/Execution/LivePlanEditWatchTests.cs`
passes - all eight behaviours U1-U8, without editing the test file.

### The baseline

Per **task**, the per-**file** hashes of `TaskDefinitionFiles.Enumerate(task)` (`task.json`, the
resolved action file, `guardrails/**`, `preflights/**`), computed with the **same `HashText` primitive
`TaskDefinitionHash` uses**, so the two cannot disagree about what defines a task.

`TaskDefinitionFiles` is **`internal`**, in namespace **`Guardrails.Core.Journal`** (not `Loading`) -
same assembly, so a `using` is all it needs.

`logs/` and `state/` are not in that enumeration, which is why the harness's own constant writes into
the plan folder cannot trigger the watch (U8).

### The ignore list - applied HERE, and NOT in `HashText`

`HashText.EnumerateFolderFiles` enumerates `"*"` with `SearchOption.AllDirectories` and filters
**nothing**, so a stray `.DS_Store`, `Thumbs.db`, `*.swp`, `*.orig` or `*.rej` in a `guardrails/` folder
is part of a task's definition today. Drop those patterns **in the watch, before comparing** (U7).

**Do NOT "fix" it centrally.** `HashText` feeds `TaskDefinitionHash` and `PlanDefinitionHash`, so
changing its file set silently changes **every recorded definition hash** - and a changed definition
hash is a **definition-drift halt on the next resume of every affected plan**, plus a re-staled
`state/guardrails-review.json` for every plan keyed on `PlanDefinitionHash`. `HashText.cs` is outside
your `writeScope` for exactly this reason. Applying the list only here makes the watch strictly
**quieter** than the hash and never noisier; anything the hash sees and the watch ignores is a
pre-existing drift condition the resume-time check already owns. (Whether `HashText` should carry the
list is plan section 14's one genuinely open question, with a migration attached - it is NOT yours.)

### The two methods

- **`Poll()`** recomputes the surface, returns what changed since the last call, **and re-baselines**
  (U1, U2, U3). It **never throws**: an unreadable file is skipped (U6). Tasks absent from the baseline
  - a JIT wave's new tasks - are added **silently**. Report a `PlanEdit` per changed task, carrying the
  old and new hashes and a `PlanEditedFile` per changed file with the right `PlanEditKind`.
- **`Rebaseline(params string[] taskIds)`** silently re-baselines those tasks; **no ids re-baselines the
  whole plan** (U4), and an unknown id is a **no-op** (U5). Both matter to task 09: it calls the
  plan-wide form after each of five harness writers.

### What you must NOT do

- Do NOT touch `Scheduler.cs`, `DecisionEntry.cs`, `RunReport.cs` or `RunCommand.cs` - they are task
  09's, and an edit there fails this task immediately.
- Do NOT touch `HashText.cs` or `TaskDefinitionFiles.cs`.
- Do NOT edit the authored tests.
- Do NOT add a `FileSystemWatcher`, a thread, a lock or a daemon. The watch is a passive object that
  recomputes when asked; task 09 decides when to ask.

**Scope boundary (harness-enforced):** Write only to
`src/Guardrails.Core/Execution/LivePlanEditWatch.cs`. After this task completes, the harness runs a
`git diff` check and rejects any edit outside that path. An out-of-scope edit fails the task
immediately and consumes a retry. Do NOT edit the authored tests: make them pass by fixing the
implementation, and if a test is genuinely wrong or incompatible, write `{"needsHuman": "<why>"}` to
the state-out path and stop.
