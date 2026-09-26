# Runbook: Qwen 3.6 and Qwen 3.8 through a gateway, without `claude-local` (#782, replaces the #570 runbook)

> **For the lead:** post this to #570 and #786 once #782 is merged and released. Everything below describes
> the shipped behavior; the contract is `docs/plans/02-schemas-and-contracts.md` §9.10.

The `claude-local` wrapper script is retired. A `kind: "claude"` block with a `baseUrl` now does what the
wrapper did, and more: Guardrails sets Claude Code's gateway environment itself, isolates the child from your
`~/.claude`, pins every model alias to the block's model, and checks the gateway and the loaded model before any
task runs. You still run `llama-server` and LiteLLM yourself.

## 1. Start the services

One `llama-server` holds one model, so each Qwen version needs its own server on its own port. Adjust the
model paths to yours:

```bash
llama-server -m /models/Qwen3.6-35B-A3B-Q4_K_M.gguf --alias Qwen3.6-35B-A3B --jinja -c 65536 -np 1 --port 8080
llama-server -m /models/Qwen3.8-27B-Q4_K_M.gguf     --alias Qwen3.8-27B     --jinja -c 65536 -np 1 --port 8081
```

- `--alias` gives the backend a name the preflight can match exactly. Without it, `llama-server` reports its
  model path, and `backendModel` is matched as a substring of the file name instead.
- `contextTokens` below must be the **per-slot** window: `-c` divided by `-np`. With `-c 65536 -np 1` that is
  65536. With `-np 2` it would be 32768, and a `contextTokens` above the per-slot window halts the run.

```yaml
# litellm.yaml
model_list:
  - model_name: Qwen
    litellm_params:
      model: openai/Qwen3.6-35B-A3B
      api_base: http://127.0.0.1:8080/v1
      api_key: none
  - model_name: Qwen3.8
    litellm_params:
      model: openai/Qwen3.8-27B
      api_base: http://127.0.0.1:8081/v1
      api_key: none
```

```bash
litellm --config litellm.yaml --port 4000
```

Give each `model_name` exactly one `api_base`. A `model_name` that LiteLLM load-balances across several backends
has no single identity, so the preflight records it as "unverified".

If LiteLLM has a `master_key`, export it before running Guardrails and name the variable in the block:

```bash
export LITELLM_KEY=sk-...            # PowerShell: $env:LITELLM_KEY = 'sk-...'
```

## 2. The config

In the plan's `guardrails.json`:

```json
"maxParallelism": 1,
"promptRunners": {
  "default": "qwen36",
  "qwen36": {
    "kind": "claude",
    "command": "claude",
    "baseUrl": "http://127.0.0.1:4000",
    "model": "Qwen",
    "backendModel": "qwen3.6-35b-a3b",
    "contextTokens": 65536
  },
  "qwen38": {
    "kind": "claude",
    "command": "claude",
    "baseUrl": "http://127.0.0.1:4000",
    "model": "Qwen3.8",
    "backendModel": "qwen3.8-27b",
    "contextTokens": 65536
  }
}
```

- Add `"authTokenEnv": "LITELLM_KEY"` to each block if you exported a key. It takes the variable's **name**,
  never the key. Without it, a fixed placeholder token is sent.
- Keep `"command": "claude"`. A block's `command` defaults to the block's NAME, so without it Guardrails would
  try to launch a program called `qwen36` (`validate` warns with `GR2009`).
- `baseUrl` is the gateway's root, without `/v1`.
- `model` is required and must be a `model_name` from `litellm.yaml`.
- Declare the version in `backendModel` (`qwen3.6-35b-a3b`, not `qwen`). A declared value that does not match
  what the backend has loaded halts the run.
- To run one model only, keep just its block and point `default` at it. To send one task to 3.8, set
  `"runner": "qwen38"` in that task's `action`.
- Don't put `ANTHROPIC_BASE_URL`, `ANTHROPIC_AUTH_TOKEN`, the `ANTHROPIC_DEFAULT_*_MODEL` variables,
  `CLAUDE_CODE_SUBAGENT_MODEL`, `CLAUDE_CODE_MAX_CONTEXT_TOKENS`, `CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC` or
  `CLAUDE_CONFIG_DIR` in `env`, or `--settings` in `extraArgs`. Guardrails owns them, and `validate` rejects them
  (`GR2084`, a malformed gateway block).

## 3. Check, then run

```bash
guardrails validate <plan>/
guardrails providers check <plan>/ qwen36     # a tool_use round trip through LiteLLM
guardrails providers check <plan>/ qwen38
guardrails run <plan>/
```

`providers check` sends one tool definition through the gateway and returns a `tool_result`, which exercises
LiteLLM's Anthropic-to-OpenAI tool-call translation. An "UNMET" step there means Claude Code's tool calls won't
work through that model either.

Before any task runs, the preflight halts the run, with the reason, when:
- a managed Claude Code settings file on this machine, or the target repo's `.claude/settings.json` or
  `.claude/settings.local.json`, sets anything that could send a request elsewhere or with another credential
  (`ANTHROPIC_BASE_URL`, `ANTHROPIC_API_KEY`, `CLAUDE_CODE_USE_BEDROCK`, `apiKeyHelper` and the like). The
  message names the file and the key;
- a block names `authTokenEnv` and that variable (`LITELLM_KEY` here) is unset or empty;
- LiteLLM is not answering, rejects the key, doesn't list `Qwen` or `Qwen3.8`, or can't return a reply to a short
  message;
- the server behind `Qwen3.8` doesn't have `qwen3.8-27b` loaded, `contextTokens` exceeds its per-slot window, or
  `Qwen` and `Qwen3.8` resolve to the same loaded model.

On success the run header names each backend, for example:

```
Gateway: block 'qwen36' → http://127.0.0.1:4000, model 'Qwen': backend http://127.0.0.1:8080 Qwen3.6-35B-A3B (backendModel 'qwen3.6-35b-a3b' matched).
Gateway: block 'qwen38' → http://127.0.0.1:4000, model 'Qwen3.8': backend http://127.0.0.1:8081 Qwen3.8-27B (backendModel 'qwen3.8-27b' matched).
```

If a line says `backend identity unverified`, the numbers from that run can't be attributed to a model; fix that
before using the run for the #544 decision.

## 4. The five #570 traps

| Trap | Now |
|---|---|
| 1. Claude model names reaching LiteLLM | Every model alias, the subagent model and `fallbackModel` are pinned to the block's `model`. A Claude name that would still reach a gateway block (a task's `action.model`, a `--model` in `extraArgs`) draws a `GR2085` warning at `validate`. |
| 2. The wrapper picking the model | Gone: there is no wrapper. The block's `model` is the model. |
| 3. Parallel launches racing one server | `GR2086` warns when `maxParallelism > 1` and two models share a gateway, and the preflight halts if two model names are served by the same loaded model. |
| 4. Services not started | The preflight halts before any task, naming the unreachable gateway or the failing model. |
| 5. Timeouts | Still operator guidance: a local model is slower than Claude, so size the task's `timeoutSeconds` for it. |

## 5. What to know

- **No dollar figures.** Gateway attempts record no cost, and token usage is shown in its place
  (`48.2k tok (gateway)`). `--max-cost-usd` doesn't limit gateway spend; the run says so at startup.
- **A clean Claude Code profile per run.** Claude Code runs with its own empty config directory under
  `logs/<runId>/claude-config/`, so your `~/.claude` settings, `CLAUDE.md`, skills, memory, MCP servers and
  stored login aren't used. Session transcripts land there too. Claude Code also has no record of trusting the
  repo, so some project settings may not apply; treat the block's `allowedTools` as the whole grant.
- **The token is visible to the agent's shell commands.** With the placeholder that is harmless. With a real key
  for a remote gateway, any command the model runs can read it.
- **Some traffic may still leave the machine.** Claude Code's nonessential traffic is turned off, but some
  features (fast-mode checks, WebFetch's safety check) are documented to call `api.anthropic.com` directly.
- **The identity check is point-in-time.** It runs once, at the start. Don't reload a server with a different
  model while a run is in progress.
- **Not supported by Anthropic.** Claude Code calling a non-Claude model through a gateway is outside what Claude
  Code supports. Re-run `scripts/smoke/claude-gateway-live-smoke.ps1` after every Claude Code upgrade.
- Each attempt's provenance in `run.json`, its telemetry row and its `attempt-finished` event carry `gateway`
  and `backendModel`, so the dogfood numbers can be told apart from Claude runs.
