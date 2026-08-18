## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — in a WAVED plan that key is the WAVE-QUALIFIED id, i.e.
  `wave-02-attempt-launch-wiring/14-land-ssot-schema-deltas`, NOT the stableId and NOT
  the bare folder name. The harness REJECTS a fragment keyed by anything else (every
  attempt), so:
  `{ "wave-02-attempt-launch-wiring/14-land-ssot-schema-deltas": { "someKey": "someValue" } }`.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Task

Land wave 2's schema deltas in **`docs/plans/02-schemas-and-contracts.md`** — the SSOT.

**Invariant 4: a schema change lands in the SSOT in the SAME change as the code that motivates it.**
Every other task in this wave has already shipped its code; this task is what stops those changes
from being claims that live outside the schema and decay with nothing noticing. The plan's terminal
gate asserts this file mentions `tierSource`, and it currently does **not** — so without this task
the whole plan fails at the terminal gate, after every task has run, where there is no retry.

**`docs/plans/17-model-tiering.md` is the design of record** — §12.4, §12.5 and §9.3 in particular.
Where it and this summary differ, it wins.

**Read the actual shipped code before writing.** This documents what LANDED, not what was planned.
Every type named below is on this branch now; read it rather than trusting this prompt's paraphrase.

### What to document

**1. §7 journal — the attempt record (DoR §12.4).**

- `provenance` gains **`runner`** (resolved block name), **`kind`**, **`tier`** (the rung that
  resolved), and **`tierSource`**: `"task" | "plan-default" | "override"`. Document the **producer of
  each value**, because each has exactly one (DoR D31):

  | `tierSource` | produced by | `provenance.tier` |
  |---|---|---|
  | `"task"` | `action.tier` (or judge frontmatter `tier`) supplied the rung | the rung |
  | `"plan-default"` | `tiering.defaultTier` supplied it | the rung |
  | `"override"` | a full `action.runner`/`action.model` **pin** bypassed resolution | **absent** |
  | *(absent)* | legacy fallback — no rung at all (D30) | absent |

- The attempt `outcome` enum gains **`no-route`** — resolution found zero candidate blocks
  at-or-above the task's rung; settles needs-human. (The SSOT already uses the string `no-route` in
  §9.6 prose; this is the **enum value**, which is new.)
- The attempt record gains optional **`usage { inputTokens, outputTokens }`**. Document it
  **honestly**: the field exists and, as of this wave, **nothing populates it** — the value reaches
  `PromptResult` and stops, because journalling it needs `ActionRun`/`RunReport`/`Scheduler` changes
  that were deferred (issue #475). Say so in one sentence rather than describing a field that is
  always absent as though it were live.
- The attempt record gains optional **`judge { runner, kind, model, effort, tier, strength, bumped }`**
  — absent entirely when no judge resolved through routing (Invariant 7).
- **Absent, never null** throughout: old journals read fine, and a script attempt simply omits these.

**2. §3 action block — `ActionDefinition.TierOrigin`.**

Wave 1 added a `TierOrigin` enum (`None` / `Task` / `PlanDefault`) recording **which source supplied
`action.tier`** — the distinction `PlanLoader` used to destroy by collapsing `action.tier` and
`tiering.defaultTier` into one field at load. It is the input `tierSource` is derived from (together
with the pin check for `override`). Document that deriving the origin by *comparing* the tier to
`tiering.defaultTier` is wrong — it misreports whenever a task's own tier equals the default.

**3. §9 seam note + §9.6's v1 content.**

Note in §9 that `FromConfig` switches on `kind`, and that `--model`/effort flags are emitted from the
**resolved** route. Then give §9.6 its v1 normative content: the single candidacy predicate
(`routing` present AND rung ∈ `routing.tiers` AND not `costly`), ascending-`strength` ordering, the
climb to a stronger rung, the **costly floor** (no override, no dial), and `no-route`.

**4. §6.3's answer — read it from state, do not re-derive it.**

Task `04-implement-unavailability-classification` published its answer to the DoR §6.3 open question
in the state you receive, under the key
`wave-02-attempt-launch-wiring/04-implement-unavailability-classification`, with fields
`answerToDoR63`, `alreadyCovered`, `added` and `newEnumMember`. **Read `GUARDRAILS_STATE_IN` and
document that answer** — which connection-level shapes the shipped quarantine already classified
`Transient`, which signals task 04 added, and that **no new `PromptFailureKind` member was
introduced** (v1 forbids it; a connection failure is `Transient` and rides the shipped #115 pause).
Name the concrete signal families so the answer is citable rather than a summary — the DNS family,
the errno spellings, TLS/handshake, and a runner binary that never launched.

An answer that exists only as a regex in `ClaudeSignalClassifier.cs` is an answer nobody can cite;
that is exactly why task 04 was made to publish it and why this task exists.

### How to write it

Match the surrounding document — its section numbering, table style, and voice. This file is large;
**edit the relevant sections in place, do not append a new block at the end** and do not restructure
anything you were not asked to change. Every delta above is **additive and optional**; nothing here
removes or renames an existing field, and an older journal or plan folder must still read cleanly.

**Scope boundary (harness-enforced):** Write only to `docs/plans/02-schemas-and-contracts.md`. After
this task completes, the harness runs a `git diff` check and rejects any edit outside that path —
including `docs/plans/17-model-tiering.md` (the DoR is input, not output), any file under `src/` or
`tests/`, or the plan folder itself. An out-of-scope edit fails the task immediately and consumes a
retry. If the code you are documenting looks wrong, document what it DOES and say so in your
state-out fragment under a `notes` key — do not fix it here.
