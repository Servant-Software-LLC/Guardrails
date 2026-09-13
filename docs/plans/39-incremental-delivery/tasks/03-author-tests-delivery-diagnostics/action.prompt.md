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

- **GR2078** — a **post-delivery wave with no entry preflight** (design 39 §1c, DECIDED in review).
- **GR2079** — a wave sets **`delivers: true` and carries no `guardrails/` exit gate** (§5), so an
  author learns that wave cannot deliver rather than discovering it by its absence from the report.

**Both are WARNINGS, not errors, and the reason is recorded in the design: an ERROR would fail plans
that are correct-but-unguarded.** Do not "strengthen" them to errors.

**Advance the next-free marker to GR2080**, and update the marker's prose — the comment currently
names the last taken code, and that claim goes stale the moment you add two.

**Pin these behaviours to these EXACT method names:**

- `GR2078_FiresWhenAPostDeliveryWaveHasNoEntryPreflight`
- `GR2078_IsSilentWhenTheWaveHasOne`
- `GR2078_IsSilentForAWaveThatFollowsNoDelivery` — the load-bearing negative: this must not fire on
  every wave, only on one that follows a delivery point.
- `GR2079_FiresWhenADeliveringWaveHasNoExitGate`
- `GR2079_IsSilentWhenTheWaveHasAnExitGate`
- `GR2077_RemainsReservedAndUnallocated` — assert no `DiagnosticCodes` constant equals
  `"GR2077"`. The reservation was prose-only: the pre-existing catalogue tests cannot catch
  taking it, because `TheNextFreeMarkerNamesACodeThatIsActuallyFree` only checks the marker is
  unused and above the high-water mark (taking 2077 AND 2078 still leaves the highest at 2078),
  and `NoTwoConstantsShareACode` never sees GR2077 at all since it is reserved in a COMMENT, not
  a constant.

**Note the three DECLARED-EXEMPT rows.** `GR2078_IsSilentWhenTheWaveHasOne`,
`GR2078_IsSilentForAWaveThatFollowsNoDelivery`, `GR2079_IsSilentWhenTheWaveHasAnExitGate` and
`BothAreWarnings_AndDoNotMoveTheExitCode` are NOT in the red census, because a diagnostic that
does not exist yet is silent and emits no exit code — they pass by construction on the stub tree
and demanding `Failed` from them made this task unsatisfiable (measured). They must still EXIST;
the census asserts that, and task 04's forward census requires each observed `Passed`.

**A third guardrail now checks the NUMBERING** (`03-codes-and-marker.ps1`): both constants
declared, GR2077 still unallocated, and exactly one live next-free marker reading GR2080. Before
it existed the cheapest passing implementation wrote the tests against the string literals and
never opened `DiagnosticCodes.cs`.
- `BothAreWarnings_AndDoNotMoveTheExitCode` — a warning that silently became an error would fail
  correct-but-unguarded plans.

The tests MUST COMPILE and FAIL. Do NOT implement the checks.

**Scope boundary (harness-enforced):** Write only to `tests/Guardrails.Core.Tests/WaveDelivery/WaveDeliveryDiagnosticsTests.cs` and `src/Guardrails.Core/Loading/DiagnosticCodes.cs`. After this
task completes, the harness runs a `git diff` membership check and rejects any edit outside these paths. An
out-of-scope edit fails the task immediately and consumes a retry. If you hit a compile error caused by a
missing symbol in another file, do NOT edit that file — write `{"needsHuman": "<what is missing>"}` to the
state-out path and stop.

**The harness runs this task's guardrails itself when you finish.** Do not try to run the guardrail scripts yourself: the shell they need is not granted to you, and a call refused on two attempts can halt the task even after the work is done.

