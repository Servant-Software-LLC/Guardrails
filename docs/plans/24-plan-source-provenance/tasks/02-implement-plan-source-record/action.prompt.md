## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in, i.e.
  `{ "02-implement-plan-source-record": { "someKey": "someValue" } }`. The harness
  REJECTS a fragment keyed by anything else (every attempt).
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

Implement `PlanSourceRecord` so the tests task 01 authored **pass**.

**Write exactly one file:** `src/Guardrails.Core/Breakdown/PlanSourceRecord.cs` — replacing the
`NotImplementedException` stubs with real logic.

**Scope boundary (harness-enforced):** Write only to
`src/Guardrails.Core/Breakdown/PlanSourceRecord.cs`. After this task completes, the harness runs a
`git diff` check and rejects any edit outside that path — including
`tests/Guardrails.Core.Tests/PlanSource/PlanSourceRecordTests.cs`, any other production file, or the
`.csproj`. An out-of-scope edit fails the task immediately and consumes a retry. **The test file is
NOT yours to edit.** If a test looks wrong, it is still the contract: implement to it. If you hit a
compile error caused by a missing symbol in another file, do NOT edit that file — write
`{"needsHuman": "<what is missing>"}` to the state-out path and stop.

### Read the tests FIRST — they are the specification

Task 01 authored `tests/Guardrails.Core.Tests/PlanSource/PlanSourceRecordTests.cs` and the stub file
you are about to fill. **This paragraph describes the authoring-time state, before task 01 had
actually run — verify it is still accurate before assuming the same shape applies.** Task 01 was told
to choose the member shape it thought the implementation wanted (a factory such as
`Capture(string planPath)`, plus `SourcePath` / `SourceBytes` / `SourceSha256` / `SourceSha256Lf` /
`DeclaredDelegatedDecisions` / `Stamps`), so the exact signatures are whatever landed on disk. Read
both files before writing a line. `git diff HEAD~1 --stat` and `git show` will show you what task 01
committed.

You MAY reshape the stub's members (that file is in your write scope) — but only in ways the
**existing tests still compile and pass against**. Changing the stub to dodge a test is the one thing
this task cannot do.

### The behaviour, field by field — each rule is load-bearing

| Field | Rule |
|---|---|
| `SourceSha256` | SHA-256 over the file's **raw bytes**. Read with `File.ReadAllBytes`, **never** `File.ReadAllText`: `ReadAllText` decodes, so a UTF-8 BOM or an encoding round-trip silently changes the digest and it stops being byte-exact against the hash Charter wrote over the same file. |
| `SourceSha256Lf` | SHA-256 over **those same bytes** with `CRLF` and a lone `CR` normalised to `LF`. Normalise the BYTES (or a byte-faithful projection of them) — not a re-encoded string. **Both hashes are required.** A raw mismatch is usually `core.autocrlf`, not tampering, and a check whose first alarm is a false one trains everyone to ignore it. |
| `SourceBytes` | The byte length actually read. |
| `Stamps` | An **OPEN MAP** keyed by whatever `<!-- charter: <key>=<value> -->` comments the plan carries — not two named fields. Charter adds stamp lines over time and an open map absorbs them with no schema change here. Empty map (never null) when the plan carries none. **Duplicate key: FIRST WINS, and the duplicate is reported** on whatever diagnostic/duplicate collection the tests assert over. |
| `DeclaredDelegatedDecisions` | The integer from `DECISIONS DELEGATED TO YOU: (\d+)\*\*`, or **0** when the line is absent. Absence is unambiguous: Charter emits the line whenever the count is >= 1 and never when it is 0 — so do NOT invent a "unknown"/nullable third state. |

Hash strings are `sha256:<lowercase-hex>`, matching the shape the plan of record shows
(`docs/plans/24-plan-source-provenance.md` section 3).

### Two things this task must NOT do

- **Do NOT wire anything.** This task implements the type only. `InitialBreakdownInvoker` and
  `BreakdownCommand` belong to task 05 and are outside your write scope.
- **Do NOT weaken a test to make it pass**, and do not add `[Fact(Skip=…)]` anywhere — the test file
  is out of scope, so any such edit fails the write-scope check immediately.

Use `System.Security.Cryptography.SHA256` and `System.Text.Json` from the BCL; add no package
reference (the `.csproj` is out of scope). Match the file's surrounding house style — the repo
centralises build policy in `Directory.Build.props`, so nullable/implicit-usings settings are already
decided for you.
