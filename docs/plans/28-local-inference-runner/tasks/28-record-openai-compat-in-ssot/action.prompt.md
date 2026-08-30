## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key - the name of the directory this task.json lives in, NOT the stableId. The
  harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "28-record-openai-compat-in-ssot": { "someKey": "someValue" } }`.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- Guardrails constrain the OUTCOME, never HOW you implement it. Never reshape working
  code - or reword a document away from its own conventions - to match a check's
  pattern.
- If you cannot proceed without a human decision, write
  {"needsHuman": {"question": "<question>", "kind": "blocked-work"}} to the
  state-out path and stop. If instead a guardrail reports something ABSENT that you
  can see is PRESENT, that guardrail is defective: use "kind": "defective-guardrail"
  and quote (a) the guardrail's exact claim and (b) the file:line that refutes it.
  If you cannot produce BOTH quotes it is not a defective guardrail - retry the work,
  or escalate as "blocked-work". Difficulty is never "defective-guardrail".

## Plan of record

This task implements part of `docs/plans/28-local-inference-runner.md`. READ THE SECTION(S) NAMED BELOW before you start -
the plan carries the reasoning, the rejected alternatives, and the exact file:line evidence.
Where this prompt and the plan disagree, the plan is authoritative and you should say so in
your summary.

Read: **plan section 12**.

## Task

### What to build

The SSOT and tiering-DoR edits, from the plan's section 12 list. These land in the same change as the
code (invariant 4), and every one of them describes behaviour tasks 01-27 have now shipped - read the
real code, not this plan's quotations of it, before writing.

**Start with the canonical block, because a guardrail checks it structurally.** In
`docs/plans/02-schemas-and-contracts.md` section 2, the `"promptRunners": { … }` block marked by the
`canonical-schema:promptRunners` sentinel must gain `endpoint`, `contextTokens`, `apiKeyEnv`, `wire`
and `engine`, **each shown in its absent (`null`) state, INSIDE that block**, noting they apply to
`kind: "openai-compat"` only and that `command` is ignored for that kind. Update the `kind` comment:
`claude` and `openai-compat` are IMPLEMENTED. That block is **parsed and validated by
`SchemaDriftTests`**, so malformed JSON there fails your own guardrail immediately - and it is
mirrored byte-for-byte by task 29, so get it right here first.

Then the rest of section 12:

2. **Section 8, per-attempt log layout.** Reword `claude-stream.jsonl` to admit a non-Claude writer,
   led by a `runner-notice` object, mirrored onto `guardrail-<name>.stream.jsonl`.
3. **Section 9 intro.** `ServesRoles` and `NeedsContainmentHook` as build facts; the
   `PromptInvocation.Role` contract and its classification rule; the empty-path convention; and that
   the containment splice is now conditioned on `NeedsContainmentHook`.
4. **Section 9.4.** The splice condition, and why a runner with no write/shell tools needs no hook.
5. **GR2009's bullet.** Kind-aware: PATH probe for `claude`, skipped for `openai-compat`.
6. **The cost bullets.** A runner reporting no cost records `null`, never `0`; judge spend is
   recorded and is **not** summed into the run total.
7. **Section 4.2.** The verdict-contract section has two forms, selected by runner capability.
8. **A new section 9.8, "The `openai-compat` runner (#223)"** - block schema, role gate, wire mapping,
   containment primitive, failure taxonomy, verdict transcription, the preflight and its zero-cost
   condition, **and the tool-capability probe with the section 6.6 false green it closes**.
9. **The validation table** - rows for GR2065, GR2066 and GR2067, and amend GR2044's row so `local`
   names `openai-compat`.
10. **`providers init`** - `OpenAiCompat` joins `ModelEnumerable`.

In `docs/plans/17-model-tiering.md`: correct the **stale GR2044 row** (it still says a recognised-but-
unimplemented kind loads clean and throws at registry construction; that describes pre-Stage-1.5
behaviour), and add a pointer from section 4.4's seam paragraph to plan 28.

Write for a reader who has not read plan 28. State the MLX position explicitly: the kind is named
after the **protocol**, so MLX needs no new kind - and the tool-capability probe is what turns "it
drops in" into "it is supported".

**Scope boundary (harness-enforced):** Write only to `docs/plans/02-schemas-and-contracts.md`, `docs/plans/17-model-tiering.md`. After this task completes, the
harness runs a `git diff` check and rejects any edit outside these paths - including changes to
other production files, neighbouring test files, or the `.csproj`. An out-of-scope edit fails the
task immediately and consumes a retry. If you hit a compile error caused by a missing symbol in
another file, do NOT edit that file - write `{"needsHuman": "<what is missing>"}` to the
state-out path and stop.
