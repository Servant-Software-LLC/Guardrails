## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's WAVE-QUALIFIED id as the single
  top-level key (this plan is waved), e.g.
  `{ "wave-04-fix-the-advice/03-reconcile-promptcomposer-advisory": { "k": "v" } }`.
  The harness REJECTS a fragment keyed by anything else (every attempt).
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Task
`AppendWorktreeSafety` in `src/Guardrails.Core/Prompts/PromptComposer.cs` ships in EVERY worktree-mode
prompt and recommends a three-line recipe in which ALL THREE lines are unusable under the harness's own
defaults (grep for the method - do not trust a line number):

    git diff > /tmp/mine.patch      <- the redirect writes outside the worktree: blocked by the
                                       harness's own WorktreeContainmentHook redirect check
    git checkout -- <files>         <- ungranted on a clean box
    git apply /tmp/mine.patch       <- ungranted on a clean box

After wave 4 task 02, `RetryPolicy` tells the agent the opposite - so one prompt currently contradicts
itself. Reconcile it into ONE story:
- keep the `git stash` warning (it is correct and valuable);
- replace the recipe with one that works under the harness's own defaults, consistent with the salvage
  advice: use the granted read-only route and the agent's own file-editing tools, writing any scratch
  file INSIDE the worktree (never `/tmp`, which the containment hook blocks).

`tests/Guardrails.Core.Tests/PromptComposerTests.cs` pins the current text - update that assertion in
the same change (it is in your writeScope). Do not weaken unrelated assertions.

**Scope boundary (harness-enforced):** Write only to `src/Guardrails.Core/Prompts/PromptComposer.cs` and
`tests/Guardrails.Core.Tests/PromptComposerTests.cs`. An out-of-scope edit fails the task immediately
and consumes a retry.
