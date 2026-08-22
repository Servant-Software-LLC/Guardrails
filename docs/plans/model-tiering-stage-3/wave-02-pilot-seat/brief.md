# Wave 2 — the pilot seat (#349): the model that ACTUALLY ran

> **This wave is a JIT stub.** Its tasks are authored at the wave-2 checkpoint, against the
> **materialized integration worktree** — not now, and not from this brief alone. The whole point is
> that wave 1's real bytes exist by then. Seeded from `model-tiering-stage-3.charter.md` §B.

## What this wave must accomplish

`ResolveModelForDisplay` records the model the harness **asked for**. The Claude CLI already tells us
what actually **ran** — its `stream-json` opens with
`{"type":"system","subtype":"init","model":"claude-…"}` — and the harness already tees that stream to
`claude-stream.jsonl`. `ClaudeStreamParser.Feed` returns on every line whose `type != "result"`,
throwing the init model away. **That one discard is the entire gap.**

Parse the echo; never force `--model`. Forcing one would pin the zero-setup user who deliberately
passes nothing, and would record the model we *requested* — the weaker fact.

## The five surfaces, in dependency order

1. **Capture** — `ClaudeStreamParser` reads `model` from `system`/`init`, falling back to the terminal
   `result` line; surface it on `PromptResult`; populate it in `ClaudePromptRunner`.
   *The load-bearing change; everything else is downstream of it.*
2. **Persist** — the provenance record (see the settled contract below), populated in `AttemptJournaler`.
3. **Log header** — the per-attempt preamble prints the resolved model, and both strings on mismatch.
4. **Live UI** — a new default-method `IRunObserver` event, rendered by `LiveRunObserver` and
   forwarded by **both** decorators (`OnTheFlyLogSiteObserver`, `OnTheFlyDiagramObserver`). Assert the
   forwarding on the decorators themselves, not only on `LiveRunObserver`.
5. **Run report** — a models-used summary line.

## The settled contract — do NOT re-litigate this at the checkpoint

The charter review resolved `s3-provenance-shape`, and it amends DoR §9.3:

- **`provenance.model` becomes best-known-actual** — observed ?? route ?? sentinel. Existing readers
  improve with no change on their side.
- **`requestedModel` is written ONLY when it differs.** Present *is* the mismatch signal.
- **There is NO `resolvedModel` key.** DoR §9.3 asked for one; Stage 2 refused it in the shipped
  contract at `JournalModel.cs:401` (*"two fields claiming the same fact is how they drift"*). The
  settled shape honours both: one field per fact, and a second field only for the disagreement.

`AttemptProvenance.Effort` already shipped with Stage 2 — that half of §9.3 is done. Do not re-add it.

## What the authoring agent must check first, at the checkpoint

- Read the **materialized** `AttemptProvenance` in the integration worktree. Stage 2 restructured it
  (`Runner`/`Kind`/`Tier`/`TierSource`/`Effort`/`Judge`), and wave 1 has landed since this brief was
  written. Do not trust this brief's description of any signature — verify it.
- **Enumerate every construction site of the sink type before writing a `writeScope` (#474).** The
  known trap in this exact area: `AttemptJournaler` does not build `AttemptRecord` from a
  `PromptResult`, it builds it from an `ActionRun` declared in `ActionRunner.cs`. Trace the sibling
  datum that already makes the whole trip (`CostUsd`) end to end, and put every file on its path in
  scope. A scope traced on type names passed `validate`, `graph --check` and a full review, then
  dead-ended the agent at `needsHuman`.
- `docs/plans/pilot-seat-model-provenance/` is a **superseded** 12-task folder from 2026-08-11 that
  was never run and targets the pre-Stage-2 contract. Wave 1's charter deletes it. Borrow its
  decomposition if useful; do not execute it.

## Out of scope for this wave

Recording which model **authored** a breakdown is **#495**, sequenced after #349 — not here.
