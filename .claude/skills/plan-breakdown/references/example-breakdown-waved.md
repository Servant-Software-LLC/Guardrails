# Worked example — a WAVED breakdown, end to end (#254)

The few-shot reference for the **waved** path (SKILL.md Step 9). Input: a small reviewed plan authored
as **two ordered stages** where Stage 2 builds on Stage 1's *materialized* output. Output: a nested
`<plan>/<wave-NN-slug>/…` folder — two mini-plans in strict order, each with its own entry/exit gates.

The runnable, `guardrails validate`-clean realization of this walkthrough is
**`examples/waved-hello/`** (source plan `waved-hello.md` → generated folder `waved-hello/`). Read the
files there alongside this doc; the paths below are that folder.

## Input plan (`waved-hello.md`)

> **Stage 1 (Wave 1) — Scaffold the greeting toolkit.** Produce `out/greet.ps1` (prints
> `Hello, <name>!` for `-Name`) and `out/config.json` (the recipient name, from seeded state). The two
> deliverables don't depend on each other.
>
> **Stage 2 (Wave 2) — Generate and check the greeting.** Builds on Stage 1's materialized output: run
> `out/greet.ps1` for the name in `out/config.json` → `out/greeting.txt`, then write `out/report.md`
> quoting it. Stage 2's tasks name paths and the `greet.ps1 -Name` signature that don't exist until
> Stage 1 runs.

## Step 0.8 — FLAT vs WAVED (the layout fork)

The plan is authored as **ordered stages**, and Stage 2 explicitly **builds on Stage 1's materialized
output** — every Stage-2 task references `out/greet.ps1` / `out/config.json`, which don't exist until
Stage 1 runs. That is the waved signal → `$waved = true`. (Contrast: if this were one stage — "write a
greeting toolkit and use it" with no materialization boundary — it would stay flat, and fine-grained
parallelism would be a task DAG inside ONE wave. Don't wave for parallelism.)

## Step 9 — the nested layout produced

```
waved-hello/
├── guardrails.json                       # ONE shared run config (maxParallelism 2, workspace "..")
├── state/seed.json                       # { "recipientName": "World" } — ONE continuous state
├── wave-01-scaffold/
│   ├── preflights/01-fresh-scaffold-start.ps1     # ENTRY gate (wave 1): negative fresh-start baseline
│   ├── guardrails/01-scaffold-union-clean.ps1     # EXIT gate: union-safe, scope:"integration" (GR2028)
│   │   └── 01-scaffold-union-clean.json           #   { "scope": "integration" }
│   └── tasks/
│       ├── 01-write-greet-script/  (writeScope out/greet.ps1)   → 01-greet-script-runs.ps1
│       └── 02-write-config/        (writeScope out/config.json) → 01-config-valid.ps1
└── wave-02-greet/
    ├── preflights/01-scaffold-materialized.ps1    # ENTRY gate: "prior wave materialized" (#181 @ boundary)
    ├── guardrails/01-greeting-complete.ps1         # EXIT gate: terminal postcondition, LOCAL (last wave)
    └── tasks/
        ├── 01-generate-greeting/ (writeScope out/greeting.txt) → 01-greeting-exists.ps1
        └── 02-write-report/      (dependsOn 01-generate-greeting; writeScope out/report.md)
                                                                 → 01-report-quotes-greeting.ps1
```

Wave dirs match `^wave-([0-9]+)-[a-z0-9-]+$`, numbered contiguously (`01`, `02`); the numeric prefix
drives the strict order. There is **no root `tasks/`** (a waved plan replaces it with wave subfolders).

## Wave 1 (`wave-01-scaffold`) — two independent leaves + its two gates

Run Steps 1–8 scoped to Stage 1. Two deliverables with no dependency on each other → **two parallel
leaves** (`dependsOn: []` each). Because the wave is **multi-leaf**, its EXIT gate carries a real
integration re-run (GR2028 **per wave**):

- **ENTRY gate** `preflights/01-fresh-scaffold-start.ps1` — wave 1's entry is the ordinary plan-start
  baseline. Here a NEGATIVE fresh-start check ("`out/greeting.txt` is not already present"), which IS the
  `tests-fail-on-current-code`/`tests-fail-on-stubs` family (not a new archetype), correct plan/wave-entry
  only (skip-once).
- **EXIT gate** `guardrails/01-scaffold-union-clean.ps1` (+ `.json` `{ "scope": "integration" }`) — a
  **union-safe CONDITIONAL** invariant: *if* `out/greet.ps1` / `out/config.json` is present, verify it is
  non-empty + conflict-marker-free; never REQUIRE both (a partial merge may not hold both yet). This is
  the GR2028 integration re-run for the wave — NOT a whole-build/suite terminal postcondition.
- **Tasks** `01-write-greet-script`, `02-write-config` — ordinary script tasks, each with a `writeScope`
  narrowed to its one output and a per-task guardrail (`01-greet-script-runs` actually runs the script;
  `01-config-valid` parses the JSON + asserts a non-empty name).

## Wave 2 (`wave-02-greet`) — the materialization boundary + an intra-wave edge

Stage 2 begins only after Stage 1's outputs are on the branch (the wave barrier). Its ENTRY gate is the
load-bearing new artifact:

- **ENTRY gate** `preflights/01-scaffold-materialized.ps1` — the **#181 positive-baseline archetype at
  the wave boundary**: assert `out/greet.ps1` and `out/config.json` are **present and non-empty** before
  this wave's DAG spends a turn. Positive-monotone-safe (assert-**present**). This IS the realization of
  *terminal-gate-of-wave-1 == preflight-of-wave-2*: wave 1's exit gate certified the merged HEAD, wave 2's
  entry gate verifies that HEAD carries what it depends on.
- **Tasks** `01-generate-greeting` (runs `out/greet.ps1` for the configured name → `out/greeting.txt`)
  then `02-write-report` (`dependsOn: ["01-generate-greeting"]` — an **intra-wave** edge, a plain sibling
  folder name). There is **no cross-wave edge** to wave 1 (that would be GR2034); the wave barrier orders
  the stages, and wave 2's actions read wave-1's real files directly.
- **EXIT gate** `guardrails/01-greeting-complete.ps1` — wave 2 is the LAST wave, so its exit gate runs on
  the fully-merged HEAD and IS the whole-plan terminal soundness boundary (a plan-root `<plan>/guardrails/`
  would be optional-additive). Wave 2 is a single linear chain (one leaf), so this terminal postcondition
  is **LOCAL** (no `scope` key) — marking it `scope:"integration"` would false-RED at an intermediate
  union (#125/#165).

## Wave-qualified identity + the state key (illustrative)

`waved-hello` is script-only, so it publishes nothing to state. But if `wave-01-scaffold/02-write-config`
were a **prompt** action publishing the chosen name for a later task, its fragment MUST be keyed by the
**wave-qualified id**, not the bare folder name:

```json
{ "wave-01-scaffold/02-write-config": { "chosenName": "World" } }
```

A bare `02-write-config` key is rejected as foreign on every attempt (the #164 loop, one level up). The
harness-contract header, the `## Task` example, and the state-output guardrail's index
(`$fragment.'wave-01-scaffold/02-write-config'.chosenName`) all use that same wave-qualified id. A wave-2
task reads it as `$state['wave-01-scaffold/02-write-config']` — satisfied by the barrier (GR2022's
wave-aware branch), no `dependsOn` edge (none is possible cross-wave).

## The JIT staged variant — the one-ahead stub across a 3-wave plan (#365)

`waved-hello` is broken down whole-plan up front because Stage 2's paths are named and stable. When a
downstream wave references artifacts you can only know *after* the upstream runs (a generated type, a
signature you'd otherwise guess), use the JIT staged flow instead — and the point below is the invariant a
2-wave plan can't show: **exactly one un-authored stub wave stays visible ahead at every step until the
last wave.**

To exercise it, take a **3-stage** version — the two stages above plus **Stage 3 (Wave 3) — Publish**: build
`out/published.md` from Stage 2's materialized `out/report.md` (a path that only exists once Stage 2 has run).
All three stages reference not-yet-existing upstream artifacts, so each is authored JIT, against the real
materialized workspace. The plan folder **grows one wave at a time** (`wave-01…` → `+wave-02…` → `+wave-03…`),
numbering staying contiguous (GR2033), the diagram always showing **one** stub node ahead:

1. **Initial breakdown.** Author + `/guardrails-review` **wave 1** only. Leave **just `wave-02-greet/`** as a
   stub (declared dir, **empty `tasks/`**) — one wave visible ahead. **Do not** also stub `wave-03-publish/`
   (that is the #365 mistake in the other direction — see the negative example). `guardrails graph` shows
   `wave-01` + a `wave-02` stub node.
2. **Run → halt at wave 2.** `guardrails run waved-hello3` runs wave 1, then **honest-halts** at the empty
   wave 2 (exit 2, `RunReport.WaveHalt` `NextWaveUnauthored`) and prints the **integration worktree** path
   (`<worktreeRoot>/<runId>/_integration`) — the materialized upstream (the user's checkout stays read-only).
3. **Author wave 2 — and re-stub wave 3.** Re-invoke `/plan-breakdown` in JIT mode: author
   `wave-02-greet/tasks/` **reading that integration worktree** — inspect the REAL `out/greet.ps1` signature
   and `out/config.json` shape wave 1 produced, so wave 2's tasks/guardrails reference bytes that actually
   exist (no stale line-numbers, no hedged architecture claims — #203). **Then, because Stage 3 remains,
   create the next stub `wave-03-publish/` (dir + empty `tasks/`, no `brief.md`)** and **regenerate the
   diagram** — restoring the one-ahead invariant (#365). `guardrails graph` now shows `wave-01`, `wave-02`,
   and a fresh `wave-03` stub node. The re-stub carries **no `brief.md`**: that file is the human's OPTIONAL
   opt-in for future auto-breakdown (SSOT §14.10; its presence alone is the signal), so the skill never seeds
   it — the report just notes the human may add one.
4. **Review wave 2** → `/guardrails-review waved-hello3/wave-02-greet` (single-wave review) →
   `guardrails mark-reviewed waved-hello3/wave-02-greet`. (You review the authored wave 2, not the fresh empty
   `wave-03` stub — an empty wave has nothing to attack.)
5. **Run → resume → halt at wave 3.** `guardrails run waved-hello3` again — cross-wave resume skips wave 1,
   drains wave 2, then **honest-halts at the `wave-03-publish` stub** step 3 created. Without that re-stub the
   run would instead have completed at wave 2's terminal gate as if the plan were done (the #365 bug).
6. **Author wave 3 — the FINAL wave, no re-stub.** JIT-author `wave-03-publish/tasks/` against the
   now-materialized `out/report.md`. Stage 3 is the last planned stage, so **create no stub after it** and
   regenerate the diagram (three authored waves, zero stubs). `/guardrails-review` it, `mark-reviewed`.
7. **Run → resume → complete.** `guardrails run waved-hello3` drains wave 3 and reaches the **terminal gate**
   (wave 3's exit gate on the fully-merged HEAD) — the plan is done. The honest-halt loop ended exactly once,
   at the final wave, with no leftover stub.

## Step 7 — the closing report (what the skill says to the user)

> Breakdown of `waved-hello.md` → `waved-hello/` — **WAVED** (2 stages, Stage 2 builds on Stage 1's
> materialized `out/greet.ps1` + `out/config.json`), **2 waves, 4 tasks**:
>
> | Wave | Entry gate | Tasks (guardrails) | Exit gate |
> |---|---|---|---|
> | wave-01-scaffold | 01-fresh-scaffold-start (negative fresh-start baseline) | 01-write-greet-script (greet-script-runs), 02-write-config (config-valid) — two parallel leaves | 01-scaffold-union-clean (union-safe, `scope:"integration"`, GR2028) |
> | wave-02-greet | 01-scaffold-materialized (**prior wave materialized**, #181 @ boundary) | 01-generate-greeting (greeting-exists) → 02-write-report (report-quotes-greeting) | 01-greeting-complete (terminal postcondition, **LOCAL** — last wave = whole-plan boundary) |
>
> - `dependsOn` is intra-wave only (`02-write-report` → `01-generate-greeting`); the wave barrier orders
>   Stage 1 before Stage 2 — no cross-wave edge (GR2034).
> - Both waves authored up front (Stage 2's paths are designable up front). Had Stage 2 referenced
>   not-yet-existing artifacts, it would be a JIT stub (run → honest-halt at the empty wave → author
>   against the integration worktree → review that wave → resume).
> - Author-time smoke-test (#302): every script guardrail — task-level AND both waves' entry/exit gates —
>   was executed against a valid + an invalid sample (the wave-2 entry gate against a materialized sample
>   and a missing-`out/greet.ps1` sample).
> - `guardrails validate waved-hello` → OK.
>
> **This is a draft.** Review the folder — especially the wave gates — edit, delete, or add, then run
> `/guardrails-review waved-hello` (wave by wave) before executing with `guardrails run waved-hello`.

---

# Negative examples — waved-plan-specific mistakes

- **Waving a flat plan.** A single-stage feature ("add a `--stats` flag") emitted as `wave-01-…` /
  `wave-02-…` for "parallelism" — waves are the COARSE stage ordering, and the barrier *destroys*
  cross-wave parallelism. Fine-grained parallelism is a task DAG inside ONE wave. (See the flat worked
  example `example-breakdown.md` for the right shape.)
- **A cross-wave `dependsOn` edge.** `wave-02-greet/01-generate-greeting` declaring `dependsOn:
  ["wave-01-scaffold/01-write-greet-script"]` (or even a bare `01-write-greet-script` intending wave 1)
  → **GR2034 hard error**. The dependency belongs in wave 2's ENTRY gate ("prior wave materialized"), not
  a task edge.
- **A whole-suite `tests-pass` marked `scope:"integration"` in an intermediate wave's exit gate.** It
  re-runs at every union and red-halts a correct partial merge (#125). Keep whole-build/suite LOCAL;
  make the wave's integration guardrail a union-safe conditional invariant. Only the LAST wave's exit
  gate is the whole-suite home.
- **A negative-polarity wave-2+ entry gate.** Asserting an artifact is "not yet present" at a wave-2
  entry gate flips false the instant an unrelated file lands — a false-RED. Wave-entry-materialized
  checks are positive (assert-present); the negative fresh-start check belongs at wave 1 only.
- **Breaking down a downstream wave up front with GUESSED paths** when JIT was available — produces the
  stale line-number / unhedged architecture-claim failure (#203). Prefer the JIT flow: author the wave
  against the materialized integration worktree.
- **Dropping the forward stub after a JIT step (#365).** JIT-authoring `wave-K+1` but NOT re-creating the
  `wave-(K+2)` stub while planned stages remain — the folder ends up with only the authored waves and no
  empty `tasks/` ahead. The wave-aware diagram then shows no future-wave node and `guardrails run` drains to
  the terminal gate **as if the plan were complete**, silently ending the JIT loop one wave early. The fix
  (§9.5 step 3) is to re-stub `wave-(K+2)` and regenerate the diagram in the SAME step that authors
  `wave-K+1`; only the FINAL wave gets no stub after it.
- **Stubbing every remaining wave (`K+1..N`) up front.** The over-correction: emitting empty
  `wave-03…`/`wave-04…`/… dirs at the initial breakdown. It clutters the diagram with N stub nodes and, worse,
  the deep stubs are authored from GUESSED intent rather than the materialized upstream — the exact #203
  guesswork the JIT flow exists to avoid. Keep **one** stub ahead and re-create it one at a time (#365).
