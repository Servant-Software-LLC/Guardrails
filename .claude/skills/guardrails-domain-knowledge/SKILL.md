---
name: guardrails-domain-knowledge
description: |
  Guardrails product knowledge for all agents working in this repo. Use when working
  on anything related to Guardrails:
  - The task/guardrail/state conceptual model and the four-stage workflow
  - Plan-folder layout, schemas, or child-process contracts
  - Harness execution semantics (retry, needs-human, resume, merge)
  - Authoring or reviewing the plan-breakdown / guardrails-review skills
  - Roadmap, v2 bets, or scope questions

  Provides: the mental model, execution semantics, contract quick-reference, and
  pointers to the single-source-of-truth documents.

  SELF-UPDATING: When your work changes the domain model, schemas, contracts,
  execution semantics, or roadmap in ways that affect this knowledge, you MUST update
  this skill (and docs/plans/02-schemas-and-contracts.md if a contract moved) before
  completing your task. Update the affected section(s) only.
---

# Guardrails Domain Knowledge

## Quick Reference

**What is Guardrails?** A system that turns a reviewed markdown plan into an
executable, file-system task DAG where every task carries executable acceptance
checks ("guardrails"), plus a cross-platform .NET harness (`guardrails` dotnet tool)
that runs the DAG to green -- retrying failed tasks with guardrail feedback, and
halting honestly (`needs-human`) when a task can't converge.

**The bet:** in agentic engineering, verification is the bottleneck, not generation.
Humans review the *checks* once instead of reviewing *every agent output* forever.

## The model

- **Plan folder** `<plan-name>/` generated next to `<plan-name>.md`:
  `guardrails.json` (run config) + `state/` (seed, merged state, journal) +
  `logs/<runId>/<task-id>/attempt-N/` (per-attempt artifacts, sibling of `state/`,
  divided by `runId` so re-runs never interleave) +
  `tasks/<NN-verb-object>/` (one folder per task) + optional `diagram.md`/`diagram.html`.
- **Two-scope preflights/guardrails -- four folders** (design-of-record
  `docs/plans/09-preflight-first-class.md`, build brief `docs/plans/preflights-impl.md`; SSOT section
  1/3.3): `preflights/` and `guardrails/` are first-class folders at TWO scopes:
  - **Plan-level** `<plan>/preflights/` -- the **"Full Flight Checks"** -- siblings of `tasks/`/
    `guardrails.json`/`state/` at the **plan root**; evaluated ONCE, BEFORE the Scheduler builds waves,
    against the starting repo.
  - **Plan-level** `<plan>/guardrails/` -- the **"Terminal Gate"** -- also at the plan root; evaluated
    ONCE, at run end, on the merged plan-branch HEAD. Replaces the old no-op-END `integrationGate: true`
    sink-task modelling (see the Task bullet below).
  - **Task-level** `tasks/<id>/preflights/` -- a JIT dependency-delivery check, sibling of the existing
    `tasks/<id>/guardrails/`; evaluated in the task's segment worktree at `taskBase`, BEFORE its action.
  - **Task-level** `tasks/<id>/guardrails/` -- the existing per-task postcondition folder, unchanged.
  All four folders share **one** guardrail-file parser/grammar (SSOT section 4) -- they differ only in
  WHERE they live and WHEN they run; every file opens with a `catches:` declaration and a malformed one
  (no `catches:`) is a hard load error, **GR2027**. **Landed vs pending -- see Status:** the
  loader/validator for all four folders is implemented; the three harness phases that RUN the
  plan-level folders (pre-DAG, terminal, task-preflight slot -- see Execution semantics) are landing
  incrementally, so do not assume a phase fires without checking current `Scheduler`/`TaskExecutor`
  wiring.
- **Diagram** -- two companion files written by `guardrails graph` at the plan-folder root,
  both generated, non-authored, and excluded from `guardrails.baseline`:
  - `diagram.md`: Mermaid `flowchart TD` (GitHub render artifact). First line is a
    `<!-- guardrails:graph v1 source-sha256=<hash> -->` provenance comment. NOT part of the
    plan contract; safe to delete and regenerate.
  - `diagram.html`: interactive local-navigation companion (pan/zoom/fullscreen + Mermaid
    `click href` directives pointing to task/guardrail source). Suppressed by `--no-html`.
    Node clicks require a local HTTP server -- browsers block file://->file:// by default.
  Both share the same `source-sha256` key. `guardrails graph --check` exits 0 (fresh), 2
  (stale/missing), 1 (load/validate error). See SSOT section 10.
  - **Live status overlay (issue #219, a THIRD companion):** during a run the harness writes
    `logs/<runId>/diagram.html` -- the SAME DAG with per-node status badges (a spinner while
    in-flight, a settled check / X / "?" once finished). It is gitignored runtime state,
    `--fresh`-cleared, separate from the static plan-root `diagram.html` (a tracked artifact the
    run never touches). Same `source-sha256`; status is **hash-neutral chrome**. See SSOT
    section 10.1.
- **Task** = `task.json` (`description`, `dependsOn`, optional `retries`/`timeoutSeconds`/
  `writeScope`/`action`) + one action file + `guardrails/` with >=1 guardrail.
  Zero guardrails = validation error.
  - `integrationGate: true` as a task kind is **RETIRED** (SSOT section 3.3): the terminal
    whole-repo integration gate is now the plan-root `<plan>/guardrails/` folder (the Terminal
    Gate -- see the four-folder bullet above), NOT a sink task. No coexistence window -- a plan
    that STILL declares `integrationGate: true` is a hard validation error, **GR2029**. The old
    GR2017 (require exactly one sink) is gone; GR2018's content teeth (a gate must actually re-run
    >=1 `scope: "integration"` check) are re-homed onto the folder as **GR2028**.
  - `writeScope: ["src/Foo/"]` (optional) drives the deterministic **write-scope CHECK** (SSOT
    section 3.4): after the action, before the task's own guardrails, the harness computes
    `git diff --name-status <taskBase>..<HEAD>` in the segment worktree and asserts every changed
    path is in scope. Absent => no check. **Two enforcement phases (#280):** **phase 1** runs on the
    *action's* writes BEFORE the guardrails -- a violation is a guardrail-class failure that
    scoped-**reverts** the out-of-scope paths (out-of-scope MODIFY/DELETE `git checkout <taskBase> --`,
    a new file `git rm -f`; in-scope WIP survives) and retries with feedback (eventual `needs-human`);
    **phase 2** runs AFTER the guardrails PASS, before the segment settle -- it re-runs the SAME
    Check+ScopedRevert but does **NOT** fail the attempt (a passing guardrail's `npm ci`/build side
    effects are expected and STRIPPED silently, echoed to `scope-clean.log` + an `IRunObserver` note,
    never punished). Net: a writeScope task's segment commit contains exactly the in-scope diff. Phase 2
    is skipped for a no-writeScope task (its safety net is section 5.3(D)). Renames = paired D+A (both in
    scope). GR2019 (error): scope entry escapes workspace. GR2020 (warning): vacuous/over-broad scope.
    TDD triad replacement.
  - **Dependency/build-dir staging exclusion (SSOT section 5.3(D), #280):** EVERY harness `git add -A`
    site (`GitWorktreeProvider.Integrate` + the write-scope check's staging in both `Check` and
    `HasFileChanges`) excludes a curated reconstructable set -- v1: **`node_modules` at any depth** +
    the harness's own `.guardrails-staging/` + `.guardrails-agent-io/` -- via a git pathspec exclude
    (`SegmentStaging`, a single named constant). So a guardrail's `npm ci` `node_modules` can NEVER be
    committed regardless of `.gitignore` timing or writeScope, and a leftover `node_modules` in a reused
    linear-chain worktree never raises a spurious write-scope violation. It is **stage-exclusion, NOT
    worktree deletion** (the dirs stay on disk -- warm-cache #255 compatible); the one exception is
    `PreserveAttemptToRef` (#195 salvage ref), left on plain `git add -A` since it is never merged.
  - **The triad (`captureHashes`/`restoreOnRetry`/`exclusive`) and `WorkspaceLock` are REMOVED**
    -- replaced by physical worktree isolation (one task per segment worktree) and `writeScope`.
- **Worktree isolation**: the workspace must be a git repository top-level (GR2015 error
  otherwise; GR2016 warning for deep `worktreeRoot` + deep source tree risking Windows MAX_PATH).
  At run start the harness creates a **plan branch** `guardrails/<plan-name>` off the user's HEAD
  and an integration worktree on it (sole merge target). Each task runs in a **segment worktree**:
  - Linear chain: **reuses one** segment worktree (downstream commits on top of upstream's tip
    in the same tree -- no inter-hop merge, no inter-hop re-verify).
  - Fan-out: **inherits one** chain, **forks the rest** off the producer's committed tip (W-2,
    not the inheritor's advanced tip).
  - Fan-in: **forks one** upstream and merges the others in (union settle, SSOT section 5.3).
  Worktrees are created under a harness-owned root outside the workspace (default
  `<temp>/guardrails-worktrees/<hash>/<runId>/`, overridable via `worktreeRoot` in
  `guardrails.json`). `maxParallelism` defaults to **3**. The user's checkout is **read-only
  for the entire run**; the only optional write to the user's branch is `--merge-on-success`
  (AI-merge is withheld at that boundary).
- **Runtime containment hook + stash safety (issues #199/#192, SSOT section 9.4):** the write-scope
  CHECK (section 3.4 above) is a post-hoc, INNER boundary -- it only ever sees the segment's own
  `git diff`, so a write to an absolute path OUTSIDE the segment (a sibling task's tree, the user's
  main checkout, anywhere else) never appears in it and goes undetected. For every worktree-mode
  prompt invocation (action OR guardrail) the harness now ALSO generates a Claude Code **PreToolUse
  hook** (`Guardrails.Core.Prompts.WorktreeContainmentHook`, written into the attempt's log dir --
  never inside the segment, so it can't pollute `git status`) and injects it via `claude -p
  --settings <path>` -- an OUTER, hard-enforced runtime boundary absent in serial mode. It intercepts
  `Write`/`Edit`/`MultiEdit`/`NotebookEdit` and write-ish `Bash` (redirects, `tee`/`cp`/`mv`, `git
  checkout --`, `git worktree add`) and blocks (exit 2 + stderr) any target path resolving outside
  the worktree root, reusing `WorkspaceContainment.Escapes`'s exact rule (re-expressed in
  shell/PowerShell, since the hook runs as an OS process, not a .NET callback) -- never a
  reimplemented rule. The SAME hook additively blocks the entire `git stash` family unconditionally
  in worktree mode (`refs/stash` is repo-wide, not per-worktree -- a concurrent task's stash can
  silently cross-contaminate a sibling's tree, the #192 incident), and the harness-contract context
  every worktree-mode prompt already receives (`PromptComposer`, gated on `isWorktreeMode`) carries a
  `## Worktree safety` advisory with the stash-free alternative (`git diff` -> `git checkout --` ->
  `git apply`). Honest boundary: this defends the TOOL-CALL layer Claude Code exposes -- the `Bash`
  matcher is a heuristic over command TEXT, not an OS-level sandbox, so it raises the bar sharply
  against accidental/careless escapes but is not proof against a deliberately adversarial agent.
- **Action kinds**: `.prompt.md` -> LLM (via pluggable `IPromptRunner`; v1 = Claude Code
  CLI headless); anything else -> process via the interpreter map.
- **Guardrails**: deterministic (exit 0 = pass; failure reason on stdout) or prompt
  (MUST write `{pass, reason}` verdict JSON to `GUARDRAILS_VERDICT_OUT` -- CLI exit
  codes are never semantic). Ordered by filename, cheapest first. ALL must pass.
  Every guardrail opens with a `catches:` comment naming the wrong implementation it would catch.
  **Negative assertion (#176):** a legitimate deterministic archetype -- the mirror of a presence check.
  A presence/`covers-key-behaviors` check fails when a kept token is ABSENT (`if -notmatch "X" { exit 1 }`
  = "X must be present"); a **negative assertion** fails when an EXCLUDED token is PRESENT (`if -match
  "X" { exit 1 }` = "X must be absent"). Emit one when an action prompt explicitly excludes a
  scenario/keyword the artifact must NOT contain (a wizard-blocked mode, a forbidden construct); pair it
  with the positive coverage check. **GR2026 is (correctly) SILENT on a negative assertion (post-#177):**
  the stale-coverage lint flags only POSITIVE require-present coverage tokens (a `-notmatch … exit` /
  `-match … $hits++` block); a `-match … exit` (require-absent) token is excluded, because its keyword is
  intentionally absent from the prompt (SSOT section 4.4). A GR2026 warning on a negative assertion would
  be the #177 false positive -- not a reason to remove the guardrail.
  **Verify-don't-replay (#62):** a guardrail receives the action's recorded outcome --
  `GUARDRAILS_ACTION_RESULT` (`{kind, exitCode, summary}`), `_STDOUT`, `_STDERR` (SSOT
  section 5.1) -- and may verify a postcondition from it instead of re-running the action's
  command. It's a speed/flake trade-off, sound only against output the action couldn't fabricate.
  **Guardrail scope** (SSOT section 4.3): a guardrail declares an optional `scope` (`"local"`
  default, or `"integration"`). The **integration-guardrail set** = all `scope: "integration"`
  guardrails across the plan (typically the whole-repo build + full test suite). At **every** union
  point (a fan-in or a non-FF plan-branch integration) the harness re-runs, on the merged bytes,
  **the integration set ONLY** -- one set, run uniformly at every union and again on the final merged
  HEAD by the terminal `<plan>/guardrails/` folder (the Terminal Gate, SSOT section 3.3). There is
  **no** per-task or per-colliding-sibling
  guardrail selection at a union in v1: the integration set IS the whole re-verify. (The earlier
  "union task's own set + each colliding sibling's full set + integration set" three-part model was
  **never implemented** -- `ShouldRunAtUnion` had zero callers; SSOT section 4.3 is integration-set-only.)
  **Accepted residual (#132):** because the union re-verify is integration-set-only, a hunk an AI-merge
  silently drops on a **shared file** (overlapping `writeScope`s of colliding siblings) is re-verified
  at the union ONLY by an integration-scoped guardrail; a drop catchable solely by a sibling's `local`
  guardrail is NOT re-run at the union (running `local` guardrails on union bytes false-fails -- no
  fragment, inverted anti-tautology). The mitigation is **authoring, not runtime**: author a
  `scope:"integration"` union-guardrail on the shared file (`plan-breakdown` emits one for overlapping
  scopes; `guardrails-review` flags its absence WEAK). See SSOT section 4.3 "Accepted residual".
  **Author-time smoke-test gate for SCRIPT guardrails (#302).** When a `.sh`/`.ps1`/`.py` guardrail
  (a script in ANY of the four folders -- `tasks/<id>/guardrails/`, `tasks/<id>/preflights/`,
  `<plan>/guardrails/`, `<plan>/preflights/`) is authored or CHANGED and it is
  RUNNABLE-AT-AUTHOR-TIME, the author MUST smoke-test it by **EXECUTING** it before committing --
  against **(a) a representative VALID sample** of the checked artifact (expect **exit 0**) AND
  **(b) a deliberately INVALID one** (expect **non-zero**) -- never defer its first real execution to
  `guardrails run`, where a broken guardrail script burns the task's WHOLE retry budget to
  `needs-human`, blocks every downstream task, and masquerades as an implementation bug (the task's own
  deliverable was valid all along). The two-sided run catches all three script-defect classes at once:
  a runtime bug of the script's OWN (bash quote-stripping, path handling, a broken `--json` parse --
  misbehaves on any input); a **false-red no correct implementation can satisfy** (every attempt
  dead-ends at `needs-human`); and a **toothless** check that passes the invalid sample (a tautology /
  over-broad check). **RUNNABLE-AT-AUTHOR-TIME** = idempotent (no persistent workspace side effects --
  a temp dir cleaned via `trap`/`finally` is fine) AND its input is available in-repo or
  **hand-synthesizable** AND it needs no live external dependency (no server boot, no network, not the
  full merged HEAD). **Two gotchas the rule encodes:** (1) `bash -n` / `sh -n` (or a `pwsh -NoProfile`
  parse) is a CHEAP FIRST PASS only -- **necessary, not sufficient**: the motivating bug (a LikeC4
  single-quote nested inside a bash `-e '...'` block, silently quote-stripped so the throwaway fixture
  was corrupted on EVERY attempt regardless of the task's output) is *syntactically valid* bash -- only
  EXECUTING it reveals the corruption. (2) The real input is often the task's **not-yet-authored
  output**, so "run against the real input" fails module-not-found -- the rule requires a **hand-written
  representative sample** of the expected artifact. This is the KEY case: a guardrail that RENDERS or
  EXECUTES the task's own output (into a throwaway workspace, a rendered fixture, an `--input-type`
  block) is exactly the one whose first real execution is deferred to runtime today, and exactly where
  its own harness/fixture bugs hide. **Carve-out -- NOT runnable-at-author-time** (needs a live service,
  the built binary, the full merged HEAD): the gate does NOT apply; run at least the syntax pass, reason
  explicitly about correctness, and **state in the breakdown/review report that the guardrail could not
  be author-time-executed and why** -- an honest deferral, never a silent one. **Distinct from #248:**
  #248 runs the *underlying tool* once to check a guardrail's assumption about that tool's PRINTED
  output; #302 EXECUTES the guardrail *script itself* against hand-synthesized valid+invalid samples to
  check the script's OWN correctness -- the motivating bug had no tool-output assumption, so #248's
  probe did not cover it. Homed here; enforced by `plan-breakdown` (Step 7.0d self-validate) and probed
  by `guardrails-review`.
- **State**: snapshot-in / fragment-out. Attempt gets an immutable snapshot
  (`GUARDRAILS_STATE_IN`); action may write a JSON-object fragment (`GUARDRAILS_STATE_OUT`);
  harness (single writer) deep-merges fragments into `state/state.json` in completion order after
  guardrails pass. **Single-writer-per-key is ENFORCED, not a convention (SSOT section 6.2)**:
  a fragment's top-level keys must each equal the writing task's OWN id -- which is its **FOLDER
  NAME** (the directory the `task.json` lives in, e.g. `02-generate-greeting`), NOT its `stableId`
  (an internal regeneration token; a `stableId`-keyed fragment is rejected as foreign, #164)
  (reserved keys -- none in
  v1). A foreign task id or any arbitrary shared key makes the fragment invalid-fragment -- it is
  rejected (not stripped), the attempt fails and retries with feedback, and nothing merges. This
  makes the harness the sole writer of every task's namespace. Scalars/arrays last-writer-wins
  within a task's own namespace. `state/seed.json` (committed) seeds the runtime state.

## Execution semantics

- Attempt = snapshot -> run action (failed action skips guardrails) -> **write-scope check**
  (if `writeScope` set: deterministic read-only git diff membership test in the segment worktree;
  violation = retry with feedback naming out-of-scope paths) -> run guardrails (`failFast` default)
  -> all pass: merge fragment + `succeeded` -> else compose `feedback.md` and retry.
- **Failed-attempt retry**: `git reset --hard <taskBase> + git clean -fd` in the segment worktree
  (preserving every upstream/sibling commit; `taskBase` != `preHead`).
- **Retry salvage / incremental retries (#195 -> #306)**: the harness STASHES a non-final worktree-mode
  rollback instead of pure discard, and exposes it to the retry as a first-class, AGENT-CONTROLLED input.
  Immediately BEFORE the F2 reset above, the attempt's full working tree (including uncommitted writes) is
  committed to `refs/guardrails/<taskId>/attempt-<N>` (a throwaway-index side-channel snapshot, never a
  real commit on the segment branch) AND a directly-applyable `prior-attempt.patch` (`git diff --binary
  <taskBase> <ref>`) is written into that attempt's log dir. The next attempt still starts from the clean
  `taskBase` -- unchanged, the DEFAULT -- but its `feedback.md` "## Prior attempt work is salvageable"
  section lets the agent pull **ALL** (`git apply prior-attempt.patch`), **SOME** (`git checkout <ref> --
  <path>` per file), or **NONE** (re-author), plus a `git diff --stat` summary. **#306 supersedes #195's
  scope guard**: salvage now fires for **EVERY non-final worktree failure** -- guardrail-fail, action-fail,
  timeout, max-turns, output-cap, write-scope -- NOT only the two non-logic budget-exhaustion outcomes,
  because the retry agent (informed by the per-guardrail verdicts, below) decides how much to reuse. A
  genuine no-op attempt (empty diff) is not offered a stash. **Two suppress-the-stash exceptions:**
  (1) **fragment-rejection** paths (invalid-fragment / foreign-key) keep the #162 re-author disclosure,
  not stashed; (2) a **protected-artifact (tests-untouched-class) guardrail failure** is suppressed AT
  CREATION (no ref, no patch) so a gamed edit is genuinely unrecoverable via salvage -- keyed off a robust
  archetype matcher (`GuardrailArchetypes.IsProtectedArtifactCheck`: the `tests-untouched` doctrine +
  pristine/unchanged/unmodified/immutable/read-only synonyms, NOT a bare `"untouched"` substring). That
  suppression is DEFENSE-IN-DEPTH only -- the real guarantee that a re-introduced gamed edit can never
  reach green is the deterministic per-attempt re-check (write-scope + the task's own guardrails, re-run on
  the FINAL state). Best-effort: a git-spawn fault during salvage (git off PATH -> `Win32Exception`, bad
  dir) degrades to no-salvage, never crashes the attempt. The `prior-attempt.patch` routes through the same
  `SegmentStaging` reconstructable-exclusions as the segment commit (#280) -- `node_modules` / `.guardrails-*`
  never bloat the patch -- via the throwaway index. Gated by `preserveAttemptsForSalvage` (`RunConfig`,
  default `true`). Salvaged files remain subject to the task's `writeScope` -- the check is retrospective on
  the FINAL state regardless of how it got there. Pruned on task settle-`succeeded` and on a full `--fresh`
  reset (a task parked at `needs-human` keeps its refs for human inspection).
- **Per-guardrail verdicts + honest retry messaging (#306, closes the #167 gap)**: a guardrail-failure
  `feedback.md` carries a "## Prior attempt: guardrail verdicts" ledger -- every guardrail marked ✅ (passed,
  do not break) or ❌ (failed, with its reason) -- so a one-token miss becomes a one-token fix, not a
  re-author. The PROMPT-action retry header is now chosen by what actually happened to the on-disk work:
  **Persisted** (serial/final -- "keep what already works", still true), **rolled-back-but-stashed**
  (worktree + salvage -- "SAVED, recover from the salvage section"), or **rolled-back-and-lost** (worktree,
  salvage off -- "not recoverable, re-author"). This fixes the #167 gap where the guardrail-fail/action-fail
  (and write-scope) headers falsely claimed "keep what already works" while the reset had discarded the
  writes. Serial mode is unchanged (writes persist across attempts -> the "keep what works" wording is
  already accurate, and no stash is needed).
- Retry budget exhausted -> `needs-human`; transitive dependents -> `blocked`;
  **independent branches keep running**.
- **No-op-deadlock short-circuit (#174 / #182)**: a guardrail-failed attempt escalates to `needs-human`
  IMMEDIATELY -- on the **2nd** such attempt, without exhausting the remaining budget -- when **both**
  hold: (a) the action made **no observable change** this attempt (a *genuine no-op*), AND (b) the
  guardrail failure is **byte-identical** to the previous attempt's, which was **also** a no-op. A no-op
  action cannot fix a guardrail failure it did not cause (e.g. a no-op verification task whose guardrail
  fails on an AI-merge duplicate it never wrote, #175), and an unchanged failure proves nothing
  converged -- so a
  further attempt has zero probability of differing. **"No observable change" is mode-specific**: in
  **worktree mode** it is exit 0 + no state fragment + no file diff vs `taskBase`; in **serial mode**
  (#182, no `taskBase`) it is exit 0 + no state fragment + the action's **stdout/stderr byte-identical**
  across the two attempts (the proxy for "the action behaved identically"). The byte-identical guardrail
  failure is the load-bearing "cannot converge" evidence in both modes. **Conservative**: never fires
  when the action wrote a fragment, never in worktree mode when the segment diff reports file changes,
  never in serial mode when the action's stdout/stderr CHANGED across attempts, never when the guardrail
  output CHANGED between attempts (those can still converge). Same `needs-human` transition as budget
  exhaustion. (SSOT section 7.)
- **Deterministic-script reproduction short-circuit (#264)** -- a **SIBLING** of the no-op one for
  `script` actions, which have **no agent to self-correct** between attempts. On the **2nd**
  guardrail-class-failed attempt the harness settles `needs-human` early when the action is a `script`, the
  run is in **worktree mode**, AND **both** the action's recorded stdout/stderr AND the guardrail-class
  failure (a failed guardrail, OR a write-scope violation keyed on its offending paths + git statuses)
  reproduced **byte-identically** to the previous attempt -- positive evidence the script is behaving
  DETERMINISTICALLY, so a re-run is provably pointless. It fills the gap the no-op short-circuit misses: a
  script that WROTE FILES is not a no-op (non-empty segment diff), so the #174 `ActionWasNoOp` half is
  false and it never fires. The flaky/nondeterministic escape hatch is preserved -- a script hitting a
  network service, stamping a timestamp, or with a flaky guardrail produces DIFFERENT output/failure across
  attempts and keeps its **full budget**. Retry feedback is **script-appropriate** (a deterministic-action
  header -- "no agent to self-correct... edit the script or its guardrail to converge", SSOT section 8),
  not the agent-oriented "fix what failed, don't start over" wording. Same `needs-human` transition as
  budget exhaustion. (SSOT section 7.)
- **Prompt-runner failure classification** (SSOT section 9, #114/#115/#119): a non-success prompt
  result is classified (in the runner quarantine) into `Transient` | `OutputCap` | `Timeout` | `Error`.
  - **Transient** (429/503/529, "overloaded", rate/session/usage limit): does NOT consume the retry
    budget -- the harness PAUSES (bounded backoff, bounded by `transientPauseBudgetSeconds`) and
    re-runs the same attempt, surfacing a distinct `PromptPaused` observer event. A rate limit is
    NEVER `needs-human` until the pause budget is spent (then a distinct `rate-limited` outcome,
    "re-run later"). A cleared pause is never journaled (observe-only).
  - **OutputCap** (`CLAUDE_CODE_MAX_OUTPUT_TOKENS`, default raised to 64000 via `maxOutputTokens`):
    distinct `output-cap` outcome + actionable "write incrementally / split" retry feedback.
  - **Timeout**: distinct `timeout` outcome + **mode-aware** retry feedback (issue #167) -- in
    **serial** mode "continue from preserved partial work"; in **worktree** mode the non-final
    attempt's segment is reset to `taskBase` + cleaned, so the feedback discloses the file-write
    rollback and instructs re-authoring (never the false "partial work preserved" claim). The retry
    clock is EXTENDED (1x -> 1.5x -> 2.25x, capped 4x). (SSOT section 7.)
- **Per-run cost cap** (`maxCostUsd` in `guardrails.json`, optional decimal USD): **every
  prompt-spend is charged against it** -- the journal's cumulative cost = sum of every attempt's
  `costUsd` PLUS the top-level `overheadCostUsd` overhead sink (harness-internal prompt spend that is
  NOT a task attempt: overwatch-diagnose #269 + AI-merge worker #314 + terminal needs-human triage
  #314; charged via `RunJournal.AddOverheadCost`, folded into `JournalCost.Total`). When that
  cumulative cost reaches/exceeds the cap, the scheduler stops launching new attempts -- each
  not-yet-launched task settles `needs-human` ("cost cap reached") and its transitive dependents
  `blocked`; an in-flight attempt is never interrupted. Absent => no cap; non-positive cap is a
  validation error (GR2012). See SSOT section 2.
- **Integration (A) Fast-forward**: a linear chain's commit where no sibling has advanced the
  plan branch -> `git merge --ff-only`, **no new union, no re-verify** (bytes already passed
  the task's guardrails in the segment worktree). The FF'd commit carries
  `Guardrails-Task`/`Guardrails-Run` trailers for resume.
- **Integration (B) Union** (fan-in, or non-FF where a sibling raced): real merge that MUST be
  re-verified on the merged bytes. Resolution: git auto-merge -> **AI-merge** -> human.
  - `git merge --no-commit`; on conflict, the **AI-merge worker** (a constrained prompt behind
    `IPromptRunner`) is a **BYTE PRODUCER** only -- it writes resolved bytes to
    `GUARDRAILS_MERGE_OUT` via the three-way on-disk env contract (`GUARDRAILS_MERGE_BASE`,
    `GUARDRAILS_MERGE_OURS`, `GUARDRAILS_MERGE_THEIRS`, `GUARDRAILS_MERGE_OUT`). Two
    **deterministic checks** gate the output: (i) no conflict markers remain (`git diff --check`);
    (ii) blast-radius: modified only the git-reported-conflicted files (`git status --porcelain`).
    Violation -> discard (`reset --hard`) + `needs-human`. 1 retry. `PromptResult.IsError` and
    exit code are **never** the verdict. AI-merge resolves harness-internal unions only; withheld
    at the `--merge-on-success` user-branch boundary.
  - **The verdict** (for both clean-auto and AI-resolved) is the **deterministic re-verify**:
    re-run **the integration-guardrail set** (integration-set-only, SSOT section 4.3) on the merged
    bytes via the attempt-decoupled `IReVerifier` seam (no attempt lifecycle, no action result). Any
    fail -> `reset --hard preHead`; `needs-human`; no fragment, no `mergeSequence`.
  - AI-merge + re-verify run in a private forked worktree **off** the serialize lock; only the
    final integration of the verified result into the plan branch is **under the lock**.
- **Merge-collision attribution on gate failure (#175), HEDGED (#272)**: when the terminal gate fails on
  the final merged HEAD (typically the whole-repo build/test), the `needs-human` diagnosis leads with the
  **reported failure detail as the PRIMARY signal**, then -- **only** when ≥2 `writeScope`s overlap --
  appends the overlapping task pairs + the shared path(s) as a **possibility to verify IF that detail looks
  merge-related**, NOT an assertion a collision occurred. Mere `writeScope` overlap is a **WEAK** signal: a
  TDD stub+impl pair overlaps **by design** (the impl overwrites the stub) and merges cleanly the
  overwhelming majority of the time, so the pre-#272 confident *"this may be a merge collision between
  '07-...' & '09-...'"* wording sent triage down the wrong path when the merge was in fact clean and the
  failure unrelated. The hedged hint still points at the real trap it exists to catch -- a 3-way/AI-merge
  that silently kept **both** copies of a definition two overlapping-scope tasks each appended to a shared
  file (a duplicate class/member, CS0101, with **no conflict marker**) -- but leaves the reported failure
  detail as the thing to trust first. The hint is **advisory + structural** -- derived PURELY from the
  `writeScope`-overlap topology (never the compiler error text / a CS-code), added **only** when ≥2
  `writeScope`s overlap (nothing for disjoint scopes). It is **attribution, not prevention**: the harness
  cannot generically detect a semantic duplicate (that is the build guardrail's job); the PREVENTION is
  authoring -- the overlapping-writeScope union-guardrail's **duplicate-definition check** (`plan-breakdown`
  emits it, `guardrails-review` flags its absence; catalogue → overlapping-writeScope union-guardrail).
  (SSOT section 3.3 / 3.4.)
- **B1 atomic settle** (under the serialize lock, fixed order): (1) deep-merge fragment into
  `state.json`; (2) `git commit` the integration carrying `Guardrails-Task`/`Guardrails-Run`
  trailers (FF'd commits AND merge commits); (3) consume `mergeSequence` + journal `Succeeded`.
- **Resume-by-trailer**: the plan-branch's trailer-bearing commits are the durable resume record.
  On resume, stale segment refs (`guardrails/<runId>/*`) are deleted **before** any trailer read
  (W-1: a trailer on a surviving segment ref that never FF'd is not authoritative).
- **End-of-run delivery**: when the run drains green AND `mergeOnSuccess`/`--merge-on-success` is
  set, the harness merges the plan branch into the user's original branch. **AI-merge is NOT used
  here.** A conflict / failed re-verify / dirty user tree halts, plan branch intact. Default OFF.
  The outcome is a `MergeOnSuccessResult`: `FastForwarded` / `Merged` (delivered, exit 0) or
  `Conflict` / `DirtyWorkingTree` / `HookRejected` (halted; work is durable on the plan branch, exit 2).
  **Green-but-undelivered warning (#340):** because the default is OFF, a wholly-green run can deliver
  NOTHING while the console reads like a delivering run — the verified work sits on `guardrails/<plan-name>`
  one `--fresh`/`reset -y` from destruction. The Scheduler sets `RunReport.WhollyGreenButUndelivered` (wholly
  green + `mergeOnSuccess` false + a real separate plan branch — worktree mode, NOT serial/`runOnCurrentBranch`,
  where the work is already in the user's checkout) and the CLI prints a **loud end-of-run warning** (naming
  the branch + the destruction risk) when it's true and the terminal gate passed. Exit stays 0 — a safety
  notice, not a failure. This is the WARNING backstop only; the default is unchanged (a default-flip is a
  separate decision under architect review).
- **Hook policy at the two commit boundaries (#149)** — internal vs user-facing commits are treated
  oppositely:
  - **Internal bookkeeping commits bypass user hooks.** The segment integration commit (`Integrate`,
    `--no-verify --allow-empty`) and the non-FF union merge commit (`CommitStagedMerge`, `--no-verify`)
    run with `--no-verify` — they are machine commits in throwaway worktrees on `guardrails/<plan>`,
    not the user's deliverable, so a global `pre-commit` hook (e.g. GitGuardian `ggshield`) must never
    gate harness plumbing. (Motivating incident: an offline `ggshield` hook failed an internal marker
    commit and crashed a run.)
  - **The user-facing merge-back KEEPS hooks** — deliberately, exactly like a manual `git merge`. The
    non-FF merge commit runs the user's `pre-commit`/`commit-msg` hooks. If a hook rejects it, the
    harness `git merge --abort`s (user branch left clean at its pre-merge HEAD), leaves the plan branch
    intact, surfaces the hook's stderr, and returns `HookRejected` — a graceful delivery-halt, not a
    crash. **Inherent FF caveat (intended):** an FF delivery creates no commit, so no commit hook fires
    there; hooks run on the non-FF merge commit only.
- **Honest halt on infrastructure faults (#150)**: an unexpected fault during a run (a task executor or
  integration git step throwing — e.g. git unavailable) is NOT an unhandled crash. The scheduler returns
  an **aborted `RunReport`** carrying a `RunAbort` (one-line `Headline`/`Remedy` + full exception
  `Detail`); the CLI renders the one-liner + remedy, writes the full fault to `logs/<runId>/abort.log`,
  and exits non-zero (harness error, exit 1). End-of-run cleanup still runs. An aborted report is failed
  regardless of per-task outcomes.
- Resume: `succeeded` is terminal (use `guardrails reset` to force);
  `needs-human`/`failed`/`blocked` -> fresh budget; crashed `running` -> `pending`.
  **Definition-drift halt (#274 Part A):** editing an already-`succeeded` task and re-running no longer
  silently reuses the stale cached segment (the pre-Part-A bug ran the OLD version -- even under `--fresh`
  before Part B). On resume, BEFORE the DAG is built, the harness recomputes each pre-settled-green task's
  `definitionHash` (over its `task.json` + resolved action + `guardrails/**` + `preflights/**`) and
  compares it to the hash recorded at its last settle (journal + `Guardrails-Task-Hash:` plan-branch commit
  trailer); a recorded-hash-absent settle (pre-upgrade) assumes unchanged -> match. A **mismatch on ANY
  pre-settled-green task HALTS the whole run** -- it schedules nothing and returns
  `RunReport.DefinitionDrift`, **exit 2**, with a per-file "what changed" report (which task drifted,
  old->new hash, per-file added/removed/modified, a `git diff <oldCommit>..HEAD` command, and the task's
  transitive-dependent set) -- rather than silently reusing the stale segment or silently auto-rerunning
  (unsound for a fan-in descendant off a base still carrying its own stale commit).
  **Safe-auto-resolve + scoped rewind (#274 Part C -- SHIPPED):** Part A's halt is lifted for a
  PROVABLY-SAFE subset. `S` = drifted tasks ∪ their `TransitiveDependentsOf` closure. ONE pure,
  matrix-tested predicate (`SafeSuffixEvaluator`) decides whether `S` forms a safe TRAILING SUFFIX of the
  plan branch's `--first-parent` `Guardrails-Task:`-trailer history, honoring the **merge-tip caveat**
  (a fan-in/union commit whose non-first-parent lineage carries a task NOT in `S` is REFUSED -- `git reset
  --hard` un-integrates that lineage too, but a first-parent walk never sees it). **Floor = HALT, never
  destroy:** a non-`S` trailer in range, an uncontained merge lineage, a trailer-less hand-fix commit in
  range, OR a **copied-trailer hand-fix** (#322, SSOT §7.2 rule 3 -- a task-in-`S` commit in the removed range whose
  `Guardrails-Task-Hash:` does NOT corroborate the journal's recorded settle hash for that task, in TWO cases:
  (a) **present-but-uncorroborated**, a hash the harness never recorded at that task's settle, forged OR
  "correctly" hand-typed; AND (b) **null-hash**, no `Guardrails-Task-Hash:` at all -- a #197
  copied-`Guardrails-Task:` hand-fix OR a genuinely pre-#274 machine commit predating hash-stamping -- both
  refusing on ANY branch, INCLUDING an all-null pre-#274 branch (rebuild with `reset <folder> -y`); there is NO
  null-hash exemption) all refuse. Corroboration reads the JOURNAL (single-writer provenance),
  never the branch trailer under test (circular), and a genuine modern settle always corroborates (commit hash
  == journal hash, both stamped at B1 settle; the recorded value does not move through a drift), so the
  deliberate-definition-edit auto-resolve still resolves Safe. When safe, the
  harness physically **rewinds the plan branch** (`git reset --hard` to
  the parent of `S`'s earliest commit -- DESTRUCTIVE on the harness-owned `guardrails/<plan>` branch, never
  the user's checkout; discarded commits stay reflog-recoverable), journal-resets `S`, and the next wave
  re-runs it from the clean base -- at the pre-DAG gate, before any segment is forked. **Two consumers, one
  primitive:** (1) run-time auto-resolve gated by the unified `autonomyPolicy` (SSOT §2.1, default
  `"prompt"` = prompt in an interactive TTY / HALT non-interactively; `"auto"`, via `--autonomy auto` or the
  legacy alias `--reprocess-drift`, = auto-resolve no prompt; `"halt"` = strict Part A). (2) manual scoped
  `guardrails reset <folder> <taskId>...` = the named set ∪ descendants; safe ⇒ rewind + reset, unsafe ⇒
  REFUSE naming the blocker (use `reset <folder> -y` for the always-sound full rebuild). **Unsafe drift
  ALWAYS halts under EVERY policy -- no flag authorizes an unsound rewind (spend, not soundness).** An
  auto-resolved run returns the NORMAL exit code (0/2) + emits `IRunObserver.DecisionRecorded` and appends a
  `boundary:"drift"` entry to the durable, unified top-level `decisions[]` journal section (SSOT §2.1/§7 --
  the canonical store, which replaced the pre-fold `driftResolutions[]`; its headline/subject/detail carry
  the rewind target + per-task old->new hash); only a declined/refused drift is the exit-2
  `RunReport.DefinitionDrift`. Serial mode / non-git = no plan branch to carry a stale commit, so both
  consumers degrade to a sound journal-only reset (no rewind). Unrecognized `autonomyPolicy` = **GR2031**.
  **Crash-atomic + CAS:** the rewind + per-task journal-reset are made crash-atomic by a
  `state/rewind-intent.json` marker (written before `reset --hard`, cleared after both effects persist,
  replayed idempotently on resume) AND a general resume invariant -- a journal-`succeeded` task whose
  plan-branch trailer is ABSENT (its commit was rewound off) MUST re-run, never be skipped (closes the lost
  non-drifted-descendant hole). A **compare-and-swap** on the plan-branch tip guards concurrent same-plan
  sessions / mid-prompt edits: the Scheduler executes the CLI's CAPTURED authorized plan and re-verifies
  the tip before rewinding, HALTING (never destroying) on a moved tip or a diverged plan. Attribution reads
  only a commit's LAST trailer block (git-interpret-trailers), so a `Guardrails-Task:` line in a hand-fix's
  prose is not mis-attributed. (SSOT §7.2.)
  **Outcome-agnostic by design (issue #190):** resume does NOT distinguish WHY a task is
  `needs-human` -- a rate-limited/timeout/output-cap halt (self-resolving) and a genuine
  `needsHuman`/permission-wall/exhausted-guardrail halt (needs a human fix first) both reset to
  `pending` with a full fresh budget on a plain re-run. A clean per-outcome tightening was evaluated
  and deliberately deferred (SSOT section 7) -- re-running without fixing anything can silently burn
  a budget re-failing the identical way.
  **Hand-fixing a merged WORKSPACE file (issue #197):** the user's checkout is read-only for the
  whole run, so a fix to a file an upstream task already wrote+merged must be committed on the
  harness's plan branch (`guardrails/<plan-name>`, at its integration worktree
  `<worktreeRoot>/<runId>/_integration`) -- NOT the user's own checkout. **Commit with a PLAIN message
  and NEVER copy any `Guardrails-*` trailer onto it (#322):** a copied `Guardrails-Task-Hash:` is
  misclassified as a machine segment and was *silently discarded* by the safe-suffix rewind pre-#322;
  a "correct" hand-typed hash is worse still -- it makes the drift check skip the task as
  pre-settled-green (a fake-green settle, violating honest-halts). A **trailer-less** hand-fix is the
  safe form: the refuse floor *protects* it from being rewound, and `CreateSegment` forks every new
  attempt off a LIVE rev-parse of the plan branch, so it is picked up automatically on the next resume.
  There is deliberately **no `guardrails hash` command** -- the trailer-less rule is the answer. Full
  steps: SSOT section 7.
- Harness exit codes: 0 green / 1 harness or validation error (incl. a run **aborted** by an
  infrastructure fault, #150) / 2 needs-human or blocked, OR a wholly-green run whose opt-in delivery
  was **halted** (`Conflict`/`DirtyWorkingTree`/`HookRejected` — work durable on the plan branch) /
  3 cancelled. See SSOT section 7.1.
- A prompt action can short-circuit with `{ "needsHuman": "<question>" }` in its fragment --
  no retry burn on a genuine human decision.
- **`needsHarnessWrite` (issue #191)**: a SECOND fragment escape hatch, parallel to `needsHuman` --
  `{ "needsHarnessWrite": { "path", "content", "reason"? } }` asks the .NET HARNESS PROCESS ITSELF
  (never subject to Claude Code's tool-permission layer) to write a `.claude/` file the action's own
  subprocess can never write (broader than #101's new-subdirectory-only gap; survives
  `dangerouslyDisableSandbox`). Unlike `needsHuman` it does NOT short-circuit -- guardrails still run
  afterward. Validated BEFORE the write with THREE checks: `WorkspaceContainment.Escapes` (always);
  `WriteScope.IsInScope` (only when the task declares a `writeScope` -- absent means allowed, mirroring
  section 3.4); and (#321) a **permission-file carve-out** -- `.claude/settings.json` and
  `.claude/settings.local.json` are DENIED (the harness will not write permission-granting files on an
  agent's behalf; a human must author them), while all OTHER `.claude/` deliverables
  (commands/skills/hooks/agents) remain writable. **Halt/hatch interaction (#321 -> #325 -> #329):** the
  permission-wall structural-`.claude/` halt (section 9.3) is now OUTCOME-AWARE -- consulted only on an
  attempt that did NOT converge, so a probe-then-hatch (or a read-source-recovery) attempt whose write
  lands and whose guardrails pass completes GREEN by the general rule (#325 removed the old #321
  `.claude/`-drop filter as redundant -- the converged OUTCOME is the authority, source-vs-destination
  moot). When a non-converged attempt DOES halt, WHAT it reports leads with the true primary cause (#329):
  a guardrail that genuinely RAN and FAILED is reported `guardrail-failed` with `failedGuardrails[]`
  populated (the `.claude/` wall carried as SECONDARY context in the summary/`feedback.md`), NOT
  `permission-denied` with an empty list. Only a wall with no guardrail failure to report -- an
  action-failed #104 first-attempt wall, or the eager #86 repeat -- stays `permission-denied`. The halt
  DECISION is unchanged; only the reported outcome/message/`failedGuardrails` differ. Singular per attempt
  in v1. SSOT section 9 / 9.3.
- **The overwatcher (active AI supervisor, #269, SSOT §9.2, design `docs/plans/11-overwatcher.md`)**: an
  **advisory** AI supervisor consulted DURING a run when a task struggles. It **subsumes** the shipped
  one-shot needs-human triage (now the §9.2.1 `TerminalExhaustion` case, invariants preserved verbatim) and
  adds EAGER triggers (#305 Decision C): it engages at **`attempt ≥ 2`** plus the typed transitions
  (no-op-deadlock #174/#264, permission-wall, terminal exhaustion), fires **at most once per attempt**, and
  is **bounded by `maxCostUsd`**. The diagnose core is ON by default (fires whenever an overwatch-capable
  prompt runner resolves — the reserved **`overwatch`** profile with fallback; a script-only plan gets none)
  and **never gates** — it classifies doomed-vs-retryable + renders a precise diagnosis. **The mechanical
  asymmetry (`OverwatchFixClassifier`, load-bearing):** a PURE classifier over the same guardrail/preflight
  folders + `task.json` verdict fields that `TaskDefinitionFiles`/`PlanDefinitionHash` fold over decides each
  proposed fix's authority — **DENYLIST** (the verdict surface: any guardrail/preflight body OR `writeScope`/
  `scope`/`dependsOn`/`integrationGate`) is propose-only at EVERY tier (applying it re-stales the #260 review
  marker via `PlanDefinitionHash`); **ALLOWLIST** (ephemeral guidance injection + `maxTurns`/`retries`/
  `timeoutSeconds` overrides) is the action/budget layer; **DEFAULT** (unclassified, incl. `action.prompt.md`
  edits in v1) is propose-only (closed allowlist). Tiers map onto the shared `autonomyPolicy` (NO new field):
  `halt` = diagnose+always-halt; `prompt` (default) = diagnose + TTY-propose the allowlist lever
  (non-interactive → honest halt); `auto` **degrades to `prompt` in v1** (silent auto-apply is v2). **"No
  sanctioned change ⇒ no grant ⇒ honest halt"**: it may never grant "keep trying, unchanged" — a granted
  retry ALWAYS applies a sanctioned change (guidance/budget), which is exactly how it can un-halt the #174/
  #264 floor (the deterministic short-circuits stay the FLOOR). **Disjoint from the drift-halt by task
  state** (§7.2): overwatcher acts on FAILING tasks in-run; drift-halt on already-`succeeded` tasks at
  resume. **Reporting:** a `boundary:"task"` `decisions[]` entry (durable audit) + an append-only per-task
  `logs/<runId>/<task-id>/overwatch.jsonl` (multi-fire detail). v2 bets: silent `auto`-tier auto-heal +
  persistent authoring-defect fixes + the inter-wave role.

## The four-stage workflow

PLAN (human+agents write/review the .md) -> BREAK (`/plan-breakdown` generates the folder,
**inserting guardrail-enabling tasks** like authoring the unit tests an implementation task's
guardrails will run -- with tests-fail-on-current-code proving non-tautology) -> REVIEW
(human edits; `/guardrails-review` adversarial pass: "what wrong implementation passes these?")
-> RUN (`guardrails run`). Generated output is always a **draft** until a human reviews it.

BREAK ends by generating `diagram.md` (`guardrails graph`); REVIEW re-checks it
(`guardrails graph --check`) and regenerates if the human's edits made it stale.

## Multi-wave plans (nested layout, M2 v1 -- SSOT section 14)

The recursion is **`task ⊂ wave ⊂ plan`**: a **wave** is a first-class completion unit -- a task DAG plus
its own entry/exit gates -- that participates in the SAME resume + drift + reset model as a task, one level
up. A **waved plan** is a strictly-ordered sequence of waves sharing **one run config, one continuous plan
branch, and one continuous journal**, separated by **hard barriers**. There is **no DAG of waves** -- a
total order driven by the wave folder's numeric prefix.

- **Layout / detection.** A plan is *waved* iff it has **no root `tasks/`** AND >=1 immediate subdir
  matching **`^wave-([0-9]+)-[a-z0-9-]+$`** (the numeric group is load-bearing -- it drives the strict
  order). Each wave is a mini-plan folder `<plan>/<waveDir>/{preflights,guardrails,tasks}/`. A flat plan is
  unchanged. Loader/validator codes: **GR2032** mixed layout (root `tasks/` AND wave dirs), **GR2033** wave
  numbering (duplicate `NN` or a non-conforming sibling dir = error; a numbering **gap** = warning),
  **GR2034** cross-wave `dependsOn` (`dependsOn` is intra-wave, plain sibling folder names only).
- **Wave-qualified identity (the load-bearing delta, generalizes invariant #2).** In a waved plan a task's
  canonical id is **`<waveDir>/<taskFolder>`** (e.g. `wave-02-provision/01-author-tests`). This id is the
  journal `tasks{}` key, the `Guardrails-Task:` trailer value, AND the **section 6.2 single-writer
  state-fragment key** -- a fragment's top-level key must equal this exact id, so two waves may each reuse
  `01-` numbering with zero namespace collision; a bare/other-wave key is rejected as foreign (exactly like
  a `stableId`-keyed one, #164). The GR2022 cross-task state-read lint gains a **wave-aware branch**: an
  earlier-wave read is satisfied by the barrier, a same-wave read still needs the `dependsOn` ancestor, a
  later-wave read is an error.
- **`WaveDefinitionHash`** (section 7.2/7.3) nests between `PlanDefinitionHash` and `TaskDefinitionHash`:
  it FOLDS each constituent task's `TaskDefinitionHash` value plus the wave's own preflight/guardrail gate
  files, and **excludes the shared `guardrails.json`** (Decision C). Folding the child hashes guarantees
  the levels cannot drift. It anchors **wave-level drift**: a COMPLETED wave whose hash changed on resume
  gets the SAME drift treatment as a task -- halt/prompt/auto per `autonomyPolicy`, a `boundary:"wave"`
  `decisions[]` entry. Drift fires ONLY on already-COMPLETED units; editing an all-`pending` future wave is
  sanctioned forward adjustment, NOT drift (the `isCompleted` predicate is the clean separator).
- **Wave loop + hard barrier (LANDED, M2b).** `Scheduler.RunWavedAsync` drives, per wave in strict order:
  skip if complete (resume, + a wave-drift check); the between-wave JIT checkpoint (an empty/unauthored next
  wave HONEST-HALTS `RunReport.WaveHalt`, pointed at the integration worktree, Decision D); the wave ENTRY
  preflight (skip-once); drain the wave's DAG on the continuous plan branch via the EXISTING Scheduler
  worker loop (a shared `DrainAsync`); HARD BARRIER (full drain -- any needs-human/blocked/failed halts the
  run, later waves never start, their tasks reported `blocked`); the wave EXIT gate; the **`Guardrails-Wave:`
  / `Guardrails-Wave-Hash:` marker commit** (Decision E) + journal the wave complete. **Continuity is the
  load-bearing refactor:** ONE integration handle + runId + journal + `settled`/`directoryOwner` are created
  ONCE and shared across every wave's drain -- never a fresh integration/journal per wave -- so wave-1's
  records coexist with wave-2's and the plan branch is one continuous branch. Per-wave `waves[]` journal
  record + entry/exit markers mirror `planPreflights`/`planGuardrails`. Wave-scoped `guardrails reset <plan>
  <wave>` rewinds that wave + downstream; runtime wave-drift (a completed wave's hash changed) is
  halt/prompt/auto per `autonomyPolicy` with a `boundary:"wave"` decision. Both the wave rewind and the wave
  reset ROUTE THROUGH the **marker-aware `SafeSuffixEvaluator`** (a `TrailerCommit.IsWaveMarker` flag EXEMPTS
  the harness's own `Guardrails-Wave:` marker commits from the trailer-less REFUSE, #311): the evaluator
  DERIVES the reset target from the live first-parent history (always an ancestor -- no dangling-`MarkerSha`
  sideways reset), and STILL REFUSES if a trailer-less NON-marker commit (a #197 human hand-fix) is in the
  removed range -- so the always-safe-suffix property holds for pure-harness history but a rewind never eats
  a human's fix. Reuses the Part C rewind primitive + a tip CAS + the crash-atomic `RewindIntent` (now
  carrying the wave dirs, so a crash-replay clears the wave entries). FLAT plans have no markers, so the
  task-path behaviour is byte-identical. `graph <plan>/<wave>` per-wave sub-diagrams are the one deferred v1
  nicety (`graph <plan>` renders the whole waved DAG).
- **STATUS (M2 v1): the FOUNDATION (M2a) + the EXECUTION LOOP (M2b) both LANDED + tested.** M2a: nested-layout
  loader/validator (GR2032-GR2034, GR2022 wave branch), wave-qualified identity (the single-writer key),
  `WaveDefinitionHash`, the journal `waves[]` schema (`WaveStatus`/`WaveJournalEntry` + `MarkerSha`), the
  `DependencyGraph.Waves()->Tiers()` rename, a committed waved fixture that validates clean. M2b: the
  wave-execution loop -- one continuous integration worktree + journal + plan branch across waves, per-wave
  entry/exit gate invocation, the `Guardrails-Wave:` marker commit, cross-wave resume, runtime wave-drift
  resolution, and wave-scoped reset. **`guardrails run` on a waved plan now ACTUALLY RUNS** wave by wave
  behind hard barriers (the M2a honest-halt exit-1 stub is GONE); `plan` output is wave-aware (per-wave
  tiers); the live/plain UI segments the task table per wave (`IRunObserver.WaveStarting`/`WaveFinished`).

**Authoring waved plans (the BREAK/REVIEW side -- procedures in `plan-breakdown` Step 9 +
`guardrails-review`; #254).** The execution contract above is the RUN side; authoring adds:
- **Detection is a layout fork at breakdown time.** `plan-breakdown` emits the nested layout iff the
  source plan is authored as ordered STAGES whose later stages build on the prior stage's *materialized*
  artifacts (real file paths/signatures that don't exist until an upstream stage runs). A flat / single-
  stage plan stays flat -- **do NOT wave for parallelism** (fine-grained parallelism is a task DAG inside
  ONE wave; a wave barrier destroys cross-wave parallelism -- SSOT §14 C5).
- **A wave is a mini-plan; author it with the SAME procedure, one level up.** Run the whole breakdown
  (TDD split, sparsest intra-wave DAG, guardrail selection, generative insertions) *per wave*. The
  four-folder model applies at wave granularity: `<plan>/<wave>/preflights/` = the wave **ENTRY gate**
  ("the prior wave's outputs MATERIALIZED" -- the **#181 positive-baseline archetype at the wave
  boundary**, positive-monotone-safe assert-present) and `<plan>/<wave>/guardrails/` = the wave **EXIT
  gate** (this wave's terminal postconditions). `terminal-gate-of-wave-N == preflight-of-wave-(N+1)` --
  one boundary, two authored folders. **GR2028 applies per wave**; every INTERMEDIATE wave's exit gate
  keeps a whole-build/suite check LOCAL and any `scope:"integration"` guardrail union-safe/conditional
  (#125/#165 per wave), while the LAST wave's exit gate -- run on the fully-merged HEAD -- is the
  whole-plan terminal boundary.
- **JIT staged breakdown -- the key capability.** A downstream wave often can't be broken down up front
  (its tasks reference artifacts that don't exist until the prior wave runs). So `plan-breakdown` supports
  authoring wave N+1 **after** wave N executes, **reading the materialized upstream from the integration
  worktree** (`<worktreeRoot>/<runId>/_integration`, Decision D -- the user's checkout stays read-only).
  Workflow: break down + review the ready waves; leave a not-yet-designable wave as a declared **stub**
  (empty `tasks/`); `run` executes to the stub and **honest-halts** (`RunReport.WaveHalt`,
  `NextWaveUnauthored`) pointing at the integration worktree; author the wave against that materialized
  workspace; `/guardrails-review` **that single wave** (each wave has its own `PlanDefinitionHash`-keyed
  review marker); resume. The whole-plan "break down everything up front" path still works when the
  downstream waves ARE designable up front.
- **`dependsOn` is intra-wave only (GR2034); the state key is wave-qualified.** A cross-wave dependency is
  expressed as the downstream wave's entry gate, never a task edge. A prompt action's state fragment must
  be keyed by the wave-qualified id `<waveDir>/<taskFolder>` (a bare folder-name key is rejected foreign
  every attempt -- the #164 loop one level up).
- **Per-wave author-time smoke-test (#302).** Every runnable script guardrail generated in ANY wave's
  four folders (task-level AND the wave entry/exit gates) is EXECUTED against a valid + invalid sample at
  author time. A wave entry gate that checks the not-yet-materialized upstream is the high-value
  render/execute target -- hand-synthesize a materialized + a missing-artifact sample and run it both
  ways. Worked authoring example: `examples/waved-hello/` (a 2-wave demo that `guardrails validate`s
  clean) + `plan-breakdown/references/example-breakdown-waved.md`.

## Load-bearing invariants

1. **Deterministic over prompts** -- prompt-judges are last resort, never alone, and
   must pass the demotion gate in the guardrail catalogue.
2. **Harness is the single writer of merged state**; children only ever get snapshots and
   write fragments.
3. **Verdicts come from files, never CLI exit codes** (prompt guardrails). The AI-merge
   worker is a BYTE PRODUCER -- its exit code is never a verdict either.
4. **`docs/plans/02-schemas-and-contracts.md` is the schema SSOT** -- C# serializers,
   skills, and examples implement it; never fork it.
5. **Honest halts** -- the harness never marks unverified work done.
6. **Worktree isolation is the concurrency safety boundary** -- each task runs in its own
   segment worktree; the user's checkout is read-only for the entire run.

## Where truth lives

| Question | Document |
|---|---|
| Any schema / env var / contract | `docs/plans/02-schemas-and-contracts.md` (SSOT) |
| Mental model, principles, out-of-scope | `docs/plans/01-overview.md` |
| Milestones, exit criteria, v2 bets, risk register | `docs/plans/03-roadmap.md` |
| Founding plan (history) | `docs/plans/00-initial-plan.md` |
| Multi-wave plans (nested layout, design of record) | `docs/plans/10-multi-wave-plans.md` (contract in SSOT section 14) |
| The overwatcher (active AI supervisor, design of record) | `docs/plans/11-overwatcher.md` (contract in SSOT §9.2/§9.2.1) |
| Golden example (runnable + skill reference) | `examples/hello-guardrails/` |
| Waved worked example (2 waves, validate-clean) | `examples/waved-hello/` + `plan-breakdown/references/example-breakdown-waved.md` |

## Status (update as milestones complete)

- M1 Foundations: **complete** (docs + golden example committed).
- M2 Walking skeleton: **complete**. Solution (`Guardrails.Core`/`.Cli`, net8.0 dotnet
  tool), `PlanLoader`/`PlanValidator` with precise diagnostic codes (GR10xx loading,
  GR20xx validation), `InterpreterMap` (SSOT section 5.2, injectable PATH probe),
  `ProcessRunner` (ArgumentList, tree-kill timeout), `SerialExecutor` (serial, ordinal
  folder order, dependency-failure -> blocked, no retry), and `guardrails run`/`validate`.
  3-OS CI matrix in `.github/workflows/ci.yml`. M2 runs **script actions only** -- a plan
  with prompt actions/guardrails validates fine but `run` fails fast ("not supported until M5").
  State/journal/log env vars are **not** set yet (only `GUARDRAILS_PLAN_DIR`, `_TASK_ID`,
  `_TASK_DIR`, `_ATTEMPT`="1").
- M3 State + journal + resume: **complete**. `JsonMerger` (pure deep-merge) + `StateManager`
  (single writer of `state/state.json`: seed/init, immutable snapshots, fragment merge after
  guardrails pass, `merge-conflicts.log`, atomic writes). `RunJournal` (`state/run.json` per
  section 7: kebab-case status/outcome strings, attempt records, `planHash`, loud warning on
  resume mismatch) with the section 7 resume matrix. `SerialExecutor` now threads snapshot ->
  action -> guardrails -> merge, writes the section 8 per-attempt log layout. New CLI: `status`,
  `reset <folder> [task]`, `run --fresh`. M4 still owns DAG/parallelism/retry/needs-human.
- M4 DAG + parallelism + retry: **complete**. `DependencyGraph` (cycle detection -> GR2007,
  **`Tiers()`** = the DAG's topological levels [renamed from `Waves()` in M2 to free "wave" for the
  plan-stage concept, SSOT section 14.4], transitive-dependent closure), Channel-based `Scheduler`
  (maxParallelism workers; failure blocks the transitive closure while independent branches finish;
  resume pre-pass), `TaskExecutor` retry loop (budget = 1 + retries; `feedback.md` written per failed
  attempt; budget exhaustion -> `needs-human`; cancellation -> `pending`), `guardrails plan` (**tiers**
  preview), Spectre live table UI, exit code 3 on cancellation. `SerialExecutor` is gone --
  `Scheduler` with maxParallelism 1 is serial mode.
- M5 Prompts: **complete**. Full `promptRunners` config parsing; `IPromptRunner` seam,
  `ClaudePromptRunner` (the ONLY place Claude flag spelling + `stream-json` parsing live --
  prompt via stdin, cwd = workspace, `--add-dir <planDir>`, teed to `claude-stream.jsonl`);
  `PromptComposer` (body + Shared state + Output contract + Previous-attempt feedback +
  Verdict contract); `GuardrailVerdictReader` (verdict file is the ONLY pass/fail authority).
  **needsHuman short-circuit** (SSOT section 9): escalates IMMEDIATELY -- no retry burn,
  no guardrails, no fragment merge. Prompts now run; tested tokenlessly + opt-in real-claude.
- **Reality Gate 1 + 2: MET.** `guardrails run examples/hello-guardrails/hello-guardrails`
  completes end-to-end green with the real Claude CLI (~$1.00 total).
- M6 Skills: **complete**. `plan-breakdown` SKILL.md + references (guardrail-catalogue with
  archetype table / decision tree / demotion gate / anti-patterns), `guardrails-review`
  (adversarial "cheapest wrong implementation" pass, BLOCKER/WEAK/NIT, per-finding approval),
  agents `guardrails-harness-developer`/`-skill-author`/`-test-author`/`-devils-advocate`,
  `guardrails-dev-knowledge`, lightweight `uber-report` (Reality Gate-first), and the real
  README. **Round-trip proven**: a fresh breakdown of `hello-guardrails.md` validates clean
  and matches the golden folder.
- **Reality Gate: MET -- all three booleans** (build+tests; end-to-end run on real Claude,
  verified 2026-06-10 at ~$0.90; plan-breakdown round-trip).
- M7 Polish + packaging: **complete except dogfood execution** (which awaits human review).
  Run-level cost aggregation (`JournalCost.Total`). `guardrails run --dry-run`: validates,
  prints waves preview + per-task resolution (kind/runner/retry-budget) + journal-aware
  resume SKIPs, exits 0 having touched no state. Packaging: PackageId
  `ServantSoftware.Guardrails`, version `1.0.0-preview.1`, MIT LICENSE, README packed;
  release pipeline `.github/workflows/release.yml` (tag `v*` -> 3-OS matrix -> pack ->
  `dotnet nuget push` via Trusted Publishing/OIDC). Clean-machine pack/install acceptance
  passed. **Dogfood artifact authored, not executed**: `docs/plans/04-dogfood-cost-cap.md`
  + breakdown folder await human review before `guardrails run`.
- **Plan-08 parallel execution** (branch `dogfood/plan-08`, draft PR #107, tasks 01-26
  complete): worktree isolation + reuse/chaining topology (plan branch off user HEAD,
  segment-worktree reuse for linear chains, fan-out inherit-one + fork-the-rest off W-2,
  fan-in fork); `writeScope` CHECK (deterministic read-only, triad replacement, GR2019/2020);
  per-attempt logs elevated to `logs/<runId>/<task-id>/attempt-N/` (sibling of `state/`);
  FF-free integration for linear chains (no re-verify), re-verified union for fan-in/non-FF;
  B1 atomic settle (fragment -> commit -> journal); AI-merge worker (byte producer,
  `GUARDRAILS_MERGE_BASE/_OURS/_THEIRS/_OUT` on-disk env contract, two deterministic gates,
  1-retry budget, verdict = deterministic `IReVerifier` re-verify); `scope: "integration"`
  guardrail set + terminal `integrationGate` sink (GR2017/2018); resume-by-trailer (FF commits
  carry trailers; stale segment refs deleted before any trailer read, W-1);
  `mergeOnSuccess`/`--merge-on-success` (AI-merge withheld at user-branch boundary);
  `maxParallelism: 3` default, `worktreeRoot`/`runOnCurrentBranch` config fields (GR2015/2016);
  triad (`captureHashes`/`restoreOnRetry`/`exclusive`, `WorkspaceLock`) REMOVED.
- **Retry salvage** (#195): a worktree-mode `max-turns`/`output-cap` rollback preserves the attempt's
  full working tree to `refs/guardrails/<taskId>/attempt-<N>` before the existing F2 reset discards
  it; the next attempt's `feedback.md` names the ref + a `git diff --stat` summary and instructs
  selective `git checkout <ref> -- <path>` adoption instead of a from-scratch redo. New `RunConfig`
  field `preserveAttemptsForSalvage` (default `true`). Salvaged files stay subject to `writeScope`
  (the check is retrospective on final state). Pruned on task settle-`succeeded` and on `--fresh`.
- **Incremental retries / stash-the-failed-attempt** (#306, supersedes #195's scope guard AND #167's
  "just make the messaging honest"): salvage now STASHES **every** non-final worktree failure --
  guardrail-fail, action-fail, timeout, max-turns, output-cap, write-scope -- not only the two non-logic
  budget-exhaustion outcomes, and exposes the stash as a first-class agent input: `refs/guardrails/<taskId>/attempt-<N>`
  PLUS a directly-applyable `prior-attempt.patch` in the attempt log dir. `feedback.md`'s "## Prior
  attempt work is salvageable" section offers pull-ALL (`git apply`) / SOME (`git checkout <ref> -- <path>`)
  / NONE. Guardrail-fail feedback also carries a per-guardrail **verdict ledger** (✅/❌ + reason). The
  PROMPT retry header is now rollback-aware (Persisted / rolled-back-but-stashed / rolled-back-and-lost),
  closing the #167 gap where guardrail-fail/action-fail/write-scope headers falsely claimed "keep what
  already works" after a reset. Clean-slate reset to `taskBase` stays the DEFAULT; stash is opt-in for the
  agent. Fragment-rejection paths (invalid-fragment/foreign-key) keep the #162 re-author disclosure and are
  NOT stashed (documented exception). A **protected-artifact (tests-untouched-class) guardrail failure** is
  also suppressed AT CREATION (no ref/patch — gamed edits unrecoverable via salvage) via robust archetype
  matching (`GuardrailArchetypes.IsProtectedArtifactCheck`, NOT a bare `"untouched"` substring); this is
  defense-in-depth — the deterministic per-attempt re-check is the real backstop. Salvage git faults are
  best-effort (`Win32Exception` included), never crash; the patch excludes `SegmentStaging` reconstructable
  dirs (#280). Serial mode unchanged (writes persist). Code: `TaskExecutor.TryStashFailedAttempt`
  / `StashIfRollingBack`, `GuardrailArchetypes`, `GitWorktreeProvider.DiffAgainstBase`,
  `AttemptArtifacts.WriteSalvagePatch`, `RetryPolicy` (rollback/salvage-aware `AppendHeader` +
  `AppendVerdictLedger` + `AppendSalvageSection`), `SalvageRef.PatchPath`.
- **M2 multi-wave plans -- v1 FOUNDATION (M2a) + EXECUTION LOOP (M2b) both landed** (#254, SSOT section 14).
  See the **Multi-wave plans** section above for the model. **M2a (foundation):** nested-layout detection +
  waved loader/validator (**GR2032** mixed, **GR2033** numbering, **GR2034** cross-wave `dependsOn`, GR2022
  wave-aware branch); **wave-qualified identity** (`TaskNode.Id` = `<waveDir>/<folder>`, `TaskNode.WaveDir`,
  `PlanDefinition.Waves`/`IsWaved`, `WaveNode`), pinned by a no-collision property test; **`WaveDefinitionHash`**;
  the journal **`waves[]` schema** (`WaveStatus`, `WaveJournalEntry`); GR2028 per-wave; the
  `DependencyGraph.Waves()->Tiers()` rename; the committed `waved-example` load/validate fixture. **M2b
  (execution loop):** the continuity refactor -- `Scheduler.RunAsync` splits into a flat/waved dispatch over
  a shared `DrainAsync`, with ONE integration handle + runId + journal + accumulators created once and shared
  across every wave (never per-wave); `RunWavedAsync` = the wave loop + HARD BARRIER + per-wave entry/exit
  gates (via the `IReVerifier` seam) + the `Guardrails-Wave:` marker commit (`IWorktreeProvider.CommitWaveMarker`
  / `ReconcileWavesFromPlanBranch`); cross-wave resume (`EvaluateWaveCompletion` +
  `RunJournal.WaveEntryOf`/`RecordWaveCompleted`/`ResetWaveToPending`); runtime wave-drift (`boundary:"wave"`
  `DecisionEntry`, `autonomyPolicy` halt/prompt/auto); wave-scoped reset (`RunReset.WaveReset`, `guardrails
  reset <plan> <wave>`); `IRunObserver.WaveStarting`/`WaveFinished`; `RunReport.WaveHalt`. The M2a honest-halt
  exit-1 stub is GONE. **#311 remediation:** both the wave rewind AND the wave reset route through the
  marker-aware `SafeSuffixEvaluator` (`TrailerCommit.IsWaveMarker`) so a rewind never discards a trailer-less
  human hand-fix, derives an always-ancestor target, CAS-guards a concurrent run, and (via
  `RewindIntent.Waves`) crash-replays the wave entries. Tested: Core `SchedulerWaveExecutionTests`
  (continuity/barrier/resume/drift/reset/crash-replay) + `SafeSuffixEvaluatorTests` (marker exempt /
  trailer-less-non-marker refuse) + Integration `WaveExecutionRunTests` (real git: continuity + markers +
  materialization gate + resume + real wave rewind + hand-fix refuse + dangling-markerSha-ignored +
  HEAD-independence). Next-free GR code: **GR1010 / GR2037** (GR2035 = DuplicateCheckName — two checks in one
  folder sharing a `Name`, #332/SSOT §4.5; GR2036 = ExpectedDurationNonPositive — the optional guardrail
  `expectedDurationSeconds` progress hint ≤ 0, SSOT §4.1.1 / §12.1, issue #331 — the long-running-guardrail
  heartbeat).
- **M3 the overwatcher v1 (diagnose + propose) -- LANDED** (#269, design of record
  `docs/plans/11-overwatcher.md`, contract SSOT §9.2/§9.2.1/§8, #305 decisions baked in). The `Overwatch`
  component (`Guardrails.Core/Execution/Overwatch.cs`) SUBSUMES `NeedsHumanTriage` (now the §9.2.1
  `TerminalExhaustion` case, all invariants preserved) and adds EAGER triggers -- fires at `attempt >= 2`
  plus the typed transitions (no-op-deadlock #174/#264, permission-wall, terminal exhaustion), **at most
  once per attempt**, **bounded by `maxCostUsd`**. Diagnose core ON by default (reserved **`overwatch`**
  prompt profile with fallback; script-only plan gets none). The load-bearing **mechanical asymmetry**
  (`OverwatchFixClassifier`, pure, over the guardrail/preflight folders + `writeScope`/`scope`/`dependsOn`/
  `integrationGate` verdict fields that `TaskDefinitionFiles`/`PlanDefinitionHash` fold over): DENYLIST
  (verdict surface) = propose-only at every tier + re-stales the #260 review marker; ALLOWLIST (guidance
  injection + `maxTurns`/`retries`/`timeoutSeconds`) = the action/budget layer, proposed in v1; DEFAULT
  (incl. `action.prompt.md` edits in v1) = propose-only. Tiers map onto `autonomyPolicy` (no new field):
  `halt`/`prompt`(default TTY-propose, non-interactive honest-halt)/`auto`(degrades to prompt in v1). **"No
  sanctioned change => no grant => honest halt"** reconciles it with the #174/#264 FLOOR (which stays the
  floor). Reporting: `boundary:"task"` `decisions[]` + append-only per-task `overwatch.jsonl`. Disjoint from
  drift-halt by task state. `TaskExecutor` takes `Overwatch? overwatch` (was `NeedsHumanTriage? triage`);
  the CLI-side interaction seam (`IOverwatchInteraction`) defaults to non-interactive honest-halt in v1
  (mid-run TTY confirm is a v2 UX bet). Tested: Core `OverwatchClassifierTests` (asymmetry matrix) +
  Integration `OverwatchTests` (advisory-never-gates, no-sanctioned-change/grant, tier mapping, cost bound,
  reporting, eager once-per-attempt, un-halt-the-short-circuit, drift-disjoint). v2 bets: silent `auto`-tier
  auto-heal + persistent authoring-defect fixes + the inter-wave role. Next-free GR code: GR2037 (GR2035 =
  DuplicateCheckName #332; GR2036 = ExpectedDurationNonPositive #331).
- **Overhead-cost sink now covers THREE prompt sources (#314) -- LANDED.** M3's overhead sink was
  generalized: `JournalDocument.OverwatchCostUsd` -> `OverheadCostUsd`, `RunJournal.AddOverwatchCost` ->
  `AddOverheadCost` (also added to `ISchedulerJournal` as a default no-op so scheduler fakes are
  unaffected), SSOT §7 `overwatchCostUsd` -> `overheadCostUsd`. It now charges the `PromptResult.CostUsd`
  of ALL THREE harness-internal prompt-spend sources -- overwatch-diagnose (#269) + the **AI-merge worker**
  (`AiMergeResolver`, charged per attempt right after the runner returns, before the deterministic gates) +
  the **terminal needs-human triage** (`NeedsHumanTriage.RunAsync`, charged right after the runner returns,
  before any parse) -- so all three count toward `maxCostUsd` AND appear in the reported total. Clean rename
  (M3 shipped `overwatchCostUsd` on master only, never in a NuGet release). The journal is threaded into
  `IAiMergeWorker.TryResolveAsync` (as `ISchedulerJournal`) and `NeedsHumanTriage.RunAsync` (as
  `RunJournal`); charging always happens BEFORE the parse/gate so spend counts regardless of outcome.
