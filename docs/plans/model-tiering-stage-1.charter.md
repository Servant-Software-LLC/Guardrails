---
charter-format-version: 1
---
# Model tiering — Stage 1: the registry, the axes, and gated tagging (#201)

Stage 1 of the model-tiering epic. It builds the **substrate** and nothing that reads it: a provider
registry that can describe more than one model, three per-model axes, and a difficulty tag on each task.
Nothing routes yet — the resolver is Stage 2. This stage is done when a config can *say* what it has and
`guardrails validate` holds it to that.

**Gate:** the design of record (#342) must be reviewed and merged first — that is Stage 1's stated
dependency in DoR §10, not a formality. Everything below is downstream of decisions settled there.

:::note
**Four workstreams, no ordering between them.** #224 (registry + axes), `guardrails providers init`,
#225 (gated tagging) and the pre-run availability check touch different files and can land in any order. That makes this a genuinely parallel
task DAG rather than a chain — which is most of why it is a good candidate to drive through Guardrails
itself.
:::

:::warn
**Self-hosting hazard — this stage edits the files the harness verifies itself with.** #224 adds
diagnostic codes to `DiagnosticCodes.cs` (and moves its next-free sentinel); #225 edits a `.claude/` skill.
Both are surfaces where a Guardrails run against this repo has bitten before. The three specific traps and
their mitigations are in *Dogfooding this stage* below — they are not hypothetical, they cost a full halt
and two retries in the #374 arc.

**Dogfood with the RELEASED `guardrails` only — never a debug build, never under the debugger.** This stage
edits the harness itself, so a locally-built or debugger-hosted `guardrails` would be verifying the plan
with the very code the plan is changing: a run could pass because of an uncommitted edit, or fail on one,
and neither result would mean anything. Fix the harness under test to a published version and the run
becomes evidence again. The released binary is also the only configuration a user will ever have, which is
the thing the dogfood is supposed to be measuring.
:::

## What already exists

- **#200** shipped `action.model` — the per-task override this mirrors, same file, same pattern.
- `PromptRunnerRegistry.FromConfig` builds runners from `promptRunners`; every existing config is
  single-kind and implicitly Claude.
- `guardrails.json` **already parses comments** (`PlanJson.Options` → `JsonCommentHandling.Skip`), which is
  why the generated registry annotates that file directly rather than a sibling `.jsonc`.

## The work

### A · #224 — provider registry, the three axes, validation

1. A `kind` discriminator on `RawPromptRunner`/`PromptRunnerConfig` (`claude|codex|openrouter|local`),
   **defaulting to `claude`** so every existing plan validates unchanged.
2. `PromptRunnerRegistry.FromConfig` switches on `kind`. Only `ClaudePromptRunner` is real in this stage —
   concrete non-Claude runners are #223. An unimplemented kind **fails registry construction with an
   actionable message**, never a silent fallback to Claude.

   **Registry construction is the BACKSTOP, not the gate.** Failing there means the harness is already
   running: a wave is in flight, work may be committed, and the reviewer learns about a config problem as a
   halt rather than as a review finding. Anything knowable from the plan plus the config should fail
   **before the run starts** — see *Failing early* below.
3. Per-model **routing guidance** on the block — exists, validates, round-trips. First consumer is Stage 2.
4. **The three axes, top-level on the block** (DoR §4.1 / charter Decision 7): `costly` (bool),
   `strength` (int ≥ 1, higher = stronger), `specialization`
   (`coding|planning-reasoning|general|unspecified`). All optional; malformed = a validation error.
5. **`routing.rank` is NOT implemented** (settled OD-F). Ordering is ascending `strength` — *the weakest
   model that can serve the tier goes first*. A config still carrying `rank` raises a **retired-field
   warning**, so a migrated config's ordering can never change silently.
6. SSOT §9 — prose **and** the canonical-schema sentinel — updated in the same change.

### B · `guardrails providers init` — the generated registry

Enumerate each configured provider's models where the kind has an enumeration surface, and write/merge
blocks into `guardrails.json` **with the legal values for each axis as `//` comments**.

- **Idempotent**: re-running against a human-annotated config leaves that annotation byte-identical. Never
  reorders, never deletes.
- **Never fabricates a model id** (settled OD-E). A kind with no enumeration surface gets its existing
  blocks annotated plus an explicit *"could not enumerate"* comment, and exits 0.
- Output presented as a diff to accept.

:::note
**A model id is a routing target, not documentation.** That is what makes "never fabricate" a hard rule
rather than a nicety: a stale or invented id would be *spent against* at a model that may not exist, or
silently substituted by a provider that resolves unknown names loosely.
:::

### C · #225 — gated difficulty tagging

1. `action.tier` on `RawAction` and the resolved model, mirroring `action.model`/`action.maxTurns`.
2. `/plan-breakdown` classifies each prompt task (and surviving judge guardrails) `easy|medium|hard`, and
   **reports the classification** — never silent.
3. A plan-wide default tier covers anything left untagged, including a task hand-added after breakdown.
4. `guardrails validate` rejects an unrecognized tier.
5. SSOT §3 and the plan-breakdown skill's quality bar updated in the same change.

:::warn
**The gate is the load-bearing part of #225.** When tiering is **not** configured (no `routing` block —
the single-model default), the skill writes **no `action.tier`, no `tiering` block, and no classification
report lines**. A single-model user's breakdown must be **byte-identical to today** (DoR Invariant 7). This
is the acceptance criterion most likely to be asserted and least likely to be genuinely tested — see the
open question on how we prove it.
:::

:::diagram
flowchart TB
  G["#342 DoR reviewed + merged<br/>(Stage 1's stated dependency)"] --> A
  G --> B
  G --> C
  A["A · #224 registry<br/>kind · routing guidance · 3 axes<br/>retired-rank warning · SSOT §9"]
  B["B · providers init<br/>annotate + enumerate<br/>idempotent · never fabricates"]
  C["C · #225 gated tagging<br/>action.tier · classify + report<br/>SSOT §3 · skill doctrine"]
  G --> D
  D["D · pre-run availability check<br/>/guardrails-review names unservable models<br/>static now · JIT judge deferred to #223"]
  A --> Z{"Stage 1 done:<br/>a config can SAY what it has,<br/>and validate holds it to that"}
  B --> Z
  C --> Z
  D --> Z
  Z -.->|"first reader"| E["Stage 2 — the static resolver (#226)"]
  D -.->|"JIT judge half"| F["#223 — concrete non-Claude runners"]
:::

### D · pre-run model availability in `/guardrails-review`

Added in review (see *Failing early*). Scope is the settled **split**: statically-named models now, JIT
judge resolution deferred to #223.

1. `/guardrails-review` walks the task folder it is reviewing and collects every model a task **names
   statically** — `action.model`, and each surviving judge guardrail's configured model.
2. For each, assert the model resolves to a configured runner in `guardrails.json`. A model no runner can
   serve is reported as a **review finding**, naming the task and the model.
3. **Reports, never rewrites.** `/guardrails-review` is read-only by doctrine — it names what it found and
   leaves the fix to the human, exactly as it does for a weak guardrail.
4. A judge whose model is resolved **just-in-time** is explicitly **out of scope here** and must not be
   silently skipped: the review says it could not be checked and why, so the gap is visible rather than
   assumed covered.

:::warn
**D edits `.claude/skills/guardrails-review/` — trap 3 applies to it too.** A Claude Code subprocess cannot
write under `.claude/`, so if D is dogfooded it needs the straight-to-hatch `needsHarnessWrite` form, the
same as #225. Two of four workstreams now touch `.claude/`; a breakdown that gives only #225 the hatch will
dead-end D on attempt 1, before any guardrail runs.
:::

## Acceptance

The criteria that actually discriminate — a green run must satisfy every one:

- **Additive, not breaking**: every existing `promptRunners` config (no `kind`) validates and runs unchanged.
- An unrecognized `kind` **fails validate** with a message naming the bad value.
- A block carrying `costly`/`strength`/`specialization` validates and round-trips; each malformed form
  (non-bool `costly`, `strength: 0`, out-of-enum `specialization`) fails validation.
- **`providers init` is idempotent** — running it twice against a human-annotated config leaves the
  annotation byte-identical. *A generator that clobbers the annotation it exists to solicit is worse than
  no generator.*
- **`providers init` never invents a model** — against a kind with no enumeration surface it exits 0,
  annotates, emits the "could not enumerate" comment, and writes no model identifier.
- **Gated tagging**: breaking down a plan against a **no-`routing`** config produces a folder
  **byte-identical to today**.
- SSOT §9 and §3 (sentinel included) land in the **same change** as their code.
- **A plan naming a model no configured runner can serve is a `/guardrails-review` FINDING**, not a
  successful review followed by a mid-run halt — and a JIT-resolved judge is reported as unchecked rather
  than passed over in silence.

:::warn
**Re-verify the diagnostic-code block against `DiagnosticCodes.cs` at landing, and renumber if it moved.**
The file is the registry; this plan is not. Note the DoR's own docs currently disagree on the block's size
(GR2043–GR2048 / –GR2052 / –GR2053 appear in different places) — the *start* is right, master's next-free
is GR2043. Allocate exactly the codes this stage needs and fix the range in one place. This is precisely
the failure the DoR warns about one layer up: *a reservation in an unmerged doc is a wish, not a
reservation.*
:::

## Dogfooding this stage

Driving this through Guardrails is the intent (`/plan-breakdown` → `/guardrails-review` → `run`). Three
traps, all of which fired during the #374 arc:

1. **The orphaned-golden trap (#193).** This stage edits `DiagnosticCodes.cs` and its sentinel — a file
   with pinned tests. Any task touching it must **own** the goldens its change invalidates, and test
   filters must be **class-scoped, never name-substring**. A broad `--filter` sweeps in pre-existing tests
   the task cannot edit, and every attempt then dead-ends.
2. **Comment-blind greps (#97/#98).** Diagnostic-code work is comment-heavy by nature — codes are
   documented in comments beside their declaration. A raw forbidden-string grep will fail a *correct*
   implementation and whack-a-mole to `needs-human`. Strip comments before matching.
3. **`.claude/` writes need the escape hatch.** #225's skill-doctrine change targets
   `.claude/skills/plan-breakdown/SKILL.md`. A Claude Code subprocess **cannot** write under `.claude/` —
   that task must use the straight-to-hatch `needsHarnessWrite` form or it dead-ends on attempt 1, before
   any guardrail runs.

Also expect stale per-wave diagrams to dirty the tree mid-run (#447 is still open). Harmless now that #448
narrowed the delivery gate, but it will show up in `git status`.

## Open decisions (for your review)

The first two decide the shape of the work; the third decides whether its most important acceptance
criterion is real or decorative.

:::question
{"id":"providers-init-timing","title":"In Stage 1, NO kind has an enumeration surface yet — only `claude` is implemented (others are #223), and OD-E settled that Claude cannot be enumerated. So `providers init` can only annotate existing blocks. Build it now anyway?","mode":"single","options":["Build it now, annotate-only — the axes need soliciting the moment they exist, and annotating is most of the value even with zero enumeration","Defer the whole verb to #223, when a provider with a real enumeration surface exists to make it worth running","Build the annotate half now and land the enumeration seam unimplemented, so #223 fills in a socket rather than adding a verb"],"recommended":"Build it now, annotate-only — the axes need soliciting the moment they exist, and annotating is most of the value even with zero enumeration","target":"human", "answer": ["Build it now, annotate-only \u2014 the axes need soliciting the moment they exist, and annotating is most of the value even with zero enumeration"]}
:::

:::question
{"id":"dogfood-split","title":"Which parts of Stage 1 go through Guardrails as a dogfood run, given that `providers init` enumeration would mean network calls inside guardrails?","mode":"single","options":["Dogfood #224 and #225; hand-code `providers init` — network enumeration cannot be a deterministic guardrail, and a flaky gate erodes trust in the whole run","Dogfood all three, with `providers init`'s guardrails scoped strictly to the annotate path (no live provider calls)","Hand-code all of Stage 1 — it touches DiagnosticCodes.cs and a .claude/ skill, the two surfaces that have cost the most in past runs"],"recommended":"Dogfood #224 and #225; hand-code `providers init` — network enumeration cannot be a deterministic guardrail, and a flaky gate erodes trust in the whole run","target":"human", "answer": ["Dogfood #224 and #225; hand-code \u0060providers init\u0060 \u2014 network enumeration cannot be a deterministic guardrail, and a flaky gate erodes trust in the whole run"]}
:::

:::question
{"id":"invariant-7-proof","title":"How do we PROVE the byte-identical-when-unconfigured gate (DoR Invariant 7), rather than merely asserting it?","mode":"single","options":["A committed golden task-folder from a no-routing config, plus a meta-test asserting a fresh breakdown reproduces it byte-for-byte","A meta-test that runs breakdown against a no-routing config and asserts NO action.tier / tiering block / report line appears anywhere in the output","Both — the golden catches drift the negative assertions never enumerated, the negative assertions say plainly what must never appear"],"recommended":"Both — the golden catches drift the negative assertions never enumerated, the negative assertions say plainly what must never appear","target":"human", "answer": ["Both \u2014 the golden catches drift the negative assertions never enumerated, the negative assertions say plainly what must never appear"]}
:::

## Failing early

**A failure that only the harness can raise is a failure discovered too late.** By the time
`PromptRunnerRegistry.FromConfig` throws, a run is in flight — a wave may have committed work, and a
config problem the plan could have declared arrives as a halt instead of as a review finding.

Everything decidable from *the plan plus the config* should be decided before `run`:

- **`/guardrails-review` verifies model availability for the plan it is reviewing.** For every runner a task
  could reach, the review asserts that the model is configured, and reports the ones that are not. A plan
  that names a model nobody has is a review finding, not a mid-run halt.
- **JIT judge resolution is the hard case.** A judge model chosen at judging time is, by construction,
  chosen after the work it judges has been done — so if that resolution can fail, the run has already paid
  for the task before finding out.
- **Registry construction keeps its actionable failure** as the backstop for what genuinely cannot be known
  early (a provider that is configured but down at that moment).

**Scope settled (see the resolved question below): split.** The `/guardrails-review` hook and the check for
**statically-named** models land in this stage, as **D** below. **JIT judge resolution is deferred to
#223**, where a judge can actually resolve to a non-Claude model and the check has more than one case to
verify. #223 fills a socket rather than adding a gate — the same shape already chosen for `providers init`.

:::question
{"id":"fail-early-scope","title":"Where does the pre-run model-availability check land?","mode":"single","options":["In this stage, as part of #224 — the axes and the `kind` discriminator are exactly what makes the check possible, so shipping them without it ships the halt-at-runtime behaviour the reviewer objected to","In #223, when concrete non-Claude runners exist — until then only `claude` is real, so the check can only ever assert one thing and the value is mostly theoretical","Split: land the `/guardrails-review` hook and the check for statically-named models now; defer JIT judge resolution to #223 where a judge can actually resolve to a non-Claude model"],"recommended":"Split: land the `/guardrails-review` hook and the check for statically-named models now; defer JIT judge resolution to #223 where a judge can actually resolve to a non-Claude model","rationale":"The reviewer's objection is about WHERE failures surface, and that is worth fixing while the config surface is being designed rather than retrofitting a gate later. But with only `claude` implemented in this stage, a full JIT-judge check has almost nothing to verify — it would be built against a single hard-coded case and would get its real test in #223 regardless. Landing the hook now means #223 fills a socket instead of adding a gate, which is the same shape already chosen for `providers init` in the enumeration decision above.","target":"human", "answer": ["Split: land the \u0060/guardrails-review\u0060 hook and the check for statically-named models now; defer JIT judge resolution to #223 where a judge can actually resolve to a non-Claude model"]}
:::

## Scope / non-goals

- **In:** the registry schema, the three axes, `providers init`, `action.tier` + gated classification,
  validation for all of it, and the SSOT updates that travel with them.
- **Out:** the resolver and anything that *reads* a tier (Stage 2, #226); concrete non-Claude runners
  (#223); probes (#227), the escalation ladder (#228), steering (#231) — all v2 bets.

## Related

#201 (epic) · #224 · #225 · #223 (concrete runners, standalone) · #200 (`action.model`, the pattern this
mirrors) · DoR [`17-model-tiering.md`](17-model-tiering.md) §4.1/§4.2/§4.3/§5/§10 ·
[`model-tiering-foundation.md`](model-tiering-foundation.md) (the Stage 1 brief this charter plans from) ·
[`model-tiering-verifier.charter.md`](model-tiering-verifier.charter.md) (the verifier half) · #342 (the gate)
