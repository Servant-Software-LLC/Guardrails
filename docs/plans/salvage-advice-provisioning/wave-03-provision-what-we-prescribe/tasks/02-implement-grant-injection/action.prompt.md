## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's WAVE-QUALIFIED id as the single
  top-level key (this plan is waved), e.g.
  `{ "wave-03-provision-what-we-prescribe/02-implement-grant-injection": { "k": "v" } }`.
  The harness REJECTS a fragment keyed by anything else (every attempt).
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Task
Make task 01's tests pass, and land the contract change in the SSOT in the SAME change (the repo rule).

1. In `src/Guardrails.Core/Prompts/ClaudePromptRunner.cs`, inject **Bash(git show*) and nothing else**,
   UNCONDITIONALLY, alongside the existing unconditional `--add-dir` planDirectory injection. Grep for
   the `--add-dir` marker to find the seam - a sibling wave edited this area, so do not trust a line
   number. Unconditional is deliberate: conditioning on "an attempt that carries a salvage ref" would
   make the effective permission set vary between attempts of the same task, reintroducing exactly the
   nondeterminism this wave exists to remove. Surface what was injected so task 03 can record it.
2. Inject NO write verb, under any condition.
3. In `docs/plans/02-schemas-and-contracts.md`, add ONE normative line to the retry-feedback contract:
   the harness must never present a runnable command the effective permission set does not grant - and
   state that the harness injects the read-only grant its own protocol depends on.

Do NOT edit task 01's authored tests in `tests/Guardrails.Core.Tests/ToolGrantInjectionTests.cs` (task 01
owns that file). If those tests are genuinely wrong, emit {"needsHuman": "<why>"} instead.

## Re-baseline the tests your own change invalidates (you OWN this)

`tests/Guardrails.Core.Tests/ClaudePromptRunnerArgsTests.cs` PINS the exact `--allowedTools` argument
built before injection existed (SSOT §9 quarantines all flag spelling in that class, and this file is
what holds it to account). Unconditional injection WILL break two of its assertions, and no other task
owns that file, so it is in YOUR writeScope: update it in the SAME change.

- `AllowedTools_AreJoinedWithCommas` asserts the joined value equals `Read,Edit,Bash(dotnet *)` exactly.
  The injected grant now also appears in that value. **Keep it an exact-equality pin** - exactness is the
  entire point of this file - and update the expected string to the full emitted set, in the order your
  implementation emits it. Do not soften `Assert.Equal` into `Assert.Contains` to make it pass.
- `NoAllowedTools_OmitsTheFlag` asserts that a plan declaring no `allowedTools` gets no `--allowedTools`
  flag at all. **That is a real behaviour change and the test caught it correctly.** The decision of
  record is CONFIRMED: the harness provisions what it prescribes, so the flag is now ALWAYS emitted, even
  when the plan declares nothing. Re-point this test rather than deleting it - assert that with an empty
  declared list the flag IS present and its value is EXACTLY the injected grant and nothing else. That is
  strictly stronger than the assertion it replaces: it still guards against a stray grant leaking in.

**Preserve the INTENT while re-baselining, do not delete the coverage.** Deleting or weakening a failing
assertion instead of re-pointing it is the failure this task must not commit. Touch only the two
assertions above; leave every other test in that file alone.

These rules are ENFORCED, not advisory - guardrails `03-golden-args-tests-pass` and
`04-golden-coverage-preserved` run that file's tests AND check the coverage structurally, so deleting a
test or softening `Assert.Equal` into `Assert.Contains` fails the task rather than going green. If you
rename `NoAllowedTools_OmitsTheFlag` (its name becomes inaccurate once the flag is always emitted, so a
rename is reasonable), **keep the `NoAllowedTools_` prefix** - that is how the check finds it.

**Scope boundary (harness-enforced):** Write only to
`src/Guardrails.Core/Prompts/ClaudePromptRunner.cs`, `docs/plans/02-schemas-and-contracts.md` and
`tests/Guardrails.Core.Tests/ClaudePromptRunnerArgsTests.cs`. An out-of-scope edit fails the task
immediately and consumes a retry.
