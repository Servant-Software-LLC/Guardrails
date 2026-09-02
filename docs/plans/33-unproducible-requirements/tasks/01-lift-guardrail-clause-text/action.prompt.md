## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g.
  `01-lift-guardrail-clause-text`), NOT the stableId. The harness REJECTS a fragment
  keyed by anything else (every attempt), so:
  `{ "01-lift-guardrail-clause-text": { "someKey": "someValue" } }`.
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

Create `src/Guardrails.Core/Loading/GuardrailClauseText.cs` — a new `internal static class
GuardrailClauseText` in namespace `Guardrails.Core.Loading` — and MOVE these six members into it from
`src/Guardrails.Core/Loading/PlanValidator.cs`, **byte-for-byte unchanged apart from their access
modifier** (`private` → `internal`) and their new home:

| member | current location | kind |
|---|---|---|
| `PresenceClause` | `PlanValidator.cs:2515` | `static readonly Regex` |
| `ClauseFailsTheGuardrail` | `PlanValidator.cs:2525` | `static readonly Regex` |
| `RegexMetacharacters` | `PlanValidator.cs:2530` | `const string` |
| `TryLiteralWitness` | `PlanValidator.cs:2707` | `static string?` |
| `MatchesWitness` | `PlanValidator.cs:2802` | `static bool` |
| `BlankCommentLines` | `PlanValidator.cs:1833` | `static string` |

Line numbers are an authoring-time snapshot — **grep for each member name**, do not trust the number.

**`IsCommentLine` (`PlanValidator.cs:1836`) moves with `BlankCommentLines`**, because
`BlankCommentLines` calls it. `StripCommentLines` (`:1823`) also calls `IsCommentLine` and **stays in
`PlanValidator`** — repoint it at `GuardrailClauseText.IsCommentLine` rather than duplicating the
method. Two copies of a comment-detection predicate that can drift apart is precisely the defect class
this plan exists to catch.

Leave every call site working: `PlanValidator` keeps calling these members, now through
`GuardrailClauseText`. This is a **pure refactor** — GR2057's behaviour must be identical afterwards.

**Do NOT "improve" anything while you are in there.** In particular:

- `PresenceClause` matches **single-quoted** patterns only (`'…'`). Its own doc comment explains, in
  three bullets, why double-quoted and composed operands are deliberately unmatched. **Do not widen it.**
  A later task in this plan depends on that restriction being intact, and widening it to "make something
  work" is prohibition 4 of the plan's section 11.
- Do not rename a member, reorder a regex alternation, retighten a character class, or reflow a doc
  comment. Carry the XML doc comments across verbatim with their members.

**Scope boundary (harness-enforced):** Write only to
`src/Guardrails.Core/Loading/GuardrailClauseText.cs` and `src/Guardrails.Core/Loading/PlanValidator.cs`.
After this task completes, the harness runs a `git diff` check and rejects any edit outside these paths
— including changes to other production files, test files, or the `.csproj`. An out-of-scope edit fails
the task immediately and consumes a retry. **`tests/Guardrails.Core.Tests/GuardrailRequiresForbiddenTokenTests.cs`
is NOT in your scope and must not be touched**: it is the existing GR2057 suite, and this task's gate is
that it passes *unedited*. If you hit a compile error caused by a missing symbol in another file, do NOT
edit that file — write `{"needsHuman": "<what is missing>"}` to the state-out path and stop.

## Done when

- `GuardrailClauseText.cs` exists and declares all seven members (the six above plus `IsCommentLine`).
- `dotnet build` is green.
- The existing GR2057 tests pass **and their file is byte-identical** to what you started with.
