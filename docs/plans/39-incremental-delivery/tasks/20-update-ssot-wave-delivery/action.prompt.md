## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g. `20-update-ssot-wave-delivery`), NOT the
  stableId. The harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "20-update-ssot-wave-delivery": { "someKey": "someValue" } }`.
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

Record design 39 §5's contracts in `docs/plans/02-schemas-and-contracts.md`:

- a wave's **`brief.md` front matter** gains **`delivers: true`** (bool, **default false**). NOT a
  wave manifest — there is no such thing, and §14.1 says so; the design's first draft was corrected
  at review. Record the front-matter surface in §14.1's layout notes and in §14.10 (`brief.md`),
  and say that `WaveDefinitionHash` already folds it, so flipping the flag on a completed wave
  trips drift;
- `run.json`'s `waves.<dir>` gains **`delivered`** — `{at, commit, covers: ["<wave>", …]}` or null,
  where `covers` names the non-delivering waves that rode along, so the report can say what a merge
  actually carried;
- the **`WaveDelivered`** observer event;
- **`GR2078`** (a post-delivery wave with no entry preflight) and **`GR2079`** (a `delivers: true`
  wave with no exit gate), BOTH warnings, in the diagnostics registry section — and while you are
  there, **correct the stale sentence** that currently reads *"an unrelated new code should take
  `GR2078`"*: this plan takes GR2078 and GR2079, so the next free code is **GR2080**. GR2077 stays
  reserved by name;
- the **trial-merge ref** `refs/guardrails/trial/<waveDir>` (§1): a delivering wave gates against
  the trial merge and only fast-forwards the user's branch on green, which changes §14.3's
  exit-gate contract;
- **`DecisionEntry` gains a `Wave` member** (§1a) — the wave-scoped interlock reads a recorded
  attribution rather than parsing `Subject`. That is a change to the shared `decisions[]` surface;
- in **§7**, beside `supplied[]`, the **`refreshed[]`** provenance section (design 39 §1c, "How a refresh
  is recorded"): `"refreshed": [ { "at", "commit", "from", "upstream", "deliveredWave", "paths" } ]` —
  absent (never null, never empty) when there was no refresh, and written only after its commit exists;
- in **§5.3**, the **refresh commit's shape**: a `--no-ff` merge of the `upstream` sha (never the branch
  name) in the integration worktree, with the plan-branch tip as FIRST parent and the trailer
  `Refreshed-From: <from>` / `Guardrails-Run: <runId>` — never `Supplied-By:`, because nothing was
  supplied — and its **trigger**: refresh iff the user's branch tip was NOT an ancestor of the
  plan-branch tip at delivery. Say why the trigger is not `FastForwarded`: the trial merge makes every
  promotion a fast-forward, so that check would never fire;
- in **§14.3**, the **gate-halt disclosure**: a wave entry-preflight or exit-gate halt appends every
  `supplied[]` and `refreshed[]` record, oldest first (a refresh reads `refresh from '<from>' at
  <upstream, 10 chars>`), AFTER the failing check names, and a run with neither section renders
  byte-identically to today; and the **single-reader rule** — `UnauthoredContentNote` is the ONLY code
  that reads the two sections to answer "what is in this tree that no task authored?".

Write it in the document's own conventions — match the surrounding sections rather than importing
design 39's. Do NOT reword existing prose to satisfy a checking pattern; if a required token reads
wrong in context, say so via `needsHuman`.

**Scope boundary (harness-enforced):** Write only to `docs/plans/02-schemas-and-contracts.md`. After this
task completes, the harness runs a `git diff` membership check and rejects any edit outside these paths. An
out-of-scope edit fails the task immediately and consumes a retry. If you hit a compile error caused by a
missing symbol in another file, do NOT edit that file — write `{"needsHuman": "<what is missing>"}` to the
state-out path and stop.

**The harness runs this task's guardrails itself when you finish.** Do not try to run the guardrail scripts yourself: the shell they need is not granted to you, and a call refused on two attempts can halt the task even after the work is done.

