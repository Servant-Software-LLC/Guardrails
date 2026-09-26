---
charter-format-version: 1
---
# Architecture: first-class gateway support on the `claude` runner (#782)

**Status:** proposed design of record, for review in Charter before implementation.
**Issue:** #782, which is Phase 0-G of the approved #544 design
([`544-local-inference-actions.charter.md` §10, on branch `design/544-local-inference-actions`](https://github.com/Servant-Software-LLC/Guardrails/blob/design/544-local-inference-actions/docs/plans/544-local-inference-actions.charter.md)).
**Epic:** #786. **Replaces:** the `claude-local` wrapper runbook in #570. **Binds to:** `master` at `812cab3c`.

> **Revision note (round 1).** An adversarial pass of `041fae10` returned *revise*. It checked Claude Code's
> public docs. Its main finding was that the environment variables are not the only authority Claude Code
> reads: user settings, project settings, `apiKeyHelper`, OAuth credentials and `fallbackModel` can each
> override or bypass them. It also found the backend model's identity was asserted and never proven. The
> lead decided three points, recorded as D1–D3 below and implemented as given:
> - **D1**, how the harness takes authority over the child's routing and credentials (§1.2);
> - **D2**, how the backend model is identified (§3.2);
> - **D3**, where gateway-ness lives (§1.1).
>
> Items the reviewer confirmed from the docs are treated as confirmed. Items it could not verify are
> assigned to the live smoke (§5).

## What's being asked

The approved #544 sequencing answer is "gateway first". Claude Code pointed through an Anthropic-compatible
gateway (LiteLLM) at a local model (`llama-server` running Qwen) becomes a **supported, validated**
configuration of the `kind: "claude"` runner. Its dogfood results then decide how hard to push native #544.
Today it works only through an operator wrapper script (`claude-local`), and #570 lists five ways that fails
silently.

**Placement:** harness (config, child launch, validation, preflight, provenance), SSOT §2, §9.6 and a new §9.10,
README, and a live smoke. **Invariants:**
- **1 (deterministic gates):** untouched.
- **5 (honest halts):** a wrong backend, a dead gateway or an unresolved token halts before any task runs.
- **6 (light setup):** the operator still runs `llama-server` and LiteLLM; the harness never starts either.

**Support posture, disclosed.** Anthropic documents gateways for its own models. Running non-Claude models
through one is outside what Claude Code supports. The smoke records `claude --version` and is re-run on every
CLI upgrade (§5).

## 1. Contract

```jsonc
"qwen36": {
  "kind": "claude",
  "baseUrl": "http://127.0.0.1:4000",   // NEW. Absolute http/https; no userinfo, no query; trailing "/" normalized
  "authTokenEnv": "LITELLM_KEY",        // NEW, optional. The NAME of an env var whose value becomes ANTHROPIC_AUTH_TOKEN
  "model": "Qwen",                      // REQUIRED on a gateway block
  "contextTokens": 32768,               // widened from openai-compat: the PER-SLOT window of the backend (§1.3)
  "backendModel": "qwen3.6-35b-a3b"     // NEW, optional. What the backend must have loaded (§3.2)
}
```

All four gateway keys are **block-level only** and allowed only on `kind: "claude"`. A block with `baseUrl` is a
**gateway block**. A `claude` block without one launches byte-identically to today.

### 1.1 Where gateway-ness lives (D3)

`PromptRunnerRegistry` builds the `ClaudePromptRunner` instance with the block's gateway configuration. Every
invocation dispatched **to that instance** is a gateway dispatch, whichever call site produced it: a task
action, a tier route, a judge, `ai-merge` (`AiMergeResolver`), `breakdown`, `ai-triage` or `overwatch`. The
instance owns all of the following, in one place:

- the launch (§1.2);
- the provenance fields `Gateway` and `BackendModel` (§4);
- the null cost (§4), which also makes `AiMergeResolver`'s `AddOverheadCost` a no-op for a gateway merge.

**An invocation's settings can come from a different block** (for example `runnerConfig` A routed to gateway
runner B). The instance therefore **drops** any owned or scrubbed variable from `invocation.Settings.Env`,
compared case-insensitively, and records the drop in the stream log's first line. Those settings can never
re-introduce what the gateway removed.

### 1.2 Taking authority over the child (D1)

Claude Code reads routing, credentials and model choice from several places besides its environment. So every
gateway dispatch uses **both** of the following layers.

**(a) An isolated config directory.** The child runs with `CLAUDE_CONFIG_DIR` pointing at a per-run scratch
directory (`logs/<runId>/claude-config/`, created empty). That removes the user-level settings `env`,
`apiKeyHelper`, stored OAuth credentials, `fallbackModel`, and user agents and hooks.

- **The cost, stated:** the child also gets no user `CLAUDE.md`, user skills, memory or user MCP servers.
  For a harness actor that is arguably desirable: a hermetic child is one whose behavior the plan, not the
  operator's home directory, determines.

**(b) A harness `--settings` file on every gateway dispatch.** Command-line settings outrank **project**
settings, which (a) does not remove. A target repository's `.claude/settings.json` could otherwise set
`ANTHROPIC_BASE_URL` in its own `env` and redirect the child. The harness writes **one** composed settings
file into the attempt log directory, containing:

- `env`: the owned values (below);
- `model` and `fallbackModel`: both the block's `model`, so no fallback chain can leave the gateway;
- `hooks`: the worktree containment hook, when that splice applies (`ActionRunner.cs:193-197` today appends
  its own `--settings`). For a gateway dispatch the instance **merges** that hook into the composed file and
  passes a single `--settings`. It never passes two, because whether the CLI merges repeated flags is
  unverified.

A user `--settings` in a gateway block's `extraArgs` is `GR2084`, because on a gateway dispatch the harness
owns that flag.

**Managed settings outrank both layers.** The preflight (§3) therefore reads the documented managed-settings
sources for the host OS and **halts** if any of them sets a routing, credential or model-alias variable. The
harness cannot override those, so it refuses to pretend it can.

**The child environment,** built by the instance in this order:

1. **Scrub,** comparing names with the OS's `EnvNameComparison` (case-insensitive on Windows):
   - every inherited `ANTHROPIC_*` variable;
   - every `CLAUDE_CODE_USE_*` variable (Bedrock, Vertex and the rest);
   - `CLAUDE_CODE_OAUTH_TOKEN` and `CLAUDE_CODE_PROVIDER_MANAGED_BY_HOST`;
   - any inherited `CLAUDE_CONFIG_DIR`.
2. **Apply the harness set and the user's `env` map** as today, minus owned and scrubbed keys (§1.1).
3. **Set the owned values.** The same values also go into the composed settings file's `env`:
   - `ANTHROPIC_BASE_URL` = `baseUrl`;
   - `ANTHROPIC_AUTH_TOKEN` = the value of `$authTokenEnv`, or the fixed non-secret
     `guardrails-gateway-no-auth` when `authTokenEnv` is absent;
   - `ANTHROPIC_DEFAULT_HAIKU_MODEL`, `ANTHROPIC_DEFAULT_SONNET_MODEL`, `ANTHROPIC_DEFAULT_OPUS_MODEL`,
     `ANTHROPIC_DEFAULT_FABLE_MODEL` and `CLAUDE_CODE_SUBAGENT_MODEL` = the block's `model`, so that aliases,
     background calls and subagents all resolve to it (#570 trap 1);
   - `CLAUDE_CODE_MAX_CONTEXT_TOKENS` = `contextTokens`, when it is declared;
   - `CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC=1`;
   - `CLAUDE_CONFIG_DIR` = the scratch directory.

**Every owned variable is owned outright.** Setting one in the block's `env` is `GR2084`, and the check is
always case-insensitive. That includes `CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC`. Claude Code treats any
non-empty value as "set", including `"0"`, so there is no value an operator could supply that re-enables the
traffic. The key is owned rather than offered as an override that could not work.

**What still leaves the machine, disclosed.** The documentation says some features call `api.anthropic.com`
directly, not the base URL (for example fast-mode checks and WebFetch's safety check). Whether
`CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC` suppresses those is **unverified**. The smoke records any such
request (§5). The design does **not** claim that nothing leaves the machine.

**Agent shells inherit `ANTHROPIC_AUTH_TOKEN`,** because the Bash tool's subprocesses get the child
environment. For a local gateway without authentication that value is the placeholder and harmless. For a
remote gateway with a real token, commands the model runs can read it. This is disclosed in the SSOT and
README.

`authTokenEnv` naming a variable that is **unset or empty** at run start is a preflight halt that names the
variable.

### 1.3 Context window (W5)

`contextTokens` (already an `openai-compat` key, and widened here to gateway blocks) sets
`CLAUDE_CODE_MAX_CONTEXT_TOKENS`, so Claude Code compacts before the backend's window fills.

- **It must be the per-slot window.** `llama-server -c C -np N` gives each slot `C / N` tokens.
- **The preflight checks it.** When §3.2 resolves the backend, the preflight halts if `contextTokens`
  exceeds the per-slot `n_ctx` reported by `/props`.
- **Unverified, and assigned to the dogfood:** whether Claude Code honors the variable on a non-Anthropic
  model. §7 records compaction events for this reason.

## 2. Validation (static, offline)

`GR2083` is reserved by name for #544 and is not yet in `master`. This work **lands that reservation** as a
comment in `DiagnosticCodes.cs` and in the SSOT §9.6 marker, takes **GR2084–GR2086**, and advances the marker
to `GR2087`.

| Code | Sev | Rule |
|---|---|---|
| `GR2084` | error | `ClaudeGatewayBlockInvalid`. Covers: `baseUrl` not absolute http/https, carrying userinfo or a query, or with a path ending `/v1` or `/v1/messages` (Claude Code appends `/v1/messages`); `authTokenEnv` not a valid variable name (`[A-Za-z_][A-Za-z0-9_]*`, which rejects a pasted `sk-…` secret); a gateway key on a non-`claude` block or under `guardrailOverrides`; a gateway block with no `model`; an **owned variable** (§1.2) in `env`, checked case-insensitively; or `--settings` in the block's `extraArgs` |
| `GR2085` | warning | `ClaudeModelNameToGateway`. A model string would reach a gateway block and is a Claude model, meaning it contains `claude` anywhere (so `anthropic.claude-…` and `us.anthropic.claude-…` count too) or is an alias (`sonnet`, `opus`, `haiku`, `fable`, `opusplan`, `default`), case-insensitively and with any `[1m]`-style suffix removed first. Sources scanned: the block's `model`; a `routing` tier; `action.model`; a judge's frontmatter pin; and `--model` or `--fallback-model` in `extraArgs` (base and `guardrailOverrides`). A warning, because a LiteLLM `model_list` may map that name on purpose |
| `GR2086` | warning | `ClaudeGatewayModelsShareEndpoint`. `maxParallelism > 1`, and two or more distinct models resolve to one gateway. Gateways are compared after normalizing the host (`localhost` / `127.0.0.1` / `::1`) and a trailing `/`. The static half of §3.2's halt |

`GR2009`'s PATH probe of `command` still applies, because Claude Code is still what gets launched.

## 3. Preflight

This joins `PlanPreflightPhase` beside the openai-compat endpoint check, using the same pattern: it runs
before the DAG, burns no retries, and records a halt as the journal's top-level `halt`. A plan with no gateway
block makes **zero** connections, proven with a loopback listener that fails on any accepted connection.

### 3.1 Reachability

| Probe | Once per | Halt when |
|---|---|---|
| Managed-settings sources for this OS | run | any of them sets an owned or scrubbed variable (§1.2) |
| `authTokenEnv` | gateway block | unset or empty |
| `GET {baseUrl}/v1/models`, sending `Authorization: Bearer` | `baseUrl` | refused, DNS, timeout, TLS, 5xx, 401/403, or `model` not in `data[].id`. A 404/405 downgrades to a warning and skips the model check |
| `POST {baseUrl}/v1/messages`, `max_tokens: 256` | (`baseUrl`, `model`) | not a 200 with a content block (the error text is quoted). The larger `max_tokens` leaves room for a reasoning model's thinking (#759) |

### 3.2 Backend identity (D2)

LiteLLM answering says nothing about which model is actually loaded behind it. That is how #760's silent
substitution happens: a `Qwen3.8` alias served by a `llama-server` that still has 3.6 loaded. So the preflight
resolves the backend identity deterministically, for each (`baseUrl`, `model`):

1. **`GET {baseUrl}/model/info`** (LiteLLM). Find the entry for `model` and read `litellm_params.api_base`
   and `litellm_params.model`.
2. **Ask that backend what it has loaded:** `GET {api_base}/props` (`llama-server`: the model path or alias,
   and the per-slot `n_ctx`), or failing that `GET {api_base}/v1/models`.
3. **The identity** is the pair (normalized `api_base`, loaded model id). It is recorded in provenance as
   `BackendModel` (§4).

The preflight **halts** when:
- two distinct `model` strings in one plan resolve to the **same** backend identity (#760);
- an operator-declared `backendModel` does not match the loaded model id;
- `contextTokens` exceeds the backend's per-slot `n_ctx` (§1.3).

**When the identity cannot be resolved** (the gateway is not LiteLLM, `/model/info` is absent, or the backend
does not answer), nothing is claimed:
- provenance records `BackendModel: "unverified"`, and the run header says **"backend identity unverified"**
  for that block;
- a declared `backendModel` is reported as **"declared, not verified"**, never as matched.

**`ObservedModel` is a CLI echo on this path.** It is the model name Claude Code says it asked for, not
evidence of what served the request. Provenance labels it that way, and `BackendModel` is the only field that
makes a claim about the backend.

**Point-in-time.** This is checked once, before the DAG. A backend that swaps models mid-run is not detected.
D2 narrows #760's window; it does not close it.

### 3.3 Mid-run failures

These keep the shipped `ClaudeSignalClassifier` classification:
- connection refused, reset or error, and 429/503/529 → `Transient` (the bounded #115 pause);
- a model LiteLLM does not know → `Error`.

Summaries and pause notices gain `(via gateway <baseUrl>)`. The quarantine is untouched.

`guardrails providers check <block>` gains a manual `tool_use` round trip through the gateway, which exercises
the Anthropic-to-OpenAI tool-call translation.

## 4. Provenance, cost, telemetry

- **`AttemptProvenance.Gateway`:** the normalized `baseUrl`, without userinfo.
- **`AttemptProvenance.BackendModel`:** the resolved identity, or `"unverified"`.

Both are set by the gateway instance for every dispatch, so attempts, judges and overhead calls all carry them.
`TelemetryRow` gains the same two fields. Without them the #544 dogfood numbers could not be told apart from
Claude runs.

**Cost is `null` at the source.** The gateway instance never reports Claude Code's `total_cost_usd`, which is
priced from Anthropic's list and is fiction for Qwen. Token counts are kept. Because the cost is null at the
source, every consumer sees nothing: the attempt, the judge, and `AiMergeResolver.AddOverheadCost`.

The run-start `Note:` says how far `maxCostUsd` still applies:
- it **does not bind** when every prompt dispatch resolves to a gateway block;
- it **partially binds** when a tier can escalate to a non-gateway Claude block. Only that spend counts.

(Question `gateway-cost`.)

## 5. Tests and the live smoke

- **Unit (`ClaudePromptRunner`, `StreamJsonCliSession`):**
  - the exact scrub (including case variants on Windows), the owned values, the placeholder token, the alias
    set and `CLAUDE_CODE_SUBAGENT_MODEL`;
  - the composed `--settings` file, which carries `env`, `model`/`fallbackModel` and the merged containment
    hook, with exactly one `--settings` flag;
  - owned keys dropped when they arrive from a different block's settings;
  - a non-gateway block launches byte-identically to before.
- **Validator:** one test per `GR2084` clause (including a lowercase `anthropic_base_url` in `env` on
  Windows); per `GR2085` source and alias form (`us.anthropic.claude-…`, `Sonnet[1m]`, `--fallback-model`);
  `GR2086` with `localhost` versus `127.0.0.1`.
- **Preflight (integration):** a loopback fake gateway that serves `/v1/models`, `/model/info` and
  `/v1/messages`, plus a fake backend `/props`. Each §3.1 and §3.2 halt is covered, and so is the
  "unverified" disclosure. Zero connections with no gateway block. One probe per key, counted at the listener.
  The managed-settings halt uses a test-injected source path.
- **Provenance:** `Gateway`, `BackendModel` and `CostUsd: null` are read from the bytes of `run.json` and a
  telemetry row, including for an `ai-merge` dispatch.
- **Live smoke, `scripts/smoke/claude-gateway-live-smoke.ps1`.** Manual, never CI, and re-run on every
  Claude Code upgrade. It records `claude --version`.
  1. **Seeds hostile configuration:**
     - a hostile **user** config in the operator's real config dir: a canary `ANTHROPIC_API_KEY` in settings
       `env`, a canary `apiKeyHelper`, a redirecting `ANTHROPIC_BASE_URL`, and a `fallbackModel`;
     - the same set as **project** settings in the target repository.
  2. **Runs a one-task plan** through a recording proxy in front of LiteLLM. The run must be green.
  3. **Asserts over every recorded request** (`/v1/models`, `HEAD /api/hello`, `count_tokens`,
     `/v1/messages`, and subagent requests identified by `x-claude-code-agent-id`):
     - no canary appears in any header;
     - every request hit the declared gateway;
     - no request named a `claude-*` model;
     - every request carried a Bearer token and no `x-api-key`.
  4. **Records separately** any connection to `api.anthropic.com` (§1.2's disclosure).

  The smoke proves the unverified items: the environment names, the precedence of `CLAUDE_CONFIG_DIR` and
  `--settings`, and whether the subagent and background model aliases take effect.

## 6. Docs and the runbook replacement

- **README:** a "Running prompt tasks on a local model through a gateway" section, beside the Cursor section.
  It includes the §1.2 disclosures.
- **SSOT:**
  - §2 gains the new keys, mirrored into `.claude/skills/plan-breakdown/references/schemas.md`;
  - a new **§9.10**, "The `claude` runner over a gateway (#782)", holds §1–§4;
  - §9.6 gains `GR2084`–`GR2086` and the `GR2083` reservation.
- **The #570 runbook, replaced** (posted to #570 and #786 when this ships). The operator still runs
  `llama-server` and LiteLLM.

```jsonc
"maxParallelism": 1,
"promptRunners": {
  "default": "qwen36",
  "qwen36": { "kind": "claude", "baseUrl": "http://127.0.0.1:4000", "model": "Qwen",
              "backendModel": "qwen3.6-35b-a3b", "contextTokens": 65536 },
  // 3.8 needs its OWN llama-server (another port), mapped in LiteLLM's model_list.
  // The preflight halts if "Qwen3.8" resolves to the server that has 3.6 loaded.
  "qwen38": { "kind": "claude", "baseUrl": "http://127.0.0.1:4000", "model": "Qwen3.8",
              "backendModel": "qwen3.8-27b", "contextTokens": 65536 }
}
```

How the five #570 traps end up:
- **Trap 1** (Claude model names reaching LiteLLM): `GR2085`, plus the owned alias and subagent variables, plus
  the neutralized `fallbackModel`.
- **Trap 2** (the wrapper picking the model): gone, because there is no wrapper.
- **Trap 3** (parallel launches racing): `GR2086` warns, and §3.2's backend-identity check halts.
- **Trap 4** (services not started): the §3.1 preflight.
- **Trap 5** (timeouts): remains operator guidance.

## 7. The Bifrost dogfood

It is **gated on D2 and §1.3 shipping**, because without backend identity and the context setting its numbers
cannot be attributed. The maintainer runs it with the live UI. Its scope is question `dogfood-scope`.

| Metric | Source |
|---|---|
| turns to green; attempts per task | `run.json` (`NumTurns`, attempt count) |
| context overflows and compactions | attempts with a context-length error; compaction events in `claude-stream.jsonl`; whether `CLAUDE_CODE_MAX_CONTEXT_TOKENS` moved compaction earlier (§1.3) |
| malformed tool-call rate | `tool_use` blocks whose result is an input-validation or parse error, divided by all `tool_use` blocks, from `claude-stream.jsonl` |
| wall time | per attempt and per task, from the journal |
| backend identity | `BackendModel` on every attempt (it must be verified, not `"unverified"`) |

Results are recorded as a table in a comment on #786, with the run IDs.

## 8. Implementation handoff

Sequenced; each stage green before the next.

| # | Agent | filesTouched | Deliverable |
|---|---|---|---|
| 1 | `guardrails-harness-developer` | `src/Guardrails.Core/Model/PromptRunnerConfig.cs`, `src/Guardrails.Core/Loading/RawManifests.cs`, `src/Guardrails.Core/Loading/PlanLoader.cs`, `src/Guardrails.Core/Prompts/PromptRunnerRegistry.cs` | The keys; gateway configuration handed to the runner instance (D3) |
| 2 | `guardrails-harness-developer` | `src/Guardrails.Core/Prompts/ClaudePromptRunner.cs`, `src/Guardrails.Core/Prompts/StreamJsonCliSession.cs`, `src/Guardrails.Core/Execution/ProcessRunner.cs`, `src/Guardrails.Core/Prompts/WorktreeContainmentHook.cs` | §1.2: scrub, owned values, `CLAUDE_CONFIG_DIR`, the composed `--settings` with the merged hook, owned-key drops, the gateway summary suffix, null cost |
| 3 | `guardrails-harness-developer` | `src/Guardrails.Core/Loading/PlanValidator.cs`, `src/Guardrails.Core/Loading/DiagnosticCodes.cs` | `GR2084`–`GR2086`; the `GR2083` reservation |
| 4 | `guardrails-harness-developer` | `src/Guardrails.Cli/PlanPreflightPhase.cs`, `src/Guardrails.Cli/Commands/ProvidersCommand.cs` | §3.1–§3.2 preflight; the `providers check` gateway mode |
| 5 | `guardrails-harness-developer` | `src/Guardrails.Core/Journal/JournalModel.cs`, `src/Guardrails.Core/Telemetry/TelemetryRow.cs`, `src/Guardrails.Core/Execution/ActionRunner.cs`, `src/Guardrails.Core/Execution/GuardrailRunner.cs`, `src/Guardrails.Core/Execution/AiMergeResolver.cs` | `Gateway` / `BackendModel` provenance and telemetry; the ActionRunner splice handed to the gateway instance to compose |
| 6 | `guardrails-test-author` | `tests/Guardrails.Core.Tests/ClaudeGatewayEnvironmentTests.cs`, `tests/Guardrails.Integration.Tests/ClaudeGateway/ClaudeGatewayPreflightTests.cs` | §5's unit, validator, preflight and provenance tests |
| 7 | `guardrails-harness-developer` | `scripts/smoke/claude-gateway-live-smoke.ps1` | The hostile-config live smoke |
| 8 | `guardrails-skill-author` | `docs/plans/02-schemas-and-contracts.md`, `.claude/skills/plan-breakdown/references/schemas.md`, `README.md` | §6's documentation edits |

## 9. Devil's advocate

**"Two isolation layers plus a managed-settings check is heavy for a config feature."** Each layer closes a
different authority:
- the config directory removes user settings, `apiKeyHelper` and OAuth;
- `--settings` outranks project settings;
- managed settings cannot be outranked at all, so the harness can only detect them and refuse.

Dropping any one of the three leaves a path by which a request reaches a server the plan did not name, with
a credential it did not grant.

**"The backend probe is LiteLLM-specific."** Yes, and it says so. A gateway it cannot see through is
recorded as unverified instead of being assumed correct. `backendModel` gives the operator a way to state
the expectation, and the harness only claims it matched when it has checked.

**"The config directory strips the child's user skills and `CLAUDE.md`."** That is intended. A harness actor's
behavior should come from the plan and the repository, not from the operator's home directory, and the gateway
path is where that matters most, since the dogfood results must be reproducible.

## 10. Decisions for the maintainer

:::question
{ "id": "gateway-cost", "title": "What cost should a gateway dispatch record?", "mode": "single", "options": ["null at the source: the gateway runner instance never reports Claude Code's computed cost; tokens are kept; the run-start Note says maxCostUsd does not bind, or only partially binds when a tier can escalate to a non-gateway Claude block", "Keep Claude Code's computed total_cost_usd"], "recommended": "null at the source: the gateway runner instance never reports Claude Code's computed cost; tokens are kept; the run-start Note says maxCostUsd does not bind, or only partially binds when a tier can escalate to a non-gateway Claude block", "rationale": "Claude Code prices each call from Anthropic's list for the model name it thinks it used, which is fiction for local Qwen. Nulling it in the runner instance, not at each consumer, means the attempt, the judge and the ai-merge overhead sink all see the same null, and the telemetry cost column the #544 decision reads stays honest. Tokens are real and stay. The Note tells the operator exactly how much of the budget brake still applies.", "target": "human" }
:::

:::question
{ "id": "dogfood-scope", "title": "What Bifrost run should the #544 decision point rest on?", "mode": "single", "options": ["One Bifrost-shaped task on Qwen 3.6 and on Qwen 3.8, at least 3 runs per model, serial", "A 3 to 5 task Bifrost plan slice on Qwen 3.6, at least 3 runs, serial", "Both: the single task on each model first (at least 3 runs per model), then the plan slice on the better model (at least 3 runs), all serial, gated on backend identity and the context setting shipping"], "recommended": "Both: the single task on each model first (at least 3 runs per model), then the plan slice on the better model (at least 3 runs), all serial, gated on backend identity and the context setting shipping", "rationale": "The single task isolates tool-calling and context behavior per model cheaply. Only dependent tasks show whether Qwen through Claude Code carries real work, with state passing, retries and dependents reading transcripts. One run per cell is an anecdote. Three is the minimum that separates the model from luck, and elapsed time is not the constraint. The gate exists because numbers from an unverified backend or an unset context window could not be attributed.", "target": "human" }
:::
