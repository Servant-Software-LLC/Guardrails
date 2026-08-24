## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — for a WAVED plan that is the WAVE-QUALIFIED id, i.e.
  `{ "wave-05-review-net/03-author-tests-review-net-doctrine": { "someKey": "someValue" } }`.
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

Author the TDD **red** for this wave's skill deliverable: a doctrine-anchor test that goes red when the
`/guardrails-review` model-appropriateness probe is missing, gutted, or pasted in the wrong place.

You author ONE file. You do **not** touch `.claude/skills/guardrails-review/SKILL.md` —
`04-add-model-appropriateness-probe` writes it, and every assertion you make here is red until it does.

**Scope boundary (harness-enforced):** Write only to
`tests/Guardrails.Core.Tests/ModelTiering/ModelAppropriatenessDoctrineAnchorTests.cs`. After this task
completes the harness runs a `git diff` check and rejects any edit outside that path — in particular
editing the skill file to make your own test pass would fail the task immediately and consume a retry. If
you hit a compile error caused by a missing symbol in another file, do NOT edit that file — write
`{"needsHuman": "<what is missing>"}` to the state-out path and stop.

### Read this first — it is the shape you are copying

`tests/Guardrails.Core.Tests/SeamDoctrineAnchorTests.cs`. That test exists for exactly this reason: #382
ships as SKILL TEXT ONLY, with no `validate` code and no diagnostic code, so a deliverable with no
regression signal is one silent edit away from being retired by accident. This wave is in the same
position, for the same stated reason. Copy its structure — the `(SkillFile, Clause, Doctrine)` row list, the
`Anchors()` `TheoryData`, the reflow-tolerant `Normalize`, the failure message that names WHICH doctrine
was lost and tells the reader to re-point the anchor rather than delete the row, and the anchor-set hygiene
fact.

**Reuse its `Normalize` semantics exactly** (strip line endings, indentation, markdown blockquote markers
and whitespace runs, then compare ordinal). That is what makes an anchor survive a re-wrap and fail only on
a change of words — and an anchor set that breaks on re-wrapping is an anchor set someone deletes for being
noisy.

### 1. The file

`tests/Guardrails.Core.Tests/ModelTiering/ModelAppropriatenessDoctrineAnchorTests.cs`, namespace
`Guardrails.Core.Tests.ModelTiering`, class `ModelAppropriatenessDoctrineAnchorTests`. One skill file is
under test:

```
.claude/skills/guardrails-review/SKILL.md
```

Resolve it from the repo root the way `SeamDoctrineAnchorTests` does (`TestPaths.ProjectDir` walked up two
levels), and assert the file exists before matching, so a moved skill fails with "file not found" rather
than with fourteen mystery clause failures.

### 2. The fourteen clauses — copy these VERBATIM

Each row is `(clause, doctrine)`. The **clause** text below is matched against the skill after
normalization, so copy it character-for-character into the row — including the backticks, the em dashes,
the middle dot, the `§` signs and the asterisks. Do not paraphrase, re-punctuate, shorten, or "fix" any of
them: `04-add-model-appropriateness-probe` is given this identical list and writes these exact sentences
into the skill, so any divergence you introduce makes a clause that can never be satisfied and dead-ends
that task.

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

Write the **doctrine** column yourself, one sentence each, saying what is lost if that row goes. Use these
as your source — they are why each clause is on the list:

1. the probe EXISTS, in section 2's list, and says which issue and charter section it comes from.
2. the GATE. Without it the probe has no "when", and the graceful skip has nothing to hang on.
3. the SILENCE, stated as an absolute. The three "no …" clauses are the whole of it, and the third one is
   the one a careful author breaks by being helpful.
4. the WHY, in the charter's own words. Delete it and the skip reads as politeness rather than a
   requirement, and the next editor softens it.
5. the deterministic finding's DEFINITION, in the charter's own words.
6. the TRAP. This is the single most likely way the probe ships and finds nothing forever.
7. where to read INSTEAD of the resolved tier — the field that survives the load-time collapse.
8. the judge population, its tag SITE, and the inheritance that keeps this half from being noise. SSOT
   §4.2 is explicit that an absent judge `tier` means the judge follows the actor — so flagging every
   untagged judge would fire on almost every configured plan, which is how a check gets muted.
9. the judgment finding's DEFINITION. Without it the net ships half-built, which was an explicit option
   the charter considered and rejected.
10. the standing ruling that difficulty maps to a candidate SET. A probe that reasons about "the right
    model" instead of "the right rung" is arguing with the design.
11. the no-code ruling, with its reason attached. The reason is what stops the next person re-opening it.
12. the boundary against a diagnostic that already exists. Without it the probe emits a second opinion on
    a config the validator has already reported.
13. the REPORT counterpart of the silence, and the sharpest clause in the set: section 6's convention is to
    state what the pass could not check, which is exactly the instinct that would break the skip.
14. the QUALITY BAR item, so the probe is on the checklist and not merely in the prose.

### 3. The three facts

**`TheSkillStillCarriesTheClause`** — the `[Theory]` over the fourteen rows, `[MemberData(nameof(Anchors))]`,
matching normalized clause against normalized skill. Its failure message must name the doctrine and the
clause, say that this wave ships no `validate` code and no diagnostic code so this test is the only durable
evidence the rule was applied, tell the reader to re-point the anchor in the SAME change that moved the
clause rather than deleting the row, and note that matching ignores line endings, indentation, blockquote
markers and whitespace runs so a re-wrap cannot cause it.

**`TheThreeInsertionsLandInTheirOwnSections`** — the placement fact, and the one the clause theory cannot
carry: a probe pasted at the end of the file satisfies every clause above. Accumulate all three checks and
report them together rather than exiting on the first:

- clause 1 sits **between** `### 2. Adversarial pass per task (the heart)` and `### 2b. EXECUTE the guardrails`
  — i.e. inside section 2's probe list, where its siblings are;
- clause 13 sits **between** `### 6. Report` and `### 7. Record the review`;
- clause 14 sits **after** `## Quality bar`.

Compare normalized offsets, and assert each section heading is itself present first, so a restructured
skill fails with "the heading moved" rather than with three unexplained ordering failures.

**`TheAnchorSetIsEvidence_NotCeremony`** — the anchor set's own hygiene, asked of itself the way this repo
asks it of a guardrail: every normalized clause is at least 19 characters (short enough to hit by accident
is short enough to survive a gutted skill), and no two rows pin the same clause (which would read as
broader coverage than it is). **This fact passes the moment you author the file** — it reads no skill text —
and a guardrail on this task requires it to be observed PASSED. That is deliberate: without it, a row list
that failed to load at all would be counted as a clean red.

### Do not do these

- **Do not edit the skill.** Failing is the point; it is outside your writeScope and the harness will
  reject the edit.
- **Do not soften a clause to something already in the skill.** A guardrail on this task requires **every**
  row of the theory to be observed FAILED — a row that is green on today's skill is a row that pins nothing,
  and it will be named.
- **Do not add rows of your own.** Fourteen is the set; `04-add-model-appropriateness-probe` is given the
  same fourteen and writes exactly those.
- **Do not implement anything else.** This file reads one markdown document and asserts on it.
