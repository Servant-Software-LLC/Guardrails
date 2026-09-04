## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g.
  `01-update-ssot-event-schema`), NOT the stableId. The harness REJECTS a fragment
  keyed by anything else (every attempt), so:
  `{ "01-update-ssot-event-schema": { "someKey": "someValue" } }`.
- EXCEPTION — the CONTROL KEYS `needsHarnessWrite` and `needsHuman` are TOP-LEVEL
  SIBLINGS of your folder-name key, never nested inside it. They are instructions to
  the harness, not state, so the rule above does not cover them:
  `{ "01-update-ssot-event-schema": { "someKey": "someValue" },
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

Land the five schema edits that `--on-event` requires into `docs/plans/02-schemas-and-contracts.md`.

This is invariant #4 being honoured rather than deferred: **a contract change lands in the SAME
change-set that motivates it.** Layer 3 makes two contract changes — a new `bracket` field on the
section 8.1 event row, and a whole new section 8.3 for webhook delivery — and both are written out
verbatim in the plan of record. Your job is to apply them, not to invent them.

**Scope boundary (harness-enforced):** Write only to `docs/plans/02-schemas-and-contracts.md`. After
this task completes the harness runs a `git diff` check and rejects any edit outside that one file —
source, tests, other docs, anything. An out-of-scope edit fails the task immediately and consumes a
retry. If applying an edit appears to require changing another file, do NOT change it: write
`{"needsHuman": {"question": "<what the other file needs and why>", "kind": "blocked-work"}}` to the
state-out path and stop.

### The exact text is already written — copy it, do not compose it

Open `docs/plans/36-onevent-webhooks.md` and read section
**`## 7. Schema changes — exact 02-schemas-and-contracts.md edits`** in full before you touch
anything. It contains Edits 1 through 5 as literal text. Apply all five **verbatim**. Edit 1 and
Edit 4 are given as diffs (`-` is the line to remove, `+` the line to write); Edits 2, 3 and 5 are
given as block quotes — strip the leading `> ` and write the content as ordinary document prose.

Read enough of the surrounding SSOT to place each edit correctly. Do not restate the design
document's arguments; state the contract.

### Anchor every edit by GREP, never by line number

Section 7 cites line numbers ("currently line 3798", "ending line 3869", "line 3911", "line 1715").
Those were true when the design was written and go stale the moment anything above them shifts.
**Find each site by grepping for its durable marker instead:**

| Edit | What it does | Grep for this marker |
|---|---|---|
| 1 | In section 8.1's per-row field table: correct the `` `seq` `` row's wording and add a new `` `bracket` `` row immediately after it | the bold line `**On every row, without exception.**`, then the table row whose first cell is the backticked field name `seq` |
| 2 | Append one sentence to section 8.1's multi-process paragraph | `A runId spans processes` |
| 3 | Insert an entirely NEW `### 8.3` — between the END of section 8.2 and the `## 9. Prompt runners` heading | `## 9. Prompt runners` |
| 4 | Replace the stale sentence in section 5.1's closing paragraph | `Harness-process knobs` |
| 5 | Append one sentence to section 12.2's `GET /events` paragraph | `GET /events` — **there are TWO occurrences**; the target is the one inside section 12.2, not the cross-reference inside section 8.1 |

If a grep returns a different number of hits than the table says, **trust the grep**, apply the edit
where the surrounding prose actually matches the description, and say so in your summary.

### Two placement facts worth stating twice

- **Edit 3 creates a new subsection, it does not append to 8.2.** The heading must be at `###` depth
  and numbered `8.3`, and it must sit after all of section 8.2's body and before `## 9.`.
- **Edit 1's new row uses the table's existing shape.** Section 8.1's field table is already
  `` | `field` | description | `` — one backticked field name in the first cell. Write the `bracket`
  row in that same form. This is the document's own convention, not a pattern invented for a check.

### Done when

All five edits are in the file; section 8.3 exists at the right depth in the right place; the
`bracket` row sits in the section 8.1 field table in that table's own style; and the document still
reads as one piece rather than as an appended patch.
