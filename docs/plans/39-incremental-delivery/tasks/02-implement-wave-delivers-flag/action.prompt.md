## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g. `02-implement-wave-delivers-flag`), NOT the
  stableId. The harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "02-implement-wave-delivers-flag": { "someKey": "someValue" } }`.
- EXCEPTION — the CONTROL KEYS `needsHarnessWrite` and `needsHuman` are TOP-LEVEL
  SIBLINGS of your folder-name key, never nested inside it.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- Guardrails constrain the OUTCOME, never HOW you implement it. Never reshape working
  code — or reword a document away from its own conventions — to match a check's pattern.
- If you cannot proceed without a human decision, write
  {"needsHuman": {"question": "<question>", "kind": "blocked-work"}} to the
  state-out path and stop. If instead a guardrail reports something ABSENT that you can
  see is PRESENT, that guardrail is defective: use "kind": "defective-guardrail" and
  quote (a) the guardrail's exact claim and (b) the file:line that refutes it. If you
  cannot produce BOTH quotes it is not a defective guardrail — retry the work, or
  escalate as "blocked-work". Difficulty is never "defective-guardrail".

## Task

**The flag is `delivers: true` in the wave's `brief.md` YAML FRONT MATTER — not a new file
(review, 2026-09-11).** There is no per-wave manifest and you must not invent one: SSOT §14.1
says v1 has *"no per-wave config in v1"*, and `WaveFolder.TryResolveWaveTarget` treats a
directory carrying its own `guardrails.json` as **a plan in its own right, never a wave** — but
only when a command is pointed AT that directory. The parent plan silently ignores the stray
file: it stays waved, `validate` does not warn, and a flag written there is never read. `brief.md` already exists as the optional
per-wave file (`WaveNode.BriefFileName`) and is already folded into `WaveDefinitionHash`, so
this surface needs no change to wave DETECTION and no change to the hasher. Parse the front
matter in `WaveFolder.cs`; a wave with no `brief.md`, or a brief with no front matter, is
`delivers: false`.

Fill real logic over the stub so `WaveDeliversFlagTests` passes. Design 39 §1b/§3.

Default **false**, and a wave with no `guardrails/` exit gate is never a delivery point whatever the
flag says.

Do NOT edit the authored tests; emit {"needsHuman": "<why>"} if one is genuinely wrong.

**Scope boundary (harness-enforced):** Write only to `src/Guardrails.Core/Model/WaveNode.cs` and `src/Guardrails.Core/Loading/WaveFolder.cs`. After this
task completes, the harness runs a `git diff` membership check and rejects any edit outside these paths. An
out-of-scope edit fails the task immediately and consumes a retry. If you hit a compile error caused by a
missing symbol in another file, do NOT edit that file — write `{"needsHuman": "<what is missing>"}` to the
state-out path and stop.

**The harness runs this task's guardrails itself when you finish.** Do not try to run the guardrail scripts yourself: the shell they need is not granted to you, and a call refused on two attempts can halt the task even after the work is done. Tests authored by OTHER tasks may legitimately fail on your base until their own implementing task lands; only this task's tests are yours to turn green.

