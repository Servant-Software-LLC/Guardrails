## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g. `09-implement-resource-supply-brief`), NOT the
  stableId. The harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "09-implement-resource-supply-brief": { "someKey": "someValue" } }`.
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

Implement `Overwatch.ProposeResourceSupplyAsync` and the `missing-resource` trigger over task 08's stubs —
design 41 §2.3 — so `OverwatchResourceSupplyBriefTests` passes, **including its real-seam row**.

### The trigger token

Add the case to `OverwatchTriggers.Token` in `src/Guardrails.Core/Execution/OverwatchTrigger.cs`:

```csharp
OverwatchTrigger.MissingResource => "missing-resource",
```

That token is the wire spelling used by `decisions[]` and `overwatch.jsonl` (SSOT §8), so it must never
fork.

### The brief

`ProposeResourceSupplyAsync` runs ONE diagnose with `OverwatchTrigger.MissingResource`. **Reuse the §9.2
diagnose machinery rather than writing a second copy of it** — `RunDiagnoseAsync` already gets five things
right that this consult needs, and each of them was a bug once:

- the read-only tool profile (`DiagnoseTools` = Read/Glob/Grep — #452, the empty `AllowedTools` default);
- the abort after three consecutive tool denials (`DenialAbortThreshold`);
- the overhead cost charge made **before** parsing, so a billed failure still counts toward `maxCostUsd`
  and appears in the reported total;
- no-verdict recording (#452): a diagnose that ran and produced nothing is REPORTED, never silent;
- working directory `plan.Workspace`, so the model can Read the candidate file and judge whether it is a
  real bundle or a placeholder.

The cleanest shape is to give the existing diagnose path a brief-builder seam and pass the new builder in,
rather than duplicating the invocation. Do not weaken or re-route the existing path to do it.

**The first line is PINNED, exactly:**

```
# Overwatch resource supply: task '<id>' (attempt <n>, trigger: missing-resource)
```

The §7 wiring proof's fake CLI routes on this line, and so does the operator reading `overwatch.jsonl`. A
heading that is merely similar breaks the proof's controls silently.

**The brief puts harness facts first — #709's rule, applied from the start.** It states, in this order:

- the task id and description;
- the agent's question **verbatim, inside a delimited block marked UNTRUSTED** — it is the one piece of
  model-authored text in the brief and must never read as a harness fact;
- the attempt history table, from the existing renderer (`RenderAttemptHistory`);
- the candidate table — for each candidate: the path, "absent at run base `<sha10>`", "committed at
  checkout `HEAD` `<sha10>` on branch `<branch>`", and "no other task produces it";
- this instruction, in substance: *"These facts were verified by the harness. Do not assert anything about
  files, tests, other tasks or plan-level gates beyond them. Nothing you write reaches the task's next
  attempt."*

**The vocabulary is offered ONLY here:**

```json
{"classification": "retryable|doomed",
 "diagnosis": "<one paragraph: why supplying these files resolves the task's question>",
 "fixes": [{"kind": "resource-supply", "path": "<a path from the candidate table>"}]}
```

Tell the model to propose no fix when the question is not asking for a missing file these candidates
satisfy. Do **not** offer `guidance`, `budget`, `file-edit` or `task-field` here — a fix this gate can never
certify is an invitation to spend a diagnose on nothing.

**What evidence the model must cite: none that the gate trusts.** It names a candidate path and explains
itself in `diagnosis`, and that explanation is recorded as its *unverified* claim. Do not add a check that
the diagnosis quotes the question: candidates are themselves tokens taken from the question, so such a
check is a tautology dressed as certification. The semantic judgement cannot be checked — §2.2's facts, the
task's own guardrails and the delivery interlock are what bound it.

### The generic diagnose brief stays byte-identical

`BuildDiagnosePrompt` is not yours to touch. A run that never hits a missing-resource halt must send exactly
the bytes it sends today. Guardrail `04-generic-brief-unchanged.ps1` asserts the shipped heading, the
verdict line and all four shipped op shapes are still in that method, and that `resource-supply` does not
appear inside it.

### `overwatch.jsonl`

`src/Guardrails.Core/Execution/OverwatchDetailWriter.cs` is in your scope for the detail stream design 41 §6
specifies: a record with `trigger: "missing-resource"`, a `fixes[]` entry of
`{ kind: "resource-supply", authority: "default", target: <path> }` per proposed op, and — when the
Scheduler later certifies one — an `applied: { supplied: [<paths>], commit: <sha> }`. `OverwatchDetailApplied`
today carries only `Guidance`/`ExtraRetries`; extend it additively (omit-null is already configured) so no
existing record's shape changes.

### The real-seam row is the gate, not a formality

`ProposeResourceSupply_DrivesTheRealClaudePromptRunner_WritingItsStreamLogAndReturningARealVerdict` drives
the REAL `ClaudePromptRunner` over a fake CLI process. It is gated by its own guardrail
(`03-real-seam-tests-pass.ps1`), separately from the other rows, because it is the one that catches the
failure the others cannot see: a brief that composes perfectly in a unit test and throws through the real
runner, where a blanket `catch` turns the crash into a safe default and nothing says so. That is the
measured `CriticalityJudge` empty-`StreamLogPath` bug, and it is why `PromptInvocation.StreamLogPath` must
be a real per-attempt path here (`overwatch-stream-attempt-<n>.jsonl` under the task log dir), never empty.

If that row fails while the others pass, the brief is fine and the INVOCATION is wrong. Read the failure
before touching the brief text.

Do NOT edit the authored tests; emit {"needsHuman": "<why>"} if one is genuinely wrong.

**Scope boundary (harness-enforced):** Write only to `src/Guardrails.Core/Execution/Overwatch.cs`, `src/Guardrails.Core/Execution/OverwatchTrigger.cs`, and `src/Guardrails.Core/Execution/OverwatchDetailWriter.cs`. After this
task completes, the harness runs a `git diff` membership check and rejects any edit outside these paths. An
out-of-scope edit fails the task immediately and consumes a retry. If you hit a compile error caused by a
missing symbol in another file, do NOT edit that file — write `{"needsHuman": "<what is missing>"}` to the
state-out path and stop.

**The harness runs this task's guardrails itself when you finish.** Do not try to run the guardrail scripts yourself: the shell they need is not granted to you, and a call refused on two attempts can halt the task even after the work is done. Tests authored by OTHER tasks may legitimately fail on your base until their own implementing task lands; only this task's tests are yours to turn green.
