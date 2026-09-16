## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g. `19-update-ssot-and-design-docs`), NOT the
  stableId. The harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "19-update-ssot-and-design-docs": { "someKey": "someValue" } }`.
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

Apply the plan-document edits of **design 41 section 12**, in
`docs/plans/41-overwatcher-supply-autoresolve.md`. **That section IS your specification**: it carries the
exact `:::diff` hunk for every edit below. Open it and apply each hunk; do not re-derive the wording from
the rest of the design.

These land in the SAME change as the code (invariant 4 — a contract that moves without its document is
how the next reader reasons from stale facts).

### `docs/plans/02-schemas-and-contracts.md` (eight edits)

- **§1** — the staging tree is **not** the overwatcher's hand-off point. This hunk both ADDS and
  **DELETES**: the parenthetical listing who hands off through `logs/<runId>/supplied/` loses its
  `, or the overwatcher, §3.6` item, because the missing-resource auto-resolve commits directly and never
  stages. Removing those words is half the edit — adding the new sentence without removing them leaves the
  document asserting both.
- **§2.1** — the three new `task`-boundary `decision` values: `advisory`, `auto-supplied` and `observed`,
  beside the shipped `halted` / `prompted-approved` / `prompted-declined` / `auto-applied` / `no-verdict`.
  Say why `auto-supplied` is deliberately NOT `auto-applied` (that token means a provably safe resolution;
  this is a bounded judgement) and that it holds delivery exactly as `proceeded-best-guess` does.
- **The delivery interlock, in BOTH places the SSOT states its token set** — the #597 banner's case (b),
  and `delivery.reason`'s case (a′). Each gains `auto-supplied`. One token set, shared by the run-end and
  the wave-barrier interlock. Editing only one of the two is the defect this pair of hunks exists to
  prevent.
- **§7 `supplied[]`** — what a `by: "overwatcher"` record means: written only by the certified
  missing-resource auto-resolve, its `commit` on the plan branch, it never drains `logs/<runId>/supplied/`,
  its `bytes` is the source blob's size, and the checkout commit and branch it read from are named in the
  `auto-supplied` `decisions[]` entry written in the same step.
- **§8 `overwatch.jsonl`** — the new trigger: `trigger: "missing-resource"`, a `fixes[]` entry of
  `{ kind: "resource-supply", authority: "default", target: <path> }` per proposed op, and — on
  `decision: "auto-supplied"` — `applied: { supplied: [<paths>], commit: <sha> }`.
- **§8.1** — the `supplied-resources-committed` row gains `by` (`operator` | `overwatcher` |
  `task:<folder>`, the same value as the `supplied[]` record), and the human-facing console line becomes
  `[supplied] by <by>: N resource(s) committed <commit>: <paths>` in both the live table and `--no-ui`
  output.
- **§9.2** — two hunks. The ONE exception to *"never fires when the agent itself emitted `needsHuman`"* is
  the **missing-resource consult**; and the silence rule ("not consulted records nothing") gains its one
  exception — once the dial is engaged and the halt is shaped like a missing resource, EVERY reason the
  auto-resolve is not attempted records one outcome-inert `observed` entry, because at the dial that
  promised an auto-resolve, its absence must not be silent.
- **NEW §9.2.2** — inserted after §9.2.1 and before §9.3, with the heading and the full body design 41 §12
  gives: When (the three tiers), Candidates, Proposal, Certification
  (`OverwatchSupplyAutoResolve.Certify`), Consumer (`Scheduler.OnSettledAsync`), Record, and Floors
  untouched. `MissingResourceSignal` is the Tier-1 detector and is named there.

### `docs/plans/40-in-flight-resource-supply.md` §3

Add the **"Wired by design 41 (issue #712)"** paragraph immediately after the paragraph ending
*"...does not treat this as the precedent that erodes that ruling."* Plan 40 shipped this decision as a
gate nothing called; design 41 is what wires it.

### `docs/plans/12-autonomous-mode.md` §11

Add `auto-supplied` to the suppression set in the `§5.3 / §2 (delivery)` bullet, beside
`proceeded-best-guess` and `proceeded-unreviewed` — the set that **defaults `mergeOnSuccess` to OFF**.

### How to write it

Write each edit in **the target document's own conventions** — match the surrounding section's shape, not
design 41's. **Do not reword existing prose to match a checking pattern.** The guardrail requires only
tokens the contract genuinely needs, and every one of them was checked against a sibling precedent already
in that same file before it was pinned. If one still reads wrong in context, say so via `needsHuman`
rather than bending the document around it.

One spelling note: design 41 §12 refers to the design of record as
`41-overwatcher-supply-autoresolve.charter.md`, while the committed file is
`docs/plans/41-overwatcher-supply-autoresolve.md`. Use whichever form the surrounding document already
uses for design-of-record references; the guardrail accepts either.

**Scope boundary (harness-enforced):** Write only to `docs/plans/02-schemas-and-contracts.md`,
`docs/plans/40-in-flight-resource-supply.md` and `docs/plans/12-autonomous-mode.md`. After this task
completes, the harness runs a `git diff` membership check and rejects any edit outside these paths. An
out-of-scope edit fails the task immediately and consumes a retry. If you need a change in another file to
make this one correct, do NOT edit that file — write `{"needsHuman": "<what is missing>"}` to the
state-out path and stop.

**The harness runs this task's guardrails itself when you finish.** Do not try to run the guardrail scripts yourself: the shell they need is not granted to you, and a call refused on two attempts can halt the task even after the work is done.
