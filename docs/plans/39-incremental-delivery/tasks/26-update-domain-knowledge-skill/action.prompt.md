## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g. `26-update-domain-knowledge-skill`), NOT the
  stableId. The harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "26-update-domain-knowledge-skill": { "someKey": "someValue" } }`.
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

Record plan 39's wave-delivery model in `.claude/skills/guardrails-domain-knowledge/SKILL.md` —
design 39 §1, §1a, §1b, §1c and §5, and the SSOT sections task 20 wrote.

**This is the surface an AGENT reads, and nothing tests it.** The SSOT is the contract; this skill is
where every agent working in this repo learns the model. A capability the domain skill does not
describe is one the next breakdown, review or overwatcher reasons about from stale facts. Plan 40 had
this task; plan 39's first breakdown did not.

Cover these, in the skill's own style. Edit the existing bullets where each fact belongs — do not
append a detached list at the end:

- **The per-wave `delivers` flag** — `delivers: true` in a wave's `brief.md` YAML front matter,
  default false, with no per-wave manifest; and a wave with no `guardrails/` exit gate is never a
  delivery point (`GR2079` warns).
- **Per-wave delivery at the barrier** — a delivering wave's work ships to the user's branch when its
  exit gate passes against the trial merge, not only at run end. Find the existing
  `**End-of-run delivery**` bullet and extend it; do not leave it implying delivery happens only once.
- **The journal record** — `run.json`'s `waves.<dir>.delivered` (`{at, commit, covers}`, or null).
- **The observer event** — `IRunObserver.WaveDelivered`, forwarded through every decorator (the
  `ObserverForwardingSweepTests` contract). It belongs beside the existing
  `IRunObserver.WaveStarting`/`WaveFinished` mention.
- **The wave-scoped interlock** — a machine decision holds back delivery for its OWN wave only, not
  the whole run. Use that phrase: `wave-scoped interlock`.
- **The post-delivery refresh and the entry preflight that makes it safe** — when the user's branch
  has moved on (its tip is not an ancestor of the plan-branch tip at delivery), the plan branch merges
  the delivered user tip back in; the next wave then re-verifies its own baseline before spending
  anything (`GR2078` warns when a wave that follows a delivery point has no entry preflight).
- **`refreshed[]` and its single reader** — a refresh is recorded in its own top-level `run.json`
  section, `refreshed[]`, beside plan 40's `supplied[]`, and its commit carries a `Refreshed-From:`
  trailer. `UnauthoredContentNote` is the ONE reader of both sections, which is what lets a wave-gate
  halt say which refresh or supply is in the tree instead of blaming the wave that failed.

Also add `GR2078` and `GR2079` to the GR-code ledger — the list that already carries
`GR2042 = StructuralOverScope`.

**A refresh is not a supply, and the skill must not say otherwise.** Never describe a refresh as
recorded in `supplied[]`, as having a `by` value, or as carrying a supply trailer. When you contrast the
two, say what a refresh IS — its own `refreshed[]` section and a `Refreshed-From:` trailer — and make
any comparison an explicit negation ("a refresh is never recorded in `supplied[]`"). The guardrail
refuses an un-negated description of the rejected model, however it is worded.

## Your deliverable is under `.claude/` — use needsHarnessWrite, do NOT write directly

The tool-permission layer refuses every `.claude/` write unconditionally. Do NOT attempt a direct
`Write`/`Edit`: a probe wastes a turn and populates the permission-wall tracker. FIRST write a
`needsHarnessWrite` request to the state-out path; the harness performs the write and your guardrails
then run against the result.

**`needsHarnessWrite` is a TOP-LEVEL key — a SIBLING of your folder-name key, NEVER nested inside it.**
Nested one level down the harness REJECTS the attempt and nothing is written.

Use **`edits`**, not `content` — `SKILL.md` is very large, and `edits` costs what your change costs:

`{"needsHarnessWrite": {"path": ".claude/skills/guardrails-domain-knowledge/SKILL.md", "reason": "design 39
wave-delivery domain model (#525)", "edits": [{"old": "<verbatim anchor>", "new": "<replacement>"}]}}`

Each `old` must occur EXACTLY ONCE — copy it out of the file rather than retyping, and include enough
context to be unique.

**Scope boundary (harness-enforced):** Write only to `.claude/skills/guardrails-domain-knowledge/`. After this
task completes, the harness runs a `git diff` membership check and rejects any edit outside these paths. An
out-of-scope edit fails the task immediately and consumes a retry. If you hit a compile error caused by a
missing symbol in another file, do NOT edit that file — write `{"needsHuman": "<what is missing>"}` to the
state-out path and stop.

**The harness runs this task's guardrails itself when you finish.** Do not try to run the guardrail scripts yourself: the shell they need is not granted to you, and a call refused on two attempts can halt the task even after the work is done.
