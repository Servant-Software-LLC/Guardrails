## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g. `08-record-shim-contract`), NOT the
  stableId. The harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "08-record-shim-contract": { "someKey": "someValue" } }`.
- EXCEPTION — the CONTROL KEYS `needsHarnessWrite` and `needsHuman` are TOP-LEVEL
  SIBLINGS of your folder-name key, never nested inside it. They are instructions to
  the harness, not state, so the rule above does not cover them:
  `{ "08-record-shim-contract": { "someKey": "someValue" },
  "needsHarnessWrite": { "path": "…", "edits": [ … ] } }`. Nest one inside your
  folder-name key and the harness REJECTS the attempt — nothing is written.
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

Task 02 changed how EVERY script guardrail in the product is invoked. That is a contract change, and
`docs/plans/02-schemas-and-contracts.md` is the SSOT — it must carry the change in the same change-set as
the code, not later.

Read the shipped implementation before you write a word of this: grep
`src/Guardrails.Core/Execution/InterpreterMap.cs` for `ShimScript` and
`src/Guardrails.Core/Execution/GuardrailRunner.cs` for `ToGuardrailResult`, and read the shim script
itself. **Describe what landed, not what design 38 proposed** — if the two differ, the code is the
authority and the difference is worth a sentence.

Record four things, in the section where the SSOT already describes guardrail execution and its verdict
(find it; do not invent a new top-level section):

1. **A `.ps1` guardrail is invoked through a harness-owned shim**, not directly. Both the `pwsh` and the
   `powershell.exe` templates route through it. Use the phrase **`harness-owned shim`** verbatim — the
   guardrail binds to it, because the bare word `shim` already appears once in this document in an
   unrelated back-compat sentence and a check keyed on it would certify nothing.
2. **Exit `97` is reserved** and means the guardrail aborted before reaching its own verdict. Write it as
   **`exit 97`** — a bare `97` already occurs twice in this document, so that is the discriminating form. The
   harness records it as a FAILURE with a reason naming the abort and with stderr carried in full, so
   the interpreter's error record reaches the retry-feedback tail.
3. **Exit 0 is no longer sufficient for a pass on a `.ps1` guardrail.** State the reason in one line: an
   engine-raised error under PowerShell's default preference is non-fatal, so a script that aborted
   mid-check used to exit 0 and be recorded as a PASS (#608).
4. **The one behavioural divergence**, honestly: a guardrail that runs a failing native command and then
   falls off its end **without an explicit `exit`** now takes that command's exit code, where it
   previously exited 0. Measured at **0 of 900** committed guardrails, and GR2037 entry `#608b` keeps it
   that way. Do not present this as having no cost; present it as a measured, bounded and gated one.

## Constraints

**Scope boundary (harness-enforced):** Write only to `docs/plans/02-schemas-and-contracts.md`.

This is the document every other document defers to. Match its conventions exactly — section numbering,
the way it cross-references issue numbers, its level of detail. Add no more than the four facts above; a
contract document that explains a rationale at design-document length stops being usable as a reference.
The rationale already lives in `docs/plans/38-guardrail-scan-soundness.md` — cite it **by that path**
(the guardrail checks for the citation) rather than restating it.
