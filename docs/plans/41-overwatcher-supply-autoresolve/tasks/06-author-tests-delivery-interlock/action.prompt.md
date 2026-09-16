## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g. `06-author-tests-delivery-interlock`), NOT the
  stableId. The harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "06-author-tests-delivery-interlock": { "someKey": "someValue" } }`.
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

Author failing tests AND the minimal stub for the **one shared delivery-interlock predicate** — design 41
§6 ("Delivery"), and the defect it closes: **"What the code does today" fact 11**.

**The defect, stated once.** `RunOutcomePolicy.SuppressingDecision` (run end, called at `Scheduler.cs:1187`)
and `RunOutcomePolicy.SuppressingDecisionForDelivery` (every wave barrier, called at `Scheduler.cs:3085`)
each list the suppressing tokens **separately** — measured at `RunOutcomePolicy.cs:56-59` and `:82-87`. A new
suppressing token added to only one of them leaks machine-decided work onto the user's branch at a wave
barrier, and every proof run that passes `--no-merge-on-success` would still be green. Design 41 adds exactly
such a token, so this is not hypothetical.

**Test files (both already exist — ADD to them, do not rewrite them):**
- `tests/Guardrails.Core.Tests/RunOutcomePolicyTests.cs` — class `RunOutcomePolicyTests` (the run-end rows)
- `tests/Guardrails.Core.Tests/WaveDelivery/WaveScopedInterlockTests.cs` — class `WaveScopedInterlockTests`
  (the wave-barrier rows)

**Stub files:** `src/Guardrails.Core/Execution/RunOutcomePolicy.cs` and
`src/Guardrails.Core/Execution/DecisionEntry.cs`.

### The two new decision tokens (data — declare them with their real values)

Add both to `DecisionTokens` in `src/Guardrails.Core/Execution/DecisionEntry.cs`. They are **data, not
behaviour** — there is no behavioural stub for a `const string`, and the tests cannot compile without them
(#155: the TDD red must COMPILE and fail).

```csharp
/// <summary>
/// A certified missing-resource auto-resolve committed a file onto the plan branch (design 41 §6, SSOT
/// §9.2.2). Deliberately NOT <see cref="AutoApplied"/>: that token means a PROVABLY SAFE resolution, and
/// this is a bounded judgement — a model decided the halt was about this file. It HOLDS DELIVERY exactly
/// as <see cref="ProceededBestGuess"/> does, at run end AND at every wave barrier.
/// </summary>
public const string AutoSupplied = "auto-supplied";

/// <summary>
/// The overwatcher was consulted, or began an auto-resolve, and changed nothing it was asked to (design 41
/// §6). Already EMITTED today by <c>Overwatch.NonGrant</c> (<c>Overwatch.cs:370-376</c>) as a bare string
/// literal; this promotes it to a constant so a consumer asserts against the constant, never a literal.
/// Outcome-inert: it does not hold delivery.
/// </summary>
public const string Advisory = "advisory";
```

`Advisory`'s value must be exactly the string `Overwatch.NonGrant` already emits. Do **not** edit
`Overwatch.cs` to use the constant — that file is outside your scope (task 09 owns it).

### The stub: ONE throwing predicate, and BOTH spellings defined in terms of it

In `src/Guardrails.Core/Execution/RunOutcomePolicy.cs`:

```csharp
/// <summary>
/// THE delivery-interlock token set, spelled ONCE (design 41 §6, fact 11). Both
/// <see cref="SuppressingDecision"/> (run end) and <see cref="SuppressingDecisionForDelivery"/> (every wave
/// barrier) are defined in terms of it, so a new suppressing token cannot be added to one spelling and
/// missed by the other. Task 07 implements it: true for <c>proceeded-best-guess</c>,
/// <c>proceeded-unreviewed</c> and <c>auto-supplied</c>; false for every other token.
/// </summary>
private static bool HoldsDelivery(DecisionEntry decision) => throw new NotImplementedException();
```

and rewire both existing spellings to call it — this rewiring is the stub, and it is the whole point:

```csharp
public static DecisionEntry? SuppressingDecision(IEnumerable<DecisionEntry> decisions) =>
    decisions.FirstOrDefault(HoldsDelivery);

public static DecisionEntry? SuppressingDecisionForDelivery(
    IEnumerable<DecisionEntry> decisions, IReadOnlyCollection<string> coveredWaves) =>
    decisions.FirstOrDefault(d => HoldsDelivery(d) && (d.Wave is null || coveredWaves.Contains(d.Wave)));
```

**Keep `HoldsDelivery(d)` FIRST in that `&&`.** With the wave filter first, `&&` short-circuits on an
uncovered wave and the predicate is never reached, so
`AnAutoSuppliedDecisionInAnUncoveredWave_DoesNotHoldThisDelivery` would pass on the stub and fail the red
census. The order is also the correct reading: "does this decision hold delivery at all" is the question the
shared predicate owns; the wave scope only narrows WHICH delivery it holds.

Leave `SuppressesDelivery` (both overloads) and `ProceededUnreviewedWaveCount` exactly as they are. The two
bool overloads are already defined in terms of the entry-returning pair (#597), so they inherit the stub for
free — do not give either its own token list.

**Expect a loud, temporary red, and do not soften it.** Routing the SHIPPED `SuppressingDecision` through a
throwing predicate turns the existing rows in both test files — and any other suite that reaches an interlock
— red until task 07 lands. That is correct and expected: `FirstOrDefault(predicate)` on a NON-EMPTY stream
reaches the predicate. The temptation is to "protect" them by leaving `SuppressingDecision` on its own token
list and stubbing only the delivery-scoped spelling; **that reintroduces fact 11 verbatim** and makes the
`BothSpellingsAgreeOnEveryToken_OverTheSameDecision` row unprovable. Task 07 carries a guardrail
(`04-existing-interlock-rows-still-green.ps1`) whose only job is to prove those rows came back green.

### Traits — a NEW value, and only on the rows you add

Every test you ADD carries `[Trait("Category", "OverwatchSupply")]`. That value is **new**: the existing
Supply-area tests carry `Category="Supply"` (11 files, measured), and the plan's baseline preflight excludes
the plan trait by EXACT `Category!=` match, so the two must not be confused.

**Do NOT retrofit the trait onto the rows already in these two files.** They are neither new nor edited, and
tagging them would pull them out of the baseline preflight — weakening the one check that proves this area
was green before the plan started.

### Pin these behaviours to these EXACT method names

**In `RunOutcomePolicyTests` (run-end, run-scoped):**

- `SuppressesDelivery_True_WhenAutoSuppliedRecorded` — a run that recorded one `auto-supplied` entry
  (alongside ordinary `escalated`/`halted` ones, which must not mask it) does not auto-deliver.
- `SuppressingDecision_ReturnsTheAutoSuppliedEntry_AsTheEvidence` — the EVIDENCE, not a bare bool (#597):
  the returned entry is the `auto-supplied` one and carries its `Subject` (the halted task's id). The #597
  banner names which decision at which task; a bool cannot.
- `AutoSupplied_SuppressesDelivery_ButIsNotAnUnreviewedWave` — both facts over the same `decisions[]`:
  `SuppressesDelivery` is true AND `ProceededUnreviewedWaveCount` is 0. An auto-supply is not an unreviewed
  wave and must not reach the distinct exit code.
- `AutoApplied_StillDelivers_BecauseAutoSuppliedIsADifferentToken` — the load-bearing NEGATIVE. A run whose
  only decision is `auto-applied` (a provably-safe resolution) DELIVERS normally. This is the row that
  rejects a predicate returning true for everything, and it is the token-confusion row: `auto-supplied` is
  deliberately not `auto-applied` (design 41 §6).

**In `WaveScopedInterlockTests` (the wave barrier — the row that is the whole point):**

- `AnAutoSuppliedDecision_HoldsTheWaveBarrierDelivery` — an `auto-supplied` entry with `Wave` set to
  wave-02 holds the delivery whose covered set is `{wave-02}`, and
  `SuppressingDecisionForDelivery` returns THAT entry. **Without this row a token added to only the run-end
  spelling passes every other test in the plan**, and machine-decided work reaches the user's branch at a
  barrier.
- `AnAutoSuppliedDecisionOutsideAnyWave_HoldsEveryDelivery` — `Wave` is null (design 41 §6 sets `wave` only
  when the plan is waved, so a flat plan's entry has none). It holds the delivery whatever the covered set
  is: the check fails CLOSED, exactly as it already does for `proceeded-best-guess`.
- `AnAutoSuppliedDecisionInAnUncoveredWave_DoesNotHoldThisDelivery` — an `auto-supplied` entry in wave-01,
  whose work already reached the user's branch, does not hold a later `{wave-02, wave-03}` delivery. The new
  token joins the token set; it does not widen the SCOPING rule design 39 settled.
- `BothSpellingsAgreeOnEveryToken_OverTheSameDecision` — the anti-fact-11 row, and the one to write most
  carefully. For EACH token in `DecisionTokens` that a run can record — at minimum `ProceededBestGuess`,
  `ProceededUnreviewed`, `AutoSupplied`, `AutoApplied`, `Advisory`, `Escalated`, `Halted`, `Observed`,
  `BlockerRetried`, `NoVerdict` — build a single-entry stream whose `Wave` is a wave you also pass as the
  covered set (so the wave filter is satisfied for every row and the comparison isolates the TOKEN set), and
  assert:

  ```
  RunOutcomePolicy.SuppressingDecision(stream) is not null
      == RunOutcomePolicy.SuppressingDecisionForDelivery(stream, covered) is not null
  ```

  Assert it token by token with the token named in the failure message, so a future token added to one
  spelling names itself. Do not assert a hard-coded list of which tokens suppress — that is the other three
  rows' job; this row asserts the two spellings cannot DISAGREE.

**No row asserts over an EMPTY decisions stream.** `Enumerable.FirstOrDefault(source, predicate)` never
invokes the predicate on an empty sequence, so such a row would be green on the throwing stub and would have
to be declared exempt. There is no reason to write one here — the existing rows already cover the empty case.

**There are NO declared red-census exemptions in this task.** Every pinned row above calls a spelling over a
non-empty stream and is therefore genuinely red against the throwing predicate.

The pinned tests MUST COMPILE and FAIL. Do NOT implement `HoldsDelivery`.

**Scope boundary (harness-enforced):** Write only to `tests/Guardrails.Core.Tests/RunOutcomePolicyTests.cs`, `tests/Guardrails.Core.Tests/WaveDelivery/WaveScopedInterlockTests.cs`, `src/Guardrails.Core/Execution/DecisionEntry.cs`, and `src/Guardrails.Core/Execution/RunOutcomePolicy.cs`. After this
task completes, the harness runs a `git diff` membership check and rejects any edit outside these paths. An
out-of-scope edit fails the task immediately and consumes a retry. If you hit a compile error caused by a
missing symbol in another file, do NOT edit that file — write `{"needsHuman": "<what is missing>"}` to the
state-out path and stop.

**The harness runs this task's guardrails itself when you finish.** Do not try to run the guardrail scripts yourself: the shell they need is not granted to you, and a call refused on two attempts can halt the task even after the work is done.
