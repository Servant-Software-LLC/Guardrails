## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g.
  `25-implement-halt-text`), NOT the stableId. The harness REJECTS a fragment
  keyed by anything else (every attempt), so:
  `{ "25-implement-halt-text": { "someKey": "someValue" } }`.
- EXCEPTION — the CONTROL KEYS `needsHarnessWrite` and `needsHuman` are TOP-LEVEL
  SIBLINGS of your folder-name key, never nested inside it.
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

Make the `needs-human` halt name the asymmetry and print the three-command sequence, so
`SuppliedHaltTextTests` passes.

**Find the seam yourself.** Grep `src/Guardrails.Cli/Commands/RunCommand.cs` for
`NeedsHumanClosingLine` and `RenderNeedsHumanSections` rather than trusting a line number —
this file is large, and task 16 has already edited it by the time you run.

The existing `blocked-work` closing line reads *"answer the question or re-scope the task
(action, writeScope, dependencies), then re-run to resume."* That line is correct as far as it
goes and **actively misdirects for a missing FILE**: re-scoping the task cannot conjure a
vendored artifact, and the operator is not told the channel that can. Extend it; do not replace
the guidance that is right for the other cases.

Two things the text must carry, because they are what §5a calls v1 acceptance conditions:

1. **The asymmetry, named.** Plan-folder edits reach a running plan; code artifacts do not.
   This is the confusion that produced #373 — the reframed prompt reached the run and the file
   did not — and the measured cost was entirely in NOT KNOWING the rule.
2. **The three-command sequence, copy-pasteable.** `guardrails supply`, the reset, the resume,
   with the plan folder and the real paths filled in rather than left as `<placeholders>`
   (#431: a template the reader has to complete is not a hand-over). Task 18 ships
   `supply --resume` as the opt-in shorthand — mention it, but keep the explicit three-command
   form as the one the halt PRINTS, because `reset` chooses WHICH descendants to re-arm and
   folding that choice into a shorthand hides a real decision. That is the maintainer's ruling
   (`d40-resume-ergonomics`), not a style preference.

**Do not make this fire on every halt.** It belongs to the missing-resource case. A halt about
a genuinely over-scoped task should not be told to go and supply a file.

Do NOT edit the authored tests; emit `{"needsHuman": "<why>"}` if one is genuinely wrong.

**Scope boundary (harness-enforced):** Write only to
`src/Guardrails.Cli/Commands/RunCommand.cs`. After this task completes, the harness runs a
`git diff` membership check and rejects any edit outside that path. An out-of-scope edit fails the task immediately and consumes a retry. If you hit a compile
error caused by a missing symbol in another file, do NOT edit that file — write
`{"needsHuman": "<what is missing>"}` to the state-out path and stop.
