## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g. `03-author-tests-delivery-diagnostics`), NOT the
  stableId. The harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "03-author-tests-delivery-diagnostics": { "someKey": "someValue" } }`.
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

Author failing tests for the two WARNING codes this design adds, and reserve their constants.

**Test file:** `tests/Guardrails.Core.Tests/WaveDelivery/WaveDeliveryDiagnosticsTests.cs`
**Test class:** `WaveDeliveryDiagnosticsTests`
**Constants:** add both to `src/Guardrails.Core/Loading/DiagnosticCodes.cs`.

Every test carries `[Trait("Category", "WaveDelivery")]`.

**The codes, and the numbering — get this right, it has gone wrong three times recently.** Next free
is **GR2078**; **GR2077 is RESERVED BY NAME** for #587 check B and must not be taken.

- **GR2078** — a **post-delivery wave with no entry preflight** (design 39 §1c, DECIDED in review): a
  wave that follows a delivery point. A delivery point is an earlier wave whose
  `WaveNode.IsDeliveryPoint` is true, meaning its `brief.md` sets `delivers: true` AND it has an exit-gate
  check. Build the fixtures that way: a `delivers: true` wave with no exit gate never delivers, so nothing
  follows it as a delivery.
- **GR2079** — a wave sets **`delivers: true` and carries no `guardrails/` exit gate** (§5), so an
  author learns that wave cannot deliver rather than discovering it by its absence from the report. It
  reads `WaveNode.Delivers`, the declared flag, never `IsDeliveryPoint`, which is false for exactly the
  waves this code must name.

**Both are WARNINGS, not errors, and the reason is recorded in the design: an ERROR would fail plans
that are correct-but-unguarded.** Do not "strengthen" them to errors.

**Give each constant the catalogue's severity marker.** `DiagnosticCatalogue` parses
`DiagnosticCodes.cs` itself and reads each code's severity from the START of its doc comment, in the
shape every existing constant already uses:

```csharp
/// <summary>GR2079 (WARNING) — A wave sets <c>delivers: true</c> but carries no <c>guardrails/</c> exit gate, so it cannot deliver.</summary>
public const string <YourName> = "GR2079";
```

That is `GRxxxx (WARNING) — ` followed by a one-line summary.
`DiagnosticCatalogueTests.EveryCodeCarriesASeverityMarkerAndAUsableSummary` fails a constant without it.

**One existing test goes red when you add them, and that is expected.**
`DiagnosticCatalogueTests.EverySeverityMarkerMatchesWhatTheSourceTreeActuallyDoes` re-derives each code's
severity from its emission sites in `src/`, and a code emitted nowhere reads as retired. Your two
`(WARNING)` markers have no emission site until task 04 adds the checks, so that test fails on your
tree, and task 04's tests-pass guardrail requires it green again. Do not "fix" it: do not mark the codes
`(RETIRED)` or `(ERROR)`, and do not edit that test, which is outside your scope.

**Advance the next-free marker to GR2080**, and update the marker's prose — the comment currently
names the last taken code, and that claim goes stale the moment you add two.

**Pin these behaviours to these EXACT method names:**

- `GR2078_FiresWhenAPostDeliveryWaveHasNoEntryPreflight`
- `GR2078_IsSilentWhenTheWaveHasOne`
- `GR2078_IsSilentForAWaveThatFollowsNoDelivery` — the load-bearing negative: this must not fire on
  every wave, only on one that follows a delivery point.
- `GR2079_FiresWhenADeliveringWaveHasNoExitGate`
- `GR2079_IsSilentWhenTheWaveHasAnExitGate`
- `BothAreWarnings_AndDoNotMoveTheExitCode` — a warning that silently became an error would fail
  correct-but-unguarded plans.
- `GR2077_RemainsReservedAndUnallocated` — assert no `DiagnosticCodes` constant equals
  `"GR2077"`. The reservation was prose-only: the pre-existing catalogue tests cannot catch
  taking it, because `TheNextFreeMarkerNamesACodeThatIsActuallyFree` only checks the marker is
  unused and above the high-water mark (taking 2077 AND 2078 still leaves the highest at 2078),
  and `NoTwoConstantsShareACode` never sees GR2077 at all since it is reserved in a COMMENT, not
  a constant.

**Five rows are DECLARED EXEMPT from the red census.**
- `GR2078_IsSilentWhenTheWaveHasOne`, `GR2078_IsSilentForAWaveThatFollowsNoDelivery`,
  `GR2079_IsSilentWhenTheWaveHasAnExitGate` and `BothAreWarnings_AndDoNotMoveTheExitCode`: a
  diagnostic that does not exist yet is silent and emits no exit code, so they pass by construction
  on the stub tree, and demanding `Failed` from them made this task unsatisfiable (measured).
- `GR2077_RemainsReservedAndUnallocated`: this task's own third guardrail refuses a `"GR2077"`
  constant, so on every tree that passes it a correct reservation test is green. Pinning it red let
  only a wrongly-failing test through (review, 2026-09-13). Write it correctly; do NOT make it fail
  to please the census.

All five must still EXIST: the census asserts that, and task 04's forward census requires each
observed `Passed`.

**A third guardrail now checks the NUMBERING** (`03-codes-and-marker.ps1`): both constants
declared, GR2077 still unallocated, and exactly one live next-free marker reading GR2080. Before
it existed the cheapest passing implementation wrote the tests against the string literals and
never opened `DiagnosticCodes.cs`.

The pinned tests MUST COMPILE and FAIL; the exempt rows pass. Do NOT implement the checks.

**Scope boundary (harness-enforced):** Write only to `tests/Guardrails.Core.Tests/WaveDelivery/WaveDeliveryDiagnosticsTests.cs` and `src/Guardrails.Core/Loading/DiagnosticCodes.cs`. After this
task completes, the harness runs a `git diff` membership check and rejects any edit outside these paths. An
out-of-scope edit fails the task immediately and consumes a retry. If you hit a compile error caused by a
missing symbol in another file, do NOT edit that file — write `{"needsHuman": "<what is missing>"}` to the
state-out path and stop.

**The harness runs this task's guardrails itself when you finish.** Do not try to run the guardrail scripts yourself: the shell they need is not granted to you, and a call refused on two attempts can halt the task even after the work is done.

