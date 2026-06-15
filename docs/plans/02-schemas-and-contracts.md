# 02 — Schemas and Contracts (single source of truth)

Every schema and child-process contract in the Guardrails system is defined **here**.
The C# serializers (`src/Guardrails.Core`), the `plan-breakdown` and `guardrail-review`
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
├── guardrails.lock              # OPTIONAL committed breakdown manifest (§11)
├── diagram.md                   # OPTIONAL generated DAG diagram — non-authored (§10)
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
  "guardrailMode": "failFast",        // "failFast" (default) | "runAll"
  "workspace": "..",                  // cwd for all child processes, relative to the plan dir
  "interpreters": {                   // EXTENDS/OVERRIDES built-in defaults (§5.2)
    ".ps1": ["pwsh", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "{script}"]
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

- `workspace` is the repo/directory the plan operates ON (typically the folder that
  contains the plan folder). Children run with cwd = workspace; everything
  Guardrails-specific arrives via absolute paths in env vars (§5.1).
- `guardrailMode: failFast` stops at the first failing guardrail of a task attempt
  (guardrails are ordered cheapest-first by filename convention); `runAll` runs every
  guardrail and aggregates all failures into one feedback document.

## 3. `tasks/<id>/task.json`

```jsonc
{
  "description": "Implement the --stats flag",   // required, one line, human + feedback use
  "stableId": "k3f9a1",        // optional; stable task identity for the regeneration merge (§11)
  "dependsOn": ["01-author-stats-tests"],        // required (may be []); task ids
  "retries": 3,                // optional; overrides defaultRetries
  "timeoutSeconds": 3600,      // optional; whole-attempt ceiling (action + guardrails)
  "exclusive": null,           // optional; null/absent = default by action kind:
                               //   prompt action  → true  (runs alone — sole workspace access)
                               //   exe/script     → false
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
yet consume it, but because the merge keys identity on it, `validate` **does** enforce that any
declared `stableId` is unique across tasks (a duplicate is a `GR2010` error — almost always a
copy-paste slip). `validate` does not *require* one. Absent ⇒ task identity falls back to the
folder name.

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

cwd = resolved `workspace`. Process arguments are passed via `ArgumentList`
(never a concatenated shell string). For prompt processes, the same information is
*embedded in the composed prompt* (agents read instructions, not env vars).

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

---

## 6. State

### 6.1 Lifecycle

- `state/seed.json` (optional, **committed**): initial state authored with the plan.
- `state/state.json` (runtime, gitignored): the merged state. Created at run start
  from `seed.json` (or `{}`) when missing. `guardrails run --fresh` deletes runtime
  state (journal, state.json, logs) and re-seeds.
- The **harness is the single writer** of `state.json`. Child processes never touch it.

### 6.2 Fragments (snapshot in, fragment out)

Each attempt receives an immutable snapshot (`GUARDRAILS_STATE_IN`). An action that
wants to publish state writes a JSON **object** to `GUARDRAILS_STATE_OUT`.
Convention (not enforced): namespace under your own task id —

```json
{ "02-generate-greeting": { "greetingPath": "out/greeting.txt" } }
```

A fragment that exists but is not a parseable JSON object ⇒ the attempt **fails**
(reason: "invalid state fragment") and is retried — better than silently dropping data.
The fragment is merged only after **all guardrails pass**.

### 6.3 Merge policy (deterministic)

Deep merge into `state.json`: objects merge recursively; **scalars and arrays are
last-writer-wins**. Merge order = task completion order, recorded as a monotonic
`mergeSequence` in the journal. Every overwrite of an existing non-null value with a
*different* value is appended to `state/merge-conflicts.log` — tab-separated columns
`seq, task, jsonPath, old, new`, with values as compact JSON.

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
  explicit way to force a re-run.
- `needs-human` — retry budget exhausted. All *transitive* dependents become `blocked`.
  Independent branches keep running.
- Resume rules (`guardrails run` on an existing journal): `succeeded` → skip;
  `needs-human` / `failed` / `blocked` → `pending` with a fresh retry budget;
  `running` (crashed previous run) → `pending`, attempt numbering continues.

**Harness exit codes**: `0` all succeeded · `1` harness/validation error ·
`2` the operation completed but an actionable condition was found — for `run`: a task is
needs-human/blocked; for `graph --check`: the diagram is stale or missing (the "regenerate"
signal); for `lock --check`: the folder has drifted from the lock or the lock is missing (the
"re-lock" signal) · `3` cancelled.

---

## 8. Per-attempt log layout

```
state/logs/<task-id>/attempt-N/
├── state-in.json            # the snapshot given to this attempt
├── action-stdout.log / action-stderr.log
├── action-result.json
├── action-out-fragment.json # the LIVE GUARDRAILS_STATE_OUT target the action writes
├── fragment.json            # copy of the fragment made on successful merge — audit trail
├── composed-prompt.md       # prompt actions/guardrails: exactly what the runner got
├── claude-stream.jsonl      # raw runner output stream
├── guardrail-<name>.stdout.log / .stderr.log / .verdict.json
└── feedback.md              # composed failure feedback (input to the NEXT attempt)
```

---

## 9. Prompt runners

`promptRunners` (§2) maps names to runner configs. The `IPromptRunner` C# interface
quarantines all CLI specifics (flag spelling, output parsing). v1 ships `claude`:

- Invocation: `claude -p --output-format stream-json --verbose --permission-mode <m>
  --allowedTools <list> --max-turns <n> [--model <m>] [extraArgs…]`
- Prompt delivered via **stdin** (no arg-length/quoting issues).
- cwd = workspace; `--add-dir <planDir>` grants access to state/verdict paths.
- The composed prompt (§8 `composed-prompt.md`) = body + appended harness sections:
  shared state (inlined ≤ 16 KB, else by path), output contract (actions), previous-
  attempt feedback (actions, attempt ≥ 2: "fix these specific problems; do not start
  over"), verdict contract (guardrails: "you are a verifier — do NOT fix anything").
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

## 10. Diagram artifact (`diagram.md`)

`guardrails graph [folder]` renders the plan's task/guardrail DAG as a Mermaid
`flowchart TD` and writes it to `<plan-folder>/diagram.md`. The file is a **generated,
non-authored artifact**: it is NOT part of the plan contract, the loader/validator ignore
it (a present `diagram.md` at the plan root validates clean), and it is safe to delete and
regenerate. Nothing is added to `guardrails.json` or its model — `guardrails.json` carries
`//` comments the loader skips, and rewriting it through System.Text.Json would strip them,
so the staleness key lives in `diagram.md` instead.

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

- `guardrails graph [folder]` — render and write `diagram.md`; print the written path; exit
  `0`. Front-doors through load/validate first: on any load/validate error, print
  diagnostics and exit `1`.
- `--stdout` — print the diagram to stdout; write nothing to disk; exit `0`.
- `--check` — write nothing. Recompute `source-sha256`, read the value embedded in an
  existing `diagram.md`, and exit `0` when present and equal (fresh). When the diagram is
  **stale or missing**, print one actionable line (`diagram.md is stale …` / `diagram.md
  missing …`) and exit `2` — the "regenerate" signal, distinct from a genuine error so CI can
  tell "regenerate the diagram" apart from "the plan is broken". A **load/validate error**
  (no `guardrails.json`, invalid plan, missing folder) front-doors first and exits `1` with
  diagnostics, never reaching the freshness check. A missing `diagram.md` counts as stale
  (exit `2`).
- `--format <mermaid>` — default and only accepted value is `mermaid` (reserved for future
  formats).

---

## 11. Breakdown manifest + regeneration merge (`guardrails.lock`)

The plan is the **source of truth**. A re-run of `/plan-breakdown` re-derives the task set and
the `dependsOn` DAG from the (changed) plan — these are machine-owned and not hand-edited. The
**only** durable human asset in a generated folder is **guardrail CRUD** (editing a guardrail
script, or adding a new one). So a regeneration must re-derive tasks while **preserving human
guardrail edits**, discarding them only when the task they belong to no longer exists. The
manifest is the deterministic foundation that makes this possible. (Tracked in issue #5.)

### 11.1 The lock file

`guardrails lock [folder]` captures the **authored** files of a plan folder and writes
`<plan-folder>/guardrails.lock` — a **committed** artifact (unlike harness-owned `state/`). It
is the BASE that a later regeneration diffs against.

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

The lock carries **no timestamp** — its identity is the `files` map alone, so re-running
`guardrails lock` on an unchanged folder rewrites a **byte-identical** file (a deterministic
projection, no git churn — matching the `diagram.md` precedent in §10).

**Included:** `guardrails.json`, every task's `task.json` / `action.*` / `guardrails/*`, and the
committed `state/seed.json`. **Excluded:** the lock file itself, the generated `diagram.md`,
`*.tmp` (atomic-write residue), and harness-owned runtime under `state/` (`state.json`,
`run.json`, `merge-conflicts.log`, `logs/…`). Hashes are SHA-256 (lowercase hex) over
**newline-normalized** text (matching `PlanHash`), so CRLF/LF checkouts hash identically.

### 11.2 Drift classification (LOCAL vs BASE)

Comparing a freshly captured snapshot (LOCAL) against the lock (BASE) classifies each file:

| Status | Meaning |
|---|---|
| `Unchanged` | BASE == LOCAL — human didn't touch it; the merge may take REMOTE freely |
| `Edited` | present in both, content differs — a human edit to preserve |
| `Added` | in LOCAL only — a human-authored file to preserve |
| `Missing` | in BASE only — deleted on disk since the last lock |

### 11.3 The regeneration merge (BASE / LOCAL / REMOTE)

A re-run has three inputs: **BASE** (the lock), **LOCAL** (on disk = BASE + human CRUD), and
**REMOTE** (a fresh generation from the changed plan). Per guardrail:

| BASE | LOCAL | REMOTE | result |
|---|---|---|---|
| present | == BASE | changed | take REMOTE (machine owns it) |
| present | edited | == BASE | keep LOCAL (preserve the human edit) |
| present | edited | also changed | **CONFLICT → block the run** until a human applies or discards |
| present | edited | gone (task removed) | drop (task no longer needed → its guardrail goes too) |
| absent | added | absent | keep (human-authored guardrail) |

Task matching across a regeneration uses `stableId` (§3), not the renumbered folder name, so a
"slightly altered + reordered" task carries its human guardrails forward while a materially
changed or removed task does not. The **identity-aware regeneration and the conflict gate are
the skill-orchestration layer (issue #5, follow-up)**; this document and the CLI ship the
manifest, the classification, and the `lock` command — the deterministic primitives that layer
consumes.

### 11.4 Command contract

Exit codes follow §7: `0` clean, `1` a genuine error, `2` an actionable "regenerate" condition
(the same signal `graph --check` uses for a stale/missing diagram).

- `guardrails lock [folder]` — capture authored files and write `guardrails.lock`; print the
  path + file count; exit `0`. A pure content snapshot — it does **not** load or validate the
  plan (run `guardrails validate` for that). Missing folder → exit `1`.
- `--check` — write nothing. Recompute the snapshot and compare to the lock: clean → exit `0`;
  drift **or a missing lock** → one actionable line and exit `2` (the "regenerate" signal,
  distinct from a genuine error so CI can tell "re-lock the folder" apart from "the tool
  failed"). A **corrupt** lock (present but unparseable) → exit `1`.
- `--diff` — write nothing. Print one line per changed file (`EDITED` / `ADDED` / `MISSING`)
  and exit `0` (printing the report IS the success, drift or not). A **missing** lock → exit
  `2` (run `guardrails lock` first — there is no BASE to diff against); a **corrupt** lock →
  exit `1`.
