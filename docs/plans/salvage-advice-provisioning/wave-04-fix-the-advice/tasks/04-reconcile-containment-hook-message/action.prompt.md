## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's WAVE-QUALIFIED id as the single
  top-level key (this plan is waved), e.g.
  `{ "wave-04-fix-the-advice/04-reconcile-containment-hook-message": { "k": "v" } }`.
  The harness REJECTS a fragment keyed by anything else (every attempt).
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Task
`src/Guardrails.Core/Prompts/WorktreeContainmentHook.cs` generates the PreToolUse hook. Its `git stash`
BLOCK MESSAGE recommends the same stash-free recipe as the advisory
(`git diff > /tmp/mine.patch` -> `git checkout -- <files>` -> `git apply`) in BOTH the bash and the
PowerShell variants. Grep for the block message in each variant - do not trust a line number.

That recipe is self-defeating from this file above all: the hook's OWN redirect check blocks writing
`/tmp/mine.patch` (outside the worktree), and both git verbs are ungranted on a clean box. The
PowerShell variant's relative `TEMP/mine.patch` lands INSIDE the worktree, where it would then trip the
write-scope check as an out-of-scope path.

Reconcile both messages with the story wave 4 task 02 and 03 establish: point the agent at the granted
read-only route plus its own file-editing tools, and keep any scratch file inside the worktree and
inside the task's writeScope.

Change ONLY the message text. Do NOT alter the hook's matching/blocking logic, and do NOT add a
`git restore` matcher (that verb is not granted and is out of scope for this plan).

**Scope boundary (harness-enforced):** Write only to
`src/Guardrails.Core/Prompts/WorktreeContainmentHook.cs`. An out-of-scope edit fails the task
immediately and consumes a retry.
