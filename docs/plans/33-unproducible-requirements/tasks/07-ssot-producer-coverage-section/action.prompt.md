## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g.
  `07-ssot-producer-coverage-section`), NOT the stableId. The harness REJECTS a fragment
  keyed by anything else (every attempt), so:
  `{ "07-ssot-producer-coverage-section": { "someKey": "someValue" } }`.
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

Add **§4.8** to `docs/plans/02-schemas-and-contracts.md` — the SSOT section for GR2060, which task 4
shipped. The contract moves in the same change-set as the code (invariant 4), and this is that move.

**Placement:** immediately after §4.7 and before `## 5. Child-process contract`. §4.7 ends around line
1520 — an authoring-time snapshot, so **grep for the §4.7 heading and the `## 5.` heading** rather than
trusting the number.

**Heading, verbatim:**

`### 4.8 Guardrails that CANNOT PASS given what this plan BUILDS (validated, GR2060 — error)`

**Opening paragraph must say why this is a sibling section rather than a fourth row of §4.7's table:**
the §4.7 three are decidable from **one script's own text**; GR2060 is **relational** — it reads the
script, the union of every task's `writeScope`, and the workspace's current bytes. Same consequence (red
before the task runs, red forever, and `/guardrails-review` structurally misses it because it hunts
*weakness* while this guardrail is *strong*), different evidence base.

Carry doc 19 §3.1's predicate and **all ten conservatism conditions verbatim**, and the cross-reference
to §14.1/GR2062. Give §4.7 one closing sentence pointing forward to §4.8.

**Two paragraphs this section MUST carry — they are the reason it is being written by hand rather than
copied from doc 19, which did not anticipate either:**

**(a) The two suppressions, and that they are NOT interchangeable.** `PlanIsClosed` suppresses GR2060
for an **empty stub wave**. It does **not** cover an authored **partial prefix**, for which the
suppression lives in `Scheduler.UnsatisfiableWhileIncomplete`, keyed on `wavePrefixIsIncomplete`. State
plainly that `PlanIsClosed` returns `true` for a partial prefix and is therefore **not** a soundness
guarantee for the JIT gate. This is the trap that cost the design a milestone's worth of rework, and the
next reader will repeat it unless the document says so.

**(b) The excused-not-vanished rule.** A GR2060 finding excused at the JIT gate still appears in the
gate-decision report, and still errors under a plain `guardrails validate`. Suppression is about which
**verdict** a finding may cast, never about whether an operator **sees** it.

**Write it in the document's own voice.** Match the surrounding sections' heading depth, table style,
and level of detail. Do not restate the harness execution contract, and do not rewrite §4.7.

**Scope boundary (harness-enforced):** Write only to `docs/plans/02-schemas-and-contracts.md`. After
this task completes, the harness runs a `git diff` check and rejects any edit outside that path. §14.10's
GR-code paragraph is **task 8's** deliverable — do not touch it here, or the two tasks collide on the
same file.

## Done when

- §4.8 exists between §4.7 and `## 5.`, carrying the predicate, the ten conditions, and both required
  paragraphs.
- §4.7 carries one forward-pointing sentence.
- §14.10 is untouched.
