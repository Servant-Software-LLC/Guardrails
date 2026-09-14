## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g. `01-author-tests-wave-delivers-flag`), NOT the
  stableId. The harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "01-author-tests-wave-delivers-flag": { "someKey": "someValue" } }`.
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

**WHERE THE FLAG LIVES — read this before anything else (review, 2026-09-11).** The flag is
`delivers: true` in the **YAML front matter of the wave's existing optional `brief.md`**
(`WaveNode.BriefFileName`), NOT a new per-wave JSON manifest. The design's first draft said
"the wave's own manifest"; **there is no wave manifest**, SSOT §14.1 says v1 has *"no per-wave
config in v1"*, and the obvious guess fails SILENTLY: a `guardrails.json` dropped into `wave-NN/` is
ignored by the plan's loader. The plan stays waved, `validate` says nothing, and a flag written there
is never read, so it looks as if it worked (measured, review 2026-09-13). Only a command pointed at
that wave directory itself treats it as a separate plan (`WaveFolder.TryResolveWaveTarget`). Do not
create a new file.

Author failing tests AND the minimal stub for the per-wave `delivers` flag — design 39 §1b and §3.

**Test file:** `tests/Guardrails.Core.Tests/WaveDelivery/WaveDeliversFlagTests.cs`
**Test class:** `WaveDeliversFlagTests`
**Stub: TWO members on `src/Guardrails.Core/Model/WaveNode.cs`, both with THROWING getters.**

```csharp
/// <summary>The wave's DECLARED <c>delivers: true</c>, from its brief.md front matter. Default false.</summary>
public bool Delivers { get => throw new NotImplementedException(); init { } }

/// <summary>True only when the wave delivers at its barrier: <see cref="Delivers"/> AND at least one exit-gate check.</summary>
public bool IsDeliveryPoint => throw new NotImplementedException();
```

They are two members because two consumers need two different facts (review, 2026-09-13).
- `GR2079` (task 04: a wave sets `delivers: true` but has no exit gate) needs the DECLARED flag, because
  it must name exactly the waves whose flag cannot take effect.
- The barrier delivery (tasks 08 and 29) needs the EFFECTIVE predicate. An empty exit gate returns
  `Pass`, so a gate-less delivering wave would otherwise deliver behind zero checks.

One member can serve only one of them.

Why the getters throw: a working `bool Delivers { get; init; }` returns `false`, which is exactly what
`Delivers_DefaultsToFalse_WhenTheManifestOmitsIt` and the two `IsDeliveryPoint`-is-false rows assert, so
they would pass on the stub and fail the red census (review, 2026-09-13). `WaveNode` is a `sealed
record`, so its generated `Equals`/`GetHashCode`/`ToString` also throw while the stub stands; that is
expected, and task 02 replaces both stubs.

Build the plans with `WavePlanBuilder` (`WaveBrief` writes a wave's `brief.md`, `WaveGuardrail` adds an
exit-gate check) and load them through it, so every row reads the members from a node the real loader
built.

Every test carries `[Trait("Category", "WaveDelivery")]`.

**The default is the whole safety property.** DECIDED in review: per-wave `delivers`, **default
false**. A plan that marks nothing must behave byte-identically to today — one merge at run end.
Default `true` was rejected precisely because it would turn every existing waved plan into a per-wave
deliverer on upgrade, changing what those plans do with the user's branch without anyone asking.

**Pin these behaviours to these EXACT method names:**

- `Delivers_DefaultsToFalse_WhenTheManifestOmitsIt` — the never-weaker requirement.
- `Delivers_IsTrue_WhenTheManifestSetsIt`
- `WaveDefinitionHash_ChangesWhenDeliversChanges` — flipping the flag on a wave must MOVE that
  wave's definition hash, so a change to delivery behaviour on a COMPLETED wave trips drift
  (SSOT §14.6/§14.7) instead of passing silently under a review marker that still reads
  `passed`. This is FREE with the chosen surface and the test exists to prove it stayed free:
  `WaveDefinitionHash.Compute` → `GateDefinitionOf` already folds `brief.md` when present, and
  says so in its own comment. Assert it rather than assuming it.
- `AWaveWithNoGuardrailsFolder_IsNeverADeliveryPoint` — §3: no gate, no delivery, regardless of the
  flag. A wave whose `brief.md` sets `delivers: true` and that has no `guardrails/` folder has
  `IsDeliveryPoint == false`. It waits for the run-end delivery like today.
- `Delivers_StaysTheDeclaredFlag_WhenTheWaveHasNoExitGate` — that same wave has `Delivers == true`.
  Rejects folding the gate check into `Delivers`, which would leave `GR2079` nothing to fire on.
- `IsDeliveryPoint_IsTrue_WhenTheWaveDeliversAndHasAnExitGate` — `delivers: true` plus one exit-gate
  check. Rejects a predicate that is always false.
- `IsDeliveryPoint_IsFalse_WhenTheManifestOmitsDelivers` — an exit-gate check and no `delivers` key.
  Rejects treating "has an exit gate" alone as a delivery point, which would turn every gated wave of an
  existing waved plan into a per-wave deliverer.
- `APlanMarkingNoWave_LoadsIdenticallyToBefore` — assert the never-weaker property directly rather
  than trusting it falls out.

**Two rows are DECLARED EXEMPT from the red census.**
- `WaveDefinitionHash_ChangesWhenDeliversChanges`: the hash already folds `brief.md` (see its bullet),
  so a correct test is green on arrival (measured with `guardrails plan-hash`, review 2026-09-13).
- `APlanMarkingNoWave_LoadsIdenticallyToBefore`: it pins behaviour that must NOT change, so a correct
  test has nothing to be red about.

Both must still EXIST: the census asserts that, and task 02's forward census requires each observed
`Passed`. Write them correctly; do NOT make them fail to please the census.

The pinned tests MUST COMPILE and FAIL; the exempt rows need not. Do NOT implement the behaviour.

**Scope boundary (harness-enforced):** Write only to `tests/Guardrails.Core.Tests/WaveDelivery/WaveDeliversFlagTests.cs` and `src/Guardrails.Core/Model/WaveNode.cs`. After this
task completes, the harness runs a `git diff` membership check and rejects any edit outside these paths. An
out-of-scope edit fails the task immediately and consumes a retry. If you hit a compile error caused by a
missing symbol in another file, do NOT edit that file — write `{"needsHuman": "<what is missing>"}` to the
state-out path and stop.

**The harness runs this task's guardrails itself when you finish.** Do not try to run the guardrail scripts yourself: the shell they need is not granted to you, and a call refused on two attempts can halt the task even after the work is done.

