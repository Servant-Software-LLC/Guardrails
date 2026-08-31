## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key - the name of the directory this task.json lives in, NOT the stableId. The
  harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "30-record-in-domain-knowledge": { "someKey": "someValue" } }`.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- Guardrails constrain the OUTCOME, never HOW you implement it. Never reshape working
  code - or reword a document away from its own conventions - to match a check's
  pattern.
- If you cannot proceed without a human decision, write
  {"needsHuman": {"question": "<question>", "kind": "blocked-work"}} to the
  state-out path and stop. If instead a guardrail reports something ABSENT that you
  can see is PRESENT, that guardrail is defective: use "kind": "defective-guardrail"
  and quote (a) the guardrail's exact claim and (b) the file:line that refutes it.
  If you cannot produce BOTH quotes it is not a defective guardrail - retry the work,
  or escalate as "blocked-work". Difficulty is never "defective-guardrail".

## Plan of record

This task implements part of `docs/plans/28-local-inference-runner.md`. READ THE SECTION(S) NAMED BELOW before you start -
the plan carries the reasoning, the rejected alternatives, and the exact file:line evidence.
Where this prompt and the plan disagree, the plan is authoritative and you should say so in
your summary.

Read: **plan sections 3.2, 3.5, 6.6 and 7**.

## Task

### What to build

Record the `openai-compat` runner in
`.claude/skills/guardrails-domain-knowledge/SKILL.md`, in the section where prompt runners and the
execution contract are described. Update the affected section(s) only.

Cover, briefly and in the skill's own voice:

- **`openai-compat` is a second implemented runner kind**, named after the **PROTOCOL** not the
  engine - Ollama, llama.cpp, **MLX** (`mlx_lm.server` or LM Studio's MLX engine), LM Studio and vLLM
  all speak it, so none of them needs a new kind. `local`, `codex` and `openrouter` remain reserved.
- **v1 is a VERIFIER, not an actor.** It serves the `Guardrail` and `Advisory` roles only, offers a
  fixed read-only `Read`/`Glob`/`Grep` tool set, and refuses an `Action` invocation loudly.
  `PromptRole` is the required `PromptInvocation` field that makes that refusal possible;
  `ServesRoles`/`NeedsContainmentHook`/`WritesFiles` are facts about the BUILD, never config keys.
- **The reachability rule:** the block is reachable by exactly two human acts - a judge guardrail's
  frontmatter pin, or an Advisory reserved profile name (`overwatch`, `ai-triage`). GR2066 errors on
  the five Action routes; GR2065 checks the block schema; GR2067 warns on undeclared `strength` and
  on an unreachable block.
- **The section 6.6 false green, and the probe that closes it** - a server may accept `tools`, call
  none, and return an immaculate `{"pass": true}`, so the pre-DAG preflight probes tool capability per
  (endpoint, model) and a `Guardrail`-role invocation that calls no `tool_calls` fails.
- **Judge spend is recorded but NOT folded into `JournalCost.Total`** - actor spend and verifier
  spend are two numbers on purpose.

Keep it proportionate: this is a knowledge skill, not a copy of the SSOT. Point at SSOT section 9.8
for the full contract.

### Tool discipline - this is what killed the four prior attempts, not the content

Four attempts have failed here and NONE of them failed on understanding the material. Attempt 3 got four
of the five required tokens into the file. They failed on how they reached for the filesystem.

**Use the `Grep`, `Glob` and `Read` TOOLS. Never `Bash` for searching or reading.** This runner grants
`Bash` only for `dotnet *` and `git log/diff/show/status`. A `grep`, `find`, `cat`, `ls` or `head`
through `Bash` is REFUSED every time. Each refusal burns a turn, and repeated refusals trip the
permission-wall detector - attempt 2 ended `permission-denied` that way, and the run's halt message
still quotes a refused `grep -n "^## 9" ...` as though it were a failed write.

**Your deliverable is under `.claude/`. Write it with the `Write` and `Edit` TOOLS only.** Do not shell
out to copy, redirect, or `sed` into it: `.claude/` writes through `Bash` are structurally blocked by the
containment hook, and no number of retries clears that.

### Adopt the prior attempt instead of starting over

The best previous attempt is preserved. Its retry-salvage section names a git ref and a
`prior-attempt.patch` you can `git apply` (`git show` and `git diff` ARE granted). Attempt 3 left the
file needing only ONE more token; attempt 4 ignored the salvage, started from scratch, wrote nothing and
regressed to zero of five. Read the patch first.

### The five tokens the guardrail checks, and the one that is still missing

All five must appear literally in `.claude/skills/guardrails-domain-knowledge/SKILL.md`:

  `Advisory`  `MLX`  `PromptRole`  `ServesRoles`  `tool_calls`

The only one attempt 3 did not land is **`tool_calls`** - the tool-capability probe and the
"accepts a tools array, calls none" false green it closes (SSOT section 9.8). Make sure the prose that
carries it actually explains the probe; the guardrail checks the token, but a token dropped into an
unrelated sentence would be exactly the reword-to-match-a-pattern this task's own instructions forbid.

Keep it proportionate. This is a knowledge skill, not a copy of the SSOT - point at section 9.8 for the
full contract.

**Scope boundary (harness-enforced):** Write only to `.claude/skills/guardrails-domain-knowledge/SKILL.md`. After this task completes, the
harness runs a `git diff` check and rejects any edit outside these paths - including changes to
other production files, neighbouring test files, or the `.csproj`. An out-of-scope edit fails the
task immediately and consumes a retry. If you hit a compile error caused by a missing symbol in
another file, do NOT edit that file - write `{"needsHuman": "<what is missing>"}` to the
state-out path and stop.

## Your deliverable is under `.claude/` - use the harness write hatch

Your primary deliverable is a file under `.claude/`, which a Claude Code subprocess CANNOT write -
the tool-permission layer refuses every `.claude/` write unconditionally. Do NOT attempt a direct
`Write`/`Edit` to the `.claude/` path: a direct-write probe wastes a turn and populates the
harness's permission-wall tracker. Instead, FIRST write a `needsHarnessWrite` request to the
state-out path. The harness (which is NOT subject to that layer) performs the write directly, then
your guardrails still run normally against the result. There are two forms, and they are mutually
exclusive - send exactly one:

- **MODIFYING an existing file - use `edits` (prefer this, and this task is a modification):**
  `{"needsHarnessWrite": {"path": "<workspace-relative path>", "reason": "<why>", "edits":
  [{"old": "<verbatim anchor text>", "new": "<replacement text>"}]}}`.
  Each `old` must occur **exactly once** in the file - zero matches and two-or-more matches are
  both rejected, so include enough surrounding context to make each anchor unique. `old` is matched
  VERBATIM (exact indentation, punctuation and blank lines; only line endings are tolerated), so
  copy the passage out of the file rather than retyping it. Edits apply in order and ATOMICALLY: if
  any one fails, none are written and the file is unchanged. Use `edits` **however large the file
  is** - its cost scales with your change, not the file.
- **CREATING a file - use `content`:**
  `{"needsHarnessWrite": {"path": "<path>", "content": "<full file content>", "reason": "<why>"}}`.
  Do NOT use `content` to modify a large existing file.

If your deliverable spans SEVERAL files, send an ARRAY of those entries in ONE request. Do NOT
deliver them one per attempt: a failed attempt rolls the workspace back to a clean base, so an
earlier attempt's write is DISCARDED and progress cannot accumulate.

If you already attempted a direct write and it was refused, do NOT retry it or try workarounds
(PowerShell, `dangerouslyDisableSandbox`) - just emit `needsHarnessWrite` as above.
