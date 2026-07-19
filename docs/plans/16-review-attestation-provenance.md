# 16 — Review-attestation provenance (making the review marker trustworthy — or its trust honestly bounded) — design of record (issue #366)

> **Status: PROPOSED — design only, no code.** This DoR makes the review marker
> (`state/guardrails-review.json`) carry **provenance** so a future runtime boundary can gate on it
> *meaningfully* — while stating plainly the ceiling a plain-file model imposes (invariant 6): **you cannot
> prove a human reviewed; you can only make a forge costlier, more visible, recorded, and no cheaper than
> the honest escape hatch.** Delivered in two phases: **Phase 1 (v1)** is additive + fully back-compat
> (schema field + a review-report artifact + a `mark-reviewed` that records what it saw); it changes **no**
> runtime behavior and **unblocks nothing on its own** — it lays the anchor. **Phase 2 (v2 bet, owned by
> #361 Open K)** is the `autonomy.reviewGate: "enforce"` runtime halt, honestly characterized as
> **"best-effort + recorded," not "unforgeable."**
>
> **SSOT coordination.** The marker is a contract; its home is `02-schemas-and-contracts.md` §13 (SSOT,
> invariant 4). A parallel branch is in flight on §13, so this doc does **not** edit §13 — it **proposes the
> exact §13 delta by name** (§12 below) for the SSOT author to apply in the same change that lands the
> harness work. Where this doc and §13 ever differ, §13 wins for the wire contract; this doc owns the
> rationale, the threat model, and the phasing.
>
> **Numbering note (for the lead).** The task brief named this `13-…`, but `13-/14-/15-` are already
> occupied on master (`13-merge-on-success-default.md`, …). This doc takes the next free prefix, **16**.
> Renumber if you intended otherwise.

---

## 1. What it is, and the pain

`/guardrails-review` records that a human ran the adversarial pass over the current plan by writing a
committed marker `state/guardrails-review.json` (SSOT §13, `ReviewMarker.cs`):

```jsonc
{ "version": 1, "reviewedAt": "2026-06-22T14:03:11Z", "planHash": "sha256:…" }
```

`planHash` is `PlanDefinitionHash.Compute(plan)` (§7.3) — a **pure function of the readable plan files**.
`guardrails mark-reviewed <folder>` writes this marker for **any structurally-valid plan, with no human
check and no authorization** (`MarkReviewedCommand.Run`: it loads the plan, refuses only on *parse/schema
errors*, then calls `ReviewMarker.Write` unconditionally).

The consequence, stated exactly: **a marker whose `planHash` matches the current `PlanDefinitionHash` proves
that _a marker exists_, not that _a human reviewed_.** Anyone (or any agent) with write access to the plan
folder can synthesize a "Reviewed" attestation — run `mark-reviewed`, or write the three-field JSON directly.
Both the hash *input* (readable files) and the hash *function* are available to any process with tree access.

### Why it is latent today, and why it stops being latent

Today the marker only drives **GR2025 — a warning** (`ReviewMarker.Evaluate` → `NudgeMessage`; the Scheduler
never gates a run on it). So it is **not a security boundary today** and the forgeability is benign. The pain
is entirely **prospective**: every feature that would **promote the marker into a runtime boundary** inherits
a forgeable anchor. Two are already on the table:

- **Autonomous mode (#361, draft PR #364).** The answer-injection design originally accepted a
  `review-attested` answer that pointed at a marker and re-verified the hash. The adversarial pass showed
  this only proves *a marker exists* — and worse, the forge path was **cheaper than the honest
  `proceed-unreviewed` opt-in**: a forged-reviewed wave exits `0`, keeps `mergeOnSuccess` **ON** (so it
  auto-delivers, #340), carries **no** "ran unreviewed" reporting flag, and never trips the GR2040
  compound-config gate. `review-attested` was therefore **dropped** from #361 v1.
- **Open K — "promote GR2025 to a runtime halt"** (autonomous-mode, deferred). Same inheritance: a runtime
  gate on a forgeable file gates on nothing.

**#366 is the design that turns the marker into something a runtime gate can honestly rest on** — or bounds
its trust so plainly that Open K's enforce-mode ships with its limits named. Open K is **explicitly gated on
this doc**; this doc is what unblocks it.

## 2. Placement

**Schema + harness + skill + docs — Phase 1 is v1; enforce-mode is a named v2 bet.**

| Concern | Where | Phase |
|---|---|---|
| The marker contract (new `attestation` block) | **Schema** — §13 delta (proposed by name, §12) | 1 (v1) |
| `mark-reviewed` records evidence-class; `ReviewMarker` read tolerance | **Harness** — `MarkReviewedCommand.cs`, `ReviewMarker.cs` | 1 (v1) |
| `/guardrails-review` leaves a durable review-report artifact | **Skill** — `guardrails-review/SKILL.md` Step 6/7 | 1 (v1) |
| Stop calling the review floor "unforgeable"; state the write-access ceiling | **Docs** — §13 + this doc + skill | 1 (v1) |
| `autonomy.reviewGate: "enforce"` runtime halt | **v2 bet** — owned by #361 **Open K**, not scheduled here | 2 (v2) |

Phase 1 is deliberately **behavior-inert**: it adds a field, an artifact, and a recorded classification, and
changes **no** exit code, no gate, no default. It is the anchor a later enforce-mode needs. That separation is
the whole point — the anchor is cheap and reversible; the boundary is a deliberate v2 decision.

## 3. The honest threat model + trust boundary (read this before the schema)

Invariant 6 is load-bearing here: **plain files, light setup — no databases, daemons, or SaaS.** In that
model the firstmate/agents have **write access to the plan tree by construction** — that is *how* the plan is
authored and reviewed. This has three hard consequences that no schema in this doc escapes:

1. **Any field the marker gains, an adversary with write access can also write.** `attestation.source:
   "review-artifact"` is a string; an agent can type it.
2. **Any artifact `mark-reviewed` requires, an adversary can also fabricate.** A review-report file is bytes;
   an agent can write plausible bytes.
3. **Any digest chain is computable by anyone with read access** — exactly the property that makes `planHash`
   forgeable. A `reportDigest` proves "a report with this digest exists," not "a human wrote it." It is the
   `planHash` problem again, one level up.

There is **no secret** in a plain-file / no-daemon / no-SaaS model that the honest `/guardrails-review`
session holds but a forging agent does not. **Therefore this design cannot cryptographically prove a human,
and does not claim to.** (The one mechanism that *would* raise the bar to "needs a private key" —
GPG-signed commits — is discussed and deferred in §11 OD-7: it breaks *light setup* and does not fit
autonomous crews where the machine holds the key anyway.)

**The trust boundary, stated plainly:**

> The review floor is exactly as strong as **write-access to the plan folder** — and no design that stays
> inside invariant 6 changes that. Provenance cannot make the marker unforgeable.

**What provenance nonetheless buys** (all real, all bounded — this is the case for building it):

- **A → It distinguishes "a real review pass ran" from "a bare marker was written."** Today those are
  byte-identical. After Phase 1, a marker records *which class of evidence it rests on* (`review-artifact`
  vs `bare` vs `machine` vs `legacy`) — deterministically, no judge in the loop.
- **B → It raises the cost and visibility of a forge.** A forge is no longer a one-line JSON write; to reach
  the strongest class it must fabricate a plausible *review report* too. That is a larger, inspectable lie a
  PR reviewer or an audit can catch — forgery moves from *trivially invisible* to *detectable-in-principle by
  inspection*.
- **C → It closes the asymmetry that motivated #366.** The adversarial finding was that a forged-reviewed run
  was **cheaper and quieter** than the honest `proceed-unreviewed` hatch. Provenance + a *recorded* enforce
  decision (§6) makes a forged-green run **at least as costly and as visible** as `proceed-unreviewed`: the
  gate records exactly what evidence it trusted, so the forge leaves an audit trail naming its own lie.

**What it cannot buy:** proof of a human; prevention of a determined forger; a boundary that survives
write-access. If the maintainer judges bounded value B/C not worth the ceremony, the honest fallback is
Phase 1(a) **alone** — the docs-honesty fix (issue #366 option 4), which costs almost nothing and still
removes the "unforgeable" overclaim.

## 4. Marker-provenance schema — the concrete delta

**Additive. Back-compat by construction** (`ReviewMarker.ReadOptions` is
`PropertyNameCaseInsensitive` with **no** `UnmappedMemberHandling.Disallow`, so System.Text.Json **ignores
unknown members** — confirmed against the same discipline `JournalJson` uses). A pre-provenance marker reads
as `legacy`; a newer marker read by an older tool has its `attestation` block silently ignored and behaves
exactly as a v1 marker.

```jsonc
{
  "version": 2,                              // bump; readers NEVER gate on version (see rules)
  "reviewedAt": "2026-06-22T14:03:11Z",      // UNCHANGED
  "planHash": "sha256:…",                    // UNCHANGED — PlanDefinitionHash (§7.3), wire name kept
  "attestation": {                           // NEW, optional block; absent ⇒ read as `legacy`
    "source": "review-artifact",             // evidence class: review-artifact | bare | machine
    "tool": "guardrails 1.0.0-preview.43",   // self-reported: the CLI build that stamped it (informational)
    "actor": "david.maltby@hotmail.com",     // OPTIONAL, SELF-REPORTED, NON-AUTHORITATIVE reviewer id
    "evidence": {                            // present ONLY for source: "review-artifact"
      "reportPath": "state/reviews/review-1a2b3c4d5e6f-2026-06-22T140311Z.md",  // relative to plan folder
      "reportDigest": "sha256:…"             // sha256 of the report bytes (newline-normalized) at stamp time
    }
  }
}
```

### The `source` enum — named for what the CLI can actually verify

The issue floats `attestationSource` as *human `/guardrails-review` vs `mark-reviewed`-bare vs machine*.
Sharpen that honestly: **the CLI cannot authenticate the actor** — a human and a machine invoke the *same*
`mark-reviewed`. What it *can* verify deterministically is **evidence class**. So `source` is the
evidence class, and the actor is a separate, clearly-labeled self-report:

| `source` | Meaning (what the CLI verified) | Written when |
|---|---|---|
| `review-artifact` | A `/guardrails-review` report artifact was **present and digested** at stamp time. Strongest class. | `mark-reviewed` found a report for the current `planHash` (or `--evidence <path>`). |
| `bare` | `mark-reviewed` invoked with **no** review artifact — the current unconditional behavior; the human's manual "I read it" confirmation. | `mark-reviewed <folder>` with no artifact present. |
| `machine` | Explicitly stamped by an **automated** flow (auto-breakdown / autonomous mode). **Never masquerades** as human review. | `mark-reviewed <folder> --source machine`. |
| `legacy` | **Read-time only, never written.** A marker with no `attestation` block (a v1 marker, or `version` present but block absent). | — |

### Field rules (for the harness author)

- **`evidence` is present iff `source == "review-artifact"`.** `reportPath` is plan-folder-relative and lives
  under `state/reviews/` — which §7.3 **excludes** from `PlanDefinitionHash`, so the report **cannot re-stale
  the marker** (no circularity, mirroring why the marker itself lives under the excluded `state/`).
  `reportDigest` binds marker↔report at stamp time (see §6 for re-verify).
- **`actor` and `tool` are informational and self-reported** — never authoritative, and any surfacing MUST
  label them so (e.g. `reviewer (self-reported): …`). They exist for audit-trail richness (a name to ask),
  not trust.
- **Readers never gate on `version`.** Classification is by **presence of the `attestation` block + its
  `source`**, never by the integer. Bumping to `2` is a signal, not a gate — an older reader that ignores
  `version` and a newer reader both work. (This preserves the current behavior: `ReviewMarker.Read` today
  deserializes `version` but never branches on it.)
- **`ReviewMarker.Read` stays tolerant** — a present-but-unparseable marker still reads as `null` (→
  `Missing`), never throws. The new block is optional; a marker with `attestation` malformed but the top
  three fields intact should still deserialize (the block deserializes to `null` → `legacy`), matching the
  tolerant manifest/journal readers.

### The review-report artifact (what `/guardrails-review` must now leave behind)

**Today the review pass leaves _nothing_ durable.** `/guardrails-review` Step 6 prints a findings table to
the conversation; the *only* on-disk trace a review ever happened is the marker itself — which is precisely
the circularity #366 names. For provenance to key on evidence, the skill must **write** that evidence.

- **Home:** `state/reviews/review-<planHashShort>-<reviewedAtCompact>.md` — committed alongside the marker,
  under the hash-**excluded** `state/` tree. It is a **plan artifact** (like the marker), not per-run runtime
  state — so it belongs under `state/`, not `logs/` (which is per-`runId`, and `--fresh`-wiped; a review has
  no `runId`). See §11 OD-5.
- **Content:** the Step 6 findings table + verdict (blockers addressed/declined) + the `planHash` it attests
  to. Human-readable; its value as evidence is that a forge must now *fabricate* it, and a reviewer/audit can
  *read* it.
- **Binding:** the skill writes the report, then calls `mark-reviewed`, which digests the report and records
  `evidence.reportDigest` + `reportPath`. The skill can't compute `PlanDefinitionHash` itself (it delegates to
  the CLI, per §13) — same division of labour, extended: **skill writes evidence, CLI digests + stamps.**

## 5. The `mark-reviewed` change (and what stays back-compat)

Today `mark-reviewed` writes unconditionally after a structural-validity check. The change is **additive and
non-breaking** — the human's real workflow keeps working:

- **`mark-reviewed <folder>` (bare, no artifact):** stamps `source: "bare"`, clears GR2025 **exactly as
  today**. The shipped manual-confirmation flow (a human reads the plan, runs `mark-reviewed`) is
  **unchanged** — it just now records that it was a bare stamp.
- **`mark-reviewed <folder>` with a review report present** (the new `/guardrails-review` flow) **or
  `--evidence <path>`:** digests the report, stamps `source: "review-artifact"` + `evidence`.
- **`mark-reviewed <folder> --source machine`:** stamps `source: "machine"` — for auto-breakdown / autonomous
  flows (#360/#361) that mark a wave reviewed without a human. **A machine flow must use this** so it never
  masquerades as `review-artifact`. (This is the honest counterpart to #361's rule that machine-decided work
  defaults `mergeOnSuccess` OFF.)
- **`mark-reviewed <folder> --reviewer <id>`:** records the self-reported `actor` (optional).

**Design decision — `mark-reviewed` does NOT refuse a bare stamp.** An alternative was "refuse to stamp
unless a review artifact exists." Rejected for Phase 1: it would break the shipped flow and violate the
warn-never-block invariant for a benefit that only matters under a *future* enforce-mode. Instead, **the
distinction is recorded, not enforced at mark-time** — a `bare` marker still clears the warning; only
enforce-mode (§6) ever treats `bare` as insufficient. This keeps invariant 5 (honest halts, never a
surprise block) and the human's workflow intact, while still laying the anchor.

## 6. How this unblocks Open K — enforce-mode is *best-effort + recorded*, not *unforgeable*

Open K = "promote GR2025 from a warning to a runtime halt." Concretely, a future
`autonomy.reviewGate: "enforce"` (v2, owned by #361). **The minimum provenance that lets that gate mean
something:**

> Enforce-mode honors a marker **iff all three hold**:
> 1. `ReviewMarker.Evaluate` returns `Reviewed` (recorded `planHash` == current `PlanDefinitionHash` — §13
>    staleness, **unchanged**);
> 2. `attestation.source == "review-artifact"`; **and**
> 3. the recorded `evidence.reportDigest` **re-verifies** against the on-disk report at `reportPath`.
>
> A `bare` / `machine` / `legacy` marker, a hash mismatch, or a digest that no longer verifies **does not
> satisfy enforce-mode** → the run **halts** (control-flow halt + **distinct non-zero exit**, mirroring
> #361's floor-3 characterization — the review gate is a control-flow halt, *not* a deterministic guardrail
> verdict, so this stays aligned with invariant 3), **or** takes the honest `proceed-unreviewed` hatch (its
> distinct exit + `mergeOnSuccess` **OFF** + reporting flag).

**And — the load-bearing half — enforce-mode RECORDS what it trusted.** When it honors a marker it appends to
the journal `decisions[]` (`boundary:"review-gate"`) the exact evidence: `source`, `reportPath`,
`reportDigest`, and the self-reported `actor`. So a forged-green run is **auditable and at least as visible as
`proceed-unreviewed`** — it leaves a trail naming precisely the evidence it rested on. This is what closes the
#366 asymmetry (§3-C).

**Honest limitation, stated in the DoR so it ships named:** this is **best-effort + recorded, NOT
unforgeable.** A forger with write access can still write `source: "review-artifact"`, fabricate a report, and
record a matching digest — everything is forgeable in a plain-file model (§3). What enforce-mode buys is the
three bounded gains of §3 (distinguish/cost/recorded-symmetry), *not* a security boundary. **Open K's
enforce-mode must therefore be documented as "best-effort + recorded."** A runtime gate on a plain committed
file is a **speed bump with an audit trail**, not a lock. If that is not strong enough for a given crew, the
real boundary is out-of-model (GPG-signed commits, §11 OD-7) — and even that does not fit machine-held-key
autonomous crews.

## 7. Migration / back-compat

- **Existing `{version:1, reviewedAt, planHash}` markers keep working, read as `legacy`.** The absent
  `attestation` block deserializes to `null`; `Evaluate` still classifies `Missing`/`Stale`/`Reviewed` purely
  by the `planHash` compare (**§13 staleness unchanged**). GR2025 is unchanged (warn on missing/stale). A v1
  marker on an unedited plan reads `Reviewed` + `legacy` — so in **warn-mode it still clears the nudge** (no
  user is worse off), and only a *future* enforce-mode treats `legacy` as insufficient.
- **`PlanDefinitionHash` staleness semantics (§13, GR2025) are unchanged.** No new input, no re-hash. The
  review-report artifact lives under the hash-**excluded** `state/` tree, so it introduces no re-stale loop.
- **Older tools reading a newer (v2) marker do not break** — unknown members are ignored
  (`ReviewMarker.ReadOptions`), so a preview.42 tool reading a preview.43 marker still reads the three v1
  fields and behaves identically. Forward- and backward-compatible.
- **No forced re-review.** Unlike the #260 `PlanHash`→`PlanDefinitionHash` widening (which re-staled every
  prior marker once), this change does **not** touch the hash, so **no existing marker re-stales**. Provenance
  is gained lazily: the next `/guardrails-review` upgrades a plan from `legacy` to `review-artifact`.

## 8. Invariants in play

- **#6 (plain files, light setup — no daemon/SaaS).** The **binding** constraint and the source of the
  residual. It is *why* a human can't be proven; the whole design stays inside it (committed files only, no
  secret store). Respected — and named as the ceiling. *Strained:* the design is honest that #6 caps it at
  "best-effort + recorded."
- **#5 (honest halts; nothing marked done unverified; needs-human is a feature).** Directly served: a
  `bare`/`legacy` marker is honestly *labeled unverified* rather than silently equated with a real review;
  enforce-mode halts honestly (or takes the recorded hatch). Strengthened.
- **#4 (§02 is the SSOT; a contract change lands there in the SAME change).** In play — the marker is a
  contract. Honored in spirit via a **verbatim §13 delta proposed by name** (§12), to be applied by the SSOT
  author in the same change as the harness work (a coordination exception because a parallel §13 branch is in
  flight — see the Status note).
- **#1 (deterministic guardrails over prompt-judges; judges never alone).** The attestation *classification*
  and the *digest re-verify* are deterministic reads — no judge decides "was this reviewed." Respected.
  *Honest strain:* whether the review was a *good* review is inherently a human act; provenance records *that*
  an artifact exists, never *that it was good* — named, not papered over.
- **#3 (verdicts from verdict files, not exit codes).** Tangent but aligned: enforce-mode's halt is a
  control-flow halt with a distinct non-zero exit (per #361 floor-3), **not** a guardrail verdict — the marker
  is an attestation, not a verdict file, and the design keeps that separation.

## 9. Devil's-advocate self-critique

**DA-1 — "This is security theater. A forger writes `source: review-artifact` + a fake report + a matching
digest in one commit. You added ceremony, not safety."** *The strongest objection, and conceded in full:* it
does **not** prevent forgery, and this doc says so in three places (§3 trust boundary, §6 honest limitation,
Status). What it changes is not *preventability* but (B) the **cost/visibility** — the forge is now a
multi-file fabrication a reviewer/audit can catch, not a one-line JSON — and (C) the **asymmetry** the issue
was filed over: a forged-reviewed run was *cheaper and quieter* than `proceed-unreviewed`; provenance + a
*recorded* enforce decision makes it at least as costly and visible. The value is **bounded and named, not
oversold.** And there is a graceful floor: if the maintainer judges even B/C not worth the ceremony, Phase
1(a) alone — the docs-honesty fix (issue option 4) — still removes the overclaim at near-zero cost.

**DA-2 — "The digest re-verify is pointless: `state/` is excluded from `PlanDefinitionHash`, so an attacker
edits the report freely."** The digest is recorded *in the marker*; enforce-mode recomputes the report's
digest and compares to the recorded value, so editing the report **after** stamping breaks the binding
(detected). Of course the attacker can re-stamp both — back to DA-1's residual. The digest binds report↔marker
*at stamp time*; it makes neither unforgeable. Named.

**DA-3 — "`bare` markers become second-class; honest hand-`mark-reviewed` users get downgraded."** In
warn-mode (Phase 1) **nothing changes** — `bare` clears GR2025 exactly as today. Only a *future*, opt-in
enforce-mode (autonomous crews) distinguishes them — and that is the point: an unattended run *should* demand
stronger evidence than a bare stamp. Interactive humans on warn-mode are untouched.

**DA-4 — "Self-reported `actor` is worse than nothing — it looks authoritative but isn't."** It must be
labeled non-authoritative wherever surfaced (schema doc + any display). Its only value is audit richness. If
the maintainer thinks it invites false confidence, **drop it** — it is the most optional field (OD-2).

**DA-5 — "Making `/guardrails-review` write a committed artifact pollutes the plan folder / git history."**
Small, under `state/reviews/`, committed like the marker already is, hash-excluded (no re-stale loop). Same
*class* of artifact as the marker — a committed attestation — and it is the **only** durable record a review
ever happened (today there is none). A proportionate cost.

**DA-6 — "Why not just sign the marker and make it truly unforgeable?"** A signature needs a **secret**. In a
no-daemon/no-SaaS model any secret the honest review session holds is also available to a forging agent with
tree access — there is no trusted key store. The one real anchor is a **GPG-signed commit**, but it (a) signs
the *commit*, not the marker semantics, (b) needs human GPG setup (breaks *light setup*, invariant 6), and (c)
does not fit autonomous crews where the machine holds the key anyway. Floated and deferred as OD-7 — named as
the real boundary precisely so the doc doesn't pretend the plain-file version is one.

**The residual, plainly:** *You cannot beat write-access in a plain-file model.* Provenance buys cost,
visibility, an audit trail, and symmetry-with-the-honest-hatch — **never proof.** Everything above lives
inside that ceiling, and the design's honesty about it is the deliverable as much as the schema is.

## 10. Phasing

**Phase 1 — v1, ships now. Additive, back-compat, behavior-inert. Lays the anchor.**
1. **(a) Docs-honesty (the standalone floor, issue option 4).** Remove any "unforgeable" framing of the review
   floor from §13, this doc, and the skill; state it is only as strong as write-access to the plan folder.
   *This alone is a shippable minimum if the maintainer stops here (OD-1).*
2. **(b) Schema:** add the additive `attestation` block (§4) — §13 delta (§12).
3. **(c) Skill:** `/guardrails-review` writes the review report to `state/reviews/…` (§4) and passes it to
   `mark-reviewed`.
4. **(d) Harness:** `mark-reviewed` auto-detects/digests the report → `review-artifact`; bare → `bare`;
   `--source machine`; `--reviewer`. `ReviewMarker` gains the `attestation` record, stays read-tolerant.
5. **(e) All still warn-never-block** — GR2025 unchanged; **no** runtime gate. Fully reversible.

**Phase 2 — v2 bet, owned by #361 Open K. NOT scheduled here.** The `autonomy.reviewGate: "enforce"` runtime
halt (§6): honors only a `review-artifact` marker whose digest re-verifies; records the trusted evidence into
`decisions[]`; a non-qualifying marker halts (distinct exit) or takes `proceed-unreviewed`. Documented as
**best-effort + recorded, not unforgeable.** Its go/no-go is a #361/roadmap decision; this doc only defines
the **minimum provenance** that makes it meaningful.

## 11. Open decisions — for the maintainer

- **OD-1 — Scope of Phase 1.** Ship full Phase 1 (b–e), or **only** 1(a) the docs-honesty fix?
  *Recommendation:* full Phase 1 — the anchor is cheap, back-compat, and unblocks Open K later; but 1(a) alone
  is a legitimate, near-zero-cost stopping point if you want to defer the schema.
- **OD-2 — Keep or drop the self-reported `actor` field?** *Recommendation:* keep, **clearly labeled
  non-authoritative** (audit richness). Drop if you judge the false-confidence risk (DA-4) higher than the
  audit value.
- **OD-3 — `version` bump.** Bump to `2`, or stay `1` and detect legacy purely by absent `attestation` block?
  *Recommendation:* bump to `2` **with the invariant "readers never gate on version."** Signal, not a gate.
- **OD-4 — Enum naming.** My evidence-class `review-artifact` / `bare` / `machine` (+ read-time `legacy`), or
  the issue's actor-flavored `human` / `cli-bare` / `machine`? *Recommendation:* evidence-class — the CLI can
  verify *evidence*, not *actor*; naming the enum for the unprovable actor re-imports the overclaim #366 was
  filed against.
- **OD-5 — Review-report home.** `state/reviews/` (committed, hash-excluded, durable — my recommendation) vs
  `logs/` (per-`runId`, `--fresh`-wiped — a poor fit; a review has no `runId`). *Recommendation:*
  `state/reviews/`.
- **OD-6 — Enforce-mode's treatment of `legacy`.** Treat a `legacy` marker as insufficient (halt with a
  "re-run review to upgrade" message — my recommendation; grandfathering re-opens the exact forge hole) vs
  grandfather it (less migration friction, weaker floor). A Phase-2 call; flagged now because it shapes the
  migration story.
- **OD-7 — GPG-signed commit as an optional "real boundary" tier.** Float it as an out-of-v1 option (name the
  only mechanism that raises the bar to "needs a private key"), or omit entirely? *Recommendation:*
  float-and-defer — name it in the roadmap so no one mistakes the plain-file version for a lock, but do not
  build it (breaks *light setup*; does not fit machine-held-key crews).

## 12. SSOT deltas + implementation handoff

### 12.1 Proposed `02-schemas-and-contracts.md` §13 delta (by name — DO NOT edit §13 here)

Apply in the **same change** as the harness work (invariant 4), coordinated with the in-flight §13 branch:

1. **Replace the marker JSON block** with the v2 shape (§4 above): add the optional `attestation` object
   (`source`, `tool`, `actor`, `evidence{reportPath, reportDigest}`), bump the illustrated `version` to `2`.
2. **Add a "Provenance (issue #366)" subsection** stating: the `source` enum semantics
   (`review-artifact | bare | machine`, read-time `legacy`); that `evidence` is present iff
   `source: review-artifact`; that `reportPath` lives under the hash-**excluded** `state/reviews/` (cross-ref
   §7.3 exclusions); that `reportDigest` binds report↔marker at stamp time; that `actor`/`tool` are
   self-reported/non-authoritative; and the reader rule **"never gate on `version`; classify by the
   `attestation` block."**
3. **Add the trust-boundary sentence** to §13, replacing any "unforgeable"/"can never falsely vouch"
   framing with: *the review floor is only as strong as write-access to the plan folder; provenance raises the
   cost/visibility of a forge and records what an enforce-gate trusts — it does not prove a human
   (invariant 6).* (Note: §13's existing "can never falsely vouch for **changed** content" claim is about
   *staleness* and stays true — scope it explicitly to staleness so it isn't read as a forgeability claim.)
4. **Add a forward-pointer:** "A runtime gate on this marker (`autonomy.reviewGate: enforce`, #361 Open K) is
   **best-effort + recorded**, not unforgeable — see `docs/plans/16-review-attestation-provenance.md`."
5. **Multi-wave (§13/§7.3 per-wave note):** the `attestation` block is **per-wave** exactly as the marker is;
   the review report lives under `<plan>/<wave>/state/reviews/`. No new wave semantics.

### 12.2 Implementation handoff

| Agent | filesTouched | Work | Sequencing |
|---|---|---|---|
| **SSOT author** (coordinate w/ in-flight §13 branch) | `docs/plans/02-schemas-and-contracts.md` §13 (+ §7.3 cross-ref) | Apply the §12.1 delta | **First** (or merged with the parallel §13 branch) |
| `guardrails-harness-developer` | `src/Guardrails.Core/Review/ReviewMarker.cs`; new `…/Review/ReviewAttestation.cs` (or nested record); `src/Guardrails.Cli/Commands/MarkReviewedCommand.cs` | Add `Attestation` record + fields (read-tolerant, ignore-unknown preserved); `mark-reviewed` evidence auto-detect + `sha256` digest helper + `--evidence`/`--source`/`--reviewer`; **bare path unchanged**; `version`→2 write, never gate read on it | After SSOT delta |
| `guardrails-skill-author` | `.claude/skills/guardrails-review/SKILL.md` (Step 6/7); `guardrails-domain-knowledge` if it summarizes the marker | Write the review report to `state/reviews/…`, then call `mark-reviewed`; document `review-artifact` vs `bare` vs `machine`; drop any "unforgeable" wording | After harness (so `mark-reviewed` accepts the artifact) |
| `guardrails-test-author` | `tests/**` marker tests | Round-trips: v1 marker → `legacy`; v2 marker read by old options ignores `attestation`; digest re-verify pass/fail; `bare` vs `review-artifact` classification; `machine` source; **§13 staleness unchanged** (no re-stale from the report artifact) | Alongside harness |

**Enforce-mode (Phase 2 / Open K)** is **deferred** — no handoff here; it is owned by #361 and defines its own
`autonomy.reviewGate` contract + `decisions[]` recording when scheduled.

## 13. Proposed plan-document edits (this DoR's own footprint)

- **New:** this file, `docs/plans/16-review-attestation-provenance.md` (numbering per the Status note —
  renumber if the lead intended `13-`, which is occupied).
- **`docs/plans/03-roadmap.md`:** add a one-line v2-bet pointer under autonomous mode — *"Review-gate
  enforce-mode (#361 Open K) — runtime halt on the review marker; best-effort + recorded per
  `16-review-attestation-provenance.md`, gated on #366."* (Proposed; apply on approval.)
- **`docs/plans/02-schemas-and-contracts.md` §13:** the §12.1 delta (proposed by name; **not** edited here —
  parallel branch in flight).
