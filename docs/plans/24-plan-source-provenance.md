# 24 — Plan-source provenance, and the delegated-decision count gate (#505 · #500)

**Status:** reviewed, ready for breakdown. **Issues:** #505 (provenance), #500 (the second half of the
delegated-decision gate). **Related:** #496 (the unattended Charter → Guardrails harness).

## 1. What this is, and why the two halves ship together

The harness reads the source `plan.md` at exactly one place and then forgets it. Two separate needs turn
out to be served by that same read, and doing them apart would mean reading one file twice for two checks
that must agree about what they read.

**Half A — provenance (#505).** Nothing on the Guardrails side records the markdown it consumed.
`PlanHash` covers `guardrails.json` plus each `task.json`; all four sibling hashes are folder-internal.
So Charter's `handoffSha256` is a tamper detector **with no consumer**, and the chain
`.charter.md → planSha256 → plan.md → handoffSha256 → ???` stops one hop short of what #496 claims to
prove.

**Half B — the delegated-decision count gate (#500).** `plan-breakdown` now records a Charter-delegated
decision and a plan-root preflight fails the run if a **found** id went unrecorded. But that gate is
authored by the agent it polices, so it cannot catch a breakdown that never **ran** the scan: no ids found
⇒ no `decisions.md`, no preflight, and a green run on an invented decision.

Charter supplied the missing signal: the **count line** is emitted into `plan.md` *before our breakdown
agent exists*. A check that reads it owes that agent nothing.

## 2. The one chokepoint

`InitialBreakdownInvoker.PrepareInvocation` → `TryReadPlan` (`InitialBreakdownInvoker.cs:51,92`) is the
only place harness code reads a source `plan.md`. Its only caller is `BreakdownCommand`. The JIT wave path
reads `<wave>/brief.md` and never touches it.

That site **provably has the bytes**, and it runs **outside the agent it polices** — it is the thing that
invokes that agent. Both properties are what make it the right home; a later reader has neither.

## 3. Deliverable A — `<plan>/state/plan-source.json`

Written by the harness at breakdown time.

```json
{ "version": 1, "capturedAt": "2026-08-27T18:00:00Z", "sourcePath": "docs/plans/foo.md",
  "sourceBytes": 18422,
  "sourceSha256": "sha256:<hex>",
  "sourceSha256Lf": "sha256:<hex>",
  "declaredDelegatedDecisions": 2,
  "stamps": { "plan-sha256": "<hex>", "answers-sha256": "none" } }
```

Field rules, each load-bearing:

- **`sourceSha256`** hashes the bytes as read. Use `File.ReadAllBytes`, not `ReadAllText` — the current
  call decodes, and a BOM/encoding round-trip makes the hash not byte-exact against Charter's.
- **`sourceSha256Lf`** hashes the same bytes with CRLF/CR normalised to LF. **Both are required.** A raw
  mismatch will usually be `core.autocrlf`, not tampering, and a check whose first alarm is a false one
  trains everyone to ignore it.
- **`stamps` is an OPEN MAP**, keyed by whatever `<!-- charter: <key>=<value> -->` comments are found —
  not two named fields. Charter adds stamp lines over time; an open map absorbs them with no schema
  change here. Duplicate keys: first wins, and the duplicate is reported.
- **`declaredDelegatedDecisions`** is the integer from
  `DECISIONS DELEGATED TO YOU: (\d+)\*\*`, or **0** when the line is absent. Unambiguous, because Charter
  emits the line whenever the count is ≥ 1 and never when it is 0.

**It lives under `state/` and that is not cosmetic.** A field on `guardrails.json` folds into
`PlanDefinitionHash`, which keys the review attestation — so *recording provenance would de-attest the
plan's review* and re-fire GR2025. `state/` is excluded from all four hashes and from
`BreakdownManifest.ShouldInclude` (only the committed `state/seed.json` is authored content), and
`RunReset` deletes named files rather than the folder, so this survives `--fresh`. **A test must assert
that survival explicitly** — it is the kind of property a later refactor silently breaks.

## 4. Deliverable B — the declared-count gate

After the breakdown agent returns, the harness compares what it read against what the agent produced:

> The harness read a plan declaring **N** delegated decisions. The folder records **M**. If **N ≥ 1** and
> **M ≠ N**, fail the breakdown.

A breakdown that never scanned produces no `decisions.md` ⇒ M = 0 ⇒ red. That is the case the plan-root
preflight structurally cannot catch, and it needs no cooperation from the agent.

**Two limits, to be stated in the failure text rather than discovered:** it proves the **count**, never
that a decision was made **well**; and it depends on Charter's count-line guarantee, so markers present
with **no** count line is a Charter bug to file there, not a plan defect.

## 5. Deliverable C — the interactive door

`/plan-breakdown` invoked as a skill runs **no harness code at all**, so neither A nor B happens on that
path. It needs a deterministic verb the skill can invoke — `guardrails record-plan-source <folder>
<plan.md>` — writing the identical artifact.

This is the part that is harder than it looks, and it is deliberately last: the skill must call it, and a
skill that forgets is exactly the failure mode B exists to catch. Ship A and B first; C is a separate
task and may be deferred without invalidating them.

## 6. Out of scope

- **Plan DRIFT** — noticing the source changed *after* the breakdown. That is #496's territory. An
  embedded expectation cannot detect it, and widening this into a drift detector re-imports every problem
  that made the #500 preflight embed its ids rather than grep the plan.
- **Validating the stamps against Charter.** We record what we found. Verifying the join is `charter
  verify`'s job, and its own help says a green verify detects inconsistency between two mutually-writable
  files and can never detect incorrectness.

## 7. Done when

- `state/plan-source.json` is written on every `guardrails breakdown`, with both hashes, the open stamps
  map, and the declared count.
- The declared-count gate fails a breakdown whose folder under-records, and its message names N, M, and
  both limits.
- A test proves the artifact survives `--fresh`.
- A test proves `PlanHash` and `PlanDefinitionHash` are unchanged by the artifact's presence — the
  de-attestation trap must be guarded, not just avoided.
- SSOT (`docs/plans/02-schemas-and-contracts.md`) carries the new artifact, and
  `guardrails-domain-knowledge` is updated in the same change.
