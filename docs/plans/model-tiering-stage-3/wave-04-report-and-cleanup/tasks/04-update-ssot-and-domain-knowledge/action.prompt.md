## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — for a WAVED plan that is the WAVE-QUALIFIED id, i.e.
  `{ "wave-04-report-and-cleanup/04-update-ssot-and-domain-knowledge": { "someKey": "someValue" } }`.
  The harness REJECTS a fragment keyed by anything else (every attempt), including the bare folder
  name and the stableId.
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

## Harness-write escape hatch (one of your two files lives under `.claude/`)

One of your deliverables is a file under `.claude/`, which a Claude Code subprocess CANNOT write — the
tool-permission layer refuses every `.claude/` write unconditionally. Do NOT attempt a direct
`Write`/`Edit` to the `.claude/` path: a direct-write probe wastes a turn and populates the harness's
permission-wall tracker. Instead, FIRST write a `needsHarnessWrite` request to the state-out path. The
harness (which is NOT subject to that layer) performs the write directly, then your guardrails still run
normally against the result. There are two forms, and they are mutually exclusive — send exactly one:

- **MODIFYING an existing file — use `edits` (this is your case):**
  `{"needsHarnessWrite": {"path": "<workspace-relative path>", "reason": "<why>", "edits":
  [{"old": "<verbatim anchor text>", "new": "<replacement text>"}]}}`.
  Each `old` must occur **exactly once** in the file — zero matches and two-or-more matches are both
  rejected, so include enough surrounding context to make each anchor unique. `old` is matched
  VERBATIM (exact indentation, punctuation and blank lines; only line endings are tolerated), so
  copy the passage out of the file rather than retyping it. Edits apply in order and ATOMICALLY: if
  any one fails, none are written and the file is unchanged. An empty `new` deletes the anchored
  text. Use `edits` **however large the file is** — its cost scales with your change, not the file.
- **CREATING a file — use `content`:**
  `{"needsHarnessWrite": {"path": "<workspace-relative path>", "content": "<full file content>",
  "reason": "<why>"}}`.
  Do NOT use `content` to modify a large existing file: the harness refuses full-content mode for an
  existing target over 64 KB, and re-emitting thousands of lines you did not mean to change risks
  silently corrupting them.

If your deliverable spans SEVERAL files, send an ARRAY of those entries in ONE request — one entry per
file, mixing `edits` and `content` freely. Do NOT deliver them one per attempt: a failed attempt rolls the
workspace back to a clean base, so an earlier attempt's write is DISCARDED and progress cannot accumulate.
The array is applied ATOMICALLY — if any entry fails, nothing is written anywhere and every file is
unchanged, so fix the entry the message names and re-emit the WHOLE array. One entry per file: two entries
naming the same file are rejected as ambiguous (merge their changes into a single `edits` array).

If you already attempted a direct write and it was refused, do NOT retry it or try workarounds
(PowerShell, `dangerouslyDisableSandbox`) — just emit `needsHarnessWrite` as above.

**Only the `.claude/` file needs the hatch.** `docs/plans/02-schemas-and-contracts.md` is an ordinary
write — use `Edit` on it directly.

## Task

Record the run-report contract this wave just shipped: #349's fifth and last operator surface. You are the
**only** task in this wave permitted to touch these files; two tasks sharing either of them is the union
hazard that costs a run.

**Read what `02-implement-models-used-report` actually landed before writing** —
`src/Guardrails.Core/Journal/JournalModelsUsed.cs` and the addition to
`RunCommand.PrintTotalCost`. Your job is to describe the shipped code, not this prompt's summary of it; in
particular the exact rendered segment format is the implementation's, not yours.

### What changed, in one paragraph

Wave 2 made `AttemptProvenance.Model` best-known-actual and added `RequestedModel`, written **only** when
the route asked for something else. Wave 3 put both in front of the operator per attempt. This wave closes
the set with the run-level aggregate: one summary line naming every model the run's attempts actually used,
and how many attempts each carried. Nothing re-derives anything — it is a read over the same journal
records the cost and per-tier lines already aggregate.

### 1. `docs/plans/02-schemas-and-contracts.md`

One delta, and it is **not** a new section — it extends the run-summary bullet list that is already there.
Grep for **`Per-tier spend: easy:`**; the models-used entry belongs immediately after that bullet, in the
same form. That bullet is the precedent for tone, length and structure — follow it rather than inventing
one. It should carry, in the document's own voice:

- the literal label the line prints, spelled exactly **`Models used`**, quoted the way that bullet quotes
  `Per-tier spend:` and the one above it quotes `Total prompt cost: $X.XXXX`;
- **what is aggregated** — the per-attempt **`provenance.model`** recorded in §7, one segment per distinct
  model, every attempt counted independently (a retry ran a model again), named the same way the per-tier
  bullet names `provenance.tier` + `costUsd` + `usage`;
- **the mismatch**, which is the whole reason the line is more than trivia: the requested id is named only
  where `provenance.requestedModel` was recorded, i.e. only where the runner served something other than
  the route asked for. Its presence *is* the signal; there is no flag beside it;
- **the suppression** — the line is **omitted entirely** when no attempt recorded a model, so a
  deterministic-only plan prints exactly today's summary. This is the same Invariant-7 discipline the
  per-tier bullet states, and it is the half a reader most needs, because it is what promises every
  existing single-model user's run report is unchanged;
- that it is **additive** to the total and per-tier lines, never a replacement.

Say plainly that the line is printed from the `run` summary only — `guardrails status` prints the cost line
but deliberately not this one, following the per-tier sibling.

### 2. `.claude/skills/guardrails-domain-knowledge/SKILL.md`

This skill's frontmatter makes it SELF-UPDATING when a contract moves. Add a short bullet to the
**`## Model tiering -- the SCHEMA half only`** section, immediately after the existing
**"Both are now IN FRONT OF THE OPERATOR"** bullet (grep for `BEST-KNOWN-ACTUAL` to find that family — it
is the precedent for length, placement and voice). Two or three sentences: the `run` summary now closes
with a **`Models used:`** line aggregating the per-attempt `provenance.model`, naming the requested id only
where it differed, and **absent entirely** on a run that recorded no model. This skill is a pointer, not a
second copy of the SSOT — point at SSOT section 9 for the wire detail. Deliver it via `needsHarnessWrite`
with an `edits` array, as described above.

### Do not re-litigate the settled shape

- `provenance.model` is best-known-actual; `requestedModel` appears **only** on disagreement; there is
  **no `resolvedModel` key** (DoR §9.3 asked for one, Stage 2 refused it, and the charter review settled
  it — `s3-provenance-shape`). Do not add it, and do not describe it as deferred.
- `docs/plans/17-model-tiering.md` is deliberately **not** in your scope: wave 2 already amended §9.3, and
  the run-summary surface is not described in that document. Do not edit it.
- The superseded `docs/plans/pilot-seat-model-provenance/` folder that `03-delete-superseded-plan-folder`
  removed is **not** a contract change and has no place in either document. Do not add a note about it.

**Scope boundary (harness-enforced):** Write only to `docs/plans/02-schemas-and-contracts.md` and
`.claude/skills/guardrails-domain-knowledge/SKILL.md`. The harness runs a `git diff` check after this task
and rejects any edit outside those two paths — an out-of-scope edit fails the task immediately and consumes
a retry. In particular, do NOT edit source or tests to match the prose: the code is the thing that shipped,
and this task records it.
