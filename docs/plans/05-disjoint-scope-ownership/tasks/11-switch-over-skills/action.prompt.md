## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Task
Switch the Claude skills to the `writeScope` model (M6 — §7, §10 M6 of
`docs/plans/05-disjoint-scope-ownership.md`). This is the skills/docs vertical; there
is no separate test-author task because the deliverable is skill prose + docs +
fixtures, not new harness code — the deterministic guardrail is the existing golden
round-trip meta-test plus targeted greps on the skill files. Do ALL of:

1. `.claude/skills/plan-breakdown/**`:
   - The skill now DECLARES an explicit `writeScope` for EVERY generated task — narrow
     globs derived from what the task writes; `[]` for pure build/test gate and
     state-only tasks; justified `["**"]` only for genuinely repo-wide work (with a
     one-line justification in the task description).
   - It adds read-after-write `dependsOn` edges (if task B reads files task A produces,
     `B dependsOn A`).
   - It STOPS emitting `captureHashes`, `tests-untouched`, and `restoreOnRetry`. Update
     SKILL.md Step 5 (the test-author/implementation handoff), the guardrail catalogue
     (`references/guardrail-catalogue.md`), the .NET stack file if it references the
     triad, `references/schemas.md`, and `references/example-breakdown.md` so the worked
     example shows `writeScope` and NO triad.
2. `.claude/skills/guardrails-review/**`: it challenges `writeScope` in BOTH directions
   — too-broad/universal (weak ownership guardrail, narrow it) and too-narrow (would
   revert legitimate writes), plus missing read-after-write edges and overlapping scopes
   among independents.
3. `.claude/skills/guardrails-domain-knowledge/**`: update the contract quick-reference
   to describe `writeScope` and the retired triad (honor the skill's SELF-UPDATING
   clause).
4. Regenerate the committed golden example
   `examples/hello-guardrails/hello-guardrails/**` to the new pattern (explicit
   `writeScope` per task, no triad) so it stays the skill's few-shot reference AND so
   `GoldenRoundTripTests` still loads + validates it clean against the new harness.

Exit criterion (M6): the golden round-trip test passes, the skill files emit
`writeScope` and no triad, and the example validates clean (GR2015 satisfied because
every implementation task's writeScope excludes its test-author's outputs).

Do NOT remove the triad from the harness schema/loader/code in THIS task — that clean
removal is M7. Here you only change the SKILLS to stop EMITTING it and regenerate the
example; the harness still understands the triad until M7. Publish nothing to state.
