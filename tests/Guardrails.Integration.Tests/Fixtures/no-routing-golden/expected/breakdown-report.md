# Breakdown report — hello-greeting

The Step 7 report `/plan-breakdown` printed for this plan, captured verbatim as part of the
golden. It is here because two of the three things the gate must not emit are files, and the
third is a REPORT LINE — a report the golden did not capture could not be asserted against.

## Tasks

| # | Task | Action | Depends on | maxTurns | Guardrails |
|---|------|--------|-----------|---------|------------|
| 1 | `01-seed-recipient` | script (`action.ps1`) | — | — | 1 deterministic |
| 2 | `02-write-greeting` | prompt | `01-seed-recipient` | runner default (25) | 1 deterministic |
| 3 | `03-review-greeting` | prompt | `02-write-greeting` | 20 | 1 deterministic + 1 judge |

## Guardrail choices

- `01-seed-recipient` → `01-recipient-seeded.ps1`. The plan dictates the exact file content,
  so the check compares it exactly. No judge: there is nothing subjective here.
- `02-write-greeting` → `01-greeting-addresses-recipient.ps1`. Derives the expected greeting
  from `out/recipient.txt` rather than hard-coding `World`, so the check still holds if the
  seed changes.
- `03-review-greeting` → `01-review-structure.ps1` for the required sections and the verbatim
  quote, plus `02-verdict-is-substantive.prompt.md`, the one surviving judge. It survived the
  Step 4 demotion gate because "does the verdict commit to a view" cannot be regex'd; the
  structural half was demoted to the script beside it.
- Plan level → `01-out-folder-complete.ps1`, so a run cannot end green with a deliverable that
  a later task deleted.

## maxTurns

`03-review-greeting` gets 20 (it reads one file and writes one file, but has a judge to
satisfy on retry). The other prompt task takes the runner default. `01-seed-recipient` is a
script and takes none.

## What I inserted that the plan did not ask for

- The plan-level completeness guardrail. The plan's "done when" list is a whole-run claim and
  no single task can prove it.

## Assumptions

- `out/` is created by the seed task; nothing outside `out/` is written, which is what the
  three `writeScope` declarations pin.

## Review this before running

This is a DRAFT. Check the guardrails prove the deliverables rather than restating them, and
that the DAG matches the order you actually want.
