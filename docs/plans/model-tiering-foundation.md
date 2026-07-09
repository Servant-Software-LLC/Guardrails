# Model tiering — Stage 1: Foundation (provider registry + difficulty tagging)

> **Design of record: [`13-model-tiering.md`](13-model-tiering.md)** — the contract-locked
> decisions (registry shape, tier enum, GR-code block, SSOT deltas) live there; where this
> brief and the DoR differ, the DoR wins. "Stage" here means a sequential design phase of
> this epic — NOT a #254 runtime wave (SSOT §14).

Part of the model-tiering epic (#201). This is stage 1 of 3 sequential plans (foundation →
consumers → dynamic behavior); stages 2 and 3 depend on this one landing first. Covers issues
**#224** (provider registry + config schema) and **#225** (plan-breakdown difficulty-tier
tagging) — these two don't depend on each other, so their tasks may run in parallel.

## Context

Exactly one prompt-runner CLASS exists today: `ClaudePromptRunner`
(`src/Guardrails.Core/Prompts/ClaudePromptRunner.cs`), selected per config block by
`PromptRunnerRegistry.FromConfig` (`src/Guardrails.Core/Prompts/PromptRunnerRegistry.cs`). That
file's own doc comment already names the extension seam this stage uses:

> "v1 ships a single runner CLASS (`ClaudePromptRunner`); each config block becomes one instance
> carrying that block's `command`. A future CLI is a new class keyed by a discriminator — the seam
> is here, not in the harness."

The raw config shapes already carry a `Model` field but no vendor/kind discriminator and no
per-model guidance:
- `RawPromptRunner` / `RawPromptRunnerOverrides` (`src/Guardrails.Core/Loading/RawManifests.cs:31-59`)
  — the `promptRunners.<name>` config block.
- `RawAction` (`src/Guardrails.Core/Loading/RawManifests.cs:95-105`) — `task.json`'s `action` block;
  already has `Model` (shipped, #200) alongside `Runner`/`MaxTurns`, but no difficulty-tier field.

The SSOT (`docs/plans/02-schemas-and-contracts.md`) §9 "Prompt runners" (line ~1332) documents the
promptRunners schema and carries a **drift-tested `canonical-schema:promptRunners` sentinel block**
(lines ~109-133) — any schema change here must update that block in the same change, or the drift
test fails. §3 documents `task.json`'s schema (where the new tier field joins `action.model`).

## The ask

### #224 — Provider registry + config schema
1. Add a `kind` (or similarly named) discriminator to `RawPromptRunner`/`PromptRunnerConfig` —
   e.g. `"claude" | "codex" | "openrouter" | "local"` — defaulting to `"claude"` for backward
   compatibility with every existing plan's `promptRunners` block.
2. Extend `PromptRunnerRegistry.FromConfig`'s factory to switch on `kind` and construct the
   matching `IPromptRunner`. Only `ClaudePromptRunner` needs a real implementation in this stage —
   concrete Codex/OpenRouter/local runners are #223 (a separate, standalone issue). For an
   unimplemented kind, fail registry construction with an honest, actionable message (not a silent
   fallback to Claude) — this is the seam #223 later fills in.
3. Add a per-model **routing-guidance** field to the runner config schema — prose and/or a
   tag/enum set describing what kinds of tasks that model should take on. Not consumed by anything
   yet in this stage (stage 2's resolution step, #226, is the first consumer) — this stage only needs
   the field to exist, validate, and round-trip.
4. `guardrails validate` rejects an unrecognized `kind` and a malformed guidance value.
5. Update SSOT §9 (prose + the canonical-schema sentinel block) in the same change.

### #225 — plan-breakdown difficulty-tier tagging
1. Add `action.tier` to `RawAction` and the resolved task-action model, mirroring how
   `action.model`/`action.maxTurns` already exist (same file, same pattern).
2. `/plan-breakdown` classifies each prompt-driven task (and any surviving judge-guardrail) into a
   tier — `easy | medium | hard` — and writes it to `task.json`. Surface the classification in the
   breakdown report, never silent (the #42 test-framework-choice precedent).
3. A plan-wide default tier (config-level, e.g. `guardrails.json`) applies to any task left
   untagged — including one a human hand-adds to the folder after breakdown.
4. `guardrails validate` rejects an unrecognized tier value.
5. Update SSOT §3 (task.json schema) and the plan-breakdown skill's quality-bar checklist (mirror
   how #94's maxTurns-by-archetype bump is documented there) in the same change.

## Acceptance

- Every existing plan's `promptRunners` config (no `kind` specified) continues to validate and run
  unchanged — this stage is additive, not breaking.
- A runner config with an unrecognized `kind` fails `guardrails validate` with an actionable
  message naming the bad value.
- A `task.json` with `action.tier: "easy"|"medium"|"hard"` validates; an absent tier resolves to
  the configured plan-wide default; an unrecognized tier value fails validation.
- `/plan-breakdown` assigns and reports a tier per generated task on a real plan.
- SSOT §9 and §3 (including the canonical-schema sentinel) are updated in the same change as their
  respective code changes — not left to drift.

## Stack

.NET 8 / xUnit v3 for `Guardrails.Core` (registry + schema work, `guardrails validate`).
`.claude/skills/plan-breakdown/SKILL.md` for the tagging doctrine (a `guardrails-skill-author`
task). Verification: `dotnet test tests/Guardrails.Core.Tests` (schema/registry unit tests) +
the plan-breakdown golden round-trip meta-test.

## Related
#201 (epic), #224, #225, #200 (shipped `action.model`, the pattern this mirrors), #223 (concrete
non-Claude runners — separate, standalone), stage 2 (`model-tiering-consumers.md`) and stage 3
(`model-tiering-dynamic-behavior.md`), both of which depend on this stage landing first.
