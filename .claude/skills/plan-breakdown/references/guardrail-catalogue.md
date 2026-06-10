# Guardrail catalogue — archetypes, decision tree, anti-patterns

The quality of a plan breakdown IS the quality of its guardrails. This catalogue is
the selection doctrine: **deterministic over prompts, always.** A unit test beats a
regex; a regex beats an LLM judge. Every guardrail must answer one question:

> **"What wrong implementation does this catch?"**

Write that answer as a comment at the top of every guardrail file (`# catches: …` in
scripts; an HTML comment or frontmatter note in prompt guardrails). If you cannot
write the sentence, the guardrail is decorative — delete it.

## Archetypes (strongest/cheapest first)

| # | Archetype | Form | Use when | Catches |
|---|-----------|------|----------|---------|
| 1 | **file-exists / file-contains** (regex) | script | Any artifact-producing task — almost always guardrail #1 | Agent claimed success without producing the artifact, or produced the wrong shape |
| 2 | **command-exit-code** | script | Task output is itself runnable; CLI behavior checks | Artifact exists but is broken when actually executed |
| 3 | **build-passes** | script (`dotnet build`) | Any code-producing task | Code that doesn't compile |
| 4 | **specific-tests-pass** | script (`dotnet test --filter`) | Behavior implementation — filter to THIS task's tests; whole-suite green belongs to a terminal integration task only | Wrong behavior, regressions in the targeted area |
| 5 | **lint/format clean** | script | The repo already has a configured linter (never introduce one ad hoc) | Style/usage violations the repo's standards forbid |
| 6 | **schema-validates** | script | Task emits structured data and a schema exists (or you inserted a schema-author task) | Structurally invalid output |
| 7 | **port/endpoint-answers** | script (probe + curl, owns process start/stop, with timeout) | Task delivers a running service behavior | Service that builds but doesn't actually serve |
| 8 | **tests-fail-on-current-code** | script | THE distinctive one — for inserted test-author tasks; run the new tests against the pre-implementation code and require failure (or skipped-with-reason) | Tautological tests that pass against a stub and verify nothing |
| 9 | **prompt-judge** | `.prompt.md` (writes `{pass, reason}` verdict) | **LAST RESORT** — see the demotion gate | Genuinely subjective properties: tone, clarity, design taste |

## The prompt-judge demotion gate

For EVERY candidate prompt-judge, ask all four. Any "no" → demote to a deterministic
archetype:

1. **Is the property genuinely subjective** (tone, clarity, taste)? If a regex,
   schema, or test could check it, it must.
2. **Is it paired with ≥ 1 deterministic guardrail** on the same task? A judge is
   never alone.
3. **Is the judge criterion-specific**, not vibes? "PASS iff the report names every
   failed task" — never "is this good?".
4. **Is it pointed at the raw artifact**, not at anything the action wrote about its
   own work? If the action can game it by writing a flattering summary, point the
   judge at the artifact itself.

The judge prompt must instruct: *you are a verifier; do NOT fix anything; write
`{"pass": bool, "reason": string}` to `GUARDRAILS_VERDICT_OUT`; the reason becomes
retry feedback, so make it actionable.* (The harness appends the full verdict
contract automatically — the prompt only needs the criterion. See
`examples/hello-guardrails/hello-guardrails/tasks/03-quality-check/guardrails/02-tone-is-friendly.prompt.md`
for the golden reference.)

## The decision tree (apply per task)

```
What is the task's primary deliverable?
├── A file/artifact            → file-exists (always) + the strongest content check available:
│                                schema-validates > file-contains-regex > prompt-judge
├── Code (library/feature)     → build-passes + specific-tests-pass (--filter THIS task's tests)
│                                └─ tests don't exist yet? → INSERT a test-author task upstream
│                                   (its guardrails: build-passes + tests-fail-on-current-code)
├── A runnable script/tool     → file-exists + command-exit-code on a representative invocation
├── A running service          → port-answers + endpoint-content (curl + contains/schema)
├── Config/data                → schema-validates; else file-contains on load-bearing keys
├── Docs / prose               → file-exists + file-contains (required headings/terms);
│                                prompt-judge ONLY for genuine subjective quality, never alone
└── Refactor (no new behavior) → build-passes + existing-tests-still-pass (the suite IS the guardrail)
```

Per task: **minimum 1, typical 2–3, soft max 4** guardrails. Order them
**cheapest-first** by filename (`01-exists`, `02-builds`, `03-tests`, `04-review`) —
the default `failFast` mode stops at the first failure, so a cheap existence check
should fail before an expensive test run or a paid judge ever starts.

## Anti-patterns (the review skill hunts for these — don't generate them)

- **Tautological**: the guardrail checks something the action writes specifically to
  satisfy it ("status.txt contains DONE"). The action controls the evidence.
- **Echo-judge**: a prompt-judge evaluating the action's own claim of success (its
  summary, its commit message) rather than the artifact.
- **Over-broad**: "all tests pass" on an early task — it fails for unrelated reasons,
  poisons retries with noise, and serializes the DAG. Whole-suite green belongs to one
  terminal integration task.
- **Hidden-state**: the guardrail depends on machine state (network, globally
  installed tools, a developer's home dir) rather than ancestor outputs or the repo.
  Declare required interpreters via `guardrails.json` instead.
- **Unactionable failure**: a guardrail that fails with "FAIL" and nothing else. The
  failure line on stdout becomes the retry feedback — "greeting.txt missing 'Hello'"
  converges; "FAIL" loops.

## The artifact-ancestry rule

A guardrail may only reference artifacts that are (a) produced by an ANCESTOR task in
the DAG, or (b) pre-existing in the repo. A guardrail that checks something no
upstream task produces will fail forever — that is a missing inserted task (see the
skill's Step 5), not a guardrail problem. Sweep every guardrail against this rule
before writing the folder.
