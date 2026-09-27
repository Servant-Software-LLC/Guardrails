# Local inference setup: running Guardrails on Qwen with no cloud model

This guide takes a Mac from nothing to a Guardrails run whose every prompt goes to a **local** model. It also
covers the interactive skills (`/plan-breakdown`, `/guardrails-review`). It was written for company-approved Qwen
models served by [`llama-server`](https://github.com/ggml-org/llama.cpp) behind [LiteLLM](https://docs.litellm.ai/),
but the steps apply to any model you can serve that way.

It is written so a person **or an AI coding agent** can follow it top to bottom. Each step ends with a check,
and you shouldn't move on until that check passes.

> **Status: experimental.** This needs Guardrails **1.25.0 or later** (#782). Anthropic documents gateways for its
> own models; running Claude Code against a non-Claude model through a gateway is outside what Claude Code
> supports. Guardrails checks as much of it as it can before any task runs. Progress and results are tracked in
> epic [#786](https://github.com/Servant-Software-LLC/Guardrails/issues/786).

**Already have a `claude-local` setup?** Skip to
[Worked example: an existing `claude-local` setup](#worked-example-an-existing-claude-local-setup). It reuses what you
have and gives the exact checks and runner block, all verified on a MacBook.

## How it fits together

```
guardrails run ─► claude (Claude Code CLI) ─► LiteLLM :4000 ─► llama-server :8080 (Qwen 3.6)
                                                             └► llama-server :8081 (Qwen 3.8)
```

Guardrails still launches the Claude Code CLI for every prompt. A `claude` runner block with a `baseUrl` points it
at LiteLLM, and Guardrails configures that child itself. No Anthropic account, API key or subscription usage is
involved. You start `llama-server` and LiteLLM yourself; Guardrails never starts them.

| Stage | Who talks to the model | How it goes local |
|---|---|---|
| `guardrails run`: task actions, prompt judges, overwatch, triage, AI-merge, between-wave breakdown | Guardrails' runner | A `claude` block with `baseUrl` (step 5). Fully checked by the preflight. |
| `guardrails breakdown` (headless CLI) | Guardrails' runner | Same block, same preflight |
| `/plan-breakdown` and `/guardrails-review` (interactive skills) | **Your** Claude Code session | You point your session at LiteLLM yourself (step 9). Guardrails does not govern this session. |
| Charter review | No model | Nothing to do |

## 1. Prerequisites

| Need | Check it | Notes |
|---|---|---|
| Guardrails 1.25.0+ | `guardrails --version` | `brew update && brew upgrade guardrails`, or `dotnet tool update -g ServantSoftware.Guardrails` |
| Claude Code CLI | `claude --version` | Only the program is used. No Anthropic login is needed for gateway runs. |
| `llama.cpp` built for Apple silicon (Metal) | `<llama.cpp>/build/bin/llama-server --version` | A recent build. Tool calling needs the `--jinja` chat-template support. |
| The model files | `ls <models>/*.gguf` | For example `Qwen3.6-35B-A3B-MXFP4_MOE.gguf` and `Qwen3.8-27B-Q4_K_M.gguf` |
| LiteLLM proxy | `litellm --version` | `pip install 'litellm[proxy]'` |
| `python3`, `curl` | `python3 --version` | Used by the checks below |
| The .NET SDK your **target repo** pins | `cd <repo> && dotnet --version` | This is for the plan's own build and test guardrails, not for Guardrails. For example, Bifrost's `global.json` needs 10.0.400. `curl -sSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh && bash /tmp/dotnet-install.sh --version <ver> --install-dir "$HOME/.dotnet"` |
| *(Optional, for the smoke test only)* PowerShell 7 and a Guardrails source checkout | `pwsh --version` | `brew install powershell/tap/powershell`, then `git clone https://github.com/Servant-Software-LLC/Guardrails` |

**Memory:** each loaded model uses roughly its file size in unified memory, plus its context cache. Running both
Qwen models at once needs room for both. If that is too much, run only one (step 2).

## 2. Start `llama-server`

One `llama-server` holds one model, so each model gets its own server on its own port. Replace `<llama.cpp>` and
`<models>` with your paths:

```bash
<llama.cpp>/build/bin/llama-server -m <models>/Qwen3.6-35B-A3B-MXFP4_MOE.gguf \
  --spec-type draft-mtp --alias Qwen3.6-35B-A3B --jinja -c 65536 -np 1 --metrics --port 8080

<llama.cpp>/build/bin/llama-server -m <models>/Qwen3.8-27B-Q4_K_M.gguf \
  --alias Qwen3.8-27B --jinja -c 65536 -np 1 --metrics --port 8081
```

Each flag matters:
- **`--jinja`** turns on the chat template's tool-call support. Without it, Qwen's tool calls can come back as
  plain text, and Claude Code cannot act on them.
- **`--alias`** is the name the Guardrails preflight uses to confirm which model is loaded. **Keep it honest:** if
  you reuse a launch line with `--alias Qwen3.8-27B` over the 3.6 file, the check believes the alias. If in doubt,
  drop `--alias` and the file name is used instead.
- **`-c 65536 -np 1`** gives one slot a 65,536-token window. `contextTokens` (step 5) must not exceed the window
  **one slot** reports. Read it from `/props` (`default_generation_settings.n_ctx`) rather than computing it: older
  `llama.cpp` builds split `-c` across `-np` slots, while newer ones share one KV cache, so each slot reports the
  full window. The preflight compares against that reported number.
- `--spec-type draft-mtp` is multi-token-prediction drafting for the 3.6 MoE build. It is a speedup, not a
  requirement.

Run each in its own terminal, or with `nohup … > llama-3.6.log 2>&1 &`.

**Check:**

```bash
curl -s http://127.0.0.1:8080/props | python3 -c 'import sys,json; p=json.load(sys.stdin); print(p.get("model_alias"), p.get("model_path"))'
curl -s http://127.0.0.1:8081/props | python3 -c 'import sys,json; p=json.load(sys.stdin); print(p.get("model_alias"), p.get("model_path"))'
```

Each should print its own alias and file. **Two ports reporting the same model is the silent 3.6-for-3.8
substitution this setup exists to prevent.**

## 3. Start LiteLLM

Save this as `litellm.yaml`. Give each `model_name` exactly **one** `api_base`: a name LiteLLM load-balances
across several servers has no single identity, and the preflight can't verify it.

```yaml
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
litellm --config litellm.yaml --host 127.0.0.1 --port 4000
```

**Does your LiteLLM require a key?** If its config (or environment) sets a `master_key`, then **every** request
must carry that key, including Guardrails' requests. **A `claude-local` setup does:** it keeps the key as
`LITELLM_MASTER_KEY` in its `.env` file. Find out with a request that carries no key:

```bash
curl -s -o /dev/null -w "%{http_code}\n" http://127.0.0.1:4000/v1/models
```

- `200` means no key is needed. Skip to the check below.
- `400` (LiteLLM's `"No connected db."`), `401` or `403` means a key is required. Export it in the shell you'll run
  Guardrails from. You'll give Guardrails the variable's **name**, never the key:

  ```bash
  # claude-local setup: load its .env (exports LITELLM_MASTER_KEY)
  set -a; source <claude-local folder>/.env; set +a
  # any other setup:
  export LITELLM_MASTER_KEY='sk-...'
  ```

  Then add `"authTokenEnv": "LITELLM_MASTER_KEY"` to each gateway block in step 5. Without it, Guardrails sends a
  placeholder token, LiteLLM rejects it, and the run stops before any task with **"the gateway refused the
  credential"** (#791).

**Check:**

```bash
curl -s http://127.0.0.1:4000/v1/models ${LITELLM_MASTER_KEY:+-H "Authorization: Bearer $LITELLM_MASTER_KEY"} \
  | python3 -c 'import sys,json; print([m["id"] for m in json.load(sys.stdin)["data"]])'
```

This should print both `Qwen` and `Qwen3.8`.

## 4. Check what Claude Code will pick up from your settings

Guardrails isolates each gateway run from your `~/.claude`. It still **halts** if either of these sets anything
that could send a request elsewhere or with another credential:
- a *managed* Claude Code setting on the machine: `/Library/Application Support/ClaudeCode/managed-settings.json`,
  a `managed-settings.d/` drop-in, or the `com.anthropic.claudecode` configuration profile;
- the target repo's `.claude/settings.json` or `.claude/settings.local.json`.

Look now so it doesn't surprise you mid-setup:

```bash
cat "/Library/Application Support/ClaudeCode/managed-settings.json" 2>/dev/null
ls "/Library/Application Support/ClaudeCode/managed-settings.d" 2>/dev/null
cat <repo>/.claude/settings.json <repo>/.claude/settings.local.json 2>/dev/null
```

Keys that halt a run: an `env` entry for `ANTHROPIC_*`, `CLAUDE_CODE_USE_*` (Bedrock/Vertex/Foundry), the model-alias
variables or `CLAUDE_CONFIG_DIR`; `apiKeyHelper`; `forceLoginMethod`/`forceLoginGatewayUrl`; and, in managed settings
only, `model`, `fallbackModel` or `availableModels`. A managed file is your company's to change. If one blocks you,
say so on #786 rather than editing it.

## 5. Point the plan at the gateway

In the plan's `guardrails.json`, set `maxParallelism` and replace `promptRunners`:

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

- **`"command": "claude"`** must stay. A block's `command` defaults to the block's *name*, so without it Guardrails
  would try to run a program called `qwen36`.
- **`baseUrl`** is LiteLLM's root, without `/v1`.
- **`model`** is required, and must be a `model_name` from `litellm.yaml`. Every Claude Code model alias,
  background model and subagent model is pinned to it.
- **`backendModel`** is what the server must have *loaded*. Include the version (`qwen3.6-35b-a3b`, not `qwen`).
  A mismatch stops the run before any task.
- **`contextTokens`** must not exceed the per-slot window `/props` reports (step 2); the run stops if it does. It can
  be smaller. Claude Code then compacts earlier, which keeps a local model's turns faster (64K is a good start).
- If step 3 showed that LiteLLM requires a key, add `"authTokenEnv": "LITELLM_MASTER_KEY"` to each block. Run
  every `guardrails` command below from the shell that exported it.
- **Running one model only:** keep just its block and point `default` at it. To send one task to 3.8, set
  `"runner": "qwen38"` in that task's `action`.
- **Timeouts:** local models are slower than Claude, especially Qwen 3.8. If attempts end as `timeout`, raise the
  task's `timeoutSeconds` rather than retrying blindly.
- **Don't** add `ANTHROPIC_*`, `CLAUDE_CODE_*` or `CLAUDE_CONFIG_DIR` to a block's `env`, or `--settings`,
  `--model` or `--fallback-model` to its `extraArgs`. Guardrails owns them, and `validate` rejects them (`GR2084`).

Editing `guardrails.json` changes the plan's hash. If `validate` then warns that the plan is not reviewed
(`GR2025`), re-mark it once you're happy: `guardrails mark-reviewed <plan>/`.

## 6. Validate and check tool calling

```bash
guardrails validate <plan>/
guardrails providers check <plan>/ qwen36
guardrails providers check <plan>/ qwen38
```

- **`validate`** must exit 0.
  - `GR2084` is an error: a malformed gateway block.
  - `GR2085` warns that a Claude model name (`sonnet`, `claude-…`) would reach the gateway, usually from a task's
    `action.model`. Remove the pin.
  - `GR2086` warns that two models share one gateway at `maxParallelism` above 1.
- **`providers check`** sends one tool definition through the gateway and expects a tool call back. An
  **UNMET** step means Claude Code's tool calls won't work through that model. The usual cause is a
  `llama-server` started without `--jinja`.

## 7. Optional: the hostile-config smoke test

This is the strongest proof that nothing leaks to Anthropic. It plants hostile Claude Code settings in a
throwaway directory, runs a tiny plan through a recording proxy in front of LiteLLM, and asserts:
- every request went to your gateway, with your model and no Claude model name;
- no `x-api-key` header was sent;
- your real `~/.claude` was never touched.

It needs PowerShell 7 and the Guardrails source checkout from step 1:

```bash
cd <Guardrails checkout>
pwsh scripts/smoke/claude-gateway-live-smoke.ps1 -GatewayUrl http://127.0.0.1:4000 \
  -Model Qwen -BackendModel qwen3.6-35b-a3b -ContextTokens 65536
# add: -AuthTokenEnv LITELLM_MASTER_KEY   if LiteLLM requires a key (step 3)
```

Re-run it after every Claude Code upgrade.

## 8. Run

```bash
guardrails run <plan>/
```

The run header names each backend it verified, for example:

```
Gateway: block 'qwen36' → http://127.0.0.1:4000, model 'Qwen': backend http://127.0.0.1:8080 Qwen3.6-35B-A3B (backendModel 'qwen3.6-35b-a3b' matched).
```

Before any task starts, the preflight halts the run, with the reason, if:
- a settings file from step 4 sets a routing or credential key;
- `authTokenEnv`'s variable is unset;
- LiteLLM isn't answering or doesn't list the model;
- the loaded model doesn't match `backendModel`;
- `contextTokens` is too large;
- two model names turn out to be the same loaded model.

A halt costs no retries: fix the cause and run again.

A header line that says **`backend identity unverified`** means Guardrails couldn't prove which model answered.
The run still works, but its numbers can't be attributed to a model. Fix it before using the run as evidence.

**Spend:** gateway attempts record no dollar cost. The live table, summary and `status` show token usage instead
(`48.2k tok (gateway)`), and `--max-cost-usd` does not limit gateway spend.

## 9. The interactive skills (`/plan-breakdown`, `/guardrails-review`)

These run inside **your own** Claude Code session, so you point that session at LiteLLM yourself. Use **Qwen 3.8**
here: plan-breakdown is the most demanding step in the pipeline.

```bash
cd <your repo>
env -u ANTHROPIC_API_KEY \
  ANTHROPIC_BASE_URL=http://127.0.0.1:4000 \
  ANTHROPIC_AUTH_TOKEN="${LITELLM_MASTER_KEY:-local-gateway}" \
  ANTHROPIC_DEFAULT_OPUS_MODEL=Qwen3.8 ANTHROPIC_DEFAULT_SONNET_MODEL=Qwen3.8 \
  ANTHROPIC_DEFAULT_HAIKU_MODEL=Qwen3.8 CLAUDE_CODE_SUBAGENT_MODEL=Qwen3.8 \
  CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC=1 CLAUDE_CODE_MAX_CONTEXT_TOKENS=65536 \
  claude --model Qwen3.8
```

Then run `/plan-breakdown <plan>.md`, and `/guardrails-review <plan>/` on its output, as usual.

Unlike a Guardrails run, this session is **not** isolated:
- **Your `~/.claude/settings.json` still applies, and its `env` block overrides the shell.** Make sure it does not
  set `ANTHROPIC_*` variables or an `apiKeyHelper`. A `"model"` there is fine, because `--model` on the command
  line wins.
- `ANTHROPIC_AUTH_TOKEN` must be set, even to a dummy value. Otherwise Claude Code falls back to your stored login
  and sends it to the gateway.
- Expect it to be slow, and watch for context exhaustion. Plan-breakdown reads large reference files, and Claude
  Code's own prompt is large. If it compacts constantly or loses the thread, break the plan into smaller pieces.

## 10. The dogfood: evidence for the next decision

Whether Guardrails builds its own native local-model runner (#544) depends on how this path performs. Record each
run in a table on [#786](https://github.com/Servant-Software-LLC/Guardrails/issues/786). Run the same task at least
**3 times per model**, serial, and only runs whose header shows a verified backend count.

| Run | Model | Task or plan | Result | Attempts to green | Context overflows | Malformed tool calls | Wall time | Notes |
|---|---|---|---|---|---|---|---|---|

Start small:
1. One task from an already-reviewed plan (such as Bifrost) on Qwen 3.6, 3 runs; then on Qwen 3.8, 3 runs.
2. A 3–5 task slice of that plan on the better model, 3 runs.
3. Then an interactive `/plan-breakdown` on a small plan (step 9).

The per-attempt details live in `<plan>/state/run.json`: each attempt's provenance carries `gateway` and
`backendModel`. Transcripts are in `<plan>/logs/<runId>/`.

## Worked example: an existing `claude-local` setup

This is the setup verified on the maintainer's MacBook: an existing `claude-local` install, reused as is. It
differs from the generic steps above in four ways:
- **one** model;
- LiteLLM protected by a **master key**;
- a **`*` catch-all** route;
- `llama-server` started with its defaults (no `--alias`, no `-c`, no `--jinja`).

Every value below comes from a check you can run yourself. Run everything in **one shell**, because the key is
exported into it.

**A. Find the `claude-local` folder and load its key.**

```bash
CL_DIR="$(dirname "$(readlink -f "$(command -v claude-local)")")"; echo "claude-local folder: $CL_DIR"
set -a; source "$CL_DIR/.env"; set +a; echo "key loaded: ${LITELLM_MASTER_KEY:+yes}"
```

Expect a folder path and `key loaded: yes`. If the folder is empty, `claude-local` isn't on your `PATH`; set
`CL_DIR` to the folder that holds `claude-local`, its `.env` and `litellm-config.yaml`.

**B. Confirm LiteLLM needs that key, and accepts it.**

```bash
curl -s -o /dev/null -w "no key:   HTTP %{http_code}\n" http://127.0.0.1:4000/v1/models
curl -s -w "\nwith key: HTTP %{http_code}\n" -H "Authorization: Bearer $LITELLM_MASTER_KEY" http://127.0.0.1:4000/v1/models
```

Verified result:
- **`no key: HTTP 500`**. LiteLLM gives a generic error when no key is sent at all. A *wrong* key, such as the
  placeholder Guardrails sends when `authTokenEnv` is missing, gets `400 "No connected db."` instead.
- **`with key: HTTP 200`** and a model list:

```json
{"data":[{"id":"qwen-3.6-35b-mtp", ...},{"id":"*", ...}],"object":"list"}
```

So the block needs `"authTokenEnv": "LITELLM_MASTER_KEY"`. Its `model` must be the **exact** listed name,
`qwen-3.6-35b-mtp`, not `Qwen`.

**C. Check where every LiteLLM route goes. The `*` route matters most.**

```bash
grep -n -B1 -A6 'model_name' "$CL_DIR/litellm-config.yaml"
```

Verified result: both routes point at the same local server.

```yaml
model_list:
  - model_name: qwen-3.6-35b-mtp
    litellm_params:
      model: openai/models/Qwen3.6-35B-A3B-MXFP4_MOE.gguf
      api_base: http://127.0.0.1:8080/v1
      api_key: "sk-local-no-auth-needed"
  - model_name: "*"            # catch-all: any unrecognized model name, e.g. claude-sonnet-...
    litellm_params:
      model: openai/models/Qwen3.6-35B-A3B-MXFP4_MOE.gguf
      api_base: http://127.0.0.1:8080/v1
      api_key: "sk-local-no-auth-needed"
```

- **Safe:** the `*` route forwards to the *local* `llama-server`, so a stray Claude model name is answered by local
  Qwen and never leaves the machine.
- **Not safe:** a `*` route that forwards to a paid provider (`openai/*`, `anthropic/*` with a real key). Any stray
  model name would then spend money and send code off the machine.
- Guardrails runs don't depend on the catch-all. They pin every Claude Code model alias to the block's `model`, and
  the preflight insists on an exactly listed name. It is still a useful safety net for the interactive session
  (step 9).

**D. Check what `llama-server` has loaded, and its context window.**

```bash
curl -s http://127.0.0.1:8080/props | python3 -c 'import sys,json; p=json.load(sys.stdin); print("alias:", p.get("model_alias"), "| path:", p.get("model_path"), "| n_ctx:", p.get("default_generation_settings",{}).get("n_ctx"), "| slots:", p.get("total_slots"))'
```

Verified result (paths shortened):
`alias: …/models/Qwen3.6-35B-A3B-MXFP4_MOE.gguf | path: …/models/Qwen3.6-35B-A3B-MXFP4_MOE.gguf | n_ctx: 262144 | slots: 4`

- There is no `--alias`, so the alias *is* the file path. `"backendModel": "qwen3.6-35b-a3b"` matches the file name
  `Qwen3.6-35B-A3B-MXFP4_MOE.gguf` (case-insensitive substring).
- Each slot reports a 262,144-token window. Recent `llama.cpp` builds share one KV cache across all slots, so a single
  session really can grow that large. Use **`"contextTokens": 65536`** anyway:
  - Claude Code then compacts well before a 262K prompt, which on a Mac would be very slow to process and heavy on
    memory;
  - 64K is plenty for one plan task;
  - 65536 passes the preflight's window check, which compares `contextTokens` to that reported `n_ctx`.

**E. The plan's runner block.**

```json
"maxParallelism": 1,
"promptRunners": {
  "default": "qwen36",
  "qwen36": {
    "kind": "claude",
    "command": "claude",
    "baseUrl": "http://127.0.0.1:4000",
    "model": "qwen-3.6-35b-mtp",
    "authTokenEnv": "LITELLM_MASTER_KEY",
    "backendModel": "qwen3.6-35b-a3b",
    "contextTokens": 65536
  }
}
```

- **Name the block after the model** (`qwen36`), not `claude`. The live table's **Model** column shows the
  *block's name*, never the model ID. A block named `claude` makes a run served by local Qwen read "claude" there.
  `"kind": "claude"` and `"command": "claude"` stay as they are: they say *how* the prompt is run (the Claude Code
  CLI), not which model answers.
- There is only one model here, so there is only one block. For a Qwen 3.8 block, LiteLLM first needs its own
  `model_name` pointing at a second `llama-server` (step 2 and step 3).
- **Renaming an existing block** changes `guardrails.json`, and so the plan's hash. Do it between runs, not
  during one. If `validate` then warns that the plan is not reviewed (`GR2025`), re-mark it with
  `guardrails mark-reviewed <plan>/`.

**F. Validate, check tool calling, run.** Use the same shell, with Guardrails **1.26.0 or later**, so a rejected
key is reported as one (#791):

```bash
brew update && brew upgrade guardrails && guardrails skills install --force
guardrails validate <plan>/
guardrails providers check <plan>/ qwen36
guardrails run <plan>/
```

- **`providers check`:** if a step is UNMET, restart `llama-server` with `--jinja`. `claude-local` doesn't pass it,
  although recent `llama.cpp` builds may enable it by default.
- **The run header** names the model that is actually serving, and should read like this:

  ```
  Gateway: block 'qwen36' → http://127.0.0.1:4000, model 'qwen-3.6-35b-mtp': backend http://127.0.0.1:8080 …Qwen3.6-35B-A3B-MXFP4_MOE.gguf (backendModel 'qwen3.6-35b-a3b' matched).
  ```

  The table's Model column shows `qwen36` (the block name). Each finished attempt's summary also says
  `(via gateway http://127.0.0.1:4000)`, and each attempt in `state/run.json` records `gateway` and `backendModel`.

  If it says `backend identity unverified`, don't use that run's numbers for the dogfood (step 10) until it's fixed.

## Troubleshooting

| Symptom | Likely cause | Fix |
|---|---|---|
| `providers check` UNMET, or the agent prints JSON-looking "tool calls" instead of acting | `llama-server` started without `--jinja` | Restart it with `--jinja` |
| Preflight: "the gateway refused the credential" (often HTTP 400 `No connected db.` from LiteLLM) | LiteLLM has a `master_key`, and the block has no `authTokenEnv` (so a placeholder token was sent), or the variable holds the wrong key | Step 3: export the key and set `"authTokenEnv": "LITELLM_MASTER_KEY"` |
| Preflight: gateway not answering | LiteLLM or `llama-server` isn't running, or the wrong port | Re-run the checks in steps 2 and 3 |
| Preflight: `backendModel` mismatch | The server has a different model loaded, or `--alias` is wrong | Check `/props` (step 2); fix the launch line or the block |
| Preflight: two model names share one loaded model | Both LiteLLM entries point at the same server | Give each `model_name` its own server and port |
| Preflight names a settings file and a key | Step 4 | Remove the key from the repo file; ask your admin about a managed one |
| Header: `backend identity unverified` | LiteLLM isn't reporting `/model/info`, a `model_name` has several backends, or the backend is not on this machine or a private network | Fix the LiteLLM config (one `api_base` per name) |
| `validate`: `GR2009` "command not found" for `qwen36` | `"command": "claude"` is missing | Add it (step 5) |
| Attempts end as `timeout` | Local models are slow | Raise the task's `timeoutSeconds` |
| Repeated context overflows or constant compaction | Task too large for the window | Smaller tasks; raise `contextTokens` toward the per-slot `n_ctx` that `/props` reports |
| The target repo's build guardrails fail with "A compatible .NET SDK was not found" | The repo's `global.json` needs an SDK you don't have | Install it (step 1) |

## What still leaves the machine

- **Prompts and code:** no. Every model request goes to LiteLLM, which forwards it to `llama-server`.
- **Claude Code's own background calls:** Guardrails turns nonessential traffic off. Anthropic documents that a
  few features (fast-mode checks, WebFetch's safety check) call `api.anthropic.com` directly, and whether that
  switch suppresses them is unverified. For the interactive session in step 9, you set that switch yourself.
- **The gateway token:** the agent's shell commands can read it, as `ANTHROPIC_AUTH_TOKEN` and under the name you
  gave `authTokenEnv`. It is also readable in the session transcripts the local log viewer serves. That is harmless
  with a local, unauthenticated LiteLLM, but keep it in mind if you point `baseUrl` at a remote gateway.

## Reference

- The full contract is SSOT [`docs/plans/02-schemas-and-contracts.md`](plans/02-schemas-and-contracts.md) §9.10.
- Design of record: [`docs/plans/782-claude-gateway.charter.md`](plans/782-claude-gateway.charter.md).
- Epic and roadmap: [#786](https://github.com/Servant-Software-LLC/Guardrails/issues/786). The native local
  runner design is [#544](https://github.com/Servant-Software-LLC/Guardrails/issues/544).
