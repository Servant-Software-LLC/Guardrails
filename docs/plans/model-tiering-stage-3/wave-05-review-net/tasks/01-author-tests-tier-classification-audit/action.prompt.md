## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — for a WAVED plan that is the WAVE-QUALIFIED id, i.e.
  `{ "wave-05-review-net/01-author-tests-tier-classification-audit": { "someKey": "someValue" } }`.
  The harness REJECTS a fragment keyed by anything else (every attempt), including the bare folder
  name and the stableId.
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

Author the TDD **red** for this wave's deterministic half: a folder-observable audit that finds every
prompt task and every surviving prompt-judge guardrail a tiering-configured plan left **unclassified** —
and that produces **nothing at all** on a plan generated before tiering shipped.

You write the tests, the two committed fixtures, and a **throwing stub**. You do **not** implement the
audit — `02-implement-tier-classification-audit` does, and it may not edit anything you author here.

**Scope boundary (harness-enforced):** Write only to
`tests/Guardrails.Core.Tests/ModelTiering/TierClassificationAudit.cs` (the stub),
`tests/Guardrails.Core.Tests/ModelTiering/TierClassificationAuditTests.cs` (the tests), and anything under
`tests/Guardrails.Core.Tests/TestData/tier-tags/` (the fixtures). After this task completes the harness
runs a `git diff` check and rejects any edit outside those paths — including other production files,
neighbouring test files, and the `.csproj`. An out-of-scope edit fails the task immediately and consumes a
retry. If you hit a compile error caused by a missing symbol in another file, do NOT edit that file — write
`{"needsHuman": "<what is missing>"}` to the state-out path and stop.

### Read these first — they are the shape you are copying

- `tests/Guardrails.Core.Tests/SeamProofPlacement.cs` and `tests/Guardrails.Core.Tests/SeamProofProximityTests.cs`.
  This is the precedent for the whole task: a rule that ships **no `validate` code and no GR code**, so its
  only durable regression signal is a reference implementation living in `tests/` plus a committed fixture
  pair. Copy its posture — the two-sided fixtures, `RunOnMutatedCopy` for the cases a committed fixture
  cannot carry, the non-vacuity assertion before every "no findings" assertion, and a finding record whose
  `Detail` names the remedy.
- `tests/Guardrails.Core.Tests/TestData/seam-proof-at-tstar/`. The shape of a plan folder built as test
  data: `guardrails.json` plus `tasks/<id>/{task.json, action.*, guardrails/NN-*.ps1}`, all files tiny.
- `src/Guardrails.Core/Model/ActionDefinition.cs` — **read the `TierOrigin` docstring before writing a
  line of the audit's contract.** It is the whole reason this finding is computable.
- `src/Guardrails.Core/Model/GuardrailDefinition.cs` — `Tier`, the judge-side frontmatter tag site.
- `tests/Guardrails.Integration.Tests/ModelTiering/NoRoutingGolden.cs` — `IsUnconfiguredForTiering`, the
  already-codified gate predicate. **Do not invent a second spelling of it.** (You cannot call it: it is
  `internal` to a different test assembly. Restate the same two-part predicate over `PlanDefinition`.)

### 1. The stub — `tests/Guardrails.Core.Tests/ModelTiering/TierClassificationAudit.cs`

Namespace `Guardrails.Core.Tests.ModelTiering`. A `public static class TierClassificationAudit` with
exactly these three members, each body `throw new NotImplementedException();`, plus the finding types:

```csharp
public static bool IsTieringConfigured(PlanDefinition plan);
public static IReadOnlyList<TierClassificationFinding> Audit(PlanDefinition plan);
public static IReadOnlyList<string> ClassifiableSubjects(PlanDefinition plan);

public enum TierClassificationSubject { PromptTask, PromptJudge }

public sealed record TierClassificationFinding(
    string SubjectId,
    TierClassificationSubject Kind,
    string? ResolvedTier,
    TierOrigin Origin,
    string Detail);
```

`SubjectId` is the task's `Id` for a prompt task and `<taskId>/<guardrailName>` for a judge guardrail
(`<plan>/guardrails/<name>` and `<waveDir>/<name>` for a plan-root or wave-root judge). Give each member a
doc comment stating what it will mean — the stub is read by the next task as its contract. Do not write any
logic in it, and do not pre-empt the implementation's structure beyond these signatures.

### 2. The fixture pair — `TestData/tier-tags/configured/` and `TestData/tier-tags/untagged/`

Two plan folders, **byte-identical except for one key**. Both are FLAT plans whose four tasks form a single
linear chain `01 → 02 → 03 → 04` (one leaf, no fan-in — so no plan-root `guardrails/` folder is required
and the terminal-gate diagnostic stays silent).

`guardrails.json`, in both:

- one `promptRunners` block **named `claude`**, with `"default": "claude"`;
- that block carries a `routing` block whose `tiers` are `["easy", "medium", "hard"]` — this is what makes
  tiering **configured**;
- a top-level `"tiering": { "defaultTier": "medium" }`;
- no `costly` key anywhere (an explicit `true` would make the block a non-candidate and change what the
  fixture is about).

The four tasks, each with a real one-path `writeScope`, a `# catches:`-opening `exit 0` guardrail, and a
one-line action:

| task folder | action | tier / pin | what it is FOR |
|---|---|---|---|
| `01-author-widget-tests` | `action.prompt.md` | `action.tier: "medium"` | the CLASSIFIED prompt task — and the one key that differs between the fixtures |
| `02-implement-widget` | `action.prompt.md` | `action.model: "claude-opus-5"`, no tier | discharged by a MODEL pin |
| `03-tune-widget-effort` | `action.prompt.md` | `action.effort: "xhigh"`, no tier | discharged by an EFFORT pin |
| `04-seed-widget-dir` | `action.ps1` | nothing | a SCRIPT task: no prompt, no model, never a subject |

`01-author-widget-tests` also carries a **surviving prompt-judge guardrail** —
`guardrails/02-widget-review.prompt.md`, with YAML frontmatter declaring `tier: hard` — so the clean
fixture has a classified judge as well as a classified task. Its sibling `guardrails/01-tests-build.ps1`
stays a script guardrail.

**`untagged/` differs from `configured/` in exactly one way: `tasks/01-author-widget-tests/task.json` has
no `action.tier`.** Nothing else — not a description, not a writeScope, not a comment. That is what makes
the pair a discriminator rather than a snapshot.

### 3. The tests — `TierClassificationAuditTests.cs`, class `TierClassificationAuditTests`

Namespace `Guardrails.Core.Tests.ModelTiering`. Load a fixture with `new PlanLoader().Load(...)` addressed
through `TestPaths.Fixture("tier-tags/configured")`, asserting the load produced no errors — the same
`Load`/`LoadFrom` helper pair `SeamProofProximityTests` uses. Author **exactly these eleven test methods,
by these exact names** — a guardrail on this task reads them out of the TRX by name:

**Group A — the audit's behaviour. These MUST FAIL against the throwing stub.**

1. `ConfiguredPlan_FullyTagged_ProducesNoFinding` — over `configured/`: `IsTieringConfigured` is true,
   `ClassifiableSubjects` is non-empty (assert non-vacuity **first**, exactly as the seam precedent does —
   an audit reporting nothing because it recognised nothing is green for the wrong reason), and `Audit` is
   empty.
2. `ConfiguredPlan_UntaggedPromptTask_IsAFindingThatNamesTheTask` — over `untagged/`: exactly one finding,
   `Kind` is `PromptTask`, `SubjectId` is `01-author-widget-tests`, and `Detail` names the remedy.
3. `PlanWideDefaultTier_DoesNotDischargeTheFinding_BecauseItIsResolvedAtLoad` — over `untagged/`, on that
   same finding: `ResolvedTier` is `"medium"` and `Origin` is `TierOrigin.PlanDefault`. **This is the
   sharpest assertion in the file.** The loader resolves `tiering.defaultTier` into every untagged task, so
   an audit that read the RESOLVED tier would report this plan as fully classified and find nothing,
   forever. Assert the resolved tier is present AND the task is still flagged.
4. `ScriptActionTask_IsNeverFlagged_ItRunsNoModel` — over `untagged/`: no finding names
   `04-seed-widget-dir`, and it is absent from `ClassifiableSubjects` too. A script action runs no model,
   so it is not a subject at all rather than a subject that passed.
5. `PinnedTask_IsNotFlagged_WhetherThePinIsModelRunnerOrEffort` — over `untagged/`: neither
   `02-implement-widget` (model pin) nor `03-tune-widget-effort` (effort pin) is flagged; then, on a
   mutated copy in which `01-author-widget-tests` gains `"runner": "claude"` in its action block, the
   finding disappears entirely. Three pins, one method.
6. `UntaggedJudge_IsAFindingOnlyWhenItHasNoClassifiedActorToFollow` — the subtlest rule in the wave, in
   three mutated copies. **Read SSOT §4.2 before writing it**: an absent judge `tier` does not mean
   undefined, it means *the judge's rung follows the actor it guards*. So:
   - on a copy of `untagged/` (task 01 is unclassified) with the judge's frontmatter `tier` removed:
     **two** findings — the task, and its judge, which has no classified actor to follow;
   - on a copy of `configured/` (task 01 carries `action.tier: "medium"`) with the same key removed:
     **no** finding at all. Flagging this case would fire on almost every configured plan, which is how a
     check gets muted;
   - on a copy of `configured/` with an extra plan-root `guardrails/01-final-review.prompt.md` carrying no
     frontmatter `tier`: **one** finding — it guards no task, so there is no actor to follow.
   On each finding assert `Kind` is `PromptJudge`, `ResolvedTier` is null and `Origin` is
   `TierOrigin.None` (`GuardrailDefinition.Tier` is bound from frontmatter and from nothing else — there
   is no plan-wide default standing behind a judge), and that `Detail` names the discharge as a
   frontmatter `tier`, not an `action.tier`.
7. `TheAuditNamesWhatItSaw_SoAnEmptyResultIsNotAVacuousOne` — over `configured/`: `ClassifiableSubjects`
   names the three prompt tasks and the one prompt judge, and nothing else.

**Group B — the graceful skip. Do NOT author these as a TDD red, and the census guardrail on this task
deliberately EXCLUDES them by name.** A silence assertion cannot be red before the feature exists: a legacy
folder produces no finding both before and after. They are asserted here alongside the positive cases, and
the positive ones carry the red. (Getting this backwards is what nearly destroyed the Invariant-7 test in
wave 1.)

8. `LegacyPlan_WithNoTierVocabularyAnywhere_ProducesNothingAtAll` — on a mutated copy of `untagged/` with
   the `routing` block, the `tiering` block, the remaining `action.tier`/pins and the judge's frontmatter
   `tier` all removed — a plan as it would have been generated before tiering shipped:
   `IsTieringConfigured` is false and `Audit` is **empty**.
9. `RemovingOnlyTheTieringMetadata_SilencesTheFinding_TheTagsAreUntouched` — on a mutated copy of
   `untagged/` with **only** the `routing` and `tiering` blocks removed and every tag left exactly as it
   is: `Audit` is empty. The configuration is the only variable, so this is the proof that the gate is the
   config and not the tags — and it is also the plan the validator already reports on its own (tags with
   no routing anywhere), which the review probe defers to rather than duplicating.

**Group C — the fixtures' own integrity. These never call the audit, so they PASS the moment you author
the fixtures correctly, and the census guardrail requires them to be observed PASSED.**

10. `TheTwoFixturesDifferOnlyInTheMissingTag` — the two fixtures hold the same set of files; exactly one
    file differs; it is `tasks/01-author-widget-tests/task.json`; and the difference is exactly the
    `action.tier` key (present as `"medium"` in `configured/`, absent in `untagged/`) with every other
    property equal. Model it on `TheTwoFixturesDifferOnlyInWhereTheProofSits`, and make the failure message
    say the pair has drifted and why that turns it back into a snapshot.
11. `BothFixturesLoadAndValidateClean_BecauseValidateCannotSeeThisDefect` — a `[Theory]` over both
    fixtures: `new PlanValidator(FakeExecutableProbe.All).Validate(plan)` returns **zero diagnostics**.
    This is the executable form of the ruling that no GR code is allocated: the defect is invisible to
    `validate` by design, which is precisely why the review pass is the only gate. If a diagnostic fires,
    **fix the FIXTURE, never this assertion** — and if you cannot, that is a `needsHuman`, because a
    fixture the validator objects to is not the plan this test is about.

Also carry the anchor-set hygiene habit forward where it applies: assert non-vacuity before every "no
findings" assertion in Group A and Group B, so no test in this file can be green because the audit saw
nothing at all.

### Do not do these

- **Do not add a validator check and do not allocate a diagnostic code.** The ruling is settled and is not
  yours to revisit: the harness does not block on a model-quality opinion. Everything you write lives under
  `tests/`.
- **Do not implement the audit.** Failing is intentional; not COMPILING is a mistake to fix. The stub
  exists so the test project builds and a non-zero `dotnet test` unambiguously means the tests ran and
  failed.
- **Do not make the two fixtures differ in anything but that one key**, however tempting a clarifying
  comment is.
- **Do not put the fixtures anywhere but under `TestData/tier-tags/`** — your writeScope covers that
  directory and nothing else under `TestData/`.
