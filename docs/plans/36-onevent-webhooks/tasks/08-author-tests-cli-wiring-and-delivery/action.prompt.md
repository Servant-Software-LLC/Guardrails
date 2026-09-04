## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g.
  `08-author-tests-cli-wiring-and-delivery`), NOT the stableId. The harness REJECTS a fragment
  keyed by anything else (every attempt), so:
  `{ "08-author-tests-cli-wiring-and-delivery": { "someKey": "someValue" } }`.
- EXCEPTION — the CONTROL KEYS `needsHarnessWrite` and `needsHuman` are TOP-LEVEL
  SIBLINGS of your folder-name key, never nested inside it. They are instructions to
  the harness, not state, so the rule above does not cover them:
  `{ "08-author-tests-cli-wiring-and-delivery": { "someKey": "someValue" },
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

Author the end-to-end proof that `--on-event` actually delivers — a real `guardrails run` against a real
loopback `HttpListener` — **and** that a bad `--on-event` configuration is rejected at startup, both
**written RED**, together with the two minimal CLI stubs that let them compile.

Create `tests/Guardrails.Integration.Tests/RunEvents/WebhookDeliveryTests.cs` and make the two stub edits
to `src/Guardrails.Cli/Commands/RunCommand.cs` described under **Deliverable 2** below.

**Scope boundary (harness-enforced):** Write only to
`tests/Guardrails.Integration.Tests/RunEvents/WebhookDeliveryTests.cs` and
`src/Guardrails.Cli/Commands/RunCommand.cs` (the stub file). After this task completes, the harness runs
a `git diff` check and rejects any edit outside these paths — including changes to other production
files, neighbouring test files, or the `.csproj`. An out-of-scope edit fails the task immediately and
consumes a retry. If you hit a compile error caused by a missing symbol in another file, do NOT edit
that file — write `{"needsHuman": "<what is missing>"}` to the state-out path and stop.

### Why this task exists, in one paragraph

Design §10 calls row 7 — the row this task and task 09 deliver — **"the row that matters most (#382)"**.
Every part of the webhook feature can be built, unit-tested and wholly GREEN while `RunCommand` never
constructs the sink, in which case the feature is reachable only from xUnit and inert from the CLI. That
defect class has recurred at this exact seam in this repo more than once. Two of the tests below are
specifically the assertions plan 35 **measured as missing**: `LogServer`'s "best-effort" final delivery
of `run-finished` failed *every single time* across ~10 measured variants, and per plan 35's own finding
these are what would have caught it and did not exist.

### Read these first

- `tests/Guardrails.Integration.Tests/RunEvents/RunCommandObserverWiringTests.cs` — the established
  composition-root idiom. Its class comment states the exact defect class it exists to catch. **Follow
  its shape:** drive the REAL production entry point, never inject the thing under test.
- `tests/Guardrails.Integration.Tests/RunEvents/RunFinishedExitPathTests.cs` — the real-CLI idiom
  (`CommandFactory.BuildRootCommand(io)` + `root.Parse(args).InvokeAsync()`), the `ScriptPlanBuilder`
  fixture, and how it locates a run's `events.jsonl` from the journal's `runId`.
- `tests/Guardrails.Integration.Tests/ScriptPlanBuilder.cs` — builds a real script-based plan folder in
  a temp directory. Use it; do not hand-roll a plan.
- `tests/Guardrails.Integration.Tests/StringConsoleIo.cs` — `OutText` gives you everything the command
  wrote. You will need a local `InvokeAsync` helper that returns **both** the exit code and `OutText`;
  the one in `RunFinishedExitPathTests` discards the console text.
- Design `docs/plans/36-onevent-webhooks.md` §3.3 (lifetime and teardown), §4.3 (headers), §4.4 (the
  receiver contract), §6.3 (`detail` withheld by default), **§6.4 (the configuration surface — the
  not-repeatable rule and the env-only secret), §6.5 (SSRF, redirects, TLS — the scheme check, the
  CR/LF rule, the plain-`http` warning)**, §6.6 (how the URL is displayed).

### Deliverable 1 — the test class

Class name is pinned: **`WebhookDeliveryTests`**, in
`tests/Guardrails.Integration.Tests/RunEvents/WebhookDeliveryTests.cs`. Every test carries **both**
`[Trait("Category", "RunEvents")]` and `[Trait("Plan", "36-onevent")]`. The `Plan` trait exists only so
this plan's baseline preflights can exclude its intentional red — never filter on it yourself.

Every **delivery** test (behaviours 1–10) starts a real `HttpListener` bound to
`http://127.0.0.1:<free port>/`, runs a real plan through the real CLI with
`--no-ui --no-log-server --on-event <that url>`, and asserts on what the listener actually received.
Capture, per request: the full request body, every request header, and the arrival order. The three
**startup-validation** tests (11–13) bind nothing at all — the CLI must reject the configuration before
the DAG starts, so a listener would only hide a delivery that must never happen.

**Two fixture rules that are easy to get wrong and expensive to debug:**

- **A `ScriptPlanBuilder` plan folder is single-use.** Running the same folder twice is a *resume*: the
  journal already has every task succeeded, so the second run drains with no attempts and emits a
  different, much smaller set of rows. Tests 8 and 10 each run a plan **twice** — build a **fresh** plan
  folder for each run, never re-invoke the same one.
- **Fail for the right reason.** These tests must fail *because nothing was delivered*, not because a
  helper throws. A test that throws unconditionally is red today and stays red after task 09 wires the
  feature — and task 09 cannot edit this file, so it would dead-end that task at `needsHuman`. Every
  assertion must actually reach the listener's captured state.

#### The thirteen behaviours — these exact method names

Behaviours 1–10 are about **delivery**; 11–13 are the §6.4/§6.5 **startup-validation** surface, which
no guardrail and no test name anywhere in this plan covered until they were added. They are cheap —
no `HttpListener`, no delivery, an immediate exit — and they are the only thing standing between the
design's stated security constraints and a tree where every one of them can be absent and the whole
plan still reports green.

**1. `RowsArriveAtALoopbackReceiver`**
At least one POST arrives, and the received bodies' `kind` values include both `task-started` and
`task-settled`. Assert the specific kinds, not merely a nonzero count — a single stray request must not
satisfy this.

**2. `RunFinishedArrives`**
A received body with `"kind":"run-finished"` carrying an `exitCode`. **This is the assertion plan 35
measured as missing.** The terminal row is the single most valuable delivery in the feature, because it
is the one a CI wrapper branches on, and it is appended in the `finally` at the very end of the run —
exactly where a teardown ordering bug eats it.

**3. `RunFinishedArrivesWhenTheReceiverIsSlow`**
Same assertion as (2), with the receiver deliberately delaying each response enough that the pump is
still backed up when teardown begins. This is the half plan 35's defect would have survived: a drain that
runs after the transport is torn down looks fine against an instant receiver and fails against a real
one. Keep the per-request delay modest (a few hundred milliseconds) — §3.3 budgets **10 s** for the
backlog phase plus **10 s** for the guaranteed terminal attempt, and the test must sit comfortably inside
that, not race it.

**4. `DeliveredBodiesMatchEventsJsonlLineForLine`**
Read the run's `events.jsonl`. For every delivered body, its `(bracket, seq)` names a real line in that
file; and for every delivered row whose file line carries **no** `detail`, the delivered body string is
**byte-identical** to the file line (§3.1 makes those two strings the same object's one serialization).
**Assert the delivered set is non-empty AND that at least one row took the byte-identical branch, before
asserting anything universal over it** — a `foreach` over an empty collection passes, which is exactly the
hollow shape the red census below is built to catch.

**5. `HeadersAreExactlyTheContract`**
On at least one captured request (assert non-empty first, same reason as (4)), §4.3 exactly:
- `Content-Type` is `application/json; charset=utf-8`
- `User-Agent` is `guardrails/<version>` where the version is `GuardrailsVersion.Current` — assert the
  full expected string. §4.3 names the trap: reading the executing assembly from Core would silently
  report `Guardrails.Core`'s own `1.0.0`, so a test that only checks the `guardrails/` prefix would pass
  over the bug.
- `X-Guardrails-Delivery-Id` equals `<runId>:<bracket>:<seq>` **reassembled from that request's own body
  fields** — do not hard-code it.
- `X-Guardrails-Event-Kind` equals the body's `kind`.
- `X-Guardrails-Delivery-Attempt` is `1` for a delivery that succeeded first try.
- And the headers §4.3 **rejected** are absent: no `X-Guardrails-Signature`, and no separate
  `X-Guardrails-Run-Id` / `X-Guardrails-Seq` / `X-Guardrails-Bracket`.

**6. `DetailIsWithheldWithoutTheFlag`**
Run a plan whose guardrail FAILS, so a `guardrail-finished` row carries real free text. Without
`--on-event-detail`, assert positively: (a) such a row **arrived**, (b) its `detail` is exactly
`(detail withheld; pass --on-event-detail)`, and (c) the matching `events.jsonl` line still carries the
REAL text — the file is never affected either way (§6.3). Do **not** phrase this as "no delivered body
contains the secret": that is vacuously true when nothing was delivered at all, which is the state of the
tree you are writing against.

**7. `DetailIsPresentWithTheFlag`**
The same plan with `--on-event-detail`: the delivered row's `detail` equals the file's value. Keep the
guardrail's failure text short so the 2000-character cap and its `…[truncated]` suffix are not in play.

**8. `AFiveHundredCausesRetriesThenARecordedDropWithExitCodeUnchanged`**
The receiver always answers `500`. Assert all three:
- **Retries happened:** the same `X-Guardrails-Delivery-Id` arrived more than once with increasing
  `X-Guardrails-Delivery-Attempt` values. Assert this on the FIRST delivery id you see; do not wait for
  or assert on the circuit.
- **The drop was recorded:** the captured console output carries a `Webhook:` summary line reporting a
  nonzero dropped count. Also assert that line does **not** contain the URL's path segment — §6.6
  requires `<scheme>://<host>[:<port>]/…`, never the path or query, because that URL is frequently itself
  the credential.
- **The exit code is unchanged:** it equals what the identical plan returns with no `--on-event` at all.
  Prefer running the plan both ways in this test over hard-coding a constant.

Note the wall-clock cost and design for it: §5.2 gives each row 4 attempts with 1 s / 2 s / 4 s backoff,
and §5.3 opens the circuit only after 5 consecutive exhausted rows, so a single-task plan here takes tens
of seconds. Use the **smallest** plan that produces the rows you need, and do not add a per-test timeout
tighter than two minutes.

**9. `EnvVarSuppliesTheEndpointWhenTheFlagIsAbsent`**
The §6.4 **environment** path — the configuration a CI job sets once, and the one this feature exists for.
Run with **no `--on-event` flag at all**; instead set `GUARDRAILS_ON_EVENT` to the listener's URL and
`GUARDRAILS_ON_EVENT_AUTH` to a fixed test value (e.g. `Bearer test-token-36`). Assert:
- rows arrived — **non-emptiness first**, then that the received kinds include `run-finished`; and
- on a captured request, the `Authorization` header is that value **verbatim**.

The auth half belongs here rather than in `HeadersAreExactlyTheContract` because `GUARDRAILS_ON_EVENT_AUTH`
is **env-only** — §6.4 gives it no flag, ever — so this is the only fixture in the plan that can reach it.
That is two properties of *one* configuration path, not a second subject.

**Set and restore both variables around this test**, following the repo's existing idiom (see
`tests/Guardrails.Integration.Tests/Commands/TelemetryCommandTests.cs`: capture the prior value, set inside
a `try`, restore it in a `finally` — restore, never blanket-clear). The suite runs in **one process** and
xUnit runs test classes in parallel, so a leaked `GUARDRAILS_ON_EVENT` would silently point another
class's real CLI run at this test's listener — a cross-class failure that looks like flakiness and reads
like nothing at all. The `finally` is mandatory; if you judge that this class also needs a collection
attribute to keep that safe, add one (it is inside your write scope).

**10. `AReceiverThatNeverBindsLeavesExitCodeUntouched`**
Point `--on-event` at a `127.0.0.1` port with nothing listening (reserve a free port and close it without
listening). Assert the run's exit code equals the exit code of the identical plan run without
`--on-event`. Same wall-clock note as (8). **This one is a declared exemption from the red bar** — see
below.

---

**Startup validation (design §6.4 and §6.5).** These three are cheap: no `HttpListener`, no port, no
delivery, and no waiting on a retry schedule — the CLI must reject the configuration and exit before
the DAG starts. Use the smallest green `ScriptPlanBuilder` plan that validates, a **fresh** folder per
test, and `--no-ui --no-log-server` as above.

**How all three assert "before any run state was touched", and that clause is the whole point.** §6.5
requires an invalid endpoint to be rejected at startup so it can never surface mid-run; task 09 places
the check beside the other option parsing, the same posture as an unparseable `--autonomy`. The
observable form of that is the journal: `RunJournal.LoadOrCreate` is the first thing the run path does
that writes run state, and it writes `<plan>/state/run.json` — `RunJournal.PathFor(planDir)`. A fresh
`ScriptPlanBuilder` folder has never run, so that file does not exist when the test begins. Each of the
three therefore asserts, after the invocation:

- the exit code is **`ExitCodes.HarnessError` (1)**;
- the console text names the **actual** problem (each test pins its own wording below);
- **`File.Exists(RunJournal.PathFor(planDir))` is `false`** — no journal, so no run state, so the
  rejection happened *before* the run and not during it.

On today's tree all three fail on **both** the exit code and the journal, because none of this
validation exists: the run proceeds to green and returns 0. That is exactly the omission the trio makes
visible. Without it, `new Uri("ftp://x")` parses fine, `TryStart` accepts it, the POST throws
`NotSupportedException`, `IsRetryable` classifies that as transient, the sink retries and records a
drop, and the exit code is untouched *by contract* (§5's preamble) — a wholly green run shipping a
scheme check nobody wrote.

**11. `ABadSchemeExitsOneBeforeTheRun`**
Run with `--on-event ftp://example.invalid/hook`. Assert the three clauses above, and that the message
**names the scheme it found** (`ftp`) alongside the option. A message that only says "invalid URL"
cannot be told apart from a bad plan folder, and §6.5 asks for one that names the scheme.

**12. `ARepeatedOnEventFlagIsRejected`**
Run with `--on-event` given **twice**, two different loopback URLs, neither with a listener. Assert the
three clauses above, and that the message names `--on-event` and says it may be given only **once**.
§6.4: *"Not repeatable. Passing it twice is a validation error naming the reason."* Silent last-wins is
how an operator sends their run to the wrong endpoint and never finds out.

**This test constrains how Deliverable 2 declares the option, and the reason was MEASURED rather than
assumed.** Declared as a single-arity `Option<string?>`, System.CommandLine 2.0.9 rejects the second
occurrence *itself*, with its own generic message and exit code 1 — measured against this repo's own
CLI: `guardrails graph <folder> --format mermaid --format dot` prints *"Option '--format' expects a
single argument but 2 were provided."* and exits **1**. That would make this test **pass against a tree
carrying no validation whatsoever** — a false green, and exactly the hollow red the census below exists
to catch. So Deliverable 2(a) declares `--on-event` **multi-valued**, accepting repetition without
erroring, and task 09 does the count check itself with a message naming the reason. That is not a
test-shaped distortion: §6.4 asks for a message *naming the reason*, and System.CommandLine's arity
error names an argument count instead.

**13. `ACrLfAuthValueIsRejected`**
Set `GUARDRAILS_ON_EVENT_AUTH` to a value containing CR/LF with a **distinctive** secret inside it —
`"Bearer sup3r-s3cret-t0k3n\r\nX-Injected: 1"` — and pass a perfectly valid
`--on-event http://127.0.0.1:<free port>/` with nothing listening on it (the run must never reach a
POST). A valid URL is deliberate: it puts the CR/LF check, not the URL check, on trial. Assert the three
clauses above, plus **both halves** of §6.4's secret rule:

- POSITIVE: the message names the variable `GUARDRAILS_ON_EVENT_AUTH` and says a CR or LF is not
  allowed in it;
- NEGATIVE: the message contains neither the whole value nor the distinctive token
  (`sup3r-s3cret-t0k3n`). §6.4 is unconditional — the value is *"never logged, echoed, journaled, or
  written to any file"* — and a validation error is precisely where a well-meaning implementation echoes
  the offending input back. Assert the positive half FIRST, or the negative one is satisfied by a
  console that printed nothing at all (#176).

The rule it enforces is a header-injection defense (§6.5): a bare CR/LF inside a header value is how an
attacker-controlled token appends headers of its own to the request. Set and restore the variable in a
`try`/`finally`, the same idiom as (9) — restore, never blanket-clear. xUnit does not parallelize
*within* a class, so this test and (9) cannot collide with each other; the **cross-class** hazard (9)
describes is unchanged, and two env-mutating tests in this file strengthen rather than weaken the case
for a collection attribute.

#### Twelve of these must be RED; the thirteenth is a declared exemption

`tasks/08-author-tests-cli-wiring-and-delivery/guardrails/02-tests-fail-on-current-code.ps1` is a
**per-test red census**: it reads the runner's own TRX and requires each of methods 1–9 and 11–13 to be
recorded `Failed`. A test named for a behaviour whose body never invokes the subject passes on this tree
and hides behind its genuinely-failing siblings, so a suite-level non-zero exit certifies nothing.

**`AReceiverThatNeverBindsLeavesExitCodeUntouched` is the exemption, and the reason is structural:** its
whole content is that the delivery mechanism does *not* affect the run, and a mechanism that does nothing
at all satisfies that exactly. It is green on the stub tree and green on a correct implementation. The
census therefore requires it to have **executed** rather than failed — so it must not be `[Fact(Skip=…)]`,
and it must not be quietly dropped. Write it.

### Deliverable 2 — the CLI stubs, and NOTHING more

These exist only so Deliverable 1 compiles and fails **on its assertions** rather than on a usage error
(#155). No behaviour. Do not construct `WebhookEventSink`; that is task 09's job, and this task's own
guardrails are written on the measured fact that `RunCommand.cs` does not reference it today.

**(a) Declare the two options — declared, parsed, and then ignored.**
Grep `src/Guardrails.Cli/Commands/RunCommand.cs` for `autonomyOption` to find where this command declares
and adds its options, and add two beside them in the same idiom:
- `--on-event <url>` — a **multi-valued** option: `Option<string[]>` with
  `Arity = ArgumentArity.OneOrMore` (the arity idiom this repo already uses — see
  `src/Guardrails.Cli/Commands/ResetCommand.cs`). Leave `AllowMultipleArgumentsPerToken` `false`, so
  `--on-event a b` does not silently collect two values from one occurrence. Give it a Description
  naming the endpoint and §8.3, and stating that it may be given only once.
- `--on-event-detail` — a `bool` flag. Give it a Description pointing at the withheld-by-default rule.

> **Multi-valued, and NOT `Option<string?>` — this is load-bearing and it was measured.** Behaviour
> (12) requires `--on-event` twice to be rejected with a message *naming the reason* (§6.4). A
> single-arity `Option<string?>` makes System.CommandLine 2.0.9 reject the duplicate on its own, with a
> generic arity message and exit code 1: measured against this repo's CLI,
> `guardrails graph <folder> --format mermaid --format dot` prints *"Option '--format' expects a single
> argument but 2 were provided."* and exits **1**. Declared that way, `ARepeatedOnEventFlagIsRejected`
> would go GREEN here — against a tree with no `--on-event` validation of any kind — and the red census
> would fail this task for a test that is doing nothing wrong. Multi-valued keeps the duplicate
> *parseable*, so the absence of validation is what the test sees, and task 09 supplies the count check
> and the message. `ArgumentArity.OneOrMore` rather than `ZeroOrMore` so a bare `--on-event` with no
> value stays a usage error instead of quietly meaning "no webhook".
>
> You still **ignore** the parsed value here. Reading it into a local (or discarding it) is fine; acting
> on it is task 09's.

Declare them, read them out of the parse result if that is the local idiom, and **go no further**: no
validation, no `GUARDRAILS_ON_EVENT` / `GUARDRAILS_ON_EVENT_AUTH` fallback, no sink. All of that is task
09. The only property this half must have is that `guardrails run … --on-event http://…` parses cleanly
instead of failing as an unknown option.

**(b) Add the two `BuildObserverChain` parameters — accepted and IGNORED.**
Grep the same file for `BuildObserverChain(`. Add to its declaration:

```csharp
Action<EventDelivery>? onRow,
bool includeDetail
```

`EventDelivery` is the `readonly record struct` task 03 added to `Guardrails.Core.Execution`.

**These two parameters take NO default values.** A defaulted parameter lets a production call site
silently deliver nothing — the plan-34 §3 swallow hazard — so every call site must be forced to say what
it passes. Contrast `RunEventStream`'s own `onRow` / `includeDetail` **constructor** parameters, which
*are* defaulted on purpose (task 03): "no webhook" is the correct answer for a run without `--on-event`
and for the existing test constructions, which then compile unchanged. Both halves are deliberate; do not
harmonize them.

In the **body**, accept both and ignore them: do **not** pass them to `RunEventStream`. Leave the chain
byte-for-byte the behaviour it has today. (If a nullability or unused-parameter analyzer objects, add a
discard — `_ = onRow;` — rather than removing the parameter or wiring it up.)

> Grep `RunCommand.cs` for `BuildObserverChain(` and update EVERY call site it returns. At authoring time
> that was **2**, in the `live` and non-`live` branches. If your grep returns a different number, **trust
> the grep**, cover what it found, and say so in your summary.

That grep also returns the method's own **declaration**, which is not a call site — count and update the
invocations. Each call site passes `null` and `false` explicitly. Task 09 replaces those two literals with
the real sink's callback and flag.

### Do not do these

- Do **not** construct, reference, or import `WebhookEventSink` in `RunCommand.cs`.
- Do **not** implement validation, env fallbacks, or `await using` lifetime — task 09 owns all of it.
- Do **not** edit `RunEventStream.cs`, `WebhookEventSink.cs`, or any other file. They are out of scope,
  and an out-of-scope edit consumes a retry.
- Do **not** weaken an assertion to make a test green. Twelve of these are supposed to be red right now.

### Done when

`tests/Guardrails.Integration.Tests` builds; methods 1–9 and 11–13 all **execute and fail** under
`--filter "Category=RunEvents&FullyQualifiedName~WebhookDeliveryTests"`; and
`AReceiverThatNeverBindsLeavesExitCodeUntouched` **executes** (green is expected there).
