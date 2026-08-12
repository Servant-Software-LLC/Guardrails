# Model tiering — Stage 1: Foundation (provider registry + difficulty tagging)

> **Design of record: [`17-model-tiering.md`](17-model-tiering.md)** — the contract-locked
> decisions (registry shape, tier enum, GR-code block, SSOT deltas) live there; where this
> brief and the DoR differ, the DoR wins. "Stage" here means a sequential design phase of
> this epic — NOT a #254 runtime wave (SSOT §14).
> **DoR revision 3 adds three items to this stage** (from the reviewed verifier charter,
> `model-tiering-verifier.charter.md`): the **three model axes** `costly`/`strength`/
> `specialization` on each registry block (DoR §4.1); the **`guardrails providers init`**
> registry generator (DoR §4.3); and `tiering.verifier.defaultTier` (DoR §6.5). It also
> **retires `routing.rank`** in favour of ascending-`strength` ordering (DoR §4.2) and
> **reallocates the GR block to GR2043–GR2053** (v1 takes GR2043–GR2052; GR2053 is v2) — the
> original GR2037–GR2045 reservation was taken by shipped work while this design sat in draft
> (DoR §13). **Re-verify before landing; the file is the registry, the design is not.**

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
6. **The three model axes (DoR §4.1, charter Decision 7):** `costly` (bool), `strength` (integer
   ≥ 1, higher = stronger), `specialization` (`coding|planning-reasoning|general|unspecified`) —
   **top-level on the block**, all optional, malformed = GR2049. Not consumed in this stage; the
   resolver (stage 2) is the first reader. **`routing.rank` is NOT implemented** — ordering comes
   from ascending `strength` (DoR §4.2/D25).
7. **`guardrails providers init` (DoR §4.3, charter Decision 8):** enumerate each configured
   provider's models where the `kind` has an enumeration surface, and write/merge the blocks into
   `guardrails.json` **with the legal values for each axis as `//` comments**. Idempotent (never
   overwrites a human annotation, never reorders, never deletes); **never fabricates a model list**
   — a `kind` with no enumeration surface gets its existing blocks annotated plus an explicit
   "could not enumerate" comment; output presented as a diff to accept. `guardrails.json` already
   parses comments (`PlanJson.Options` → `JsonCommentHandling.Skip`), so this is the same file, not
   a sibling `.jsonc`.

### #225 — plan-breakdown difficulty-tier tagging
1. Add `action.tier` to `RawAction` and the resolved task-action model, mirroring how
   `action.model`/`action.maxTurns` already exist (same file, same pattern).
2. `/plan-breakdown` classifies each prompt-driven task (and any surviving judge-guardrail) into a
   tier — `easy | medium | hard` — and writes it to `task.json`. Surface the classification in the
   breakdown report, never silent (the #42 test-framework-choice precedent). **Gated on tiering
   being configured (DoR §5, D19):** the skill produces `guardrails.json`, so it knows whether any
   `routing` block exists. If tiering is **not** configured (no `routing` block — the single-model
   default), the skill writes **NO `action.tier` fields, NO `tiering` block, and NO classification
   report lines**, and GR2041 cannot fire — a single-model user's breakdown is **byte-identical to
   today** (DoR Invariant 7).
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
  the configured plan-wide default *if one is set* (else legacy resolution); an unrecognized tier
  value fails validation.
- **With tiering configured:** `/plan-breakdown` assigns and reports a tier per generated task on a
  real plan.
- **Gated tagging (DoR Invariant 7 / D19):** breaking down a plan against a **no-`routing`** config
  produces a folder **byte-identical to today** — no `action.tier`, no `tiering` block, no
  classification report lines, GR2041 does not fire.
- SSOT §9 and §3 (including the canonical-schema sentinel) are updated in the same change as their
  respective code changes — not left to drift.
- A block carrying `costly`/`strength`/`specialization` validates and round-trips; each malformed
  form (non-bool `costly`, `strength: 0`, an out-of-enum `specialization`) fails with **GR2049**.
- **`guardrails providers init` is idempotent under re-run:** running it twice against a config a
  human has annotated leaves that annotation **byte-identical**. (This is the acceptance that
  matters most — a generator that clobbers the annotation it exists to solicit is worse than none.)
- **`providers init` never invents a model:** against a `kind` with no enumeration surface it exits
  0, annotates the existing blocks, and emits the "could not enumerate" comment — it does not fail,
  and it does not write a model identifier the provider did not report.
- **Re-verify GR2043–GR2052 against `DiagnosticCodes.cs` before landing** and renumber if the block
  has been taken again; the file is the registry, this design is not (DoR §13).

## Stack

.NET 8 / xUnit v3 for `Guardrails.Core` (registry + schema work, `guardrails validate`).
`.claude/skills/plan-breakdown/SKILL.md` for the tagging doctrine (a `guardrails-skill-author`
task). Verification: `dotnet test tests/Guardrails.Core.Tests` (schema/registry unit tests) +
the plan-breakdown golden round-trip meta-test.

## Related
#201 (epic), #224, #225, #200 (shipped `action.model`, the pattern this mirrors), #223 (concrete
non-Claude runners — separate, standalone), stage 2 (`model-tiering-consumers.md`) and stage 3
(`model-tiering-dynamic-behavior.md`), both of which depend on this stage landing first.
