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
│   ├── merge-conflicts.log      # harness-owned, gitignored (§6.3)
│   └── logs/<task-id>/attempt-N/   # per-attempt artifacts (§8)
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

---

## 2. `guardrails.json` (run configuration)

```jsonc
{
  "version": 1,                       // required; schema version of this file
  "maxParallelism": 4,                // default 4
  "defaultRetries": 2,                // retries AFTER the first attempt; default 2
  "defaultTimeoutSeconds": 1800,      // per-attempt ceiling when nothing narrower applies
  "maxCostUsd": 5.00,                 // OPTIONAL per-run cost ceiling, decimal USD; absent = no cap
  "guardrailMode": "failFast",        // "failFast" (default) | "runAll"
  "workspace": "..",                  // cwd for all child processes, relative to the plan dir
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

## 3. `tasks/<id>/task.json`

```jsonc
{
  "description": "Implement the --stats flag",   // required, one line, human + feedback use
  "stableId": "k3f9a1",        // optional; stable task identity for the regeneration merge (§11)
                               //   format ^[a-z0-9][a-z0-9._-]*$ (GR2011); unique (GR2010)
  "dependsOn": ["01-author-stats-tests"],        // required (may be []); task ids
  "retries": 3,                // optional; overrides defaultRetries
  "timeoutSeconds": 3600,      // optional; whole-attempt ceiling (action + guardrails)
  "exclusive": null,           // optional; null/absent = default by action kind:
                               //   prompt action  → true  (runs alone — sole workspace access)
                               //   exe/script     → false
  "captureHashes": [           // optional; workspace-relative files whose SHA-256 the HARNESS
    "tests/MyProj/FooTests.cs" //   records into state after a successful action (§3.1) — the
  ],                           //   agent never computes a hash. Missing file ⇒ attempt fails.
  "restoreOnRetry": false,     // optional, default false; opt-in to restore-on-retry for the
                               //   captureHashes files above (§3.1). true ⇒ also snapshot their
                               //   authored bytes and restore them before a downstream retry.
                               //   true with empty/absent captureHashes = validation error (GR2014).
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

### 3.1 `captureHashes` — harness-computed content hashes

`captureHashes` is an optional list of **workspace-relative file paths**. After a task's action
succeeds (and **before** its guardrails run), the harness computes each listed file's **SHA-256
over its raw bytes** (uppercase hex) and merges the result into the task's state fragment under:

```jsonc
{ "<taskId>": { "fileHashes": { "<relative/path>": "<UPPERCASE-SHA-256-HEX>" } } }
```

The hashes then merge into `state.json` like any fragment, so a downstream task reads them via
`GUARDRAILS_STATE_IN`. The hash is computed **in harness code** — the action agent never runs
`git hash-object`, `Get-FileHash`, or any shell command, so a scoped `allowedTools` (or an offline
sandbox) can never block it. If a declared file does not exist after the action, the attempt
**fails** with an actionable message naming the missing path (the action claimed success but did
not produce a declared output); nothing is recorded.

**Paths are workspace-relative and validated.** Each `captureHashes` entry must be a
workspace-relative path that stays inside the workspace. `validate` rejects an absolute path, a
drive- or root-rooted path, or any entry whose normalized resolution escapes the workspace root
(e.g. `../../etc/passwd`) as a `GR2013` error naming the offending task and path.

**Merge ordering — capture overlays the action's own fragment.** Capture does not replace the
fragment the action wrote to `GUARDRAILS_STATE_OUT`; it **overlays** onto it. The harness reads the
action's pending fragment, sets `{ "<taskId>": { "fileHashes": { … } } }`, and writes the result
back, **preserving the action's own-namespace keys** (other keys under `<taskId>`). For a path that
appears in **both** the action's own `fileHashes` and the harness capture, **the harness-computed
value takes precedence** (it overwrites the action's). A non-object or unparseable action fragment
still triggers the **invalid-fragment** attempt failure (§6.2) — capture leaves those bytes
untouched and never papers over them, so declaring `captureHashes` does not change whether a task
with a malformed fragment fails. Capture writes only under the task's own id, so a capture-overlaid
fragment satisfies single-writer-per-key (§6.2) by construction; a **foreign** top-level key in the
action's own fragment makes the whole attempt fail at the merge step (§6.2) regardless of capture.

The canonical use is the `tests-untouched` guardrail: a test-author task declares the test files
in `captureHashes`, and the implementation task's guardrail recomputes with
`Get-FileHash -Algorithm SHA256` (a pwsh cmdlet, run by the interpreter — not the agent sandbox)
and compares. SHA-256-over-raw-bytes is chosen so the harness (`SHA256.HashData`) and a guardrail
(`Get-FileHash`) agree exactly, with no git dependency and no shared git-index mutation that could
race under `maxParallelism > 1`. It sidesteps the **git-blob** normalization hazard, but it is an
**exact raw-byte match**: a line-ending normalization that touches the file between capture and the
downstream recompute (git `autocrlf` on checkout, or an IDE/formatter rewriting the file) makes the
comparison **fail closed** — a spurious "tests changed" block a human then reviews. Safe, but
possible; it does not silently pass.

### 3.1.1 `restoreOnRetry` — opt-in baseline restore

`restoreOnRetry` is an optional task-level boolean (default **false**) that sits alongside
`captureHashes`. **By default, `captureHashes` ONLY hashes** for tamper-detection (above) — nothing
is snapshotted and nothing is restored. Setting `restoreOnRetry: true` on the **author** task opts
that task's captured files into restore-on-retry; with it off, the dirtied-file dead-end below is
back for that task — which is fine, it is opt-in.

**What `restoreOnRetry: true` does.** Beyond hashing, the harness snapshots each captured file's
authored bytes into a runtime baseline store (`state/captured/<author-task-id>/<path>` — harness-owned,
gitignored, excluded from the lock manifest like the rest of `state/` runtime). The snapshot is taken
**only on a clean capture** (a successful action whose fragment is valid), strictly **before** the
author task's own guardrails run — and thus before it is journaled `succeeded`. A baseline is therefore
written even on an author attempt whose guardrails later FAIL; that is harmless: the author re-snapshots
(overwrite) on its next clean attempt, and a consumer never restores from an author that never succeeds
(the author's dependents stay `blocked` and never run). Before **each attempt** of a task that transitively depends on
a `restoreOnRetry` author, the harness restores any captured file whose current bytes differ from that
baseline (a no-op on the first attempt). This removes the dead-end where an implementation task edits
a captured test file: the workspace is not reset between attempts, and an authored test file is
typically untracked in git, so without restore the dirtied file would persist and `tests-untouched`
would fail every retry identically. With restore, a retry starts from the pristine test file, so an
implementation correct against the *original* tests passes.

**Scope and validation.** Restore only ever touches files named in an upstream `restoreOnRetry`
`captureHashes` — never other workspace files. `restoreOnRetry: true` with an empty/absent
`captureHashes` has nothing to act on and is a **`GR2014`** validation error naming the task. When
**two** ancestor authors capture the same relative path, restore is **last-write-wins in ancestor-id
ordinal order** (each baseline is keyed under its own author id, so they never collide in the store;
the consumer restores ancestors in ordinal id order, so the highest-sorting author's bytes win).

**Resolved against the workspace.** Both snapshot and restore resolve each captured path against the
**plan workspace** — the canonical base the `GR2013` check validated — never against a consumer
task's per-task `workingDirectory`. The harness additionally re-asserts workspace containment before
every write (defense-in-depth): a path that would escape the workspace is **not** written and is
recorded as un-restorable. Each restore — and any captured file the harness could **not** restore
(missing baseline, or a containment skip) — is recorded in the attempt's `restored-baseline.log`, so
the audit log is loud exactly when restore could not protect the workspace, not only on success.

**Resume.** Because the snapshot is taken only on a clean capture and *before* the author is journaled
`succeeded`, a crash mid-snapshot re-runs the author on resume and re-snapshots from scratch — the
baseline is consistent on crash-resume. `guardrails run --fresh` deletes `state/captured/` (§6.1) so a
stale baseline can never revert a legitimately re-authored file on the next run.

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
| `GUARDRAILS_LOG_DIR` | all | `state/logs/<task>/attempt-N/` — scratch space welcome |
| `GUARDRAILS_FEEDBACK` | actions, attempt ≥ 2 | Path to `feedback.md` describing the previous attempt's failures |
| `GUARDRAILS_ACTION_STDOUT` | guardrails | The action's captured stdout file |
| `GUARDRAILS_ACTION_STDERR` | guardrails | The action's captured stderr file |
| `GUARDRAILS_ACTION_RESULT` | guardrails | `action-result.json`: `{ "kind", "exitCode", "summary" }` |
| `GUARDRAILS_VERDICT_OUT` | prompt guardrails | Where the verdict JSON must be written (§4.2) |

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

### 5.3 Harness writes to the workspace — exactly one case

The harness is otherwise a **read-only** actor on the workspace: actions and guardrails write
workspace files; the harness reads them (hashes, captures, lock manifest) and owns only `state/`.
There is **exactly one** exception, bounded here so a future feature cannot quietly widen it:

> **The harness writes a workspace file only when restoring a `restoreOnRetry` `captureHashes`
> file to its authored baseline before an attempt (§3.1.1) — and never otherwise.** Each such write
> targets a path that (a) appears in an upstream `restoreOnRetry` task's `captureHashes`, (b) resolves
> against the plan workspace, and (c) passes the same workspace-containment check as `GR2013`
> immediately before the write. A path failing any of these is **not** written and is logged as
> un-restorable.

Any new capability that needs the harness to write workspace files must be added to this list with
its own containment analysis — the default remains that the harness does not mutate the workspace.

---

## 6. State

### 6.1 Lifecycle

- `state/seed.json` (optional, **committed**): initial state authored with the plan.
- `state/state.json` (runtime, gitignored): the merged state. Created at run start
  from `seed.json` (or `{}`) when missing. `guardrails run --fresh` deletes runtime
  state and re-seeds. The `--fresh` deletion list is: `run.json`, `state.json`,
  `merge-conflicts.log`, the `logs/` tree, and the `captured/` baseline store (§3.1.1) —
  the captured store MUST be wiped or a stale baseline would revert a legitimately
  re-authored file on the next run, before any task re-snapshots its current bytes.
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
          "logDir": "state/logs/01-write-greeting-script/attempt-1"
        }
      ]
    }
  }
}
```

**Status semantics**
- `succeeded` — terminal. Resume skips it; `guardrails reset <folder> <task>` is the
  explicit way to force a re-run. Resetting a task also clears that task's captured
  baseline store (`state/captured/<task-id>/`, §3.1.1), so the re-run re-snapshots from
  its fresh authored bytes rather than restoring a stale baseline.
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
state/logs/<task-id>/attempt-N/
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

**Secret-scanner exclusion (issue #67).** Because the baseline is a committed file of pure
SHA-256 hashes, generic secret scanners (ggshield/GitGuardian) flag a hash as a false-positive
"high entropy secret" and block the commit. The baseline must stay committed (it is the BASE for
merge), so whenever the tool **writes** a baseline — `guardrails lock` and the regeneration
`merge --apply` — it also ensures the enclosing git repo's `.gitguardian.yaml` (or an existing
`.gitguardian.yml`) excludes `**/guardrails.baseline` from scanning. The exclusion is **merged**
into any existing config (the right ignored-paths key is chosen for the file's v1/v2 schema; other
keys are preserved) and is **idempotent**; it is a no-op when there is no enclosing git repo. A
freshly created config carries an explanatory comment header; the merge path does not preserve
existing comments.

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
