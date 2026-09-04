## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g.
  `11-record-ws-closure-in-design`), NOT the stableId. The harness REJECTS a fragment
  keyed by anything else (every attempt), so:
  `{ "11-record-ws-closure-in-design": { "someKey": "someValue" } }`.
- EXCEPTION — the CONTROL KEYS `needsHarnessWrite` and `needsHuman` are TOP-LEVEL
  SIBLINGS of your folder-name key, never nested inside it. They are instructions to
  the harness, not state, so the rule above does not cover them:
  `{ "11-record-ws-closure-in-design": { "someKey": "someValue" },
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

Record the review closure in `docs/plans/585-layer3-webhooks-contract.md`, the layer-3 design of
record.

**This is a small task — one new section and one corrected line.** Do not restate the design, do not
re-argue any decision, and do not touch the eleven sections that already exist beyond the one line
named below.

### Which file is the design of record — read this before you open anything

Three near-identical copies of this design sit side by side in `docs/plans/`. You are editing exactly
one of them, and that is deliberate:

| File | What it is | Your action |
|---|---|---|
| `585-layer3-webhooks-contract.md` | **The design of record.** The living document a future reader is pointed at. | **EDIT THIS ONE — it is your only write scope.** |
| `585-layer3-webhooks.charter.md` | The **reviewed charter** — the exact bytes the human annotated, carrying its `charter-format-version` marker and its review stamps. | **NEVER EDIT.** It is hash-stamped and `charter verify` depends on those bytes; changing so much as a space breaks the custody chain that proves *this* review happened over *these* words. An immutable review artifact is the whole point of it. |
| `36-onevent-webhooks.md` | The **flattened handoff** the current run executes from, with the five `:::question` answers folded in inline. | **Read-only.** Read the settled answers here (section 2 of your task below needs them); it is a generated hand-over, not the record. |

Do not "keep them in sync" — they are three different artifacts with three different jobs, and the
divergence between them is intentional. The harness enforces this: your `writeScope` is the single
contract file, and an edit to either of the others fails the task immediately.

**Scope boundary (harness-enforced):** Write only to
`docs/plans/585-layer3-webhooks-contract.md`. After this task completes the harness runs a `git diff`
check and rejects any edit outside that one file. An out-of-scope edit fails the task immediately and
consumes a retry. If recording the closure appears to require changing another file, do NOT change
it: write `{"needsHuman": {"question": "<what and why>", "kind": "blocked-work"}}` to the state-out
path and stop.

### 1. Add a new closing section: `## 12. Review closure`

Append it after section 11 (`## 11. Proposed plan-document edits`), as the document's last section.
It records two things and nothing else:

- **The `ws:` endpoint is SUPERSEDED, not deferred.** Point at section 2.1, which argues it: a `ws:`
  endpoint removes one of the consumer's three obligations, the webhook removes all three, and the
  "did it arrive?" accounting moves to the side that can answer it. So **no `ws:` follow-up issue is
  filed**, and **#585 can be closed with layer 3's implementation** rather than left open behind a
  dangling question. Say "superseded" in those words — the distinction from "deferred" is the whole
  point of the record, and a reader who finds "deferred" will go looking for the follow-up issue that
  does not exist.
- **The charter review settled all five open questions.** The design carried five `:::question`
  blocks and every one of them is answered; the answers are recorded inline in the flattened plan
  of record, `docs/plans/36-onevent-webhooks.md`. Read them there and name what was settled — briefly,
  one line each is plenty. **The review happened in Charter, not in a draft PR** (a design of record
  is reviewed in Charter; a PR is a code-review vehicle), so say Charter.

### 2. Correct the stale status line

The document's fourth paragraph still reads **"Status: proposed. To be delivered as a draft PR for
inline review…"**. That is no longer true: the review has happened. Update that line to say the
design is reviewed and settled, and name Charter as the vehicle rather than a draft PR.

### Write it in the document's own voice

Match the surrounding sections — heading depth and numbering, the way section 2.1 and section 8 state
a closure, the em-dash-and-bold prose style this document uses throughout.

### Done when

Section 12 exists as the document's last section, records the `ws:` supersession (in those words)
with its pointer to section 2.1, states that #585 can be closed with the implementation, records that
the Charter review settled all five open questions — and the status line at the top no longer claims
the design is awaiting a draft-PR review.
