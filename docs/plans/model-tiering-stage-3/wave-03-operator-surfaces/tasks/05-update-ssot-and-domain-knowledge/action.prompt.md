## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — for a WAVED plan that is the WAVE-QUALIFIED id, i.e.
  `{ "wave-03-operator-surfaces/05-update-ssot-and-domain-knowledge": { "someKey": "someValue" } }`.
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

Record the two operator-surface contracts this wave just shipped. You are the **only** task in this wave
permitted to touch these files; two tasks sharing either of them is the union hazard that costs a run.

**Read what tasks 02, 03 and 04 actually landed before writing.** Your job is to describe the shipped
code, not this prompt's summary of it — in particular, the exact wording of the new log line and of the
new interface member's doc comment are theirs, not yours.

### What changed, in one paragraph

Wave 2 made `AttemptProvenance.Model` best-known-actual and added `RequestedModel`, written **only** when
the route asked for something else. This wave put both in front of the operator, and nothing here
re-derives either: the per-attempt log preamble and a new live/plain UI event both read the folded
provenance object.

### 1. `docs/plans/02-schemas-and-contracts.md`

Two deltas. Neither is a new section — both extend text that is already there.

- **§8, the per-attempt log layout.** The `attempt-route.log` entry in that fenced block currently
  describes the resolved block / model / effort, the rung requested vs served, the `tierSource`, and the
  two loud §9.6 lines. Extend it with the mismatch disclosure: the file now names the literal key
  **`requested model:`**, present **only** when the runner echoed something other than the route asked
  for — and, like its `attempt-provenance.json` sibling two lines above, the log is **re-written once the
  action returns**, because the observed model is not known when the attempt launches. That sibling entry
  is the precedent for both the tone and the placement; follow it rather than inventing a new form.
- **The §9.6 disclosure prose** (grep for `Disclosure — a climb and a binding ceiling must be LOUD`).
  That paragraph enumerates what the route log names and then lists the loud lines. Add the model
  mismatch to it, in the document's own voice: the presence of `requested model:` *is* the mismatch
  signal — there is no separate flag, and an always-written line would be a duplicate of `model:` in the
  overwhelmingly common agreeing case, which is exactly what the contract refuses. Say plainly that this
  changes what is LOGGED, never what is SELECTED, as the surrounding text already does for the climb and
  the ceiling.
- **The new UI event.** Document `IRunObserver.AttemptModelResolved` beside the other observer events
  this file already names inline (`IRunObserver.DecisionRecorded`, `IRunObserver.PromptPaused`,
  `IRunObserver.OverwatchNoVerdict`, `IRunObserver.WaveStarting` — six such mentions exist; match that
  form). Two facts earn their place: it carries the best-known-actual model plus the requested one **only
  on disagreement**, and — like `VerifierAdvisoryFound`, whose paragraph is the precedent — it has a
  **default no-op body, so a transparent decorator must forward it explicitly** or it is swallowed
  silently in exactly the mode most operators run.

### 2. `.claude/skills/guardrails-domain-knowledge/SKILL.md`

This skill's frontmatter makes it SELF-UPDATING when a contract moves. Add a short note to the
**`## Model tiering -- the SCHEMA half only`** section (grep for that heading — wave 2's provenance note
is already there and is the precedent for length and placement): the per-attempt route log now discloses
the mismatch, and `IRunObserver.AttemptModelResolved` surfaces the same pair to the live and plain UIs.
**Two or three sentences** — this skill is a pointer, not a second copy of the SSOT. Deliver it via
`needsHarnessWrite` with an `edits` array, as described above.

### Do not re-litigate the settled shape

- `provenance.model` is best-known-actual; `requestedModel` appears **only** on disagreement; there is
  **no `resolvedModel` key** (DoR §9.3 asked for one, Stage 2 refused it, and the charter review settled
  it — `s3-provenance-shape`). Do not add it, and do not describe it as deferred.
- `docs/plans/17-model-tiering.md` is deliberately **not** in your scope: wave 2 already amended §9.3,
  and neither the route-log format nor the observer set is described in that document. Do not edit it.

**Scope boundary (harness-enforced):** Write only to `docs/plans/02-schemas-and-contracts.md` and
`.claude/skills/guardrails-domain-knowledge/SKILL.md`. The harness runs a `git diff` check after this
task and rejects any edit outside those two paths — an out-of-scope edit fails the task immediately and
consumes a retry. In particular, do NOT edit source or tests to match the prose: the code is the thing
that shipped, and this task records it.
