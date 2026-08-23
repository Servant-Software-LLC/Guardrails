# Wave 2 — the pilot seat, part 1: capture and persist (#349)

> **This wave is a JIT stub.** Its tasks are authored at the wave-2 checkpoint, against the
> **materialized integration worktree** — not now, and not from this brief alone. Seeded from
> `model-tiering-stage-3.charter.md` §B, surfaces 1–2 of 5.

## Budget — read this before you decompose

**Author AT MOST 5 task folders.** The wave-2 breakdown that preceded this one declared 12 and was
killed by the 30-minute wall clock at folder 6, having used 173 turns of the 800 it was granted (#504:
the turn budget scales with brief size, the wall clock does not). Measured on that run: **~17 min of
orientation before the manifest was declared, then ~2.6 min per task folder.** Five folders is the
ceiling that fits, and this brief deliberately narrows the orientation to buy back some of it.

If your decomposition genuinely needs more than 5, **the wave is mis-scoped — say so in your report**.
Author the prefix in strict dependency order (#501 keeps a valid prefix, so a truncation costs a resume
rather than the wave) and do **not** compress two separately-verifiable deliverables into one folder to
fit the count. A folder that bundles "implement it and document it" is over-sized however well it fits.

## What this wave must accomplish

`ResolveModelForDisplay` records the model the harness **asked for**. The Claude CLI already tells us
what actually **ran** — its `stream-json` opens with
`{"type":"system","subtype":"init","model":"claude-…"}` — and the harness already tees that stream to
`claude-stream.jsonl`. `ClaudeStreamParser.Feed` returns on every line whose `type != "result"`,
throwing the init model away. **That one discard is the entire gap.**

Parse the echo; never force `--model`. Forcing one would pin the zero-setup user who deliberately
passes nothing, and would record the model we *requested* — the weaker fact.

Two surfaces, in dependency order:

1. **Capture** — `ClaudeStreamParser` reads `model` from `system`/`init`, falling back to the terminal
   `result` line; surface it on `PromptResult`; populate it in `ClaudePromptRunner`.
   *The load-bearing change; every later wave is downstream of it.*
2. **Persist** — the provenance record (settled contract below), populated in `AttemptJournaler`.

The wave's **last** task owns the SSOT provenance delta and the DoR §9.3 amendment — it is the only
task in this wave permitted to touch `docs/plans/02-schemas-and-contracts.md` or
`.claude/skills/guardrails-domain-knowledge/SKILL.md`. Two tasks sharing either file is the union
hazard that costs a run (charter §"Waves, and the union hazard that decides them").

## The settled contract — do NOT re-litigate this at the checkpoint

The charter review resolved `s3-provenance-shape`, and it amends DoR §9.3:

- **`provenance.model` becomes best-known-actual** — observed ?? route ?? sentinel. Existing readers
  improve with no change on their side.
- **`requestedModel` is written ONLY when it differs.** Present *is* the mismatch signal.
- **There is NO `resolvedModel` key.** DoR §9.3 asked for one; Stage 2 refused it in the shipped
  contract at `JournalModel.cs:401` (*"two fields claiming the same fact is how they drift"*). The
  settled shape honours both: one field per fact, and a second field only for the disagreement.

`AttemptProvenance.Effort` already shipped with Stage 2 — that half of §9.3 is done. Do not re-add it.

## Orientation — scoped to this wave, and the one trap that has already bitten

- Read the **materialized** `AttemptProvenance` in the integration worktree. Stage 2 restructured it
  (`Runner`/`Kind`/`Tier`/`TierSource`/`Effort`/`Judge`), and wave 1 has landed since this brief was
  written. Do not trust this brief's description of any signature — verify it.
- **Enumerate every construction site of the sink type before writing a `writeScope` (#474).** The
  known trap in this exact area: `AttemptJournaler` does not build `AttemptRecord` from a
  `PromptResult`, it builds it from an `ActionRun` declared in `ActionRunner.cs`. Trace the sibling
  datum that already makes the whole trip (`CostUsd`) end to end, and put every file on its path in
  scope. A scope traced on type names passed `validate`, `graph --check` and a full review, then
  dead-ended the agent at `needsHuman`.
- **Both record paths must carry it** — the serial journaller and the worktree settle path. A datum
  that reaches only one of them is the silent half-failure this stage exists to catch.

## Out of scope for this wave — these are later waves, already scoped

- The attempt-log preamble, the `IRunObserver` event and the two decorators → **wave 3**.
- The models-used run-report line, and deleting the superseded
  `docs/plans/pilot-seat-model-provenance/` folder → **wave 4**.
- The `/guardrails-review` model-appropriateness net (#229) → **wave 5**.
- Recording which model **authored** a breakdown is **#495**, sequenced after #349 — not in this stage.
