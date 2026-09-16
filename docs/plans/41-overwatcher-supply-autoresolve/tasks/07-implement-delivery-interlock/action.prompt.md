## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g. `07-implement-delivery-interlock`), NOT the
  stableId. The harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "07-implement-delivery-interlock": { "someKey": "someValue" } }`.
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

Implement the shared delivery-interlock predicate task 06 stubbed, so the `OverwatchSupply` rows in
`RunOutcomePolicyTests` and `WaveScopedInterlockTests` pass — design 41 §6 ("Delivery"), closing
**"What the code does today" fact 11**.

**One token set, one owner.** `RunOutcomePolicy` gets ONE private predicate — *this decision holds
delivery* — and **both** `SuppressingDecision` (run end, `Scheduler.cs:1187`) and
`SuppressingDecisionForDelivery` (every wave barrier, `Scheduler.cs:3085`) are defined in terms of it:

```csharp
private static bool HoldsDelivery(DecisionEntry decision) =>
    decision.Decision == DecisionTokens.ProceededBestGuess ||
    decision.Decision == DecisionTokens.ProceededUnreviewed ||
    decision.Decision == DecisionTokens.AutoSupplied;
```

That is the whole implementation of the predicate. The two spellings keep their existing shapes and call it:

```csharp
public static DecisionEntry? SuppressingDecision(IEnumerable<DecisionEntry> decisions) =>
    decisions.FirstOrDefault(HoldsDelivery);

public static DecisionEntry? SuppressingDecisionForDelivery(
    IEnumerable<DecisionEntry> decisions, IReadOnlyCollection<string> coveredWaves) =>
    decisions.FirstOrDefault(d => HoldsDelivery(d) && (d.Wave is null || coveredWaves.Contains(d.Wave)));
```

**Do not re-list a token anywhere else in this file.** A guardrail
(`03-one-shared-predicate.ps1`) requires `DecisionTokens.AutoSupplied` and
`DecisionTokens.ProceededBestGuess` to appear **exactly once each** in the comment-stripped source, and
requires the predicate's own name to appear at least three times (its declaration plus both call sites).
`DecisionTokens.ProceededUnreviewed` legitimately appears a second time, in
`ProceededUnreviewedWaveCount` — leave that method exactly as it is. An auto-supply is **not** an
unreviewed wave and must never reach the distinct exit code.

**Why this shape and not two lists that happen to agree.** Fact 11 is that the two spellings already listed
the tokens separately. They agreed, so nothing was visibly wrong — until a token is added to one of them. A
review cannot see that defect; only the shared definition removes it. `SuppressesDelivery` (both overloads)
is already defined in terms of the entry-returning pair (#597), so it inherits the predicate for free: do
not give it a token test of its own.

### The tokens

Task 06 declared `DecisionTokens.AutoSupplied` (`"auto-supplied"`) and `DecisionTokens.Advisory`
(`"advisory"`) in `src/Guardrails.Core/Execution/DecisionEntry.cs`. Your job there is the CONTRACT, not the
values:

- Keep both constants and their values exactly as they are.
- Make sure `AutoSupplied`'s XML doc states the two facts a future reader needs: it is deliberately **not**
  `AutoApplied` (that token means a provably-safe resolution; this is a bounded judgement, design 41 §6),
  and it **holds delivery at run end AND at every wave barrier**.
- `Advisory` is the constant form of the literal `Overwatch.NonGrant` already emits
  (`Overwatch.cs:370-376`). It is outcome-inert — it must NOT be in the predicate. Do not edit
  `Overwatch.cs` to consume the constant: that file is outside your scope (task 09 owns it).

Also update `RunOutcomePolicy`'s own class-level XML doc, which today names only the two shipped tokens as
the suppression set. A doc comment that lists a token set is a third spelling of it — name the predicate
instead, and let it be the single place the set is written.

### What must go green

- The `OverwatchSupply`-traited rows in `tests/Guardrails.Core.Tests/RunOutcomePolicyTests.cs` and
  `tests/Guardrails.Core.Tests/WaveDelivery/WaveScopedInterlockTests.cs` (guardrails 01 and 02).
- **Every OTHER row already in those two files** (guardrail 04). Task 06's stub routed the shipped
  `SuppressingDecision` through a throwing predicate, which turned those rows red on purpose. They are not
  collateral damage to be tolerated — they are the never-weaker proof that the shipped run-end interlock
  still behaves exactly as it did, including `TheOperatorOverrideStillLiftsTheInterlock`
  (`--merge-on-success` must still deliver past a suppressing decision, #361/#597).

Do NOT edit the authored tests; emit {"needsHuman": "<why>"} if one is genuinely wrong.

**Scope boundary (harness-enforced):** Write only to `src/Guardrails.Core/Execution/DecisionEntry.cs` and `src/Guardrails.Core/Execution/RunOutcomePolicy.cs`. After this
task completes, the harness runs a `git diff` membership check and rejects any edit outside these paths. An
out-of-scope edit fails the task immediately and consumes a retry. If you hit a compile error caused by a
missing symbol in another file, do NOT edit that file — write `{"needsHuman": "<what is missing>"}` to the
state-out path and stop.

**The harness runs this task's guardrails itself when you finish.** Do not try to run the guardrail scripts yourself: the shell they need is not granted to you, and a call refused on two attempts can halt the task even after the work is done. Tests authored by OTHER tasks may legitimately fail on your base until their own implementing task lands; only this task's tests are yours to turn green.
