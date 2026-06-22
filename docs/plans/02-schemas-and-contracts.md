# 02 — Schemas and Contracts (single source of truth)

Every schema and child-process contract in the Guardrails system is defined **here**.
The C# serializers (`src/Guardrails.Core`), the `plan-breakdown` and `guardrails-review`
skills, and the example plan folders all implement this document. If code or a skill
disagrees with this doc, one of them is wrong — fix in the same change.

JSON files are read with comments and trailing commas allowed (humans hand-edit them).
All harness writes are atomic (write temp file, then move over the target).

---

## 1. Plan folder layout

A *plan folder* is generated next to its source markdown plan (`<plan-name>.md` →
`<plan-name>/`):

```
plan-name/
├── guardrails.json              # run configuration (§2)
├── guardrails.baseline          # OPTIONAL committed breakdown manifest (§11)
├── diagram.md                   # OPTIONAL generated DAG diagram — non-authored (§10)
├── diagram.html                 # OPTIONAL interactive local viewer — non-authored (§10)
├── state/
│   ├── seed.json                # OPTIONAL committed initial state (§6.1)
│   ├── state.json               # runtime merged state — harness-owned, gitignored
│   ├── run.json                 # run journal — harness-owned, gitignored (§7)
│   └── merge-conflicts.log      # harness-owned, gitignored (§6.3)
├── logs/
│   └── <runId>/<task-id>/attempt-N/   # per-attempt artifacts (§8) — divided by runId, sibling of state/
└── tasks/
    └── <NN-verb-object>/        # task id = folder name, kebab-case, NN = topological hint
        ├── task.json            # task manifest (§3)
        ├── action.prompt.md     # or action.ps1 / action.sh / action.py / action.cmd / …
        └── guardrails/
            ├── 01-build-passes.ps1        # deterministic guardrail (§4)
            ├── 01-build-passes.json       # optional metadata sidecar (§4.1)
            └── 02-review.prompt.md        # prompt guardrail with YAML frontmatter (§4.2)
```

Task ids are their folder names. The `NN-` prefix is a human-scanning hint only;
`dependsOn` is the truth for ordering.

**Workspace must be a git repository top-level.** Parallel execution never writes the user's
checkout. At run start the harness creates a **plan branch** `guardrails/<plan-name>` off the
user's current HEAD and a **harness-owned integration worktree** on it; this is the sole merge
target and the terminal-gate site for the run. Each task runs in a **segment worktree**: a linear
chain **reuses one** segment worktree passed along the chain; a fan-out **inherits one** chain and
**forks the rest** off the producer's committed tip; a fan-in **forks one** upstream and merges the
others in. `runId` lives in worktree directory names and commit trailers, **not** the branch name.
`guardrails validate` and a run pre-flight reject a non-git-top-level workspace (**`GR2015`**, a
FRESH code — the old plan-07 draft cited `GR2013`, which is **taken on `master`** by the live triad
`CaptureHashEscapesWorkspace`). The harness creates all worktrees under a **harness-owned root
outside the workspace** — default `<temp>/guardrails-worktrees/<workspace-hash>/<runId>/`,
overridable via `guardrails.json: worktreeRoot`. Worktrees + the plan branch are runtime state
(wiped by `--fresh`, pruned on resume; the integration worktree is reattached, not pruned). The
user's own working tree and branch are **read-only for the entire run**; the only optional write to
the user's branch is `--merge-on-success` (§5.3). A `runOnCurrentBranch` opt-in makes the plan
branch the current branch but still integrates via a harness-owned worktree, never the user's live
checkout.

The per-attempt log tree moves out of `state/` to a top-level `logs/` sibling, **divided by
`runId`** (`logs/<runId>/<task-id>/attempt-N/`), so logs are findable and a re-run's logs never
interleave with a prior run's. `state/` holds only harness-owned mutable run state; `logs/` is
append-only audit. `--fresh` clears `logs/` for the abandoned run.

---

## 2. `guardrails.json` (run configuration)

```jsonc
{
  "version": 1,                       // required; schema version of this file
  "maxParallelism": 3,                // default 3 in worktree mode (chain-reuse keeps a linear chain to ONE tree)
  "defaultRetries": 2,                // retries AFTER the first attempt; default 2
  "defaultTimeoutSeconds": 1800,      // per-attempt ceiling when nothing narrower applies
  "maxCostUsd": 5.00,                 // OPTIONAL per-run cost ceiling, decimal USD; absent = no cap
  "guardrailMode": "failFast",        // "failFast" (default) | "runAll"
  "workspace": "..",                  // cwd for all child processes, relative to the plan dir
  "worktreeRoot": null,               // OPTIONAL; override the git-worktree root. null = <temp>/guardrails-worktrees/<hash>/<runId>/
  "runOnCurrentBranch": false,        // OPTIONAL; if true the plan branch IS the current branch (still integrated via a harness-owned worktree)
  "mergeOnSuccess": false,            // OPTIONAL; if true AND the whole run goes green, merge plan branch guardrails/<plan-name> into the user's original branch at run end (ff-only when possible; AI-merge is NOT used here)
  "triageAutoFile": false,            // OPTIONAL; opt-in auto-file of the needs-human triage GH issue (§9). Default OFF = draft into feedback.md only; gated behind a configured GH repo + token when on
  "interpreters": {                   // EXTENDS/OVERRIDES built-in defaults (§5.2)
    ".ps1": ["pwsh", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "{script}", "{args}"]
  },
  "promptRunners": {                  // §9
    "default": "claude",
    "claude": {
      "command": "claude",
      "permissionMode": "acceptEdits",
      "allowedTools": ["Read", "Edit", "Write", "Grep", "Glob", "Bash(dotnet *)"],
      "maxTurns": 50,
      "model": null,                  // null = CLI default
      "extraArgs": [],
      "guardrailOverrides": {         // tighter profile for verdict-only guardrail prompts
        "permissionMode": "default",
        "allowedTools": ["Read", "Grep", "Glob", "Write"],
        "maxTurns": 20
      }
    }
  }
}
```

<!-- canonical-schema:promptRunners — the `"promptRunners": { … }` block above (from its
     `"promptRunners":` line through its matching close, leading 2-space indent included) is the
     CANONICAL copy. `.claude/skills/plan-breakdown/references/schemas.md` mirrors it byte-for-byte
     between its `canonical-schema:promptRunners` sentinels (drift-tested). Edit here first. -->

- `workspace` is the repo/directory the plan operates ON (typically the folder that
  contains the plan folder). Children run with cwd = workspace; everything
  Guardrails-specific arrives via absolute paths in env vars (§5.1).
- `guardrailMode: failFast` stops at the first failing guardrail of a task attempt
  (guardrails are ordered cheapest-first by filename convention); `runAll` runs every
  guardrail and aggregates all failures into one feedback document.
- `maxCostUsd` caps total spend for the run. When the journal's cumulative cost — the sum of
  every attempt's `costUsd` (§7) — reaches or exceeds it, the harness stops launching new
  attempts: each not-yet-launched task settles `needs-human` (reason "cost cap reached") and
  its transitive dependents `blocked`, via the same halt path as any other needs-human task. An
  attempt already in flight is never interrupted — the cap gates new launches, not running work.
  Absent ⇒ no cap. A present non-positive value is a validation error (GR2012).
- `worktreeRoot` overrides where the integration + segment worktrees are created. Each task's child
  processes run with cwd = its segment worktree; the integration worktree (plan branch
  `guardrails/<plan-name>`) is written only by the harness's integration (§5.3).
- `runOnCurrentBranch` (default `false`) makes the plan branch the current branch instead of a fresh
  `guardrails/<plan-name>`; the harness still integrates via a harness-owned worktree, never the
  user's live checkout. **Pre-flight:** if `runOnCurrentBranch` is set AND the current branch has
  uncommitted changes, the harness PROMPTS for explicit permission at run start (interactive) or
  REFUSES and halts (non-interactive, unless an explicit `--yes`/auto-confirm is given) — because the
  end-of-run integration merges back into the current branch and a dirty tree invites merge
  complications. **GR2016** (warning): a deep `worktreeRoot` + deep source tree risks exceeding
  Windows MAX_PATH (260 chars); document `core.longpaths` as the mitigation.
- `mergeOnSuccess` (default `false`; CLI `--merge-on-success` overrides) opts into end-of-run
  delivery of the plan branch into the user's original branch. **AI-merge is withheld at this
  boundary** — a conflict, a failed post-merge re-verify, or a dirty user tree halts to `needs-human`
  with the plan branch intact; never a force-overwrite, never an AI auto-resolve of the user's commits.
- `maxParallelism` defaults to **3** because chain-reuse keeps a linear chain to one worktree; the
  peak tree count is the DAG's max antichain width + the integration worktree. Drop to 2 on a
  disk-constrained box; raise on a fast/large `worktreeRoot` volume.

## 3. `tasks/<id>/task.json`

```jsonc
{
  "description": "Implement the --stats flag",   // required, one line, human + feedback use
  "stableId": "k3f9a1",        // optional; stable task identity for the regeneration merge (§11)
                               //   format ^[a-z0-9][a-z0-9._-]*$ (GR2011); unique (GR2010)
  "dependsOn": ["01-author-stats-tests"],        // required (may be []); task ids
  "integrationGate": false,    // optional, default false; marks a terminal whole-repo integration gate (§3.3)
  "writeScope": ["src/Foo/"],  // optional; the deterministic write-scope check (§3.4). Absent ⇒ NO check.
                               //   every path the action's post-action diff (staged worktree vs <taskBase>)
                               //   adds/modifies/deletes/renames must be IN scope, or the task fails and
                               //   retries with feedback after a SCOPED REVERT of the out-of-scope paths
                               //   (in-scope WIP preserved). Renames = paired D+A (both in scope). A vacuous
                               //   "**" / bare top-level dir is a granularity smell.
  "retries": 3,                // optional; overrides defaultRetries
  "timeoutSeconds": 3600,      // optional; whole-attempt ceiling (action + guardrails)
  "action": {                  // OPTIONAL — omit to use convention discovery:
                               //   exactly ONE file named action.* in the task folder;
                               //   zero or multiple action.* files = validation error
    "path": "action.prompt.md",      // relative to task dir; kind derived from extension
    "args": [],                      // deterministic actions only
    "runner": "claude",              // prompt actions only; default = promptRunners.default
    "maxTurns": 80,                  // prompt actions only
    "timeoutSeconds": 2400,          // narrower than task timeout
    "workingDirectory": null,        // overrides config workspace (rare)
    "env": { "MY_VAR": "value" }     // extra env vars for this action's process
  }
}
```

**Action kind by extension**: `.prompt.md` → prompt; anything else → script/executable
resolved through the interpreter map (§5.2). A task **must** have an action and
**at least one guardrail** — zero guardrails is a validation **error** (a task that
can't be verified has no business in the DAG).

`stableId` is an **optional** identity that survives renumbering and slug edits across
regenerations — the key the merge (§11) uses to recognize "this is the same task, slightly
altered" versus "this is a new task". It is reserved for that merge and the runtime does not
yet consume it, but because the merge keys identity on it, `validate` **does** enforce two rules
on any declared `stableId`: it must be **unique** across tasks (a duplicate is a `GR2010` error —
almost always a copy-paste slip), and it must match `^[a-z0-9][a-z0-9._-]*$` (lowercase
alphanumerics, optionally with `.` `_` `-`; a `GR2011` error otherwise). The format is reserved so
a real id can never collide with the merge's synthetic `folder:<name>` identity (the colon is
disallowed). `validate` does not *require* one. Absent ⇒ task identity falls back to the folder
name — see §11.3 for why minting one is still recommended.

*(Former §3.1/§3.1.1 — the `captureHashes`/`restoreOnRetry` triad — are **removed in this change**,
along with the harness `CapturedFileStore`/`FileHashCapture`/`RestoreAncestorCaptures`/`WorkspaceLock`
and the GR2013/GR2014 triad diagnostic meanings. Test files are now protected by (i) physical
worktree isolation and (ii) the §3.4 write-scope check: an implementation task's `writeScope` excludes
the test files, so an edit to them fails the deterministic check.)*

### 3.2 Worktree task semantics

The harness creates one integration worktree per run (plan branch `guardrails/<plan-name>`) — the
sole merge target. Each task runs in a **segment worktree**: a linear chain reuses one segment
worktree (the downstream task commits on top of the upstream's tip in the SAME tree — no inter-hop
merge, no inter-hop re-verify, sound because no union is formed); a fan-out **inherits one** chain
(the longest-downstream successor reuses the producer's segment worktree directory; ordinal-id
tiebreak) and **forks the rest** off the producer's **recorded** committed sha (never the live
segment-branch tip, which the inheritor may have advanced); a **fan-in** task forks a fresh segment
off the **plan-branch tip**, which already contains every producer's integrated work (the producers'
own settles unioned it onto the plan branch), so the fan-in sees the merged tree without a separate
private merge. *(A private pre-merge worktree — `CreateFanIn` — is defined on the provider but is
**not wired in v1**; the plan-branch union is the v1 fan-in mechanism. See plan 08
`topology-wiring-design.md` Decision F.)* A failed attempt does NOT discard the worktree — the harness `git reset --hard
<taskBase> + git clean -fd` (preserving every upstream/sibling commit in the tree; `taskBase` is the
task's start commit, distinct from the plan-branch `preHead`). A task that depends on another reads
the producer's MERGED outputs (its worktree descends from the producer's committed tip). No
cross-task `actionExitCode` channel exists. The user's checkout is never written; the plan branch's
trailer-bearing commits (plain FF'd commits AND merge commits) are the durable resume record (§7).
At run end the harness sweeps the segment worktree directory of every task that settled **green** (its
work is durable on the plan branch, so the directory is pure waste — the direct fix for **#126**),
then prunes the registrations; a **non-green** (needs-human/failed/blocked) task's worktree is left in
place as the fix/resume inspection surface, and the integration worktree is never swept. A cancelled
run skips the sweep entirely (its in-flight worktrees are reclaimed by the next run's resume prune).

### 3.3 Terminal integration gate (`integrationGate`)

`integrationGate: true` marks a task as the terminal whole-repo integration gate — the final
soundness boundary run once on the fully merged plan-branch HEAD after all other tasks succeed. The
gate task's guardrails are exactly the run's **integration-guardrail set** (§4.3): all guardrails
declared `scope: "integration"` across the plan, typically the whole-repo build and the full test
suite.

**Hard validation requirements** (both are errors, not warnings, because the terminal gate is the
sole whole-repo soundness boundary for FF chains and AI-resolved unions):

- **GR2017** (error): a plan with ≥2 leaf tasks, or any fan-in task, MUST declare **exactly one**
  `integrationGate: true` sink. A plan with a single linear chain and no fan-in may omit it. Missing
  gate on a multi-leaf/fan-in plan → `validate` error naming the missing sink.
- **GR2018** (error): the `integrationGate` sink MUST carry **at least one** `scope: "integration"`
  guardrail (§4.3). An integration gate with no integration-scoped guardrail would verify nothing —
  the gate is the terminal soundness boundary and must not be empty.

### 3.4 Write-scope check (`writeScope`)

`writeScope` is an optional list of **workspace-relative path prefixes / globs** declaring the
surface a task is permitted to add/modify/delete/rename. It drives a **deterministic harness check**:
after the task's action and **before** its own `guardrails/`, the harness inspects the action's
**uncommitted** writes in the segment worktree and asserts every changed path satisfies
`IsInScope(path, writeScope)`. Because the check runs **before** the segment commit, the action's
output is not yet on `segmentHEAD` (HEAD == `taskBase` at this point); a `taskBase..segmentHEAD`
commit diff would be empty and pass vacuously. The harness therefore stages the worktree
(`git add -A`) and diffs the **index against `taskBase`**
(`git diff --cached --name-status --no-renames <taskBase>`), which surfaces modified, deleted, AND
new/untracked paths. Staging is not a content rewrite, and the Scheduler stages + commits the same
tree on the pass path anyway. A violation is a guardrail-class failure: the harness performs a
**scoped revert** that undoes ONLY the out-of-scope paths — an out-of-scope MODIFY/DELETE is restored
with `git checkout <taskBase> -- <path>`, a newly-ADDED out-of-scope file is removed with
`git rm -f -- <path>` — leaving same-attempt **in-scope WIP intact**, then retries with feedback
naming the out-of-scope paths (eventual `needs-human`).
**Absent ⇒ no check** (the off-switch — a task that can't be confidently scoped omits the field and
is reported as a broad surface, never given a vacuous `**`). **Renames** are NOT detected via git
`-M`; a rename presents as a paired **D + A**, and **both** paths must be in scope. **Deletions:**
the deleted path must be in scope. The declared scope is also injected into the action prompt
(advisory) — the deterministic check is the gate. `validate` rejects a scope entry that escapes the
workspace (**GR2019**, error) and warns on a vacuous/over-broad scope (**GR2020**, warning;
`plan-breakdown` should omit rather than emit a vacuous scope). **TDD test-protection:** a
test-author task owns its test files in `writeScope`; the implementation task's `writeScope` EXCLUDES
the test files, so the check deterministically enforces "the implementation may not write the tests"
(the replacement for the `captureHashes`/`tests-untouched`/`restoreOnRetry` triad **that this same
change deletes** — the triad was live on `master`). The matcher (`IsInScope`/`Overlaps`/segment-matcher)
is specified in full in plan 08 §2.1 (glob grammar, the 27-row truth table) and carries the §2.2
proof harness (the 27-row table + the two fuzz properties: membership-implies-overlap AND
`Overlaps`-completeness). It is read-only, so a matcher bug can only false-red or miss-catch ONE
task's own verdict — never write another task's files; `Overlaps` (the scheduler hint) retains
cross-task reach and keeps the full fuzz rigor.

## 4. Guardrails

Files under `tasks/<id>/guardrails/`, executed in filename sort order (**ordinal**,
locale-independent — task folders sort the same way). Convention: order
cheapest-first (`01-exists`, `02-builds`, `03-tests`, `04-review`).

A guardrail's **name** (used in the journal, feedback, and UI) is its filename minus
the extension, with `.prompt.md` stripped as a whole:
`02-tone-is-friendly.prompt.md` → `02-tone-is-friendly`; `01-build.ps1` → `01-build`.
Every guardrail file **opens with a `catches:` comment** stating what wrong
implementation it catches (script comment or frontmatter field) — if you can't
write that sentence, the guardrail is decorative and should be deleted.

**Pass/fail contract (deterministic)**: exit code `0` = pass, non-zero = fail.
On failure, print a one-line *actionable* reason to stdout — that text becomes the
retry feedback ("greeting.txt missing 'Hello'" beats "FAIL").

### 4.1 Metadata sidecar (deterministic guardrails, optional)

`<guardrail-basename>.json` next to the script:

```jsonc
{
  "description": "Solution builds clean",
  "args": ["--configuration", "Release"],
  "timeoutSeconds": 600
}
```

### 4.2 Prompt guardrails (`*.prompt.md`)

YAML frontmatter (all keys optional) + prompt body:

```markdown
---
description: LLM review of the report tone
runner: claude
maxTurns: 20
timeoutSeconds: 900
---
You are a verifier. Read the report at out/report.md and judge ONLY whether ...
```

**Verdict contract**: a prompt guardrail MUST end by writing

```json
{ "pass": false, "reason": "Report never names the failing task." }
```

to the file at `GUARDRAILS_VERDICT_OUT`. Missing file, invalid JSON, or missing
`pass` ⇒ the guardrail **fails** with reason "guardrail produced no valid verdict".
CLI exit codes are never used for semantic pass/fail of prompt guardrails — exit
codes only distinguish "ran" from "crashed".

### 4.3 Guardrail scope (`scope: "integration" | "local"`)

A guardrail declares an optional `scope` (deterministic sidecar key §4.1, or prompt frontmatter
§4.2): `"local"` (default) or `"integration"`. The run's **integration-guardrail set** = the union
of all `scope: "integration"` guardrails across the plan (typically the whole-repo build + the
whole test suite). At **every union point** (a fan-in or a non-FF plan-branch integration, §5.3), on
the merged bytes, BEFORE the merge commit and BEFORE any downstream action, the harness re-runs (via
the attempt-decoupled re-verify seam): (1) the union task's full guardrail set; (2) **every colliding
sibling's FULL guardrail set — UNCONDITIONALLY, with NO touched-files filter** (the AI may have
dropped a colliding sibling's contribution, leaving the sibling's test file untouched — a
touched-files skip would miss exactly that); and (3) the integration-guardrail set. **The
touched-files local-skip applies ONLY to a distant, NON-colliding task's `local` guardrails** (re-run
only if the merge touched that task's files); it is **never** applied to a colliding sibling. The
terminal `integrationGate` sink (§3.3) runs the **same** integration set on the final merged HEAD —
the terminal gate and the per-union re-verify are one mechanism at two scopes. Because the re-verify
runs on arbitrary union bytes outside any attempt lifecycle, it uses a **public attempt-decoupled
re-verify seam** (NOT the attempt-bound internal guardrail runner). The re-verify child process runs
with cwd = the integration worktree and `GUARDRAILS_WORKSPACE` set to that same path (#124) — so a
guardrail reading `$GUARDRAILS_WORKSPACE` resolves files identically in-attempt and at re-verify; the
`GUARDRAILS_ACTION_*` attempt-lifecycle vars stay deliberately absent (there is no action at a union
point). `plan-breakdown` marks the build/test guardrails `scope: "integration"`; `guardrails-review`
flags an integration-sensitive plan with no integration-scoped guardrail (BLOCKER).

---

## 5. Child-process contract

### 5.1 Environment variables (all paths absolute)

| Variable | Set for | Meaning |
|---|---|---|
| `GUARDRAILS_PLAN_DIR` | all | Plan folder root |
| `GUARDRAILS_TASK_ID` | all | Current task id |
| `GUARDRAILS_TASK_DIR` | all | Current task folder |
| `GUARDRAILS_ATTEMPT` | all | 1-based attempt number |
| `GUARDRAILS_STATE_IN` | all | Read-only merged-state **snapshot copy** taken at attempt start; immutable for the attempt |
| `GUARDRAILS_STATE_OUT` | actions | Path the action may write its JSON fragment to (§6.2). Not pre-created; absence after success = "nothing to contribute" |
| `GUARDRAILS_STATE_FRAGMENT` | guardrails | Path of the action's (not-yet-merged) fragment, if the action wrote one — lets a guardrail validate proposed state |
| `GUARDRAILS_LOG_DIR` | all | `logs/<runId>/<task>/attempt-N/` — scratch space welcome |
| `GUARDRAILS_WORKSPACE` | worktree mode (in-attempt AND re-verify) | The effective worktree directory. In-attempt: the task's isolated SEGMENT worktree (where the action writes files that `Integrate` commits). At re-verify (§4.3): the INTEGRATION worktree the union bytes were merged into. Set in BOTH contexts to the same kind of value (the effective workspace = cwd) so a guardrail reading `$GUARDRAILS_WORKSPACE` behaves identically in-attempt and at the union point (#124). Absent in serial shared-workspace mode (cwd is the plan `workspace`) |
| `GUARDRAILS_FEEDBACK` | actions, attempt ≥ 2 | Path to `feedback.md` describing the previous attempt's failures |
| `GUARDRAILS_ACTION_STDOUT` | guardrails | The action's captured stdout file |
| `GUARDRAILS_ACTION_STDERR` | guardrails | The action's captured stderr file |
| `GUARDRAILS_ACTION_RESULT` | guardrails | `action-result.json`: `{ "kind", "exitCode", "summary" }` |
| `GUARDRAILS_VERDICT_OUT` | prompt guardrails | Where the verdict JSON must be written (§4.2) |
| `GUARDRAILS_MERGE_BASE` | AI-merge worker | Path to the merge-base copy of the conflicted file on disk (§9.1) |
| `GUARDRAILS_MERGE_OURS` | AI-merge worker | Path to the "ours" copy of the conflicted file on disk (§9.1) |
| `GUARDRAILS_MERGE_THEIRS` | AI-merge worker | Path to the "theirs" copy of the conflicted file on disk (§9.1) |
| `GUARDRAILS_MERGE_OUT` | AI-merge worker | Path the worker writes its resolved merged bytes to (§9.1); the harness reads this file |

**Recorded action outcome — verify, don't replay (issue #62).** `GUARDRAILS_ACTION_RESULT`
/ `_STDOUT` / `_STDERR` hand a guardrail the action's *already-captured* result, so it can
verify a postcondition by inspecting what the action produced instead of re-running the
action's command. Two honesty constraints the guardrail catalogue expands on:
- The action's `exitCode` here is **always 0** — a non-zero action fails the attempt
  *before* any guardrail runs — so a guardrail must never re-assert the exit code (a
  tautology); it verifies recorded *output/artifacts* or upstream state.
- Verify-don't-replay is a speed/flake trade-off, sound only when the postcondition is
  expressible from recorded output the action could not fabricate (a produced artifact, a
  runner-written result file such as a TRX, an upstream state value) — **not** the action's
  own self-reported success line in `_STDOUT`, which is an echo-judge. When the strong
  postcondition isn't expressible from recorded output, re-executing reality is the honest gate.

cwd = resolved `workspace`. Process arguments are passed via `ArgumentList`
(never a concatenated shell string). All child `stdout`/`stderr` is decoded as
UTF-8 and all `stdin` is written as UTF-8 (no BOM), independent of the host console
code page (e.g. the Windows OEM page CP437/850) — so the captured artifacts (§8)
round-trip non-ASCII faithfully and match the harness's own UTF-8-no-BOM writes
(`AtomicFile`). For prompt processes, the same information is *embedded in the
composed prompt* (agents read instructions, not env vars).

### 5.2 Interpreter map (built-in defaults)

| Extension | Command line (first available wins) |
|---|---|
| `.ps1` | `pwsh -NoProfile -ExecutionPolicy Bypass -File {script}` → fallback `powershell.exe …` (Windows only) |
| `.sh` | `bash {script}` |
| `.py` | `python3 {script}` → fallback `python {script}` |
| `.cmd` / `.bat` | `cmd /c {script}` (Windows only; validation error elsewhere) |
| `.dll` | `dotnet {script}` |
| none / `.exe` | direct spawn |

`guardrails.json: interpreters` extends/overrides these. `{script}` and `{args}` are
substitution tokens (`{args}` defaults to appending after the script path).
`guardrails validate` reports any extension used by the plan whose interpreter is
not resolvable on PATH.

### 5.3 Harness writes to the workspace — two bounded cases

**The harness writes only the harness-owned integration worktree (plan branch
`guardrails/<plan-name>`), via integration, after a task's action and guardrails succeed in its
segment worktree — and never otherwise. The user's checkout is read-only for the entire run.**

There are two kinds of integration. **(A) Fast-forward** (a linear chain's commit, no sibling has
advanced the plan branch): `git merge --ff-only` — **no new union, no re-verify** (the bytes already
passed the task's guardrails in the segment worktree). **(B) Union** (a fan-in, or a non-FF
integration where a sibling raced): a real merge that MUST be re-verified on the merged bytes before
the commit.

**Union resolution: git auto-merge → AI-merge → human.** `git merge --no-commit`; on conflict, the
**AI-merge worker** (a constrained prompt behind `IPromptRunner`, §9.1) produces merged BYTES only,
trusted via two **deterministic** checks — (i) no conflict markers remain (`git diff --check`),
(ii) blast-radius: it modified only the git-reported-conflicted files (`git status --porcelain`); an
out-of-bounds write or a remaining marker ⇒ discard (`reset --hard`) + needs-human. 1 retry. The AI
resolves harness-internal unions only; it is **withheld** at the `--merge-on-success` user-branch
boundary.

**The verdict (identical for clean-auto and AI-resolved) is the deterministic re-verify:** re-run
the union task's own guardrails + the run's **integration-guardrail set** (§4.3) on the `--no-commit`
merged bytes, then assert `git status --porcelain` shows only the staged merge (W3 read-only check).
Any re-verify fail / remaining conflict / dirtied tracked file ⇒ `git reset --hard preHead`;
`needs-human`; write no fragment, consume no `mergeSequence`. AI-merge + its re-verify run in the
fan-in's **private forked worktree OFF the serialize lock**; only the integration of the verified
result into the plan branch is **under the lock**, with a staleness re-verify against the current
plan-branch bytes.

**The atomic settle (state + git + journal as one ordered unit, under the serialize lock).** On
success, in this FIXED order: (1) deep-merge the task's fragment into `state.json`; (2) `git commit`
the integration (the FF move for case A, the merge commit for case B) carrying the parseable
`Guardrails-Task: <taskId>` / `Guardrails-Run: <runId>` trailer — **written on the plain FF'd commit
as well as on merge commits**, so resume can read FF integrations (§7); (3) consume the
`mergeSequence` + journal `Succeeded`. The fragment merge precedes the commit so the resume pre-pass
can never treat a task succeeded-by-commit while its state is missing. Every non-success path is a
single `git reset --hard preHead` (NOT `merge --abort`, which fails rc=128 on the dirtied-tracked
path) — leaving state, git, and journal all UNCHANGED, never half-merged, and the user's checkout
untouched. A git/IO failure during integration is a `needs-human` halt routed through the normal
failed path, never an uncaught throw.

**Retry preserves upstream work:** a failed attempt is `git reset --hard <taskBase> + git clean -fd`
in its segment worktree (keeping every upstream/sibling commit; `taskBase ≠ preHead`), not a
discard-and-recreate.

**Run end (opt-in delivery).** When the run drains wholly green AND `mergeOnSuccess`/
`--merge-on-success` is set, the harness merges the plan branch into the user's original branch
(ff-only when possible, else a real merge whose re-verify must pass). **AI-merge is NOT used here.**
A conflict / failed re-verify / dirty user tree halts to `needs-human`, plan branch intact — never a
force-overwrite. Default OFF leaves the plan branch for the user to review and merge. The merge-back
outcome is reported as `MergeOnSuccessResult` (`FastForwarded` / `Merged` / `Conflict` /
`DirtyWorkingTree`); a dirty user working tree is refused **before any git merge runs** (the harness
never runs git over uncommitted user work) and returns `DirtyWorkingTree`.

Any new capability that needs the harness to write outside the integration worktree or the opt-in
end-of-run delivery to the user's branch must be added to this section with its own containment
analysis — the default remains that the harness does not mutate the user's checkout.

---

## 6. State

### 6.1 Lifecycle

- `state/seed.json` (optional, **committed**): initial state authored with the plan.
- `state/state.json` (runtime, gitignored): the merged state. Created at run start
  from `seed.json` (or `{}`) when missing. `guardrails run --fresh` deletes runtime
  state and re-seeds. The `--fresh` deletion list is: `run.json`, `state.json`,
  `merge-conflicts.log`, and the `logs/<runId>/` tree for the abandoned run.
- The **harness is the single writer** of `state.json`. Child processes never touch it.

### 6.2 Fragments (snapshot in, fragment out)

Each attempt receives an immutable snapshot (`GUARDRAILS_STATE_IN`). An action that
wants to publish state writes a JSON **object** to `GUARDRAILS_STATE_OUT`, with every
top-level key namespaced under its own task id —

```json
{ "02-generate-greeting": { "greetingPath": "out/greeting.txt" } }
```

**Single-writer-per-key (ENFORCED).** A merged fragment's top-level keys must each be the
writing task's **own id** (or a harness reserved key — **none in v1**, see
`ReservedMergeKeys` below). A fragment with **any other** top-level key — a **foreign task
id** OR an arbitrary **shared** (non-task) key — fails as **invalid-fragment** and is **NOT**
merged (the attempt fails, retries with feedback naming the stray key, and nothing reaches
`state.json`). The fragment is **rejected, not stripped**. This makes the harness the single
writer of every task's namespace, closing the #48 cross-task poisoning vector: no task can
overwrite another task's captured `fileHashes` (or any derived key) by writing under that
task's id. `needsHuman` is **exempt** — it short-circuits the attempt (§9) *before* the merge
step, so it is never subject to this rule.

A fragment that exists but is not a parseable JSON object ⇒ the attempt **fails**
(reason: "invalid state fragment") and is retried — better than silently dropping data.
An **empty** object `{}` passes vacuously (no keys) and merges nothing. The fragment is
merged only after **all guardrails pass**.

**`ReservedMergeKeys`** is the harness allowlist of top-level keys permitted in addition to
the writing task's own id. It ships **EMPTY** in v1 — there is deliberately no shared writable
namespace. Any future reserved key MUST carry its own anti-poisoning analysis before admission:
a shared writable key is exactly the cross-task poisoning vector this rule closes.

### 6.3 Merge policy (deterministic)

Deep merge into `state.json`: objects merge recursively; **scalars and arrays are
last-writer-wins**. Merge order = task completion order, recorded as a monotonic
`mergeSequence` in the journal. Every overwrite of an existing non-null value with a
*different* value is appended to `state/merge-conflicts.log` — tab-separated columns
`seq, task, jsonPath, old, new`, with values as compact JSON.

With single-writer-per-key enforced (§6.2), last-writer-wins is reachable only **WITHIN a
task's own namespace** (a task overwriting a value it previously wrote under its own id) or
against committed **`seed.json`** content under that namespace — **never cross-task at the
root**. A conflict row's `jsonPath` therefore always begins with the writing task's own id
(e.g. `01-author.fileHashes."Tests.cs"`).

---

## 7. `state/run.json` (journal)

```jsonc
{
  "version": 1,
  "runId": "2026-06-10T16-22-31Z-a1b2",
  "planHash": "sha256:…",          // hash of guardrails.json + all task.json; mismatch on resume ⇒ loud warning
  "nextMergeSequence": 3,
  "tasks": {
    "01-write-greeting-script": {
      "status": "succeeded",        // pending | running | succeeded | needs-human | blocked | failed
      "mergeSequence": 1,
      "attempts": [
        {
          "attempt": 1,
          "startedAt": "…", "endedAt": "…",
          "actionExitCode": 0,
          "outcome": "succeeded",   // succeeded | action-failed | guardrail-failed | timeout | cancelled | invalid-fragment | needs-human
          "failedGuardrails": [ { "name": "02-tests-exist", "reason": "no *.Tests.csproj found" } ],
          "costUsd": null,          // prompt attempts: total_cost_usd from the runner
          "logDir": "logs/2026-06-10T16-22-31Z-a1b2/01-write-greeting-script/attempt-1"
        }
      ]
    }
  }
}
```

**Status semantics**
- `succeeded` — terminal. Resume skips it; `guardrails reset <folder> <task>` is the
  explicit way to force a re-run.
- `needs-human` — retry budget exhausted. All *transitive* dependents become `blocked`.
  Independent branches keep running.
- Resume rules (`guardrails run` on an existing journal): `succeeded` → skip;
  `needs-human` / `failed` / `blocked` → `pending` with a fresh retry budget;
  `running` (crashed previous run) → `pending`, attempt numbering continues.

**Harness exit codes**: `0` all succeeded · `1` harness/validation error ·
`2` the operation completed but an actionable condition was found — for `run`: a task is
needs-human/blocked; for `graph --check`: the diagram is stale or missing (the "regenerate"
signal); for `lock --check`: the folder has drifted from the baseline or the baseline is missing
(the "re-baseline" signal); for `merge`: there are unresolved conflicts to resolve, or the BASE
baseline is missing and must be established first (§11.5) · `3` cancelled.

**Plan-file → task-folder argument fixup** (all commands taking a plan folder as their first
positional: `run`, `validate`, `plan`, `graph`, `lock`, `merge`, `logs`). Before the folder's existence
is checked, the CLI applies one fixup so a user who passes the authored plan *source file*
instead of the generated *task folder* is not blocked: when the argument ends with `.md`
(ordinal, case-insensitive) **or** resolves to an existing file rather than a directory, and a
sibling directory with the same stem exists (`plans/0003-foo.md` → `plans/0003-foo/`), the
command silently switches to that folder and prints one info line
(`info: resolved plan file → task folder "<folder>"`). When no such sibling folder exists the
argument is passed through unchanged, so a genuinely bad path still produces the existing
`GR1001` "Plan folder does not exist" error (issue #16).

---

## 8. Per-attempt log layout

```
logs/<runId>/<task-id>/attempt-N/
├── state-in.json            # the snapshot given to this attempt
├── action-stdout.log / action-stderr.log
├── action-result.json
├── action-out-fragment.json # the LIVE GUARDRAILS_STATE_OUT target the action writes
├── fragment.json            # copy of the fragment made on successful merge — audit trail
├── composed-prompt.md       # prompt ACTION: exactly what the runner got
├── claude-stream.jsonl      # prompt ACTION: raw runner output stream (canonical debug artifact)
├── transcript.md            # prompt ACTION: CLI-equivalent view, rendered deterministically from the stream (#27)
├── guardrail-<name>.stdout.log / .stderr.log   # script guardrail: captured output
├── composed-prompt.<name>.md                   # prompt guardrail: exactly what the verifier got
├── guardrail-<name>.stream.jsonl               # prompt guardrail: raw runner output stream
├── guardrail-<name>.transcript.md              # prompt guardrail: deterministic transcript projection
├── guardrail-<name>.verdict.json               # prompt guardrail: the verdict file (§4.2) — the ONLY pass/fail authority
└── feedback.md              # composed failure feedback (input to the NEXT attempt)
```

Prompt **actions** write `composed-prompt.md` / `claude-stream.jsonl` / `transcript.md`. Prompt
**guardrails** write the same three artifacts *per guardrail*, namespaced by the guardrail's
`<name>` (filename minus extension, §4): `composed-prompt.<name>.md`,
`guardrail-<name>.stream.jsonl`, `guardrail-<name>.transcript.md`, plus the
`guardrail-<name>.verdict.json` verdict file. Script guardrails write
`guardrail-<name>.stdout.log` / `.stderr.log`. The `<name>` is sanitized for the filesystem (any
character other than a letter, digit, `-`, `_`, or `.` becomes `_`).

`transcript.md` (and each `guardrail-<name>.transcript.md`) is a PURE, DETERMINISTIC projection of
its `*.jsonl` stream (no model in the loop): assistant prose + `● Tool(args)` + truncated `⎿`
tool-result summaries + the final result text; thinking blocks and all telemetry (thinking-token
counters, rate-limit/init/usage events) are dropped. It is what a human skims and what a dependent
task's prompt links to (§9, #26) — the raw stream stays as the debug artifact.

---

## 9. Prompt runners

`promptRunners` (§2) maps names to runner configs. The `IPromptRunner` C# interface
quarantines all CLI specifics (flag spelling, output parsing). v1 ships `claude`:

- Invocation: `claude -p --output-format stream-json --verbose --permission-mode <m>
  --allowedTools <list> --max-turns <n> [--model <m>] [extraArgs…]`
- Prompt delivered via **stdin** (no arg-length/quoting issues).
- cwd = workspace; `--add-dir <planDir>` grants access to state/verdict paths.
- The composed prompt (§8 `composed-prompt.md`) = body + appended harness sections:
  shared state (inlined ≤ 16 KB, else by path), **dependency context** (actions: pointers to
  the transitive `dependsOn` closure's `transcript.md` + contributed `fragment.json`, present
  on every attempt — #26 Gap 4), output contract (actions), previous-attempt feedback (actions,
  attempt ≥ 2: the latest `feedback.md` verbatim + pointers to ALL prior attempts' transcript
  and feedback — #26 Gaps 2 & 3, "fix these specific problems; do not start over"), verdict
  contract (guardrails: "you are a verifier — do NOT fix anything").
- Semantic success for a prompt **action** = process completed AND result `is_error == false`.
  For a prompt **guardrail** = the verdict file, full stop.
- Per-attempt `total_cost_usd` is recorded in the journal. The `run` summary and
  `guardrails status` print a final `Total prompt cost: $X.XXXX` line summing every
  recorded attempt's `costUsd`; the line is omitted entirely when no attempt recorded a
  cost (deterministic-only plans stay noise-free).
- `guardrails validate` probes each DECLARED runner's `command` on PATH and emits a
  **warning** (GR2009) if it does not resolve — not an error, since the plan may run on
  another machine where the runner is installed.
- A prompt action may signal an unresolvable decision by writing
  `{ "needsHuman": "<question>" }` into its fragment — the harness treats the attempt
  as needs-human immediately (no retry burn).

### 9.1 AI-merge worker

The AI-merge worker resolves a git merge conflict during a union (§5.3 case B). It is a **constrained
prompt action behind `IPromptRunner`** (the same seam as `claude`). **The existing `IPromptRunner`
contract returns metadata only** (`PromptResult` = `{Completed, IsError, ResultText, CostUsd,
NumTurns, Summary}`) — **there is no byte channel.** So the worker uses the existing **on-disk file
convention** (the runner writes a file, the harness reads it) via a **NEW merge env contract**, and a
**distinctly named merge prompt profile** (NOT a `guardrailOverrides`-shaped profile — that is a
guardrail-verifier concept). **It is a BYTE PRODUCER, never a VERDICT PRODUCER:**

- **Merge env contract (new):** `GUARDRAILS_MERGE_BASE`, `GUARDRAILS_MERGE_OURS`,
  `GUARDRAILS_MERGE_THEIRS` (the three-way inputs on disk) and `GUARDRAILS_MERGE_OUT` (the path the
  worker writes the resolution to). The harness reads `GUARDRAILS_MERGE_OUT` after the run. These four
  files live in a harness temp dir that is **granted to the runner's sandbox** (the runner's cwd is
  the integration worktree, so a temp dir outside it would otherwise be unreachable — the resolution
  could not be written and `GUARDRAILS_MERGE_OUT` would stay empty). The same four **absolute paths
  are embedded verbatim in the composed prompt body**, not just the env-var names (agents read
  instructions, not env — §5.1). The temp dir stays OUTSIDE the worktree so it never pollutes
  `git status` or the merge commit.
- **Input:** the conflicted files (with markers) + base/ours/theirs on disk, and the colliding
  upstream tasks' intents (their `task.description` + `writeScope`) composed into the prompt string.
- **Output:** the merged bytes only, written to `GUARDRAILS_MERGE_OUT`. A rationale is logged
  (NON-gating, never read as a verdict). `PromptResult.IsError` and the exit code are **not** the
  verdict.
- **Trust:** three deterministic checks — (i) the resolution is non-degenerate: an empty or
  whitespace-only `GUARDRAILS_MERGE_OUT` is a FAILED attempt (an empty resolution would otherwise
  pass gates ii/iii vacuously and silently blank the conflicted file); (ii) no conflict markers
  remain (`git diff --check`); (iii) blast-radius (modified only the git-reported-conflicted files,
  `git status --porcelain`). A violation ⇒ discard (`reset --hard`) + `needs-human`.
- **Budget:** 1 retry (2 attempts). Escalate to `needs-human` on markers-left / out-of-bounds /
  re-verify-fail / budget. The AI's exit code is never a verdict.

Its cost is charged against `maxCostUsd` like any prompt attempt. It is configured under
`promptRunners` as a **reserved merge runner profile** (e.g. `ai-merge`) — a distinct merge profile
named for what it is (read the conflict, write only `GUARDRAILS_MERGE_OUT`), **not** a
`guardrailOverrides` block.

### 9.2 AI triage on needs-human (plan 08 §9, PO #7 / Decision 8)

When a task exhausts its retry budget and transitions to `needs-human`, the harness optionally
runs a **one-shot advisory triage step** to classify the root cause. It is a **constrained prompt
action behind the existing `IPromptRunner` seam** (the same seam as `claude` and the AI-merge
worker), invoked under a **distinct `ai-triage` prompt profile** in `promptRunners` — not a
`guardrailOverrides` block. `NeedsHumanTriage` is the class that owns the step.

**Trigger — exhaustion only.**

- Triage fires **ONCE** on the **terminal exhaustion transition** (all retry budget consumed by
  action/guardrail failures across every attempt).
- It does **NOT** fire when the agent itself emitted `{"needsHuman": "..."}` (that is already a
  human ask — the question is already posed; additional triage is redundant and would race).
- It does **NOT** fire mid-retry (between attempts while budget remains).

**Diagnosis — tool-vs-local.**

Given the failed task (`task.json`, every attempt's action output, the failing guardrail outputs,
and the run context), the triage prompt classifies the root cause as one of:

- `guardrails-tool` — a Guardrails harness or tooling limitation; warrants a GH issue against
  the Guardrails repo. The triage response includes a ready-to-file `ghIssueTitle` + `ghIssueBody`.
- `local-repo` — a problem with the plan, code, or tests for the **current** repo; no Guardrails
  issue is warranted. The triage response includes an `analysis` field.

**`feedback.md` — TASK-LEVEL under the elevated logs.**

Triage writes `logs/<runId>/<task-id>/feedback.md` — a **sibling of the `attempt-N/` directories**,
NOT inside any attempt dir. This is distinct from the **per-attempt** `feedback.md` written by §8
(which lives at `logs/<runId>/<task-id>/attempt-N/feedback.md` and is the retry's input). The
task-level `feedback.md` captures:

- The diagnosis (`guardrails-tool` or `local-repo`).
- The evidence the triage drew on.
- For a `guardrails-tool` diagnosis: the drafted GH-issue title and body.

**Needs-human message pointer.**

The task's `needs-human` message (surfaced in the run summary and `guardrails status`) references
the `logs/<runId>/<task-id>/feedback.md` path so the human lands on the triage diagnosis
immediately.

**Strictly advisory — gates nothing.**

The task is **already `needs-human`** before triage runs; triage can **never** change that verdict,
re-open the task, mark it done, or burn retry budget.

- `PromptResult.IsError` and the runner exit code are **never** read as a verdict.
- A thrown exception or a runner error means "no `feedback.md` was produced"; it is logged and the
  task remains plainly `needs-human`. Triage must **never** block or abort the run — all other
  independent tasks continue normally.

A prompt proposes, a file certifies: only a written `feedback.md` provides the diagnosis; a
failed/throwing triage is silently skipped.

**Opt-in auto-file (`triageAutoFile`, default OFF).**

By default, triage only **drafts** the GH issue (title + body) into `feedback.md` and files
**nothing** to a remote. Only when `triageAutoFile` is explicitly opted in — gated behind a
configured GH repo + token — does the harness auto-file the issue. Default is **OFF**.

---

## 10. Diagram artifacts (`diagram.md` + `diagram.html`)

`guardrails graph [folder]` renders the plan's task/guardrail DAG as a Mermaid
`flowchart TD` and writes two companion files:

- **`diagram.md`** — the GitHub render artifact: a provenance comment + fenced Mermaid
  block + structure-only caption. GitHub renders it inline.
- **`diagram.html`** — the local-navigation companion: a self-contained pan/zoom/fullscreen
  HTML viewer whose task/guardrail nodes carry `click href` directives pointing to their
  source under the plan folder. Use `--no-html` to suppress it; a missing HTML file is **not
  treated as stale** by `--check`. Node clicks require serving the file via a local HTTP
  server (`python -m http.server`) — browsers block `file://→file://` navigation by default.
  The `click href` directives are HTML-only: `diagram.md` stays click-free (GitHub sandboxes
  Mermaid; the targets are `file://`-local). Assets load from CDN (needs internet once);
  offline inlining is a v2 consideration.

Both files are **generated, non-authored artifacts**: NOT part of the plan contract, safe to
delete and regenerate, and excluded from `guardrails.baseline`. Nothing is added to
`guardrails.json` or its model — the staleness key lives in the diagram files instead.

**Shape.** Per task NN: the task node fans out one edge to each of its guardrail nodes; all
of that task's guardrail nodes merge into a single per-task "Finished" node
(`<id> ✓ Finished`). Dependency edges run FROM a dependency's Finished node TO the
dependent task node — for each task B that `dependsOn` A, the diagram emits
`done_A --> task_B` (A is done, now B may start). Three `classDef`s color tasks, guardrails,
and Finished nodes distinctly. Retry / feedback (cyclic) edges are out of scope for v1.

**Provenance comment.** The first line of `diagram.md` is, verbatim:

```
<!-- guardrails:graph v1 source-sha256=<hash> -->
```

followed by a blank line and a fenced ```` ```mermaid ```` block. The comment carries only
the `source-sha256` identity — no timestamp — so re-running `graph` on an unchanged plan
produces a **byte-identical** file (a deterministic projection, no git churn).

**Caption.** Immediately after the closing mermaid fence, the written `diagram.md` carries a
single italic caption line, verbatim:

```
_Structure only — retry, feedback, and needs-human edges are omitted._
```

The flowchart draws the static task/guardrail/dependency structure only (retry, feedback, and
needs-human edges are out of scope for v1); the caption tells a reader so the diagram is not
mistaken for a one-pass pipeline. The caption lives in the markdown wrapper **only** — NOT
inside the ```` ```mermaid ```` block and NOT in the renderer's `source-sha256` semantic
content — so it does not affect the hash, leaves two regens byte-identical, and is absent from
`--stdout` (which prints the raw diagram, not the document).

**`source-sha256`.** A SHA-256 (lowercase hex) over the diagram's **semantic content** (node
labels + DAG shape) as emitted by the renderer, excluding cosmetic `classDef` styling. It
changes whenever the DRAWN diagram changes — a task, a dependency, or a guardrail (DAG
shape), or a node label (a guardrail `description`, which the renderer draws as the guardrail
label). It is stable across irrelevant input reorderings (the renderer sorts tasks,
guardrails, and dependents ordinal) and is unaffected by action kind (not drawn) or by
styling.

**Command contract.**

- `guardrails graph [folder]` — render and write `diagram.md` + `diagram.html`; print the
  written paths; exit `0`. Front-doors through load/validate first: on any load/validate
  error, print diagnostics and exit `1`.
- `--no-html` — write only `diagram.md`; skip `diagram.html`. Has no effect with `--stdout`.
- `--stdout` — print the diagram to stdout; write nothing to disk (neither `diagram.md` nor
  `diagram.html`); exit `0`.
- `--check` — write nothing. Recompute `source-sha256`, read the value embedded in an
  existing `diagram.md`, and exit `0` when present and equal (fresh). When `diagram.md` is
  **stale or missing**, print one actionable line and exit `2` — the "regenerate" signal.
  When `diagram.html` is **present but carries a different hash**, print one actionable line
  and exit `2` (a **missing** `diagram.html` is NOT stale — the caller may have used
  `--no-html`). A **load/validate error** front-doors first and exits `1`, never reaching the
  freshness check.
- `--format <mermaid>` — default and only accepted value is `mermaid` (reserved for future
  formats).

---

## 11. Breakdown manifest + regeneration merge (`guardrails.baseline`)

The plan is the **source of truth**. A re-run of `/plan-breakdown` re-derives the task set and
the `dependsOn` DAG from the (changed) plan — these are machine-owned and not hand-edited. The
**only** durable human asset in a generated folder is **guardrail CRUD** (editing a guardrail
script, or adding a new one). So a regeneration must re-derive tasks while **preserving human
guardrail edits**, discarding them only when the task they belong to no longer exists. The
manifest is the deterministic foundation that makes this possible. (Tracked in issue #5.)

### 11.1 The baseline file

`guardrails lock [folder]` captures the **authored** files of a plan folder and writes
`<plan-folder>/guardrails.baseline` — a **committed** artifact (unlike harness-owned `state/`). It
is the BASE that a later regeneration diffs against. The file is named `.baseline` (not `.lock`)
because it is a durable, committed drift-detection reference point; a `.lock` extension would
wrongly imply a gitignored transient mutex (issue #10). The command verb stays `guardrails lock`
— it **writes** the baseline — only the file it produces was renamed.

```jsonc
{
  "version": 1,
  "files": {                              // relativePath (forward-slash, ordinal-sorted) → sha256
    "guardrails.json": "<64-hex>",
    "state/seed.json": "<64-hex>",
    "tasks/01-a/task.json": "<64-hex>",
    "tasks/01-a/guardrails/01-build.ps1": "<64-hex>"
  }
}
```

The baseline carries **no timestamp** — its identity is the `files` map alone, so re-running
`guardrails lock` on an unchanged folder rewrites a **byte-identical** file (a deterministic
projection, no git churn — matching the `diagram.md` precedent in §10).

**Secret-scanner exclusion suggestion (issue #67).** Because the baseline is a committed file of
pure SHA-256 hashes, generic secret scanners (ggshield/GitGuardian) flag a hash as a false-positive
"high entropy secret" and block the commit. The baseline must stay committed (it is the BASE for
merge), so whenever the tool **writes** a baseline — `guardrails lock` and the regeneration
`merge --apply` — it **detects** whether the enclosing git repo's GitGuardian config already
excludes `**/guardrails.baseline` and, when it does not, **prints a copy-pasteable suggestion**. The
tool is **read-only and advisory here: it never modifies, creates, or edits the user's scanner
config** — it only inspects and suggests. The detection prefers `.gitguardian.yaml` over an existing
`.gitguardian.yml` (ggshield precedence) and reads the v2 `secret.ignored-paths` and v1 top-level
`paths-ignore` keys, treating reasonable spellings (`**/guardrails.baseline`, `guardrails.baseline`,
`./guardrails.baseline`) as already-covered so it never nags. The suggestion is **targeted** when a
config exists (naming the file and the exact key for its v1/v2 schema), a **create-this-file** block
when no config exists, and a **generic** line when the config can't be read. It only ever prints, so
it can never affect the exit code, and a read/parse error never escapes into `lock`/`merge` (no
failure coupling). It is a no-op (prints nothing) when there is no enclosing git repo or when the
exclusion is already present.

**Included:** `guardrails.json`, every task's `task.json` / `action.*` / `guardrails/*`, and the
committed `state/seed.json`. **Excluded:** the baseline file itself, the generated `diagram.md`
and `diagram.html`, `*.tmp` (atomic-write residue), and harness-owned runtime under `state/`
(`state.json`, `run.json`, `merge-conflicts.log`, `logs/…`). Hashes are SHA-256 (lowercase hex)
over
**newline-normalized** text (matching `PlanHash`), so CRLF/LF checkouts hash identically.

### 11.2 Drift classification (LOCAL vs BASE)

Comparing a freshly captured snapshot (LOCAL) against the baseline (BASE) classifies each file:

| Status | Meaning |
|---|---|
| `Unchanged` | BASE == LOCAL — human didn't touch it; the merge may take REMOTE freely |
| `Edited` | present in both, content differs — a human edit to preserve |
| `Added` | in LOCAL only — a human-authored file to preserve |
| `Missing` | in BASE only — deleted on disk since the last baseline |

### 11.3 The regeneration merge (BASE / LOCAL / REMOTE)

A re-run has three inputs: **BASE** (the baseline), **LOCAL** (on disk = BASE + human CRUD), and
**REMOTE** (a fresh generation from the changed plan). Per guardrail:

| BASE | LOCAL | REMOTE | result |
|---|---|---|---|
| present | == BASE | changed | take REMOTE (machine owns it) |
| present | edited | == BASE | keep LOCAL (preserve the human edit) |
| present | edited | also changed | **CONFLICT → block the run** until a human applies or discards |
| present | edited | gone (task removed) | drop (task no longer needed → its guardrail goes too) |
| absent | added | absent | keep (human-authored guardrail) |

**Task identity.** Matching across a regeneration uses `stableId` (§3), not the renumbered
folder name, so a "slightly altered + reordered" task carries its human guardrails forward while
a materially changed or removed task does not. **Open question #2 is resolved: the id is a short
*minted* token, not a slug.** `/plan-breakdown` mints one per task on first generation and
*reuses* it for the continuous task on every regeneration (minting only for genuinely new tasks);
folder renames and slug edits therefore don't break identity. The LLM owns this judgment (which
id a regenerated task reuses); `validate` enforces uniqueness (GR2010) and format (GR2011).

**Tasks without a `stableId`** match by folder name (`folder:<name>`) instead. This is a
best-effort fallback, not an equal alternative: the moment a regeneration renumbers or renames
such a task's folder, the merge reads it as *the old task dropped + a new task added*, so any human
guardrail edits on it are lost (the drop is surfaced as a warning, never silent). The merge emits a
one-line heads-up whenever either side has folder-fallback tasks. Pre-`stableId` folders therefore
sit on this boundary until re-minted; `/plan-breakdown` mints an id per task so new work doesn't.

**Per-task file matching.** Within a matched task, the merge resolves **every file under the
task's `guardrails/` directory** by its full filename (not the guardrail's logical name): the
script, its `*.prompt.md`, its metadata sidecar (`<basename>.json`, §4.1), and any file a human
added there. All are human-ownable content, so all flow through the same per-file resolution — a
human-tuned `timeoutSeconds` in a sidecar is preserved exactly like an edited script body.

**Guardrail-granularity refinements.** The five-row table is the conceptual contract; at the
per-file level two more cases resolve to **CONFLICT** because human work would otherwise be lost
silently: (a) a human *edited* a guardrail that the regeneration *removed* from a surviving task,
and (b) a human *added* a guardrail whose filename the regeneration also produced, with different
content. A human-added guardrail the regeneration doesn't emit is simply kept. A guardrail the
human *deleted* that the regeneration re-emits is taken from REMOTE (the plan wins) but reported as
a **reinstated** warning — the deletion is being undone, not honored.

**What's machine-owned.** Only files under `guardrails/` are preserved. Everything else in a task —
its `task.json`, `action.*`, and the `dependsOn` DAG — plus `guardrails.json` is re-derived from
the plan (taken from REMOTE). `state/seed.json` is treated leniently: adopted from REMOTE when
present, otherwise left as-is. A human edit to one of these machine-owned files is overwritten by a
differing REMOTE — that is contractual, but never silent: the merge warns (and `lock --diff` would
have shown the file as `EDITED`), so the human can move the change into the plan if it mattered.

The deterministic engine (`BreakdownMerge`) and the `guardrails merge` command (§11.5) implement
all of the above; the `/plan-breakdown` skill orchestrates them (§11.5).

### 11.4 Command contract

Exit codes follow §7: `0` clean, `1` a genuine error, `2` an actionable "regenerate" condition
(the same signal `graph --check` uses for a stale/missing diagram).

- `guardrails lock [folder]` — capture authored files and write `guardrails.baseline`; print the
  path + file count; exit `0`. A pure content snapshot — it does **not** load or validate the
  plan (run `guardrails validate` for that). Missing folder → exit `1`. (The verb stays `lock` —
  it WRITES the baseline; only the produced file was renamed from `guardrails.lock`, issue #10.)
- `--check` — write nothing. Recompute the snapshot and compare to the baseline: clean → exit `0`;
  drift **or a missing baseline** → one actionable line and exit `2` (the "regenerate" signal,
  distinct from a genuine error so CI can tell "re-run `guardrails lock`" apart from "the tool
  failed"). A **corrupt** baseline (present but unparseable) → exit `1`.
- `--diff` — write nothing. Print one line per changed file (`EDITED` / `ADDED` / `MISSING`)
  and exit `0` (printing the report IS the success, drift or not). A **missing** baseline → exit
  `2` (run `guardrails lock` first — there is no BASE to diff against); a **corrupt** baseline →
  exit `1`.

### 11.5 The `merge` command + skill orchestration

`guardrails merge [folder] --remote <dir> [--apply]` runs the regeneration merge (§11.3).
`folder` is the current plan folder (LOCAL, carrying `guardrails.baseline` = BASE); `--remote` is a
freshly generated candidate (REMOTE) staged from the changed plan. Both sides are loaded +
validated (so a duplicate `stableId` surfaces as GR2010 here too).

- default (**dry run**) — compute and print the resolutions (`CONFLICT` / `KEEP` / `DROP` lines,
  warnings, and a summary; `TakeRemote` is summarized as a count). Writes nothing. Exit `0` when
  there are no conflicts, `2` when there are.
- `--apply` — when there are no conflicts, materialize the merge **in place**: replace the
  authored content (`tasks/`, `guardrails.json`, and `state/seed.json` when REMOTE has one) with
  REMOTE's, overlay the preserved human guardrails onto the REMOTE task structure, and re-write the
  baseline so the merged folder is the new BASE. Harness-owned `state/` runtime and the generated
  `diagram.md` are left untouched. With conflicts present, `--apply` changes nothing and exits `2`.
  The new `tasks/` tree is assembled in a sibling staging directory and swapped in only once
  complete, so a failure mid-apply leaves the existing folder intact rather than half-written with a
  stale baseline. On success it prints the re-written baseline path and a reminder to run `validate`
  then `graph` (the merge deliberately leaves the old diagram stale, and does **not** need a second
  `lock` — `--apply` already wrote the baseline).
- exit codes (§7): `0` clean (dry run with no conflicts, or applied); `2` the actionable "a human
  must act" signal — unresolved conflicts, or a **missing** baseline (run `guardrails lock` first to
  adopt the current folder as BASE); `1` a genuine error (missing folder/remote, **corrupt**
  baseline — present but unparseable, distinct from a missing one — or an invalid plan on either
  side). The missing-baseline (`2`) vs corrupt-baseline (`1`) split mirrors `lock --check`/`--diff`
  (§11.4).

**Conflict presentation (open question #3 resolved): block + report.** Conflicts are printed to
stdout — one `CONFLICT <stableId>/<file> — <reason>` line each — and the run is blocked (exit `2`);
no `--apply` proceeds until none remain. The human resolves by editing the guardrail (or the plan)
and re-running. (`.orig`-style inline markers are a possible future addition; the run-blocking
*policy* is what's contractual.)

**Skill flow (`/plan-breakdown`, regeneration path).** When the folder already exists and the
user chooses *merge*: (1) generate the new breakdown into a **staging** folder, reusing each
continuous task's `stableId` from the existing `task.json` and minting ids only for new tasks;
(2) `guardrails merge <folder> --remote <staging>` (dry run) — on exit `2`, surface the conflicts
and **stop**; (3) on exit `0`, `guardrails merge <folder> --remote <staging> --apply`, then
`guardrails validate` + `guardrails graph`. The skill never hand-applies the per-guardrail
decisions — the deterministic engine owns them.

---

## 12. Log viewer (`run` live links + `guardrails logs`)

A small **loopback-only** HTTP server surfaces each task's per-attempt log artifacts (§8) in a
browser, so a human can answer "is it actually working?" without leaving the terminal — live
during a run, or after the fact. It serves the same on-disk files documented in §8; it adds no new
artifacts and is never part of the plan contract (the loader/validator ignore it entirely).

**Binding and safety.** The server binds to the numeric loopback address `127.0.0.1` on a port (an
automatically chosen free ephemeral port by default), **never** to a routable interface — logs may
echo secrets, so they are never exposed off the local machine (the numeric bind is deliberate, so a
custom `/etc/hosts` mapping of `localhost` cannot widen the exposure). Responses carry
`X-Content-Type-Options: nosniff` and `X-Frame-Options: DENY`. The file surface is confined to
`state/logs/<task-id>/`: the requested task id must be one the plan declares, and the requested
filename must be a bare name inside the latest `attempt-N/` directory (no traversal).

**Routes** (both the live and post-mortem servers expose the same set):

| Route | Serves |
|---|---|
| `GET /` | landing page — every task linking to its log page (the `logs` variant also shows each task's journal status) |
| `GET /tasks/{id}` | a page that tails the latest attempt's log directory for task `{id}` |
| `GET /tasks/{id}/files` | JSON `{ attempt, preferred, files[] }` — the latest attempt number, a preferred file to open first (`claude-stream.jsonl`, else `action-stdout.log`, else the first file), and the attempt's files |
| `GET /tasks/{id}/file?name={f}` | the raw text of one log file (read with a shared handle so an in-flight writer is not blocked) |

### 12.1 `guardrails run` — live log links

`run` starts the server as a **companion to the live progress table** and prints its base URL plus
clickable per-task "view log" links. It is started **only** on the interactive path (a live UI,
output not redirected) — nobody clicks links in CI or piped output — and a bind failure is
**non-fatal**: the run prints one warning and proceeds without links. The server's lifetime is the
run; it is disposed when the run ends.

| Flag | Default | Meaning |
|---|---|---|
| `--no-log-server` | off (server on) | Do not start the log server / per-task links (headless or CI use). The server is also skipped whenever the run is non-interactive or `--no-ui` is set, regardless of this flag. |
| `--log-port <n>` | `0` | Port for the live log server. `0` = an automatically chosen free port. Bound to localhost only. |

### 12.2 `guardrails logs` — post-mortem viewer

`guardrails logs [folder] [--port n] [--task id] [--no-open]` serves the **same** viewer over a
plan's **persisted** logs, decoupled from any active run — the post-mortem companion for reviewing
an overnight run, or judging whether a *passing* task's guardrails were strong enough, from the
same attempt logs. It runs until Ctrl-C, then exits `0`. The folder argument defaults to the
current directory and follows the §7 plan-file → task-folder fixup.

Unlike the live links, the post-mortem landing page reads the run journal (§7) and renders a
coloured **Status** column (`succeeded` / `running` / `needs-human` / `blocked` / `failed` /
`pending`) per task — a standalone viewer has no terminal table to carry status.

| Flag | Default | Meaning |
|---|---|---|
| `--port <n>` | `0` | Port for the viewer. `0` = an automatically chosen free port. Bound to localhost only. |
| `--task <id>` | (none) | Open straight to this task's log page instead of the task list. An unknown id falls back to the task list with a notice. |
| `--no-open` | off | Do not launch a browser; just print the URL (headless hosts). |

**Exit codes.** `0` on a clean serve or clean shutdown (Ctrl-C). A load/validate failure prints
diagnostics and exits `1`. When the plan has **no run journal yet** (never run), `logs` prints a
one-line notice and exits `0` — there is nothing to post-mortem, which is not an error. A bind
failure exits `1`.
