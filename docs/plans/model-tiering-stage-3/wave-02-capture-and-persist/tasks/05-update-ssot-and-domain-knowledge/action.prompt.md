## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — for a WAVED plan that is the WAVE-QUALIFIED id, i.e.
  `{ "wave-02-capture-and-persist/05-update-ssot-and-domain-knowledge": { "someKey": "someValue" } }`.
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

## Harness-write escape hatch (one of your three files lives under `.claude/`)

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

**Only the `.claude/` file needs the hatch.** The two files under `docs/plans/` are ordinary writes — use
`Edit` on those directly.

## Task

Record the provenance contract this wave just implemented. You are the **only** task in this wave
permitted to touch these files; two tasks sharing either of them is the union hazard that costs a run.

### The settled contract — transcribe it, do not re-derive it

The charter review resolved `s3-provenance-shape`:

- **`provenance.model` becomes best-known-actual** — the model the runner ECHOED, else the resolved
  route's model, else the `"(cli default)"` sentinel. Existing readers improve with no change on their
  side.
- **`requestedModel` is written ONLY when it differs** from `model`. Its *presence* is the mismatch
  signal; there is no separate flag and no always-written key.
- **There is NO `resolvedModel` key.** DoR §9.3 asked for one; Stage 2 refused it in the shipped
  contract (grep `src/Guardrails.Core/Journal/JournalModel.cs` for *"two fields claiming the same fact is
  how they drift"*). The settled shape honours both intentions: one field per fact, and a second field
  only for the disagreement. **Do not add it, and do not describe it as deferred — it is refused.**
- `AttemptProvenance.Effort` already shipped with Stage 2. Do not re-add it or describe it as new.

Read what tasks 02 and 04 actually landed before writing — the document must describe the shipped code,
not this prompt's summary of it.

### 1. `docs/plans/02-schemas-and-contracts.md` — the §7 provenance delta

- In the `"provenance": { … }` example block (grep for `"tierSource": "task",` to find it), add a
  **`"requestedModel"`** entry with a trailing `//` comment in the same style as its neighbours, and
  update the existing `"model"` entry's comment so it states the best-known-actual precedence rather
  than the resolved-route-only one it states today.
- Add a short prose paragraph in the "Per-attempt tier provenance" section explaining the pair. The
  **`usage` — the tokens-only accounting surface, now written on both record paths.** paragraph a little
  further down is the precedent for placement, tone and bolded lead-in — follow that form rather than
  inventing one. Say plainly that `requestedModel` is present only on disagreement, and that there is no
  `resolvedModel` key.

### 2. `docs/plans/17-model-tiering.md` — the DoR §9.3 amendment

§9.3 (grep for `### 9.3 Journal / provenance`) currently describes `resolvedModel` as a field #349 would
carry. Amend it to the settled shape above. Keep it an **amendment in place** — do not delete the
sequencing history that explains why Stage 2 did not block on #349; that context is why the section reads
the way it does.

### 3. `.claude/skills/guardrails-domain-knowledge/SKILL.md` — the contract note

This skill's frontmatter makes it SELF-UPDATING when a contract moves, and `provenance.model` changing
meaning is exactly that. Add a short note to the **`## Model tiering -- the SCHEMA half only`** section
(grep for that heading): `provenance.model` is best-known-actual, `requestedModel` appears only on
disagreement, there is no `resolvedModel`. Two or three sentences — this skill is a pointer, not a second
copy of the SSOT. Deliver this file via `needsHarnessWrite` with an `edits` array, as described above.

**Scope boundary (harness-enforced):** Write only to `docs/plans/02-schemas-and-contracts.md`,
`docs/plans/17-model-tiering.md` and `.claude/skills/guardrails-domain-knowledge/SKILL.md`. The harness
runs a `git diff` check after this task and rejects any edit outside those three paths — an out-of-scope
edit fails the task immediately and consumes a retry. In particular, do NOT edit source or tests to match
the prose: the code is the thing that shipped, and this task records it.
