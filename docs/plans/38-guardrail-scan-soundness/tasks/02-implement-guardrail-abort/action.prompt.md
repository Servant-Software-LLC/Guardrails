## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g.
  `02-implement-guardrail-abort`), NOT the stableId. The harness REJECTS a fragment
  keyed by anything else (every attempt), so:
  `{ "02-implement-guardrail-abort": { "someKey": "someValue" } }`.
- EXCEPTION — the CONTROL KEYS `needsHarnessWrite` and `needsHuman` are TOP-LEVEL
  SIBLINGS of your folder-name key, never nested inside it. They are instructions to
  the harness, not state, so the rule above does not cover them.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- Guardrails constrain the OUTCOME, never HOW you implement it. Never reshape working
  code — or reword a document away from its own conventions — to match a check's
  pattern.
- If you cannot proceed without a human decision, write
  {"needsHuman": {"question": "<question>", "kind": "blocked-work"}} to the
  state-out path and stop. If instead a guardrail reports something ABSENT that you
  can see is PRESENT, that guardrail is defective: use "kind": "defective-guardrail"
  and quote (a) the guardrail's exact claim and (b) the file:line that refutes it.
  If you cannot produce BOTH quotes it is not a defective guardrail — retry the work,
  or escalate as "blocked-work". Difficulty is never "defective-guardrail".

## Task

Make `tests/Guardrails.Core.Tests/Execution/GuardrailAbortTests.cs` pass **without editing it**. That
file is outside your `writeScope`; an edit to it fails this task and consumes a retry. If the authored
tests are genuinely wrong or incompatible, write `{"needsHuman": "<why>"}` and stop rather than changing
them.

Read `docs/plans/38-guardrail-scan-soundness.md` §3 first — **all of it**, including §3.3 and §3.4. It
carries the shim verbatim, the measured behaviour of every case, and two plausible variants that were
measured to turn `exit 1` into exit 0.

Three changes:

1. **Add the shim** as a PowerShell script the harness owns and materializes, and embed it in
   `Guardrails.Core` (an `<EmbeddedResource>` in `src/Guardrails.Core/Guardrails.Core.csproj`, the same
   mechanism `banned-guardrail-patterns.json` already uses — grep the csproj for `EmbeddedResource` and
   follow what is there rather than inventing a second scheme). Its body is design 38 §3.2, verbatim.

2. **Route the `.ps1` templates through it** in `src/Guardrails.Core/Execution/InterpreterMap.cs`. Grep
   that file for `PwshTemplate` and `PowershellTemplate` — **both** must route through the shim; the
   second is the Windows fallback used when `pwsh` is absent, and leaving it unrouted leaves the
   fail-open armed on exactly the boxes least likely to notice. Do not rely on a line number; grep the
   names.

   **Two names are PINNED, because guardrail 03 binds to them.** The shim script file is
   `src/Guardrails.Core/Execution/guardrail-shim.ps1`, and `InterpreterMap` refers to its materialized
   path through a member named **`ShimScript`**. Both `PwshTemplate` and `PowershellTemplate` must
   reference `ShimScript` as an identifier — not in a comment, not in a string literal. You are free to
   choose everything else (how the resource is materialized, where the path is cached); only those two
   names are fixed.

3. **Give exit 97 an explicit verdict branch** in `src/Guardrails.Core/Execution/GuardrailRunner.cs`.
   Grep for `ToGuardrailResult`. Today it returns `Passed = true` on `result.Succeeded` without reading
   stdout or stderr; an exit-97 result must produce `Passed = false` with a `Reason` that names the abort
   (design 38 §3.5 gives the wording) and an `Output` carrying **stderr in full**, so the
   `GUARDRAILS-ABORT:` line and the interpreter's error record both reach the retry-feedback tail (#179).
   Do **not** make the reason the first line of stdout — for an aborted guardrail that is whatever the
   script printed before it died, which is a technically-correct and useless report.

**Do not weaken the other cases to make the abort case pass.** `exit 1` must still fail, `exit 0` must
still pass, and a guardrail that writes to stderr and exits 0 must still pass — three of the six authored
tests exist only to hold that line, and design 38 §3.3 records that two obvious shim shapes broke it.

The one behavioural divergence the shim introduces (a failing native command followed by no explicit
`exit`) is documented in §3.4 and measured to occur in **0 of 900** committed guardrails. Do not add
compensating machinery for it.
