# 16 — Review-attestation provenance — evidence hygiene + audit trail for the review marker (issue #366)

> **Status: PROPOSED — design only, no code. Scope: evidence hygiene (NO enforce-mode).** A focused
> adversarial pass and the maintainer ruling settled this: **there is no unforgeable option in a plain-file /
> same-machine-autonomous model — not provenance, not a digest chain, not even a GPG-signed commit (the
> autonomous agent holds the key).** So #366 does **not** try to be a forgery deterrent and does **not** add a
> runtime gate. It ships exactly one thing: **non-adversarial evidence hygiene plus an audit trail** — a
> deterministic, recorded distinction between *"a real `/guardrails-review` pass ran and left a report"* and
> *"a bare marker was written."* The recorded class is read by **humans and tools for audit, never by the
> Scheduler.** That inert-to-gates property **is the intended scope, not a limitation.**
>
> **Enforce-mode (the autonomous-mode Open-K runtime gate) is DROPPED** — see §6 and the companion note for
> `docs/plans/12-autonomous-mode.md` §10 K.
>
> **SSOT coordination.** The marker is a contract; its home is `02-schemas-and-contracts.md` §13 (SSOT,
> invariant 4). A parallel §13 branch is in flight, so this doc does **not** edit §13 — it **proposes the exact
> §13 delta by name** (§12) for the SSOT author to apply in the same change as the harness work. Where this doc
> and §13 differ, §13 wins for the wire contract; this doc owns the rationale, the threat model, and the scope.
>
> **Numbering note (for the lead).** The task brief named this `13-…`, but `13-/14-/15-` are occupied on master
> (`13-merge-on-success-default.md`, …). This doc takes the next free prefix, **16**. Renumber if intended
> otherwise.

---

## 1. What it is, and the pain

`/guardrails-review` records that a human ran the adversarial pass over the current plan by writing a committed
marker `state/guardrails-review.json` (SSOT §13, `ReviewMarker.cs`):

```jsonc
{ "version": 1, "reviewedAt": "2026-06-22T14:03:11Z", "planHash": "sha256:…" }
```

`planHash` is `PlanDefinitionHash.Compute(plan)` (§7.3), a pure function of the readable plan files.
`guardrails mark-reviewed <folder>` writes this marker for **any structurally-valid plan, with no human check
and no authorization** (`MarkReviewedCommand.Run`: it refuses only on parse/schema errors, then calls
`ReviewMarker.Write` unconditionally).

**The hygiene gap.** Two very different events produce **byte-identical** markers today:

1. A human ran the full `/guardrails-review` adversarial pass, addressed the findings, and stamped.
2. Something ran `mark-reviewed` (or wrote the three-field JSON) with **no review at all** — a lazy shortcut, a
   buggy skill step, a mechanical batch stamp, a copy from another plan.

A matching marker proves *a marker exists*, not *a review happened*. There is **no durable trace** of case 1 to
distinguish it from case 2: `/guardrails-review` prints its findings to the conversation and leaves nothing on
disk but the marker itself. That indistinguishability — and the absence of any audit record of *what* a review
found — is the pain #366 removes.

### What this is NOT

This is **not** a forgery deterrent, and the design makes no security claim (§3). #366 was surfaced by the
adversarial pass on autonomous mode (#361 / draft PR #364), where a `review-attested` answer path was
**dropped** because the marker is forgeable. That history is the *origin* of the issue, not its scope: the
maintainer ruling is that **no plain-file design can make the marker a real boundary**, so #366 is scoped to
hygiene + audit and the runtime-gate ambition (Open K) is dropped (§6). What remains is worth doing on its own
terms: telling a real review from a bare stamp, deterministically and on the record, is everyday
quality-of-life for anyone maintaining or auditing a plan.

## 2. Placement

**Schema + harness + skill + docs — all v1, single phase. No v2 bet, no runtime behavior change to any gate.**

| Concern | Where |
|---|---|
| The marker contract (new `attestation` block) | **Schema** — §13 delta (proposed by name, §12) |
| `mark-reviewed` records evidence-class additively (+ stamp-time hygiene checks, F2) | **Harness** — `MarkReviewedCommand.cs`, `ReviewMarker.cs` |
| `/guardrails-review` leaves a durable review-report artifact | **Skill** — `guardrails-review/SKILL.md` Step 6/7 |
| A read-only `plan-hash` affordance the skill needs to embed the hash (F2) | **Harness/CLI** — small read command (§12.2, OD-3) |
| Stop calling the review floor "unforgeable"; state the write-access ceiling | **Docs** — §13 + this doc + skill |
| ~~Runtime `autonomy.reviewGate: enforce`~~ | **DROPPED** — not viable in a plain-file model (§6) |

The change is **behavior-inert to every gate**: it adds a field, an artifact, and a recorded classification, and
changes **no** exit code, no default, no Scheduler decision. GR2025 stays a warning. That is deliberate and
complete — the recorded class exists for humans and tooling to read, not for the harness to act on.

## 3. The trust boundary — stated flatly

Invariant 6 is binding: **plain files, light setup — no databases, daemons, or SaaS.** Agents have write access
to the plan tree by construction (that is *how* a plan is authored and reviewed), and there is **no secret** the
honest `/guardrails-review` session holds that a forging agent with tree access does not. Therefore:

> **Against a competent agent with write access to the plan folder — the autonomous threat model — forgery cost
> is ~zero, and there is no unforgeable option: not provenance, not a digest chain, not even a GPG-signed
> commit (the autonomous agent holds the signing key). The ceiling any design in this model can reach is a
> recorded audit trail. That is the ceiling #366 targets, and the only thing it claims.**

Against that ceiling, here is precisely what the design does and does not buy:

- **A → It distinguishes "a real review pass ran" from "a bare marker was written" — the value.** Deterministic,
  recorded classification (`review-artifact` vs `bare` vs `machine`; `legacy` for pre-#366 markers). No judge in
  the loop. This catches the **everyday, non-adversarial** failure modes — a *lazy, buggy, or mechanical*
  bare-stamp — and gives every marker audit richness (source, tool, a report to read).
- **B → A small floor against a _lazy_ forger, nothing more.** A forger who can't be bothered to author a report
  (writes an empty/absent one) can't reach the `review-artifact` class — the stamp-time checks (§4/F2) downgrade
  it to `bare`. That is the entire "deterrence" value, and it is small: **it stops a lazy forger, not a
  competent one.** A competent forger authors a plausible report with the right embedded hash and reaches
  `review-artifact` at ~zero cost. B is a hygiene floor, **not** a security gain.
- **What it does NOT buy:** proof of a human; prevention of a competent forger; any boundary that survives
  write-access; a basis for a runtime gate. The earlier framing that provenance "raises forge cost" as a
  security property or "closes the asymmetry" with the honest escape hatch is **withdrawn** — those overstated
  the residual. The honest positioning is A (hygiene) + audit, full stop.

## 4. The `attestation` block — the concrete schema delta

**Additive. Back-compat by construction** (`ReviewMarker.ReadOptions` is `PropertyNameCaseInsensitive` with **no**
`UnmappedMemberHandling.Disallow`, so System.Text.Json **ignores unknown members** — confirmed by the pass
against the code, same discipline `JournalJson` uses). A pre-#366 marker reads as `legacy`; a newer marker read by
an older tool has its `attestation` block silently ignored and behaves exactly as a v1 marker.

```jsonc
{
  "version": 2,                              // bump; readers NEVER gate on version (rule below)
  "reviewedAt": "2026-06-22T14:03:11Z",      // UNCHANGED
  "planHash": "sha256:…",                    // UNCHANGED — PlanDefinitionHash (§7.3), wire name kept
  "attestation": {                           // NEW; on a v1 marker it is absent ⇒ read as `legacy`
    "source": "review-artifact",             // evidence class: review-artifact | bare | machine
    "tool": "guardrails 1.0.0-preview.43",   // self-reported CLI build that stamped it (informational)
    "actor": "david.maltby@hotmail.com",     // OPTIONAL, SELF-REPORTED, NON-AUTHORITATIVE reviewer id
    "evidence": {                            // present ONLY for source: "review-artifact"
      "reportPath": "state/reviews/review-1a2b3c4d5e6f-2026-06-22T140311Z.md",  // relative to plan folder
      "reportDigest": "sha256:…"             // sha256 of the report bytes, newline-normalized (F7), at stamp time
    }
  }
}
```

### The `source` enum — named for what the CLI can actually verify

The CLI cannot authenticate an actor (a human and a machine invoke the *same* `mark-reviewed`). What it can
verify deterministically is **evidence class**, so `source` is the evidence class; the actor is a separate,
clearly-labeled self-report:

| `source` | Meaning (what the CLI verified) | Written when |
|---|---|---|
| `review-artifact` | A `/guardrails-review` report artifact was present, **passed the F2 stamp-time checks**, and was digested. | `mark-reviewed` found a valid report for the current `planHash` (or `--evidence <path>` that passes F2). |
| `bare` | `mark-reviewed` invoked with **no** valid review artifact — the current unconditional behavior; a human's manual "I read it" confirmation, **or** a `review-artifact` attempt that failed F2 (downgraded). | `mark-reviewed <folder>` with no/invalid artifact. |
| `machine` | Explicitly stamped by an **automated** flow (auto-breakdown / autonomous mode). Never masquerades as human review. | `mark-reviewed <folder> --source machine`. |
| `legacy` | **Read-time only, never written.** A marker with no `attestation` block (a v1 marker). | — |

### F2 — stamp-time hygiene checks (what makes `review-artifact` mean anything)

Even with no runtime gate, a `review-artifact` marker must actually point at a report **for this plan**, or the
recorded class is meaningless and cross-plan-replayable. So when `mark-reviewed` would write `source:
"review-artifact"` (including via `--evidence <path>`), it MUST, at stamp time:

- **(a) Plan-binding.** Require the report to **embed the `planHash` it attests** and assert it **equals the
  marker's `planHash`** (the current `PlanDefinitionHash`). The `/guardrails-review` skill writes that hash into
  the report (it obtains it from the read-only `plan-hash` affordance, §12.2 — the skill can't compute the hash
  itself). A report with a missing or mismatched embedded hash fails this check.
- **(b) Path containment.** Validate `reportPath` **resolves under `<plan>/state/reviews/`** (full-path
  containment, not a substring match — rejects `..` escapes and out-of-tree paths).

**On failure of either, `mark-reviewed` stamps `source: "bare"`, not `review-artifact`** — it never fabricates an
evidence class it cannot substantiate. **Honest bound:** F2 does **not** stop a determined forger — they can
author a report that embeds the correct hash and reaches `review-artifact` at ~zero cost. What F2 closes is the
**accidental / mechanical** failure that would otherwise make the audit trail lie to a *cooperating* reader:
cross-plan misfiling (pointing plan B's stamp at plan A's report) and pure replay of a genuine **foreign**
report. That is exactly the hygiene value — a `review-artifact` class you can trust to mean "a report for *this*
plan exists," in the non-adversarial case.

### Field rules (for the harness author)

- **`evidence` is present iff `source == "review-artifact"`.** `reportPath` is plan-folder-relative under
  `state/reviews/` — which §7.3 **excludes** from `PlanDefinitionHash`, so the report **cannot re-stale the
  marker** (no circularity, mirroring why the marker itself lives under the excluded `state/`).
- **`reportDigest` normalization is symmetric (F7).** Stamp-time digest and any reader that re-checks it apply
  the **identical** newline normalization the codebase already uses for `PlanDefinitionHash` (CRLF/CR → LF)
  before `sha256`, so a CRLF/LF checkout digests the same on both sides. (There is no runtime re-check in scope;
  the symmetry rule is stated so an *audit tool or a future reader* that verifies the digest agrees byte-for-byte
  with the stamp.)
- **`actor` and `tool` are informational and self-reported** — never authoritative; any surfacing MUST label
  them so (e.g. `reviewer (self-reported): …`). They exist for audit richness (a name to ask), not trust.
- **Readers never gate on `version`.** Classification is by **presence of the `attestation` block + its
  `source`**, never by the integer. Bumping to `2` is a signal, not a gate — confirmed safe: `ReviewMarker.Read`
  deserializes `version` and never branches on it.
- **Write-side serialization (F7).** The new optional members serialize with **`JsonIgnoreCondition.WhenWritingNull`**
  so a marker never emits `"actor": null` / `"evidence": null` noise (a `bare` stamp writes just
  `attestation.source` + `tool`). The required top-three fields keep their current `Never` serialization for
  byte-exact back-compat. Readers are unaffected either way.
- **`ReviewMarker.Read` stays tolerant** — a present-but-unparseable marker still reads as `null` (→ `Missing`),
  never throws; a marker with the three top fields intact but a malformed `attestation` block deserializes with
  the block `null` (→ classified `legacy`), matching the tolerant manifest/journal readers.

### The review-report artifact (what `/guardrails-review` must now leave behind)

Today the review pass leaves **nothing durable** — the circularity §1 names. For the audit trail to exist, the
skill must **write** the evidence:

- **Home:** `state/reviews/review-<planHashShort>-<reviewedAtCompact>.md` — committed alongside the marker, under
  the hash-**excluded** `state/` tree. It is a **plan artifact** (like the marker), not per-run runtime state, so
  it belongs under `state/`, not `logs/` (per-`runId`, `--fresh`-wiped; a review has no `runId`).
- **Content:** the Step 6 findings table + verdict (blockers addressed/declined) **and the `planHash` it
  attests, embedded** (F2a — e.g. a `Plan-Definition-Hash: sha256:…` line the CLI can parse). Human-readable —
  its audit value is that a maintainer can *read what the review found*.
- **Binding:** the skill obtains the hash (read-only `plan-hash`, §12.2), writes the report embedding it, then
  calls `mark-reviewed --evidence <report>`, which runs F2 and records `reportDigest` + `reportPath`. Same
  division of labour as today (skill writes, CLI stamps), extended: **skill writes evidence, CLI validates +
  digests + stamps.**

## 5. The `mark-reviewed` change (and what stays back-compat)

Additive and non-breaking — the human's real workflow keeps working:

- **`mark-reviewed <folder>` (bare, no artifact):** stamps `source: "bare"`, **clears GR2025 exactly as today.**
  The shipped manual-confirmation flow (a human reads the plan, runs `mark-reviewed`) is **unchanged** — it now
  just records that it was a bare stamp.
- **`mark-reviewed <folder>` with a valid review report present, or `--evidence <path>`:** runs the F2 checks;
  on pass, stamps `source: "review-artifact"` + `evidence`; **on F2 failure, downgrades to `source: "bare"`**
  (never fabricates the class).
- **`mark-reviewed <folder> --source machine`:** stamps `source: "machine"` — for auto-breakdown / autonomous
  flows (#360/#361) that mark a wave reviewed without a human, so machine stamps are honestly labeled in the
  audit trail (never masquerade as `review-artifact`).
- **`mark-reviewed <folder> --reviewer <id>`:** records the self-reported `actor` (optional).

**`mark-reviewed` NEVER refuses a stamp.** A bare invocation, or a `review-artifact` attempt that fails F2, both
still produce a valid marker that clears GR2025 — they just record `source: "bare"`. This keeps invariant 5
(honest halts, never a surprise block) and the human workflow intact. The class is recorded for audit; it gates
nothing.

## 6. No runtime gate — the review floor stays advisory + audit-only (implication for Open K)

**Enforce-mode is dropped.** A runtime gate on a forgeable file gates on nothing: a plain-file marker is
write-forgeable at ~zero cost (§3), so an `autonomy.reviewGate: enforce` halt would provide **security theater**,
not a boundary — while adding real cost (a new gate, a new failure mode, a new escape hatch). The review floor
therefore stays what it is today, **indefinitely**: a **control-flow halt is not introduced**, GR2025 remains an
**advisory warning**, and #366 adds only the **audit trail** on top. The recorded `source` is for humans and
tooling to inspect after the fact — the Scheduler never reads it.

### Implication for autonomous-mode Open K (companion note — do not edit `12-autonomous-mode.md` here)

Autonomous-mode **Open K** ("promote GR2025 from a warning to a runtime halt") was **explicitly gated on #366**.
The #366 finding resolves it in the negative: **Open K is not viable as a real boundary in a plain-file model.**
Recommendation for the lead to fold into `docs/plans/12-autonomous-mode.md` §10 K:

> **Open K — resolved by #366: close / rescope to audit-only.** A runtime halt on the review marker cannot be a
> real boundary (the marker is write-forgeable at ~zero cost; there is no unforgeable option in a plain-file /
> same-machine model — #366 §3). Do **not** add `autonomy.reviewGate: enforce`. The review floor stays a GR2025
> advisory; #366 adds a recorded, deterministic `attestation.source` (`review-artifact | bare | machine |
> legacy`) that an autonomous run's **post-hoc report / audit** can surface (e.g. "this wave was marked
> `machine`, never human-reviewed") — but it does not gate the run.

## 7. Migration / back-compat (confirmed against the code by the pass)

- **Existing `{version:1, reviewedAt, planHash}` markers keep working, read as `legacy`.** The absent
  `attestation` block deserializes to `null`; `Evaluate` still classifies `Missing`/`Stale`/`Reviewed` purely by
  the `planHash` compare (**§13 staleness unchanged**). GR2025 unchanged. A `legacy` marker on an unedited plan
  reads `Reviewed` and still clears the nudge — no user is worse off; it simply carries no evidence class until
  the next `/guardrails-review` upgrades it.
- **`PlanDefinitionHash` staleness (§13, GR2025) is unchanged.** No new hash input, no re-hash. The review-report
  artifact lives under the hash-**excluded** `state/` tree, so it introduces **no re-stale loop** — confirmed.
- **Older tools reading a newer (v2) marker don't break** — unknown members are ignored
  (`ReviewMarker.ReadOptions`), so a preview.42 tool reads the three v1 fields and behaves identically —
  confirmed.
- **No forced re-review.** Unlike the #260 `PlanHash`→`PlanDefinitionHash` widening, this change does **not**
  touch the hash, so **no existing marker re-stales** — confirmed. Provenance is gained lazily on the next
  review.

## 8. Invariants in play

- **#6 (plain files, light setup — no daemon/SaaS).** The binding constraint and the reason the scope is
  hygiene, not security: it is *why* nothing here is unforgeable. Respected — and named as the ceiling.
- **#5 (honest halts; nothing marked done unverified; needs-human is a feature).** Served: a `bare`/`legacy`
  marker is honestly *labeled* rather than silently equated with a real review. `mark-reviewed` never blocks;
  it records. Strengthened by the audit trail; not strained by any new halt (there is none).
- **#4 (§02 is the SSOT; a contract change lands there in the SAME change).** In play — the marker is a contract.
  Honored via a **verbatim §13 delta proposed by name** (§12), applied by the SSOT author alongside the harness
  work (coordination exception: a parallel §13 branch is in flight).
- **#1 (deterministic guardrails over prompt-judges; judges never alone).** The classification and the F2 checks
  are deterministic reads — no judge decides "was this reviewed." Respected. *Honest bound:* whether the review
  was a *good* review remains a human act; the marker records *that* a report exists, never *that it was good*.
- **#3 (verdicts from verdict files, not exit codes).** Untouched — with enforce-mode dropped there is **no** new
  gate, halt, or exit code; the marker remains an attestation read for audit, not a verdict the harness acts on.

## 9. Devil's-advocate self-critique

The central verdict of the adversarial pass is now the design's own honest positioning, not a concession:
**#366's value is bounded to non-adversarial evidence hygiene + an audit trail, and that is exactly — and only —
what it ships.**

**DA-1 — "So this is nearly worthless: a competent forger reaches `review-artifact` at ~zero cost, and you've
admitted it gates nothing."** Owned, and correctly scoped. #366 makes **no** security claim (§3); it is not for
the adversarial case. Its value is the *everyday* one: today a real review and a lazy/buggy/mechanical bare-stamp
are byte-identical, and a maintainer or an audit **cannot tell them apart or see what a review found**. After
#366 they can — deterministically, on the record. That is real quality-of-life for plan maintenance and
autonomous-run reporting, independent of any adversary. A feature does not have to defeat a competent attacker to
be worth shipping; it has to solve a real problem, and "which markers are backed by an actual review, and what
did it find" is one.

**DA-2 — "Then why F2 at all, if it doesn't stop a forger?"** Because without F2 the `review-artifact` class is
*meaningless even in the cooperative case*: a stamp could point at any file, or at another plan's report, and the
audit trail would silently lie to an honest reader. F2 (plan-binding + path containment) makes the class mean "a
report for *this* plan exists" against **accident and mechanism** — misfiling, batch errors, pure replay of a
foreign report. It is a hygiene check, explicitly not a security check, and the doc says so at every mention.

**DA-3 — "`bare` markers and F2-downgrades look second-class; won't users be confused?"** GR2025 is unchanged —
`bare` clears the nudge exactly as today, and nothing blocks. The class is additive metadata for audit. A user
who never looks at it is unaffected; a user who does gets a truthful signal. No workflow regresses.

**DA-4 — "Self-reported `actor` invites false confidence."** It must be labeled non-authoritative wherever
surfaced. Its only value is audit richness (a name to ask). If the maintainer judges the false-confidence risk
higher than the audit value, drop it — it is the most optional field (OD-2).

**DA-5 — "A committed report per review pollutes the folder / git history."** Small, under `state/reviews/`,
committed like the marker already is, hash-excluded (no re-stale). Same *class* of artifact as the marker — a
committed attestation — and it is the **only** durable record of *what a review found* (today there is none). A
proportionate cost for the audit value.

**DA-6 — "Isn't GPG the honest answer if you want real provenance?"** No — and this is why enforce-mode is
dropped, not deferred. A signature needs a secret the forger doesn't have; in a same-machine autonomous model the
agent **holds the signing key**, so even a GPG-signed commit is forgeable by the exact threat model #366 was
filed against. There is **no** unforgeable option here. GPG survives in this doc only as that note (OD-4), never
as a deliverable.

**The residual, plainly:** you cannot beat write-access in a plain-file model, and no design — provenance,
digests, or signatures — changes that. #366 does not pretend to. It buys a **recorded, deterministic distinction
+ an audit trail for the non-adversarial case**, and nothing more. That honest bound *is* the design.

## 10. Phasing (single phase — v1)

There is no Phase 2. The whole design ships as one additive, back-compat, gate-inert change:

1. **Docs-honesty.** Remove any "unforgeable" / "raises forge cost" / "closes the asymmetry" framing of the
   review floor from §13, this doc, and the skill; state the write-access ceiling (§3).
2. **Schema.** Add the additive `attestation` block (§4) — §13 delta (§12).
3. **CLI affordance.** Add the read-only `plan-hash` surface the skill needs for F2a (§12.2 / OD-3).
4. **Skill.** `/guardrails-review` writes the review report to `state/reviews/…` embedding the `planHash` (F2a),
   then calls `mark-reviewed --evidence`.
5. **Harness.** `mark-reviewed` gains F2 (plan-binding + path containment → `review-artifact`, else `bare`),
   `--source machine`, `--reviewer`, symmetric digest normalization (F7), and `WhenWritingNull` serialization
   (F7); `ReviewMarker` gains the `attestation` record, stays read-tolerant.
6. **No gate.** GR2025 stays advisory; no runtime behavior changes. Fully reversible.

## 11. Open decisions — for the maintainer

- **OD-1 — RESOLVED by the ruling: scope = evidence hygiene + audit trail, no enforce-mode.** Recorded here for
  the trail; no longer open.
- **OD-2 — Keep or drop the self-reported `actor` field?** *Recommendation:* keep, clearly labeled
  non-authoritative (audit richness). Drop if the false-confidence risk (DA-4) outweighs the audit value.
- **OD-3 — How does the skill obtain the `planHash` to embed (F2a)?** The skill can't compute
  `PlanDefinitionHash` itself. Options: (a) a new read-only `guardrails plan-hash <folder>` command (prints the
  full `sha256:…`) — reusable, clean; or (b) a `mark-reviewed --print-hash` dry-run flag. *Recommendation:* (a),
  the standalone read command.
- **OD-4 — GPG note.** Keep the "even GPG isn't unforgeable for this threat model" statement (§3/DA-6) as
  rationale only — **not** a deliverable. *Recommendation:* keep as a note.
- **~~OD — enforce-mode's treatment of `legacy`~~ — REMOVED as moot** (no enforce-mode).
- **~~OD — `version` bump gating~~ — settled:** bump to `2`, readers never gate on version (§4). Recorded, not
  open.
- **~~OD — review-report home~~ — settled:** `state/reviews/` (committed, hash-excluded, durable). Recorded, not
  open.

## 12. SSOT deltas + implementation handoff

### 12.1 Proposed `02-schemas-and-contracts.md` §13 delta (by name — DO NOT edit §13 here)

Apply in the **same change** as the harness work (invariant 4), coordinated with the in-flight §13 branch:

1. **Replace the marker JSON block** with the v2 shape (§4): add the optional `attestation` object (`source`,
   `tool`, `actor`, `evidence{reportPath, reportDigest}`); bump the illustrated `version` to `2`.
2. **Add an "Evidence hygiene (issue #366)" subsection** stating: the `source` enum semantics
   (`review-artifact | bare | machine`, read-time `legacy`); that `evidence` is present iff
   `source: review-artifact`; the **F2 stamp-time checks** (report embeds `planHash` == marker `planHash`;
   `reportPath` resolves under `state/reviews/`; F2 failure ⇒ `source: bare`); that `reportPath` is under the
   hash-**excluded** `state/reviews/` (cross-ref §7.3); that `reportDigest` uses the **same newline
   normalization as `PlanDefinitionHash`** and is symmetric across writer/reader (F7); that `actor`/`tool` are
   self-reported/non-authoritative; and the reader rule **"never gate on `version`; classify by the `attestation`
   block."**
3. **Add the trust-boundary sentence**, replacing any "unforgeable" / "can never falsely vouch" framing with:
   *the review floor is only as strong as write-access to the plan folder; #366 records a deterministic evidence
   class + an audit trail for the non-adversarial case — it does not prove a human and is not a forgery deterrent
   (invariant 6).* (§13's existing "can never falsely vouch for **changed** content" claim is about *staleness*
   and stays true — scope it explicitly to staleness so it isn't read as a forgeability claim.)
4. **Add — explicitly — that the marker is read for audit, NOT by the Scheduler:** there is **no** runtime gate
   on the review marker (enforce-mode was considered and rejected — see
   `docs/plans/16-review-attestation-provenance.md` §6); GR2025 stays advisory.
5. **Multi-wave (§13/§7.3 per-wave note):** the `attestation` block is **per-wave** exactly as the marker is; the
   review report lives under `<plan>/<wave>/state/reviews/`. No new wave semantics.

### 12.2 Implementation handoff

| Agent | filesTouched | Work | Sequencing |
|---|---|---|---|
| **SSOT author** (coordinate w/ in-flight §13 branch) | `docs/plans/02-schemas-and-contracts.md` §13 (+ §7.3 cross-ref) | Apply the §12.1 delta | **First** (or merged with the parallel §13 branch) |
| `guardrails-harness-developer` | `src/Guardrails.Core/Review/ReviewMarker.cs`; new `…/Review/ReviewAttestation.cs` (or nested record); `src/Guardrails.Cli/Commands/MarkReviewedCommand.cs`; a read-only `plan-hash` command surface (OD-3) | Add `Attestation` record + fields (read-tolerant, unknown-members-ignored preserved; `WhenWritingNull` on new optionals — F7); `mark-reviewed`: F2 plan-binding + path-containment → `review-artifact` else `bare`, `--evidence`/`--source`/`--reviewer`, **bare path unchanged**, `version`→2 write but never gate read on it; a `sha256` digest helper applying the **same newline normalization as `PlanDefinitionHash`** (F7); the read-only `plan-hash` printer | After SSOT delta |
| `guardrails-skill-author` | `.claude/skills/guardrails-review/SKILL.md` (Step 6/7); `guardrails-domain-knowledge` if it summarizes the marker | Obtain the hash via `plan-hash`, write the report to `state/reviews/…` **embedding `Plan-Definition-Hash:`** (F2a), then `mark-reviewed --evidence`; document `review-artifact` vs `bare` vs `machine`; drop any "unforgeable" wording; state the class is for audit, not a gate | After harness (so `mark-reviewed`/`plan-hash` exist) |
| `guardrails-test-author` | `tests/**` marker tests | Round-trips: v1 → `legacy`; v2 read by old options ignores `attestation`; **F2 pass ⇒ `review-artifact`; F2 fail (missing/mismatched embedded hash, out-of-tree `reportPath`) ⇒ `bare`**; digest normalization symmetric across CRLF/LF; `machine` source; `WhenWritingNull` omits null optionals; **§13 staleness unchanged** (no re-stale from the report artifact) | Alongside harness |

**No enforce-mode handoff** — the runtime gate is dropped (§6). No Scheduler, `autonomyPolicy`, or
`autonomy.reviewGate` change is in scope.

## 13. Proposed plan-document edits (this DoR's own footprint)

- **New:** this file, `docs/plans/16-review-attestation-provenance.md` (numbering per the Status note — renumber
  if the lead intended `13-`, which is occupied).
- **`docs/plans/12-autonomous-mode.md` §10 K:** the **companion note** in §6 above — resolve Open K in the
  negative (close / rescope to audit-only). Proposed by name; the lead amends that file (this doc does not edit
  it).
- **`docs/plans/03-roadmap.md`:** **no** new v2 bet (enforce-mode is dropped, not deferred). If anything, a
  one-line pointer that the review floor is advisory-by-design per `16-review-attestation-provenance.md`.
  (Proposed; apply on approval.)
- **`docs/plans/02-schemas-and-contracts.md` §13:** the §12.1 delta (proposed by name; **not** edited here —
  parallel branch in flight).
