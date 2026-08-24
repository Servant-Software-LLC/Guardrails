## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — for a WAVED plan that is the WAVE-QUALIFIED id, i.e.
  `{ "wave-05-review-net/02-implement-tier-classification-audit": { "someKey": "someValue" } }`.
  The harness REJECTS a fragment keyed by anything else (every attempt), including the bare folder
  name and the stableId.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- Guardrails constrain the OUTCOME, never HOW you implement it. Never reshape working
  code — or reword a document away from its own conventions — to match a check's
  pattern.
- If you cannot proceed without a human decision, write
  {"needsHuman": {"question": "<question>", "kind": "blocked-work"}} to the
  state-out path and stop. If instead a guardrail reports something ABSENT that you
  can see is PRESENT, that guardrail is defective: use "kind": "defective-guardrail"
  and quote (a) the guardrail's exact claim and (b) the file:line that refutes it.
  If you cannot produce BOTH quotes it is not a defective guardrail — retry the work,
  or escalate as "blocked-work". Difficulty is never "defective-guardrail".

## Task

Fill real logic over the throwing stub in
`tests/Guardrails.Core.Tests/ModelTiering/TierClassificationAudit.cs` so every test in
`TierClassificationAuditTests` passes — the seven behaviour tests, the two graceful-skip silence facts, and
the two fixture-integrity facts.

**Read the tests first.** `01-author-tests-tier-classification-audit` authored them and they are the
contract; this prompt describes the rule, they define it.

**Scope boundary (harness-enforced):** Write only to
`tests/Guardrails.Core.Tests/ModelTiering/TierClassificationAudit.cs`. The harness runs a `git diff` check
after this task and rejects any edit outside that path — in particular
`TierClassificationAuditTests.cs` and everything under `TestData/tier-tags/` are **not yours**. An
out-of-scope edit fails the task immediately and consumes a retry. If a test or a fixture is genuinely
wrong or incompatible, write `{"needsHuman": "<why>"}` to the state-out path and stop rather than changing
it.

### The rule you are implementing

This is the deterministic half of the `/guardrails-review` model-appropriateness net (#229): *which prompt
tasks and surviving prompt-judge guardrails did a tiering-configured plan leave unclassified?* It is a fact
about the folder, computable from a loaded `PlanDefinition` and nothing else.

**1. `IsTieringConfigured(plan)` — the gate, and the reason the whole thing is safe to ship.**

True when the plan declares ANY tiering metadata: at least one `promptRunners.<name>` block carries a
`routing` block, **or** the config carries a top-level `tiering` block. False otherwise.

This restates the predicate `NoRoutingGolden.IsUnconfiguredForTiering` already codifies for the
Invariant-7 golden (read it in `tests/Guardrails.Integration.Tests/ModelTiering/NoRoutingGolden.cs`; you
cannot call it — it is `internal` to another test assembly — so restate the same two-part test, do not
invent a third spelling of the same fact). A plan generated before tiering shipped satisfies neither half,
and for such a plan **`Audit` returns an empty list** — not a finding, not a note, nothing. A check that
fires on every legacy plan gets muted within a week, and a muted check is indistinguishable from the
absence this stage exists to fix.

**2. `Audit(plan)` — the findings.** Empty when `IsTieringConfigured` is false. Otherwise, one finding per
unclassified subject, in a deterministic order (subject id, ordinal), across two populations:

- **A prompt-action task** (`task.Action.Kind` is a prompt) is **unclassified** when its own `task.json`
  declared no `action.tier` AND it carries no pin — no `action.model`, no `action.runner`, no
  `action.effort`.

  **`ActionDefinition.Tier` alone cannot answer this, and that is the crux of the task.** The loader
  resolves the tier by precedence — `task.json action.tier` > `tiering.defaultTier` > null — and that
  collapse destroys the answer to *"where did this rung come from?"*. A plan carrying
  `tiering.defaultTier: "medium"` therefore has a non-null `Tier` on **every** task, including one a human
  hand-added after breakdown that nobody ever classified. Read `ActionDefinition.TierOrigin` instead: only
  `TierOrigin.Task` means the task declared its own. Read that field's docstring before you write the
  predicate — it exists for exactly this reason.

- **A surviving prompt-judge guardrail** (`guardrail.Kind` is a prompt) with a null
  `GuardrailDefinition.Tier` is **unclassified only when it has no classified actor to follow**. Read
  SSOT §4.2 before you write this: an absent frontmatter `tier` does not mean undefined, it means *the
  judge's rung follows the actor it guards*. So flag it when the TASK it guards is itself unclassified, or
  when it guards no task at all — a plan-root or wave-root gate, where there is no actor to inherit from.
  An untagged judge on a classified task is correct and must not be reported; flagging it would fire on
  almost every configured plan, and a check that fires on almost every plan gets muted. There is no
  plan-wide default standing behind a judge either way (`GuardrailDefinition.Tier` is bound from
  frontmatter and from nowhere else), so the discharge is a frontmatter `tier`, not an `action.tier`.
  Sweep every folder a prompt judge can live in: each task's `Guardrails` and `Preflights`, the plan-root
  `PlanGuardrails` and `PlanPreflights`, and each wave's `Guardrails` and `Preflights`.

**Script actions and script guardrails are never subjects at all.** They run no model, so there is nothing
to classify; they are absent from `ClassifiableSubjects`, not present-and-passing.

Each finding carries the resolved tier and the origin as well as the subject, so the report can say
*"resolves to medium, but from the plan-wide default — nobody classified this one"*. Write a `Detail` that
names the remedy in one sentence and distinguishes the two populations: an `action.tier` (or a pin) in
`task.json` for a task, a frontmatter `tier` for a judge.

**3. `ClassifiableSubjects(plan)` — the anti-vacuity census.** Every subject the audit CONSIDERED,
classified or not, as a deterministically-ordered list of subject ids. An audit that reports no findings
because it recognised no subjects would be green for the wrong reason — the passing-but-blind shape this
whole stage is about — and this is what lets a test tell the two apart.

### Do not do these

- **Do not add a check to `guardrails validate` and do not allocate a diagnostic code.** The ruling is
  settled and is not yours to revisit: the harness does not block on a model-quality opinion, and a code is
  a thing that can fail a build. Everything you write stays inside this one file, under `tests/`. A
  guardrail on this task scans your file for a code literal, for a member access on the code registry, and
  for a validator construction, and fails on any of them.
- **Do not read the RESOLVED tier as evidence of classification.** See the crux above. A `Tier` of
  `"medium"` says nothing about whether anyone chose it.
- **Do not make the gate depend on whether any tag is present.** A plan with tags and no `routing` block
  anywhere is a real configuration the validator already reports on its own; this audit stays silent there,
  and one of the authored tests pins exactly that.
- **Do not edit the tests or the fixtures to make your implementation fit.** They are outside your
  writeScope and the harness will reject the edit.
