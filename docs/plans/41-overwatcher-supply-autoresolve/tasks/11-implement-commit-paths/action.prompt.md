## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g. `11-implement-commit-paths`), NOT the
  stableId. The harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "11-implement-commit-paths": { "someKey": "someValue" } }`.
- EXCEPTION — the CONTROL KEYS `needsHarnessWrite` and `needsHuman` are TOP-LEVEL
  SIBLINGS of your folder-name key, never nested inside it.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- Guardrails constrain the OUTCOME, never HOW you implement it. Never reshape working
  code — or reword a document away from its own conventions — to match a check's pattern.
- If you cannot proceed without a human decision, write
  {"needsHuman": {"question": "<question>", "kind": "blocked-work"}} to the
  state-out path and stop. If instead a guardrail reports something ABSENT that you can
  see is PRESENT, that guardrail is defective: use "kind": "defective-guardrail" and
  quote (a) the guardrail's exact claim and (b) the file:line that refutes it. If you
  cannot produce BOTH quotes it is not a defective guardrail — retry the work, or
  escalate as "blocked-work". Difficulty is never "defective-guardrail".

## Task

Fill real logic over the `CommitPaths` stub on `src/Guardrails.Core/Execution/SuppliedDrain.cs`, and
refactor `Drain` to go through it — design 41 §5 ("Drain target and provenance").

**`CommitPaths(workspace, runId, by, paths)` does exactly this, in this order:**

1. Nothing to do on an empty `paths`: return `{ CommittedPaths = [], TotalBytes = 0, CommitSha = null }`
   and make **no git call at all**. That is `Drain`'s own never-weaker guarantee, moved down a level.
2. Capture the pre-commit `HEAD` (`git rev-parse HEAD`) BEFORE staging anything. You need it before the
   first mutation, not after the failure.
3. `git add -- <paths>`, then `git commit --no-verify -m <trailers> -- <paths>`.
   - **The explicit pathspec is the whole point.** Today's `Drain` commits with NO pathspec, so anything
     an operator (or a crashed earlier step) left in the index rides along and is attributed to `by`.
     Keep `--` before the paths so a path that looks like a flag is still a path.
   - `--no-verify` for the reason the existing code states: a harness-owned plumbing commit must not be
     gated by a machine-global git hook (#149).
   - The message is unchanged: `Supplied-By: {by}` then `Guardrails-Run: {runId}`.
4. **On ANY failure, `git reset --hard <preHead>` and let the exception propagate.** Both git calls are
   inside the protected region — an `add` that fails leaves a partly-staged tree just as a failed
   `commit` does. The caller (the Scheduler's auto-resolve, a later task) records `commit-failed` and
   the original halt stands; what it must never inherit is a staged file a LATER drain would commit
   under someone else's name. Roll back, then rethrow — do not swallow.
5. Return the committed paths as given, `TotalBytes` as the sum of those files' lengths on disk, and
   `CommitSha` from `git rev-parse HEAD`.

**`Drain` keeps its signature, its behaviour and its five tests, and stops owning a second commit
mechanism.** Copy the staged files to their workspace destinations exactly as it does today, then hand
the copied destination paths to `CommitPaths` and return what it returns (`Drain` still deletes the
staging tree afterward, and still returns early with no git call when nothing is staged). The operator
path gains the pathspec and the rollback for free — that is why this is a refactor and not a second
method: two commit mechanisms would be the thing that drifts.

Byte counts stay identical: `Drain` measured the STAGED file's length, and a copy has the same length as
its source, so summing the destinations inside `CommitPaths` is the same number.
`Drain_ReturnsTheCommittedPathsAndByteCount` pins it.

Do NOT edit the authored tests; emit `{"needsHuman": "<why>"}` if one is genuinely wrong.

**Scope boundary (harness-enforced):** Write only to `src/Guardrails.Core/Execution/SuppliedDrain.cs`. After this
task completes, the harness runs a `git diff` membership check and rejects any edit outside these paths. An
out-of-scope edit fails the task immediately and consumes a retry. If you hit a compile error caused by a
missing symbol in another file, do NOT edit that file — write `{"needsHuman": "<what is missing>"}` to the
state-out path and stop.

**The harness runs this task's guardrails itself when you finish.** Do not try to run the guardrail scripts yourself: the shell they need is not granted to you, and a call refused on two attempts can halt the task even after the work is done. Tests authored by OTHER tasks may legitimately fail on your base until their own implementing task lands; only this task's tests are yours to turn green.
