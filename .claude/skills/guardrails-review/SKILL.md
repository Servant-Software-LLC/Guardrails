---
name: guardrails-review
description: |
  Adversarial second pass over a generated (and possibly human-edited) Guardrails
  task folder: per task, find the cheapest WRONG implementation that would pass all
  its guardrails. Use when the user says "review this task folder", "run guardrail
  review on <folder>", or after /plan-breakdown produces a draft. Read-only critique
  by default — applies fixes only with per-finding approval, and never deletes a
  human-added guardrail without naming it first.
---

# Guardrail Review

The plan-breakdown skill (and the human after it) decided what the guardrails are.
This skill attacks them. Its one question, asked per task:

> **"What is the cheapest action output that passes ALL these guardrails while not
> actually doing the task?"**

If such an output exists, that's a finding. An empty findings list backed by evidence
("attempted to game tasks 1–5 as follows; couldn't") is a valid outcome — don't pad.

## Procedure

### 1. Inventory
Read `guardrails.json`, every `task.json`, every action and guardrail file. Build the
DAG mentally. Run `guardrails validate <folder>` first — never hand-check what the
tool checks (schema, refs, cycles, zero-guardrail tasks, missing promptRunners).
Run `guardrails plan <folder>` to see the waves.

Then run `guardrails graph <folder> --check`. `diagram.md` is a deterministic
projection of the folder; the human and earlier passes edit guardrails between
breakdown and review, so a stale or missing diagram means the DAG changed since it
was drawn. Branch on the exit code:
- **exit 2** (stale or missing) → regenerate with `guardrails graph <folder>` and
  note in the Step 6 report that the diagram was refreshed to match the current folder.
- **exit 1** (a load/validate error) → do NOT regenerate; surface the error in the
  report — `--check` couldn't even load the plan, so the folder has a deeper problem.
- **exit 0** (fresh) → nothing to do.

While reading each guardrail script during this pass, flag any that pattern-match a
specific tool's PRINTED console output (a `Select-String`/regex on a build or test
tool's summary/error text) rather than just its exit code or a file it wrote — Step 2's
adversarial pass runs that tool once against the real workspace to check the pattern
against genuine output (see "Pattern-matching guardrail not verified against real
output" below). **Also flag every `.sh`/`.ps1`/`.py` guardrail that RENDERS or EXECUTES
the task's own output** (writes a throwaway workspace / rendered fixture, runs an
`--input-type`/`-e` block, parses `--json`) — Step 2's adversarial pass EXECUTES those
against hand-synthesized valid + invalid samples to prove the SCRIPT'S OWN correctness
(see "Script guardrail not smoke-tested against a valid + invalid sample" below, the
#302 probe — distinct from the #248 tool-output probe). **Also flag two more sets while
you are already reading:** (a) every guardrail asserting a property of **implementation
source** — §2's demotion probe asks whether a test could have carried it (#468); and
(b) every guardrail carrying **BOTH** a require-present and a forbid-present clause, or a
forbid-present clause of any kind — Probe C reconciles those pairs, and the collision they
can form is satisfiable by no file at all (#470). Noting all four candidate sets here
avoids re-reading every script during the pass.

**Get the breakdown report's `Seam ledger (#382)` in front of you NOW, before the pass** — §2's
passing-but-blind probe audits it row by row and then re-derives it from the folder, and there is no
`validate` check behind it. It lives in the Step 7.4 breakdown report, not in the plan folder, so in a
fresh session you have to ask for it; asking after you have already read every action prompt spends the
pass twice. If it cannot be produced, the probe still runs from the folder alone (its check 7) and the
Step 6 report says the ledger was unavailable — which is not the same finding as a ledger whose heading
is missing.

**Waved plan? Review wave-by-wave (#254).** A plan is *waved* when it has no root `tasks/` and ≥1
`wave-NN-<slug>/` subdir (SSOT §14; plan-breakdown Step 9). Each wave is a **mini-plan** — its own
`preflights/`/`guardrails/`/`tasks/` and its own `PlanDefinitionHash`-keyed review marker. `guardrails
validate`/`plan`/`graph` are already wave-aware; run them on the whole plan as usual. Then run the
adversarial pass (§2) **per task WITHIN each wave**, and give **each wave's entry/exit gates the
four-folder treatment** (§2 "Four-folder gap" probe, applied at wave granularity). Two review modes:
- **Whole waved plan** — review every authored wave, wave by wave, then `guardrails mark-reviewed
  <folder>` (or per wave).
- **A single freshly-authored wave (the JIT flow)** — plan-breakdown's JIT staged mode authors a
  downstream wave AFTER its upstream ran (against the materialized integration worktree), so you review
  **just that wave**: `<folder>/wave-NN-<slug>` is a mini-plan folder — run the same adversarial pass on
  its tasks + entry/exit gates, then `guardrails mark-reviewed <folder>/wave-NN-<slug>`. Do the
  waved-specific probes below (the "#254 — waved plans" block in §2) in either mode.

### 2. Adversarial pass per task (the heart)
Role-play a lazy or wrong implementer. Concrete probes (mirror of the catalogue's
anti-pattern list — `.claude/skills/plan-breakdown/references/guardrail-catalogue.md`):

- **Tautology**: does any guardrail check something the action itself writes to
  satisfy it? (Action controls the evidence.)
- **Echo-judge**: does a prompt-judge read the action's claims (summary, report
  *about* the work) instead of the raw artifact?
- **Replay-the-action**: does a guardrail re-run the action's own command (a full
  `dotnet build; dotnet test`) when the postcondition is expressible from recorded
  output — a produced artifact or a runner-written TRX (`GUARDRAILS_ACTION_RESULT` /
  `_STDOUT`, SSOT §5.1)? If so, suggest **verify-recorded-action-result** (#9): assert the
  artifact / parse the TRX instead of replaying. (Counter-check: a replay is the HONEST
  gate when no recorded GOOD target carries the postcondition — don't flag it then.)
- **Action-exit-code tautology / echo-judge on action stdout**: does a guardrail test
  `GUARDRAILS_ACTION_RESULT.exitCode -ne 0` (a tautology — the recorded exit code is
  ALWAYS 0 at guardrail time; a non-zero action failed the attempt before guardrails ran),
  or grep `GUARDRAILS_ACTION_STDOUT` for the action's own success word (`"Passed!"`,
  `"Build succeeded"` — an echo-judge, also SDK-version-brittle)? Fix: read a
  runner-written structured result (TRX) or a produced artifact, never the self-report.
- **Hollow output assertion** (#73): for a terminal/e2e guardrail whose task claims a
  **non-empty quantity of output** (migration moved-count, items written, rows produced,
  entities created), does the assertion green-light a **zero/null** result? Tells: a
  keyword-presence regex `Assert.*\([^)]*(Moved|Written|Count|Entities)` (matches
  `Assert.Equal(0, writer.Count)`), a bare `Assert.NotNull(...)`, or an `exit 0` with no
  positive-value check. "It didn't error" / "the keyword is present" is a structural no-op for
  "did anything get produced?" — a run that moved ZERO entities passes. Fix: require a
  **strictly positive** value (`(>\s*0|>=\s*1|NotEmpty\s*\(|True\s*\([^)]*Count\s*>\s*0)`), or
  read the runner-recorded count / state key and assert `> 0`. (Catalogue → positive-effect /
  non-hollow assertion.) BLOCKER — a zero-effect run goes green.
- **Judge-where-deterministic-possible**: for every `.prompt.md` guardrail, name the
  deterministic archetype that could replace it, or confirm none can (the 4-question
  demotion gate).
- **Over-broad**: "all tests pass" anywhere except the terminal `<plan>/guardrails/` folder.
  **Judge the filter's SCOPE, not its presence (#455).** A `--filter` does not make a guardrail
  narrow — a task-level filter keyed on a **plan-wide** selector (the trait every new test class in the
  plan carries, a whole test project, a bare namespace) IS this anti-pattern wearing a filter, and it
  reads perfectly well in isolation. That is how it survived a review pass. Resolve every task-level
  test filter to the actual SET of tests it selects and compare that set against the tests the task's
  own pair owns; the full two-direction probe is in §2 below (deadlock / tautology).
- **Model named but unservable — the pre-run availability check (#224 · `model-tiering-stage-1` charter D)**:
  walk the task folder you are reviewing and collect every model a task names **statically**, then assert
  each one resolves to a runner `guardrails.json` actually configures. **Reports, never rewrites** — the
  pass names the task and the model and leaves the fix to the human, exactly as it does for a weak
  guardrail; it never edits a `promptRunners` block or an `action.model` to make its own check pass.
  **Why this belongs to the REVIEW and not the harness:** `PromptRunnerRegistry.FromConfig` already refuses
  a config it cannot serve — but by then the run is IN FLIGHT: a wave may have committed work, and the
  operator meets a config problem as a mid-run halt instead of as a review finding. Registry construction
  is the **backstop**; this probe is the **gate**. Everything decidable from the plan plus the config gets
  decided BEFORE `run`.
  - **What to collect** — *statically named* = written in the folder or the config, readable without
    running anything: (1) each task's `action.model`; (2) each **surviving** prompt-judge guardrail's
    configured model — a judge's `.prompt.md` frontmatter names only a `runner`, never a model, so read
    the block it resolves to: `guardrailOverrides.model` when present, else that block's `model` (SSOT
    §2/§9); (3) each `promptRunners.<name>.model` itself, which is what every prompt on that block gets
    when no task overrides it. *Surviving* = the judges still standing after the demotion gate above — do
    not chase a judge you are already recommending be replaced by a deterministic check.
  - **Which runner will carry it** (SSOT §3/§9): `action.runner` > the prompt file's frontmatter `runner`
    > `promptRunners.default` (only when it names a declared block) > the sole declared block when exactly
    one exists > **nothing resolves**. **`action.model` does NOT select a runner** — there is no
    model→runner routing in the harness; a model is an OVERRIDE applied to whatever runner the task
    already resolves to (the Stage 2 tier resolver, #226, is the first thing that will route). So check
    each model against the ONE block that will carry it, never against the union of every declared block.
  - **Unservable, in three shapes.** **(a) Nothing resolves** — ≥2 declared blocks, no (or a dangling)
    `promptRunners.default`, and neither the task nor the judge names one; the registry throws "No prompt
    runner specified and no default is configured" → **BLOCKER**. **(b) The carrying block's `kind` has
    no concrete runner in this harness version** — `codex` / `openrouter` / `local`, of which only
    `claude` is real today (SSOT §9) → **BLOCKER**, and the shape that reads green everywhere else: such
    a config LOADS AND VALIDATES CLEAN by design (declaring a kind is legal — deliberately no diagnostic)
    and then fails registry construction with an `InvalidOperationException` naming the kind, so
    `guardrails validate` will never tell you and this probe is the only pre-run signal. **(c)
    Provider-family mismatch** — the id plainly belongs to a different provider than the carrying block's
    `kind` → **WEAK, and a judgement call**: a model identifier has **no enumerable valid set** (exactly
    why GR2030 checks only its SHAPE) and `providers init` never fabricates one, so there is no catalogue
    to look it up in. Report what you observed and why it looks wrong; never claim the model does not
    exist.
  - **Do not re-report what `validate` already says** — GR2004 (a runner name no block declares), GR2008
    (prompts but no `promptRunners`), GR2030 (a malformed `model` string), GR2009 (a `command` not on
    PATH, a WARNING since the plan may run on another machine). This probe covers precisely what those
    miss: a **well-formed** model on a **resolvable** runner the harness **cannot construct**.
  - **A judge whose model is resolved JUST-IN-TIME is OUT OF SCOPE here — and must NOT be silently
    skipped.** JIT judge resolution (the model chosen at judging time rather than written in the folder)
    is deferred to **#223**, where a judge can actually resolve to a non-Claude model and the check has
    more than one case to verify; by construction that resolution happens AFTER the work it judges is
    done, so a failure there has already been paid for. Until #223 fills the socket, the Step 6 report
    NAMES each judge this pass could not check and why, so the gap is **visible rather than assumed
    covered** — a review listing only what it verified reads as coverage it does not have. Keep that
    distinct from the ordinary *no model named* case (`model` null at both sites, so the runner is simply
    never passed `--model`): nothing is named, nothing is deferred, and there is nothing to check.
- **Missing / malformed positive-baseline (preflight) on a brownfield plan (#181)**: does the plan
  build onto **existing code that already has tests in the touched area** (a brownfield plan — it modifies
  project(s) with existing test coverage), yet carry **no `<plan>/preflights/01-baseline-<area>-tests-green`
  check**? Without it, a work task's `tests-pass` guardrail can fail from PRE-EXISTING breakage
  (misattributed → wasted retries → late `needsHuman`), and a new test's "red" is ambiguous
  (missing-behavior vs already-broken). Under the four-folder model the baseline is a **positive check FILE
  in the plan-root `<plan>/preflights/` folder** (a Full Flight Check evaluated once, before the DAG,
  against the starting repo) — NOT a no-op ROOT task. It runs the EXISTING area tests **via `--filter`**
  and asserts they PASS on the current code (area-scoped, deduped one-per-area, using the #179 re-emit
  form); it needs no `dependsOn` and no wired-in work-task edges (the preflight phase implicitly gates the
  whole DAG — "never build on red"). Check, if a baseline preflight IS present:
  - **(a) Targets only the PRE-EXISTING tests via `--filter`** — NOT the about-to-be-authored red tests
    (it runs before the DAG on the starting state — a baseline that would run a sibling `author-tests`
    task's failing tests is mis-scoped), and **NOT the whole suite/project**. A whole-project `dotnet test`
    in the preflight hits the #165/#176 compile-coupling trap (a mid-TDD project does not compile → false
    red no work task can fix). A whole-suite-scoped baseline is a **BLOCKER**.
  - **(b) It is a `<plan>/preflights/` FILE, not a task** — a lingering no-op ROOT baseline TASK (the
    retired `00-baseline-*` `exit 0`-action + `dependsOn:[]` model) is a finding: re-home it to the
    preflight folder. There is no action to no-op — the preflight file IS the verification.
  - **(c) Deduped one-per-area** — one preflight file per distinct touched test project, each scoped to its
    area, NOT one global whole-repo preflight.
  - **(d) Distinct from the terminal gate** (green START before the DAG via `<plan>/preflights/` vs green
    END on the merged HEAD via `<plan>/guardrails/` — a plan needs both).
  - **(e) The worth-it gate held** — target pre-exists, MODIFIES-not-creates, deterministic + cheap (a
    bounded, filtered command — a filtered `dotnet test` is fine; no live-service boot/poll), strictly
    narrower than the terminal gate, ≥2 work tasks build on the area.
  The inverse error: a **vacuous baseline on a GREENFIELD plan** (a `dotnet test` over a project with zero
  tests, which trivially passes) — it certifies nothing while looking like a gate; greenfield must have NO
  baseline preflight. A RED baseline preflight halts the run before the DAG (the general Full-Flight-Check
  semantics), and #179 (re-emit form) makes its WHY reach the halt feedback. The negative "not yet present"
  baseline is NOT a separate archetype — it already IS `tests-fail-on-current-code`/`tests-fail-on-stubs`
  (do not expect, or flag the absence of, a parallel "negative preflight" archetype; when emitted at plan
  level it is likewise a `<plan>/preflights/` assert-absent check). **WEAK** when the area is plausibly
  green at the start and only the baseline is missing; **BLOCKER** when there is concrete reason the area's
  existing tests are already red (every work task then mis-fails), or when a present baseline is
  whole-suite-scoped or is a lingering no-op ROOT task. (Catalogue → "Baseline-green / start-from-green
  (preflight)"; `stacks/dotnet.md §21`. plan-breakdown Step 5 adds the matching insertion rule.)
- **Coverage gap**: the action's stated completion criteria exceed what guardrails
  verify — name the unverified criterion. (E.g. action says "sorted by category";
  no guardrail checks sorting.)
- **Stale coverage check (#157)**: the inverse of the coverage gap — here a
  `covers-key-behaviors` guardrail requires MORE than the action prompt asks for, so a
  CORRECT implementation following the prompt can never satisfy it. For every
  `covers-key-behaviors` guardrail (one `if ($content -match "<token>")` / `-notmatch …
  exit 1` per behavior, or a `$hits -lt N` threshold), verify each required token is named —
  directly or via an obvious synonym — somewhere in the SAME task's action prompt. A token
  the guardrail's `match` requires but the prompt's scenario list does NOT mention →
  **BLOCKER** with the message: "guardrail requires `<token>` but action prompt does not
  mention it — the task will fail every attempt." (Mechanism: the implementation follows the
  prompt, the guardrail keeps demanding the removed token, every attempt gets contradictory
  "need `<token>`" retry feedback and the task dead-ends at `needsHuman`.) This is the
  **human-judgement complement** to the deterministic **GR2026** warning `guardrails validate`
  already emits (SSOT §4.4): the lint is a conservative keyword-presence heuristic (it stays
  silent on a synonym or a regex-shaped token); the reviewer resolves the cases the lint can't
  — confirm a flagged token really is stale (the prompt dropped the scenario) vs named only via
  a synonym (a false positive to clear), and catch a stale token the lint skipped because it was
  regex-shaped. Cross-check `validate`'s GR2026 output against this pass; don't merely re-report it.
- **Tests gameable**: implementation tasks whose tests can be edited by the same
  action — the implementation task's `writeScope` must EXCLUDE the test files its
  upstream test-author task owns (the deterministic write-scope test-exclusion, SSOT
  §3.4), so an edit to a test file fails the harness's read-only write-scope check. An
  implementation task with no `writeScope`, or one whose scope covers the test files, is
  gameable. Inserted test tasks missing the TDD "red" guardrail for their type (#155): a
  BEHAVIORAL-type test-author task missing the `build-passes` + `tests-fail-on-stubs` pair
  (a lone non-zero-exit red passes on a non-compiling garbage test file — BLOCKER); a
  data-model task split without a structural `[Fact]`/`[Theory]` covers-key-behaviors check.
- **Missing scope-boundary warning (#154)**: for any test-author task (`author-tests-*` / a
  task whose deliverable is a test file), check that `action.prompt.md` contains an explicit
  **harness-enforcement paragraph** — it must name the allowed path(s) (test file AND, under
  #155, any stub file the `writeScope` covers), the post-action `git diff` membership check,
  the retry consequence of an out-of-scope edit, and the `{"needsHuman": …}` redirect for an
  upstream missing-symbol compile error. Absence is a **WEAK** finding — the harness injects
  `writeScope` at run time, but without the consequence the agent may still drift on a compile
  error and fix a neighbouring file (an out-of-scope edit that burns a retry). Fix: add the
  Scope boundary paragraph (plan-breakdown Step 6 has the verbatim shape).
- **Missing `.claude/` `needsHarnessWrite` instruction (#313)**: for any PROMPT task whose primary
  deliverable is a file under `.claude/` (`.claude/skills/`, `.claude/commands/`, `.claude/hooks/`,
  `.claude/agents/`, `.claude/contexts/` — NEW or EXISTING file), check that `action.prompt.md`
  carries the `needsHarnessWrite` escape-hatch instruction — and that it is the STRAIGHT-TO-HATCH
  form: the agent is told to emit `{"needsHarnessWrite": …}` to the state-out path FIRST, WITHOUT a
  direct `Write`/`Edit` probe to the `.claude/` path (plan-breakdown Step 5b has the verbatim shape;
  the older reactive "if a direct write is refused, then emit …" phrasing is stale — a probe wastes a
  turn and, per #321, populates the permission-wall tracker). A Claude Code subprocess CANNOT write
  under `.claude/` — the tool-permission layer refuses it unconditionally (SSOT §9.3; the old
  `.claude/settings.json` grant is dead per #273), so a prompt that writes the file directly hits the
  wall on attempt 1 and the task dead-ends at `needs-human` before any guardrail runs. Absence is a
  **BLOCKER** — unlike the #154 scope-boundary paragraph (WEAK, a *risk* of drift), this is a *certain*
  dead-end: the wall is structural and deterministic, so the task as authored cannot complete
  autonomously. This is the doctrine gap that let the original `dfd-threagile` breakdown pass review
  and then hit the wall at run time. Fix: inject the verbatim straight-to-hatch `needsHarnessWrite`
  header (Step 5b). **Exemptions (not a finding):** a SCRIPT action writing `.claude/` (the harness
  runs it directly, off the tool-permission layer), and a task that declares `stagingOutputs` for its
  `.claude/` deliverable (it writes a staging path, not `.claude/` directly — SSOT §3.5).
  **Carve-out (#321) — a DIFFERENT finding:** a task whose primary deliverable IS a permission-granting
  settings file (`.claude/settings.json` / `.claude/settings.local.json`) cannot use `needsHarnessWrite`
  at all — the harness REJECTS writing those on an agent's behalf (a human must author them). Do NOT
  flag it for a missing header; flag it as needing a **human author** for that deliverable.
- **Unactionable failures**: guardrails that fail without printing a usable reason
  (retry feedback quality).
- **Failure detail lost to the tail (#179)**: for every guardrail that asserts a test suite
  **PASSES** (a `tests-pass` / `all-tests-pass` / `specific-tests-pass`, or a test driving a
  production seam), check it **re-emits the failure DETAIL at the END of stdout**. The harness
  feeds back only the **tail** of a failed guardrail's stdout (last ~60 lines / 4000 chars); default
  `dotnet test` prints each failure's assertion/exception text mid-run and ends with only
  `[FAIL] <name>` + a count. A bare `dotnet test … ; if ($LASTEXITCODE -ne 0) { Write-Output "…";
  exit 1 }` therefore tails out the test NAMES only — the next attempt sees WHAT failed, not WHY,
  and retries blind (plan-0009 burned 12 attempts to `needsHuman`). Tell: no
  `Select-String`/re-emit of failure-signal lines (`Error Message:`, `Assert.`, `Exception`,
  `Expected:`, `Actual:`) after the `dotnet test` call. Fix: the capture → emit-full-log →
  re-emit-at-the-end pattern (catalogue → "Failure detail must reach the retry tail";
  `stacks/dotnet.md §4.2`). **WEAK** (the run still passes/fails correctly; it degrades retry
  feedback, costing attempts). Do NOT flag the INVERSE `tests-fail-on-stubs` /
  `tests-fail-on-current-code` checks — a non-zero exit is their success, so there is no failure
  detail to feed back. **Second tell — a QUIET flag on the test command (`-v q` / `-v quiet` on
  `dotnet test`) defeats the rule even when the re-emit is present**: measured, `-v q` suppresses the
  whole `Error Message:` / `Expected:` / `Actual:` / `Stack Trace:` block and leaves only
  `[FAIL] <name>`, so the `Select-String` re-emits test NAMES and nothing else. Flag it **WEAK**
  (**BLOCKER** where the plan's own doctrine leans on the re-emit for a hard-to-diagnose task); fix by
  dropping the flag from the test command — quiet belongs on `dotnet build`, not `dotnet test`.
- **Grep-scope contamination**: a file-content guardrail that greps the project tree
  (`Get-ChildItem -Recurse | Select-String`) instead of the one file the task owns — a
  same-wave sibling sharing the term can satisfy it. (Catalogue anti-pattern.)
- **Keyword-not-structural**: an "implements/extends/declares" check matching a bare
  type name (`Select-String "IFoo"`) that a comment, `using`, or local copy satisfies —
  it should match the declaration construct (stack file's structural regex). Also flag an
  **accessor-order-sensitive** structural regex (#112): a property "declared/removed" check
  keyed on a fixed leading accessor — `\{\s*get` or `\{\s*set` (e.g.
  `public\s+\S+\s+NAME\s*\{\s*get`) — is **itself a finding**. C# accessor order is free
  (`{ get; init; }` ≡ `{ init; get; }`), so it **false-passes a removal check** when the field
  survives as `{ init; get; }` (an incomplete refactor ships green) and **false-fails a declared
  check** symmetrically. Fix: match up to the brace (`public\s+TYPE\s+NAME\s*\{`),
  order-insensitive; if accessor presence matters, test `(get|set|init)` anywhere inside the
  block. (Catalogue → member-order insensitivity; `stacks/dotnet.md §3.1`.) BLOCKER on a
  removal check — a lingering field reads as gone.
- **Comment-blind forbidden-keyword scan (#97, #98)**: a guardrail that scans a **source file**
  for **banned** constructs (read-only `MERGE`/`EXEC`/`xp_cmdshell`, no-shell, no-eval,
  no-`console.log`) by matching the **raw file including comments** — `Get-Content $f -Raw` then
  a banned-keyword `-match` with no comment-stripping. It false-POSITIVES on a comment, a string
  literal, or disabled code that *names* the banned thing. The poison case to hunt: the **same
  task** both (a) tells the action to write a **safety-header comment** naming the banned constructs
  AND (b) greps for them without stripping comments — a guaranteed false positive that whack-a-moles
  a CORRECT read-only artifact to `needs-human` (each retry strips one mention, exposes the next).
  Tell: a `Get-Content -Raw` keyword check on a source file with no `/* */` + `--` (or `//`) strip
  upstream of the match. Fix: strip the source language's comments before matching (blank-in-place
  for line-number-reporting checks); and don't pair a header-documenting prompt with a comment-blind
  grep. BLOCKER — a correct implementation fails permanently. (Catalogue → comment-blind keyword
  scan; `stacks/dotnet.md §11`.)
- **Hollow / incomplete derived corpus (#99)**: a task whose deliverable is **derived artifacts
  over a set of inputs** (doc mining, codegen-from-spec, crawl→one-output-per-page, dataset import)
  whose guardrails verify only **shape** — `file-exists` + a marker line — so a green run ships an
  **empty or partial** corpus (worse than a hard failure: it looks done). Three tells: a one-line
  **stub** passes a marker check (F1); an **index** naming only 1 of N outputs "resolves" (F2); a
  crawl capturing **2 of N** pages passes because the checks verify "what I listed exists," never "I
  listed enough" (F3 — look for guardrails iterating the *outputs* rather than the *inputs*). Fix:
  require the four completeness/substance guardrails — input→output coverage, per-output substance
  floor (anti-stub), index completeness (`produced ⊆ indexed`), ingestion lower bound. Name them as
  **lower bounds**, not faithfulness checks (the semantic residual is a human pass or a
  demotion-gated judge — never a judge alone). BLOCKER — a green run ships a hollow/partial corpus.
  (Catalogue → corpus / aggregation completeness.)
- **Terminal-postcondition at integration scope (#125)**: a `scope:"integration"` guardrail that
  asserts a **terminal postcondition** — "the final combined output exists", "the sink wrote its
  aggregate", "all N contributors present" — instead of a **union-safe invariant**. Per SSOT §4.3
  the integration set re-runs at **every** union point (every fan-in / non-FF integration, §5.3 case
  B), on partial merges where downstream tasks have **not run yet** — so a terminal postcondition
  spuriously fails at an intermediate union and escalates a healthy partial merge to `needs-human`
  (surfaced live by `parallel-hello`). Decision test per integration guardrail: *"would this pass on
  a partial merge with a downstream task unsettled?"* Evaluate that test against **every union point
  that can occur anywhere in the plan before the guardrail's own task has run** — not only unions
  structurally upstream of that task in the DAG. `scope:"integration"` re-verifies at every fan-in
  **plan-wide** (SSOT §4.3, "no per-task or per-colliding-sibling guardrail selection at a union"), so
  a merge by a **completely unrelated parallel sibling** counts just as much as one that feeds the
  guardrail's own ancestor chain. "Does a union feed into MY task's ancestors?" is the too-narrow
  version of this question, and it will miss exactly that case — two siblings with zero dependency on
  the guardrail's task, each merging back onto the plan branch before that task has even started
  (#250: this is precisely what happened to a composition-root wiring guardrail live in review — see
  the catalogue's composition-root section for the matching gotcha). If **no**, it is a terminal
  postcondition wearing an integration scope. Fix: keep the integration gate to an invariant true of
  any valid intermediate union ("any produced file present is non-empty + conflict-marker-free"); move
  the terminal assertion to a `local` guardrail on the sink (runs in-attempt on the sink's segment).
  BLOCKER on a parallel plan with unions — it spuriously red-halts a correct run. (Catalogue →
  union-safe integration section; SSOT §4.3/§5.3.)
- **Overlapping writeScopes with no integration union-guardrail (#132)**: when **two or more tasks
  have OVERLAPPING `writeScope`s on a shared file/path** (colliding siblings that can both write the
  same file — AI-merge territory at the union), verify **at least one** `scope:"integration"` guardrail
  (on the integration / fan-in task) asserts the **UNION invariant** on that shared file. The v1 union
  re-verify runs the **integration set ONLY** (SSOT §4.3) — it does NOT re-run a colliding sibling's
  per-attempt `local` guardrails (running them at the union false-fails: fragment-readers checking
  `GUARDRAILS_STATE_FRAGMENT`, anti-tautology `tests-fail-on-current-code`, not-yet-run tasks). So a
  hunk an AI-merge silently DROPS on the shared file is re-verified at the union **only** by an
  integration-scoped guardrail; a drop catchable solely by a sibling's `local` guardrail is NOT
  re-verified there (it surfaces at the terminal gate, or not at all). If **no** `scope:"integration"`
  guardrail asserts the shared-file union invariant → emit a finding **WEAK**: recommend adding one
  (as the texttools showcase does with `components-union-verified` — assert the merged shared file
  still holds every sibling's contribution, union-safe per #125). This is an **authoring nudge**, not a
  harness bug: the integration-set-only union re-verify is an accepted v1 design (#132). (Catalogue →
  overlapping-writeScope union-guardrail; SSOT §4.3 "Accepted residual".)
  - **Duplicate-definition sub-check on a shared CODE file (#175)**: tighten the above when the shared
    overlapping-`writeScope` file is a CODE file and **both** colliding tasks could ADD a type/member
    DEFINITION to it (each writes a `class`/`record`/`interface`/`enum`/method the other doesn't). A
    3-way / AI-merge of two branches that each appended the SAME new definition to **different** regions
    of the file produces **no textual conflict marker** — git keeps both copies — so a conflict-marker
    check passes while the merged file holds a **duplicate definition** (the CS0101 that red-halted
    plan-0009's terminal gate, #175). Require the `scope:"integration"` union-guardrail to carry a
    **duplicate-definition count check** for each definition both siblings could add — count occurrences
    and fail when **>1** (`[regex]::Matches($content,'class\s+<Name>').Count -gt 1` in .NET), naming the
    AI-merge duplicate. Keep it union-safe/conditional (#165) — run it only inside the existing
    file-present gate, so it passes trivially at a union where the file hasn't landed. A conflict-marker /
    contribution-present union-guardrail with **no** duplicate-definition count on a shared code file two
    tasks both define into → **WEAK** (the silent semantic-duplicate residual the harness can only
    *attribute* at the gate, not prevent, SSOT §3.3). (Catalogue → overlapping-writeScope union-guardrail;
    `stacks/dotnet.md §19`; SSOT §3.3 / §4.3 "Accepted residual".)
- **Union guardrail ancestor staleness (#159)**: for every `scope:"integration"` union guardrail on a
  fan-in / integration task, identify each **expected-contribution token** (the string it
  `match`/`notmatch`-checks for in the shared file), and for each token identify which task(s) would
  **produce** it (the task whose action / `writeScope` writes that marker). Then verify every producing
  task is in the **ancestor set** of the integration task — there is a directed path producer → fan-in in
  the DAG. If a producing task is NOT an ancestor — a **disconnected leaf** or a **side branch** with no
  path to the fan-in — flag it **WEAK**: "Union guardrail checks for `<token>` contributed by task
  `<N>`, but task `<N>` is not an ancestor of the integration task. If task `<N>` is later removed, this
  guardrail will fail spuriously. Either add a DAG edge (`<N>` → fan-in task) to make the dependency
  explicit, or remove the `<token>` check from the union guardrail." The trap is silent: a disconnected
  producer **still runs** (the harness executes every task), so the guardrail passes **today** — but the
  integration gate now **implicitly requires** a task no edge declares it depends on, and the day that
  task is deferred/removed the gate red-halts with a confusing "shared file is missing `<token>`" that
  reads as a merge failure but is a **stale guardrail**. This is the run-time-fragility analogue of the
  #132 nudge above — there the residual is a dropped hunk on a shared file; here it is a contribution
  check whose producer fell out of the ancestor set. (Relates to #132/#125; the plan-breakdown side adds
  the matching rule on dependency-edge removal, Step 4.)
- **Unregistered module**: a task adds a module/project to a build descriptor (`.csproj`
  → `.slnx`) but no guardrail checks the DESCRIPTOR names it — a descriptor build passes
  with the project unregistered. (Stack file → build-descriptor registration.)
- **Unreferenced abstraction**: a task creates an abstraction a later task must consume,
  but no guardrail checks the consumer's project file has a `<ProjectReference>` — builds
  pass independently, so a local copy of the interface slips through. (Stack file →
  cross-module reference.)
- **Built-but-unwired component (#120 — the recurring lesson)**: the plan adds an `IFoo`/`FooImpl`
  pair (or any collaborator a production assembler must construct + inject), the component tasks
  build + unit-test it through an injected constructor seam — but **no task constructs `FooImpl` and
  injects it at the production composition root** (factory / `Program.cs` / DI / `RunCommand`), and
  **no guardrail drives the REAL assembler with the new mode active**. Every check is green while the
  feature is inert (reachable only from xUnit, which injects the seam itself); the terminal
  whole-suite gate does NOT cover this. Also flag the inverse: a wiring guardrail that **constructs +
  injects `FooImpl` itself** then asserts it works — it proves the component, never the wiring; the
  guardrail must go through the production assembler (drive the real factory + assert observable
  output, or reflect on the constructed object for the non-null collaborator with a contrast case —
  the `Factory_Wires*` shape). Missing wiring task OR a seam-injecting guardrail OR reliance on
  whole-suite green to cover wiring = BLOCKER. (Catalogue → composition-root section, `stacks/dotnet.md §10`.)
  **The inverse flag has one carve-out, and it is the one you will meet (D12): a #382 real-seam test is NOT
  this finding.** Same verb, different SLOT — #120 forbids hand-injecting into the **assembler's** slot;
  #382 requires hand-injecting the real seam into the **component-under-test's own constructor**. The tell
  is what the test calls: a composition-root guardrail drives `SchedulerFactory.Create`, a real-seam test
  never does. See the D12 note in the passing-but-blind probe below before flagging.
- **Passing-but-blind faked seam (#382) — audit the SEAM LEDGER, then re-derive it**: the review question
  is *what real path does this fake stand in for, and who proves that path?* The breakdown's answer is the
  **seam ledger** — the six-column table the Step 7.4 report prints under a bolded `Seam ledger (#382)`
  line, one row per in-process seam the tests **substitute** (plan-breakdown Step 4 rule 6 is its format
  contract; the catalogue's "drive-the-real-seam" section is the guardrail's shape). A component certified
  green solely against a fake of the seam the run exercises is a *green light over a broken wire* — the
  `CriticalityJudge` escalated 100% of the time through the real `ClaudePromptRunner`; the executor's real
  `TransientBackoff` never recorded `blocker-retried`; both green on fakes, in the same wave. **This probe
  is the only gate on any of it.** #382 ships no `validate` lint by design — the substitution lives in a
  test file the run has not written yet, so there is nothing for a lint to read — which means a row that is
  missing, mis-bucketed or mis-placed is caught here or not at all. Work the checks in order; every one of
  them is decidable from the folder, on paper, without running the plan.

  **0. Obtain the ledger — and key the absence finding on the missing HEADING, never a missing table.**
  The heading is emitted UNCONDITIONALLY: a plan that fakes nothing prints the bolded line plus
  `_No in-process seam is substituted by this breakdown's tests._` and no table. That zero-row form is a
  **claim**, not an absence — checks 6 and 7 still apply to it (1–5 are simply vacuous with no rows), and
  check 7 is the one that falsifies a false claim. An **absent
  heading** is a different animal: evidence the Step 4 analysis never ran, so no seam in this plan has been
  placed at all → **BLOCKER**, whose fix is to re-run the analysis, never to hand-write a table after the
  fact. If the report is not in this session, ask for it before concluding anything — *"not produced to
  this pass"* is an **unchecked gap for the Step 6 report**, NOT an absent heading, and reporting one as
  the other manufactures a BLOCKER out of a missing attachment. With no report at all, skip checks **1–5**
  — those read ledger cells — and still run **6 and 7**, which read the folder and are where most of the
  value is anyway.

  **1. Every `bucket` cell is exactly one of `N1` `N2` `N3` `N4` `E` `C` `U`.** Blank, a bare `N`, prose,
  or anything off that list is a finding in its own right — the cell is parseable precisely so this check
  has a target, and an unparseable bucket makes checks 2–5 unrunnable. Same pass, same row: the `proof`
  cell is a path **relative to the PLAN folder** (`tasks/<T*>/guardrails/NN-….ps1`), so a path whose task
  segment disagrees with the `T*` cell is a self-inconsistent row — one of the two is wrong; say which you
  believe and why. `production type` is `—` on any `N*` row, and `T*` on an `N*` row reads `exempt`.

  **2. REJECT an `N` classification for anything off the four-item enumeration.** N is a **closed list** —
  **N1** a clock / time source, **N2** a randomness source (an RNG, a GUID factory), **N3** an ambient
  environment reader (env vars, machine name, current directory, an OS probe), **N4** a wait primitive
  (a sleep / delay / timer). It is not a category and *"it felt like non-determinism"* is not an argument.
  A seam classified N that is not literally one of those four is **E**, **C** or **U**, it owes proof, and
  it takes the severity rubric below — an accepted N row is a permanently exempted seam, and nothing
  downstream will ever ask about it again.
  > **The N4 trap is the highest-yield row on the table: fake the WAIT, never the WAITER. If the
  > substitute contains a DECISION, it is not N4.** Substituting the *sleep* so a backoff test finishes in
  > milliseconds is N4 and exempt. Substituting the **policy object** that decides *whether* to retry — and
  > that owes a recorded `blocker-retried` decision — is **C**, and it owes proof. Concretely:
  > `RetryLoop → IDelay` is **N4**; `RetryLoop → ITransientBackoff` is **C**. Read the substitute's own
  > surface, not its name: any branch, threshold, budget, classification or recorded decision inside the
  > thing being faked disqualifies N4. This exact conflation shipped a silently-swallowed transient on a
  > fully green wave — the class-(b) resolution that never recorded the decision its design required,
  > because no test drove the executor's real backoff.

  **3. Recompute T\* for every E and C row from the DAG — do not accept the cell.** T\* is the **earliest
  task at which BOTH the component's production type and the seam's production type exist**, and a type
  exists at a task when that task's `writeScope` (or an ancestor's) contains the file declaring it. So T\*
  is computable by you, without running anything — that computability is the whole reason the rule could
  replace *"where feasible"*, and skipping the recomputation gives the column back to the author who wrote
  it. A proof placed **later** than T\* is a finding **even when the proof exists and passes**: it surfaces
  the bug in a task whose `writeScope` cannot fix it, which is the `needsHuman` this doctrine exists to
  remove. The report owes a line naming T\* and why the proof could not live there — an **unnamed** late
  placement is the finding; a named one is a decision you can argue with on its merits.

  **4. An E row may NEVER invoke the construction bound (D11).** The #120(b) reflection-plus-contrast
  degradation is available to bucket **C** only. What sits beneath an **E** seam *is* a process / network /
  disk boundary, and faking that is the one substitution this rule has always permitted — so constructing a
  real E adapter cannot force a second real level. *"I could not construct it"* on an E row is therefore a
  **review finding, not a legitimate degradation**: the answer is the real adapter over a stub binary, a
  fake `HttpMessageHandler`, or a temp directory. This is the escape hatch an author reaches for first, so
  check it explicitly rather than reading past it. A **C** row that does degrade owes the **constructor
  chain that forced it**, named in the report; an unnamed degradation is a finding as well.

  **5. U rows name a receiving TASK that exists, and the terminal sink is rarely it.** A U row's proof is
  RELOCATED, not waived. `T*` is a task folder name — under Step 9 waves, a receiving **wave** folder is
  legal when that wave is not broken down yet, and that is the only non-task name the column permits — and
  `proof` reads `deferred to T*, named`. A U row pointing at the terminal sink is legitimate **only** when
  the production type genuinely first exists there; otherwise the row is mis-placed and the finding is the
  **placement**, not the bucket. Check the named task actually exists in the DAG: a U row naming a folder
  nothing matches defers the proof to nowhere, which reads on the page like compliance.

  **6. The terminal proof is a JOIN-CHECK — make it name a defect that SURVIVES the upstream proofs.**
  For the #120 wiring task and for every `<plan>/guardrails/` guardrail, read the `# catches:` and ask
  whether the defect it names could still occur **with every upstream real-seam proof passing**. *"The
  factory never hands the judge to the scheduler"* survives — that is assembly, and assembly is what the
  join-check owns. *"This seam is exercised for the first time here"* does not survive: it means a ledger
  row is mis-placed, and the fix is **upstream**, never a wider `writeScope` here. A join-check that can
  name no surviving defect is **redundant** — say so and propose deleting it, rather than leaving a gate
  that certifies nothing. Flag too the same row's proof emitted **twice**, once at T\* and again in the
  sink: the duplicate is the concentration this rule exists to remove.

  **7. Re-derive the ledger from the folder — it cannot report a fake nobody declared.** This is the check
  that makes the others worth running, because a declaration-only audit has its floor at the honesty of
  the author it grades. Walk every `author-tests-*` task's `action.prompt.md` and its paired implement
  task: wherever the prompt directs a fake / stub / mock / test double of an **in-process** collaborator the
  production run resolves (an `IPromptRunner`, the executor, the scheduler, a factory, a policy object),
  there must be a **row**. A substitution the tests make with no row is the shipped bug's own shape, and the
  ledger is silent on it by construction. Conversely, **process seams are NOT rows** — a child process, a
  CLI, a socket, an HTTP endpoint, a database, the filesystem — and flagging one is a false positive that
  teaches the next reader to skim the table, which costs more than the row was worth.

  **Then audit the archetype's SHAPE wherever a proof does exist** (the catalogue's FORBIDDEN list for
  "drive-the-real-seam" is what this mirrors): the proof is a **test** — rung 1, with **no rung-3 source-grep
  form** available, since a regex proving the test file mentions `new ClaudePromptRunner(` certifies
  vocabulary and is satisfied by a commented-out line; it asserts an **effect only the production
  implementation emits** (the stream-log FILE appears on disk, the journal holds a `blocker-retried`
  DECISION, the verdict's `Source` is not the catch-and-safe-default) — ***"the collaborator was called" is
  not an assertion***, and a recording double / call count / `Verify` **is** the passing-but-blind shape
  wearing a real-seam name; it is `scope: "local"` with the key omitted, because *"this component works
  through the real seam"* cannot be true before its own task's action has run — tagging it
  `scope: "integration"` because "it drives the real thing" is the #250 mistake, measured live at two
  unrelated parallel siblings rolled back; and its RED half is real (#155 — the red must **COMPILE** and
  fail, so the test-author task also writes whatever stub the real-seam test needs to compile).
  **Probe B operator 20 (§2b) is the mechanical half of this audit** and reading is the weak half: a test
  that satisfies a `…RealSeam…` filter while constructing the fake is textually indistinguishable from the
  real thing at this altitude.

  > **Do NOT flag a correct real-seam test as a #120 violation (D12).** The #120 probe above forbids a
  > guardrail that *constructs `FooImpl` itself and injects it*; this probe **requires** exactly that verb —
  > in a **different slot**. #120 forbids injecting the collaborator into the **assembler's** slot, which
  > bypasses the production assembler so the *wiring* is never proven. #382 requires injecting the real seam
  > into the **component-under-test's own constructor**, which proves the component through its collaborator
  > and claims nothing about the assembler. Operationally: a real-seam test never calls
  > `SchedulerFactory.Create`; a composition-root test never hand-injects; **if one test does both, it is
  > two tests**, and *that* is the finding. Flagging the mandated shape as the forbidden one is a false
  > BLOCKER against the doctrine's own output — the most expensive mistake this probe can make, because it
  > tells an author to delete the proof.

  **Severity — one rubric for every finding that leaves a SEAM UNPROVEN (checks 2–5 and 7):** **BLOCKER**
  when the un-proven seam is a **composition-root / production path**; **WEAK** when a **thin terminal
  join-check exists** but the per-component real-seam proof is missing (the proof is deferred to one sink,
  not absent). Name the **concrete seam** and the **task that should carry the proof** — T\*, computed, not
  the terminal sink, which re-creates the #378 over-scope. Two findings sit OUTSIDE that rubric and must
  not be inflated to fit it: a **check-1 malformed or self-inconsistent row** takes the severity of what it
  conceals — WEAK while the intended row is still readable, BLOCKER when the bucket cannot be determined at
  all (an undeterminable bucket is an unaudited seam); and a **check-6 redundant join-check** is WEAK (it
  certifies nothing, but it also blocks nothing) *unless* the only defect it can name is "first exercise
  here", which is a mis-placed row and takes the rubric above. Shares a root with the Over-scoped-task
  probe (§3): that sink is over-scoped *because* it concentrates this deferred proof.
  (Catalogue → "drive-the-real-seam"; `stacks/dotnet.md §10e`.)

  **The #378 boundary — inherit this rule, do not renegotiate it.** #378 owns the **size and shape of a
  task**: it reads `writeScope` cardinality, `action.maxTurns` and `dependsOn` fan-in, its mechanism is
  **GR2042**, and its verdict is *"this task is too big."* #382 owns the **placement of proof**: it reads
  which seam a test substitutes and where the real-seam proof lives, its mechanism is the ledger plus the
  archetype audited here, and its verdict is *"this proof is in the wrong task."* Therefore — **#382 NEVER
  adds a rule keyed on `writeScope`, `action.maxTurns` or `dependsOn`** (those three fields are GR2042's,
  exclusively) — and **#378 NEVER adds a rule about what a guardrail PROVES.** (Check 3 READS `writeScope`
  to locate where a type is declared; that is a lookup, not a rule keyed on the field. What #382 may never
  do is rule on how BIG a task is.) When both fire on one task, report both, each from its own evidence;
  never let one stand in for the other, and never grow a third
  half-overlapping check in the gap between them. Where they meet: told *"this task is over-scoped"*, the
  reflex is to chop the `writeScope`, which for a fan-in sink yields N small tasks that still contain the
  first exercise of every real path — the concentration survives the split. **Relocating the proof to T\*
  is the fix; narrowing `writeScope` alone is not.**
- **Wrong-implementation swap (#158)**: the next failure past #120 — given the dispatch IS wired,
  is the **right concrete type paired with the right mode**? For a dispatch / wiring task that routes
  **≥2 enum (or discriminated) values to ≥2 concrete types** AND whose dispatch tests use
  **seam-injection** (`RecordingImporter` / `FakeHandler` patterns that replace the real impl via DI and
  assert only that *an* importer was called), verify a **per-pairing proximity check** exists — one
  guardrail per pairing asserting `<EnumValue>` sits within a bounded window (`[\s\S]{0,300}`,
  multiline-dotall, both orders) of `<ConcreteType>` in the dispatch file. The swap to hunt: an agent
  routes Mode B → the wrong importer and Mode C → the other; the **build passes** (either type satisfies
  the interface in either branch), the **seam-injected dispatch tests pass** (they never check which
  concrete type was registered), and a **bare keyword check** that all enum values AND all type names
  appear *somewhere* passes too (all present regardless of pairing) — the feature ships inverted on a
  fully green suite. Flag **WEAK if the per-pairing check is missing**; **BLOCKER if the only concrete
  check is `tests-pass` with seam-injection tests** (nothing binds enum to type, so a swap is fully
  invisible). **Do NOT flag** when the dispatch tests already assert the concrete TYPE NAME
  (`Assert.IsType<TcApiLocalImporter>` on the resolved object) — the test catches the swap and the
  proximity check is redundant (the catalogue's decision gate). Distinct from #120 (built-but-unwired):
  there nothing wires the impl at all; here it is wired but possibly to the wrong mode. (Catalogue →
  "Dispatch / factory wiring"; `stacks/dotnet.md §10d`; relates to #120.)
- **Vacuous `writeScope`**: a task declares `writeScope: ["**"]`, a bare top-level dir, or
  any over-broad surface that owns everything — the write-scope check (SSOT §3.4) then
  discriminates nothing and is theater (`validate` warns GR2020). Flag every `**`/over-broad
  scope as WEAK and propose a **real surface** (the specific directories/files it writes) — or,
  if the task genuinely writes NOTHING to the repo, `"writeScope": []`. Do **NOT** propose omitting
  the field: since #389, omission is a GR2041 error, so "omit it" is never the fix (`[]` is).
- **Tests not excluded from an implementation scope**: an implementation task with an
  upstream test-author task whose own `writeScope` covers (or fails to exclude) those test
  files — the deterministic "implementation may not write the tests" boundary is open, so the
  implementation can edit the tests to force a tests-pass guardrail green. The implementation
  task's `writeScope` must EXCLUDE every test file the test-author task owns. (BLOCKER — it is
  the TDD test-protection gate.)
- **ABSENT `writeScope` — always a BLOCKER (#389)**: `writeScope` is now REQUIRED on every task,
  so a task that OMITS the field entirely is a **BLOCKER** (and a hard `validate` error, **GR2041**) —
  drop the old "genuinely repo-wide ⇒ omission is OK" exemption entirely: a terminal whole-suite gate
  or a sweeping cross-cutting change must declare its broad surface EXPLICITLY (name the directories),
  never omit. An absent scope gets NO write-scope check, so an out-of-scope escape (including an
  implementation editing the tests) goes uncaught. Fix: name the real surface, or `"writeScope": []`
  if the task writes nothing to the repo.
  **CRITICAL — `writeScope: []` is a FIRST-CLASS VALID declaration, NOT a finding.** An empty scope is
  the correct, deliberate "writes nothing to the repo" form for a configure-a-database task, a
  verification/read-only check, or a state-only task (its only output is a `GUARDRAILS_STATE_OUT`
  fragment, which is not a repo write). **Do NOT flag `[]`** — it is not "missing" and not "vacuous";
  flag ONLY a TRULY ABSENT field. (Sanity-check that a `[]`-declaring task genuinely writes nothing —
  a task that DOES write to the repo but declares `[]` will fail its own write-scope check at run time,
  which is a different, correctly-caught error.)
- **Four-folder gap — missing/empty/tautological plan-level or task-level folder (deliverable 9)**:
  the terminal integration-gate TASK (`integrationGate: true`) is **RETIRED** — a plan still
  declaring it gets a **hard validation error (GR2029)**, no coexistence window. The replacement
  is four first-class folders at fixed locations (SSOT §1/§3.3): plan-level `<plan>/preflights/`
  ("Full Flight Checks", evaluated once BEFORE the DAG against the starting repo) and
  `<plan>/guardrails/` ("Terminal Gate", evaluated once on the merged HEAD AFTER the DAG drains
  green) — both siblings of `tasks/`, `guardrails.json`, `state/` at the **plan root** — plus
  task-level `tasks/<id>/preflights/` (JIT dependency-delivery, a sibling of the existing
  `tasks/<id>/guardrails/`, evaluated per task BEFORE its attempt loop). Probe each folder the
  plan's shape requires and treat a required-but-missing folder/check as a **BLOCKER**:
  - **Missing or empty terminal folder on a multi-leaf/fan-in plan** — `<plan>/guardrails/`
    absent, or present with zero guardrail files, on a plan with ≥2 leaf tasks or any fan-in task
    → **BLOCKER**. `validate` already enforces this in worktree mode (GR2028); call it out with
    the concrete leaf/fan-in shape that triggers the obligation.
  - **Tautological terminal folder (the re-homed GR2018 obligation)** — `<plan>/guardrails/`
    present and non-empty but every file is a no-op (`exit 0`, a bare `echo`, a comment that only
    NAMES a build command, a prompt-judge with nothing to verify) rather than **≥1 real
    integration-set re-run** — a genuine whole-repo build/test/suite invocation (`dotnet test`,
    `dotnet build`, `npm test`, `pytest`, `cargo test`, …) or a union invariant → **BLOCKER**. A
    folder that merely EXISTS or merely contains a file certifies nothing; GR2018's content teeth
    survive the move from task to folder — "non-empty" is not the bar, "re-runs the integration
    set" is.
  - **A lingering `integrationGate: true` task** — flag it and point the author at the
    `<plan>/guardrails/` folder replacement; do not accept a plan whose terminal check still
    depends on the retired sink kind. (The one narrow exception: a plan's own committed,
    documented bootstrap exemption for a harness version that predates the loader — name it
    explicitly if the plan claims one, don't accept it silently.)
  - **Missing plan-level preflight on a brownfield plan** — the existing-tests-green positive
    baseline now lives as a `<plan>/preflights/` **positive** check (not a no-op ROOT task); its
    absence on a brownfield plan is the same WEAK→BLOCKER call as the baseline probe above (§2),
    just relocated to the folder.
  - **Missing task-level preflight where a `dependsOn` edge delivers a JIT dependency** — a
    consumer task that depends on a producer for a type/route/symbol/artifact it needs inside its
    OWN segment worktree at `taskBase`, with no `tasks/<id>/preflights/` check confirming the
    producer's contribution actually landed before the attempt loop spends a turn building against
    possibly-absent bytes → **WEAK** (flag **BLOCKER** when the delivery is genuinely uncertain —
    e.g. the producer is a same-wave sibling rather than a settled ancestor).
  - `scope: "integration"` itself is **UNCHANGED** — it remains the per-union tag driving the
    §4.3 per-union re-verify; only the *terminal-sink task* is retired. Do not flag a
    `scope: "integration"` guardrail elsewhere in the DAG as if it were the retired mechanism.
- **Live-probe used where a flake-free check would do — ADVISORY WARN, never a BLOCK (deliverable
  9)**: a check placed in ANY of the four folders that reaches outside the committed bytes under
  review — a network call, a polling loop, a spawned daemon/live service, anything whose outcome
  depends on more than the repo's own build/test tooling — trades determinism for **flake risk**.
  This is **authoring guidance the review emits as a WARN, never a BLOCKER**; the harness itself
  enforces NOTHING here — `guardrails validate`/`run` neither warns nor blocks on a live probe —
  so do not escalate this past a WARN no matter how bad the probe looks.
  - **Plan-level (`<plan>/preflights/` / `<plan>/guardrails/`)**: a full `dotnet test` /
    `dotnet build` over the committed bytes (the starting repo for preflights, the merged HEAD for
    guardrails) is **FINE** — that IS the canonical Full Flight Check / Terminal Gate shape, not a
    live probe. WARN only on a network/poll/daemon/live-service call there — a flake halts the
    **entire run** (plan-level has the maximal blast radius).
  - **Task-level (`tasks/<id>/preflights/`, and `tasks/<id>/guardrails/` by the same logic)**:
    **prefer** a byte/exit check (file exists, grep, a build/test scoped to the task's segment);
    WARN on a network/poll probe here too — smaller blast radius than plan-level (blocks one cone,
    not the whole run) but still a flake risk this early in the attempt loop.
  - The property under review is **FLAKE-FREEDOM, not process-count** — a `dotnet test` is a
    process start and stays fine; the WARN targets non-repeatable outcomes (network, timing,
    external services), not process spawning itself.

> **The three probes immediately below are SUPERSEDED by the four-folder model above** — they
> describe the RETIRED `integrationGate: true` task mechanism (now a hard validation error,
> GR2029) and apply only when reviewing a pre-migration plan or a named bootstrap exemption. For a
> plan authored under the four-folder model, use the terminal-folder probe above instead of the
> "exactly one `integrationGate: true` task" framing below. `scope: "integration"` itself did NOT
> change — only the terminal-sink TASK kind was retired.
- **Integration gate missing or empty**: in a plan with ≥2 leaf tasks or any fan-in, confirm
  **exactly one** task declares `integrationGate: true` (the terminal whole-repo sink, SSOT
  §3.3) and that sink carries **at least one** `scope: "integration"` guardrail — an empty gate
  verifies nothing. Zero gates on a multi-leaf/fan-in plan, two-or-more gates, or a gate with no
  integration-scoped guardrail is a BLOCKER (`validate` enforces GR2017/GR2018, but call it out
  with the missing/empty sink named).
- **Full build / whole suite marked `scope: "integration"` on the terminal gate (#165 — BLOCKER)**:
  a **whole-repo build** (`dotnet build <solution>`) or **full test suite** (`dotnet test` with no
  filter) guardrail on the terminal integration gate that declares `scope: "integration"` is the
  **#125 terminal-postcondition anti-pattern**. The integration set re-runs at EVERY union point
  (SSOT §4.3), and a full build/suite is a **terminal postcondition**, not a union-safe invariant: at
  an intermediate union in a TDD plan the merged bytes contain test files referencing types whose
  implementation task has not run yet, so the build/suite FAILS there and the harness rolls the whole
  wave back — even though every per-task guardrail passed (decision test: *"would this pass on a
  partial merge with a downstream task unsettled?"* — a full build/suite answers **no**). Flag it
  **BLOCKER** (it red-halts correct intermediate unions). Fix: drop the `scope` key (make the full
  build/suite **LOCAL** — it then runs only at the terminal gate's own attempt, the correct moment),
  and ensure the gate still carries a separate `scope: "integration"` **union-safe** guardrail to
  satisfy GR2018 (next check). (Catalogue → "A `scope:"integration"` guardrail MUST be UNION-SAFE";
  SSOT §4.3/§5.3.)
- **Terminal gate's integration-scoped guardrail not union-safe / missing (#165, GR2018 — BLOCKER)**:
  the gate sink must carry **≥1** `scope: "integration"` guardrail (GR2018), and that guardrail must
  be a **union-safe CONDITIONAL invariant** — conflict-marker-free / non-empty, or "IF contribution X
  is present, verify it's real" (gate-then-verify), so it passes trivially before a contributing task
  has run. The full build/suite does NOT qualify (previous check). If the gate's ONLY
  integration-scoped guardrail is the build/suite, or its union guardrail is written **unconditionally**
  (`if ($content -notmatch "<token>") { exit 1 }` — requires a contribution that a partial merge may
  not hold yet), flag **BLOCKER**: it either leaves the gate with no real union coverage or red-halts
  a healthy partial merge. Fix: author a conditional union invariant (the `parallel-hello`
  `01-whole-repo-greeting` template; the overlapping-writeScope union-guardrail, #132). (Catalogue →
  union-safe CONDITIONAL form; SSOT §4.3.)
- **Over-scoped task (#111 — is this too coarse to land in one session / retry cheaply?)**: a task
  trips the plan-breakdown Step 2 split-trigger — (a) it bundles multiple distinct deliverables
  ("do X **and** Y **and** Z"); (b) it has a wide blast radius (deletes ≥3 source files, or
  touches ≳10 files / test references in one action); (c) it maps 1:1 to a design milestone / phase;
  or (d) a single failed-guardrail retry re-runs an hour of work (a multi-deletion, a 100+-ref
  re-baseline). An over-scoped task thrashes at run time — every guardrail miss re-runs the whole
  oversized action — and is the most likely `needs-human` in a run. Flag it WEAK (its retry is
  expensive; it is mis-sized) and propose the split: name the deliverables it bundles and the
  smaller tasks they should become, each with its test re-baseline scoped to that piece. This is the
  inverse of the missing-insertion check (§4): there a deliverable maps to NO task; here ONE task
  carries too many deliverables.
  **Objective teeth — read the emitted JSON, don't judge the description (#378).** The (a)–(d) triggers
  above are description-level and an author/reviewer can rationalize past them for the **fan-in-sink /
  composition-root-wiring** archetype ("it's just wiring"). So ALSO read the emitted `task.json`:
  **`writeScope` cardinality + `action.maxTurns` + `dependsOn` fan-in**. Flag **BLOCKER** (a certain
  thrash, not a mere risk) on the co-occurring fingerprint — **`maxTurns` near the ceiling AND
  `writeScope` ≥ ~4**, OR **`dependsOn` fan-in ≥ ~5 with a multi-file `writeScope`** — naming the
  proposed split: **one task per collaborator wiring** (factory registration, scheduler call-site, CLI
  plumbing), with the turn-expensive composition-root proof (drive the REAL factory, #120) isolated to a
  thin sink. This is the same signal `guardrails validate` emits as a **GR2042** WARN: **cross-check it
  and RESOLVE it** (propose the concrete split), don't merely re-report the warning. Distinct from the
  passing-but-blind probe (#382) below — that one shares this root (the sink is over-scoped *because* it
  concentrates deferred integration proof) but asks a different question (which real path is unproven).
<!-- BEGIN ADDED PROBES #74/#75/#76/#96 -->
- **Keyword-not-structural for a METHOD CALL (#76)**: a "file calls `B.Method()`" guardrail that greps a
  **bare method name** — `RunAsync\s*\(` — passes on a comment (`// RunAsync(scope)`), a **local stub**
  of the same name (`private void RunAsync(...)`), or any unrelated same-named method, none of which
  invoke the real library method. The call-site sibling of the keyword-not-structural type/member trap.
  Fix: require **two sequential checks** — the **type** is referenced (`MigrationRunner`, rules out a
  local stub) AND the **dotted call** (`\.RunAsync\s*\(`, rules out comments + standalone definitions).
  Apply to any "task A must call `B.Method()`" on a specific type in another project. (Catalogue →
  method-call anchoring; `stacks/dotnet.md §15`.) BLOCKER on a wiring guardrail — a local/commented stub
  reads as wired.
- **Library bypasses its injected interface (#74)**: a task extracts a library that must write
  **through** an injected `IInterface`; it is registered + builds + tests pass — but **no guardrail checks
  the library's internals don't call the CONCRETE method directly**, bypassing the abstraction. Tell: an
  "extract … must go through `IInterface`" / "must NOT call `X` directly" task with only registration +
  build + tests-pass guardrails and no forbidden-direct-call scan of the library folder. Fix: a
  comment-stripped (#97/#98), dot-anchored (#76) forbidden-call scan of the **library project's `.cs`
  only** (exclude `bin`/`obj`). Also flag the inverse mistake: a **bare-name** bypass grep with no
  comment-strip — it *false-REDs* a correct library on a comment (whack-a-mole to `needs-human`).
  (Catalogue → no-direct-bypass; `stacks/dotnet.md §16`.) BLOCKER — a bypass ships green.
- **Enumerated behaviors unverified (#75)**: a test-author task whose action prompt lists **≥3 named
  behaviors** to encode but whose guardrails are only `tests-exist` + `tests-fail-on-current-code` —
  neither verifies the named behaviors are present, so **one** trivially-failing stub satisfies both
  while behaviors 2–N are never encoded (the coverage-gap anti-pattern, made concrete). Fix: a
  `covers-key-behaviors` check for **2–3 distinctive terms** (domain type / enum / method name — never
  generic words) from the list, **scoped to the one test file**; name it a **lower bound** (a term
  present ≠ the behavior asserted) and report which enumerated behaviors went unchecked. (Catalogue →
  covers-key-behaviors; `stacks/dotnet.md §17`.) WEAK→BLOCKER depending on how load-bearing the
  unverified behaviors are (it is the coverage-gap probe, sharpened for enumerated lists).
- **Name-convention seam unverified (#96)**: task A produces artifacts a consumer (task B / a runtime
  component) resolves by a **derived or mapped name** (url→embedded resource, step id→filename, key→file,
  route→handler, message-type→schema) — and `file-exists`/`file-contains` on A plus content checks on B
  both pass while the **naming contract is never exercised**. B derives a name A never produced (case /
  separator / single special-case drift) and **404s/silently-falls-back at runtime** on a 100%-green
  suite — invisible until the first real run. Tell: a derived-name consumer (fetch-by-name,
  embedded-resource/reflection lookup, convention file-map, route resolution) with only per-side
  file-exists/content checks and **no end-to-end lookup over the whole set**. Fix: a **consumer-driven
  integration guardrail** on a **both-sides-present** task that **parses the consumer's real map** (never
  a hard-coded contract copy), drives the lookup for **every** item, and asserts **200 + a per-item
  marker** (not a fallback body); `scope:"integration"` and **union-safe** (#125 — "every present
  artifact resolves"). Also flag the weak forms: a **sampled** check (not every item — the drift hides in
  the one special case) and a **hard-coded name list** in the test (a copy hides a consumer-side drift).
  (Catalogue → name-convention seam; `stacks/dotnet.md §18`.) BLOCKER on a UI/transport/convention-heavy
  plan — the failure is invisible to the whole suite.
<!-- END ADDED PROBES #74/#75/#76/#96 -->
<!-- BEGIN ADDED PROBES #176 — transitive compilation dependency · negative-assertion gap -->
- **Transitive compilation dependency — a test-author ancestor references a non-ancestor's type (#176)**:
  the §3 "missing edge" check applied at the IMPLICIT COMPILATION level, not just the direct-artifact
  level. For **each** task **B** whose verification step runs `dotnet build` / `dotnet test` (it compiles a
  test project): identify B's ancestor **test-author** tasks (ancestors that write `.cs` test files — those
  files are in the test project B compiles). For each such test file, consider the types its **action
  prompt's scenarios / deliverables ALLOW it to reference** (the enumerated scenarios, the named
  collaborators — not every type imaginable). If any of those types is **PRODUCED by an implementation task
  C that is NOT in B's ancestor set**, flag the **missing edge B←C**. The decision rule, stated verbatim:
  *"Task B's verification compiles the output of ancestor test-author task A. A's prompt allows referencing
  types produced by task C. C is not in B's ancestor set → missing edge B←C (add `C` to B's `dependsOn`, or
  the agent will be trapped — it can't fix a compile error in a file outside its writeScope, and may
  compensate by redefining the type in its own scope → a duplicate-definition merge collision)."* This is
  the exact failure chain of plan-0009: task 09's `dotnet test --filter` compiled the test project holding
  task 08's `MigrateDispatchTests.cs`, which referenced `CommanderRestImporter` produced by task 07 — and 07
  was NOT in 09's ancestor set, so 09 hit an unfixable compile error and redefined the class in its own
  writeScope (`Launcher.cs`), colliding with 07's copy at the AI-merge (CS0101, #175/#174). Severity: **WEAK**
  when the trap merely risks a wasted retry / `needsHuman`; **BLOCKER** when the test file's scenarios
  plainly reference the non-ancestor type (the compile failure is certain). Fix: add the producing
  implementation task to B's `dependsOn` so its output is present in B's working tree. (Distinct from the
  direct-artifact missing-edge check, §3 — there B reads C's FILE; here B COMPILES a file that references
  C's TYPE.) (plan-breakdown Step 3 adds the matching authoring rule.)
- **Negative-assertion gap — a prompt excludes a scenario but no guardrail forbids it (#176)**: when a
  task's action prompt **EXPLICITLY EXCLUDES** a scenario/keyword ("Mode C / `CommanderRest` is
  wizard-blocked — do NOT include it in the dispatch tests"; "the importer must NOT call `X` directly"),
  the corresponding guardrail must carry a **NEGATIVE assertion** verifying the excluded keyword is
  **ABSENT** — `if ($content -match "CommanderRest") { Write-Output "…"; exit 1 }` (fail-on-present).
  Without it, the agent is free to include the removed scenario **undetected**: the positive
  `covers-key-behaviors` only checks PRESENCE of the kept scenarios, so a stray excluded scenario sails
  through (exactly what slipped past plan-0009's task 08 and fed the #176 compile trap). For every
  test-author / implementation task whose prompt names an excluded scenario, confirm a fail-on-present
  guardrail exists for that keyword; if absent, flag **WEAK** (the exclusion is unenforced) — **BLOCKER**
  when the excluded scenario is the very thing that traps a downstream compile (the #176 case). Fix: add a
  fail-on-present negative-assertion guardrail (catalogue → negative assertion; `stacks/dotnet.md §20`),
  paired with the positive `covers-key-behaviors`. Note it is **correct** that `guardrails validate`'s
  GR2026 stays SILENT on this guardrail's keyword (post-#177 GR2026 flags only POSITIVE require-present
  coverage tokens, SSOT §4.4) — a GR2026 warning on a negative assertion would be the #177 false positive,
  not a signal to remove the guardrail. (plan-breakdown Step 4 adds the matching authoring rule.)
<!-- END ADDED PROBES #176 -->
<!-- BEGIN ADDED PROBE #221 — prose-only prohibition with no structural backing -->
- **Prose-only prohibition, no structural backing (#221)**: for every explicit **"do NOT …"** statement
  in a task's action prompt ("do NOT wrap this in a retry loop," "do NOT weaken this assertion to
  tolerate fewer than N arrivals," "do NOT use approach X"), verify a guardrail exists that would catch
  the forbidden shortcut. A prohibition backed by nothing but prose is free for an adversarial — or
  merely lazy — implementer to ignore; this probe mirrors exactly how the pattern was found (a real
  dogfood, not a synthesized example): a flaky-concurrency-test hardening plan forbade weakening
  `Assert.Equal(3, …)` to `Assert.True(… >= 2)` and forbade a retry-until-pass wrapper, and neither
  prohibition had a backing guardrail — both are the cheapest wrong implementation for their task.
  **Check the forbidden behavior against structural checkability** (a regex/count/shape test on the file
  the task modifies) before flagging: if it IS checkable and no guardrail enforces it, that's the
  finding; if it genuinely is NOT checkable (a judgment call with no mechanical proxy), confirm the
  breakdown report says so explicitly rather than treating the gap as covered. **Escalate to BLOCKER when
  the task's OTHER guardrail is empirical/statistical** (a "run N times, assert it always passes" flake
  check) — the forbidden shortcut can make THAT guardrail easier to pass, not harder (a weakened
  assertion tolerates the very race the empirical check exists to catch), so the guardrail suite as a
  whole rewards the shortcut instead of merely missing it. WEAK when the guardrail suite is otherwise
  deterministic and simply silent on the prohibition. Fix: add the missing structural guardrail (a
  negative assertion, #176, for an excluded keyword/scenario; a regex-lock on load-bearing text
  surviving verbatim, or a call-count + forbidden-construct scan for a banned approach/shape) — or, if
  genuinely not checkable, require the breakdown report to name it as an accepted, unguarded judgment
  call. (Catalogue → "Prose-only prohibition, no structural backing"; plan-breakdown Step 6 adds the
  matching authoring rule.)
<!-- END ADDED PROBE #221 -->
<!-- BEGIN ADDED PROBE #203/#204 — stale line-number pointer / unhedged sibling-architecture claim -->
- **Stale line-number pointer / unhedged architecture claim about a not-yet-run sibling (#203/#204)**:
  for every task whose action prompt references code belonging to **another task in the same plan**,
  check the DAG wave placement first — is the referenced task in an **earlier wave** than this one (it
  will run and commit its own edits before this task's attempt starts)? If so, scan this task's prompt
  for two violations:
  1. **A line number pointing into a file the earlier-wave task will modify** (`~231-253`, "around line
     N", "lines X-Y") — by construction the earlier task's edits land before this task runs, so the
     pointer is stale on arrival, not merely at risk of going stale. Flag **WEAK**, **BLOCKER** when the
     cited file is exactly the earlier task's own `writeScope` (the collision is certain, not merely
     plausible) — name the file and the earlier task.
  2. **An unhedged "here's how it currently works" claim about the earlier task's implementation**
     ("this REPLACES/extends the same `<X>` path", "task N built `<Y>` this way") stated as settled fact
     rather than a caveated hypothesis. Flag **WEAK**; **BLOCKER** when the plan gives no other reason to
     believe the claim holds (nothing else in the plan constrains HOW the earlier task must implement its
     deliverable, so the claim is a guess dressed as fact).
  This is the exact plan-0009-lineage failure (issue #202): a later-wave task's prompt cited
  `Scheduler.cs ~231-253` and asserted a sibling task "extends the same Scheduler path" — the sibling
  actually built a standalone `PlanPreflightPhase.cs`, and the line numbers had shifted by the time the
  later task ran, costing 60-170+ turns of pure re-discovery (one attempt fully exhausted its turn
  budget touching zero of its own deliverables). Fix: replace the line number with a durable,
  structure-stable marker (a distinctive comment string, a method/class/type name, a grep-able symbol),
  and rephrase the architecture claim as a checkable hypothesis — "this reflects the plan-authoring-time
  state, before task N had actually run — verify it's still accurate before assuming the same shape
  applies here" (catalogue → stale line-number pointer / unhedged architecture claim; SKILL.md Step 6).
  **Cross-check `maxTurns`**: a task that trips this probe usually also needs the `maxTurns: 75` bump for
  the fourth turn-expensive archetype (SKILL.md Step 4a / catalogue "maxTurns budgeting (#94)") — if the
  prompt text is hedged/de-staled but the task is still left at the default budget (or vice versa), flag
  it as a **half-applied fix**: the two are companions for the same re-discovery risk, not independent
  bullets, so fixing only one leaves the other half of the risk unaddressed.
<!-- END ADDED PROBE #203/#204 -->
<!-- BEGIN ADDED PROBE #193 — orphaned pre-existing golden swept in by a broad tests-pass filter -->
- **Orphaned golden swept in by a broad `tests-pass` `--filter` (#193)**: the **runtime** analogue of
  the #176 transitive-compilation probe. There the trap is compile-time (a test compiles a type a
  non-ancestor produces); here it is a **runtime assertion** — a code-change task's `tests-pass`
  guardrail uses a broad **name-substring** `--filter` (`--filter "FullyQualifiedName~Renderer"`,
  `~Serializer`, `~Golden`, `~Snapshot`, a bare namespace substring) that, beyond the task's own
  new tests, **also matches PRE-EXISTING tests** the task did not author. For **each** code-change
  task whose `tests-pass` guardrail carries such a broad `--filter`, enumerate the pre-existing tests
  the substring matches that are **NOT** authored by an ancestor test-author task (grep the repo's
  existing test tree for the filter substring; the matches that already exist at plan start are the
  orphans). For each orphan, ask: *does this task's change plausibly alter a **pinned literal /
  golden file / snapshot / approved output** that orphan test asserts against?* (a task that touches a
  renderer, a hash/serializer, a formatter, a message schema, any cross-cutting OUTPUT shape is the
  high-risk case — it shifts bytes every golden downstream of it pins). If **yes** AND that test +
  its golden fixture are **outside the task's `writeScope`** AND **no other task owns** re-baselining
  them → **BLOCKER**: the task is required to make a pre-existing test pass whose pinned golden its
  own change invalidates, and it **cannot edit** the golden (write-scope check red-halts the fix) —
  every attempt fails on an orphan it can't own and dead-ends at `needsHuman`. This is the runtime
  sibling of #176's "can't fix a compile error outside its writeScope" trap (there the agent
  redefines the type and collides; here it simply cannot converge). Fix, in order of preference:
  (a) **narrow the `--filter`** to the task's own tests (a class-name / trait filter, not a broad
  substring) so the orphan is never swept in; or (b) **widen the task's `writeScope`** to OWN the
  golden fixture + its pinned test, so the re-baseline is in-scope; or (c) add a **dedicated
  re-baseline task** (ancestor of this one) that owns and regenerates the affected golden, with this
  task depending on it. Severity: **BLOCKER** when the change certainly shifts the pinned output;
  **WEAK** when the collision is plausible but not certain (the filter is broad and an orphan pins a
  literal the change *might* touch). (Catalogue → orphaned-golden / broad-filter trap; relates to
  #176 transitive-compilation and the write-scope test-protection gate. plan-breakdown Step 4 adds
  the matching cross-cutting-output re-baseline authoring rule.)
<!-- END ADDED PROBE #193 -->
<!-- BEGIN ADDED PROBE #248 — pattern-matching guardrail not verified against real output -->
- **Pattern-matching guardrail not verified against real output (#248)**: any guardrail that
  pattern-matches/regexes against a **specific tool's printed console output** — a
  `Select-String`/regex check on `dotnet test`'s summary line, a grep on a build tool's error
  format, anything parsing text a tool PRINTS rather than just checking its exit code or a file
  it wrote — carries an unstated assumption about that tool's exact output format. Reading the
  script and judging whether the regex "looks plausible" is a different question from "does it
  actually match what the tool prints," and a purely textual review cannot tell them apart. For
  every such guardrail, **run the underlying tool once** against the existing repo/workspace (it
  already has a buildable/runnable state — this costs one invocation of the real tool, not a
  purpose-built repro of the guardrail's specific scenario) and check the pattern against the
  real output. Tell: a `Select-String` / regex / grep on a tool's stdout whose pattern encodes
  field order or exact wording the review has not confirmed against a real run. The motivating
  case (#248): a `tests-fail-on-stubs` guardrail's
  `Select-String -Pattern "Passed:\s*\d+,\s*Failed:\s*[1-9]"`, stacked on top of an
  already-sufficient exit-code check, assumed xUnit's summary line reads "Passed: N, Failed: M"
  — it's always "Failed: N, Passed: M" — so the regex could never match and the guardrail failed
  UNCONDITIONALLY regardless of what the agent did. The same bug existed identically in four
  scripts in one plan; a purely textual read of all four missed it because the regex reads as
  plausible without a real `dotnet test` run to check it against. Scope this narrowly — do
  **not** run the tool for guardrails with no output-format assumption to verify (`Test-Path`, a
  `git diff`, a bare exit-code check): there is nothing there a tool invocation would confirm.
  Fix: run the tool, observe its real output, and confirm the pattern matches; once confirmed
  either way, the usual fix is to DROP the pattern-match entirely and rely on the exit code alone
  (the catalogue's stub-based-TDD form already does this correctly, `stacks/dotnet.md §4.1` —
  this is a review-side gap, not a doctrine gap). **BLOCKER** when the pattern can be shown to
  never match the real output (the guardrail fails unconditionally, dead-ending every attempt at
  `needsHuman`); **WEAK** when it matches today but rests on a fragile, unconfirmed format
  assumption (SDK/framework version, locale, verbosity level) that could silently break later.
<!-- END ADDED PROBE #248 -->
<!-- BEGIN ADDED PROBE #302 — script guardrail not smoke-tested against valid + invalid samples -->
- **Script guardrail not smoke-tested against a valid + invalid sample (#302)**: EXTENDS #248 but is
  **DISTINCT**. #248 runs the *underlying tool* once to confirm a guardrail's assumption about that
  tool's PRINTED output; #302 EXECUTES the guardrail **script itself** (`.sh`/`.ps1`/`.py`, in any of
  the four folders) against hand-synthesized samples to prove the SCRIPT's OWN correctness — does it
  run at all, PASS a valid artifact, and REJECT an invalid one. The motivating bug had **no**
  tool-output assumption: a LikeC4 single-quote nested inside a bash `-e '...'` block was silently
  quote-stripped, corrupting the throwaway fixture on EVERY attempt regardless of the task's output —
  so #248's "run the tool once" probe did not cover it, and a purely textual read of the script missed
  it (the quote-stripping is *syntactically valid* bash; only EXECUTION reveals the corruption). For
  every script guardrail that is **runnable-at-author-time** — idempotent (no persistent workspace side
  effects; a temp dir cleaned via `trap`/`finally` is fine) AND its input is in-repo or
  **hand-synthesizable** AND it needs no live external dependency (no server boot, no network, not the
  full merged HEAD) — do the two-sided execution: `bash -n`/`sh -n` (or a `pwsh -NoProfile` parse) as a
  cheap FIRST pass, then run it against **(a) a hand-written representative VALID sample** of the checked
  artifact (expect exit 0) and **(b) a deliberately INVALID one** (expect non-zero). The
  **highest-value target** is a guardrail that RENDERS or EXECUTES the task's own **not-yet-authored
  output** (a throwaway workspace, a rendered fixture, an `--input-type` block): its real input does not
  exist until the task runs, so its first real execution is deferred to runtime today, and that is
  exactly where its own harness/fixture bugs (bash quoting, path handling, `--json` parsing) hide —
  hand-synthesize the sample and run it. Severity: **BLOCKER** when the script FAILS the valid sample (a
  false-red no correct implementation can satisfy → every attempt dead-ends at `needsHuman`, blocking
  downstream) or PASSES the invalid sample (a toothless / tautological check); **WEAK** when it is
  runnable-at-author-time but simply was not smoke-tested (a latent script bug the review should close
  now). **Not runnable-at-author-time** (needs a live service / the built binary / the full merged HEAD)
  → do NOT block: run the syntax pass, reason about correctness, and note in the report that the
  guardrail could not be author-time-executed and why (an honest deferral). Scope narrowly — a
  `Test-Path` / bare exit-code / `git diff` guardrail with no rendering, parsing, or fixture step has
  little a run would reveal that a read does not; spend the execution budget on the render/execute-the-
  output scripts. (Doctrine: `guardrails-domain-knowledge` → author-time smoke-test gate; plan-breakdown
  Step 7.0d adds the matching authoring rule.)
<!-- END ADDED PROBE #302 -->
<!-- BEGIN ADDED PROBE #455 — task-level test filter scoped past the task's own tests -->
- **Task-level test filter reaches a SIBLING's tests — deadlock (forward) or tautology (inverse) (#455)**:
  the concrete instance of §2's **"Over-broad: 'all tests pass' anywhere except the terminal
  `<plan>/guardrails/` folder"** rule, wearing a `--filter` so it does not look over-broad. A task-level
  `tests-pass` / `tests-fail-on-stubs` whose filter is a **plan-wide selector** (a category/trait/tag every
  new test class in the plan carries, a whole test project, a bare namespace) asserts the state of **every
  test in the plan** rather than the ones its own task pair owns.

  **Why the existing probes cannot find this, and what that demands of you.** Both of this skill's other
  executed probes evaluate a guardrail **in isolation** — #248 runs the underlying TOOL once to check an
  output-format assumption, #302 executes the guardrail SCRIPT against synthesized samples. Read alone,
  a `dotnet test <proj> --filter "Category=ModelTieringStage1"` guardrail is **flawless**: correct
  syntax, correct polarity, real teeth, runs clean. The defect exists only in the relationship between
  that filter's **SCOPE** and the DAG's **dependency EDGES**. **A probe you can satisfy by looking at one
  guardrail will fail exactly the way the pass that missed #455 failed.** Do this cross-referencing
  explicitly, and write the table down — it is the evidence that the probe actually ran:

  | task | guardrail (polarity) | filter | test classes the filter SELECTS | who authors each | who makes each green |
  |---|---|---|---|---|---|

  Fill it by (1) listing, per task, the test class(es) its `writeScope` and action prompt say it AUTHORS;
  (2) for each task-level guardrail carrying a test filter, resolving which of those classes the filter
  selects — a trait/category filter selects **every** class carrying the trait, including classes other
  tasks author; then ask **both** directions.

  **Resolve the filter's scope against the corpus — do not assume it selects what it was meant to
  select.** The lazy way to fill column 4 is to copy column 1, which reproduces the exact blind spot this
  probe exists to close. Two honest ways to fill it: **(a)** when the test classes already exist (a
  regeneration, a partially-run plan, a brownfield target), ENUMERATE what the filter selects —
  `dotnet test <proj> --filter "<the guardrail's filter>" --list-tests` names them without running
  anything, which is both cheaper and more legible than a count, and immune to the localization trap
  below. Any listed test the task's own pair does not own is the finding, in hand, without reasoning.
  **(b)** when the classes do not exist yet, resolve on paper against the
  class names the plan's action prompts PIN. If the prompts **do not pin** their test class names, that is
  itself a **BLOCKER**: neither the author nor you can determine what any task-level filter selects, and an
  author who cannot name the class falls back on the plan-wide trait — which is how this defect is born
  (plan-breakdown `stacks/dotnet.md §4.3` requires the prompt to pin file + class).

  - **Forward — does the filter select tests that only a DOWNSTREAM task can make green?** For a guardrail
    where exit 0 is the pass, take each selected class authored by another task and find the task that
    turns it green. If that task is a **descendant** of this one (it `dependsOn` this task, directly or
    transitively) → **BLOCKER: deadlock.** This task cannot go green until a task that depends on it has
    run. Note explicitly that `guardrails validate` and `guardrails graph --check` **both PASS** on this —
    the cycle is between a task and a **sibling's test corpus**, not between tasks, and no DAG check models
    it, so their green tells you nothing here. Observed cost: 4 attempts and ~$20 to `needs-human`, with
    the task's deliverable complete and its own tests green the whole time.
  - **Forward, third mode — a CONCURRENT sibling with no edge either way.** When the task that makes a
    swept-in class green is neither an ancestor nor a descendant but a **parallel** sibling, the guardrail
    is not deadlocked — it is **nondeterministic**: it passes or fails on merge order, which is the
    property that made #455 look intermittent and let a third task ride through green. Flag it **WEAK**
    (**BLOCKER** when the plan runs those tasks in the same wave, where the race is not hypothetical),
    with the same narrowing fix. Do not record "no deadlock" as "no finding".
  - **Inverse — can a SIBLING's tests satisfy the red check?** For a guardrail where NON-zero is the pass
    (`tests-fail-on-stubs` / `tests-fail-on-current-code`), ask whether the filter selects **any** test
    class this task's pair does not own. If yes → **BLOCKER: tautology.** The check needs only *some*
    matching test red, so once any sibling's intended-red tests are on the base it passes **whether or not
    this pair's own tests fail** — the TDD-red proof (#155) degrades into merge-order luck. Treat this as
    the more serious half: the forward deadlock fails loudly, this one goes **green while certifying
    nothing** (it did, three times, in the run that produced this probe).

  **Do not be reassured by a task that currently passes.** Which of the two modes bites is decided by
  **merge timing, not correctness** — in the motivating run a third task carried the identical over-broad
  filter and passed only because it branched before the sibling's red tests reached its base. Flag every
  task carrying the shape, not only the one that failed.

  **Fix:** scope the filter to the test class that task pair owns —
  `--filter "Category=<PlanTrait>&FullyQualifiedName~<ThisTaskPairsTestClass>"` — on **both** halves of the
  pair, copied verbatim so they cannot drift. The class term is MANDATORY and the trait is an OPTIONAL
  conjunct — **the trait ALONE** belongs in exactly one place, the #181 baseline preflight's `!=`
  exclusion. (Do not read this as "the trait is banned": it is the correct first term above.)

  Two sub-checks on the fixed form, both **WEAK** on their own (**BLOCKER** when they reinstate the defect
  above):
  1. **Non-discriminating substring.** `FullyQualifiedName~` is a substring match, so a class name that is
     a prefix of a sibling's re-widens the filter silently (`~Dispatch` also selects
     `DispatchRouterTests`). The cheap mechanical check is
     `dotnet test <proj> --filter "<the filter>" --list-tests` — it enumerates exactly what the filter
     selects **without running anything**, so it works on a built repo even when the plan's own tests do
     not exist yet. The fix is the namespace-qualified name.
  2. **A zero-match guard that is missing, or present but inert.** A `--filter` matching **nothing** — or
     one that is **malformed** (`\|` instead of `|`: VSTest reports `Incorrect format for TestCaseFilter`
     and runs zero tests) — exits **0**, so a typo'd class name turns a `tests-pass` into a green no-op
     and makes a `tests-fail-on-stubs` report a misleading "tautological tests" failure. Every narrowed
     filter must assert ≥1 test actually EXECUTED. Read the guard's key, because three spellings look
     right and are not — flag each **BLOCKER**, since a guard that cannot fire is worse than none (it
     buys false confidence):
     - keyed on the **`No test matches…` string** → verbosity-dependent, absent under `-v q`, so it is
       written, executed, and never fires. #248 in its purest form;
     - keyed on **`Total:`** → `Total:` counts `[Skip]`ped tests, so a fully-skipped class passes a guard
       that is supposed to prove tests ran. The executed count is `Passed:` + `Failed:`;
     - keyed on any English summary token **without pinning `$env:DOTNET_CLI_UI_LANGUAGE = 'en'`** → the
       summary is LOCALIZED (a German-culture box prints `gesamt:`, no `Total:`), so on such a machine the
       guard fires on **every** run and the guardrail fails unconditionally.

     Also check the **ordering**, which is polarity-dependent: a **forward** check must test the exit code
     BEFORE the guard (a crashed/never-started test host exits non-zero with no summary; guard-first
     misreports it as "the filter matched zero tests" and sends the retry agent to rename a correctly-named
     test class — the one file inside its `writeScope`); an **inverse** check must run the guard FIRST (a
     crash exits non-zero, which is that check's success condition, so guard-second certifies "TDD red"
     over a run that executed nothing). Wrong order → **WEAK**, **BLOCKER** on the inverse (it certifies
     nothing). (Doctrine: catalogue → "Its SCOPE decides whether it proves anything";
     `stacks/dotnet.md §4.3`; plan-breakdown Step 4 carries the matching emission rule.)
<!-- END ADDED PROBE #455 -->
<!-- BEGIN ADDED PROBES #254 — waved plans (nested layout) -->
- **Cross-wave `dependsOn` edge (#254)**: in a waved plan, `dependsOn` is **intra-wave only** — a task
  edge naming a task in **another wave** is a hard error (**GR2034**, `validate` catches it). Beyond the
  lint, flag the **shape** it signals: a wave-2 task that "depends on" a wave-1 artifact should express
  that dependency as the **wave-2 ENTRY gate** ("the prior wave's outputs materialized") plus the action
  reading the real path — the wave barrier already orders the stages. A cross-wave edge (or an attempt to
  fake cross-wave ordering with a duplicated task) is a **BLOCKER**: name the offending edge and the
  entry-gate rewrite.
- **Wave-qualified state key (#254 / #164 one level up)**: for every state-writing prompt action in a
  waved plan, the single top-level fragment key must be the **wave-qualified id `<waveDir>/<taskFolder>`**
  (e.g. `wave-02-provision/01-author-tests`), NOT the bare folder name and NOT the `stableId`. A bare or
  wrong-wave key is rejected as foreign on **every** attempt (the #164 loop, one level up) →
  `needsHuman`. Cross-check the harness-contract header example, the `## Task` fragment example, and the
  state-output guardrail's index (`$fragment.'wave-02-provision/01-author-tests'.<key>`) — all three must
  use the same wave-qualified id. A mismatch is a **BLOCKER**.
- **Missing / wrong-polarity wave ENTRY gate (#254 / #181 at the wave boundary)**: for each wave ≥ 2,
  confirm `<plan>/<wave>/preflights/` carries a **POSITIVE** "the prior wave's outputs materialized"
  check — the artifacts this wave builds on (real files/symbols/binary the prior wave produced) are
  present and non-empty before this wave's DAG runs (the #181 positive-baseline archetype at the wave
  boundary). It must be **positive-monotone-safe** (assert-**present**, never "not yet present" — a
  negative "absent" check at a wave-2 entry gate flips false the instant an unrelated file lands, a
  false-RED). A downstream wave whose tasks plainly build on upstream artifacts but whose entry gate is
  missing → **WEAK** (the run wastes a turn building against possibly-absent bytes, or misattributes an
  upstream-materialization failure to this wave's tasks); a **negative-polarity** wave-2+ entry gate →
  **BLOCKER** (it will false-RED). (Wave 1's entry gate is the ordinary plan-start baseline — a
  brownfield green-start or a negative fresh-start — reviewed by the §2 baseline probe.)
- **Wave EXIT gate — GR2028 per wave + intermediate-wave union-safety (#254 / #125 / #165)**: the
  four-folder gap and union-safe probes (§2) apply to **each wave's** `<plan>/<wave>/guardrails/`:
  - A **multi-leaf / fan-in** wave whose exit gate is absent, empty, or tautological (`exit 0`) → same
    **BLOCKER** as the flat terminal-folder probe, but **per wave** (GR2028 applies per wave): the exit
    gate needs ≥1 real integration re-run (build/suite or a union invariant).
  - An **INTERMEDIATE** wave's exit gate that marks a **whole-build / whole-suite** check
    `scope:"integration"` is the **#125 terminal-postcondition anti-pattern** → **BLOCKER**: the
    integration set re-runs at every union, and a whole build/suite red-halts a correct partial merge.
    A whole-build/suite check must be **LOCAL** (no `scope`); the wave's `scope:"integration"` guardrail
    must be a union-safe CONDITIONAL invariant. Only the **LAST** wave's exit gate (which runs on the
    fully-merged HEAD) is the right home for a whole-suite LOCAL `tests-pass` — the whole-plan terminal
    boundary. Flag a plan-root `<plan>/guardrails/` that merely DUPLICATES the last wave's exit gate as a
    NIT (it is optional-additive, not a second terminal gate).
- **Later-wave prompt references earlier-wave code (#254 / #203)**: apply the "#203/#204 stale
  line-number / unhedged architecture claim" probe with **wave placement** as the earlier/later
  discriminator — a wave-2 prompt describing wave-1's not-yet-run output is the canonical case. The
  stronger fix here is the **JIT staged-breakdown flow** (author the later wave against the materialized
  integration worktree, so nothing is guessed); if the plan authored a downstream wave up front with
  guessed paths/line-numbers where JIT was available, flag it **WEAK** and recommend the JIT flow +
  durable markers + the `maxTurns: 75` companion bump.
- **JIT stub wave (#254)**: a declared-but-empty `wave-NN-<slug>/` with an empty `tasks/` is **not** a
  finding — it is the intended JIT staging (the run will honest-halt there, author the wave against the
  materialized workspace, review it, resume). Do NOT flag an empty downstream wave as "missing tasks";
  confirm the breakdown report documents the JIT workflow for it. (Only an **authored** wave gets the
  full adversarial pass; a stub is reviewed when it is later filled.)
<!-- END ADDED PROBES #254 -->
<!-- BEGIN ADDED PROBES #468 — source-shape demotion, sample pair, count floor (auto-merge friendly) -->
- **Source-shape regex where a TEST was available (#468)**: for every guardrail asserting a property of
  **implementation source**, ask the demotion question — *is this a claim about what the code DOES at
  runtime, or a structural fact about the build/wiring graph?* Behaviour → a test could have carried it
  and did not. **Recommend the demotion and name the test**; do not merely tighten the regex. This is the
  measured base rate, not a stylistic preference: over three review rounds and five agents on one
  breakdown the test layer was **never broken by any agent in any round** while **every blocker lived in
  the source-shape layer** (including 5 regressions introduced while fixing earlier rounds), and against
  a tree with the type declarations and **no wiring at all** a 14-clause grep manifest went **10/14
  green**. *A grep manifest measures vocabulary, not capability.*
  - **Two named sub-shapes worth calling out by name.** *"X must USE Y"* (must consume / go through /
    share / not diverge from a predicate, policy, formatter) → recommend an **AGREEMENT property test**:
    assert the two sides agree over the input domain, which passes on an equivalent inlined copy today
    and fails the moment it drifts. Three successive regexes failed on the measured case; no regex can
    express it. And a **grep manifest** of N clauses over declarations → ask how many clauses a
    declarations-only tree satisfies, and report that number.
  - **Do NOT flag the legitimate ones.** Build-descriptor registration, cross-module reference chains,
    entry-point wiring (#64), the #120 grep fallback, and #176 negative assertions are structural facts
    with **no runtime proxy** and held up fine across the same rounds. The finding is *reaching for a
    regex to prove something a test could prove better*, never source-shape as such.
  - Severity: **BLOCKER** when the check can false-red a correct implementation (apply the #479 test —
    *can a correct implementation be written that this rejects?*); **WEAK** when it merely certifies
    vocabulary a test should have certified. Also a finding when the breakdown report carries **no line
    saying why no test could carry it** — the report owes one per surviving source-shape check.
    (Catalogue → "The source-shape demotion gate"; the 13-shape taxonomy table is the named battery Probe
    B works down.)
- **Source-shape guardrail with no committed two-sided sample pair (#468/#302)**: a `file-contains` check
  over implementation **code** shipped without `tasks/<id>/samples/NN-check.valid.<ext>` /
  `.invalid.<ext>`. #302 requires the two-sided execution at author time; this makes it **durable** —
  every defect in
  the taxonomy was one execution away from discovery, and what was missing was that the execution had to
  be re-run **after every edit**, which is how a raw-vs-stripped fix from round 1 was re-broken by round
  3's rewrite of the same file. **Re-run the whole pair yourself, not just the clause you are examining.**
  The **valid** half is the one that pays: it is the only half that can expose a clause that never matches
  (a `\b` collapsed to a literal `0x08` by the authoring pipeline), a false-red on legitimate brace style,
  or a case mismatch — under the invalid half everything is failing anyway. Check the valid sample is
  **complete**, not a fragment: an incomplete one produces a different failure and masks the real defect.
  **Do NOT raise this for a DOCUMENTATION deliverable** — no meaningful invalid sample of a design doc
  exists, the exemption is legitimate, and the substitute is the **PRECEDENT check** (does every demanded
  token have a sibling precedent in that same document?). There, the finding is a *missing precedent*, or
  a report that took the exemption **silently**. WEAK; BLOCKER when running the pair reveals a false-red.
  - **And check WHERE the samples live — a misplaced one is its own BLOCKER.** A sample inside
    `guardrails/` or `preflights/` is loaded as a **guardrail**: the loader enumerates every non-`.json`
    file there with no extension allowlist, so a `.valid.cs` fixture in `tasks/<id>/guardrails/` loads
    clean, **counts toward GR2003** (a fixture satisfying "this task has a guardrail"), and is
    **executed** at run time; in the catches-enforced folders it is a GR2027 load error instead. The
    samples belong in a `tasks/<id>/samples/` **sibling**, which the loader does not enumerate.
- **Executed-test COUNT used as an adequacy floor (#468)**: a guardrail asserting *"at least N tests
  executed"* as a coverage proxy. The runner counts **theory data rows, not behaviours** — one `[Theory]`
  with N `[InlineData]` rows clears it while proving one behaviour, and raising N does not fix it. Fix: a
  **behaviour manifest over discovered test NAMES** (one clause per required behaviour against
  `--filter … --list-tests` output), which is a lower bound and **ratchets** as later waves land named
  tests. **Not** the #455 zero-match guard (`>= 1` test executed), which proves the filter selected
  something and is legitimate — do not flag that. BLOCKER (it certifies adequacy it cannot certify).
<!-- END ADDED PROBES #468 -->

### 2b. EXECUTE the guardrails — the phase that catches what reading cannot (#479)

Everything above reads scripts and reasons about them. **That reasoning runs against a mental model of
the target tree, and the mental model is exactly where the errors live** — which is why this phase
exists and why it is not optional. Measured over one plan: every blocker that reached a live run was
found by *running* something, and none by reading.

Run all three probes. They catch different failures and none subsumes another. **A and B execute; C
reconciles the pairs execution reports as healthy** — it lives here because it is the residual the other
two provably cannot see, not because it runs anything. **A comes in two resolutions**: A gives one bit per
SCRIPT, and **A₂** refines it to one bit per CLAUSE, which is the only resolution at which a pre-satisfied
clause is visible at all (#478).

**Probe A — baseline (cheap, universal, no author effort).** Execute each task's guardrails from the
plan's starting workspace and record **exit code AND stderr**.

- Expect **RED**. A guardrail that is GREEN before its task runs certifies nothing — every clause is
  already satisfied, so the task is passable by doing nothing.
- **Read stderr, not just the exit code.** With `$ErrorActionPreference = 'Continue'` an exception is
  non-fatal: a broken regex in a comment/string-stripping step silently *skips the strip* and changes
  what the guardrail means. One such defect made a guardrail unsatisfiable on every attempt while
  still exiting 1 for a plausible-looking reason.
- A script that fails to **parse** is a dead-end no retry can fix (#473). So is one that crashes on a
  missing path, or whose `--filter` matches nothing.
- **Cost:** run the pure-script guardrails first — they take seconds. The ones that invoke
  `dotnet build`/`dotnet test` are minutes each; run them only when the plan is small or on request,
  and **name the ones you skipped in the Step 6 report** so the gap is visible rather than assumed
  covered.

**Probe A₂ — the baseline CLAUSE census (#478). Resolution, not a second opinion.** Probe A yields one
bit per SCRIPT; the defect is per CLAUSE. **Measured:** run as authored against the real tree, *every*
pure-script guardrail of the motivating wave exited **1** — and two of them each carried a clause that was
**already satisfied on arrival**, hidden behind its siblings' failures. Probe A reported that wave clean.
**Never treat a red baseline as a pass.**

**Census only the clause kinds that have a defect here.** A **required-present** clause (`-cnotmatch 'X'`
→ fail) must be **0 matches** on the baseline; a **numeric floor** (`-lt N`) must be **below N**. A
**forbidden-present** clause is *supposed* to be green before its task — **do not flag a ban for being
green on arrival**; a ban that is RED on arrival is Probe C's #470 collision. Roughly a third of the
corpus's clauses are bans, so inverting this cries wolf on every correctly-authored prohibition. Decide
polarity **by what the failure branch DOES, not by the operator alone** — GR2057's rule, because
`if ($c -match 'x') { $ok = $true }` is a REQUIREMENT wearing `-match`.

Two ways to get a clause's baseline verdict. Take the fast path where the shape allows; census the rest.

1. **Fast path — read Probe A's own stdout.** Where the guardrail uses the house accumulator
   (`$failures += …`) *and* the baseline run REACHED the dump, A already printed the list of clauses that
   fired. **Every required-present clause of that script must appear in that list; one missing is
   pre-satisfied.** Free, but partial — on the committed corpus the accumulator is present in **97 of the
   237** multi-clause guardrails (the other **140** are early-exit chains), and even there three shapes
   fall through to the census:
   - a **precondition early-exit** fired before the clause block — **87 of those 97** carry one, and on a
     greenfield baseline it is exactly what fires;
   - the clause sits in a **cost stage gated behind the dump** (the house pattern runs `dotnet test` only
     after the structural clauses pass, so on a baseline it never executes);
   - the clause is inside a **loop**, where no message means *"the loop found nothing to complain about"* —
     not the same as pre-satisfied.
2. **Census — one grep per clause, universal.** Lift the clause's pattern and its subject out of the script
   TEXT and run them against the baseline tree: `Select-String -Path <subject> -Pattern <pattern>`, with
   `-CaseSensitive` iff the operator was `-cmatch`/`-cnotmatch`. Record the **count**. Shape-independent —
   it works on early-exit chains, on staged scripts, on clauses that never execute, and on a script that
   does not parse (#473).
   **Census the SAME text the clause reads, or you will manufacture a false finding.** Under the
   two-variable rule a required-present clause matches `$code` (comments stripped) and a forbidden one
   matches `$scan` (comments *and* string literals stripped) — but `Select-String` reads the file raw. So
   for each hit, check where it lives: hits that are **entirely inside comments** do not pre-satisfy a
   `$code` clause. A raw count of 3 that is 3 comment mentions is **zero**. (Neither of the motivating
   instances was excused this way — `action.prompt.md` and `CriticalityJudge` were both live code.)

**Re-MEASURE every declared count; never read one.** The catalogue requires each required-present clause to
record its measured baseline count. That number came from the same mental model that wrote the clause — the
motivating instance carried `# … appears nowhere else` above a token appearing **twice in that exact file**.
A declared count that disagrees with your census is a **BLOCKER on the guardrail**, whichever number is more
convenient. A *missing* declaration is not a finding on its own; it just means you census that clause.

**Verdict — a pre-satisfied required-present clause is a BLOCKER**, even beside strong siblings: clause
strength does not compose, because the task only has to satisfy the clauses that are red. The legitimate
exceptions are real — a positive-baseline / wave-entry preflight, a `tests-untouched` regression clause, the
*"if X is present"* half of a union-safe conditional (#125/#165), a ratcheting behaviour-manifest clause on a
partially-landed tree — but **an exception counts only if the script DECLARES it.** You are grading the
declaration, not inferring the intent.

**What A₂ still does not see.** Report these; never let "census applied" absorb them.
- A **runtime-composed** pattern — built from a variable, an interpolation, or `[char]` codes (the corpus has
  one assembled from `[char]92`) — cannot be lifted out of the text. Execute that clause in isolation, or
  record it **NOT RUN** with the reason. Never as passed.
- A clause **red on arrival that goes green as a SIDE EFFECT of satisfying a sibling** — the census sees red
  and is content. That is taxonomy 10 at the clause-set level; **Probe B op 18** is the check.
- A clause whose **subject does not exist on the baseline** — trivially red, so the census is vacuous. The
  ambient-vocabulary test (taxonomy 9), applied to the file the task will create, is all you have.
- A **non-pattern** clause (`Test-Path`, a JSON-shape assertion, a sub-tool's exit code). Ask the same
  question by hand; there is no mechanical form, and the verdict is still declare-or-flag.
- **The tree you census is the PLAN's baseline, not the TASK's.** A task with ancestors runs against a tree
  those ancestors already wrote into, so a clause measured 0 today can be pre-satisfied by the time its own
  task starts — and that tree does not exist at review time. This is the deepest hole left, and it is
  unclosable by measurement here. The cheap partial: for each required-present clause on a **non-root**
  task, grep the **ancestor tasks' prompts and `writeScope`s** for the same token. An ancestor instructed to
  write it is a WEAK finding naming both tasks; an ancestor whose deliverable makes it near-certain is a
  BLOCKER. Report the residual for every task where you did not do this.
- **Red on arrival and red forever** → Probe C. **Red on arrival and cheaply gameable** → Probe B. **A₂ and B
  are independent, and neither implies the other**: the motivating clause was *both* pre-satisfied *and*
  gameable by a one-const append, and each probe is blind to the other's half.

**Probe B — the minimal-gaming mutation (the one with teeth).** For each task, apply *the cheapest edit
that satisfies the guardrail's literal text without delivering the capability*, then re-run. **Expect
RED.** If it goes GREEN, the task is passable by that edit and the guardrail is the finding.

Measured example: appending `internal static class Marker { public const string k = "prompt"; }` — one
unused constant, zero capability — took a task's guardrail from 1 finding to **exit 0**.

The operator set is small and enumerable, which is what makes this mechanical rather than a test of how
inventive you feel that day. Work down it:

| # | operator | dies against |
|---|---|---|
| 1 | append an unused type/const containing the required token | a dotted **call**, not a bare name (#76) |
| 2 | put the token in a **comment** | comment-stripping |
| 3 | put the token in a **string literal** | string-stripping |
| 4 | satisfy a proximity regex **across a statement boundary** | keying on the outcome, not adjacency |
| 5 | one **omnibus method name** satisfying several substring markers | full pinned names, anchored |
| 6 | **declare** the method instead of forwarding/calling it | requiring `_inner.<name>(` — the forward itself |
| 7 | assign the member to **`null`** or an **empty object** | excluding `= null`, requiring the payload |
| 8 | compute the value and **discard** it (`_ = x with { … }`) | requiring the assignment back, or the destination |
| 9 | reference the type via **`nameof`** in a dead field | a dotted call |
| 10 | name an **event/local** after the class the clause wants called | a dotted call, again |
| 11 | create the required **file empty** / a method with the pinned name and an **empty body** | requiring an emit or any call inside the body |
| 12 | satisfy a `--filter` with a **`[Skip]`ped** test, or let it match nothing | the zero-match guard (#455) |
| 13 | write the token in a form the artifact never uses | see the PRECEDENT check in the catalogue |
| 14 | **flip the case** of the required identifier (`JudgeTier` → `judgeTier`) | `-cmatch` / `(?-i)` — PowerShell `-match` is case-INsensitive, C#/Java/Go identifiers are not (#468) |
| 15 | **brace** the `if`s in the method body, or nest a block inside it | never brace-matching a body in a regex — a `(?ms)` extractor stops at the first nested close, so brace STYLE decides the verdict (#468) |
| 16 | add or drop a **modifier** before the declaration (`sealed record` vs `record`, `async`, `partial`) | the part the language FIXES — the declaration keyword through the name (#468, generalising #112) |
| 17 | write the **banned** thing in a form the ban never enumerated (non-defaulted, nullable, `async`, an options object) | banning the CONSTRUCT — the enum member, the type position, the destination — not one spelling (#468) |
| 18 | satisfy **every** token of a multi-token coverage check with ONE line of real code (`var unused = new { r.A, r.B, r.C };`) | distinct constructs the outcome implies (a `[Fact]` per behaviour, a dotted call per collaborator) — comment-stripping is irrelevant here (#468) |
| 19 | clear a *"≥ N tests executed"* floor with **one `[Theory]` and N `[InlineData]` rows** | a behaviour manifest over discovered test NAMES; never a count (#468) |
| 20 | satisfy a **real-seam / composition-root `--filter`** with a test that **constructs the FAKE** — same class, same `…RealSeam…` name, a substituted seam | the test asserting an effect **only the production implementation emits** (a stream-log FILE on disk, a journal `blocker-retried` DECISION), never merely that the collaborator was called (#382) |

Three patterns generalise and are worth applying before the table: **anchor on a USE, not a mention**
(kills 1, 6, 9, 10, 11), **anchor on the DESTINATION, not the value** (kills 7 and 8), and **anchor on
what the LANGUAGE fixes, not on what the author may freely vary** (kills 14, 15, 16 — case-sensitivity,
brace style, modifier order).

**Operator 20 is outside all three patterns, and it is the only operator that can be INAPPLICABLE
(#382).** Outside, because 1–19 all ask *what TEXT satisfies the clause* while 20 asks *which OBJECT the
test constructs* — anchoring on a use, a destination, or what the language fixes does nothing against it,
which is why nothing already in the table can game a real-seam guardrail. The mutation is literal: in the
test the guardrail's `--filter` selects, swap the real seam out of the component's constructor for the fake
its sibling unit test already uses, keep the class and method names, re-run the filter. **GREEN means the
filter is selecting a NAME, not a behaviour** — the guardrail is the finding (BLOCKER on a
composition-root / production path), and the fix is the archetype's **assertion requirement** (an effect
only the production implementation emits), never a narrower filter, which only renames the same hole.

**Inapplicable, and reported that way.** Probe B mutates the target tree at review time, so operator 20
needs the test to already exist. It applies to a plan being **re-reviewed against an existing
implementation** — a resumed or regenerated plan, an amendment to a landed wave. **On a greenfield first
review the test has not been written yet, so record the operator as NOT RUN, with that reason — never as
passed.** A probe that reports "passed" when it could not execute is the exact false green #382 exists to
remove, and it is worse than no probe because it consumes the doubt that would otherwise drive a read. The
reporting rule above (name the probes you skipped) already covers it; Step 6 carries the line, and
"Probe B applied" must not silently absorb an operator that never ran.

**When Probe B keeps landing, the finding is the ARCHETYPE, not the clause (#468).** If a task's
guardrail asserts a property of **implementation source** and two or more operators above go GREEN
against it, stop patching clauses and ask whether a **test** could carry the property — that is the
demotion gate, and it is the fix. Measured over three review rounds on one breakdown: the test layer was
never broken by any agent in any round, every blocker lived in the source-shape layer, and three patch
rounds did **not converge** — round 3 found more blockers than round 2 because each fix landed beside a
fresh regression in the same file. A fourth round of clause repair is the wrong move; recommend the
demotion.

**Probe C — reconcile the clause PAIRS execution cannot separate.** A guardrail that is red before *and*
red forever is indistinguishable from a correct red to both probes above: A expects red and gets it, B
mutates and it stays red. Both report "healthy". So this probe is **read-and-reconcile, not execute** —
and it is not optional, because each shape below is a **certain dead-end**, not a weakness. In every
measured case the two colliding clauses sat **30–40 lines apart and were edited at different times**, and
**each was individually correct** — which is exactly why reading the script top-to-bottom does not find
them. Write the pairs down side by side.

| pair to reconcile | the dead-end | verdict |
|---|---|---|
| every **required-present** literal × every **forbidden-present** pattern **in the same file** (#470) | the required text trips the forbidden pattern, so **no file can satisfy both** — every attempt fails identically with coherent, actionable, wrong feedback | **BLOCKER** |
| every **forbidden-present** token × the task's own **`action.prompt.md`** (#470) | the prompt uses the banned word, inviting the agent to write the very thing that reds it. Satisfiable, but it cost a full attempt when measured | **WEAK** (BLOCKER if the prompt hands the agent the token as required vocabulary) |
| every **numeric floor** × its own **filter's cardinality** (#484) | an arithmetic dead-end — a zero-match floor exceeding what the filter can ever select | **BLOCKER** |
| every asserted **outcome** × the task's **`writeScope`** (#474) | the guardrail demands something the task is not permitted to write | **BLOCKER** |

For the first row, do it mechanically: take each required clause's literal text **de-regexed** and match
it against each forbidden pattern in the same file. The measured instance was a required
`[Trait("Category", "TierResolution")]` whose own **string literal** carried the token a later clause
forbade — its blast radius was three downstream tasks. **Fix for both #470 rows:** run the forbidden scan
over **STRIPPED** source (comments **and** string literals — #97/#98 covers only comments) and anchor the
ban on a **USE, not a mention** (#76). **Check the fix does not create the mirror dead-end:** strip in
**two levels**, not one — required clauses read the comment-stripped text (so a token that legitimately
lives in an attribute or message string can still satisfy them), forbidden clauses read the
literal-stripped text. A "fix" that strips literals for *every* clause makes the required one
unsatisfiable, which is the same BLOCKER wearing the other polarity. Do not read this as a revert of
#177: GR2026 fires when a guardrail
REQUIRES a token the prompt never mentions; this fires when it FORBIDS a token the prompt DOES use —
opposite polarities, each silent in the other's healthy case. (Catalogue → "A forbidden token must not
collide with what the task REQUIRES".) A mechanical `validate` lint now backstops the narrowest slice of
this — **GR2057** fires when one subject variable carries both a required-present literal and a
forbidden-present pattern that the literal trips. **Still run this probe**: GR2057 is deliberately silent
wherever it cannot PROVE the collision, and every one of those gaps is yours — clauses over DIFFERENT
subjects (the two-variable `$code`/`$scan` fix, which it must not flag), compound `-and`/`-or` conditions,
interpolated or composed patterns, anchored forbidden patterns, `.sh` guardrails, and both the cross-file
and prompt↔guardrail axes. A green `validate` means the provable case is clear, not that the pair agrees.

### 3. DAG soundness
- Every edge justified (artifact, guardrail, or explicit ordering — not prose order).
- **Missing edges**: task B reads a state key or file only task A produces, with no
  path A→B. **Apply this at the IMPLICIT COMPILATION level too** (#176): if B's verification
  compiles a test project containing an ancestor test-author task's `.cs` file that references a
  type produced by a non-ancestor implementation task, that is a missing edge — see the
  "Transitive compilation dependency" probe in §2.
- **False edges** serializing genuinely parallel work.
- A terminal task aggregates (suite green / e2e) so the run has a meaningful end.
- **Terminal `<plan>/guardrails/` folder on a parallel plan (NOT an `integrationGate` sink).** A plan
  with ≥2 leaf tasks or any fan-in (the shape a parallel run produces) MUST carry a non-empty plan-root
  **`<plan>/guardrails/`** folder (the terminal gate run once on the merged plan-branch HEAD, SSOT §3.3)
  with ≥1 real integration-set re-run — see the four-folder gap probe in §2 for the content bar
  (GR2028). The retired `integrationGate: true` sink TASK is a **GR2029 hard error**: a lingering one is
  the BLOCKER, not its absence. A single linear chain with no fan-in needs no terminal folder.
- **Waved plan — the DAG is PER WAVE (#254).** In a waved plan each wave's `tasks/` is a self-contained
  DAG; check acyclicity, missing edges, and false edges **within each wave**. Cross-wave ordering is the
  barrier, not a task edge (a cross-wave `dependsOn` is GR2034 — see the §2 waved probe). "A terminal
  task aggregates" becomes **per wave**: each multi-leaf/fan-in wave's EXIT gate is its aggregation
  point, and the LAST wave's exit gate is the whole-plan end.

### 4. Missing-insertion check
Re-apply plan-breakdown Step 5: any guardrail referencing an artifact no ancestor
produces and the repo doesn't already contain → a missing guardrail-enabling task.

### 5. State-contract lint
- Every prompt action carries the harness-contract header block.
- Every state key consumed downstream is produced upstream (or seeded).
- **Every state key consumed downstream has a fragment-key-present guardrail on its
  producer** (reads `GUARDRAILS_STATE_FRAGMENT`, asserts non-null/non-empty) — otherwise
  the action can skip writing the key and the consumer runs with null. (Catalogue
  state-output leaf.)
- **State-out key MUST be the task FOLDER NAME, never the `stableId` (#164).** For every
  state-writing prompt, read the fragment example/instruction in the `## Task` body and the
  harness-contract header. The single top-level key must be **this task's folder name** (the
  directory the `task.json` lives in). A fragment example keyed by anything else — most often
  the task's `stableId` (a `^[a-z0-9][a-z0-9._-]*$` token like `j9hf6y` that is NOT the folder
  name), a foreign task's folder name, or an arbitrary shared key — is a **BLOCKER**: the
  harness rejects it as a foreign/unowned key on **every** attempt (single-writer-per-key, SSOT
  §6.2), rolling back file writes and dead-ending the task at `needsHuman` (the #164 failure
  loop). Cross-check that the producer's state-output guardrail indexes the **same** folder name
  (`$fragment.'<folder-name>'.<key>`); a mismatch between the prompt's key and the guardrail's
  index is the same BLOCKER. Fix: rewrite the fragment example to
  `{ "<this-task-folder-name>": { … } }` and align the guardrail's index.
- `promptRunners` present iff prompts exist; `allowedTools` scoped, not blanket. **On a
  multi-task plan (≥2 tasks joined by `dependsOn`), flag an `allowedTools` that carries
  stack-specific commands but no read-only git inspection** (`Bash(git log*)`, `Bash(git
  diff*)`, `Bash(git show*)`, `Bash(git status*)`) **— a MINOR finding, not a blocker**
  (plan-breakdown Step 6, #252): without it a downstream task's prompt cannot cheaply
  inspect what an ancestor task already committed and falls back to broad `Grep`/`Glob`
  sweeps. Still do not suggest adding a state-mutating git command (`restore`, `reset`,
  `checkout`, `push`, `commit`, `stash`) — the read-only default is the right thing to
  AUTHOR, because on a clean box or in CI the plan's list IS the whole grant. But do not
  read the omission as a restriction: **`allowedTools` is a floor, not a ceiling** — Claude
  Code MERGES the harness's `--allowedTools` with the operator's `~/.claude/settings.json`,
  so a plan's list can only GRANT a capability, never WITHHOLD one. On a box whose settings
  already allow `Bash(git checkout:*)`, a task's prompt can run `git checkout` even though
  the plan lists no git at all. So never raise — or dismiss — a finding on the premise that
  leaving a verb out of `allowedTools` makes it unavailable.
- **Every statically named model resolves to a runner the config can actually construct** — the §2
  model-availability probe from the config side: each `promptRunners.<name>.model` /
  `guardrailOverrides.model` sits on a block whose `kind` has a concrete runner (only `claude` does today,
  SSOT §9), and `promptRunners.default` names a declared block whenever two or more are declared. Report,
  never rewrite — and report a JIT-resolved judge model as UNCHECKED (#223) rather than passing over it.

### 6. Report

| Task | Guardrail | Severity | What wrong implementation slips through | Concrete fix |
|---|---|---|---|---|

Severities: **BLOCKER** (a wrong implementation passes) · **WEAK** (gameable,
nondeterministic-where-deterministic-possible, or unactionable) · **NIT**.
For WEAK prompt-judges, the fix column contains the replacement deterministic
guardrail — ideally as ready-to-paste script text.

The report also states what the pass could NOT check — as explicit lines, never silent omissions: an
unchecked gap that goes unmentioned is indistinguishable from a verified one. At minimum:

- the model-availability probe's JIT-resolved judge models, deferred to #223;
- **which guardrails Step 2b actually EXECUTED, and which it skipped** — name the toolchain-invoking
  ones you did not run, and say whether Probe B (the minimal-gaming mutation) was applied per task or
  only to a sample;
- **the classes no probe can see** (#470 require-and-forbid, #474 unreachable-outcome, #484 arithmetic
  dead-ends). These are red before AND red forever, so a baseline cannot distinguish them from a
  correct red. If you hand-checked them, say so; if you did not, say that.
- **how far the A₂ clause census got, per task** (#478) — for each guardrail, whether its required-present
  clauses were resolved by the **fast path** (an accumulator list Probe A actually printed) or by a **hand
  census**, and **name every clause left unmeasured** with its reason: a runtime-composed pattern, a subject
  absent from the baseline, a non-pattern clause, a cost stage that never executed. A red Probe A plus an
  unmeasured clause set is **not** a clean task, and must not be reported as one — that conflation is the
  whole of #478. State separately that **the censused tree is the PLAN's baseline, not each task's** — a
  non-root task's clause can be pre-satisfied by an ancestor's output, and no review-time measurement can
  see that tree; say whether the ancestor-prompt/`writeScope` textual check was run in its place.
- **whether Probe B operator 20 ran, or was INAPPLICABLE** (#382) — on a greenfield first review the
  real-seam test does not exist yet, so the operator is reported **not run**, with that reason, and is
  never folded into a blanket "Probe B applied". Reporting it as passed would be the false green the
  operator exists to catch.
- **whether the seam ledger was available to this pass at all** (#382). A ledger **not produced** to the
  review is an unchecked gap and says so; a ledger whose bolded `Seam ledger (#382)` **heading is absent**
  is a finding in the table. Do not report either as the other.

Then ask: **"Apply fixes?"** — per-finding approval, never bulk-silent. If a finding
concerns a guardrail the human added or edited (check `git log`/`git diff` if the
folder is tracked, else say you cannot tell), name that explicitly before proposing
changes to it.

### 7. Record the review — leave durable evidence, then stamp (#366)

When the review pass is complete (findings reported; fixes applied or explicitly declined), record it so
the harness's review nudge clears **and a durable audit trail is left behind**. Today the pass leaves
nothing on disk but the marker itself, so a real review and a bare stamp are byte-identical (#366 §1).
Close that gap in three moves — **get the hash → write the report → stamp with the report as evidence**:

**1. Obtain the plan hash (the skill can't compute it).** `PlanDefinitionHash` is computed by the CLI, so
ask for it:

```bash
guardrails plan-hash <folder>
```

It prints a single `sha256:…` line and writes nothing to disk. Capture it as `<planHash>`; its first 12
hex characters (after the `sha256:` prefix) are `<planHashShort>`.

**2. Write the review report** — the durable evidence. Home it under the plan's hash-**excluded** `state/`
tree at:

```
<plan>/state/reviews/review-<planHashShort>-<reviewedAtCompact>.md
```

(`<reviewedAtCompact>` is the review's UTC timestamp with punctuation stripped — e.g. hash `1a2b3c4d5e6f`
at `2026-06-22T14:03:11Z` → `state/reviews/review-1a2b3c4d5e6f-2026-06-22T140311Z.md`.) The report is
human-readable — its whole point is that a maintainer can later **read what the review found**. It
contains:
- the **Step 6 findings table + the verdict** (which BLOCKERs were addressed vs explicitly declined), and
- an embedded **plan-hash line the CLI parses (F2a)** — on its own line, the full `<planHash>` verbatim:

  ```
  Plan-Definition-Hash: sha256:…
  ```

`state/reviews/` is under the tree `PlanDefinitionHash` **EXCLUDES** (SSOT §7.3), so the report **cannot
re-stale the marker** — the same reason the marker itself lives under `state/`. It is a committed plan
artifact (like the marker), not per-run runtime state, so it belongs under `state/`, never `logs/` (a
review has no `runId`, and `--fresh` would wipe `logs/`).

**3. Stamp the marker, passing the report as evidence:**

```bash
guardrails mark-reviewed <folder> --evidence <report>
```

`mark-reviewed` runs the **F2 stamp-time checks** on the report — **(a) plan-binding:** it must embed a
`Plan-Definition-Hash:` equal to the current plan hash; **(b) path containment:** `<report>` must resolve
under `<plan>/state/reviews/` — and on pass records `attestation.source: review-artifact` plus the
report's `reportDigest`. **On failure of either check it downgrades to `source: bare`** (it never
fabricates a class it can't substantiate) and never refuses the stamp.

**Either path form works** (issue #430): a relative `<report>` is resolved against the **current
directory** first, as any shell path is — so `--evidence docs/plans/<plan>/state/reviews/<report>.md`
from a repo root is correct — falling back to plan-relative (`--evidence state/reviews/<report>.md`)
when you are standing inside the plan folder. Absolute paths work unchanged; `/` and `\` are
interchangeable on Windows.

A downgrade prints a **`WARNING: --evidence did NOT qualify … DOWNGRADING to source: bare` block on
stderr** naming the resolved path and the reviews root it was checked against, and the `OK:` line ends
with `DOWNGRADED: …`. **Never let that scroll past** — the stamp still exits 0, so the warning is the
only signal your review was recorded as a bare stamp. An **F2a** downgrade usually means the plan
changed after you ran `plan-hash` (re-run from move 1 with the fresh hash); an **F2b** downgrade means
the resolved path (which the warning prints) isn't under `state/reviews/` — compare it with the root in
the warning and re-run with the corrected path.

#### Evidence classes — recorded for AUDIT, not a gate

The stamp records a deterministic `attestation.source` (read back at read-time as `legacy` for a pre-#366
marker that has no attestation block):

| `source` | Meaning (what the CLI verified) | Written when |
|---|---|---|
| `review-artifact` | A review report for *this* plan was present and passed the F2 checks; its `reportDigest` is recorded. The only class backed by a durable report. | `mark-reviewed --evidence <report>` and F2 passes. |
| `bare` | No valid review report backs the stamp — the unchanged manual "I read it" confirmation, **or** a `--evidence` attempt that failed F2 (downgraded). Clears GR2025 exactly as before. | `mark-reviewed <folder>` with no / invalid `--evidence`. |
| `machine` | An **automated** flow stamped it (auto-breakdown / autonomous mode) — honestly labeled, never masquerading as human review. | `mark-reviewed <folder> --source machine`. |
| `legacy` | **Read-time only, never written** — a v1 marker with no attestation block. | — |

(`--reviewer <id>` records a self-reported, **non-authoritative** `actor` — a name to ask in an audit,
never a trust signal; label it as self-reported wherever surfaced.)

**State plainly what the class is — and is NOT.** The recorded class is for humans and tooling to inspect
after the fact; **it gates nothing — the Scheduler never reads it, and GR2025 stays an advisory warning**
(#366 §6; enforce-mode was considered and dropped). It is **not** a forgery deterrent and makes **no**
security claim: the marker is only as strong as **write-access to the plan folder**, and any agent that can
author the plan can author a matching report — there is no unforgeable option in a plain-file model (not
provenance, not a digest chain, not even a signed commit — the autonomous agent holds the key). And **the
harness never writes the marker on a human's behalf** to fake a human review — a `machine` stamp is labeled
`machine`, and `mark-reviewed` never fabricates a `review-artifact` class it can't substantiate. The value
is everyday **evidence hygiene + an audit trail** — telling a real review pass from a bare stamp,
deterministically and on the record, and preserving what the review found — and nothing more. (Any older
"unforgeable" / "raises forge cost" framing of the review floor is withdrawn.)

**Waved plan (#254):** each wave is a mini-plan with its **own** `PlanDefinitionHash`-keyed marker and its
own review report under `<plan>/<wave>/state/reviews/`. Run the three moves against the wave folder —
`guardrails plan-hash <folder>/wave-NN-<slug>`, write the report under that wave's `state/reviews/`, then
`guardrails mark-reviewed <folder>/wave-NN-<slug> --evidence <report>`. When you reviewed the **whole** plan
wave-by-wave, do this per wave; when you reviewed a **single freshly-authored wave** (the JIT flow), stamp
just that wave — its marker is keyed on that wave's hash, so re-authoring or resuming other waves does not
falsely mark this one reviewed (and editing this wave's files re-stales just its marker). Do not mark a wave
reviewed while a BLOCKER in it is open.

The marker `mark-reviewed` writes is the committed, `PlanDefinitionHash`-keyed `state/guardrails-review.json`
(SSOT §13 / §7.3). Until the plan's behavioral definition changes, `guardrails validate`/`run` stop emitting
the GR2025 "not reviewed" warning; editing any `task.json` / `guardrails.json`, **an `action.*` body, or a
guardrail/preflight body or `.json` sidecar** re-stales the marker and the nudge returns. `PlanDefinitionHash`
**covers guardrail/preflight/action BODIES** — not just structure/config like the narrower `PlanHash` — so
editing a guardrail's LOGIC after review (broadening a grep, dropping an assertion, `exit 0`-ing a check)
NOW re-stales the marker and re-fires GR2025 (#260); bodies are exactly what the review scrutinizes most. The
marker is COMMITTED as part of the reviewed plan: because it is `PlanDefinitionHash`-keyed it **self-stales
the instant any hash-covered file changes** — a **staleness** guarantee (it can never keep vouching for
*changed* content), NOT a forgery guarantee (an agent with tree access can always re-stamp; #366 §3).
`--fresh` does NOT wipe the marker or the report — `--fresh` clears only genuine runtime state (`run.json`,
`state.json`, `merge-conflicts.log`, `logs/`, `captured/`). Do NOT mark a plan reviewed while a BLOCKER
finding remains unaddressed.

## Quality bar
- [ ] `guardrails validate` ran first; findings don't duplicate the tool.
- [ ] `guardrails graph --check` ran; exit 2 (stale/missing) → regenerated and noted; exit 1 (error) → surfaced, not silently regenerated.
- [ ] Every BLOCKER names the concrete wrong implementation, not a vibe.
- [ ] Terminal/e2e tasks claiming an output quantity assert a STRICTLY POSITIVE value (no hollow `Assert.Equal(0,…)` / `NotNull` / bare `exit 0`); every structural property check is accessor-order-insensitive (no `\{\s*get` / `\{\s*set` anchor).
- [ ] Every WEAK judge finding names its deterministic replacement (or proves none exists).
- [ ] Every **statically named** model (`action.model`; each surviving judge's runner-configured `model` / `guardrailOverrides.model`; each `promptRunners.<name>.model`) was checked against the ONE block that will carry it (`action.runner` > frontmatter `runner` > `promptRunners.default` > the sole declared block — `action.model` never selects a runner), and that block's `kind` has a concrete runner in this harness version — a model no configured runner can serve is a FINDING naming the task and the model, not a mid-run registry halt: BLOCKER when nothing resolves or the `kind` is unimplemented (such a config LOADS AND VALIDATES CLEAN, so `validate` never catches it), WEAK for a provider-family mismatch (a model id has no enumerable valid set to check against). The probe REPORTS, never rewrites. Every judge whose model is resolved just-in-time is reported as UNCHECKED with the reason, deferred to #223, never silently passed over (#224).
- [ ] Coverage gaps cite the exact unverified completion criterion.
- [ ] Every `covers-key-behaviors` guardrail's required tokens are each named (directly or via synonym) in the SAME task's action prompt; a token the guardrail requires but the prompt never mentions is a BLOCKER ("the task will fail every attempt") — the human-judgement complement to the deterministic GR2026 warning (#157).
- [ ] **Every task declares a `writeScope` (#389)** — an ABSENT field is a BLOCKER (GR2041); `"writeScope": []` ("writes nothing to the repo") is a FIRST-CLASS VALID declaration and is NOT flagged (flag only a truly absent field). Every TDD implementation task's `writeScope` EXCLUDES its test-author task's test files (but may TARGET the stub file the test-author wrote, #155); no task carries a vacuous `**`/over-broad `writeScope` (propose a real surface or `[]`, never omission).
- [ ] Every inserted test-author task carries the correct TDD "red" for its type (#155): a BEHAVIORAL type has `build-passes` + `tests-fail-on-stubs` (with minimal stubs in its `writeScope`), not a lone non-zero-exit red gameable by non-compiling garbage; a split data-model task has a structural `[Fact]`/`[Theory]` covers-key-behaviors check.
- [ ] Every test-author task's `action.prompt.md` carries a **Scope boundary (harness-enforced)** paragraph (allowed path(s) + `git diff` check + retry consequence + the `needsHuman` redirect for an upstream missing-symbol compile error); absence is WEAK (#154).
- [ ] Every PROMPT task whose primary deliverable is a file under `.claude/` (new or existing) carries the verbatim STRAIGHT-TO-HATCH `needsHarnessWrite` escape-hatch instruction in its `action.prompt.md` (emit `needsHarnessWrite` to the state-out path FIRST, no direct `Write`/`Edit` probe to the `.claude/` path); absence is a BLOCKER — the tool-permission layer refuses `.claude/` writes unconditionally, so the task hits the wall on attempt 1 and dead-ends at `needs-human` (SSOT §9.3 / #191 / #313 / #321). Exempt: SCRIPT actions, and tasks that declare `stagingOutputs` for the deliverable. A task whose deliverable IS `.claude/settings.json` / `.claude/settings.local.json` cannot use the hatch (the harness rejects permission-file writes on an agent's behalf, #321) — flag it for a human author instead.
- [ ] Every state-writing prompt's fragment example/key is the task's FOLDER NAME (never the `stableId` or a foreign/shared key), and the producer's state-output guardrail indexes that same folder name — a `stableId`-shaped or otherwise-unowned key is a BLOCKER (harness rejects it every attempt → `needsHuman` loop, #164).
- [ ] A **brownfield** plan (modifies project(s) with existing tests in the touched area, worth-it gate passing) carries the #181 positive baseline as a **`<plan>/preflights/` POSITIVE check** (the general positive-baseline archetype — e.g. `01-baseline-<area>-tests-green`), NOT a no-op ROOT task: a plan-level Full Flight Check evaluated ONCE before the DAG against the starting repo, running the EXISTING area tests **via `--filter`** and asserting they pass (area-scoped, deduped one-per-area, #179-re-emit form); it targets the PRE-EXISTING tests via `--filter`, NOT the about-to-be-authored red tests and NOT the whole suite (whole-suite scope hits the #165/#176 compile-coupling trap → BLOCKER); it is DISTINCT from the terminal `<plan>/guardrails/` gate (green START before the DAG vs green END on the merged HEAD). A **greenfield** plan (or one failing the worth-it gate) has NO baseline preflight (a vacuous `dotnet test` over a zero-test project is itself a finding). Missing baseline preflight on brownfield is WEAK (BLOCKER when the area's existing tests are in fact red at start). A RED baseline preflight halts the run before the DAG (the general Full-Flight-Check semantics) (#181).
- [ ] A parallel plan (≥2 leaf tasks or any fan-in) has NO `integrationGate: true` sink task — a lingering `integrationGate: true` in any `task.json` is the BLOCKER (a **GR2029** hard error), not its absence — and instead carries a non-empty **`<plan>/guardrails/`** folder (the Terminal Gate) with **≥1 real integration-set re-run** (a whole-repo build / full suite / union invariant, `validate` enforces this as **GR2028**; a folder that merely exists or holds only a tautological `exit 0` certifies nothing → BLOCKER). Its `scope: "integration"` union-guardrail is a **union-safe CONDITIONAL invariant** (conflict-marker-free / "if X present, verify it"), NOT the full build or whole suite: a full-build or whole-suite guardrail marked `scope: "integration"` in the terminal folder is the #125 terminal-postcondition anti-pattern → **BLOCKER** (it red-halts correct intermediate unions where downstream TDD tasks have not run yet); the full build/suite must be **LOCAL** (#165). (`scope: "integration"` itself is unchanged — the per-union re-verify tag, SSOT §4.3.)
- [ ] Every `IFoo`/`FooImpl` pair has a wiring task + a composition-root guardrail that drives the REAL assembler (no seam-injecting guardrail; whole-suite green does not stand in for wiring) (#120).
- [ ] (#382) The **seam ledger** was audited. The Step 7.4 report carries the bolded `Seam ledger (#382)` **HEADING** — an ABSENT heading is a BLOCKER (the Step 4 analysis never ran), the zero-row form (`_No in-process seam is substituted by this breakdown's tests._`) is a CLAIM that gets checked rather than an absence, and a ledger **not produced to this pass** is an unchecked-gap line in the report, never a finding. Every `bucket` cell is one of `N1` `N2` `N3` `N4` `E` `C` `U`, and an **N classification off the four-item enumeration is REJECTED** (clock / randomness / ambient env reader / wait primitive) — including the **N4 trap**: if the substitute contains a DECISION it is **C**, not N4 (`RetryLoop → IDelay` is N4; `RetryLoop → ITransientBackoff` is C). Every **E**/**C** row's proof sits at a **recomputed T\*** (a later placement is a finding even when the proof exists and passes, and must name T\*); every `proof` path is plan-folder-relative with its task segment agreeing with the `T*` cell; **no E row invokes the construction bound** (D11 — the #120(b) degradation is bucket **C** only, and a degraded C row names the constructor chain that forced it); every **U** row names a receiving task (or, under waves, a receiving wave) that actually exists. The ledger was **re-derived from the folder** — every `author-tests-*` task faking an in-process seam the run drives has a ROW, and process seams (child process, CLI, socket, HTTP, DB, filesystem) have none. Severity: BLOCKER when the un-proven seam is a composition-root/production path, WEAK when only a thin terminal join-check covers it.
- [ ] (#382) Where a real-seam proof EXISTS, its shape holds: a **test** (rung 1 — no rung-3 source-grep form), asserting an **effect only the production implementation emits** (a recording double / call count / `Verify`, or "the collaborator was called", IS the passing-but-blind shape), at `scope: "local"` with the key omitted (`scope: "integration"` here is the #250 mistake), with a #155-real RED that COMPILES. Every terminal composition proof (the #120 wiring task and each `<plan>/guardrails/` guardrail) names in its `# catches:` a defect that **survives every upstream real-seam proof passing** — one that can name none is redundant (propose deleting it), and one whose only defect is *"this seam is exercised for the first time here"* means a ledger row is MIS-PLACED, fixed upstream and never by a wider `writeScope` here; no row's proof is emitted twice (at T\* and again in the sink). A correct real-seam test is **NOT** a #120 violation (D12 — same verb, different slot: #120 forbids injecting into the ASSEMBLER's slot, #382 requires injecting into the COMPONENT's own constructor; one test doing both is two tests). The #378 boundary held both ways: #382 added no rule keyed on `writeScope` / `action.maxTurns` / `dependsOn` (GR2042's fields, exclusively), and #378 added no rule about what a guardrail PROVES.
- [ ] (#382) Probe B **operator 20** was applied, or explicitly recorded as NOT RUN with its reason: satisfy a real-seam / composition-root `--filter` with a test that CONSTRUCTS THE FAKE under the same real-sounding name — GREEN means the filter selects a name, not a behaviour (BLOCKER; the fix is the assertion requirement, not a narrower filter). It is INAPPLICABLE on a greenfield first review (the test does not exist yet) and is then reported **not run** — never as passed, and never absorbed into a blanket "Probe B applied".
- [ ] No task carries the **structural over-scope fingerprint** (GR2042): a `maxTurns`-near-ceiling + `writeScope` ≥ ~4 co-occurrence, `writeScope` ≥ ~6, or a `dependsOn` fan-in ≥ ~5 with a multi-file `writeScope` — the fan-in-sink / composition-root-wiring archetype. BLOCKER with the proposed split (one task per collaborator wiring; composition-root proof isolated to a thin sink); resolve the `guardrails validate` GR2042 WARN, don't merely re-report it (#378). On a fan-in sink, test the relocation remedy FIRST (#382): narrowing `writeScope` yields N small tasks that still hold the first exercise of every real path, so the concentration survives the split — but report the two findings separately, from their own evidence, since neither issue's mechanism may read the other's fields.
- [ ] Every dispatch task routing ≥2 enum values to ≥2 concrete types whose dispatch tests use seam-injection has a per-pairing proximity check binding `<EnumValue>` to `<ConcreteType>` (WEAK if missing; BLOCKER if the only concrete check is `tests-pass`); omitted only when the tests assert the concrete TYPE NAME (#158).
- [ ] Every forbidden-keyword scan over a source file strips comments before matching; no task both documents banned constructs in a header comment AND greps for them comment-blind (#97, #98).
- [ ] Every derived-corpus task asserts input→output coverage + per-output substance floor + index completeness (`produced ⊆ indexed`) + ingestion lower bound, named as lower bounds (no judge alone for faithfulness) (#99).
- [ ] Every `scope:"integration"` guardrail is union-safe (passes the "would this pass on a partial merge with a downstream task unsettled?" test, checked against EVERY union point plan-wide — including a merge by a completely unrelated parallel sibling, not just unions structurally upstream of the guardrail's own task in the DAG, #250); terminal postconditions live in a `local` guardrail on the sink (#125).
- [ ] Every set of ≥2 tasks with OVERLAPPING `writeScope`s on a shared file has ≥1 `scope:"integration"` guardrail asserting the shared-file UNION invariant — the union re-verify is integration-set-only (#132), so a sibling's `local`-only coverage is NOT re-run at the union; flag WEAK if missing. When the shared file is a CODE file and both siblings could ADD a type/member definition, that union guardrail also carries a **duplicate-definition count check** (`[regex]::Matches($content,'class\s+<Name>').Count -gt 1`, union-safe/conditional) — a 3-way merge keeps both copies with no conflict marker (CS0101), the #175 residual; WEAK if absent.
- [ ] Every task whose verification runs `dotnet build`/`dotnet test` was checked for a **transitive compilation dependency** (#176): an ancestor test-author task's `.cs` file referencing a type produced by a task NOT in the verifying task's ancestor set is a missing edge — add the producing task to `dependsOn` (WEAK, or BLOCKER when the compile failure is certain).
- [ ] Every code-change task whose `tests-pass` guardrail uses a **broad name-substring `--filter`** was checked for an **orphaned pre-existing golden** (#193 — the runtime analogue of #176): the filter sweeps in a PRE-EXISTING test (not authored by an ancestor) whose pinned literal/golden/snapshot the task's change plausibly alters, AND that test+golden is outside the task's `writeScope` AND no other task owns re-baselining it → **BLOCKER** (the task must pass a test it can't edit → `needsHuman` loop). Fix: narrow the `--filter` to the task's own tests, widen the `writeScope` to own the golden+test, or add a dedicated re-baseline ancestor task. WEAK when the collision is plausible but not certain.
- [ ] Every guardrail that asserts a test suite PASSES (`tests-pass`/`all-tests-pass`/`specific-tests-pass`, or a production-seam driver) re-emits the failure DETAIL (assertion/exception lines) at the END of stdout so it reaches the harness retry tail — not just the `[FAIL] <name>` summary default `dotnet test` leaves (#179); absence is WEAK (degrades retry feedback, costs attempts). No such guardrail carries a QUIET flag on its TEST command (`-v q`/`-v quiet` on `dotnet test`): measured, it suppresses the entire `Error Message:`/`Expected:`/`Actual:`/`Stack Trace:` block, so even a correct re-emit tails out test names only — WEAK, and quiet belongs on `dotnet build`. The INVERSE `tests-fail-on-stubs` / `tests-fail-on-current-code` checks (non-zero exit = success) do NOT re-emit and must not be flagged.
- [ ] Every action prompt that **excludes** a scenario/keyword ("do NOT include `CommanderRest`") has a matching **negative-assertion** guardrail (`if ($content -match "<keyword>") { … exit 1 }`, fail-on-present) verifying the keyword is ABSENT (#176); absence is WEAK (BLOCKER when the excluded scenario traps a downstream compile). GR2026 correctly stays silent on the negative assertion's keyword (post-#177, §4.4) — a GR2026 warning there is the false positive, not a reason to delete the guardrail.
- [ ] Every explicit **"do NOT …"** statement in a task's action prompt has a matching structural guardrail (a negative assertion, #176, for an excluded keyword/scenario; a regex-lock on load-bearing text surviving verbatim, or a count/forbidden-construct scan for a banned approach/shape) — or the breakdown report states explicitly that the forbidden behavior is not structurally checkable. WEAK when the prohibition is merely uncovered by an otherwise-deterministic suite; **BLOCKER** when the task's OTHER guardrail is empirical/statistical (a "run N times, assert it always passes" flake check) and the forbidden shortcut would make THAT guardrail EASIER to pass rather than harder — the perverse-incentive case (#221).
- [ ] Every task whose prompt references an **earlier-wave sibling's** code was checked for a stale line-number pointer and an unhedged "here's how it currently works" claim (#203/#204): a cited line number into a file the earlier task will still modify is WEAK/BLOCKER (durable marker instead); an unhedged architecture claim about the sibling's not-yet-run implementation is WEAK/BLOCKER (caveat it as authoring-time state, verify before relying on it). Cross-check the paired `maxTurns: 75` bump (Step 4a's fourth archetype) — flag a **half-applied fix** if only one of the two companion rules was applied.
- [ ] Every `scope:"integration"` union guardrail's expected-contribution tokens are each produced by a task in the integration task's ANCESTOR set (a directed path producer → fan-in); a token whose only producer is a disconnected leaf / side branch is WEAK ("if task `<N>` is later removed, this guardrail will fail spuriously — add a DAG edge or drop the check") (#159).
- [ ] Every task ran through the over-size split-trigger; any task bundling multiple deliverables / wide blast radius / 1:1-to-a-milestone / expensive-retry is flagged WEAK with a proposed split (#111).
<!-- BEGIN ADDED CHECKS #74/#75/#76/#96 -->
- [ ] Every "task A calls `B.Method()`" guardrail anchors on BOTH the type reference and the dotted call (`\.Method\s*\(`), never a bare method-name grep (#76).
- [ ] Every "extract a library that must write through `IInterface`" task has a forbidden-direct-call scan of the library folder — comment-stripped and dot-anchored, never a bare-name grep that false-REDs on a comment (#74).
- [ ] Every test-author task whose prompt enumerates ≥3 behaviors has a covers-key-behaviors check (2–3 distinctive terms, scoped to the one test file), named as a lower bound, with the unchecked behaviors reported (#75).
- [ ] Every producer↔consumer derived-name seam has a consumer-driven integration guardrail on a both-sides-present task that drives the real lookup for EVERY item and asserts 200 + a per-item marker — union-safe, no hard-coded name copy, no sampling (#96).
<!-- END ADDED CHECKS #74/#75/#76/#96 -->
- [ ] Every guardrail that pattern-matches/regexes a tool's PRINTED console output (not just its exit code or a file it wrote) was verified by actually RUNNING that tool once against the real repo/workspace and checking the pattern against the real output — not just reasoning about whether the regex looks plausible; a pattern shown to never match the real output is a BLOCKER (the guardrail fails unconditionally, dead-ending every attempt at `needsHuman`), a fragile-but-currently-matching format assumption is WEAK. Does not apply to exit-code-only / file-existence / diff checks — there is no output-format assumption to verify there (#248).
- [ ] Every TASK-LEVEL guardrail running a test filter was cross-referenced against the DAG's dependency EDGES — not read in isolation, which is why #248/#302 (both single-guardrail probes) cannot see this (#455). The scope×edges table was written out with column 4 RESOLVED (the filter run for a real `Total:` count where the classes exist, or resolved on paper against prompt-PINNED class names — prompts that do not pin their test class names are themselves a BLOCKER, since no one can then tell what any filter selects), and BOTH directions asked: **forward** — a filter selecting tests only a DOWNSTREAM task can make green is a **BLOCKER** (deadlock; `validate` and `graph --check` both PASS, the cycle is task↔sibling-test-corpus, not task↔task); **inverse** — a red check a SIBLING's tests can satisfy is a **BLOCKER** (tautology; the #155 red proof degraded to merge-order luck). Every task carrying the shape is flagged, not only the one that failed (merge timing decides which mode bites). A concurrent sibling with no edge either way is **nondeterministic**, not exempt — WEAK, BLOCKER same-wave. Fix: `--filter "Category=<PlanTrait>&FullyQualifiedName~<ThisTaskPairsTestClass>"` on both halves of the pair (class term mandatory, trait an optional conjunct; the trait ALONE lives only in the #181 preflight's `!=` exclusion). Sub-checks: the class substring is discriminating (`--list-tests` enumerates what it really selects; `~Dispatch` also selects `DispatchRouterTests`), and each narrowed filter carries a zero-match guard that can actually fire — BLOCKER if keyed on the `No test matches…` string (suppressed by `-v q`), on `Total:` (counts `[Skip]`ped tests), or on English tokens without pinning `$env:DOTNET_CLI_UI_LANGUAGE='en'` (the summary is localized); and the guard's ORDER matches its polarity (forward: exit-code first; inverse: guard first).
- [ ] Every `.sh`/`.ps1`/`.py` guardrail that is runnable-at-author-time (idempotent, input in-repo or hand-synthesizable, no live dependency) was smoke-tested by EXECUTING the guardrail SCRIPT itself against a hand-written VALID sample (exit 0) AND a deliberately INVALID one (non-zero) — `bash -n`/`sh -n` treated as a cheap first pass only; the highest-value target (a guardrail that renders/executes the task's own not-yet-authored output) was run against a synthesized sample. FAILS the valid sample or PASSES the invalid sample = BLOCKER; runnable-but-unrun = WEAK; not-runnable-at-author-time (live service / built binary / merged HEAD) = syntax pass + honest report deferral, never a block. Distinct from #248 (which runs the underlying TOOL, not the guardrail script) (#302).
- [ ] (#254) A waved plan was reviewed WAVE-BY-WAVE (each wave a mini-plan): the §2 adversarial probes ran per task within each wave, and each wave's entry/exit gates got the four-folder treatment. No cross-wave `dependsOn` edge (GR2034 — a wave-2 dependency on a wave-1 artifact is the wave-2 ENTRY gate, not an edge; BLOCKER if present). Every waved-plan prompt's state fragment is keyed by the WAVE-QUALIFIED id `<waveDir>/<taskFolder>` (header + example + state-output guardrail index agree; a bare/wrong-wave key is a BLOCKER, the #164 loop one level up).
- [ ] (#254) Each wave ≥ 2 has a POSITIVE, positive-monotone-safe ENTRY gate ("prior wave's outputs materialized"; missing = WEAK, negative-polarity = BLOCKER). Each multi-leaf/fan-in wave's EXIT gate satisfies GR2028 (≥1 real integration re-run); every INTERMEDIATE wave's exit gate keeps whole-build/whole-suite LOCAL and any `scope:"integration"` guardrail union-safe/conditional (a whole-suite marked `scope:"integration"` in an intermediate wave = BLOCKER, #125); only the LAST wave's exit gate carries a whole-suite LOCAL `tests-pass`. A declared-but-empty JIT stub wave is NOT flagged as missing tasks; the JIT workflow for it is documented in the breakdown report.
<!-- BEGIN ADDED CHECKS #468/#470 -->
- [ ] (#468) Every guardrail asserting a property of IMPLEMENTATION SOURCE was run through the demotion question — behaviour → a test (or an AGREEMENT property test for "X must USE Y"), source-shape only for a structural fact with no runtime proxy. A behavioural claim carried by a regex is a finding NAMING the test that should replace it (BLOCKER when a correct implementation can be written that it rejects, WEAK when it merely certifies vocabulary), and a surviving source-shape check with no report line saying WHY no test could carry it is itself a finding. Legitimate structural facts — build-descriptor registration, cross-module reference chains, entry-point wiring, the #120 grep fallback, #176 negative assertions — are NOT flagged. When ≥2 Probe B operators go green against one source-shape guardrail, the finding is the ARCHETYPE, not the clause: recommend the demotion rather than a fourth round of clause repair (three rounds did not converge).
- [ ] (#468) Every source-shape guardrail over CODE ships a committed `.valid`/`.invalid` sample pair in a `tasks/<id>/samples/` sibling — NEVER inside `guardrails/`/`preflights/`, where the loader would treat the fixture as a guardrail (counts toward GR2003, executed at run time, or GR2027) — and BOTH halves were re-run in this pass — the valid half especially, being the only half that can expose a clause that never matches, a false-red on legitimate brace style, or a case mismatch. The valid sample is COMPLETE, not a fragment. DOCUMENTATION deliverables are exempt from the pair (no meaningful invalid sample exists) but NOT from the PRECEDENT check, and the exemption is named in the report rather than taken silently. No guardrail asserts an executed-test COUNT as an adequacy floor (theory rows, not behaviours — use a behaviour manifest over discovered test NAMES); the #455 zero-match guard is not that and is not flagged.
- [ ] (#470) Probe C ran: every required-present literal was reconciled against every forbid-present pattern IN THE SAME FILE (a hit is unsatisfiable-by-construction → BLOCKER), and every banned token against the task's own `action.prompt.md` (a hit invites the agent to write what reds it → WEAK). Every forbidden scan runs over STRIPPED source — comments AND string literals — and is anchored on a USE, not a mention. Not confused with GR2026/#177, which is the opposite polarity (REQUIRES a token the prompt never mentions).
<!-- END ADDED CHECKS #468/#470 -->
- [ ] (#478) Probe **A₂** ran: every **required-present** clause and every **numeric floor** of every guardrail has a BASELINE verdict — resolved by the fast path (an accumulator failure list Probe A actually printed) or by a hand census (`Select-String` of the clause's own pattern against the clause's own subject, case-sensitive iff the operator was `-cmatch`/`-cnotmatch`). A clause already satisfied on the baseline is a **BLOCKER** (the task is passable without delivering it, and it certifies nothing for the life of the plan) unless the script DECLARES its exception — positive-baseline/wave-entry preflight, `tests-untouched` regression, the "if X is present" half of a union-safe conditional, or a ratcheting behaviour manifest. **Forbidden-present clauses are NOT censused** — a ban green on arrival is a correct ban, and a ban RED on arrival is Probe C's #470 collision. Every count a script DECLARES was re-measured, never read (a `# … appears nowhere else` comment sat over a token appearing twice in that exact file); a declared count disagreeing with the census is a BLOCKER. Clauses left unmeasured (runtime-composed pattern, subject absent from the baseline, non-pattern clause, ungated cost stage) are NAMED in the report as NOT RUN, never absorbed into a red Probe A. A₂ does not subsume Probe B and B does not subsume A₂ — the motivating clause was both pre-satisfied AND gameable by a one-const append.
- [ ] No fix applied without explicit approval; human-authored guardrails called out.
- [ ] The review left durable evidence (#366): the plan hash was obtained via `guardrails plan-hash <folder>` (the skill can't compute it), a review report — the Step 6 findings table + verdict + an embedded `Plan-Definition-Hash: sha256:…` line (F2a) — was written under the hash-EXCLUDED `<plan>/state/reviews/`, and the marker was stamped with `guardrails mark-reviewed <folder> --evidence <report>` (recording `attestation.source: review-artifact`, or a downgrade to `bare` on an F2 failure) — clearing the GR2025 nudge (#79/#131), NOT run while a BLOCKER remained open. The recorded evidence class (`review-artifact` / `bare` / `machine`, read-time `legacy`) is for AUDIT, not a gate — the marker is only as strong as write-access to the plan folder, and the harness never writes it on a human's behalf. For a waved plan, run the flow per-wave against `<folder>/wave-NN-<slug>` (its own `state/reviews/` + hash) after a single-wave JIT review, or whole-plan after a wave-by-wave pass (#254).
