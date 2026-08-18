## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — in a WAVED plan that key is the WAVE-QUALIFIED id, i.e.
  `wave-03-verifier-route/02-implement-judge-resolution`, NOT the stableId and NOT
  the bare folder name. The harness REJECTS a fragment keyed by anything else (every
  attempt), so:
  `{ "wave-03-verifier-route/02-implement-judge-resolution": { "someKey": "someValue" } }`.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Task

Implement **`TierResolver.ResolveJudge`** so the tests authored by
`01-author-tests-judge-resolution` pass. **Do NOT edit those tests.** If they are genuinely wrong or
incompatible, write `{"needsHuman": "<why>"}` to the state-out path rather than changing them.

**`docs/plans/17-model-tiering.md` §6.5 and §6.5.1 are the design of record** and win over any
paraphrase. Read the rules there; the tests pin them.

### The one rule that is a correctness requirement, not a style note

**Candidacy is `PromptRunnerConfig.ServesTier` and nothing else (D22a).** The judge path must CALL
that predicate — the same one `SelectCandidate` calls, the same one `validate`'s GR2048 calls. A
second candidacy implementation here is precisely the divergence D22a exists to forbid: if the judge
path counts a costly block as serving a rung and the actor path does not, validation passes and the
run dies at runtime. Wave 1 ships a property test asserting the resolver's candidate set agrees with
`ServesTier` for every (block, rung) pair; keep the judge path inside that agreement.

### Order of operations (§6.5.1 restates it with the floor in place)

1. Frontmatter `tier` / `runner` pin wins outright.
2. Judge rung = the actor's effective **rung**.
3. Weak-actor **strength** bump — weakest candidate at that rung with strength strictly greater.
4. **Floor:** if the rung from (2)–(3) is below `tiering.verifier.minTier`, raise to `minTier` and
   re-select from `Candidates(minTier)`. **Never the reverse** — a result at or above is untouched.
5. The costly floor (§6.2) applies to every selection above; specialization breaks remaining ties.

### The two asymmetries an implementer gets backwards

- **Degrade, do not halt.** When the only stronger block is `costly: true`, the judge **stays at the
  actor's route** and the run **proceeds** with an advisory. The actor in the same situation
  **halts** (`no-route`). Same input, opposite response — degrade what is advisory, halt what is
  load-bearing. Do not reuse the actor's halt path here.
- **D29's carve-out is narrow.** A **pinned** costly ACTOR licenses a costly judge bump. The
  `default` pointer does **not** — it is a plan-wide fallback, not a decision about this task, and
  treating it as sanction would silently license costly judges across an entire plan.

### Scope

Fill logic over the stubs task 01 wrote. `PromptFile.cs` is deliberately **outside** your write
scope: task 01 declared `PromptFrontmatter.Tier` and its tests compile against that declaration —
changing it under them is how a pair silently stops proving anything. If its shape is genuinely
wrong, write `{"needsHuman": "<what is wrong>"}` rather than editing it.

**Scope boundary (harness-enforced):** Write only to
`src/Guardrails.Core/Prompts/TierResolver.cs` and `src/Guardrails.Core/Prompts/TierResolution.cs`.
After this task completes, the harness runs a `git diff` check and rejects any edit outside those
paths — including the test file, `PromptFile.cs`, `GuardrailRunner.cs`, or the `.csproj`. An
out-of-scope edit fails the task immediately and consumes a retry.

**Do not change the ACTOR half.** `SelectCandidate` and `Resolve` have their own passing tests from
waves 1 and 2; a regression there fails this wave's exit gate, not just your own guardrail.
