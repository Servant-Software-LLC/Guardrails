---
charter-format-version: 1
---
# Architecture: first-class gateway support on the `claude` runner (#782)

**Status:** proposed design of record, for review in Charter before implementation.
**Issue:** #782, which is Phase 0-G of the approved #544 design
([`544-local-inference-actions.charter.md` §10, on branch `design/544-local-inference-actions`](https://github.com/Servant-Software-LLC/Guardrails/blob/design/544-local-inference-actions/docs/plans/544-local-inference-actions.charter.md)).
**Epic:** #786. **Replaces:** the `claude-local` wrapper runbook in #570. **Binds to:** `master` at `812cab3c`.

## What's being asked

The approved #544 sequencing answer is "gateway first". That means Claude Code, pointed through an
Anthropic-compatible gateway (LiteLLM) at a local model (`llama-server` running Qwen), becomes a **supported,
validated** configuration of the existing `kind: "claude"` runner. The dogfood results from that path then
decide how hard to push native #544.

Today it works only through an operator wrapper script (`claude-local`), and #570 lists five ways it fails
silently. This design moves the wrapper's job into two block keys. It adds checks for those traps, a preflight,
and provenance so the dogfood numbers can be attributed. It builds no new runner.

**Placement:** harness (config, env, validation, preflight, provenance), SSOT §2 and a new §9.10, README, and a
live smoke script. No skill changes beyond the schema mirror.

**Invariants in play:**
- **1 (deterministic gates):** untouched. The gateway changes which model answers, not how the gate decides.
- **5 (honest halts):** a dead gateway or a missing token halts before any task runs.
- **6 (light setup):** the operator still runs `llama-server` and LiteLLM. The harness never starts or
  manages them.

## 1. Config contract

```jsonc
"qwen36": {
  "kind": "claude",
  "baseUrl": "http://127.0.0.1:4000",   // NEW. Absolute http/https, WITHOUT a trailing /v1 (Claude Code appends /v1/messages)
  "authTokenEnv": "LITELLM_KEY",        // NEW, optional. The NAME of an env var; its value becomes ANTHROPIC_AUTH_TOKEN
  "model": "Qwen"                       // REQUIRED on a gateway block (GR2084)
}
```

Both keys are **block-level only** and apply only to `kind: "claude"`. Declaring either key under
`guardrailOverrides`, or on any other kind, is `GR2084`. A block with `baseUrl` is a **gateway block**. A
`claude` block without one is byte-identical to today, down to the child environment.

### 1.1 The child environment of a gateway block

`ClaudePromptRunner.BuildEnvironment` currently overlays the harness `GUARDRAILS_*` set, the output-token cap,
and then the user's `env` map, which wins last. Everything else is inherited from the operator's shell
(`ProcessRunner.ApplyEnvironment` scrubs only undeclared `GUARDRAILS_*`). For a gateway block it gains three
steps, in this order.

1. **Scrub the inherited routing and credentials.** Remove every inherited `ANTHROPIC_*` variable, plus
   `CLAUDE_CODE_USE_BEDROCK` and `CLAUDE_CODE_USE_VERTEX`. The failures this prevents:
   - an ambient `ANTHROPIC_API_KEY` would be sent to the gateway as `x-api-key`, leaking a real Anthropic key
     to a server that may not be local;
   - an ambient `ANTHROPIC_BASE_URL` or `ANTHROPIC_MODEL` could route around the block;
   - a Bedrock or Vertex switch would send the request to a cloud provider entirely.

   This needs `ApplyEnvironment` to accept removals. Today it only overlays values.
2. **Set the harness-owned values:**
   - `ANTHROPIC_BASE_URL` = `baseUrl`;
   - `ANTHROPIC_AUTH_TOKEN` = the value of `$authTokenEnv`. If `authTokenEnv` is absent, it is set to the
     fixed, non-secret string `guardrails-gateway-no-auth`. Always setting the token means Claude Code never
     falls back to the operator's own login and sends that credential to the gateway (asserted by the live
     smoke, §5);
   - `ANTHROPIC_DEFAULT_HAIKU_MODEL` and `ANTHROPIC_SMALL_FAST_MODEL` = the block's `model`. Claude Code's
     background small-model calls then go to the same model instead of a `claude-haiku-*` name that LiteLLM
     cannot route (#570's open unknown);
   - `CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC=1`. Only local models are approved, so Claude Code's
     telemetry, error reporting and update checks should not leave the machine. This one is a default, and
     the `env` map may override it.
3. **Apply the user's `env` map last,** as today. An entry for one of the three owned credential and routing
   variables (`ANTHROPIC_BASE_URL`, `ANTHROPIC_AUTH_TOKEN`, `ANTHROPIC_API_KEY`) is `GR2084`: two sources for
   one value is a surprise whichever one wins. Other `ANTHROPIC_*` entries (for example
   `ANTHROPIC_CUSTOM_HEADERS`) are passed through.

**`guardrailOverrides.env` replaces `env` wholesale** (`PromptRunnerSettings.With`). Steps 1 and 2 still run
for judges on a gateway block, because they come from block-level keys and not from the settings merge.

**`authTokenEnv` names a variable that is unset or empty at run start:** the run halts in preflight (§3) and
names the variable. It never proceeds with the placeholder, because the operator declared that a token is
needed.

The Claude Code environment variable names above are quarantined in `ClaudePromptRunner`, next to
`CLAUDE_CODE_MAX_OUTPUT_TOKENS`. They are Claude Code's documented names at the time of writing. The live
smoke is what proves the installed CLI honors them.

## 2. Validation (static, offline)

`GR2083` is reserved by name for #544. This design takes **GR2084–GR2086** and moves the marker to `GR2087`.

| Code | Sev | Rule |
|---|---|---|
| `GR2084` | error | `ClaudeGatewayBlockInvalid`: `baseUrl` is not an absolute http/https URL, or its path ends in `/v1`; `authTokenEnv` is not a valid variable name (`[A-Za-z_][A-Za-z0-9_]*`, which also rejects a pasted `sk-…` secret); either key appears on a non-`claude` block or under `guardrailOverrides`; a gateway block has no `model`; or the `env` map sets `ANTHROPIC_BASE_URL`, `ANTHROPIC_AUTH_TOKEN` or `ANTHROPIC_API_KEY` on a gateway block |
| `GR2085` | warning | `ClaudeModelNameToGateway`: a model string that is a Claude id (`claude-*`) or alias (`sonnet`, `opus`, `haiku`, `opusplan`) would reach a gateway block, from the block's own `model`, a `routing` tier, a task's `action.model`, or a judge's frontmatter pin. This is #570 trap 1. It is a warning rather than an error because a LiteLLM `model_list` may alias that name on purpose |
| `GR2086` | warning | `ClaudeGatewayModelsShareEndpoint`: `maxParallelism > 1` and two or more distinct models resolve to one `baseUrl`. Two models behind one gateway work if each has its own backend. They fail or thrash if both share one single-model `llama-server` |

A **gateway block's `model` is required** because without `--model` Claude Code sends its own default model id.
A **gateway block also draws a run-start `Note:`** stating that cost is not recorded (§4). GR2009's PATH probe
of `command` still applies, because Claude Code is still what gets launched.

## 3. Preflight and mid-run failures

**Run preflight.** This joins `PlanPreflightPhase` beside the openai-compat endpoint check, with the same
pattern: pre-DAG, so it burns no task retries; it halts the run before any task is scheduled; and it is
recorded as the journal's top-level `halt`. It runs once per distinct `baseUrl`, and then once per distinct
(`baseUrl`, `model`):

| Probe | Pass | Halt | Warn |
|---|---|---|---|
| `authTokenEnv` resolves to a non-empty value | yes | unset or empty, naming the variable | — |
| `GET {baseUrl}/v1/models`, with the token as `Authorization: Bearer` | 200, with `model` among `data[].id` | refused, DNS, timeout, TLS, 5xx, 401/403; or the model is not listed | 404/405: the listing is not offered, so the model assertion is skipped |
| `POST {baseUrl}/v1/messages`, one user turn, `max_tokens: 256` | 200 with at least one content block | any non-200, with the gateway's error text quoted | — |

The message probe exists because LiteLLM answering `/v1/models` says nothing about whether `llama-server`
behind it is up and has the model loaded. `max_tokens` is 256 rather than a handful because a reasoning model
spends its first tokens thinking (#759). A plan with no gateway block makes **zero** connections, proven by a
loopback listener that fails on any accepted connection, exactly as plan 28 proves it.

**`guardrails providers check <block>`** gains a gateway mode. It performs a `tool_use` round trip through the
gateway, which exercises the Anthropic-to-OpenAI tool-call translation. It is manual and not in CI, like the
rest of `providers check`.

**Mid-run failures keep the shipped classification.** `ClaudeSignalClassifier` already treats `connection
refused/reset/error` and 429/503/529 as `Transient`, which is the bounded #115 pause with no retry burn.
Examples:
- a LiteLLM restart is classified correctly;
- a crashed `llama-server` behind a live LiteLLM comes back as a 5xx carrying "connection refused", and is
  also `Transient`;
- a model LiteLLM does not know is a 400, and is `Error`.

The one change is that a gateway block's attempt summary and pause notice add `(via gateway <baseUrl>)`, so
the operator knows which server to restart. The classifier's vendor quarantine is not touched.

## 4. Provenance, cost and telemetry

- **`AttemptProvenance.Gateway`** (a new string field) holds the `baseUrl` with any userinfo removed. It is set
  for every attempt and judge that ran on a gateway block. `Runner`, `Model` and `RequestedModel` already
  record the block and the model string. The telemetry row (SSOT §15.2) gains the same field. Without it, the
  #544 dogfood numbers could not be told apart from Claude runs.
- **Cost is recorded as `null` for gateway blocks.** Claude Code's `total_cost_usd` is computed from
  Anthropic's price list for whatever model name it believes it used. Against Qwen it is fiction, and a
  fictional number would feed `maxCostUsd` and the telemetry corpus as if real. The cost is `null`, not `0`,
  because a zero would claim the run was free (§9's cost rule). `Usage` (token counts) is kept, because the
  gateway reports real counts. The consequence is that `maxCostUsd` cannot bind on a gateway block, as with
  `cursor` and `openai-compat`. The run-start `Note:` says so. Question `gateway-cost`.

## 5. Tests and the live smoke

- **Unit (`ClaudePromptRunner`):**
  - the gateway environment exactly: scrub set, owned values, placeholder token, background-model aliasing,
    and that the `env` map wins for non-owned keys;
  - a non-gateway block's environment is byte-identical to before;
  - a judge under `guardrailOverrides.env` still gets steps 1 and 2.
- **Unit (`ApplyEnvironment`):** removals are applied, and an inherited `ANTHROPIC_API_KEY` is absent from the
  child.
- **Validator:** one test per `GR2084` clause, and per `GR2085` source (block `model`, routing tier,
  `action.model`, judge pin). `GR2086` fires at `maxParallelism: 2` and not at 1.
- **Preflight (integration, loopback fake gateway):** each row of the §3 table; zero connections with no
  gateway block; one probe per distinct (`baseUrl`, `model`), counted at the listener.
- **Provenance:** `Gateway` and `CostUsd: null` are read back from the bytes of `run.json`, not from the
  in-memory object.
- **Live smoke, `scripts/smoke/claude-gateway-live-smoke.ps1`** (manual, never CI, the same posture as
  `cursor-live-smoke.ps1`):
  - it starts a small recording proxy in front of the operator's LiteLLM and runs a one-task plan through it;
  - it asserts the run is green;
  - it asserts every `/v1/messages` request carried the block's model, a Bearer token and **no** `x-api-key`;
  - it asserts no request named a `claude-*` model.

  The smoke is what proves the environment names in §1.1 against the installed Claude Code.

## 6. Docs and the runbook replacement

- **README:** a new "Running prompt tasks on a local model through a gateway" section, beside the existing
  Cursor section.
- **SSOT:**
  - §2 gains `baseUrl` and `authTokenEnv` in the canonical block, mirrored byte-identically into
    `.claude/skills/plan-breakdown/references/schemas.md`;
  - a new **§9.10**, "The `claude` runner over a gateway (#782)", holds §1–§4 of this document;
  - §9.6 gains `GR2084`–`GR2086`.
- **The #570 runbook, replaced** (posted to #570 and #786 once this ships). The operator still runs
  `llama-server` and LiteLLM; only the wrapper script goes away.

```jsonc
"maxParallelism": 1,
"promptRunners": {
  "default": "qwen36",
  "qwen36": { "kind": "claude", "baseUrl": "http://127.0.0.1:4000", "model": "Qwen" },
  // Qwen 3.8 needs its own backend: a second llama-server on another port, with LiteLLM's
  // model_list mapping "Qwen3.8" to it. One llama-server holds one model; GR2086 warns
  // if both are used in one parallel run.
  "qwen38": { "kind": "claude", "baseUrl": "http://127.0.0.1:4000", "model": "Qwen3.8" }
}
```

Three of the five #570 traps are closed by this change:
- **trap 1** (a Claude model name reaching LiteLLM) by `GR2085` and the background-model aliasing;
- **trap 2** (`claude-local` choosing the model from its first argument) by construction, since there is no
  wrapper;
- **trap 4** (services not started) by the preflight.

Trap 3 (parallel launches racing to start the server) disappears, because the harness starts nothing, and
`GR2086` covers the model-sharing half. Trap 5 (timeouts on the slower model) remains operator guidance.

## 7. The Bifrost dogfood, and what it must record

This is the evidence the #544 decision point is waiting for.

- **Run:** see question `dogfood-scope`. It runs serial, with the maintainer using the live UI, and the
  maintainer launches it.
- **Metrics:**

  | Metric | Source |
  |---|---|
  | turns to green | `NumTurns` per attempt, plus attempts per task, from `run.json` |
  | context overflows | attempts whose summary carries a context-length error, and whether Claude Code auto-compacted in time for a 64K backend (Claude Code does not know the backend's window, which is the #544 hypothesis being tested) |
  | malformed tool-call rate | `tool_use` blocks whose result is an input-validation or parse error, divided by all `tool_use` blocks, from `claude-stream.jsonl` |
  | wall time | per attempt and per task, from the journal |

  All of it is attributable through `Gateway` (§4) and lands in the telemetry corpus automatically.
- **Recorded:** as a short results table in a comment on #786, with the run IDs, so the decision point cites
  data rather than impressions.

## 8. Implementation handoff

Sequenced; each stage green before the next.

| # | Agent | filesTouched | Deliverable |
|---|---|---|---|
| 1 | `guardrails-harness-developer` | `src/Guardrails.Core/Model/PromptRunnerConfig.cs`, `src/Guardrails.Core/Loading/RawManifests.cs`, `src/Guardrails.Core/Loading/PlanLoader.cs` | The two keys and the gateway-block predicate |
| 2 | `guardrails-harness-developer` | `src/Guardrails.Core/Prompts/ClaudePromptRunner.cs`, `src/Guardrails.Core/Execution/ProcessRunner.cs` | §1.1 environment steps; removals in `ApplyEnvironment`; the `(via gateway …)` summary suffix |
| 3 | `guardrails-harness-developer` | `src/Guardrails.Core/Loading/PlanValidator.cs`, `src/Guardrails.Core/Loading/DiagnosticCodes.cs` | `GR2084`–`GR2086` |
| 4 | `guardrails-harness-developer` | `src/Guardrails.Cli/PlanPreflightPhase.cs`, `src/Guardrails.Cli/Commands/ProvidersCommand.cs` | The §3 preflight and the `providers check` gateway mode |
| 5 | `guardrails-harness-developer` | `src/Guardrails.Core/Journal/JournalModel.cs`, `src/Guardrails.Core/Execution/ActionRunner.cs`, `src/Guardrails.Core/Execution/GuardrailRunner.cs` | `AttemptProvenance.Gateway`; null cost for gateway blocks |
| 6 | `guardrails-test-author` | `tests/Guardrails.Core.Tests/ClaudeGatewayEnvironmentTests.cs`, `tests/Guardrails.Integration.Tests/ClaudeGateway/ClaudeGatewayPreflightTests.cs` | §5's unit, validator, preflight and provenance tests |
| 7 | `guardrails-harness-developer` | `scripts/smoke/claude-gateway-live-smoke.ps1` | The live smoke with its recording proxy |
| 8 | `guardrails-skill-author` | `docs/plans/02-schemas-and-contracts.md`, `.claude/skills/plan-breakdown/references/schemas.md`, `README.md` | §6's documentation edits |

## 9. Devil's advocate

**"This is sugar over the `env` map, which already works."** Partly. The `env` map can set
`ANTHROPIC_BASE_URL` today. What it cannot do:
- remove an inherited `ANTHROPIC_API_KEY`, which then leaks to the gateway;
- tell `validate` that a block is a gateway, so trap 1 cannot be warned about;
- give the preflight an endpoint to probe;
- give provenance a fact to record.

Every one of those is a failure that is silent today. The keys exist so the harness knows what the operator
meant.

**"Setting Claude Code environment variables couples us to vendor names that can change."** True, and it is
already the case for `CLAUDE_CODE_MAX_OUTPUT_TOKENS`. The names are quarantined in one class, and the live
smoke is the check that catches a rename, because a renamed variable shows up there as a `claude-haiku-*`
request or an `x-api-key` header.

## 10. Decisions for the maintainer

:::question
{ "id": "gateway-cost", "title": "What cost should a gateway block record?", "mode": "single", "options": ["null: record no cost; tokens are kept; maxCostUsd cannot bind on gateway blocks and the run-start Note says so", "Keep Claude Code's computed total_cost_usd"], "recommended": "null: record no cost; tokens are kept; maxCostUsd cannot bind on gateway blocks and the run-start Note says so", "rationale": "Claude Code prices the call from Anthropic's list for the model name it thinks it used; against local Qwen that number is fiction. Feeding it to maxCostUsd would halt or not halt a run on a made-up figure, and feeding it to the telemetry corpus would corrupt the cost column the #544 decision reads. Tokens are real and stay. The cost is that the budget brake does not apply to local work, which is already true of cursor and openai-compat.", "target": "human" }
:::

:::question
{ "id": "dogfood-scope", "title": "What Bifrost run should the #544 decision point rest on?", "mode": "single", "options": ["One Bifrost-shaped task (edit two files, add a test, build, test), run once on Qwen 3.6 and once on Qwen 3.8, serial", "A real slice of a Bifrost plan (3 to 5 dependent tasks) on Qwen 3.6, serial", "Both: the single task on each model first, then the plan slice on the better model"], "recommended": "Both: the single task on each model first, then the plan slice on the better model", "rationale": "The single task isolates tool-calling and context behavior per model cheaply, which is what distinguishes the gateway path from native. But the decision is really about whether Qwen through Claude Code can carry real work, which only dependent tasks show, with state passing, retries and dependents reading transcripts. Running the slice only on the better model keeps the attended time down.", "target": "human" }
:::
