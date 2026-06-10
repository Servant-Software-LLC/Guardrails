---
name: uber-report
description: |
  Run the Guardrails agent team against the current state of the repo and produce a
  short, honest status + readiness report with a slug-keyed task index. Use at the
  start of a work session, after a burst of multi-session work, or before deciding
  what to build next. The headline is the Reality Gate — three booleans from
  evidence, never from plans. Reports land in .claude/tasks/.
---

# Guardrails — Uber Report

A deliberately lightweight adaptation of the house uber-report pattern. This repo is
small and single-track: **don't pad — a short report on a small repo is correct.**

## The Reality Gate (fill from EVIDENCE, first, always)

| Gate | How to check | Pass? |
|------|--------------|-------|
| Build + tests pass | `dotnet build` + `dotnet test` (Release, clean) | ☐ |
| Walking skeleton runs | `guardrails run examples/hello-guardrails/hello-guardrails --fresh --no-ui` exits 0 (NOTE: spends ~$1 of tokens; if unwilling, mark UNVERIFIED, never pass-by-assumption) | ☐ |
| plan-breakdown round-trips | a breakdown of `hello-guardrails.md` passes `guardrails validate` | ☐ |

Until all three pass, the verdict is hard-capped at **"walking skeleton not
proven"** — no doc counts, no milestone counts in the summary.

## Procedure

1. **Prior context**: glob `.claude/tasks/*.tasks.json`; carry forward open slugs;
   mark satisfied ones completed (confirm by inspection).
2. **WIP survey** (brief): `git -C <repo> status --short`, `log --oneline -15`,
   unmerged branches. Single-session repo — this is usually one line, but say it.
3. **Reality Gate** (above).
4. **Two-axis status**:
   - Harness vs `docs/plans/03-roadmap.md` milestones (M1–M7, exit-criteria-based —
     Built ≠ Verified; check the domain-knowledge Status section, then verify claims).
   - Skill maturity, per skill: missing / drafted / validated-against-example /
     battle-tested-on-real-plan.
5. **Slim dispatch** (read-only, parallel; skip any with nothing to assess):
   `guardrails-harness-developer` — code health of src/** (TODO/dead code/oversized,
   seam violations, test gaps); `guardrails-test-author` — suite health,
   passing-but-blind hunts; `guardrails-architect` — plan-doc drift vs as-built;
   `guardrails-devils-advocate` — risk register against the seeded risks in
   `03-roadmap.md` (every seeded row appears, N/A'd if inapplicable). Each proposes
   ≤ 5 findings, cited `path:line`, falsifiable.
6. **Report** → `.claude/tasks/YYYY-MM-DD-uber-report-vN.md` (never overwrite a
   prior version):

   ```markdown
   # Guardrails — Status & Readiness
   **Date / HEAD / WIP one-liner**
   ## Reality Gate            (the table, with evidence)
   ## Executive summary       (3–5 sentences; no counts-as-progress)
   ## Milestones M1–M7        (exit-criterion verdicts, evidence cited)
   ## Skill maturity
   ## Findings                (merged, deduped, ranked; ≤ ~10)
   ## Risk register           (seeded rows from 03-roadmap.md + run-specific)
   ## Proposed next actions   (slug-keyed)
   ```

7. **Task index** → same basename `.tasks.json`: `{ slug, kind, priority, effort,
   status, dependsOn, notes }` per action; carry-forward joins on slug.

## Honesty rules

- Built ≠ verified-running: milestone verdicts come from exit criteria demonstrated,
  not code existing.
- Findings are falsifiable (`path:line` or a named contract) — "looks fine" is not
  a finding.
- The Reality Gate's token-spending check is either RUN or marked UNVERIFIED.
  Never inferred.
