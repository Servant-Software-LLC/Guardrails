# Wave 5 — the review net (#229)

> **This wave is a JIT stub.** Its tasks are authored at the wave-5 checkpoint, against the
> **materialized integration worktree** — by which point waves 1–4 have landed the codes it cites and
> the provenance shape it reasons about. Seeded from `model-tiering-stage-3.charter.md` §C.

## Budget — read this before you decompose

**Author AT MOST 4 task folders.** See #504 and wave 2's brief for the measurement. This wave edits a
skill document and its meta-tests, not the harness, so it should decompose small; if it does not,
**say so** rather than compressing deliverables.

## What this wave must accomplish

Add the model-appropriateness net to `/guardrails-review` — the third advisory surface, and the only
one that fires at **zero spend**. Two findings of very different character, and the charter is
explicit that they are worth separating:

- **Missing classification** *(deterministic)* — a prompt-action task, or a surviving judge guardrail,
  with neither a difficulty tag nor an explicit `action.model` / `action.effort` pin. This is a fact
  about the folder. It is the safety net for a task a human hand-added after breakdown, and with the
  ladder (#228) deferred to v2 there is **no runtime backstop**: a mis-tag is caught here or not at all.
- **Mismatched tier** *(judgment)* — a high-risk task tagged for a weak tier, or a mechanical one
  tagged frontier-only. Genuinely a model's opinion about difficulty.

Both are **advisory findings in the review report**, at the skill's existing severity conventions,
never a silent auto-fix.

## Two rulings, neither of which is yours to revisit

- **No GR code.** Per DoR §12.6: *a GR code is a thing that can fail a build, and the harness does not
  block on a model-quality opinion.* Do not allocate one, and do not add a validator check.
- **Graceful skip is a requirement, not politeness.** A plan generated before tiering shipped has no
  tier field anywhere. The check must produce **nothing at all** on such a folder — Invariant 7's
  review-time counterpart. A check that fires on every legacy plan gets muted within a week, and a
  muted check is indistinguishable from the absence this stage exists to fix.

**The graceful-skip case needs its own test, and that test is a silence assertion.** A silence
assertion cannot be red before the feature exists — a legacy folder produces no finding both before
and after. Do not author it as a TDD "red" test and do not let a red-census guardrail demand that it
fail; assert it alongside the positive cases and let the positive ones carry the red. Getting this
wrong is what nearly destroyed the Invariant-7 test in wave 1.

## Orientation — scoped to this wave

- `.claude/skills/guardrails-review/SKILL.md` — the existing probe structure and severity conventions.
  New probes must read like the ones already there.
- The tier vocabulary as **materialized** by Stage 2 and wave 1 — `Tier`, `TierSource`, the `costly`
  tri-state, and GR2051/GR2052/GR2053. Read them from the tree, not from this brief.
- The skill's meta-tests (golden-folder round-trips). A skill change without its meta-test is how a
  probe ships that nothing exercises.

## Standing rulings not to re-litigate

- `costly` is **tri-state**.
- Difficulty maps to a candidate **SET**, not to a single model strength.

## SSOT ownership

The **last** task in the wave owns any SSOT or domain-knowledge delta, and no other task may touch
`docs/plans/02-schemas-and-contracts.md` or
`.claude/skills/guardrails-domain-knowledge/SKILL.md`. If nothing documented changes, emit no docs
task rather than a vacuous one.

## Out of scope

- The runtime ladder (#228) — deferred to v2, and its absence is precisely why the deterministic
  finding above matters.
- Recording which model **authored** a breakdown (#495).
