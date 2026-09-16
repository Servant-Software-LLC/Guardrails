## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g. `05-implement-supply-certification`), NOT the
  stableId. The harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "05-implement-supply-certification": { "someKey": "someValue" } }`.
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

Implement the pure certification gate and the shared threshold rule, add the `resource-supply` parser
case, and **delete the four shipped members design 41 §3.4 retires**. Design 41 §2.1, §2.4, §3.1, §3.4.

**Do NOT edit the tests.** Your `writeScope` covers only `src/` files. If you believe a test is genuinely
wrong, emit `{"needsHuman": "<why, with the file:line>"}` rather than working around it.

### 1. `GateThreshold.Effective` — one rule, one place

A per-gate `gateThresholds` override for that gate when present, else the run-wide
`escalationThreshold`. A null `autonomy` block resolves to the documented default
(`EscalationThreshold.High`) — **never `Critical`**, which would open the auto-resolve gate on a run that
configured nothing.

Then **replace `CriticalityJudge`'s private `EffectiveThreshold` with a call to it** and delete the
private copy. The rule is spelled twice in the tree today and this is the first half of collapsing it.

**`Scheduler.EffectiveThresholdToken` is the OTHER half and it is NOT yours.** `Scheduler.cs` is outside
your `writeScope` and belongs to the wiring task. Do not reference it, do not edit it, and do not treat
its survival as your task being incomplete.

### 2. `OverwatchSupplyAutoResolve.Certify` — the deterministic gate

**It is a PURE function: no git, no journal, no filesystem.** A guardrail enforces that by banning
`RunJournal`, `SuppliedDrain`, `PlanDefinition`, `File.`, `Directory.` and `ProcessStartInfo` from the
whole file. This is the property that makes the gate trustworthy: everything it decides on was
established by the harness before it ran, so there is nothing for it to go and look up.

The checks, **in this order** — the order is load-bearing, because each refusal row in the tests
observes its own token only when every earlier check has passed:

1. `policy == Auto`, the block is present, and
   `GateThreshold.Effective(autonomy, CriticalityGate.NeedsHuman) == Critical`. The Scheduler already
   checked this; the gate owns the rule so a future caller cannot skip it. Fails `dial-not-critical`.
2. `gateThresholds.review-gate` is not `proceed-unreviewed`. Fails `proceed-unreviewed`.
3. A proposal exists and its classification is `retryable`. Fails `doomed`.
4. It contains at least one `resource-supply` op. Fails `no-resource-supply-op`.
5. Every proposed path, after `/` and `./` normalization, is a candidate. Fails `not-a-candidate`.
6. No path is proposed twice. Fails `duplicate-path`.

**A pass certifies every proposed op, each paired with ITS CANDIDATE's source sha** — never a sha taken
from the proposal. The model cannot contribute a fact about where bytes came from.

**There is no partial certification.** Any failure refuses the whole proposal. A partial supply would
re-arm a task that then halts again on the file it still does not have, and spend money doing it.

### 3. The `resource-supply` parser case

`OverwatchProposal.ParseFix` gains a `resource-supply` case yielding
`OverwatchFixKind.ResourceSupply` with the op's `path` as `TargetPath`. An op with no non-blank `path`
is dropped by the existing advisory-never-gates rule, exactly as `file-edit` already is.

`OverwatchFixClassifier` **does not change**: a `ResourceSupply` op already falls through to `Default`
(propose-only), so it can never reach the guidance/budget allowlist. A test pins that it stays so.

### 4. Delete the four retired members — and repair what referenced them

Delete all four from `src/Guardrails.Core/Execution/OverwatchDecision.cs`:

- **`OverwatchSupplyAutoResolve.Resolve`** — replaced by `Certify`. Its staged-tree drain and its
  `plan.Workspace` target are the #712 defect, not evidence.
- **`ProposedSequenceFor`** — `RunCommand`'s halt text is the single producer of those three commands at
  every dial (`d41-below-critical`). This is a second, unreachable copy of the same message.
- **`OverwatchDecisionKind.AutoResolve`** and **`OverwatchDecision.AutoResolvedPaths`** — `OverwatchDecision`
  is the control-flow signal the `TaskExecutor` retry loop reads, and a supply is a Scheduler action that
  never passes through that loop. The answer to "which component acts on `AutoResolve`" is: none should.

Do not sweep anything else that merely sounds similar. `SafeToAutoResolve`, `SchedulerDriftAutoResolveTests`,
`DefinitionDriftAutoResolveTests` and `DecisionEntry.AutoResolved` belong to the unrelated definition-drift
feature and must not be touched.

**Confirm the blast radius yourself before you cut** — ship the command, not my word for it:

```
grep -rn "AutoResolvedPaths\|ProposedSequenceFor\|OverwatchDecisionKind.AutoResolve" src/ tests/
grep -rn "OverwatchSupplyAutoResolve" src/ tests/
```

When this task was written those reported: the declarations themselves, three `<see cref>` doc comments
(`OverwatchDecision.cs` twice and `OverwatchFix.cs` once), and the test file task 04 already rewrote —
no production caller at all. **Trust your own grep if it disagrees.**

**The doc comments are a BUILD ERROR, not a nicety.** `TreatWarningsAsErrors` is true repo-wide
(`Directory.Build.props`), so a `<see cref>` left pointing at a deleted member is CS1574 and it FAILS THE
BUILD. Rewrite each of them as you delete:

- `OverwatchDecision.cs` — the `AutoResolvedPaths` doc's `<see cref="OverwatchSupplyAutoResolve.Resolve"/>`
  and the `AutoResolve` enum member's doc go with their members.
- `OverwatchDecision.cs` — the `OverwatchSupplyAutoResolve` class summary still describes the old
  staged-file drain, `dial:critical` gating and the three-command proposal. Rewrite it for what the class
  now is: the pure §3.1 gate. Its `<see cref="SuppliedDrain.Drain"/>` and
  `<see cref="RunJournal.RecordSupplied"/>` references go with the prose, and they must, because the
  purity guardrail bans both identifiers from the file.
- `OverwatchFix.cs` — `OverwatchFixKind.ResourceSupply`'s summary says the judge names a path *the
  operator has already staged*. That premise is what `d41-supply-source` overturned: the source is the
  file as committed at the operator checkout's `HEAD`. Correct the prose. Its
  `<see cref="OverwatchSupplyAutoResolve"/>` names the TYPE, which survives, so that reference stays valid.

Do NOT edit the authored tests; emit `{"needsHuman": "<why>"}` if one is genuinely wrong.

**Scope boundary (harness-enforced):** Write only to `src/Guardrails.Core/Execution/GateThreshold.cs`, `src/Guardrails.Core/Execution/OverwatchDecision.cs`, `src/Guardrails.Core/Execution/OverwatchProposal.cs`, `src/Guardrails.Core/Execution/OverwatchFix.cs`, and `src/Guardrails.Core/Execution/CriticalityJudge.cs`. After this
task completes, the harness runs a `git diff` membership check and rejects any edit outside these paths. An
out-of-scope edit fails the task immediately and consumes a retry. If you hit a compile error caused by a
missing symbol in another file, do NOT edit that file — write `{"needsHuman": "<what is missing>"}` to the
state-out path and stop.

**The harness runs this task's guardrails itself when you finish.** Do not try to run the guardrail scripts yourself: the shell they need is not granted to you, and a call refused on two attempts can halt the task even after the work is done. Tests authored by OTHER tasks may legitimately fail on your base until their own implementing task lands; only this task's tests are yours to turn green.
