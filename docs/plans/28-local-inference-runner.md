# 28 — A local-inference `IPromptRunner`: the `openai-compat` runner class (#223)

**Status:** reviewed-quality draft, for inline review as a draft PR before any breakdown.
**Issue:** #223. **Parent epic:** #201 (model tiering). **Fills:** the seam named in
`docs/plans/17-model-tiering.md` §4.4. **Depends on:** Stage 1 (`kind` discriminator, shipped) and
Stage 2 (the tier resolver, shipped).

> **Revision note.** A first draft of this plan was put through an adversarial pass by a
> non-authoring agent, which found two blockers and nine lesser defects — including a self-
> contradiction across §4 and §9, a validate-time gate that structurally cannot see the pin it
> forbids, and a containment claim resting on the wrong function. §11 records what survived. The
> sections most changed are §3.2, §3.5, §5.3, §6.3 and §9; the business case in §3.2 now carries its
> own acceptances, which it did not.

---

## 1. The gap

```
$ grep -n "PromptRunnerKind.Claude =>" src/Guardrails.Core/Prompts/PromptRunnerRegistry.cs
62:            PromptRunnerKind.Claude => new ClaudePromptRunner(runner.Name, runner.Command, processRunner),
63:            _ => throw new InvalidOperationException(UnimplementedKindMessage(runner))
```

One arm. Everything else throws. `PromptRunnerKinds.Implemented` is a one-element list
(`PromptRunnerConfig.cs:234`), and `PlanValidator.ValidatePromptRunnerKindsImplemented`
(`PlanValidator.cs:301-319`) turns any other declared kind into a GR2044 error before the run starts.

So the model-tiering epic delivered the whole routing apparatus — three axes, a candidacy predicate,
a resolver, per-attempt provenance, a per-tier spend line — and there is exactly **one place it can
route to**. Tiering is not the gap. The gap is that `Local` and `OpenAiCompat` are names in an enum
(`PromptRunnerConfig.cs:204, 214`) with no class behind them, and a Mac Studio arriving in
September 2026 has nowhere to plug in.

The seam is already designed and this plan does not redesign it. `IPromptRunner`
(`PromptInvocation.cs:167-174`) is one method. Adding a provider is "a new class plus one arm of that
switch" (SSOT §9). What this plan settles is the part the seam deliberately left open: **what a
chat-completion endpoint can and cannot honestly be asked to do inside this harness.**

---

## 2. The shape mismatch, stated before anything is decided

`claude -p` is an **agent**. It reads files, edits them, runs commands, loops over tool calls, and
reports cost and turns. An OpenAI-compatible endpoint is a **chat completion**: text in, text out.

The harness's demands on a prompt child are agent-shaped throughout:

| What the harness requires | Where | What a chat endpoint gives you |
|---|---|---|
| Write a state fragment to an absolute path | `PromptComposer.cs:135-156` | nothing — no file write |
| Write a verdict JSON to an absolute path | `PromptComposer.cs:350-355`; read at `GuardrailRunner.cs:246` | nothing |
| Read the artifacts under judgement | every prompt guardrail's body | nothing |
| Run builds/tests, edit code | every prompt **action** | nothing |
| Containment inside a segment worktree | `--settings <hookfile>` spliced at `ClaudePromptRunner.cs:422` | **nothing — and the flag is silently ignored** |
| A stream that keeps producing | the #504 stall watchdog, `ClaudePromptRunner.cs:107, 114, 178` | nothing, on a blocking POST |
| `usage`, `total_cost_usd`, `num_turns` | `ClaudeStreamParser.cs:172-174` | `usage` yes; cost no; turns are yours to count |

Row 5 is the one that shapes this design.

```csharp
// GuardrailRunner.cs:170, :215-219   (ActionRunner.cs:171-175 is the twin)
bool isWorktreeMode = !string.IsNullOrEmpty(worktreeRoot);
...
if (isWorktreeMode)
{
    string guardrailSettingsPath = WorktreeContainmentHook.WriteHookFiles(…);
    settings = settings with { ExtraArgs = [.. settings.ExtraArgs, "--settings", guardrailSettingsPath] };
}
```

The splice is gated on `isWorktreeMode` **alone**. It reads neither the resolved judge's kind nor its
runner name, though `judgeBlock` is in scope four lines earlier at `:211`. Only
`ClaudePromptRunner` splices `ExtraArgs` into an argv. **So a second runner receives the containment
hook and drops it on the floor, and nothing anywhere says so.** The inner `WriteScopeCheck` is
post-hoc and structurally blind to writes outside the segment — which is exactly why the outer hook
exists (`WorktreeContainmentHook.cs:12-16`). A boundary that silently does not apply is this repo's
named recurring defect, arriving in the highest-stakes place it could.

**This plan therefore makes that splice kind-aware** (§3.6). It is not incidental cleanup: without
it, the flagship deliverable — a prompt guardrail pinned to a local block — is broken in worktree
mode, which is the default whenever `maxParallelism > 1`.

---

## 3. Scope of v1 — what is decided

### 3.1 One kind: `openai-compat`. `local` stays reserved.

v1 implements `PromptRunnerKind.OpenAiCompat` and nothing else. `Local`, `Codex` and `OpenRouter`
remain reserved names and remain GR2044 errors.

**MLX is a named v1 target, and it is the one the hardware makes first-class.** The maintainer reports
materially better throughput from MLX builds than from llama.cpp builds of the *same* model on Apple
silicon, and the Mac Studio this epic is aimed at is exactly where that difference is collected. MLX
is served over the same protocol — `mlx_lm.server`, or LM Studio's MLX engine — so it needs **no new
kind and no new config key**: an `endpoint` and a `model`, like every other engine here.

That it drops in is a property of the kind being named after the **protocol** rather than the engine,
which is the whole reason §3.1 exists. But "it drops in" is not the same as "it is supported", and
the difference is the rest of this plan: an engine is supported when its dialect is *probed* (§7,
§8), its failures carry *its own* remedy text (§6.2), and its capabilities are *verified rather than
assumed* (§7's tool-capability probe). MLX was absent from the first draft of this plan and from
#223's own title, so all three of those were written against Ollama alone. They are corrected below —
and the general rule they now follow is that **no engine name may appear in a code path**, only in
operator-facing text and in the opt-in dialect check.

**`local` is not implemented, and it should not be** — it would be the same wire protocol under a
second name, and the enum's own doc says `openai-compat` is one kind *because* Ollama, llama.cpp,
LM Studio and vLLM share that protocol (`PromptRunnerConfig.cs:206-214`) — MLX joins that list on the
same grounds. The tiering DoR records
`local` as an accident: *"`local` was missing from this list until 2026-08-15 — Stage 1 shipped it as
a fourth reserved token and Stage 1.5 kept it rather than make an unrequested breaking change"*
(`17-model-tiering.md:386-390`).

**Rejected alternative — give `local` a real meaning: "an `openai-compat` endpoint asserted to be
loopback", fixing D21a's complaint that `openai-compat` cannot tell a local box from a cloud API.**
Rejected because `strength` already carries that fact, and D21a's own settled resolution says so:
*"A user who dislikes the guess overrides it by declaring `strength` — which is the entire point of
the axis existing"* (`17-model-tiering.md:367-369`). A second kind carrying a duplicate of
`strength`'s meaning would give the one candidacy predicate two inputs that can disagree.

**What v1 does change about `local`:** GR2044's message for it names `openai-compat` as the spelling
the operator almost certainly wants. One sentence, and it converts a dead end into a redirect.

### 3.2 The actor-vs-verifier question — settled: **v1 is not an actor**

> **The `openai-compat` runner serves the `Guardrail` and `Advisory` roles only. It offers a fixed,
> harness-implemented, read-only tool set — named `Read`, `Glob` and `Grep` — and no write tool, no
> shell tool, and no way to be selected for a task action. It refuses an `Action` invocation loudly
> rather than serving it.**

Three reasons, in descending order of force.

**(a) Containment does not survive the crossing.** §2 row 5. A write-capable local actor in worktree
mode would run with the outer boundary absent, and the run would look completely normal. Every other
consequence of getting this wrong is recoverable; this one produces a green run over a tree the
harness cannot account for.

**(b) The remaining 80% is not tools, it is the machinery around them.** Write and shell would also
require: the permission-denial → `BlockedWritePaths` → `PermissionWallTracker` feedback loop
(`PromptInvocation.cs:133-143`), the harness-injected salvage grant
(`ClaudePromptRunner.cs:438, 453-465`), `--add-dir` reach outside cwd, the `stagingOutputs` and
`needsHarnessWrite` escape hatches, and a .NET re-expression of the containment rule. That is Claude
Code, rebuilt inside `Guardrails.Core`.

**(c) It is the profile the harness already defines for verifiers — down to the tool names.**
`guardrailOverrides` is documented as *"the tighter, read-mostly verifier profile"*
(`PromptRunnerConfig.cs:100-105`). And the harness's own supervisory prompts do not merely *want*
that profile, they **name it in prose**:

> *"You are a read-only supervisor. Your ONLY tools are Read, Glob and Grep — you have no Bash and no
> write tools…"* — `Overwatch.cs:524-525`
>
> *"You are read-only: your ONLY tools are Read, Glob and Grep. No Bash, no writes."* —
> `NeedsHumanTriage.cs:219`

Those strings are **harness-owned and stay verbatim**. That is why this runner's tools are named
`Read`, `Glob` and `Grep` and not `read_file` / `list_files` / `grep`: a tool schema whose names
disagree with the prompt text the harness already sends is a contradiction handed to the weakest
model in the system. Matching the existing vocabulary costs nothing and is the only spelling that
makes "zero new mechanism" true.

### 3.3 The business case, with its limits stated

`SchedulerFactory` resolves four reserved profiles by NAME from the registry —
**`overwatch`**, **`ai-triage`**, **`ai-merge`**, **`breakdown`** (`SchedulerFactory.cs:234, 242,
251, 260`; shared resolution at `:293-301`). Note the two `ai-` prefixes: a block named `triage` or
`merge` matches nothing, and `ResolveReservedRunner` then falls back to the default runner **with no
diagnostic at all** — a silent no-op of exactly the kind this document exists to prevent. §7 adds a
warning for it.

Two profiles are `Advisory`-role and are v1's payload:

| Declaring | Routes to local | Not routed |
|---|---|---|
| `"overwatch": { "kind": "openai-compat", … }` | the overwatcher diagnose **and** the criticality assessment (`CriticalityJudge` is constructed with the overwatch runner, `SchedulerFactory.cs:202`) | — |
| `"ai-triage": { … }` | the terminal needs-human triage | — |

**The first draft claimed one block routed all three. It routes two;** triage is a separate profile.

**And "zero new mechanism" was false in a second way.** Both advertised consumers parse the model's
final message **strictly**:

- `OverwatchProposal.cs:40` — `JsonDocument.Parse(resultText)`, then requires an object with a
  string `diagnosis`.
- `NeedsHumanTriage`'s sidecar writer — the same shape.

The whole final message must be a bare JSON object: no prose, no fence. Claude complies because it is
a strong instruction-follower; a 30B local model routinely does not. The failure is advisory-safe —
`RecordNoVerdict` fires and nothing changes a verdict — but **the payoff would be zero**: three model
calls that reliably produce nothing.

**So the lenient extractor is not a verdict-path detail; it is shared.** §6.4's extractor (last
fenced ```json block, else the last top-level object) becomes **one** `PromptJsonExtractor` used by
three consumers: the verdict transcription, `OverwatchProposal.TryParse`, and the triage sidecar. It
only ever widens what parses on paths that currently fail closed, so it cannot make a Claude run
worse. Without it, §3.3 is a claim rather than a deliverable — and it now carries its own acceptances
in §9.

### 3.4 The role seam — a new required field on `PromptInvocation`

The runner cannot refuse an `Action` invocation without being told which it got, and today
`PromptInvocation` does not carry that fact.

```csharp
/// <summary>What this prompt is FOR. Set by the harness at every call site; a runner may refuse a
/// role its class cannot honestly serve (SSOT §9).</summary>
public required PromptRole Role { get; init; }

public enum PromptRole
{
    /// <summary>Produces work: writes files other than its own verdict, or runs commands.</summary>
    Action,
    /// <summary>Renders a verdict on work. Reads; writes only the verdict file.</summary>
    Guardrail,
    /// <summary>Renders an opinion the harness may not treat as a verdict. Reads; writes nothing.</summary>
    Advisory
}
```

**The classification rule, so a future call site does not have to guess:** *does this prompt write
anything other than its own verdict file?* Yes ⇒ `Action`. No, and its output is a pass/fail ⇒
`Guardrail`. No, and its output is advice ⇒ `Advisory`.

There are **seven** construction sites, and the seventh is the one a careless sweep misses —
`CriticalityJudge`'s is target-typed (`=> new()`), so a grep for `new PromptInvocation` finds six:

| Site | Role | Why |
|---|---|---|
| `ActionRunner.cs:185` | `Action` | the task action itself |
| `WaveBreakdownInvoker.cs:178` | `Action` | authors a task folder (the `breakdown` profile; `InitialBreakdownInvoker` and `BreakdownCommand` reach the runner through here, not directly) |
| `AiMergeResolver.cs:126` | `Action` | writes `GUARDRAILS_MERGE_OUT` (the `ai-merge` profile) |
| `GuardrailRunner.cs:222` | `Guardrail` | the judge |
| `Overwatch.cs:457` | `Advisory` | advisory-never-gates |
| `NeedsHumanTriage.cs:91` | `Advisory` | advisory-never-gates |
| `CriticalityJudge.cs:325` | `Advisory` | **target-typed `new()`** — no tools, no env, no cwd |

**`required`, not defaulted, and that is the whole point.** A default would let a new call site
silently acquire the permissive value. The compiler is the gate.

**The cost of `required`, which the first draft did not price.** `PromptInvocation` is a public record
in `Guardrails.Core`; adding a required member is a **source-breaking change for every test fixture
that constructs one**. `tests/**` is therefore in stage 1's `filesTouched` (§13) — otherwise stage 1's
"no behaviour change" deliverable cannot even build.

**Rejected alternative — infer the role from the environment (`GUARDRAILS_VERDICT_OUT` present ⇒
guardrail).** This repo has already run that experiment and it failed. `FakeClaudePlanBuilder`'s
remarks record it: the fixture branched on the presence of those keys, an enclosing run's inherited
`GUARDRAILS_VERDICT_OUT` made a legitimate action invocation take the verdict branch, and *"There was
no fixture-side fix … The fix had to be — and now is — production-side"* (#442,
`ProcessRunner.ApplyEnvironment`). Presence is a fact about inheritance, not about intent.

`ClaudePromptRunner` ignores `Role` entirely. No Claude run changes by one byte.

### 3.5 Role capability is a fact about the BUILD, not a config key

`PromptRunnerKinds` gains two statement-of-fact members beside `Implemented` and `ModelEnumerable`,
in the same shape and same file:

```csharp
public static IReadOnlySet<PromptRole> ServesRoles(PromptRunnerKind kind);
//   Claude       => { Action, Guardrail, Advisory }
//   OpenAiCompat => { Guardrail, Advisory }

/// <summary>True when this kind's runner is an agent whose file writes need the §9.4 containment hook.</summary>
public static bool NeedsContainmentHook(PromptRunnerKind kind);   // Claude => true, OpenAiCompat => false
```

**`ServesRoles` must be pinned by CONSTRUCTION, not by reflection.** The obvious test — read the same
field the runner reads — is an echo of itself. The honest pin is: for each (kind × role), construct
the real runner and assert it accepts or refuses.

**Rejected alternative — a `roles: ["verifier"]` key on the block.** A config key invites an operator
to *declare* a capability the build does not have. That is the same fabrication `providers init` is
forbidden from committing: *"A model identifier in the registry may only come from a provider that
reported it or a human who typed it"* (`17-model-tiering.md:555-560`). The operator declares
**preference** (`routing`, `strength`); the assembly declares **capability**.

### 3.6 The containment splice becomes kind-aware

`GuardrailRunner.cs:215-219` and `ActionRunner.cs:171-175` gain one condition:
`isWorktreeMode && PromptRunnerKinds.NeedsContainmentHook(block.Kind)`.

This is **not** a weakening. The hook exists to police `Write`/`Edit`/`MultiEdit`/`NotebookEdit`/`Bash`
tool calls (SSOT §9.4). A runner that offers none of them has nothing for it to police; generating a
Claude `settings.json` and passing it as a CLI flag to an HTTP client is not containment, it is
litter. **With the condition, the runner's own `--settings` refusal (§4) becomes a true backstop** —
reachable only if the splice and the capability list disagree, which is a harness bug worth throwing
on. Without the condition, the refusal fires on every worktree-mode pinned judge, and the flagship
deliverable is broken in the default execution mode.

### 3.7 Reachability: pin only. No `routing`. And the gate is incomplete — deliberately.

**GR2066 (error) fires when an `openai-compat` block is reachable for an `Action`.** Four
manifest-visible routes, all four required:

1. it declares `routing` — it would become a tier candidate for actors;
2. it is the **effective default** — the `default` pointer **or the sole declared runner**, because
   `PromptRunnerRegistry.ResolveDefault` treats those identically (`:145-153`) and the
   reserved-profile fallback rides on it (§3.3);
3. it is named by a task's `action.runner`;
4. it is declared under a reserved `Action`-role profile name — `ai-merge` or `breakdown`.

Route 2 fires on the most natural misconfiguration there is: a plan with a single local runner.

**There is a fifth route, and the validator cannot see it.**

```csharp
// ActionRunner.cs:211-213
PromptResult result = await registry.Resolve(
        route?.Runner?.Name ?? task.Action.Runner ?? promptFile.Frontmatter.Runner)
```

An action prompt with `runner: local-qwen` in its YAML frontmatter and nothing in `task.json` reaches
the block for an `Action`. And `PlanLoader.ApplyPromptFrontmatter` (`:1266`, `:1442-1443`) folds only
`scope` and `tier`, and **only onto guardrails** — a prompt *action*'s frontmatter is never folded
onto the plan definition at all, so `PlanValidator` has nothing to read.

**Two ways out, and this plan takes the first:**

- **Chosen — make the fifth route visible.** Extract `PlanLoader`'s frontmatter reader into a shared
  helper and have the loader fold an action prompt's `runner` onto the task definition, purely so
  validation can see it. `ActionRunner`'s resolution chain is untouched, so precedence does not move.
  **Extract, never copy:** a fourth independent frontmatter parser is how the two sites drift.
- **Rejected — concede the route to the runtime backstop.** It would leave the plan claiming
  *"the same gate/backstop pair GR2044 has"* while the single most likely author mistake — pinning a
  runner in the prompt file, where `runner:` is already a documented key — dies mid-DAG instead of at
  validate.

With route 5 closed, the block is reachable by exactly two human acts: **a judge guardrail's
frontmatter `runner:` pin** (§9.6 rule 1 honours it: an explicit pin *"names a block and bypasses
selection entirely"* and *"stops every rule below it, the floor in item 4 included"*), and **naming
the block as an `Advisory` reserved profile** — `overwatch` or `ai-triage`.

This is the costly floor's logic applied to a different scarcity: *the floor constrains what the
harness may choose, never what a human may assign.* We do not yet have the measurement that would
justify the harness choosing a local model on its own.

---

## 4. The configuration surface

```jsonc
"local-qwen": {
  "kind": "openai-compat",
  "endpoint": "http://127.0.0.1:11434/v1",  // REQUIRED for this kind. Absolute http/https base URL.
  "model": "qwen3-coder:30b",               // REQUIRED for this kind (no CLI default to fall back to).
  "contextTokens": 32768,                   // REQUIRED for this kind. Integer >= 1. See §6.1.
  "apiKeyEnv": "LOCAL_INFERENCE_KEY",       // OPTIONAL. The NAME of an env var holding a bearer token.
  "engine": "ollama",                       // OPTIONAL. ollama | llama.cpp | mlx | lm-studio | vllm.
                                            // OPERATOR-FACING TEXT ONLY (§6.2): selects the remedy
                                            // sentence in an error. It never selects a code path and
                                            // never changes a request — see §3.1.
  "wire": { "keep_alive": "30m", "options": { "num_ctx": 32768 } },  // OPTIONAL verbatim body passthrough
                                            // NB: `options.num_ctx` is an OLLAMA knob. It is inert on
                                            // MLX and on most others; §6.1 calls it belt, never
                                            // enforcement, and `contextTokens` is the real bound.
  "maxTurns": 12,
  "maxOutputTokens": 8000,
  "strength": 2,                            // SHOULD be declared — GR2067
  "costly": false,
  "specialization": "coding"
  // NO "routing" — GR2066 in v1.
}
```

**`endpoint` is a new key, not an overload of `command`.** The tiering DoR's worked example spells it
`"command": "http://inference.local:11434"` (`17-model-tiering.md:1786`); this plan rejects that, but
**not** on the grounds the first draft gave. `PlanLoader.cs:422` defaults an absent `command` to the
**block name**, so a URL-valued `command` and an omitted one both fail the PATH probe — GR2009 needs
the kind-aware fix either way, and that defect does not choose between the spellings. The real reason
is typing: `command` is documented *"The executable to launch"* (`PromptRunnerConfig.cs:14`) and is
consumed by a PATH probe. One field holding either an executable or a URL forces every reader to
sniff a string for a scheme, and `providers init` would have to guess which it wrote.

**The GR2009 defect is real and is fixed regardless.** `ValidatePromptRunnerCommands`
(`PlanValidator.cs:2950-2960`) probes **every** declared runner with no kind filter, and
`PathExecutableProbe.cs:24-26` returns false for both a URL and an unqualified block name. So today
an `openai-compat` block draws a confident, wrong warning telling the operator their endpoint is not
on PATH — and a warning that is always wrong trains people to ignore GR2009. `claude` keeps the PATH
probe; `openai-compat` is skipped there and gets §7's preflight reachability check instead.

**`apiKeyEnv` names a variable; it never holds a secret.** `guardrails.json` is committed and is
hashed into `PlanDefinitionHash`, which keys the review attestation. A token in that file would be a
secret in git *and* would de-attest the plan's review on rotation.

**`wire` is the HTTP sibling of `env`.** `PromptRunnerSettings.Env` is documented as *"a general
passthrough for runner/provider knobs the harness does not model"* (`PromptRunnerConfig.cs:146-153`).
`env` is meaningless to a runner that spawns no process; `wire` occupies exactly its role for a
request body — one key, no per-vendor knobs, and `keep_alive` / `num_ctx` / `temperature` all fit
through it.

**A `wire` map that overrides `model`, `messages`, `stream`, `stream_options`, `tools` or
`max_tokens` is a GR2065 ERROR, not a runtime throw.** Those fields are the harness's; `wire: {
"stream": false }` is the exact typo that would silently disable streaming (§6.3), and it is
statically knowable from `guardrails.json`. `RawPromptRunnerRouting` already models this shape —
`[JsonExtensionData] Extra` (`RawManifests.cs:161-162`) exists *"so the loader can SEE a stale `rank`
and warn"*. Same treatment. Gate at validate; the class's own refusal is the backstop.

**The four new keys on a block whose `kind` is not `openai-compat` are a GR2065 error, not a silent
ignore.** A key that does nothing where it was written is indistinguishable from one that works.

**Hash and attestation.** The keys are optional and additive, so no existing `guardrails.json` changes
by a byte and no existing plan is de-attested. Editing one mid-flight is a different matter — see
§6.1, where it is the prescribed remedy for the most anticipated failure.

**The knobs that map cleanly, and the ones that do not:**

| Setting | Mapping |
|---|---|
| `maxTurns` | the tool-loop iteration cap; exceeding it ⇒ `PromptFailureKind.MaxTurns`, which already auto-escalates the next attempt's budget (`TaskExecutor.cs:231-236`) |
| `maxOutputTokens` | `max_tokens`; `finish_reason == "length"` ⇒ `PromptFailureKind.OutputCap` |
| `effort` | `reasoning_effort`; a 400 naming an unknown parameter is an `Error` telling the operator to move it into `wire` or drop it |
| `Timeout` | whole-request CTS ⇒ `PromptFailureKind.Timeout` |
| `StallBound` | the SSE-chunk watchdog ⇒ `Stalled` — **honoured, though no Guardrail/Advisory call site sets one today** (§6.3) |
| `AbortAfterConsecutiveToolDenials` | **honoured.** A containment refusal (§5) *is* a denial. Both advertised consumers set it to 3 (`Overwatch.cs:472`, `NeedsHumanTriage.cs:112`), and `PromptInvocation.cs:59-61` calls it *"a runner-agnostic POLICY the harness declares"* whose detection is the runner's own business. Ignoring the one bound the harness sets on the prompts this plan is built to serve would be indefensible |
| `BlockedWritePaths` | always empty — there is no write tool to refuse |
| `permissionMode` | **ignored** — there is no permission layer to set a mode on |
| `allowedTools` | **filtered, not ignored** — see below |
| `extraArgs` | **ignored**, except `--settings` (below) |

**`allowedTools` is a filter, and the first draft's justification for ignoring it was false.** It
claimed the fixed tool set is *"strictly narrower than any declared grant"* — but
`ClaudePromptRunner.cs:415-417` always emits a grant list, and a `guardrailOverrides.allowedTools:
["Read"]` block pinned here would have received `Glob` and `Grep` too: **wider** than declared. The
rule: when the declared list names at least one of `Read` / `Glob` / `Grep`, the runner offers only
those; otherwise it offers all three. Either way the choice is disclosed (below).

**Disclosure goes in the runner's own stream log.** The runner emits a synthetic first
`{"type":"runner-notice", …}` object into `StreamLogPath` naming every declared setting it is
ignoring or narrowing, before the first wire request. That file is runner-owned and exists on both
paths (`claude-stream.jsonl` / `guardrail-<name>.stream.jsonl`). `attempt-route.log` is **not**
available — it is written once per task attempt by `TaskExecutor.cs:772-778` and has no guardrail
sibling. When `StreamLogPath` is empty (§6.5) no notice is written, which is correct for a caller
that asked for no log.

**`--settings` is fatal, and after §3.6 it is genuinely unreachable.** If it still arrives, the
splice and `NeedsContainmentHook` disagree — a harness bug — and the runner throws rather than
proceeding, exactly as `PromptRunnerRegistry.CreateRunner` throws rather than substituting Claude.

---

## 5. Containment for the read tools — defined here, not cited

The first draft claimed the read tools are *"contained better than the Claude equivalent"* by calling
`WorkspaceContainment.Escapes` against (workspace ∪ plan directory). **That function cannot do this
job:**

```csharp
// WorkspaceContainment.cs:17-22
public static bool Escapes(string workspace, string relativeEntry)
{
    // A rooted entry ignores the workspace base entirely under Path.Combine — reject outright.
    if (Path.IsPathRooted(relativeEntry)) { return true; }
```

It rejects **every rooted path outright**, and every path the harness hands a prompt is absolute —
`PromptComposer.cs:138` and `:350` ("this absolute path"), `PromptContext.cs:22`, and `Overwatch.cs:534`,
whose whole point in #452 was to name the resolved absolute log directory. A `Read` guarded by it
would refuse every read the harness instructs the model to make: #452 re-created by design.
`WorktreeContainmentHook.cs:9-14` says what the function is actually for — *"only ever polices the
plan AUTHOR's declared `writeScope` at validation time."*

**So this plan defines a real primitive**, `PromptToolContainment.IsReadable(roots, absolutePath)`:
normalise the candidate with `Path.GetFullPath`, normalise each root, and accept on a directory-
boundary match against any root — the `AcceptedRoots` shape SSOT §9.4 already uses, minus the shell
mirror, because this runs in-process. Roots = `{ WorkingDirectory, PlanDirectory }`, **empty entries
dropped** (`Path.GetFullPath("")` throws, and `CriticalityJudge.cs:325-333` supplies two empty ones).

**When the root set is empty, every read is DENIED.** Chosen deliberately: the only caller with no
roots is the criticality assessment, which needs no tools at all, and deny-all is the direction where
being wrong is a loud refused tool call rather than a silent read of the whole filesystem. A refusal
counts toward `AbortAfterConsecutiveToolDenials`.

That primitive genuinely is stronger than the Claude hook *for file tools* — no shell mirror, no
`realpath` flavour dependence, no Bash command-text heuristic (the mechanism SSOT §9.4 itself admits
*"is not a security sandbox"*). The comparison is narrower than the first draft's and it is true.

---

## 6. Failure modes, and how each one becomes loud

### 6.1 Context overflow — the one the vendor hands you

**Ollama silently truncates a prompt longer than the model's context window.** The response is
plausible, complete, and reasoned over half the evidence. Nothing in the wire protocol reports it.

**Both halves are required.**

1. **Refuse before sending.** `contextTokens` is required. The runner computes a deliberately
   **pessimistic** upper-bound estimate — `ceil(chars / 3)`, a stated constant, deliberately not
   `/4` — and refuses when `estimate + maxOutputTokens > contextTokens`. **Computed over the bytes
   actually about to be sent, on EVERY turn** — system message, user message, and all accumulated
   tool-result text — not over `invocation.ComposedPrompt` once at entry. A tool loop that reads
   three files grows the request every turn, and bounding only the first is the version of this check
   that passes its test and ships the bug.
2. **Detect after.** The response's `usage.prompt_tokens` is compared against a deliberately
   **optimistic** lower bound, `floor(chars / 4)`. A server reporting fewer prompt tokens than that
   floor truncated, and the attempt fails. This half catches a window smaller than the block claims —
   the case an operator's declaration structurally cannot cover.
3. **Set it on the wire where the protocol allows** (`wire.options.num_ctx`) — as belt, never as
   enforcement.

**A new `PromptFailureKind.ContextOverflow`**, the exact mirror of the shipped `OutputCap` on the
other side of the same window. Every consumer switch already has a `_` default
(`TaskExecutor.cs:921-943`), so the member is additive and no Claude run can produce it. It gets
actionable retry feedback and no auto-escalation — there is nothing the harness can raise.

**The remedy has a consequence the first draft prescribed without naming.** "Raise `contextTokens`"
is an edit to `guardrails.json`, which is folded into **`PlanHash`** (`PlanHash.cs:28-30`, which keys
the resume mismatch warning **and the pre-DAG `planPreflights` skip`) and into **`PlanDefinitionHash`**
(`PlanDefinitionHash.cs:19, 61`, which *"exists ONLY to key the review marker"*). So the standard fix
for this failure **re-stales the plan's review attestation and invalidates the preflight skip**. The
feedback text says so, and offers the alternative first: shrink the task's inputs.

### 6.2 The rest

| Failure | Detected by | Classified | Why not otherwise |
|---|---|---|---|
| Endpoint down: DNS, refused, reset, TLS | exception type + message, through the runner's **own** signal table | `Transient` | rides the shipped #115 bounded pause with no retry burn (SSOT §9.6). The table is this class's, never Claude's — the quarantine rule |
| HTTP 429 / 503 / 529 | status | `Transient` | matches the shipped rule |
| **Model not pulled** (Ollama 404) | status 404 + body | **`Error`**, naming the model and `ollama pull <model>` | **Never `Transient`.** A pause waits for a human action no waiting produces — it would burn `transientPauseBudgetSeconds` (default 4h) and settle `rate-limited`, a diagnosis that is false |
| Auth failure (401 / 403) | status | `Error`, naming `apiKeyEnv` and whether it was set | retrying a bad key is a loop |
| Output truncated | `finish_reason == "length"` | `OutputCap` | the shipped member, unchanged |
| Loop / no progress | iterations reach `maxTurns` | `MaxTurns` | the shipped auto-escalation applies |
| Every tool call refused | `AbortAfterConsecutiveToolDenials` reached | `Error` with the refused paths | the #452 bound, honoured (§4) |
| **Model ignores the output contract** | the extractor finds no valid object | **no verdict file is written** | `GuardrailVerdictReader.Read` already fails closed on a missing file (`GuardrailVerdict.cs:39-42`). **No pass is ever synthesised** |
| **Server accepts `tools` and never calls one** | §6.6 | **`Error`** naming the block and the endpoint | the one failure here whose natural direction is a **false GREEN** — see §6.6 |
| Server rejects `tools` outright (400) | status + body | `Error` naming the block, the endpoint and the model | a server with no tool support cannot host a verifier; retrying is a loop |
| Server reports no `usage` | absent after `include_usage` was requested | `Usage = null` + a `runner-notice` line | never `{ 0, 0 }` |

**The model-not-found remedy is per-engine text, and it is the only place an engine name may appear.**
`ollama pull <model>` is right for one engine and misleading for the four others. The runner holds a
small map from the configured block to its remedy sentence — Ollama `ollama pull <model>`, MLX
`mlx_lm.download --hf-repo <model>` or the LM Studio model manager, llama.cpp/vLLM "start the server
with `--model <model>`" — selected by an **optional `engine` hint on the block**, defaulting to a
neutral sentence naming the model and the endpoint. `engine` is **operator-facing text only**: it
never selects a code path, never changes a request, and is absent from `ServesRoles`, the containment
rules and the wire body. A hint that steers behaviour would be a second kind wearing a different name,
which §3.1 just rejected.

### 6.3 Streaming — required, with two of the first draft's three reasons withdrawn

The first draft gave three reasons. Only one survives, and the honesty matters more than the count.

- **Withdrawn — the #504 stall watchdog.** `grep -rn "StallBound" src/` has **exactly one** setter:
  `WaveBreakdownInvoker.cs:198`, an **Action**-role site. No invocation this runner can legally
  receive sets one today.
- **Withdrawn — breakdown-phase freshness.** `BreakdownProgress.StreamFreshness` is likewise
  breakdown-only.
- **Stands — the log viewer.** `LogServer` tails the stream file (`LogServer.cs:51-52`), and a run
  where a pinned judge shows a dead file for ten minutes is the "healthy-slow vs stuck" ambiguity the
  operator-visibility work exists to remove.

**Streaming is still required**, on that reason plus one more: the runner must honour `StallBound`
*if it is ever set*, and a runner that can only honour it by being rewritten is a runner that will
not be. But the §8 test row that asserted `Stalled` firing on a production path is **withdrawn** —
it asserted behaviour no call site can reach, which is a rung-1 violation wearing adversarial
clothes. It is replaced by a unit-level test that sets `StallBound` explicitly and is labelled as
covering a contract, not a current path.

**`transcript.md`: the first draft overreached.** The dependency-context consumers
(`DependencyContextBuilder.cs:70, 134`; `PromptComposer.cs:218-220, 298-301`) read the **action's**
`transcript.md`. A Guardrail-role invocation writes `guardrail-<name>.transcript.md`
(`GuardrailRunner.cs:232`), which no downstream task reads. So *"silently degrades every dependent
task"* cannot happen in v1. The runner still renders its transcript — it is the operator's only
readable view of a tool loop, and it names every tool call and its result size, so a verifier that
rendered a verdict having read nothing is visible to a human. And `PromptInvocation.cs:50` already
sanctions the null case *"(e.g. a runner whose output is not a Claude stream)"*, which is this runner
exactly.

### 6.4 The verdict, and how the model produces one without a write tool

The runner writes the verdict file, and the rule that makes that safe is that it may only ever
**transcribe**.

- **The extractor is `PromptJsonExtractor` (§3.3), shared with the two advisory consumers:** the last
  fenced ```json block, else the last top-level JSON object; it must parse; for a verdict it must
  carry a boolean `pass`. Anything else ⇒ **no file written**.
- **The failure direction is safe by construction.** No file is already the contractual fail. The
  runner can never produce a `pass: true` the model did not write as a boolean.

**The contradiction the first draft created, and the fix.** `PromptComposer.cs:350-355` says *"You
MUST end by writing your verdict as a JSON object to this absolute path"*. A runner-supplied system
message saying "you have no write tool" leaves the weakest model in the system holding two opposite
instructions — and §6.4's own argument against a `write_verdict` tool (that a model which ignores it
produces no verdict on every guardrail) applies with equal force in this direction.

**So the composer becomes capability-aware, not runner-aware.** `AppendVerdictContract` takes one
boolean — *can this runner write files?* — resolved from `PromptRunnerKinds.WritesFiles(kind)`. It
emits either the shipped section, byte-identical, or the transcription form: *"emit your verdict as
the last fenced ```json block of your final message; the harness will write it to `<path>`."* One
instruction, never two. The composer learns a **capability**, never a vendor name, so the SSOT §9
quarantine holds — the same distinction §3.5 draws between capability and preference.

`ComposedPrompt` is otherwise untouched, and `composed-prompt.md` — written by
`ActionRunner.cs:141` / `GuardrailRunner.cs:182` **before** the invocation, and called by SSOT §8
*"exactly what the runner got"* — stays true. The runner appends nothing to it; its own framing (the
tool catalogue) rides in the wire `system` message, which is the first object teed to
`StreamLogPath`.

### 6.5 The empty-path convention, promoted out of a comment

`ClaudePromptRunner.cs:70-80` documents that an empty `StreamLogPath` means *"don't write a stream
log (issue #381), NOT 'abort'"*. That convention lives **only in that comment**, while
`PromptInvocation.cs:44-45` types the field `required string` with no hint that empty is legal — and
`CriticalityJudge.cs:325-333` supplies empty `StreamLogPath`, `WorkingDirectory` **and**
`PlanDirectory`. A second runner author reading the seam contract would not know, and would crash on
the one advisory path §3.3 advertises.

The convention moves into `PromptInvocation`'s own XML docs as part of this change, covering all
three fields, and §9 gates it.

### 6.6 A server that accepts `tools` and never calls one — the false GREEN this plan otherwise ships

This is the most dangerous failure in the class, it was missing from the first draft entirely, and
widening the engine list to MLX is what surfaced it — because tool-calling support is precisely where
these servers diverge most, and it is the one capability v1 cannot do its job without.

**The mechanism.** v1's whole role is a verifier that *reads the evidence*: §5 exists to contain
`Read`/`Glob`/`Grep`, §4 filters them, §6.2 counts their refusals. An OpenAI-compatible server is
free to accept a `tools` array, ignore it, and return an ordinary completion. Nothing in the protocol
distinguishes *"I considered the tools and needed none"* from *"I do not implement tools."* So the
model answers from the composed prompt alone, having read **nothing**, and — because the prompt tells
it to end with a verdict — emits a perfectly well-formed ```json block:

```json
{ "pass": true, "summary": "the implementation satisfies the criterion" }
```

Every check in §6.2 passes. The extractor is happy, the boolean is real, the verdict file is written,
the guardrail goes **GREEN**. A deterministic gate has certified a claim no evidence was read for —
which inverts the one sentence this whole product rests on.

**Why the existing checks do not catch it.** They are all about the response being *malformed*: no
JSON, wrong block, truncation, no `usage`. This response is immaculate. And the §6.1 context checks
point the wrong way — a prompt with no tool results is *small*, so it clears both bounds comfortably.

**The fix has two halves, and the first is the load-bearing one.**

1. **Prove the capability at preflight, once per endpoint, before the DAG (§7).** A server that cannot
   call tools is a configuration fact, knowable before a token of real work is spent, and it must halt
   the run rather than degrade it.
2. **Fail the attempt at runtime when a verifier invocation completes with zero tool calls**
   (`PromptFailureKind.Error`, naming the block and the endpoint). This is the backstop for the case
   where the probe passed but this particular model, at this prompt, called nothing. It is deliberately
   a blunt rule, and the blunt direction is the safe one: a verifier that read no files has not
   verified anything, so refusing is right even in the rare case where the answer was obtainable from
   the prompt alone. The alternative — trusting it — is the false green above.

**Scoped to the `Guardrail` role only.** An `Advisory` invocation (§3.3's `overwatch` / `ai-triage`)
legitimately reasons over text it was handed and may call nothing; applying the rule there would fail
every advisory call on every engine. `PromptInvocation.Role` (§3.4) already carries the distinction,
which is a second payoff from that field existing.

---

## 7. Validation — what is knowable offline, and what is not

**The premise this plan was handed is out of date, and correcting it is part of the deliverable.**
A recognised-but-unimplemented kind is *today* a `guardrails validate` ERROR:
`ValidatePromptRunnerKindsImplemented` (`PlanValidator.cs:301-319`, wired at `:82`), with
`PromptRunnerRegistry.cs:49-53` already calling itself *"the BACKSTOP, not the gate"*. "Loads clean,
then throws at registry construction" describes pre-Stage-1.5 behaviour, and the tiering DoR's own
table still says so at `17-model-tiering.md:1656`. **That line is stale and this change corrects it.**

**`validate` gets the static half and stays static and offline.** Plan 26 §3 just ruled that making
`validate` execute things is *"a semantic change this plan does not make"*; a network probe is the
same violation. So: `endpoint` present and a well-formed absolute http/https URL; `model` present;
`contextTokens` ≥ 1; `wire` overriding nothing harness-owned; none of the four keys on a block of
another kind (all **GR2065**); none of §3.7's five actor-reachability routes (**GR2066**); `strength`
declared (**GR2067**, warning); and an `openai-compat` block that is neither pinned nor a reserved
profile is an **unreachable block** (GR2067's second form, warning — it catches the `triage`-for-
`ai-triage` misspelling that otherwise fails silently, §3.3). GR2009's PATH probe is skipped.

**Reachability goes in the `run` preflight, where plan 26 just put sample verification.** Before the
DAG, once per **distinct** endpoint: `GET {endpoint}/models`, short timeout. Unreachable ⇒ halt
before a token is spent. Reachable ⇒ assert every declared `model` for that endpoint appears in the
list; halt naming the §6.2 per-engine remedy when it does not. This also lets `OpenAiCompat` join
`ModelEnumerable` (`PromptRunnerConfig.cs:260`), which the doc already anticipates.

**`GET /models` is itself a dialect assumption, not a certainty.** It is near-universal but not
guaranteed, and an engine that serves chat perfectly while omitting the listing endpoint must not be
locked out by a check that exists to help. So a **404/405 on the listing endpoint downgrades to a
warning** naming the endpoint and skipping only the model-presence assertion; any other failure
(refused, DNS, timeout, TLS, 5xx) stays a halt. The distinction is "the server answered, but does not
offer this" versus "there is no server."

**The tool-capability probe — the check that closes §6.6.** Same preflight, same once-per-endpoint
budget: one minimal `POST {endpoint}/chat/completions` carrying a single trivial tool whose only
correct response is to call it, with `max_tokens` small. Three outcomes:

| Probe result | Verdict |
|---|---|
| the response contains a `tool_calls` entry | **capable** — proceed |
| 400/422 rejecting `tools` or an unknown parameter | **halt**, naming the block, the endpoint, the model, and that v1's verifier role requires tool calling |
| 200 with no `tool_calls` | **halt**, with the same text plus the §6.6 explanation — this is the silent case, and it is the entire reason the probe exists |

**Why a probe rather than a documented prerequisite.** "Use a server that supports tool calling" in a
README is not a gate, and the failure it fails to prevent is a *silent false green*, not a crash. The
project's own repeated lesson applies exactly: a mechanism that fails in the direction that looks
fine is caught by execution, never by reading. One request per endpoint, once per run, before any
real spend, is a cheap price for the only failure in this class that certifies work nobody checked.

**It costs zero requests on a plan with no `openai-compat` block**, on the same terms and proven the
same way as the reachability probe below.

**Model-level, not just endpoint-level.** The probe is keyed on (endpoint, model), because one server
can host both a model whose template emits tool calls and one whose template does not — swapping the
model is exactly the kind of edit that would otherwise re-open §6.6 silently.

**The condition, borrowed in spirit from plan 26 §7:**

> A plan that declares **no** `openai-compat` block must cost **zero HTTP requests**. Discovery is a
> registry scan; a plan with nothing to probe pays discovery only.

**Proven the rung-1 way:** a loopback listener that fails the test on *any accepted connection* — not
a counter the preflight increments, which would measure our own bookkeeping.

**`strength` is warned on for a mechanical reason.** `TierResolver.IsWeakVerifier` is
`block is null || (block.Strength is null && block.Kind != PromptRunnerKind.Claude)`
(`:460-461`), so an undeclared `openai-compat` block is **permanently guessed weak** and every judge
on it carries a #229 advisory forever. Advisories that always fire stop being read.

---

## 8. How this is tested without the hardware

The #382 doctrine: *a component certified only against a fake of the seam the run exercises is a
green light over a broken wire.* **The seam here is the OpenAI HTTP wire, not `IPromptRunner`.**

Both precedents exist: `LogServerTests` runs a real `HttpListener` and `HttpClient` on loopback
(`:18-19, 51-53`), and `FakeClaudePlanBuilder` drives the **real** `ClaudePromptRunner` against a real
fake-CLI process — `ClaudePromptRunnerStreamLogTests.cs:6-16` records why: #381 shipped because *"the
runner's own tests faked the seam."*

So: a loopback OpenAI-compatible server taking a **scripted response plan**, because its job is to
misbehave.

| The server does | The test proves |
|---|---|
| silently truncates the prompt, returns a confident answer | the §6.1 after-check fails the attempt |
| grows the request across three tool-call turns until it exceeds the window | the estimate is recomputed **per turn**, not once at entry |
| omits `usage` despite `include_usage` | `Usage` is `null`, never `{ 0, 0 }` |
| returns 404 `model not found` | `Error`, **not** `Transient` — no 4-hour pause |
| returns 429 | `Transient`; the shipped pause runs; the retry budget is untouched |
| emits a `json` block with `pass: true` that is **not** the last block, then prose | the strict extractor takes the last block |
| returns prose with no JSON | **no verdict file is written** |
| returns prose *around* a valid JSON object | `PromptJsonExtractor` recovers it — the §3.3 payoff |
| requests a file outside both roots | `PromptToolContainment` refuses; the refusal counts toward the denial bound |
| refuses three tool calls in a row | the #452 abort fires with the refused paths |
| **accepts `tools`, calls none, returns a well-formed `pass: true`** | the §6.6 rule fails the attempt — **and the assertion is that no verdict file with `pass: true` exists**, not merely that an error was raised. This is the false-green test; asserting the error alone would still pass if the file were written first |
| **rejects `tools` with a 400** | preflight halts naming the block, endpoint and model — before any task runs |
| **answers the tool probe with no `tool_calls`** | preflight halts; a loopback counter proves the probe ran **once** per (endpoint, model), not per invocation |
| **404s `GET /models` but serves chat** | a warning, not a halt; the model-presence assertion is skipped and the run proceeds |
| an `Advisory` invocation calls no tool | **succeeds** — the §6.6 rule is `Guardrail`-scoped, and a test that omitted this would ship a rule that breaks every advisory path |

Plus, driven from the harness rather than the server: an `Action`-role invocation is refused; a
`Guardrail`-role invocation **in worktree mode** produces a verdict file (the §3.6 regression); a
`--settings` `ExtraArg` throws; empty `StreamLogPath`/`WorkingDirectory`/`PlanDirectory` runs without
crashing.

**The assertion requirement stands** (catalogue rung 1): each asserts an effect **only the production
implementation emits** — the verdict file's bytes, the `usage` numbers in `run.json`, the pause that
did or did not happen. *"The seam was called" is not an assertion.*

**What the fake cannot prove, said plainly.** That a real Ollama / llama.cpp / **MLX (`mlx_lm.server`
or LM Studio's MLX engine)** / LM Studio / vLLM speaks the dialect we assume:
`stream_options.include_usage` support, **whether `tools` are accepted AND actually called**, whether
`num_ctx` is honoured (it is an Ollama option and means nothing to MLX — hence "belt, never
enforcement" in §6.1), the exact model-not-found body, SSE framing, `reasoning_effort` tolerance,
and whether `GET /models` exists at all. That is **dialect risk**, and no loopback fake retires it.

**MLX is the engine this list was written without, and it is the one most likely to diverge.** The
first draft enumerated four engines that all descend from broadly the same server lineage; MLX is a
separate implementation, and tool calling is exactly where these servers differ most. Nothing in the
loopback suite can tell us which MLX server, at which version, emits `tool_calls` — which is why §7's
probe verifies it at runtime instead of this plan asserting it. **This plan makes no claim about any
specific MLX server's tool support; it makes the harness refuse to proceed without proof.**

The mitigation is a manual, opt-in, non-CI verb:

> **`guardrails providers check <block-name>`** — one probe per assumption against the operator's real
> endpoint, each reported met / unmet / unknown. Not in CI, not in `run`, not in `validate`. The same
> posture as M7's opt-in real-claude smoke (`03-roadmap.md:15`).

---

## 9. Done when

Each bullet closes a specific wrong-but-passing implementation the adversarial pass found.

- `kind: "openai-compat"` constructs an `OpenAiCompatPromptRunner`; `Implemented` grows to two and the
  test pinning it against the dispatch switch still passes.
- `ServesRoles` is pinned **by construction** — for each (kind × role) the real runner is built and
  asserted to accept or refuse. Not by reading the field the runner reads.
- `PromptInvocation.Role` is `required`; **each of the seven sites is asserted to carry its §3.4
  value individually** — a Claude-invariance test alone would pass with `AiMergeResolver` mislabelled
  `Advisory`, which is how a write-capable merge lands on a runner with no write tool.
- **In worktree mode**, a prompt guardrail pinned to an `openai-compat` block produces a verdict file
  whose bytes the harness reads — proving the §3.6 splice condition. A serial-only test would pass
  with the flagship path broken.
- The same guardrail produces **no file** when the model emits no parseable verdict.
- `composed-prompt.md` holds exactly the bytes sent as the user message, compared against the request
  the loopback server received.
- The verdict-contract section is the shipped text for a writing runner and the transcription text for
  a non-writing one — asserted on the composed bytes, both ways.
- **The §3.3 payoff has acceptances:** `overwatch` and `ai-triage` declared as `openai-compat` produce
  a parsed `OverwatchProposal` and a triage sidecar from a final message containing prose around the
  JSON. The reserved names are asserted as literals against `SchedulerFactory`'s constants.
- `CostUsd` is `null`, `Usage` carries real counts, and both are read **from `run.json`'s bytes**, not
  from the `PromptResult` object.
- A truncating server fails with `ContextOverflow`; an over-long prompt is refused before the request;
  a request that grows past the window on turn three is refused on turn three.
- 404 is `Error` and 429 is `Transient`, each proven by the pause that did or did not happen.
- Streaming is proven by the stream log growing **before** the response completes.
- `validate` reports GR2065 / GR2066 / GR2067 with **one test per GR2066 route** — including the
  sole-declared-runner route, which no `default` pointer names, and the frontmatter route (§3.7).
- No GR2009 for an `openai-compat` block.
- The preflight probes each distinct endpoint once and halts on unreachable and on model-not-listed;
  a plan with no `openai-compat` block accepts **zero connections** on a loopback listener.
- **A server that accepts `tools` and calls none never produces a `pass: true` verdict file** (§6.6) —
  asserted on the file's absence, not on an exception. This is the false-green gate; every other
  bullet here guards a loud failure, and this one guards a quiet success.
- **The tool-capability probe halts on both shapes** — a 400 rejecting `tools`, and a 200 with no
  `tool_calls` — before any task runs, and runs once per (endpoint, model).
- **An `Advisory` invocation that calls no tool still succeeds**, proving the §6.6 rule is
  role-scoped and has not broken the advisory paths §3.3 advertises.
- A `GET /models` that 404s **warns and proceeds**; a refused/timed-out endpoint still halts.
- **No engine name appears in any code path** — asserted by a source-level check over the runner and
  the preflight: engine strings live only in operator-facing remedy text keyed off the optional
  `engine` hint. A plan configured for MLX and one configured for Ollama produce byte-identical
  requests for the same `model`, `wire` and prompt.
- Every ignored or narrowed setting appears in a `runner-notice` line — read from the file.
- An invocation with empty `StreamLogPath`, `WorkingDirectory` and `PlanDirectory` completes.
- `guardrails providers check` reports each dialect assumption met / unmet / unknown.
- Judge `costUsd`/`usage` reach `AttemptJudge` provenance and the per-tier report, and a test asserts
  `JournalCost.Total` is **unchanged** by their presence (§11 finding 3).
- SSOT §2 / §8 / §9 / §9.6 / §9.7 carry every change, `17-model-tiering.md:1656` is corrected, and
  `guardrails-domain-knowledge` is updated in the same change (invariant 4).

---

## 10. Out of scope

- **Task actions on local inference.** The honest v1.5 is not "add a write tool" — it is "re-express
  the containment boundary in .NET and prove it with this same adversarial suite."
- **`routing` on an `openai-compat` block** — no tier candidacy, human pin only.
- **The `local`, `codex` and `openrouter` kinds.**
- **Provider probes (#227)** beyond the one preflight check.
- **`maxRunSeconds`** — the run-level wall-clock budget §11 finding 2 names. Runner-agnostic; belongs
  with autonomous mode's liveness floor.
- **Folding judge spend into `JournalCost.Total`** — §11 finding 3.
- **A per-model pricing table** (a named v2 bet).
- **Renaming `claude-stream.jsonl`.** SSOT §8's wording is corrected to admit a non-Claude writer.
- **Any change to `validate`'s static-and-offline contract.**
- **Multimodal, embeddings, and any tool beyond `Read` / `Glob` / `Grep`.**
- **Auto-pulling a model.**

---

## 11. Risks accepted, and the arguments that survived scrutiny

**Finding 1 — the verifier-first paradox.** §6.5 exists to stop weak models judging, and this plan's
payload is judging. It holds because v1 makes the block unreachable by *selection* (§3.7): a local
judge happens only where a human pinned one, which §9.6 rule 1 explicitly honours. And the two
zero-mechanism consumers are advisory-never-gates — `Overwatch.cs:150` (*"a malformed/absent/errored
proposal = no action; verdict from files"*), `:192`, `:219`. They cannot make a run go green over
broken work. **The residual, accepted:** a human *can* pin a real prompt guardrail to a local block.
Conditions: the pin is deliberate, GR2067 warns when `strength` is undeclared, the #229 advisory
fires, and deterministic-first is untouched.

**Finding 2 — the cost cap, restated more narrowly than the first draft.** `CurrentCostUsd()` returns
`?? 0m` (`RunJournal.cs:160-166`) and GR2012 forces `cap > 0` (`PlanValidator.cs:222-231`), so `0 >=
cap` is false at all four sites (`Scheduler.cs:2792`, `Overwatch.cs:136`, `Scheduler.cs:1317`,
`Scheduler.cs:1582`). The first draft concluded the `--autonomous` liveness brake is "decorative" and
proposed warning about it. **That was wrong, by this plan's own scope:** since actions all stay on
Claude, `ActionRunner.FromPrompt` (`:502`) still records their cost and the cap binds on the dominant
spend. A warning saying otherwise would fire on every autonomous run and be misleading — the exact
"advisories that always fire" failure §7 names. **The true statement is narrower and is the one made:
judge and supervisory spend on this block is unmeasured**, which is finding 3, not a liveness
problem. No warning is added.

**Finding 3 — Deliverable D creates two cost numbers, and this plan chooses.** `JournalCost.Total`
(`:20-48`) sums attempt `CostUsd` plus `OverheadCostUsd`; judge cost is in neither, and
`grep "CostUsd\|Usage" GuardrailRunner.cs` returns nothing. Recording it is necessary — a
verifier-only v1 with no judge measurement is unfalsifiable, which disqualifies a plan whose thesis is
that measurement decides the v2 bets. **But it is recorded, NOT folded into the run total.** Folding
it would make `maxCostUsd` trip earlier on every existing Claude run and change the `--autonomous`
brake's behaviour — a semantic change to the liveness floor, shipped inside a local-inference plan.
The two numbers are labelled: the total is *actor spend*, the judge column is *verifier spend*, and
§9 gates that `JournalCost.Total` is unchanged. Whether verifier spend should count against the cap
is a real question and is filed, not answered here.

**Finding 4 — "just add write tools."** The 20% is the tool implementations; the missing 80% is
§3.2(b), and the decider is §2 row 5. Conceded: if the September goal is *actors* on the Mac Studio,
this plan does not deliver it and says so on the tin.

**Finding 5 — the verdict extractor is a heuristic.** It fails **closed**: the only outcome of a
failed parse is no file, which is already the contractual fail. Contrast the Bash containment matcher,
which fails **open** by its own admission (SSOT §9.4) and is accepted anyway.

**What the adversarial pass could not break:** §5.1's null-vs-zero reasoning, §6.4's fail-closed
argument, §7's correction of the stale GR2044 premise, §3.1's rejection of a separate `local` kind,
and finding 1 above.

---

## 12. Exact SSOT edits (`docs/plans/02-schemas-and-contracts.md`)

Invariant 4: these land in the same change as the code.

1. **§2, the canonical `promptRunners` block (line 199).** Add `endpoint`, `contextTokens`,
   `apiKeyEnv`, `wire`, **each in its absent (`null`) state**, noting they apply to
   `kind: "openai-compat"` only and that `command` is ignored for that kind. Update the `kind`
   comment: `claude` and `openai-compat` are IMPLEMENTED.
   > **The mirror is byte-for-byte and drift-tested.** The `canonical-schema:promptRunners` sentinel
   > (line 218) pins `.claude/skills/plan-breakdown/references/schemas.md` to this block.

2. **§8, per-attempt log layout (line 3164).** Reword `claude-stream.jsonl` to *"raw runner output
   stream — canonical debug artifact (historical filename; a non-Claude runner writes its own wire
   lines here, led by a `runner-notice` object disclosing any declared setting it ignores or
   narrows)"*, mirrored onto `guardrail-<name>.stream.jsonl`.

3. **§9 intro (line 3341).** Add: `ServesRoles` and `NeedsContainmentHook` as build facts; the
   `PromptInvocation.Role` contract and §3.4's classification rule; the empty-path convention (§6.5);
   and that the §9.4 containment splice is now conditioned on `NeedsContainmentHook`.

4. **§9.4 (line 4344).** State the splice condition explicitly and why a runner with no write/shell
   tools needs no hook.

5. **§9, GR2009's bullet (line 3405).** Kind-aware: PATH probe for `claude`, skipped for
   `openai-compat`, whose reachability is the run preflight's (new §9.8).

6. **§9, the cost bullets (line 3375).** A runner reporting no cost records `null`, never `0`; judge
   spend is recorded on `AttemptJudge` and is **not** summed into `Total prompt cost:`.

7. **§4.2 (line 1082).** The verdict-contract section has two forms, selected by runner capability
   (§6.4) — the shipped text is unchanged for a writing runner.

8. **New §9.8 — "The `openai-compat` runner (#223)."** Block schema, role gate, wire mapping,
   containment primitive (§5), failure taxonomy, verdict transcription, preflight + zero-cost
   condition.

9. **§9.6's validation table (line 4752).** Three rows:

| Code | Sev | Rule |
|---|---|---|
| `GR2065` | error | a malformed or misplaced `openai-compat` block: missing / non-absolute-http(s) `endpoint`, missing `model`, missing / `< 1` `contextTokens`, a `wire` map overriding a harness-owned request field, **or** any of the four keys on a block of another kind |
| `GR2066` | error | an `openai-compat` block is reachable for ACTIONS by any of five routes: it declares `routing`; it is the **effective default** (`default` pointer **or sole declared runner** — `ResolveDefault`'s own rule); a task's `action.runner`; **an action prompt's frontmatter `runner:`** (requires the §3.7 fold); or a reserved `Action`-role profile name (`ai-merge`, `breakdown`) |
| `GR2067` | warning | an `openai-compat` block declares no `strength` (`IsWeakVerifier` guesses it weak forever), **or** is unreachable — neither pinned nor a reserved profile name, which catches `triage` written for `ai-triage` |

   And amend GR2044's row so `local`'s message names `openai-compat`.

10. **§9.7 `providers init`.** `OpenAiCompat` joins `ModelEnumerable`.

11. **`DiagnosticCodes.cs:914-925`.** The marker reads *"take GR2065 and update this line"* — take
    **GR2065–GR2067**, advance to **GR2068**. The reserved-by-name gaps GR2060 (doc 19), GR2061
    (doc 18) and GR2054 (doc 17 §13.2) are untouched.

12. **`docs/plans/17-model-tiering.md:1656`.** Correct the stale GR2044 row; §4.4's seam paragraph
    gains a pointer here.

---

## 13. Implementation handoff

Sequenced; each stage green before the next.

| # | Agent | filesTouched | Deliverable |
|---|---|---|---|
| 1 | `guardrails-harness-developer` | `Prompts/PromptInvocation.cs`, all **seven** §3.4 producers, **and `tests/**`** | The `Role` seam + the §6.5 empty-path doc. **`required` is a source break at every construction site including fixtures — that is the gate**, and `CriticalityJudge.cs:325`'s target-typed `new()` is the one a grep misses. |
| 2 | `guardrails-harness-developer` | `Prompts/PromptJsonExtractor.cs`, `Execution/OverwatchProposal.cs`, `Execution/NeedsHumanTriage.cs` | The shared lenient extractor (§3.3). Widens only paths that fail closed today. |
| 3 | `guardrails-test-author` | `tests/Guardrails.Integration.Tests/FakeOpenAiServer.cs` | The adversarial loopback server. **Authored before the runner**, so the runner is written against a server that already misbehaves. |
| 4 | `guardrails-harness-developer` | `Prompts/OpenAiCompatPromptRunner.cs`, `Prompts/PromptToolContainment.cs`, `Model/PromptRunnerConfig.cs`, `Prompts/PromptRunnerRegistry.cs`, `Prompts/PromptFailureKind.cs` | The runner, the containment primitive (§5), `ServesRoles` / `NeedsContainmentHook` / `WritesFiles`, `ContextOverflow`. |
| 5 | `guardrails-harness-developer` | `Execution/GuardrailRunner.cs`, `Execution/ActionRunner.cs`, `Prompts/PromptComposer.cs` | §3.6's splice condition + §6.4's capability-aware verdict section. **The §9 worktree-mode acceptance gates this.** |
| 6 | `guardrails-test-author` | `tests/**/OpenAiCompat*Tests.cs` | The §8 table + the §3.3 payoff acceptances. |
| 7 | `guardrails-harness-developer` | `Loading/PlanLoader.cs` (frontmatter helper **extracted**, not copied), `Loading/RawManifests.cs`, `Loading/PlanValidator.cs`, `Loading/DiagnosticCodes.cs` | The block schema, the §3.7 frontmatter fold, GR2065–GR2067, kind-aware GR2009. |
| 8 | `guardrails-harness-developer` | the run preflight, `Model/PromptRunnerConfig.cs`, `Cli/Commands/` | The reachability preflight + zero-cost condition + `providers check`. |
| 9 | `guardrails-harness-developer` | `Execution/GuardrailRunner.cs`, `Journal/`, `JournalTierSpend.cs` | Deliverable D — judge cost/usage recorded, **`JournalCost.Total` provably unchanged**. |
| 10 | `guardrails-skill-author` | `docs/plans/02-schemas-and-contracts.md`, `docs/plans/17-model-tiering.md`, `.claude/skills/plan-breakdown/references/schemas.md`, `.claude/skills/guardrails-domain-knowledge/SKILL.md` | §12's edits, both halves of the drift-tested mirror. |

> **Sequencing note.** `.claude/skills/**` was under concurrent edit while this was authored. Stage 10
> must re-read both mirror halves rather than working from this document's quotations.

---

## 14. Decisions this plan leaves to the maintainer

1. **The §3.7 frontmatter fold — do it, or concede the fifth route to the runtime backstop?**
   *Recommend: do it.* Without it GR2066 cannot see the most likely author mistake, and the plan would
   be claiming a gate it does not have. But it touches the loader, and stage 7 grows.

2. **Should verifier spend count against `maxCostUsd`?** This plan records it and does **not** fold it
   (§11 finding 3), because folding silently changes the `--autonomous` brake on every existing Claude
   run. *Recommend: keep the split here, file the question.*

3. **`strength` undeclared — warning or error?** *Recommend: warning.* An error makes the
   zero-annotation path unusable and the guess is verifier-only and cheap when wrong (D21a).

4. **`guardrails providers check` — v1 or follow-on?** *Recommend: v1.* It is the only thing here that
   retires dialect risk; cutting it means first contact with the Mac Studio is a live `guardrails run`.

5. **The `engine` hint — ship it, or let every engine share one neutral remedy sentence?**
   *Recommend: ship it.* It is a string in an error message and it is the difference between an
   operator being told what to run and being told what went wrong. The risk it carries is that
   someone later makes it steer behaviour, which §3.1 forbids and §9 asserts against.

**Decided by the maintainer 2026-08-30, recorded because it changed the plan:** **MLX is a named v1
target.** The trigger was a direct report of materially better throughput from MLX builds than from
llama.cpp builds of the *same* model — which is the entire economic case of this epic, collected on
the hardware it is aimed at. Neither #223's title nor this plan's first draft mentioned MLX, so §6.2,
§7 and §8 had been written against Ollama alone: the remedy text said `ollama pull`, the belt was
`num_ctx`, and the dialect list omitted the one engine most likely to diverge. The correction added
no kind and no wire change — the kind was already named after the protocol — but it did surface
**§6.6**, a false-green hole that had nothing to do with MLX and would have shipped: a server that
accepts `tools`, calls none, and returns an immaculate `pass: true`. Widening the engine list is what
made anyone look.

**Decided, recorded because a reader will want to reopen it:** `local` is **not** implemented and
**not** removed. Removing it breaks configs that may carry the token; implementing it forks one wire
protocol across two kinds. It stays reserved, with a message pointing at `openai-compat`.
