## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's WAVE-QUALIFIED id as the single
  top-level key (this plan is waved), e.g.
  `{ "wave-04-fix-the-advice/02-implement-salvage-advice": { "k": "v" } }`.
  The harness REJECTS a fragment keyed by anything else (every attempt).
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Task
Make task 01's tests pass by rewriting `AppendSalvageSection` in
`src/Guardrails.Core/Execution/RetryPolicy.cs` (grep for the method; wave 1 already edited this file, so
line numbers have moved).

1. **Lead with the patch-file route.** `prior-attempt.patch` needs no git at all, is already inside the
   granted read surface (the harness emits `--add-dir planDirectory` unconditionally), and is strictly
   better than a whole blob for surgical edits - a diff beats re-reading the file. Stop framing it as
   "Pull in EVERYTHING".
2. **Route by size** using `SalvageRef.DiffStat`, which already carries per-file changed-line counts and
   is already embedded in the feedback: few changed lines -> read the hunk and Edit; essentially-new
   file -> pull the whole blob.
3. **Name the working invocation shape.** The harness sets cwd to the worktree, so `git -C <abs-path>`
   is unnecessary AND is the dominant cause of refusals (86 in one run, killing read-only verbs too).
   Say so explicitly.
4. **Drop the `git diff <taskBase> <ref>` alternative.** It is the only remaining command outside the
   injected grant; `git show --stat <ref>` already covers inspection. ACCEPTANCE: every command the
   salvage text emits is `git show`, the patch path, or a file-editing tool - nothing else.
5. **Warn that whole-patch adoption is often wrong** - in a real run the agent correctly refused
   `git apply` because the patch carried out-of-scope packages.lock.json churn that would have failed
   the write-scope check.

## Re-baseline the tests your own change invalidates (you OWN this)

`tests/Guardrails.Core.Tests/RetryPolicyTests.cs` PINS the exact salvage text you are rewriting - the
emitted `git show "<ref>:<path>"` strings, and the #374 regression test's `allowedTools` / `only if`
assertions. Your change WILL break some of them, and no other task owns that file, so it is in YOUR
writeScope: update those assertions in the SAME change.

**Preserve the INTENT while re-baselining, do not delete the coverage.** The #374 regression test exists
to pin a DIRECTION: no copy-pasteable `git <write-verb>` invocation may appear as the per-file recovery
route, while the prose MAY still name those verbs to warn they are ungranted. Keep that assertion alive
against your new wording - re-point it, never weaken or remove it. Deleting a failing assertion instead
of re-baselining it is the failure this task must not commit.

Do NOT touch `tests/Guardrails.Core.Tests/RetryPolicySalvageAdviceTests.cs` (task 01 owns it). If those
authored tests are genuinely wrong, emit {"needsHuman": "<why>"} rather than editing them.

**Scope boundary (harness-enforced):** Write only to `src/Guardrails.Core/Execution/RetryPolicy.cs` and
`tests/Guardrails.Core.Tests/RetryPolicyTests.cs`. An out-of-scope edit fails the task immediately and
consumes a retry.
