## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g. `08-implement-deliver-at-wave-barrier`), NOT the
  stableId. The harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "08-implement-deliver-at-wave-barrier": { "someKey": "someValue" } }`.
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

**`DeliverAndCleanup` DOES NOT EXIST — corrected at review, 2026-09-11.** `grep -rn
DeliverAndCleanup src/ tests/` exits 1; the name entered at charter review and propagated into
the design and into this prompt. The real chain is **`Scheduler.Finalize` →
`DeliverToUserBranch` → `IWorktreeProvider.MergePlanBranchIntoUserBranch`**. Grep `Scheduler.cs`
for `DeliverToUserBranch` rather than trusting any name written here.

Make `WaveBarrierDeliveryTests` pass. Design 39 §1/§3: `DeliverAndCleanup` becomes callable at a wave
barrier, gated on that wave's `Exit` being green — **the run-end call stays** for the last wave and for
every flat plan.

**Find the seam yourself.** Grep `Scheduler.cs` for `RunWavedAsync` and for the existing
`DeliverAndCleanup` / `Finalize` call rather than trusting a line number — this file has moved under
several plans and a cited line is stale on arrival.

TRIAL MERGE, then gate, then promote (DECIDED at review, 2026-09-11 — an earlier version of
this line said "merge first, then gate", which writes to the operator's branch before anything
authorises it). Merge onto `refs/guardrails/trial/<waveDir>`, gate against that tree, consult
the interlock, and fast-forward the user's branch only on green; on red delete the ref. The
exit gate must assert over the tree the delivery produces, not the plan
branch alone.

Do NOT edit the authored tests; emit {"needsHuman": "<why>"} if one is genuinely wrong.

**Scope boundary (harness-enforced):** Write only to `src/Guardrails.Core/Execution/Scheduler.cs`. After this
task completes, the harness runs a `git diff` membership check and rejects any edit outside these paths. An
out-of-scope edit fails the task immediately and consumes a retry. If you hit a compile error caused by a
missing symbol in another file, do NOT edit that file — write `{"needsHuman": "<what is missing>"}` to the
state-out path and stop.

**The harness runs this task's guardrails itself when you finish.** Do not try to run the guardrail scripts yourself: the shell they need is not granted to you, and a call refused on two attempts can halt the task even after the work is done. Tests authored by OTHER tasks may legitimately fail on your base until their own implementing task lands; only this task's tests are yours to turn green.

