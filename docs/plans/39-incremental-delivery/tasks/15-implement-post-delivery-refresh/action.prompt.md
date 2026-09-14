## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g. `15-implement-post-delivery-refresh`), NOT the
  stableId. The harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "15-implement-post-delivery-refresh": { "someKey": "someValue" } }`.
- EXCEPTION — the CONTROL KEYS `needsHarnessWrite` and `needsHuman` are TOP-LEVEL
  SIBLINGS of your folder-name key, never nested inside it.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- Guardrails constrain the OUTCOME, never HOW you implement it. Never reshape working
  code — or reword a document away from its own conventions — to match a check's pattern.
- If you cannot proceed without a human decision, write
  {"needsHuman": {"question": "<question>", "kind": "blocked-work"}} to the
  state-out path and stop. If instead a guardrail reports something ABSENT that you can
  see is PRESENT, that guardrail is defective: use "kind": "defective-guardrail" and
  quote (a) the guardrail's exact claim and (b) the file:line that refutes it. If you
  cannot produce BOTH quotes it is not a defective guardrail — retry the work, or
  escalate as "blocked-work". Difficulty is never "defective-guardrail".

## Task

Make `PostDeliveryRefreshTests` pass. Design 39 §1c, including its subsection **"How a refresh is
recorded (post-plan-40 refinement)"**, which is the contract below.

1. **Read the trigger from the trial result — never compute ancestry yourself (review round 4,
   `d39-refresh-record`).** Under §1's trial merge the promotion of the user's branch is ALWAYS a
   fast-forward to `refs/guardrails/trial/<waveDir>`, so `MergeOnSuccessResult.FastForwarded` is true on
   every delivery and a check on it alone would never refresh. The delivering wave's `TrialDelivery`
   (task 31, built by task 08's barrier flow) already knows: refresh if and only if
   `trial.UserTipWasAncestor` is false AND `PromoteTrialDelivery` returned `FastForwarded` — exactly when
   the trial merge had to create a merge commit and that commit landed.
2. **The refresh commit.** Have the Scheduler's C# perform a merge of `<upstream-sha>` inside the
   integration worktree with the `--no-ff` and `--no-verify` flags, through the same git invocation path its
   other integration-worktree commits already use. You write that call; you never run git yourself, and
   your granted tools do not include it. `<upstream-sha>` is the user's branch tip AFTER the promotion,
   which is `trial.Commit`. Merge the sha, never the branch name, so the record names exactly what was
   merged. The plan-branch tip from BEFORE the refresh must be
   the FIRST parent: never fast-forward the plan branch onto the delivered commit, which can push earlier
   waves' task commits off its `--first-parent` spine. The message is `Refreshed-From: <from>` /
   `Guardrails-Run: <runId>`, where `<from>` is the integration handle's `OriginalBranch` and `<runId>` is
   the journal's run id — never `Supplied-By:`, because nothing was supplied.
3. **The record, only after the commit exists.** Call `RunJournal.RecordRefreshed` with a
   `RefreshedRecord` — `At`, `Commit`, `From`, `Upstream`, `DeliveredWave` (the delivering wave's
   directory), and `Paths` (`git diff --name-only <commit>^1 <commit>`, forward-slash, ordinal-sorted) —
   reaching the journal through the same `_journal is Journal.RunJournal` cast the shipped supply drain
   uses. `RefreshedRecord`, `RecordRefreshed` and `UnauthoredContentNote` already exist (task 25): use
   them, never re-declare them.
4. **A refresh that fails is a fault, not a skip.** The upstream already contains everything the plan
   branch delivered, so the merge is conflict-free by construction. If git fails anyway, throw into the
   #150 fault path — an honest halt with NO record written — never continue on the stale base.
5. **The gate halt names it.** In `BuildGateHalt`, for BOTH `WaveHaltKind.EntryGateFailed` and
   `WaveHaltKind.ExitGateFailed`, append `UnauthoredContentNote.HeadlineSuffix(...)` to the headline AFTER
   the failing check names, and `UnauthoredContentNote.DetailLines(...)` to the detail. `BuildGateHalt` is
   `static` today — give it the journal state it needs rather than re-reading `run.json` from disk. When
   the run has neither `supplied[]` nor `refreshed[]`, the headline and detail must be byte-identical to
   today's. `RecordGateHalt` already copies the headline into `run.json`'s `halt.headline`, so do not touch
   the `RunHalt` schema or the CLI.

Do NOT edit the authored tests; emit {"needsHuman": "<why>"} if one is genuinely wrong.

**Scope boundary (harness-enforced):** Write only to `src/Guardrails.Core/Execution/Scheduler.cs`. After this
task completes, the harness runs a `git diff` membership check and rejects any edit outside these paths. An
out-of-scope edit fails the task immediately and consumes a retry. If you hit a compile error caused by a
missing symbol in another file, do NOT edit that file — write `{"needsHuman": "<what is missing>"}` to the
state-out path and stop.

**The harness runs this task's guardrails itself when you finish.** Do not try to run the guardrail scripts yourself: the shell they need is not granted to you, and a call refused on two attempts can halt the task even after the work is done. Tests authored by OTHER tasks may legitimately fail on your base until their own implementing task lands; only this task's tests are yours to turn green.

