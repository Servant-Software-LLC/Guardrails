## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g.
  `23-implement-entry-baseline-kind`), NOT the stableId. The harness REJECTS a
  fragment keyed by anything else (every attempt), so:
  `{ "23-implement-entry-baseline-kind": { "someKey": "someValue" } }`.
- EXCEPTION — the CONTROL KEYS `needsHarnessWrite` and `needsHuman` are TOP-LEVEL
  SIBLINGS of your folder-name key, never nested inside it.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- Guardrails constrain the OUTCOME, never HOW you implement it. Never reshape working
  code — or reword a document away from its own conventions — to match a check's
  pattern.
- If you cannot proceed without a human decision, write
  {"needsHuman": {"question": "<question>", "kind": "blocked-work"}} to the
  state-out path and stop. If instead a guardrail reports something ABSENT that you
  can see is PRESENT, that guardrail is defective: use "kind": "defective-guardrail"
  and quote (a) the guardrail's exact claim and (b) the file:line that refutes it.
  If you cannot produce BOTH quotes it is not a defective guardrail — retry the work,
  or escalate as "blocked-work". Difficulty is never "defective-guardrail".

## Task

Make the wave entry gate re-evaluate a **positive** baseline after a delivery while a
**negative** one keeps skip-once, so `WaveEntryBaselineKindTests` passes.

**Find the seam yourself.** Grep `src/Guardrails.Core/Execution/Scheduler.cs` for
`RunWaveEntryGateAsync` rather than trusting a line number — tasks 06 and 08 have already
edited this file by the time you run, and a cited line is stale on arrival.

Today the method treats every entry marker the same: *"Skip-once: a passed entry marker for
this wave is not re-evaluated on resume."* That is right for a negative baseline and wrong for
a positive one. A positive baseline asserts something about the CURRENT tree; a delivery — and
especially the §1c refresh, which pulls the user's own commits in — changes that tree, so the
marker no longer describes what it claims to.

**Do NOT resolve this by dropping skip-once altogether.** A negative baseline
("this behaviour is not yet present") is monotone: it goes false the moment the wave that
implements the behaviour lands, so re-running it on resume would false-RED a correct run. Both
directions are pinned by name for that reason, and the cheapest wrong implementation here is
the one that fixes one direction by breaking the other.

How the gate learns which kind it is holding is yours to decide — a declared kind on the
preflight, or a property the check itself reports. Whatever you choose, the tests read the
DECISION the gate made, not the mechanism.

Do NOT edit the authored tests; emit `{"needsHuman": "<why>"}` if one is genuinely wrong.

**Scope boundary (harness-enforced):** Write only to the path(s) listed above. After this
task completes, the harness runs a `git diff` membership check and rejects any edit outside
them. An out-of-scope edit fails the task immediately and consumes a retry. If you hit a compile
error caused by a missing symbol in another file, do NOT edit that file — write
`{"needsHuman": "<what is missing>"}` to the state-out path and stop.
