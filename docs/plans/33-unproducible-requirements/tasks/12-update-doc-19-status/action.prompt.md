## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g.
  `12-update-doc-19-status`), NOT the stableId. The harness REJECTS a fragment
  keyed by anything else (every attempt), so:
  `{ "12-update-doc-19-status": { "someKey": "someValue" } }`.
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

Two edits to `docs/plans/19-producer-coverage.md`, and **no others**. That document is the prior design
this plan builds on; it is not being rewritten.

**1. The status table.** Its `Milestone A — harness half (GR2060)` row currently reads **`NOT BUILT`**.
Change it to a pointer at this plan — GR2060 shipped, see `docs/plans/33-unproducible-requirements.md`.

**2. D2 gains exactly one sentence.** Append it to the existing D2 decision, verbatim in substance:

> *"a later instance (#474, plan 30) looked like shape (a) with a derived path, and a lint for it was
> designed and declined — the shape has never occurred in a form a lint could see; see
> `33-unproducible-requirements.md` §3.4 and §6.3. D2 is unchanged and is now better evidenced."*

**Read that sentence carefully before you write it, because its shape is the point.** It does NOT say
D2 was wrong, and it does NOT soften it. D2 held; the new evidence supports it. A sentence that reads as
a retraction would misrepresent the outcome — the declined lint is evidence *for* D2, not against it.

**Do NOT:**

- rewrite any other part of doc 19, including §3.1's predicate or its ten conditions — this plan adopted
  them verbatim precisely because they were right;
- change `docs/plans/03-roadmap.md`. GR2060 is not a v2 bet; it is v1 author-time validation, and the
  roadmap needs no edit;
- mark Milestone B of doc 19 as anything. It is out of scope here.

**Scope boundary (harness-enforced):** Write only to `docs/plans/19-producer-coverage.md`. After this
task completes, the harness runs a `git diff` check and rejects any edit outside that path.

## Done when

- The Milestone A status row points at this plan instead of reading `NOT BUILT`.
- D2 carries the one added sentence, framed as corroboration rather than retraction.
- Nothing else in the document changed.
