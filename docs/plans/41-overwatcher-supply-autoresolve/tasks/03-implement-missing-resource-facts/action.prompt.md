## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g. `03-implement-missing-resource-facts`), NOT the
  stableId. The harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "03-implement-missing-resource-facts": { "someKey": "someValue" } }`.
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

Fill real logic in over the two throwing stubs task 02 committed, so `MissingResourceSignalTests` and
`MissingResourceFactsTests` pass. Design 41 §2.1 and §2.2 — **the two fact tables in §2.2 are the spec.**

**Do NOT edit the tests.** Your `writeScope` covers only the two `src/` files; the test files are
deliberately outside it, so the harness rejects any edit to them and the attempt is spent. If you believe
a test is genuinely wrong, emit `{"needsHuman": "<why, with the file:line>"}` rather than working around it.

### `MissingResourceSignal.PathsIn`

Move the path-token rule out of `RunCommand.MissingResourceHaltLines` (see `ResourcePathToken` and its
`Match` call in `src/Guardrails.Cli/Commands/RunCommand.cs`) and widen it in exactly three ways:

1. **Every** token in the question, in order, deduplicated — today's code takes `Match`, the first hit only.
2. A segment may contain `@`, so `node_modules/@scope/x/index.js` comes back whole.
3. A leading `./` is normalized away, so `./mermaid.min.js` yields `mermaid.min.js`.

The token still requires a `/` somewhere, so prose like `e.g.` or `Node.js` never matches. That property
is load-bearing — it is the only thing standing between "a blocked-work question mentioned a filename" and
"the harness went looking for a file to commit" — and it has its own pinned test.

**You are not editing `RunCommand.cs` in this task.** Rewiring the halt text onto this shared predicate is
a later task's job (design 41 §11 row 9); your job is to make the predicate exist and be correct.

### `MissingResourceFacts.Compute`

**Every git call is tri-state: present, absent, or ERROR.** An error — exit 128 for a dubious-ownership
repo, a missing worktree, a corrupt object — is *never* read as absent. It stops the whole consult:
`Available = false`, `UnavailableReason = "facts-unavailable"`, and no candidates. This is the single
most important property in the file. Reading an error as absent would let the harness commit a file onto
the run's base on evidence it never actually had, which is precisely the failure design 41 exists to
prevent.

**Run-level lineage facts**, checked once. If either fails, *every* path fails with that reason:

| fact | how | reason |
|---|---|---|
| the checkout is still on the branch the run started from | `git rev-parse --abbrev-ref HEAD` in `plan.Workspace` equals `originalBranch` | `checkout-not-on-run-branch` |
| the checkout has not left the run's starting history | `git merge-base --is-ancestor <originalHeadSha> <checkout HEAD>` | `checkout-diverged` |

**Per-path facts, checked IN ORDER — the first failure names the reason:**

| # | fact | how | reason |
|---|---|---|---|
| 1 | stays inside the workspace | `WorkspaceContainment.Escapes` | `escapes-workspace` |
| 2 | not a protected path | not under `.claude/`, `.guardrails-staging/` or `.guardrails-agent-io/`, and no top-level segment starting `.git` | `protected-path` |
| 3 | not under the plan folder | path containment against `plan.PlanDirectory` | `under-plan-folder` |
| 4 | no OTHER task may produce it | every *other* task declares a `writeScope`, and none covers the path (`WriteScope.IsInScope`) | `plan-scope-incomplete` when some other task declares none; `produced-by-another-task` when one covers it |
| 5 | absent from the run's base | no object at `HEAD:<path>` in the integration worktree | `present-on-run-base` |
| 6 | no case-only twin on the run's base | no case-insensitive match in `git ls-tree -r --name-only HEAD` of the integration worktree | `case-collision` |
| 7 | the run did not delete it | `git log --diff-filter=D --format=%H <merge-base of integration HEAD and checkout HEAD>..HEAD -- <path>` is empty | `deleted-on-run-base` |
| 8 | committed in the operator's checkout | an object exists at `<checkout HEAD sha>:<path>` | `not-committed-in-checkout` |
| 9 | that object is a file | `git cat-file -t` is `blob` | `not-a-blob` |
| 10 | unmodified in the checkout's working tree | `git diff --quiet HEAD -- <path>` exits 0 | `modified-in-checkout` |

**Check 4 is "no OTHER task", and it FAILS CLOSED.** The halted task's own scope is deliberately not
required: a task that *embeds* a vendored bundle declares the HTML it writes, not the bundle. But if any
other task declares no `writeScope` at all, the question "may something else produce this?" is
unanswerable, so the path is refused as `plan-scope-incomplete` rather than allowed through. This is
`d41-candidate-scope`, decided in review — do not re-derive it.

**Every path examined gets a `MissingResourcePathVerdict`**, candidate or refused. That list is what the
`observed` decision renders, one line per path, so a refusal that is simply dropped becomes a silent stop
at the dial that promised an auto-resolve.

**A candidate carries the checkout HEAD sha it was read at** (`SourceCommit`). The later commit reads the
blob at exactly that commit, so a sha the facts did not establish is a supply nobody verified.

`plan.Workspace` IS the operator's checkout. `integrationWorktreePath` is the run's own base. They are
never the same directory, and nothing in this component writes to either.

Do NOT edit the authored tests; emit `{"needsHuman": "<why>"}` if one is genuinely wrong.

**Scope boundary (harness-enforced):** Write only to `src/Guardrails.Core/Execution/MissingResourceSignal.cs` and `src/Guardrails.Core/Execution/MissingResourceFacts.cs`. After this
task completes, the harness runs a `git diff` membership check and rejects any edit outside these paths. An
out-of-scope edit fails the task immediately and consumes a retry. If you hit a compile error caused by a
missing symbol in another file, do NOT edit that file — write `{"needsHuman": "<what is missing>"}` to the
state-out path and stop.

**The harness runs this task's guardrails itself when you finish.** Do not try to run the guardrail scripts yourself: the shell they need is not granted to you, and a call refused on two attempts can halt the task even after the work is done. Tests authored by OTHER tasks may legitimately fail on your base until their own implementing task lands; only this task's tests are yours to turn green.
