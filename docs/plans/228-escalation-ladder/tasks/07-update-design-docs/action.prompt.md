## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g.
  `07-update-design-docs`), NOT the stableId. The harness REJECTS a fragment keyed by
  anything else (every attempt), so:
  `{ "07-update-design-docs": { "someKey": "someValue" } }`.
- EXCEPTION — the CONTROL KEYS `needsHarnessWrite` and `needsHuman` are TOP-LEVEL
  SIBLINGS of your folder-name key, never nested inside it. They are instructions to
  the harness, not state, so the rule above does not cover them:
  `{ "07-update-design-docs": { "someKey": "someValue" },
  "needsHarnessWrite": { "path": "…", "edits": [ … ] } }`. Nest one inside your
  folder-name key and the harness REJECTS the attempt — nothing is written.
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

The escalation ladder has shipped. Move the CONTRACT in the same change-set as the code: record it in
the SSOT, and reconcile the model-tiering design of record, which currently describes this section as
deferred **and describes a different design from the one that shipped**.

**Scope boundary (harness-enforced):** Write only to `docs/plans/02-schemas-and-contracts.md` and
`docs/plans/17-model-tiering.md`. After this task completes, the harness runs a `git diff` check and
rejects any edit outside these paths. An out-of-scope edit fails the task immediately and consumes a
retry.

Both files are large. Navigate them by **grepping for the durable markers below**, not by line number —
earlier tasks in this plan did not touch these files, but they move constantly for other reasons.

### 1. `docs/plans/02-schemas-and-contracts.md` — the SSOT

Grep for `Per-attempt tier provenance` to find the §7 section that owns this contract. It carries a
four-row table of `tierSource` values (`"task"`, `"plan-default"`, `"override"`, *(absent)*), and above
it a `provenance` wire example whose `"tierSource"` line spells the alternatives in a trailing comment.
Bring both up to date, matching the surrounding style exactly (the table's tokens are written
backticked-and-quoted — `` `"task"` `` — so the new one is written `` `"escalated"` ``):

- a **fifth row** for `` `"escalated"` ``: produced by the escalation ladder (#228) when a PREVIOUS
  attempt of this task failed its guardrails, with `provenance.tier` being the rung actually served
  after the climb.
- the new key **`escalatedFrom`**: the rung the FIRST (un-escalated) resolution served. Present only on
  an attempt the ladder moved; **absent — never null** — on every other attempt, which is what makes
  its presence the escalation signal without a second flag beside it. It goes in the wire example
  beside `tier`/`tierSource` and in the prose, the same way `requestedModel` is documented.
- prose naming the mechanism as the **escalation ladder** and stating the three things a reader cannot
  infer from the table:
  - the trigger is **`guardrail-failed` only** — never a timeout, a max-turns stop, a transient pause
    or a permission wall, each of which has its own counter and its own remedy;
  - an escalated attempt draws from the **same retry pool** — one rung per guardrail failure, total
    attempts unchanged, no budget reset and no new cumulative cap;
  - `escalated` is **not** `Climbed`. A capability climb (`Candidates(RequestedTier)` was empty, so the
    resolver walked up inside ONE attempt) and an escalation (a previous attempt failed its guardrails)
    are different facts that can produce the identical `(requestedTier, tier)` pair, and the journal
    must let a reader tell them apart. That is the whole reason this value exists.

Also grep for `action.TierOrigin` in §3: the origin the loader records has three states and none of
them is `escalated`, because escalation is a RUNTIME fact no declaration site supplies. Say so in one
sentence there, so a reader of §3 does not go looking for a fourth `TierOrigin`.

### 2. `docs/plans/17-model-tiering.md` — the DoR, and the part that is now WRONG

Grep for `## 7. The escalation ladder` . That section is marked `[v2 — deferred]` and its
blockquote says v1 has no ladder. That is now false. Worse, **two of its stated decisions were
overruled** by the reviewed charter for this work, `docs/plans/228-escalation-ladder.charter.md`, and
leaving them standing would make the design of record contradict the shipped harness:

- **D15a** ("never before that rung has had one same-tier retry") is exactly the charter's **option B**,
  and the maintainer chose **option A**: *each guardrail failure climbs one rung, total attempts
  unchanged, no budget reset.* The charter's own rationale is that the usual objection to A — that it
  discards the same-tier retry the feedback loop exists for — does not hold, because `feedbackPath`
  reaches the next attempt regardless of which model runs it, so an escalated attempt is both stronger
  AND better informed.
- **The trigger set** is narrowed to **`guardrail-failed` only**. §7's own "v2 open items" list already
  proposed this as **DA F5**; the charter adopted it. `action-failed`, `invalid-fragment`, `timeout`,
  `max-turns` and `output-cap` do **not** escalate.

Rewrite §7 so it describes what shipped, and do it the way this document already handles a decision
that moved: it writes verdicts in caps beside the decision they overturn (grep for `RESOLVED` — 6 hits
at authoring time — and read one for the house form). Requirements:

- §7's heading must no longer be marked `[v2 — deferred]`, and the v1/v2 capability table near the top
  of the document (grep for `Escalation ladder + `) must no longer mark this row `**v2 (#228)**`.
- §7 must **cite `docs/plans/228-escalation-ladder.charter.md`** as the plan of record that settled the
  budget question and narrowed the trigger set, so the next reader can find the review round rather
  than re-litigating it.
- D15a and the OD-A last-attempt guarantee must be visibly marked as no longer in force rather than
  silently deleted — a superseded decision that vanishes is indistinguishable from one nobody noticed.
- Everything §7 says that DID ship stays and is now stated as fact: the cap at the strongest *served*
  rung, the costly floor binding the ladder absolutely, actions-only (a judge guardrail is never
  escalated), and `tierSource: "escalated"` as the per-attempt record.

Do not restructure either document beyond what the above requires, and do not reword a passage away
from its own conventions to satisfy a pattern — write what is true and let the check read it.
