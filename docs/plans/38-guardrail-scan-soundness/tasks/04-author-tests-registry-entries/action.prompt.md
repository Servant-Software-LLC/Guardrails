## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g. `04-author-tests-registry-entries`), NOT the
  stableId. The harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "04-author-tests-registry-entries": { "someKey": "someValue" } }`.
- EXCEPTION — the CONTROL KEYS `needsHarnessWrite` and `needsHuman` are TOP-LEVEL
  SIBLINGS of your folder-name key, never nested inside it. They are instructions to
  the harness, not state, so the rule above does not cover them:
  `{ "04-author-tests-registry-entries": { "someKey": "someValue" },
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

Extend `tests/Guardrails.Core.Tests/BannedPatternRegistryTests.cs` (class
**`BannedPatternRegistryTests`**, existing traits unchanged) with firing controls for four new GR2037
entries that do not exist yet. Read `docs/plans/38-guardrail-scan-soundness.md` §7 for what each entry
catches, and read the existing tests in this file first — follow their shape rather than inventing one.

**Scope boundary (harness-enforced):** Write only to
`tests/Guardrails.Core.Tests/BannedPatternRegistryTests.cs`. After this task completes, the harness runs
a `git diff` check and rejects any edit outside it — including
`.claude/skills/plan-breakdown/references/banned-guardrail-patterns.json`, which a **later** task owns.
An out-of-scope edit fails the task immediately and consumes a retry. If you hit a compile error caused
by a missing symbol in another file, do NOT edit that file — write `{"needsHuman": "<what is missing>"}`
to the state-out path and stop.

**Pin exactly these nine method names** — the census guardrail binds to them:

| method | fixture | expects |
|---|---|---|
| `Entry608a_ContinuePreference_FiresGr2037` | a guardrail setting `ErrorActionPreference` to `Continue` | GR2037 citing `#608a` |
| `Entry608a_StopPreference_IsClean_NoGr2037` | the same guardrail set to `Stop` | no GR2037 |
| `Entry608b_GuardrailNotEndingOnExit_FiresGr2037` | a guardrail whose last statement is not an `exit` | GR2037 citing `#608b` |
| `Entry608b_GuardrailEndingOnExit_IsClean_NoGr2037` | the same, ending on `exit 0` | no GR2037 |
| `Entry561_CommentStripBeforeLiteralNeutralize_FiresGr2037` | a scan copy that blanks block comments **before** neutralizing string literals | GR2037 citing `#561` |
| `Entry561_LiteralNeutralizeFirst_IsClean_NoGr2037` | the same replaces in the correct order | no GR2037 |
| `Entry449_WholeFileReadToBannedLiteral_NoStrip_FiresGr2037` | `Get-Content -Raw` feeding a banned-literal `-match` with no comment strip between | GR2037 citing `#449` |
| `Entry449_Respelled_WholeFileRead_StillFires` | **the same shape respelled** — `[IO.File]::ReadAllText` or `Select-String -Path` instead of `Get-Content -Raw` | GR2037 citing `#449` |
| `Entry449_StripPresent_IsClean_NoGr2037` | the same read with a comment strip in between | no GR2037 |

`Entry449_Respelled_WholeFileRead_StillFires` is the load-bearing one and must not be dropped for being
awkward. Design 38 §11 names "a lint keyed on `Get-Content -Raw`, dodged by `Select-String`" as the way
this plan fails while looking finished; that test is the only thing standing between the entry and that
outcome.

Also **update `Registry_IsExactlyTheCuratedSet_NotWhateverAccumulated`** so the curated set is the seven
entries — `#73`, `#187a`, `#462`, `#608a`, `#608b`, `#561`, `#449`. That test is the registry's
deliberate gate on silent growth; raising it here, in the same change as the controls, is the point.

All ten tests MUST fail on the current tree — the four entries do not exist yet, so every firing control
finds no diagnostic and the curated-set assertion sees three entries where it now expects seven. A test
that passes today is not coupled to the entry it claims to pin.

Do **not** author the `mustMatch` / `mustNotMatch` fixtures inside the JSON registry — those ship with
the entries in the next task, and the registry's own meta-test
(`EverySeedEntry_BadPatternMatchesAllMustMatch_AndNoMustNotMatch`) already enforces them. Your fixtures
are C# strings in this test file, exercising the validator end to end.
