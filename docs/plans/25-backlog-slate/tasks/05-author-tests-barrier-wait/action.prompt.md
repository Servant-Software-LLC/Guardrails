## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in, i.e.
  `{ "05-author-tests-barrier-wait": { "someKey": "someValue" } }`. The harness
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

Author **failing xUnit tests** for a new `BarrierWait` policy, plus the **minimal stubs** those tests
compile against.

**Write exactly two files:**

1. `tests/Guardrails.Core.Tests/Providers/BarrierWaitTests.cs` — the test file. The test class
   MUST be named **`BarrierWaitTests`** and every test MUST carry
   `[Trait("Category", "BacklogSlate")]`. Both are load-bearing: this task pair's guardrails filter
   on the class name, and the plan's baseline preflight excludes that trait.
2. `src/Guardrails.Core/Providers/BarrierWait.cs` — minimal skeleton stubs ONLY, whose members
   throw `NotImplementedException`, so the test project COMPILES.

Both directories (`tests/Guardrails.Core.Tests/Providers/`, and `BarrierWait.cs` itself) are new;
`src/Guardrails.Core/Providers/` already exists and holds unrelated types (`RegistryAnnotation`,
`SourceText`, …). Do not touch them.

**Scope boundary (harness-enforced):** Write only to
`tests/Guardrails.Core.Tests/Providers/BarrierWaitTests.cs` and
`src/Guardrails.Core/Providers/BarrierWait.cs` (the stub file). After this task completes, the
harness runs a `git diff` check and rejects any edit outside these paths — including changes to other
production files, neighbouring test files, `IRunObserver.cs`, `Scheduler.cs`, or the `.csproj`. An
out-of-scope edit fails the task immediately and consumes a retry. If you hit a compile error caused
by a missing symbol in another file, do NOT edit that file — write
`{"needsHuman": {"question": "<what is missing>", "kind": "blocked-work"}}` to the state-out path and
stop.

**The tests MUST COMPILE and FAIL against the stubs.** Failing is intentional; NOT compiling is a
mistake to fix. Do NOT implement the behaviour — write the tests and only the minimal throwing stubs.

### What the policy is for (issue #511) — read this before choosing the shape

A provider quota limit (HTTP 429 / "overloaded" / a usage-limit message) **inside a task** is already
ridden out: `TaskExecutor` pauses on `PromptFailureKind.Transient` with `TransientBackoff` and re-runs
the same attempt without burning the retry budget (issue #115). The **same signal at a wave barrier**
— where `Scheduler` invokes the JIT breakdown for the next wave — currently **ends the run**. Same
signal, same provider, two outcomes, and the barrier is where a long unattended run has the most
invested.

`BarrierWait` is the policy that closes that gap: **wait and re-probe rather than terminate.** Its
shape, settled by the plan of record:

```
nextProbe = min(resetInstant, now + probeInterval)      // probeInterval defaults to 30 minutes
```

The `min` is deliberate and is the interesting half: if the provider says it resets in three hours, we
still re-probe in 30 minutes, because the reset hint is **advisory** and a cheap early probe beats
trusting it. If the provider says it resets in five minutes, we probe then.

Two properties beyond the arithmetic, both named in the plan's "done when":

- **Bounded.** Cumulative waiting at one barrier has a ceiling. Once it is spent, the barrier settles
  (the run halts with a rate-limit reason) rather than waiting forever. Model this on
  `TransientBackoff.CanPauseAgain()` / `Elapsed` / `_budget` — read
  `src/Guardrails.Core/Execution/TransientBackoff.cs` first; it is the shipped sibling of this policy
  and the shape you should feel related to.
- **Surfaced.** The operator sees a PAUSE carrying its cause **and the next-probe time** — not a
  failure. The reason string is the payload that carries the time.

### The reuse constraint that is NOT negotiable

The observer hook this pause is surfaced through **already exists**:

```csharp
// src/Guardrails.Core/Execution/IRunObserver.cs:84
void PromptPaused(TaskNode task, string reason, TimeSpan backoff, int pauseCount) { }
```

It is raised today from `TaskExecutor.cs:218`. **#511 REUSES it.** Do NOT design toward a new observer
method, and do not write one: `IRunObserver.cs` and the three observer implementations are outside
this task's write scope, another task cluster in this plan is editing those same files, and a second
method would collide with it for no gain. Everything the operator needs travels in the existing
`reason` string — which is exactly why one of the tests below pins that the reason NAMES the
next-probe time. Design the reason as a first-class output of the policy, not an afterthought the
caller improvises.

### Testability: inject the clock and the wait, do not sleep

The policy's whole subject is time, and the default probe interval is **30 minutes**. A test that
waits for real is not a test. Give the type an injected "now" (a `DateTimeOffset` parameter or a
`Func<DateTimeOffset>`) and an injected delay function — `TransientBackoff` already does exactly this
(`Func<TimeSpan, CancellationToken, Task> delay`, defaulting to `Task.Delay`), and following it keeps
the two policies readable side by side. Every test below must run in milliseconds.

### The behaviours to encode, each bound to a PINNED test method name

Author exactly these test methods, named verbatim — the red census greps for these names:

| Test method name | Behaviour |
|---|---|
| `NextProbe_TakesTheResetInstant_WhenItIsSoonerThanTheProbeInterval` | Reset instant 5 minutes out, probe interval 30 minutes: `nextProbe` is the **reset instant**. |
| `NextProbe_TakesNowPlusProbeInterval_WhenTheResetInstantIsFurtherOut` | Reset instant 3 HOURS out, probe interval 30 minutes: `nextProbe` is **now + 30 minutes**, not the reset instant. This is the `min`, and it is the clause a "just honour the provider's hint" implementation gets wrong. |
| `NextProbe_DefaultsToThirtyMinutesOut_WhenNoResetInstantIsKnown` | No reset instant (null) and no explicit interval: `nextProbe` is **now + 30 minutes**. Pins the default THROUGH the computation — do not assert a bare constant. |
| `NextProbe_IsNeverInThePast_WhenTheResetInstantHasAlreadyPassed` | A reset instant already behind `now` must not yield a negative wait: the result is `now` (probe immediately), never a past instant and never a negative `TimeSpan`. |
| `WaitAsync_RequestsExactlyTheComputedDelay_ThroughTheInjectedClock` | The delay actually requested of the injected wait equals `nextProbe - now`. An implementation whose arithmetic is right but which then sleeps a hard-coded interval fails this. |
| `WaitAsync_IsClampedToTheRemainingCeiling_AndNeverOvershootsIt` | With less ceiling left than the computed interval, the wait is clamped to what remains — the barrier never overshoots its own bound. |
| `CanWaitAgain_IsFalse_OnceTheCeilingIsSpent` | After cumulative waiting reaches the ceiling, further waiting is refused, so the caller can settle the barrier instead of looping. Construct the policy with a small explicit ceiling; do not depend on the default. |
| `Reason_NamesTheNextProbeTime_SoTheOperatorSeesWhenTheRunResumes` | The operator-facing reason string carries **the next-probe time** as well as the provider cause. Assert the rendered time is present in the string, derived from the computed `nextProbe` — not that the string is merely non-empty. |

Every one of these must FAIL against the stubs. A test that passes against a `NotImplementedException`
stub never invoked the subject, so it certifies nothing — and the red census reads the runner's own
TRX per method, so a hollow body cannot hide behind a genuinely-failing sibling.

### The stub file

`BarrierWait.cs` needs only enough shape for the tests to compile — e.g. a class exposing the
next-probe computation, the wait, the ceiling predicate, the elapsed/probe-count accumulators and the
reason string, with bodies that are `throw new NotImplementedException();`. **You are authoring the
contract here**, so choose the shape you think the implementation and the `Scheduler` call site want —
but keep the stub minimal and do NOT implement it.

Two shape constraints that are not yours to choose, because a later task depends on them:

- The type must be named **`BarrierWait`** and live in `src/Guardrails.Core/Providers/BarrierWait.cs`.
  Task 06 wires it into `Scheduler`, and that wiring is checked by name.
- The ceiling and the probe interval must both be **constructor-settable**, so a test can pin
  behaviour without waiting 30 minutes and the call site can be configured later. A hard-coded
  `TimeSpan.FromMinutes(30)` with no way to override it makes half these tests unwritable.

### Read before you write

- `src/Guardrails.Core/Execution/TransientBackoff.cs` — the shipped in-task sibling. Same problem,
  different bound; reuse its vocabulary (`CanPauseAgain`/`Elapsed`/injected delay) where it fits so a
  reader can see they are two members of one family.
- `src/Guardrails.Core/Execution/IRunObserver.cs` around line 84 — the `PromptPaused` contract the
  reason string is written for.
- `tests/Guardrails.Core.Tests/TransientBackoffTests.cs` — the existing test style for this exact kind
  of policy (4 tests, no real sleeps). Match it.
