# 15 — Guardrail-script lint: GR2028's doctrine gap (#343) + a banned-pattern registry (#346)

**Status:** design-of-record (draft). Per the #106 design→draft-PR loop this doc should be opened as a
**draft PR for inline human review BEFORE its implementation milestones begin**. It is not a trivial
change, so the ceremony applies.

**Author:** Guardrails architect. **Design only — no source/schema/skill was edited producing this doc.**

This design covers two related GitHub issues about how `guardrails validate` treats the *source text* of
generated guardrail scripts versus the `plan-breakdown` doctrine:

- **§A — Issue #343:** GR2028's acceptance is **narrower** than the doctrine it enforces. The doctrine
  presents a content/"contribution-present" union check as an equally-valid GR2028 shape; the validator
  only credits a build/test invocation **or** a conflict-marker check.
- **§B — Issue #346:** correct SKILL.md text does **not** guarantee LLM compliance — a fresh generation
  regressed a previously-fixed anti-pattern (#187). Proposal (maintainer-generalized): a **data-driven
  banned-pattern registry** the validator scans every generated guardrail's source against.
- **§C — Interaction:** GR2028 is a *positive* requirement; the registry is a *negative* ban. One
  mechanism or two? Where do they touch?

The two recommendations, up front:

- **#343 → TIGHTEN the doctrine (option 2), not widen the validator.** The conflict-marker-freedom check
  (or a real build/test re-run) is the load-bearing **union-soundness** guarantee; a contribution-present
  check in its mandatory union-safe *conditional* form is **vacuous on absence** and cannot certify a
  sound union alone. Widening the validator to credit content-only checks would re-open the exact
  "present-but-verifies-nothing" hole GR2028 exists to close, because a content grep has **no
  ungameable signal** the way the build-invocation and the 7-char conflict token do. Fix the doctrine +
  the GR2028 error message; leave the acceptance logic as-is except for one consistency edit (below).
- **#346 → BUILD the registry, GR2037, seeded small and honestly.** Author one JSON registry beside the
  catalogue, embed it into `Guardrails.Core`, scan every four-folder **script** guardrail's
  comment-stripped body, emit a single `GR2037` per match. **Seed with #73** (the hollow-assertion
  `AVOID` regex — an exact literal, highest confidence) and **#187a** (the unanchored
  `(^|[[:space:]])`-style conflict-marker construction — the actual #346 regression). **Do not** seed
  #97/#98, #175, #120, #40 — assessed below as not a banned-regex shape.
- **#343 × #346 → TWO mechanisms, one shared edit.** Keep GR2028 (positive, topology-conditional,
  per-folder existence) and the registry (negative, unconditional, per-file ban) separate. They intersect
  at exactly one regex — the bare `=======` conflict token — which is a **latent doctrine/validator drift
  worth fixing under #343 regardless of the registry** (see §A.4).

---

## §A — Issue #343: GR2028 validation is narrower than its documented doctrine

### A.1 What's being asked

`plan-breakdown`'s SKILL.md + `references/guardrail-catalogue.md` teach that a terminal-gate
`scope:"integration"` guardrail satisfies **GR2028** by asserting *any* union-safe invariant, and they
name a **contribution-present** check ("if token `X` is present, verify it's real") as one such shape. But
`guardrails validate` rejects a content-topic-only union check (grep a shared file for `risk_tracking`,
no conflict-marker logic) and accepts one that carries a conflict-marker check — confirmed live by three
back-to-back variants. Decide: **(1) widen** GR2028 to accept any union-safe conditional invariant, or
**(2) tighten** the doctrine to say a conflict-marker/build re-run is required. Recommend one.

**Ambiguity named + narrowed.** The reporter's plan was two tasks writing **disjoint** files, so GR2028
fired purely on its topology heuristic (`leafCount >= 2`), not on a real collision surface. There are
therefore *two* latent questions: (i) *what may satisfy* GR2028 (the acceptance heuristic — the real
subject of #343), and (ii) *when GR2028 should fire at all* (leaf-count vs. writeScope-overlap trigger). I
narrow this design to (i). Question (ii) is **out of scope** and I decline to change the trigger: the
harness re-verifies the integration set at **every** union (fan-in *or* non-FF plan-branch integration,
SSOT §4.3/§5.3) regardless of writeScope overlap, so a topology-based trigger is correct; keying GR2028 on
overlap would weaken the terminal gate. See A.6.

### A.2 Placement

**Harness (error-message text only) + skill (doctrine) + schema (§3.3 consistency edit).** No change to
the GR2028 acceptance *logic* except dropping bare `={7}` from the credit regex (§A.4). This is
predominantly a **skill + docs** change: the validator is essentially right; the doctrine over-claims.

### A.3 Invariants in play

- **#1 Deterministic guardrails over prompt-judges; judges never alone.** GR2028's whole reason to exist
  is that the terminal gate must *deterministically* certify the merged HEAD. The two accepted forms
  (build-invocation, conflict-token) are **ungameable by construction**; a content-topic grep is not.
  Widening to credit content-only checks would let a tautology satisfy a deterministic gate — a direct
  strain on invariant #1. **This is the decisive argument.**
- **#4 SSOT is the schema single-source-of-truth.** GR2028's contract lives in SSOT §3.3. Any change to
  what GR2028 accepts (the `={7}` drop) lands in §3.3 in the same change. The doctrine-tightening also
  aligns SKILL.md/catalogue with §3.3.
- **#5 Honest halts / needs-human is a feature.** The current failure is an *honest* halt (validate
  rejects), but a **confusing** one: the author had no documented reason to expect it. Tightening the
  doctrine + the error message converts a trial-and-error halt into a self-explaining one.

### A.4 The exact mismatch (grounded in the code)

`PlanValidator.ReRunsIntegrationSet` (src/Guardrails.Core/Loading/PlanValidator.cs) credits a
`<plan>/guardrails/` **script** iff its comment-stripped body matches **either**:

1. `InvokesIntegrationCommand` — a recognized whole-repo build/test/suite command **invoked at a
   statement position** (issue #207 rigor: quoted literals stripped, output-builtins discarded,
   anchored at a pipeline/segment start), or
2. `UnionInvariantConflictMarker` = the regex `<{7}|={7}|>{7}` — a literal 7-char git conflict token in
   the body.

A **content/contribution-present** check — `if ($content -match 'risk_tracking') { … }` with no build
invocation and no 7-char token — matches **neither**, so `validate` rejects the folder with GR2028. This
is exactly what #343 observed. The doctrine, by contrast:

- **SKILL.md** (lines ~378–412) says the terminal folder "MUST carry ≥1 `scope:"integration"` UNION-SAFE
  invariant (GR2028)", then: *"A contribution-present check uses the same conditional shape … The
  overlapping-writeScope union-guardrail (#132) IS this integration-scoped guardrail — make THAT, not the
  build/suite, the terminal folder's union invariant satisfying GR2028."*
- **Catalogue** (§"A `scope:"integration"` guardrail MUST be UNION-SAFE", ~1170–1338) lists the
  contribution-present shape alongside the conflict-marker shape as instances of the same archetype.

Read literally, the prose licenses a contribution-present-**only** terminal gate. Note, though: **every
worked *example* in the doctrine already bundles the conflict-marker check** into the same script
(catalogue lines 1256–1258; SKILL.md line 395). So the mismatch is **prose over-claim**, not a broken
example — which is why the fix is small and low-risk.

### A.5 The crux — is the conflict-marker check load-bearing? (recommend: yes → tighten)

The question the issue asks me to settle: *is the conflict-marker-freedom check the load-bearing
union-safety guarantee (so requiring it is correct and the doctrine is wrong), or is "any union-safe
invariant" genuinely sufficient (so the heuristic is too narrow)?*

**Recommendation: the conflict-marker/build re-run is load-bearing. Tighten the doctrine (option 2).**
Three independent reasons, strongest first:

1. **A contribution-present check is vacuous exactly where a terminal gate matters most.** The
   union-safe rule (#165) *requires* the conditional form `if (token present) { verify construct }` — so
   the check passes trivially at intermediate unions before the producing task has run. The unavoidable
   consequence: the check can **only** fail when a contribution is *present-but-fake* (comment-only); it
   **cannot** fail when the merge **dropped** the contribution entirely (gate false → pass). Requiring "all
   N contributions present" would fix that — but that is the forbidden terminal-postcondition
   anti-pattern (#125), which false-fails at intermediate unions. So a content-only union check, at the
   terminal HEAD, certifies "present contributions aren't fakes" and says **nothing** about whether the
   union integrated soundly. It is a per-contribution *tightening*, not a union-*soundness* proof.
2. **Only build/test and conflict-markers are ungameable; content greps are not.** GR2028's teeth (SSOT
   §3.3) are that an `exit 0` file, a bare `echo`, or a present-but-hollow file must **not** pass. A
   recognized build invocation re-executes reality; the 7-char conflict token has "no legitimate reason to
   write other than detecting it." A content-topic grep has no such signal — the validator cannot
   distinguish `if ($content -match 'risk_tracking') {…}` from `if (Test-Path x) { exit 0 }` without
   semantic understanding. **Crediting content-only checks would re-open the precise hole GR2028 closes.**
   The validator is right to demand one of the two ungameable forms.
3. **The doctrine's own examples already comply.** The canonical `parallel-hello` /
   `components-union-verified` union-guardrails carry the conflict-marker check *and* the
   contribution-present checks together. Tightening the prose to match the examples is a no-op for
   well-authored plans; it only removes the license to write the degenerate content-only form.

**Why not widen (option 1):** there is no ungameable way to recognize "a genuine content/contribution
check" in guardrail source; any recognizer broad enough to admit `risk_tracking` also admits a tautology,
gutting GR2028. Rejected.

### A.6 Design — the doctrine-tightening + one consistency edit

**(D1) Doctrine (skill).** In SKILL.md and the catalogue, state explicitly that a **GR2028-satisfying**
terminal/wave-exit integration re-run must carry a **union-soundness proof** — *either* a recognized
whole-repo build/test/suite invocation *or* a git-conflict-marker-freedom check. A **contribution-present
check is a complementary tightening layered on top of that proof, never the sole content of the terminal
gate.** Keep presenting contribution-present checks (they are valuable for the #132/#175 overlapping-scope
case) but re-label them as *additive*, and stop implying the #132 union-guardrail satisfies GR2028 *by its
contribution checks alone* — it satisfies GR2028 **because it also carries the conflict-marker check**.

**(D2) Validator error message (harness).** GR2028's message currently says "…re-runs the whole-repo build
/ full suite / a union invariant" without telling the author that a content grep won't count or why. Add a
teaching clause naming the two accepted forms and stating that a content/contribution-present check alone
does **not** satisfy GR2028. This is the single change that would have saved the reporter the trial and
error. Message text only — **no acceptance-logic change.**

**(D3) Consistency edit — drop bare `={7}` from the GR2028 credit regex.** `UnionInvariantConflictMarker`
is `<{7}|={7}|>{7}`, so it still **credits a guardrail that greps only for a bare `=======`** — the exact
middle marker that **#187 retired** (it collides with setext underlines / `====` banners). This is a
latent drift between the validator (credits `=======`) and the #187 doctrine (drop it). Align them: change
the credit regex to `<{7}|>{7}` (credit only the labelled ours/theirs tokens, which the anchored good form
`(?m)^<<<<<<<` still contains) and update SSOT §3.3's form-(2) description to drop `=======`. This edit is
independent of the registry but is where #343 and #346 touch (see §C).

### A.7 Devil's-advocate self-critique (§A)

- **"For genuinely disjoint files there's no possible conflict, so forcing the marker check is
  ceremony."** Strongest counter. Response: (a) the check is cheap and *always honest* — trivially true
  when there's no collision, but not *false* ceremony; a non-FF race can still produce a real 3-way merge
  even with disjoint files when a sibling advances the plan branch between fork and integrate (SSOT §5.3
  case B). (b) The reporter's real grievance is that the marker check felt *meaningless*; the fix for that
  is D1+D2 (teach that it IS the zero-toolchain union-soundness proof), not widening. (c) If a plan truly
  has zero union surface, that is the *trigger* question (A.1.ii), which I decline for stated reasons.
- **"Tightening the doctrine makes GR2028 harder to satisfy for zero-toolchain plans."** No — the
  conflict-marker check is a two-line PowerShell/bash snippet already in the golden `parallel-hello`
  example; the doctrine ships it. The bar is unchanged; only the *misleading license* is removed.
- **"You're privileging one specific check (conflict markers) as canonical — brittle."** It is already
  canonical in SSOT §3.3 and the validator; this design does not introduce that privilege, it *documents*
  it honestly and removes the contradiction.

---

## §B — Issue #346: a data-driven banned-pattern registry (GR2037)

### B.1 What's being asked

#187 fixed the conflict-marker doctrine to the line-anchored form, yet a fresh `/plan-breakdown` later the
same session emitted the OLD unanchored form anyway — proving correct SKILL.md text does not guarantee LLM
compliance. Build a **deterministic** static lint. The maintainer's follow-up generalizes it to a
**data-driven banned-pattern registry**: a versioned file of `{ id, badPattern, reason, goodPatternHint }`
entries; `validate` scans every generated guardrail's own source against every entry and emits one uniform
error; extending coverage = a data-entry PR, not a harness code change. Design the format, location, GR
code, discovery/consumption, seed set, and honest limits; state how it composes with #302 and
guardrails-review.

### B.2 Placement

**Harness (the scan + one new diagnostic) + a new data contract in the SSOT + a seed data file beside the
catalogue.** This is v1-appropriate plumbing (deterministic, plain-file, no new dependency) — it is *not* a
v2 bet. It strengthens invariant #1 (a deterministic net where prose alone was failing).

### B.3 Invariants in play

- **#1 Deterministic over prompt-judges.** The registry is a pure deterministic lint — exactly the
  posture the product preaches. It catches a regression the prose-only doctrine let through.
- **#4 SSOT is schema SSOT.** The registry is a new machine-read contract → its schema + the GR2037
  semantics land in `02-schemas-and-contracts.md` (proposed §4.6) in the same change.
- **#6 Plain files, light setup.** A JSON file embedded in the harness assembly — no DB, no daemon, no
  SaaS. The registry's own quality bar (positive+negative example per entry, meta-tested) keeps it from
  becoming a false-positive source that would violate #1's spirit (a deterministic gate must not
  false-halt correct work).

### B.4 GR code — verified

`DiagnosticCodes.cs` closes with `// CURRENT next-free code: GR2037`. GR2036 (ExpectedDurationNonPositive)
is the last taken. **GR2037 is confirmed next-free.** The new constant is `BannedGuardrailPattern =
"GR2037"`; the marker line is bumped to "CURRENT next-free code: GR2038" in the same change.

### B.5 Design — registry format, location, discovery, scan

**(B5.1) Format — JSON array beside the catalogue.** One file, authored once:
`.claude/skills/plan-breakdown/references/banned-guardrail-patterns.json` (the issue's "alongside the
catalogue"). JSON, not YAML — every other harness data contract (guardrails.json, task.json, sidecars) is
JSON; adding a second serialization format for one file violates KISS. Each entry:

```jsonc
{
  "version": 1,
  "patterns": [
    {
      "id": "#73",                              // the catalogue lesson this enforces
      "badPattern": "Assert\\.\\*\\\\\\([^)]*\\(Moved\\|Written\\|Count\\|Entities\\)",
      "reason": "hollow assertion: matches Assert.Equal(0, x.Count) — a zero-quantity result passes.",
      "goodPatternHint": "require a POSITIVE value: (>\\s*0|>=\\s*1|NotEmpty\\s*\\(|True\\s*\\([^)]*Count\\s*>\\s*0)",
      "mustMatch":    "if ($src -match 'Assert.*\\([^)]*(Moved|Written|Count|Entities)') { }",   // positive fixture — MUST fire
      "mustNotMatch": "if ($src -notmatch '(>\\s*0|>=\\s*1|NotEmpty\\s*\\()') { }"               // negative fixture — MUST NOT fire
    }
  ]
}
```

`mustMatch`/`mustNotMatch` are **inline fixtures** (the maintainer's requested quality bar). A meta-test
(§B.6) asserts every entry's `badPattern` matches its `mustMatch` and does **not** match its
`mustNotMatch`, so a false-positive-generating entry cannot land. The fixtures live *in the registry* (not
a separate test file) so the entry is self-documenting and self-testing — a new lesson is a single
reviewable object.

**(B5.2) Location + loading — authored beside the catalogue, embedded into Core.** The consumer is
`PlanValidator` (in `Guardrails.Core`), which today is a pure library taking a `PlanDefinition` + an
injected `IExecutableProbe`. To keep Core self-contained and testable, **embed the one authored file into
`Guardrails.Core` via an `<EmbeddedResource>` `Link`** from the skill-folder path (MSBuild embeds a file
from outside the project dir). Then:

- The validator loads the registry from its **own embedded resource** — zero runtime path discovery, no
  Core→CLI coupling, robust for the packed global tool.
- `plan-breakdown` / `guardrails-review` already ship the same file as packed skill content (the existing
  `Content Include=".claude/skills/plan-breakdown/**/*"` glob), so the doctrine side can cite it.
- **One authored file, no second copy → no drift** (DRY / SSOT).

*Seam.* Mirror the existing probe injection exactly:

```
PlanValidator()                                     // default: loads the embedded default registry
PlanValidator(IExecutableProbe probe)               // existing
PlanValidator(IExecutableProbe probe, BannedPatternRegistry registry)   // NEW — tests inject a synthetic registry
```

`BannedPatternRegistry` is a small value type (`IReadOnlyList<BannedPattern>` + a `Load()` that
deserializes the embedded resource). This satisfies Dependency-Inversion (validator depends on an injected
abstraction, not a file path) and keeps the check unit-testable without touching the shipped registry.

*Alternative considered + rejected:* a **loose packed file the CLI discovers via `AppContext.BaseDirectory`
and passes in.** The CLI already reads packed skill files (SkillsInstaller), so this is feasible, but it
pushes registry discovery into the CLI and makes `Core`'s validator depend on the caller supplying the
registry — every non-CLI caller (tests, future consumers) must re-implement discovery. Embedding in Core
with a default-loading ctor is simpler and keeps the validator correct by default. (Both approaches ship
the same file in the nupkg, so **neither** gives shipped users a "no re-release" edit path — the "data not
code" benefit is purely *no new C# per lesson*, which both preserve.)

**(B5.3) Discovery + consumption in `validate`.** Add `ValidateBannedGuardrailPatterns` to
`PlanValidator.Validate`, iterating the **same four-folder script enumeration** the existing four-folder
checks use (task `guardrails/` + `preflights/`, wave `guardrails/` + `preflights/`, plan `guardrails/` +
`preflights/`) — uniform with GR2021/GR2027/GR2035/GR2036. For each **script** guardrail (prompt
guardrails are prose, not regex constructions; script *actions* are out of scope for v1):

1. Read the body; **strip whole-line comments** with the existing `StripCommentLines`. This is
   load-bearing: it stops a `# catches:` line that *describes* the banned construction from false-firing —
   itself an application of the #97 comment-blindness lesson to the registry. (Inline trailing-comment
   mentions remain a known minor limit; the catalogue convention puts `catches:` on its own line.)
2. For each registry entry, `Regex.IsMatch(strippedBody, entry.badPattern, CultureInvariant)`.
3. On a match, emit **one** `GR2037` error citing `entry.id`, `entry.reason`, `entry.goodPatternHint`, and
   the file path. Uniform error type, data-driven detail — no bespoke code per lesson.

Regex safety: compile with a bounded match timeout (guard against a pathological registry regex) and treat
a regex-construction error in an entry as a harness/config fault surfaced loudly (the meta-test catches it
first, so it can never reach a shipped build).

### B.6 Which lessons seed it — the honest cut

Assessed each named candidate against the one question that matters: *is it cleanly expressible as a
**banned regex over the generated guardrail's own source text**?*

| Lesson | Shape | Verdict | Why |
|---|---|---|---|
| **#73 hollow assertion** | ban a literal `AVOID` regex the guardrail uses | **SEED (wave 1)** | The catalogue already states the exact `Assert.*\([^)]*(Moved\|Written\|Count\|Entities)` to AVOID — an exact literal construction, highest confidence, lowest FP. |
| **#187a unanchored conflict-marker** | ban the `(^\|[[:space:]])`-anchored marker check | **SEED (wave 1)** | The exact #346 regression string. Distinctive (loose anchor + conflict token together); moderate confidence. Does **not** touch the GR2028 credit regex (see §C). |
| **#112 accessor-order** | ban `NAME\s*\{\s*get` (fixed leading accessor) | **CANDIDATE (wave 2)** | Expressible as a banned construct but FP-prone (a guardrail may legitimately match `{ get`); defer until a real regression, don't front-load FP risk. |
| **#187b bare `=======`** | ban `={7}` as a marker | **DEFER** | Would collide with GR2028's credit regex; only coherent *after* §A.4's `={7}` drop. #187a already covers the real regression. |
| **#97 / #98 comment-blind scan** | require comment-strip before a banned-keyword scan | **NOT A BAN** | The defect is the **absence** of a comment-strip step — a whole-script structural property, not a banned substring. A "required-pattern-conditional-on-trigger" is a rules engine, not a flat ban. Stays on guardrails-review + #302. |
| **#175 duplicate-def** | require a duplicate-def count check when two tasks define into a shared code file | **NOT A BAN** | This is a **positive/required** lesson keyed on plan topology (overlapping writeScope + code file) — the inverse polarity of a ban. A banned-regex-over-source can't detect a *missing* check. (I disagree with the comment's inclusion of it as a ban candidate; it belongs with the §A-style positive axis / guardrails-review.) |
| **#120 composition-root, #40 framework-selection, any semantic-code lesson** | — | **NOT EXPRESSIBLE** | Need semantic/structural understanding of the generated **code** (is the collaborator actually wired? is the right framework used?), not of the guardrail script's text. Stays on the #302 smoke-test + guardrails-review. |

**Net seed: #73 + #187a.** Small, high-signal, both meta-tested. This honors the maintainer's "scoped
honestly to the banned-regex-construction subset … not a cure-all."

**Honest brittleness (state it plainly).** Because the registry matches *regex text inside guardrail
source*, escaping varies and a determined LLM can respell around a given `badPattern` (e.g. drop one
alternation term). The registry is **defense-in-depth against regression of a known-bad spelling**, not a
proof. That is exactly its claimed scope.

### B.7 Composition with #302 and guardrails-review — complement, not replacement

Three layers, each catching what the others cannot; the registry is the cheapest and most reliable for its
**narrow** subset:

- **Banned-pattern registry (this design) — deterministic, zero-cost, load-time.** Catches a *known-bad
  regex construction* in guardrail source. Fires every run with no tokens. **Only** the banned-spelling
  subset.
- **#302 author-time smoke-test — deterministic, runtime.** Renders/executes the guardrail against
  hand-synthesized pass/fail fixtures. Catches wrong **logic** (a guardrail that never fires, a wrong
  target path) — including cases the registry can't see because they aren't about regex spelling at all.
- **`guardrails-review` — adversarial, semantic, token-costing.** Catches **missing** guardrails, wrong
  archetype, the #120/#40/semantic-code classes, and any respelled evasion of the registry. The "cheapest
  wrong implementation that passes" voice.

The registry does not replace either: it removes the *regression-of-a-fixed-spelling* failure mode from
the review/#302 load, so those layers spend their budget on the harder, non-mechanical checks. `#346`'s
own limits section says exactly this; the design adopts it verbatim.

### B.8 Devil's-advocate self-critique (§B)

- **"A respell defeats it, so it's theater."** It defeats *evasion by an adversary*; it does not defeat
  *accidental regression*, which is the actual #346 failure mode (a fresh generation innocently reverting
  to the old spelling it saw in training). Guarding accidental regression deterministically is real value;
  the honesty is in not overclaiming (B.6).
- **"The registry becomes its own false-positive source."** The mandatory inline `mustMatch`/`mustNotMatch`
  + the meta-test (a new entry can't land without proving it fires on the bad case and stays silent on the
  good case) is the direct mitigation. A deterministic gate that false-halts correct work violates
  invariant #1's spirit; the fixture bar is non-negotiable.
- **"Only two seed entries — is the whole registry worth the plumbing?"** The plumbing is small (one
  four-folder loop + one embedded JSON + one diagnostic) and the value is *marginal cost per future
  lesson ≈ one JSON object + two fixtures*. The alternative — a bespoke GR20xx + validator method + tests
  per lesson — is what the maintainer explicitly rejected as non-scaling. Two entries today, cheap growth
  tomorrow, is the right trade.
- **"Embedding a skill-folder file into Core is an odd dependency direction."** Acknowledged; the
  alternative (CLI-side discovery) is documented and rejected for pushing discovery into every caller. The
  embed-via-Link keeps one authored file where the doctrine lives while giving the validator a robust load
  path. If the direction proves awkward in implementation, the fallback is to author the SSOT copy in Core
  and generate the skill-folder copy at build — noted for the implementer.

---

## §C — The interaction: one mechanism or two?

**Recommendation: TWO mechanisms, kept separate; they intersect at exactly one regex.**

GR2028 and the registry are different in every structural axis:

| Axis | GR2028 (#343) | Registry (#346) |
|---|---|---|
| Polarity | **positive** — must contain a union-soundness proof | **negative** — must not contain a banned construction |
| Quantifier | **∃ over a folder** — ≥1 file satisfies it | **∀ over files** — every file must be clean |
| Trigger | **conditional** on plan topology (leaf-count ≥2 / fan-in, worktree mode) | **unconditional** — every script guardrail, every plan |
| Scope | one specific folder's aggregate (`<plan>/guardrails/`, per wave) | all four folders, per file |
| Detail | fixed message | data-driven (which entry fired) |

Collapsing these into one "pattern registry with a polarity flag" would force the flat registry to also
carry a per-entry **scope** (which folder), a per-entry **trigger** (the topology condition), and a
per-entry **quantifier** (∃-in-folder vs. ∀-over-files). That is no longer a data-driven list — it is a
rules engine, and it would complicate the one thing the registry is good at (a flat "scan every script for
every bad regex, emit one error"). **KISS/YAGNI: keep the registry ban-only; keep GR2028 a separate
positive, topology-conditional, folder-existence check.** Do **not** add a "required-pattern axis" to the
registry.

**Where they touch — the one shared edit.** The conflict-marker regex is the single point of contact:

- GR2028's **credit** regex (`UnionInvariantConflictMarker = <{7}|={7}|>{7}`) still credits a bare
  `=======` check.
- #187's **doctrine** retired bare `=======`.
- A future registry entry #187b that **banned** bare `=======` would then make the same line
  simultaneously *credited* (GR2028) and *banned* (GR2037) — incoherent.

The resolution keeps the two mechanisms separate while removing the incoherence: **drop bare `={7}` from
GR2028's credit regex under #343 (§A.4, D3)** as a doctrine-consistency fix, *independent of whether the
registry ever bans `=======`*. Then seed the registry with **#187a** (the unanchored `(^|[[:space:]])`
construction — the real regression), which does not touch the credit regex at all. Result: two clean,
separate mechanisms; one coordinated regex edit that belonged in #343 regardless.

**Sequencing across the two issues:** land **#343 first** (doctrine + error message + the `={7}` drop —
aligns the validator with #187), then **#346** (registry + GR2037, seeded #73 + #187a) on top. If they
ship together, order within the PR set doesn't matter; if separate, #343 first avoids a transient window
where the registry could contradict the credit regex.

---

## Implementation handoff

Per the #106 loop, **this design-of-record goes out as a draft PR for inline human review before any
implementation milestone starts.** The doc is on a branch; the user opens the draft PR (this agent's
boundary forbids opening it). Only after review-comments are addressed do the milestones below begin.

### Milestone 1 — #343 doctrine + error message + credit-regex alignment
- **`guardrails-skill-author`** — `filesTouched`: `.claude/skills/plan-breakdown/SKILL.md`,
  `.claude/skills/plan-breakdown/references/guardrail-catalogue.md`. Re-label the contribution-present
  shape as *additive*; state that GR2028 requires a union-soundness proof (build/test **or**
  conflict-marker), and that the #132 union-guardrail satisfies GR2028 **via its conflict-marker check**.
- **`guardrails-harness-developer`** — `filesTouched`:
  `src/Guardrails.Core/Loading/PlanValidator.cs` (GR2028 message text: add the two-accepted-forms teaching
  clause; drop `={7}` from `UnionInvariantConflictMarker`), `docs/plans/02-schemas-and-contracts.md`
  (§3.3 form-(2) description: drop `=======`).
- **`guardrails-test-author`** — `filesTouched`: `tests/**` — assert a content-only union check is
  rejected with the improved message; assert a bare-`=======`-only check is **no longer** credited by
  GR2028 (regression-guards D3).
- **Sequencing:** message + doc together (contract edit in the same change, invariant #4).

### Milestone 2 — #346 registry (after M1)
- **`guardrails-skill-author`** — `filesTouched`:
  `.claude/skills/plan-breakdown/references/banned-guardrail-patterns.json` (new — the seed file: #73 +
  #187a, each with inline `mustMatch`/`mustNotMatch`), catalogue cross-references from the #73 and #187
  sections pointing at GR2037.
- **`guardrails-harness-developer`** — `filesTouched`:
  `src/Guardrails.Core/Loading/DiagnosticCodes.cs` (add `BannedGuardrailPattern = "GR2037"`; bump the
  next-free marker to GR2038), `src/Guardrails.Core/Loading/PlanValidator.cs` (new
  `ValidateBannedGuardrailPatterns` over the four-folder **script** enumeration, comment-stripped body,
  bounded-timeout regex, injected `BannedPatternRegistry`), a new
  `src/Guardrails.Core/Loading/BannedPatternRegistry.cs` (+ record `BannedPattern`),
  `src/Guardrails.Core/Guardrails.Core.csproj` (`<EmbeddedResource>` `Link` to the seed file),
  `docs/plans/02-schemas-and-contracts.md` (new §4.6 — registry schema + GR2037 semantics + scan scope +
  fixture rule).
- **`guardrails-test-author`** — `filesTouched`: `tests/**` — the **meta-test** (every entry fires on its
  `mustMatch`, stays silent on its `mustNotMatch`, and every `badPattern` is a valid regex); a validator
  test that a seeded bad construction in each of the four folders raises GR2037 with the entry's id/reason;
  a negative test that a clean guardrail passes.
- **Sequencing:** registry file + loader + schema together; meta-test gates the seed set.

---

## Proposed plan-document edits

I propose; the user approves; then I apply.

1. **New file (this doc):** `docs/plans/15-guardrail-script-lint.md` — the design-of-record above.
2. **`docs/plans/02-schemas-and-contracts.md` §3.3** — in the form-(2) description (≈lines 491–498),
   change the conflict-marker token list from `<<<<<<<`/`=======`/`>>>>>>>` to `<<<<<<<`/`>>>>>>>` and add
   one sentence: *"The bare `=======` middle marker is **not** credited (retired by #187 — it collides
   with setext underlines / `====` banners); the labelled ours/theirs tokens are the union-soundness
   signal."* (This documents §A.4/D3.)
3. **`docs/plans/02-schemas-and-contracts.md` §4 — new §4.6 "Banned guardrail-script patterns
   (validated, GR2037 — error)"** — define: the registry file
   (`references/banned-guardrail-patterns.json`), the `{ id, badPattern, reason, goodPatternHint,
   mustMatch, mustNotMatch }` entry schema, the scan (four-folder **script** guardrails, comment-stripped
   body, per-entry `Regex.IsMatch`, one GR2037 per match), the inline-fixture quality bar + meta-test, and
   the honest-limits/composition note (complement to #302 + guardrails-review). Renumber nothing else;
   append after §4.5.
4. **`guardrails-domain-knowledge` skill** — add GR2037 to the contract quick-reference and one line under
   the guardrails bullet: *"GR2037 — a generated guardrail script contains a banned regex construction
   (data-driven registry beside the catalogue); complements #302 + guardrails-review, does not replace
   them."* (Self-updating clause of that skill, once §4.6 lands.)
5. **`docs/plans/03-roadmap.md`** — no v2 bet is created; add a one-line note that the registry is v1
   deterministic plumbing (not the CI-mode bet), if a cross-reference is wanted. Optional.

**No change** to the GR2028 *acceptance logic* (leaf-count trigger untouched; credit forms unchanged
except the `={7}` drop), to the four-folder model, or to any other contract.
