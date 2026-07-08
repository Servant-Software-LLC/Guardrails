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
- **Retry salvage (#195)**: for the two NON-LOGIC budget-exhaustion outcomes -- `max-turns` /
  `output-cap` -- the harness preserves a non-final worktree-mode rollback instead of pure discard.
  Immediately BEFORE the F2 reset above, the attempt's full working tree (including uncommitted
  writes) is committed to `refs/guardrails/<taskId>/attempt-<N>` (a throwaway-index side-channel
  snapshot, never a real commit on the segment branch). The next attempt still starts from the clean
  `taskBase` -- unchanged, deterministic -- but its `feedback.md` names the ref + a `git diff --stat
  <taskBase> <ref>` summary and instructs `git checkout <ref> -- <path>` to selectively adopt the good
  parts rather than re-deriving everything. Gated by `preserveAttemptsForSalvage` (`RunConfig`, default
  `true`). **Scope guard**: a `guardrail-failed` rollback is NEVER preserved by this mechanism (the code
  may be genuinely wrong); `timeout` is also out of scope (a generic budget signal, not a "real progress,
  just out of budget" one). Salvaged files remain subject to the task's `writeScope` -- the check is
  retrospective on the FINAL state regardless of how it got there. Pruned on task settle-`succeeded` and
  on a full `--fresh` reset (a task parked at `needs-human` keeps its refs for human inspection).
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
- **Per-run cost cap** (`maxCostUsd` in `guardrails.json`, optional decimal USD): when
  the journal's cumulative cost reaches/exceeds the cap, the scheduler stops launching new
  attempts -- each not-yet-launched task settles `needs-human` ("cost cap reached") and its
  transitive dependents `blocked`; an in-flight attempt is never interrupted. Absent => no cap;
  non-positive cap is a validation error (GR2012). See SSOT section 2.
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
  destroy:** a non-`S` trailer in range, an uncontained merge lineage, OR a trailer-less hand-fix commit in
  range all refuse. When safe, the harness physically **rewinds the plan branch** (`git reset --hard` to
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
  `<worktreeRoot>/<runId>/_integration`) -- NOT the user's own checkout. `CreateSegment` forks every
  new attempt off a LIVE rev-parse of the plan branch, so the commit is picked up automatically on
  the next resume. Full steps: SSOT section 7.
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
  afterward. Validated BEFORE the write with two checks reusing existing predicates:
  `WorkspaceContainment.Escapes` (always) and `WriteScope.IsInScope` (only when the task declares a
  `writeScope` -- absent means allowed, mirroring section 3.4). Singular per attempt in v1. SSOT
  section 9.

## The four-stage workflow

PLAN (human+agents write/review the .md) -> BREAK (`/plan-breakdown` generates the folder,
**inserting guardrail-enabling tasks** like authoring the unit tests an implementation task's
guardrails will run -- with tests-fail-on-current-code proving non-tautology) -> REVIEW
(human edits; `/guardrails-review` adversarial pass: "what wrong implementation passes these?")
-> RUN (`guardrails run`). Generated output is always a **draft** until a human reviews it.

BREAK ends by generating `diagram.md` (`guardrails graph`); REVIEW re-checks it
(`guardrails graph --check`) and regenerates if the human's edits made it stale.

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
| Golden example (runnable + skill reference) | `examples/hello-guardrails/` |

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
  waves, transitive-dependent closure), Channel-based `Scheduler` (maxParallelism workers;
  failure blocks the transitive closure while independent branches finish; resume pre-pass),
  `TaskExecutor` retry loop (budget = 1 + retries; `feedback.md` written per failed attempt;
  budget exhaustion -> `needs-human`; cancellation -> `pending`), `guardrails plan` (waves
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
  field `preserveAttemptsForSalvage` (default `true`). Scoped to non-logic outcomes only --
  `guardrail-failed` and `timeout` are never preserved by this mechanism. Salvaged files stay subject
  to `writeScope` (the check is retrospective on final state). Pruned on task settle-`succeeded` and
  on `--fresh`.
