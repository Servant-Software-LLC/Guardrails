# The no-`routing` golden — DoR Invariant 7

> **Invariant 7:** breaking down a plan against a config with no `routing` block produces a
> folder **byte-identical to today** — the same bytes `/plan-breakdown` emitted before
> difficulty tiering (#225) existed.

This folder is half of the settled two-mechanism proof of that invariant. The other half is
the negative assertions. Both live in
`tests/Guardrails.Integration.Tests/ModelTiering/`; neither replaces the other, which is
why the resolved `invariant-7-proof` question settled on **both**:

| Mechanism | Catches | Misses |
|---|---|---|
| This golden | drift **nobody enumerated** — a new key, a reordered field, a file that appeared | says only *implicitly* what must never appear |
| The negative assertions | exactly the three named leaks: `action.tier`, a `tiering` block, a classification report line | a leak in a shape nobody thought to name |

## Layout

```
input/
  hello-greeting.md      the plan that was broken down
  guardrails.json        the governing config it was broken down AGAINST — no `routing`
                         block on any prompt runner, no top-level `tiering` block
expected/
  hello-greeting/        the emitted task folder — the golden
  breakdown-report.md    the Step 7 report the breakdown printed
manifest.sha256          the byte seal over every file in expected/
```

`input/guardrails.json` is not decoration. If it ever grew a `routing` block the whole proof
would go vacuous — a breakdown against a *configured* plan is allowed to emit tiers — so
`NoRoutingGoldenTests` asserts the input is genuinely un-configured before it asserts
anything about the output.

## Why the plan is small but not trivial

The fixture plan is three tasks because that is the smallest shape containing **all three**
populations a tier could leak into:

- `01-seed-recipient` — a **script** task (never tagged, on either side of the gate);
- `02-write-greeting`, `03-review-greeting` — **prompt** tasks, each of which would carry an
  `action.tier` if tiering were configured. `03` also carries a real `action` block
  (`maxTurns: 20`), so the golden proves the absence of a `tier` *inside an action block that
  exists* — not merely the absence of the block;
- `02-verdict-is-substantive.prompt.md` — a surviving **prompt-judge guardrail**, the
  population Step 4c.2 classifies and reports but has no Stage 1 field to write to.

The `breakdown-report.md` is committed for the same reason: one of the three forbidden
emissions is a *report line*, and a report nobody captured cannot be asserted against.

## The seal (`manifest.sha256`)

`<sha256>  <path>`, one line per file under `expected/`, sorted ordinal by path, forward
slashes, LF. The test recomputes it and compares the whole document.

**Hashes are over line-ending-normalized content** (CRLF and lone CR → LF), not raw bytes.
`.gitattributes` pins `eol=lf` surgically — for the schema-drift pair, `examples/`, and
`tests/Guardrails.Core.Tests/TestData/` — and this path is not among them, so a Windows
checkout with `core.autocrlf=true` would materialize CRLF here while Linux keeps LF, and a
raw-byte seal would fail on one leg of the 3-OS matrix for a reason that has nothing to do
with tiering. Normalizing costs nothing the invariant cares about: every drift that matters
(a new key, a changed value, an added or removed file) still moves the hash.

Pinning `/tests/Guardrails.Integration.Tests/Fixtures/** eol=lf` and hardening the seal to
raw bytes is the better end state; `.gitattributes` was outside the write scope of the task
that authored this fixture.

## Regenerating

Do NOT hand-edit `expected/` to make a test pass — that is the one edit this fixture exists
to make expensive. If a breakdown legitimately changes shape:

1. re-run `/plan-breakdown` on `input/hello-greeting.md` against `input/guardrails.json`;
2. replace `expected/` with the result and re-record the report;
3. re-run the tests. The negative assertions run over the NEW bytes, so a regeneration that
   leaked a tier is caught even though the seal was re-recorded;
4. re-seal: `NoRoutingGoldenTests` prints the corrected `manifest.sha256` in its failure
   message — paste it in, and say in the commit message what moved and why.
