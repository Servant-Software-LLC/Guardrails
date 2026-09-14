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
design 39 §1, §1a, §1b, §1c, §4 and §5 as answered in review round 4, and the SSOT sections task 20
wrote.

**This is the surface an AGENT reads, and nothing tests it.** The SSOT is the contract; this skill is
where every agent working in this repo learns the model. A capability the domain skill does not
describe is one the next breakdown, review or overwatcher reasons about from stale facts. Plan 40 had
this task; plan 39's first breakdown did not.

Cover these, in the skill's own style. Edit the existing bullets where each fact belongs — do not
append a detached list at the end:

- **The per-wave `delivers` flag** — `delivers: true` in a wave's `brief.md` YAML front matter,
  default false, with no per-wave manifest; and a wave with no `guardrails/` exit gate is never a
  delivery point (`GR2079` warns).
- **Per-wave delivery at the barrier, through a trial merge** — a delivering wave's work ships to the
  user's branch when its exit gate passes against the trial merge, not only at run end. Find the
  existing `**End-of-run delivery**` bullet and extend it; do not leave it implying delivery happens
  only once. `IWorktreeProvider.CreateTrialDelivery` builds the trial merge in a harness-owned worktree,
  and `PromoteTrialDelivery` re-checks #588 (a moved branch) and #448 (a dirty working tree) before it
  fast-forwards the user's branch. **Say in one sentence that the trial merge commit is created with the
  user's git hooks** (#149), taken from the user's resolved hooks directory — including a relative
  `core.hooksPath`, which is how husky installs them and which a harness-owned worktree would otherwise
  skip. A promotion is a fast-forward, which runs no hook, so the trial merge commit is where a rejecting
  hook refuses a waved plan's delivery as `hook-rejected`. **Say that barrier delivery obeys the same
  switches as run-end delivery:** `--no-merge-on-success` turns it off at every wave barrier,
  `--merge-on-success` lifts a held delivery, a task definition edited mid-run blocks it (#556) as it
  blocks run-end delivery, and a serial run never delivers at a barrier. The interlock
  is consulted before the trial merge is built, so a held delivery never runs the user's hooks.
- **The journal record** — `run.json`'s `waves.<dir>.delivered`. A barrier delivery that begins always
  writes `status: running`, with `startedAt` and `covers`, even over an earlier `delivered` record: it is
  written when the delivery begins, after the switches and the interlock pass and before the trial merge
  runs the user's hooks (#625). It then settles as `delivered` (with `commit`) or `refused` (with
  `outcome` and `detail`). A `suppressed` record (with `detail` naming the decision) is written already
  settled. A resume whose trial finds the plan tip already on the user's branch (equal tips included, as
  right after a quiet-case promotion) skips the promotion and restores the prior `delivered` record, or
  writes one if the crash came before it, so it never records a refusal for a delivery that already
  landed. A rewound wave's re-run that delivers again replaces its record. Each settled record carries
  `at` and `covers`, every wave the delivery carries. A wave has no `delivered` key when it is not a
  delivery point, never reached its barrier, failed its exit gate, or had delivery resolved off. Its work
  reached the user's branch only if a later barrier delivery carried it (the wave is in that record's
  `covers`) or the run-end delivery landed (the top-level `delivery` record); otherwise the report says
  held or not reached. **Never teach that a missing `delivered` key means the work is not on the user's
  branch** — the guardrail refuses that sentence unless it names `covers` or the run-end delivery.
- **The observer event** — `IRunObserver.WaveDelivered`, raised only for `status: delivered` and only
  after the record is persisted, forwarded through every decorator (the `ObserverForwardingSweepTests`
  contract). It belongs beside the existing `IRunObserver.WaveStarting`/`WaveFinished` mention. It is
  projected into `observer.jsonl` as a `WaveDelivered` line (`member`, `waveDir`, `commit`, `covers`);
  `events.jsonl` gains no delivery kind, and `guardrails attach` does not replay deliveries in v1.
- **The wave-scoped interlock, and ride-along** — the #361 interlock is checked per delivery, against
  the waves that delivery carries (its `covers` list). A delivery is held when ANY wave it carries
  recorded a suppressing decision (a proceeded-best-guess or proceeded-unreviewed), so once a wave is
  held, every later delivery is held too until run end, unless the operator forces delivery with
  `--merge-on-success`. Use the phrase `wave-scoped interlock`, and state the ride-along rule in one
  sentence, in terms of the waves the delivery carries.
- **A refused delivery halts the run at that wave** — every refusal (`branch-moved`, `conflict`,
  `dirty-working-tree`, `hook-rejected`) halts with `WaveHaltKind.DeliveryRefused`, never a gate
  failure. A wave whose exit gate fails on the trial merge also settles its record as `refused`, with
  outcome `trial-gate-failed`, but which halt it raises is decided in review round 5
  (`d39-trial-gate-failure`). Its `detail` names each failing check, the user's tip the trial was built
  from, and the sha-keyed range `git log <plan-tip-sha>..<user-tip-sha>` of the user's commits the trial
  merged — a range, not a list. No `RunHaltKind` is added, and `run.json`'s `halt` section stays scoped to gates (#432): the
  durable record is the wave's `delivered` entry with `status: refused`, plus a `decisions[]` entry with
  gate `delivery-refused` (boundary `wave`, decision `halted`), so the refusal shows on the console and
  in `observer.jsonl`. The log site does not show that entry, and there is no log-site panel for a
  refused delivery in v1. The wave's marker commit and
  `completed` status are written only after its delivery settles, so a resume re-attempts a refused
  delivery at that wave's barrier. **Name both causes of `branch-moved`, each with its remedy:** the
  checkout was switched to another branch (check the branch out again, then resume), or the user's
  branch advanced after the trial was built, typically a commit made while the gate ran (resume: the
  next trial includes the new commits).
- **The partially-delivered outcome** — when some waves reached the user's branch and verified work is
  still held, `run.json`'s top-level `delivery` record reads `partially-delivered` with
  `delivered: false`, derived from `RunReport.WaveDeliveries`. `delivered` is true only when ALL
  verified work landed. The report naming the delivered and held waves prints BEFORE the verdict, and
  the exit code does not change.
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

**The interlock rule the first breakdown taught is SUPERSEDED, and the skill must not state it.** An
earlier version of this prompt said a machine decision holds back delivery for its OWN wave only. A
delivery carries every wave since the last delivery, so under that rule a held wave's commits ride a
later clean delivery onto the user's branch. Never describe the interlock as consulting only the
delivering wave. If you contrast the two, make it an explicit negation ("a decision does not hold back
only its own wave"). The guardrail refuses the narrow rule however it is worded.

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
