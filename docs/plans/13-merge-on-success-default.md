# Architecture: `mergeOnSuccess` should default ON (deliver-by-default)

> Design-of-record for issue **#340**. Decision doc — no code. The separate "loud
> undelivered-work warning" is being salvaged and shipped on its own; this doc treats that
> warning as the **interim mitigation already in place** and decides only the **default-value
> contract**.

## What's being asked

Issue #340 documents a real incident: `dfd-threagile-substrate-wave-2b` ran to a **fully
green terminal gate three times**, delivered **nothing** to `feature/dfd-threagile-substrate`,
and the console reported success **identically** to a run that DID deliver. ~$24 of
guardrail-verified work sat on `guardrails/<plan>`, one `--fresh` (which tears down the plan
branch, SSOT §6.1) away from silent destruction, while the success message reassured the user
the work was safe.

The root cause is a **default-value contract**: `mergeOnSuccess` defaults **OFF**
(`RunConfig.MergeOnSuccess = false`; `PlanLoader` applies `raw.MergeOnSuccess ?? false`). A plan
that omits the key goes green and delivers nothing. **The question this doc decides: should the
default flip to ON?**

Narrowing (not silently assumed): "deliver" = the existing end-of-run merge-back of the plan
branch into the user's original branch (SSOT §5.3 / §7.1). This doc does **not** propose any new
merge behavior — only which way the existing, already-conservative merge-back defaults.

## Placement

**Schema / contract change** (a `guardrails.json` field's default value + one new CLI opt-out
flag), landing in `docs/plans/02-schemas-and-contracts.md` §2 / §5.3 / §7.1. Harness delta is a
one-liner in `RunConfig`/`PlanLoader` plus a CLI flag and a one-time notice. **Not** a v2 bet;
**not** out of scope. It is a v1 contract correction. (Deliberately distinct from v2 bet #2 "CI
mode / PR-per-task" and v2 bet #57 "AI merge-conflict resolution" — see below.)

## Invariants in play

1. **Honest halts (Invariant 5) — the decisive one.** Green-but-undelivered is a dishonest
   "done": the harness reports success while the deliverable is not where the user asked for it.
   Default-ON makes **"green" mean "delivered,"** aligning the success signal with reality. The
   delivery-obstacle halts (`Conflict` / `DirtyWorkingTree` / `HookRejected`, exit 2) remain
   honest halts. **Strongly supported.**
2. **User checkout read-only for the run / harness is the single writer (Invariant 2, §5.3).**
   Default-ON *strains* this: mutating the user's branch is the one sanctioned write to their
   checkout, and it now fires by default. But §5.3 already carves `--merge-on-success` out as the
   *sole* sanctioned user-branch write — flipping the default does **not** add a new write path,
   it changes **when the existing sanctioned one fires**. The read-only-**during**-the-run
   guarantee is untouched (delivery is at run END, after all worktree work). Respected in letter;
   the spirit ("don't surprise the checkout") is met by loud-halt-on-obstacle + the surviving plan
   branch + the opt-out.
3. **Deterministic over prompts / AI-merge withheld (Invariant 1, §5.3).** Default-ON must **not**
   AI-merge the user's commits. Explicitly preserved: **AI-merge stays withheld at this
   boundary** — a conflict halts to `needs-human` with the plan branch intact. (Answer to conflict
   safety, below.)
4. **SSOT is the schema SSOT (Invariant 4).** The default lives in §2/§5.3/§7.1; the flip lands
   there in the same change. Verbatim edits below.

## Design

### 1. The recommendation — flip the default to ON

**Recommend: `mergeOnSuccess` defaults to `true` (deliver-by-default), using the EXISTING
conservative merge-back path unchanged.**

The reasoning turns on one observation the OFF default predates: **the merge-back is already a
non-destructive, halt-loud operation.** Today it (SSOT §5.3 / §7.1):

- fast-forwards when it can (no commit, no risk);
- for a non-FF, does a **real merge that must pass a deterministic re-verify** before it commits;
- **withholds AI-merge** — a conflict → `Conflict` halt, plan branch intact;
- **refuses a dirty user tree before any git runs** → `DirtyWorkingTree` (never touches uncommitted
  work);
- runs the user's git hooks and, on rejection, `git merge --abort`s the user branch back to its
  original HEAD → `HookRejected`;
- **is a merge, not a move** — `guardrails/<plan>` survives delivery for inspection/rollback.

OFF was the right *conservative* default when merge-back was young and unproven. It is now mature.
The thing OFF was protecting against — a destructive or AI-driven surprise mutation of the user's
branch — **cannot happen**, independent of the default, because the merge-back contract already
forbids it. So the honesty argument wins cleanly: green should mean delivered, and every obstacle
already halts loudly (exit 2) rather than silently doing the wrong thing.

**This IS the "nuanced middle" done right.** The task floated "ON with clean-FF + halt-loudly-on-
conflict rather than AI-merge" and "`--dry-run` exception" as softer options. They are already
true: the merge-back already withholds AI-merge and halts on conflict, and `--dry-run` already
touches no state and delivers nothing. So "full ON via the existing path" **is** the safest
coherent ON — no second, stricter merge-back variant is introduced (that would be a DRY-violating
two-behaviors-keyed-on-how-you-enabled-it contract; KISS says one behavior, only the default
moves).

**Rejected alternatives:**

- **Keep OFF + rely on the warning.** The warning (interim) prevents the *money-loss symptom* but
  leaves the *mental-model mismatch* (green ≠ delivered) permanent — every user takes a manual
  delivery step after every green run, forever, contradicting "verified work is safe once
  guardrails pass." Worse, an **unattended/overnight** run cannot act on a warning until a human
  returns; default-OFF under-serves exactly the unattended case that most needs delivery. The
  warning is the right interim mitigation and the right permanent backstop **for the opt-out
  case**, not a substitute for a correct default.
- **ON only in serial/shared-workspace mode.** Mode-conditional defaults are more surprising, not
  less (see the mode note under Interactions). Rejected for KISS.
- **Version-gate the flip** (`version: 1` keeps OFF, a new `version: 2` defaults ON). Zero-breaking,
  but it (a) preserves the footgun for every existing plan forever unless re-authored, (b) adds
  version-conditional-default machinery, (c) is unwarranted at the current `1.0.0-preview.N`
  pre-1.0 cadence where a loud-noted breaking default is acceptable. Rejected (YAGNI/KISS); the
  breaking-change surface is handled by messaging + the surviving plan branch instead (Migration).

### 2. Conflict-safety boundary (default-ON must NOT silently AI-merge)

**Unchanged from today, and load-bearing:** default-ON delivers through the *same* withheld-AI-
merge path. On a **non-clean** merge into the user's branch:

- FF possible → `git merge --ff-only`, `FastForwarded`, exit 0.
- Non-FF, clean 3-way → real merge, **deterministic re-verify must pass**, `Merged`, exit 0.
- **Conflict** → **no AI-merge** (withheld at this boundary); `reset`, `Conflict` halt, plan branch
  intact, exit 2, loud actionable message.
- Dirty user tree → `DirtyWorkingTree` halt **before any git runs**, exit 2.
- User hook rejects → `git merge --abort`, `HookRejected` halt with the hook's stderr, exit 2.

AI merge-conflict resolution at the user boundary remains **v2 bet #57** and is explicitly **not**
enabled by this flip. The flip changes a boolean default; it grants the AI no new authority over
the user's branch.

### 3. Interactions

- **`--no-merge-on-success` opt-out (NEW — REQUIRED by the flip).** Today there is **no** CLI way
  to turn delivery OFF: `--merge-on-success` only *forces on* ("can turn the config value on, never
  off"). With the default ON, an opt-out surface is mandatory. Introduce `--no-merge-on-success`
  and model the CLI flag as a **nullable override**. Precedence (highest wins):
  **CLI flag → `guardrails.json` `mergeOnSuccess` → default (`true`)**.

  | `guardrails.json` | CLI flag | Effective |
  |---|---|---|
  | *(omitted)* | *(none)* | **ON** (new default) |
  | `false` | *(none)* | OFF (explicit config opt-out — loader already honors an explicit `false`) |
  | `true` | *(none)* | ON |
  | *(any)* | `--merge-on-success` | ON |
  | *(any)* | `--no-merge-on-success` | OFF |
  | *(any)* | both flags | **validation error** (contradictory) |

- **`--fresh` (the incident's destruction vector).** `--fresh` tears down the plan branch (SSOT
  §6.1). Default-ON **neutralizes the incident**: a green run delivers at run end, so the work is
  in the user's branch **before** anyone reaches for `--fresh`; a later `--fresh` then destroys only
  a redundant copy on the (surviving) plan branch. For the residual **opt-out** case, the interim
  loud undelivered-work warning is the backstop. (A `--fresh` pre-flight guard that refuses to tear
  down an *undelivered* plan branch is a **related but separate hardening**, overlapping the warning
  work — out of scope for this default-value contract; noted as a candidate follow-up.)

- **Resume.** No mechanics change. Delivery fires at the end of the run that finally drains
  all-green (the existing `report.AllSucceeded && MergeOnSuccess` gate in `Scheduler.Finalize`).
  **Must confirm:** delivery is safe to *re-attempt* — a resume that re-drains green after a prior
  run already FF'd should treat "already up to date" as delivered (no error). Flag for the harness
  developer to verify; likely already a git no-op.

- **Worktree mode vs serial/shared-workspace mode (mode-dependent meaning — verify, don't
  assert).** The flip is meaningful wherever the **plan branch is the delivery target** (worktree
  mode, the default). In **serial shared-workspace** mode, task writes already land in the user's
  checkout (§3.5/§5.3), so a merge-back may be a **no-op** (the work is already there). Under
  **`runOnCurrentBranch: true`**, the plan branch **is** the current branch, so "merge plan branch
  into the user's original branch" is a self-merge / already-up-to-date — again a likely **no-op**.
  Design intent: **one default, ON, everywhere**; where there is nothing to deliver, delivery is a
  harmless no-op. The exact no-op semantics under `runOnCurrentBranch`/serial are a **mechanism
  question for the harness developer to confirm** — this doc deliberately does not assert a wrong
  mechanism.

- **CI / `--ci` consumers.** A machine consumer that manages its own delivery (v2 bet #2's
  check-run / PR-per-task) will want delivery OFF. `--ci` does **not exist in v1** (YAGNI: no
  CI-detection is built now). Design guidance recorded for that bet: **when CI mode lands it should
  set its own effective `mergeOnSuccess` default to OFF** (a machine owns delivery) and document
  why. For v1, a scripted/non-interactive consumer opts out with `mergeOnSuccess: false` or
  `--no-merge-on-success` — see Migration for the exit-code surface this creates.

- **One-time "delivered by default" notice (part of this flip).** When delivery fires **because of
  the new default** (neither the config key nor a CLI flag was set), print one line:
  `delivered to <branch> (mergeOnSuccess now defaults on; pass --no-merge-on-success or set
  "mergeOnSuccess": false to opt out)`. This makes the breaking change **observable and
  self-documenting** rather than a silent surprise. It is the delivered-case complement of the
  interim undelivered-case warning; the two never fire together.

### Seams and contracts touched

- `Guardrails.Core.Model.RunConfig.MergeOnSuccess` — default `false` → `true`.
- `Guardrails.Core.Loading.PlanLoader.LoadConfig` — `raw.MergeOnSuccess ?? false` → `?? true`.
- `Guardrails.Cli.Commands.RunCommand` — add `--no-merge-on-success`; change the flag-application
  from "force-on-only" to a **nullable tri-state override** (CLI → config → default); reject both
  flags together; emit the one-time delivered-by-default notice.
- `Guardrails.Core.Execution.Scheduler.Finalize` — **no logic change** (gate already
  `AllSucceeded && Config.MergeOnSuccess`); it simply now sees `true` by default.
- No change to `MergeOnSuccessResult`, `IWorktreeProvider.MergePlanBranchIntoUserBranch`, or the
  §7.1 exit-code semantics — the merge-back behavior is identical; only the default and the CLI
  opt-out change.

### Schema changes (exact `02-schemas-and-contracts.md` edits)

**Edit 1 — §2 config example, line 120.** Replace:

```
  "mergeOnSuccess": false,            // OPTIONAL; if true AND the whole run goes green, merge plan branch guardrails/<plan-name> into the user's original branch at run end (ff-only when possible; AI-merge is NOT used here)
```

with:

```
  "mergeOnSuccess": true,             // OPTIONAL; DEFAULT true (#340). When the whole run goes green, merge plan branch guardrails/<plan-name> into the user's original branch at run end (ff-only when possible; AI-merge is NOT used here). Set false (or pass --no-merge-on-success) to leave the work on the plan branch for manual review
```

**Edit 2 — §2 prose, lines 179–182.** Replace:

```
- `mergeOnSuccess` (default `false`; CLI `--merge-on-success` overrides) opts into end-of-run
  delivery of the plan branch into the user's original branch. **AI-merge is withheld at this
  boundary** — a conflict, a failed post-merge re-verify, or a dirty user tree halts to `needs-human`
  with the plan branch intact; never a force-overwrite, never an AI auto-resolve of the user's commits.
```

with:

```
- `mergeOnSuccess` (**default `true`, #340**) delivers the plan branch into the user's original
  branch at run end when the whole run goes green — so **"green" means "delivered."** **AI-merge is
  withheld at this boundary** — a conflict, a failed post-merge re-verify, or a dirty user tree halts
  (exit 2) with the plan branch intact; never a force-overwrite, never an AI auto-resolve of the
  user's commits. **Opt out** with `"mergeOnSuccess": false` or the CLI `--no-merge-on-success` to
  leave the verified work on the plan branch for manual review/merge. **CLI precedence** (highest
  wins): `--merge-on-success` / `--no-merge-on-success` (a nullable override) → `guardrails.json`
  `mergeOnSuccess` → the `true` default; passing both flags is a usage error. When delivery fires
  purely because of the default (no config key, no flag), the CLI prints a one-time notice naming the
  branch and the opt-out. *Rationale:* the merge-back is already non-destructive (FF-or-clean-merge,
  re-verified, AI-merge withheld, halts loudly on any obstacle, and is a merge not a move so the plan
  branch survives), so delivering by default aligns the success signal with reality without the risks
  the old OFF default guarded against. A future CI mode (roadmap bet #2) that owns its own delivery
  should set its effective default back to OFF.
```

**Edit 3 — §5.3, line ~1002 (union resolution note).** Append to the existing sentence "The AI
resolves harness-internal unions only; it is **withheld** at the `--merge-on-success` user-branch
boundary." — no wording change needed there; it already reflects withheld AI-merge. **Edit 4**
covers the §5.3 delivery description.

**Edit 4 — §5.3 "Run end (opt-in delivery)", line ~1060.** Change the heading and the "Default OFF"
sentence. Replace:

```
**Run end (opt-in delivery).** When the run drains wholly green AND `mergeOnSuccess`/
`--merge-on-success` is set, the harness merges the plan branch into the user's original branch
```
```
force-overwrite. Default OFF leaves the plan branch for the user to review and merge. The merge-back
```

with:

```
**Run end (delivery, ON by default — #340).** When the run drains wholly green AND `mergeOnSuccess`
is effective (the `true` default, or explicitly via config / `--merge-on-success`; suppressed by
`--no-merge-on-success` / `"mergeOnSuccess": false`), the harness merges the plan branch into the
user's original branch
```
```
force-overwrite. Opting out (`false` / `--no-merge-on-success`) leaves the plan branch for the user
to review and merge. The merge-back
```

**Edit 5 — §7.1 exit-code note.** No semantic change: the existing clause "every task passed but the
opt-in end-of-run delivery … was **halted**" stays accurate. Optionally drop the word "opt-in"
(delivery is now default-on). Low priority; note it so the SSOT reads consistently.

### Knowledge-skill notes required (same change)

- **`guardrails-domain-knowledge`** — "End-of-run delivery" bullet and any "Default OFF" statement:
  update to **default ON**, name the `--no-merge-on-success` opt-out and the CLI precedence, keep
  "AI-merge withheld."
- **`guardrails-dev-knowledge`** — dogfooding-safety note: with default-ON a dogfood run now
  delivers to the working branch by default; the demo-reset guidance ("`mergeOnSuccess` commits the
  solution; `--fresh` isn't a full reset") should note delivery is now the default, not opt-in.
- **`plan-breakdown` / `guardrails-review` references** — if any mirror the `mergeOnSuccess` default
  in a schema table, update to `true`.
- **`examples/hello-guardrails/…/guardrails.json`** — if it sets `mergeOnSuccess` explicitly,
  reconcile with the new default (prefer omitting the key so the example demonstrates the default).

### 5. Migration / back-compat (the breaking-change surface)

Flipping the default **changes behavior for every existing plan that omits `mergeOnSuccess`** on
upgrade: a run that used to leave work on the plan branch now mutates the user's branch. This is a
**breaking behavioral default change** and must be messaged as one:

1. **Release notes / CHANGELOG (prominent):** *"BREAKING DEFAULT: a wholly-green `guardrails run`
   now delivers to your branch by default (`mergeOnSuccess` defaults ON, #340). Pass
   `--no-merge-on-success` or set `"mergeOnSuccess": false` to keep the old
   leave-it-on-the-plan-branch behavior."* At the current `1.0.0-preview.N` pre-1.0 cadence a
   loud-noted breaking default is acceptable; if a semver line is being held, this is at least a
   minor with a headline note.
2. **Exit-code surface for scripted consumers:** default-ON converts some prior **exit-0** green
   runs into **exit-2** halts (`DirtyWorkingTree` when the user kept editing; `Conflict`;
   `HookRejected`). This is *more* honest (the work genuinely wasn't delivered), but a CI/script
   keying on exit 0 will newly see exit 2. → Reinforces that scripted/CI consumers should explicitly
   set `mergeOnSuccess: false`. Call this out in the release note.
3. **Blast radius is bounded:** delivery is a **merge, not a move** — `guardrails/<plan>` survives,
   so even a surprised user recovers by `git reset` on their branch (plan branch intact) or by
   checking out the plan branch. Combined with the one-time delivered-by-default notice, the breaking
   change is observable and reversible, not silent.

## Devil's-advocate self-critique

**Strongest counter — the inspect-first workflow.** A dogfooding/early-adopter user runs a plan
*to see what the harness produces*, fully intending to inspect `guardrails/<plan>` and cherry-pick.
Default-ON mutates their working branch/tree without asking — friction for a legitimate, arguably
common early pattern.
**Response:** (a) nothing is lost — the plan branch survives delivery, so inspect/cherry-pick/revert
are all still available; (b) delivery only FF's or clean-merges a re-verified result and halts on any
obstacle; (c) the opt-out is one flag (`--no-merge-on-success`) or one config line. Defaults should
optimize for the product's **core promise** (verified work is delivered/safe) and the common case;
the inspect-first power-user takes the one-flag opt-out. Crucially, **both populations are now
safe** — opt-out users are still covered by the interim loud undelivered-work warning — so the flip
only changes *who types a flag*, not *who can lose work*.

**Second counter — "the warning already fixes the incident, so why flip?"** The warning treats the
symptom (user might miss it); the flip fixes the cause (green ≠ delivered) and is the only option
that serves **unattended** runs, which can't act on a warning until a human returns. Recorded, with
the rebuttal, in "Rejected alternatives."

**Third counter — mode-dependent meaning.** Under `runOnCurrentBranch` / serial-shared-workspace the
flip may be a no-op. Rather than special-case the default per mode (more surprising), keep one
ON default and let delivery be a harmless no-op where there's nothing to deliver — with the exact
no-op semantics flagged as a mechanism-confirm for the harness developer, not asserted here.

**Residual risk accepted:** default-ON turns some prior exit-0 runs into exit-2 halts for scripted
consumers (item 2 above). Accepted and messaged; it is the honest signal, and the opt-out is
explicit.

## Implementation handoff (agent + filesTouched + sequencing)

Deliver this design as a **draft PR for inline human review before implementation** (#106): it is a
contract change with a genuine breaking-default judgment call — exactly the class of decision the
draft-PR loop exists for. Implementation milestones below start only after the human has reviewed
`13-merge-on-success-default.md` and the SSOT edits inline and comments are addressed.

1. **`guardrails-harness-developer`** (core + CLI). filesTouched:
   `src/Guardrails.Core/Model/RunConfig.cs` (default → `true`, doc comment),
   `src/Guardrails.Core/Loading/PlanLoader.cs` (`?? true`),
   `src/Guardrails.Cli/Commands/RunCommand.cs` (add `--no-merge-on-success`; nullable tri-state
   override + both-flags usage error; one-time delivered-by-default notice),
   `docs/plans/02-schemas-and-contracts.md` (Edits 1–5 — **same change**, Invariant 4). Confirm:
   delivery re-attempt idempotency on resume, and the `runOnCurrentBranch`/serial no-op semantics.
2. **`guardrails-test-author`** (tests, after 1 or alongside). filesTouched: `tests/**` — default
   value (omitted key → ON); precedence matrix (config `false`, `--no-merge-on-success`, both-flags
   error); dirty-tree → exit-2 halt; delivered-by-default notice fires only when neither config nor
   flag set; existing opt-in tests re-pointed to the new default.
3. **`guardrails-skill-author`** (docs/skills, after SSOT lands). filesTouched:
   `.claude/skills/guardrails-domain-knowledge/**`, `.claude/skills/guardrails-dev-knowledge/**`,
   `plan-breakdown`/`guardrails-review` references if they mirror the default,
   `examples/hello-guardrails/**` guardrails.json reconciliation.

Sequencing: SSOT + `RunConfig`/`PlanLoader` flip land **together** (Invariant 4) → CLI opt-out +
notice → tests → skills. Ideally one PR.

## Proposed plan-document edits

- **This doc:** add `docs/plans/13-merge-on-success-default.md` (this file).
- **`02-schemas-and-contracts.md`:** Edits 1–5 above (verbatim) — land with the harness flip.
- **`03-roadmap.md`:** under bet #2 (CI mode), add a one-line note that CI mode should set its
  effective `mergeOnSuccess` default OFF (machine owns delivery), cross-referencing #340; no change
  to bet #57.
- **`01-overview.md`:** if it states delivery is opt-in, update to "delivered by default; opt out
  with `--no-merge-on-success`."
