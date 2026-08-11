# Pilot-seat model provenance — surface which Claude model actually ran (issue #349)

> **Status: reviewed brief, ready for `/plan-breakdown`.** This is a dogfood plan: the maintainer
> drives `/plan-breakdown` → `/guardrails-review` → `guardrails run` (with `--no-merge-on-success`),
> reviewing each stage. Scope is the harness-capturable surfaces only; the `/plan-breakdown`-model
> annotation (surface 5) is a deferred fast-follow, out of this plan.

## Goal

Surface the **actually-resolved** Claude model (and `effort`, when set) for every prompt attempt in
the four places a human looks — the live run UI, the per-attempt log header, the end-of-run report,
and the durable journal provenance — by **capturing the model the CLI echoes in its own
`stream-json` output**, not by forcing an explicit `--model`. An operator can then always see "who's
in the pilot seat" (Haiku / Sonnet / Opus) and catch a silent model substitution.

## Why (the problem, today)

There is no legible record of which model executed a given attempt. `ResolveModelForDisplay`
(`src/Guardrails.Core/Execution/PromptExecutionSupport.cs:64`) records
`taskModelOverride ?? runnerModel ?? "(cli default)"` — the config's *guess*, never the model that
actually ran; and when neither is set (the common single-model case: no `--model` passed, CLI picks
its own default), provenance is the useless sentinel `"(cli default)"`. Meanwhile the CLI **already
tells us** the answer: Claude Code's `stream-json` emits a first line
`{"type":"system","subtype":"init","model":"claude-…",…}` (and stamps model on the terminal
`result` line), and the harness already tees the full stream to `claude-stream.jsonl` — but
`ClaudeStreamParser.Feed` (`src/Guardrails.Core/Prompts/ClaudeStreamParser.cs:77-81`) returns on
every line whose `type != "result"`, discarding the init model. That one discard is the whole gap.

This is foundational, not cosmetic: the model-tiering epic (#201, PR #342) §9.3 *assumes* per-attempt
model provenance exists; it does not. Landing this first gives that epic its groundwork (its §12.4
then adds only `runner`/`kind`/`tier` on top of this base).

## Mechanism — parse the echoed model, do NOT force `--model`

Forcing an explicit `--model` would (a) change the single-model / zero-setup user's behavior (they
pass nothing and let the CLI choose — injecting a guessed id pins them), and (b) record the model we
*asked for*, not the one that *ran* — the weaker fact. Parsing the CLI's own echo is free and is
ground truth. Capture two distinct facts:

- **`requestedModel`** — harness-planned, known at attempt launch (today's `ResolveModelForDisplay`).
  Drives the live UI *before* the stream returns.
- **`resolvedModel`** — CLI-observed actual, known once the init/result line parses. The durable
  provenance truth and the run-report value.
- **Mismatch** — when both are known and differ (alias-normalized, e.g. `sonnet` vs a full id),
  emit a loud log line + stamp it in provenance. **Observe-only, never gates**; records both strings
  rather than false-failing.

**Fallback ladder** (safe degradation, parser stays tolerant → null, never crashes): `system/init`
`model` → terminal `result` `model` → fall back to `requestedModel` *labelled as requested* → the
`"(cli default)"` sentinel survives only when nothing is knowable, and never masquerades as a real
model.

## Back-compat invariant (hard)

A single-model user gets a **strictly better record with zero new setup** — this is additive
observability, changing *nothing* about which model runs or what is spent. Old journals (no
`resolvedModel` field) must still read fine (absent-not-null).

## What to build (surfaces)

1. **Capture** — `ClaudeStreamParser` reads `model` from the `system`/`init` (and `result`) line →
   new `ClaudeResult.Model` → `PromptResult.ResolvedModel` (populated in `ClaudePromptRunner`). *The
   load-bearing change.*
2. **Persist** — extend `AttemptProvenance` (`src/Guardrails.Core/Journal/JournalModel.cs:~261`) with
   `resolvedModel` + `effort`; keep `Model` as best-known-actual (`resolved ?? requested ?? sentinel`)
   so existing readers improve automatically; populate in `AttemptJournaler`. SSOT §7 + sentinel note.
3. **Log header** — the per-attempt log preamble prints `model: <resolved>` (or `requested: X /
   resolved: Y` on mismatch).
4. **Live UI** — new `IRunObserver.AttemptModelResolved(task, attempt, model)` default-method event
   (fired when the init line parses; not a change to `AttemptStarting`'s signature); `LiveRunObserver`
   + the two decorator observers (`OnTheFlyLogSiteObserver`, `OnTheFlyDiagramObserver`) render it and
   flag mismatch.
5. **Run report** — a "models used" summary line (`src/Guardrails.Core/Execution/RunReport.cs` +
   the `RunCommand` summary renderer). The per-tier split arrives free once tiering #230 lands on top.

## Contract / SSOT

Provenance-contract change → lands in `docs/plans/02-schemas-and-contracts.md` in the **same change**:
§7 (journal: `resolvedModel`/`effort`; clarify `model` = actual-observed), §8 (log header), §9 (the
runner quarantine parses model from the stream). Update `guardrails-domain-knowledge` provenance
section. This owns the `resolvedModel`/`effort` provenance; the tiering DoR §12.4 is then trimmed to
"additive over #349's base."

## Codebase pointers (verified)

- `src/Guardrails.Core/Prompts/ClaudeStreamParser.cs` — capture `model` from `system`/`init` (+`result`).
- `src/Guardrails.Core/Prompts/PromptInvocation.cs` (`PromptResult`) — add `ResolvedModel`.
- `src/Guardrails.Core/Prompts/ClaudePromptRunner.cs` — populate it.
- `src/Guardrails.Core/Execution/PromptExecutionSupport.cs` / `TaskExecutor.ResolveModel` (~1314-1336) —
  stays the `requestedModel` source; rename its provenance role.
- `src/Guardrails.Core/Journal/JournalModel.cs` (`AttemptProvenance` ~261-286) — new fields; reconcile `Model`.
- `src/Guardrails.Core/Execution/AttemptJournaler.cs` (~74-84) — populate at settle.
- `src/Guardrails.Core/Execution/IRunObserver.cs` — new event; `src/Guardrails.Cli/Ui/LiveRunObserver.cs` + decorators — render.
- `src/Guardrails.Core/Execution/RunReport.cs` (~99) + `RunCommand` summary — the models-used line.

## Acceptance (deterministic-first — a natural showcase of certify-by-gate)

- A canned `stream-json` fixture with a known `init` model **parses to that exact model string**; a
  stream with no model line yields **null, not a crash**.
- An attempt's `run.json` provenance carries the **real `resolvedModel`** and **never the bare
  `"(cli default)"` sentinel when the stream reported a model**; a requested≠resolved case stamps
  mismatch.
- The per-attempt log header contains the resolved model string.
- An integration run's observer receives `AttemptModelResolved` with a real model; decorator
  forwarding is unit-tested.
- A two-attempt run's report lists the model(s) used.
- The existing golden plans (`hello-guardrails`, `parallel-hello`) run **byte-identically** for
  routing/spend; old journals without the new field still load.
