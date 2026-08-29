# 26 — Execute the committed sample pairs: a gate for guardrail quality (#510)

**Status:** reviewed, ready for breakdown. **Issue:** #510. **Supersedes:** the #510 half of the
abandoned `25-backlog-slate` (see §5).

## 1. The gap

```
grep -rn "samples" --include=*.cs src/    →    nothing outside tests
```

The harness never reads `tasks/<id>/samples/`. The loader deliberately excludes it and no other code
path looks at it. So the **two-sided sample pair — the strongest anti-tautology device the skills
have — is a claim recorded in a folder, never a verified fact.**

The stated contract is:

```
tasks/<id>/samples/NN-check.valid.<ext>      # the guardrail must exit 0
tasks/<id>/samples/NN-check.invalid.<ext>    # must exit non-zero
```

Both halves are assertions about behaviour. Both are checkable by running the script twice. Neither is
ever checked. A pair can ship with the polarity backwards, with an `.invalid` sample the guardrail
happily passes, or stale after the script was edited — and **every one of those states is
indistinguishable from a correct pair by inspection, which is the only inspection that happens.**

**This is the tagline inverted.** *"A prompt may propose, only a deterministic gate may certify."* For
guardrail *quality* there is no gate at all: a prompt proposes and a prompt certifies.

## 2. Why now — two measurements from the last two days

**The author-time pair caught a can-never-fail guardrail.** Stage 3 wave 2 ran its pair by hand and
found a wave-entry gate whose four clauses could never fail (PowerShell unwrapping a single-element
array literal, so the loop variable was a string). The header it wrote is the argument:

> The author-time invalid sample caught it; the valid sample did not, because under an all-present tree
> everything passes either way.

**And the doctrine alone did not hold.** A composition-root guardrail in `24-plan-source-provenance`
required `InitialBreakdownInvoker\.PrepareInvocation` — a dotted **name**. `nameof(Type.Member)` is
valid C# containing that text, so a hollow test with two dead `nameof` references passed with **zero
invocations** (measured: exit 0). The rule that would have prevented it — anchor on the CALL — was
already written in two skill files **and loaded by the agent that wrote the guardrail** (#521). A
committed sample pair, executed, would have caught it mechanically.

A guardrail that can never fail is the worst object this repo produces: it certifies broken work in
the direction that looks fine. Note the harness already lints the **opposite** polarity — **GR2055**,
a guardrail that cannot PASS — which dead-ends loudly. The dangerous polarity has no check, and
running the `.invalid` half **is** a can-never-fail detector.

## 3. The design — settled here, not left open

The issue offered three homes. This plan takes two and rejects one.

- **A verb: `guardrails samples verify [folder]`.** Walks every `tasks/<id>/samples/` pair, runs the
  matching guardrail against each half, asserts `.valid` → 0 and `.invalid` → non-zero, and reports
  every mismatch with the guardrail path, the sample path and the observed exit code. CI-runnable and
  cheap; read-only apart from its own temp dirs.
- **A preflight-phase step in `run`** that invokes the same verifier, so a bad pair fails **before any
  task spends a token**.
- **NOT in `validate`.** Validate is static and offline, runs in editors, in CI, and mid-authoring by
  the breakdown agent. Making it execute arbitrary PowerShell is a semantic change this plan does not
  make.

**Mismatch classes the report must distinguish** — each is a different authoring defect and a single
"pair failed" message would flatten them: a `.valid` that exits non-zero (a false-red that would
dead-end every attempt); an `.invalid` that exits 0 (a toothless check); a missing half; a pair with
no matching guardrail; and a guardrail that fails to parse.

**Say what it is in the failure text.** An operator who understands that this check exists to catch a
guardrail that can *never fail* will not delete it when it is inconvenient.

## 4. Done when

- The verb exists and reports every mismatch class distinctly.
- The preflight step calls it and halts the run before the DAG on a bad pair.
- Reversed polarity, a passing `.invalid`, and a missing half each produce a distinct actionable message.
- The SSOT records the verb and the phase step, and `guardrails-domain-knowledge` is updated in the
  same change (invariant 4).

## 5. Why this is its own plan

It was originally the first cluster of `25-backlog-slate`, which batched five unrelated issues to
amortise the ~$10 breakdown floor. That bundle was the wrong shape and the plan was abandoned before
running: three of its five items collided on the same files (forcing a serialized chain that gave up
the parallelism advantage), its docs sink coupled all three chains, and **delivery is all-or-nothing**
— one failing chain would strand every other chain's finished work (#525).

A Guardrails plan's unit is a **coherent deliverable**, not a shopping list. This one is coherent: a
verb, the phase that calls it, and the contract they record.

## 6. Out of scope

- Any change to `validate`'s static-and-offline contract.
- Verifying pairs for guardrails outside `tasks/<id>/samples/` (the plan-root and wave gates do not
  carry pairs today).
- `#511`, `#522`, `#523`, `#524` — the rest of the abandoned bundle. #522/#523/#524 are re-cut as
  `27-operator-visibility`; #511 is two isolated tasks and does not warrant a plan.
