## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — for a WAVED plan that is the WAVE-QUALIFIED id, i.e.
  `{ "wave-05-review-net/04-add-model-appropriateness-probe": { "someKey": "someValue" } }`.
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

## Harness-write escape hatch (your only deliverable lives under `.claude/`)

Your deliverable is a file under `.claude/`, which a Claude Code subprocess CANNOT write — the
tool-permission layer refuses every `.claude/` write unconditionally. Do NOT attempt a direct
`Write`/`Edit` to the `.claude/` path: a direct-write probe wastes a turn and populates the harness's
permission-wall tracker. Instead, FIRST write a `needsHarnessWrite` request to the state-out path. The
harness (which is NOT subject to that layer) performs the write directly, then your guardrails still run
normally against the result. There are two forms, and they are mutually exclusive — send exactly one:

- **MODIFYING an existing file — use `edits` (this is your case):**
  `{"needsHarnessWrite": {"path": "<workspace-relative path>", "reason": "<why>", "edits":
  [{"old": "<verbatim anchor text>", "new": "<replacement text>"}]}}`.
  Each `old` must occur **exactly once** in the file — zero matches and two-or-more matches are both
  rejected, so include enough surrounding context to make each anchor unique. `old` is matched
  VERBATIM (exact indentation, punctuation and blank lines; only line endings are tolerated), so
  copy the passage out of the file rather than retyping it. Edits apply in order and ATOMICALLY: if
  any one fails, none are written and the file is unchanged. An empty `new` deletes the anchored
  text. Use `edits` **however large the file is** — its cost scales with your change, not the file.
- **CREATING a file — use `content`:**
  `{"needsHarnessWrite": {"path": "<workspace-relative path>", "content": "<full file content>",
  "reason": "<why>"}}`.
  Do NOT use `content` here. `SKILL.md` is ~200 KB; the harness refuses full-content mode for an existing
  target over 64 KB, and re-emitting thousands of lines you did not mean to change risks silently
  corrupting them. Your three insertions are three `edits` entries in ONE request.

If you already attempted a direct write and it was refused, do NOT retry it or try workarounds
(PowerShell, `dangerouslyDisableSandbox`) — just emit `needsHarnessWrite` as above.

## Task

Add the **model-appropriateness net** to `/guardrails-review`: the third advisory surface of #229, and the
only one that fires at zero spend. Three insertions into
`.claude/skills/guardrails-review/SKILL.md`, in ONE `needsHarnessWrite` request with three `edits` entries.

`03-author-tests-review-net-doctrine` has already authored
`tests/Guardrails.Core.Tests/ModelTiering/ModelAppropriatenessDoctrineAnchorTests.cs`, which pins the
fourteen clauses below verbatim and asserts where three of them land. **Read that file before you write
anything** — it is the contract, and it is in your tree. This prompt was written before it ran, so if it
and this prompt ever disagree about a clause's characters, the FILE is the one your guardrail runs against;
say so in your state fragment rather than guessing.

**Scope boundary (harness-enforced):** Write only to `.claude/skills/guardrails-review/SKILL.md`. The
harness runs a `git diff` check after this task and rejects any edit outside that path — in particular the
anchor test file is **not yours**: editing it to match prose you preferred would fail the task immediately
and consume a retry. If a clause is genuinely unwritable, write `{"needsHuman": "<why>"}` to the state-out
path and stop.

### Where the three insertions go — grep for these markers, do not trust a line number

The skill is edited by many hands and every line number in it is stale on arrival. Each anchor below occurs
**exactly once** (verified 2026-08-24); confirm that yourself with `Grep` before you build the `edits`
array, because a `needsHarnessWrite` anchor that matches zero or two places rejects the WHOLE request
atomically.

1. **The probe itself → section 2's probe list**, immediately AFTER the `Model named but unservable` bullet
   and its sub-bullets, i.e. immediately BEFORE the bullet beginning
   `- **Missing / malformed positive-baseline (preflight) on a brownfield plan (#181)**:`. The
   `Model named but unservable` probe (#224) is your nearest sibling in voice, scope and shape — it is
   advisory, it reports and never rewrites, and it closes with a "do not re-report what `validate` already
   says" paragraph. **Read it in full and write in its register.** New probes must read like the ones
   already there.
2. **The report line → section 6's "At minimum:" list.** Its first item is
   `- the model-availability probe's JIT-resolved judge models, deferred to #223;`. Add yours as a new
   bullet in that list.
3. **The checklist item → the Quality bar**, immediately after the line
   `- [ ] No fix applied without explicit approval; human-authored guardrails called out.`

### The fourteen clauses — VERBATIM

These sentences must appear in the skill character-for-character (backticks, em dashes, the middle dot, the
`§` signs, the asterisks). Everything around them is yours to write: the framing, the worked example, the
severity guidance, the fix column. Do not paraphrase, re-punctuate or "improve" a clause — the anchor test
matches them after whitespace normalization and nothing else.

```text
 1  - **Model-appropriateness — the tag-quality net (#229 · `model-tiering-stage-3` charter §C)**:
 2  **Run this probe ONLY when tiering is CONFIGURED** — at least one `promptRunners.<name>.routing` block, or a top-level `tiering` block, in the config that governs this plan.
 3  On a plan generated before tiering shipped this probe produces NOTHING AT ALL — no finding, no unchecked-gap line, and no note saying it was skipped.
 4  A check that fires on every legacy plan gets muted within a week, and a muted check is indistinguishable from the absence this stage exists to fix.
 5  **Missing classification** *(deterministic — a fact about the folder)*: a prompt-action task, or a surviving judge guardrail, with neither a difficulty tag nor an explicit `action.model` / `action.effort` pin.
 6  A plan-wide `tiering.defaultTier` does NOT discharge it: the loader resolves that default into every untagged task, so a probe that read the RESOLVED tier would report every configured plan as fully classified.
 7  Read the task's own declaration — `TierOrigin.Task` is the value that survives the `?? defaultTier` collapse (DoR §12.4).
 8  A surviving prompt-judge guardrail carries its own `tier` in FRONTMATTER (SSOT §4.2), and an absent key means its rung FOLLOWS THE ACTOR it guards — so it is unclassified only when that task is unclassified too, or when it guards no task at all (a plan-root or wave-root gate).
 9  **Mismatched tier** *(judgment — a model's opinion about difficulty)*: a high-risk task tagged for a weak tier, or a mechanical one tagged frontier-only.
10  Difficulty maps to a candidate SET, not to a single model strength
11  Neither finding gets a GR code: a GR code is a thing that can fail a build, and the harness does not block on a model-quality opinion (DoR §12.6).
12  a plan with tags and no `routing` block anywhere is GR2049's business, not this probe's
13  On an unconfigured plan this line is ABSENT — the graceful skip is silence, not a line saying it was silent.
14  is named as an advisory MISSING-CLASSIFICATION finding
```

Clauses 1–12 belong to insertion 1 (the probe). Clause 13 belongs to insertion 2 (the report line). Clause
14 belongs to insertion 3 (the Quality bar item). The anchor test asserts exactly that placement.

### What the probe must actually say

**Both findings are ADVISORY**, at this skill's existing severity conventions (BLOCKER / WEAK / NIT — see
section 6), and neither is ever a silent auto-fix. Like the #224 probe, this one **reports and never
rewrites**: it names the task or the judge and leaves the fix to the human; it never edits a `task.json`, a
frontmatter block or a `promptRunners` block to make its own check pass.

**Finding 1 — missing classification, deterministic.** Give it the same treatment the #224 probe gives its
"what to collect" list: say which populations to walk (prompt-action tasks; surviving prompt-judge
guardrails, in every folder one can live in — each task's `guardrails/` and `preflights/`, the plan-root
pair, and each wave's pair), what discharges the finding, and what does not. Clause 8's inheritance rule is
what keeps the judge half from being noise, and it is worth one sentence of its own: an untagged judge on a
CLASSIFIED task is fine — SSOT §4.2 says its rung follows the actor's — so the flag is for an untagged
judge whose actor is *also* unclassified, and for one that guards no task at all, where there is no actor
to follow. Clause 6 and clause 7 are the
load-bearing pair and deserve a sentence of worked explanation between them: a plan carrying
`tiering.defaultTier: "medium"` has a non-null resolved tier on **every** task, including one a human
hand-added after breakdown that nobody ever classified — which is precisely the case this net exists for,
because with the runtime ladder (#228) deferred there is no backstop and a mis-tag is caught here or not at
all. Say plainly that a script action and a script guardrail are not subjects at all: they run no model, so
there is nothing to classify.

**Finding 2 — mismatched tier, judgment.** Be honest about what it is: a model's opinion about difficulty,
which is why it is separated from finding 1 rather than folded into it. Two shapes worth naming — a
high-risk task (a composition-root wiring, a cross-cutting output shape, an unfamiliar-SDK integration)
tagged `easy`, and a mechanical one (a rename, a config edit at a named key, a seeded directory) tagged
`hard`. Clause 10 is the guard rail on this half: a tier names a RUNG, and a rung maps to a candidate SET,
so a finding phrased as "this should run on <model>" is arguing with the design rather than applying it.

**The gate and the silence.** Clauses 2, 3 and 4 are one paragraph and should read as one. Clause 12 is its
boundary: like the #224 probe, this one defers to what `validate` already reports rather than emitting a
second opinion on the same config.

**The report line (insertion 2).** Section 6's convention is to state what the pass could NOT check —
and that instinct is exactly what would break the graceful skip, so the line must be explicitly
conditional. On a tiering-configured plan it names the tasks and judges found unclassified and states that
the mismatched-tier half is an opinion rather than a checked fact. Clause 13 is the other half of the
sentence, and it is the sharpest clause in the set.

### Do not do these

- **Do not allocate a diagnostic code and do not add a check to `guardrails validate`.** The ruling is
  settled and is not yours to revisit; clause 11 states it and its reason.
- **Do not describe `costly` as a boolean.** It is TRI-STATE — `null` is "not stated" and is deliberately
  distinct from an explicit `false`. A standing ruling, not open.
- **Do not rewrite, re-order or "tidy" anything you were not asked to insert.** Your three `edits` entries
  add text and change nothing else. A guardrail on this task re-checks that the sections around your
  insertions are still intact, and the existing `SeamDoctrineAnchorTests` rows over this same file will
  fail loudly if you disturb the clauses they pin.
- **Do not touch the anchor test.** It is outside your writeScope.
