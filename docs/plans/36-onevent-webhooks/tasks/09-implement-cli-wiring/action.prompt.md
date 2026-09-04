## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g.
  `09-implement-cli-wiring`), NOT the stableId. The harness REJECTS a fragment
  keyed by anything else (every attempt), so:
  `{ "09-implement-cli-wiring": { "someKey": "someValue" } }`.
- EXCEPTION — the CONTROL KEYS `needsHarnessWrite` and `needsHuman` are TOP-LEVEL
  SIBLINGS of your folder-name key, never nested inside it. They are instructions to
  the harness, not state, so the rule above does not cover them:
  `{ "09-implement-cli-wiring": { "someKey": "someValue" },
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

Make `WebhookDeliveryTests` pass: turn task 08's declared-but-inert `--on-event` options into a real,
correctly-scoped, correctly-torn-down webhook delivery path at the CLI composition root.

This is the task that makes the feature exist. `WebhookEventSink` is fully built and fully unit-tested by
tasks 06/07, and every one of those tests stays green over a composition root that never constructs it —
which is design §10's *"the row that matters most (#382)"* in one sentence. Until `RunCommand` builds the
sink, the feature is reachable only from xUnit and inert from the CLI.

**Do NOT edit the authored tests.** `tests/Guardrails.Integration.Tests/RunEvents/WebhookDeliveryTests.cs`
is outside your write scope; the harness rejects the edit and it consumes a retry. If you become
convinced a test is genuinely wrong, emit `{"needsHuman": {"question": "…", "kind": "blocked-work"}}`
rather than working around it.

Read design `docs/plans/36-onevent-webhooks.md` §3.3 (lifetime and teardown — the exact construction
point and the six-step dispose), §6.4 (the configuration surface), §6.5 (SSRF, redirects, TLS) and §10
rows 6 and 7 before you start.

### Find your way around by GREP, never by line number

`RunCommand.cs` is long and it moves. Locate every site below by searching for the symbol, not by
scrolling to a remembered offset:

| Grep for | What it marks |
|---|---|
| `autonomyOption` | where this command declares and adds its options, and where an unparseable value is rejected |
| `diagramSeed` | the local read immediately **before** the construction point |
| `OnTheFlyDiagramObserver? diagramObserver = null;` | the line that opens the `RunFinished` bracket — the sink is constructed **above** it |
| `BuildObserverChain(` | the method's declaration **and** its call sites |
| `liveObserver` | the Spectre `Live` region — see the console-write ban below |
| `logServer.DisposeAsync()` | the transport teardown the sink's dispose must run **before** |

### 1 — Options and env fallbacks (§6.4)

Task 08 declared `--on-event <url>` and `--on-event-detail` and then ignored them. Make them real:

- `--on-event <url>` — the endpoint. **Not repeatable**: passing it twice is a validation error naming
  the reason, so declare the option such that a second occurrence is **DETECTED**, not silently
  last-wins. Silent last-wins is how an operator sends their run to the wrong endpoint and never learns.
- `GUARDRAILS_ON_EVENT` — the endpoint, used **only when the flag is absent**, so a CI job sets it once.
  Single URL only.
- `GUARDRAILS_ON_EVENT_AUTH` — the verbatim `Authorization` header value (e.g. `Bearer abc123`).
  **Env only. Never a flag, never a file, never echoed, logged, journaled, or written anywhere.** The
  `GUARDRAILS_` prefix is load-bearing rather than cosmetic: `ProcessRunner.ApplyEnvironment` deletes
  every inherited `GUARDRAILS_*` variable that is not in a child's declared overlay (#442), so the secret
  is stripped from every action, guardrail script and AI-merge worker for free. Do not rename it.
- `--on-event-detail` — off by default (§6.3).

### 2 — Validation runs EARLY and exits before any run state is touched

**This is a placement requirement, not a code-quality one.** Validate beside the other option parsing —
the same posture as an unparseable `--autonomy` — and on a bad value return `ExitCodes.HarnessError` (1)
**before any run state is touched**. An invalid URL must never surface mid-run, and `WebhookEventSink.TryStart`
must therefore never throw.

Reject, each with a message naming the actual problem:
- **Scheme is not `http` or `https`.** Check with `Uri.TryCreate(…, UriKind.Absolute)` plus a scheme test
  — the same shape as `PlanValidator.IsAbsoluteHttpUrl`. Name the scheme you found.
- **`--on-event` given more than once.**
- **CR or LF anywhere in the `GUARDRAILS_ON_EVENT_AUTH` value** — header-injection defense.

And **warn** (one line, not an error) on plain `http` to a **non-loopback** host: the auth header and the
payload would cross the network in the clear, but a sidecar on a private network is a legitimate reason
to proceed. Loopback and RFC1918 addresses are **explicitly allowed** — blocking them would be a bug,
since an agent-side monitor on `127.0.0.1` is the primary use case (§6.5).

Anything you print about the URL prints it **redacted**: `<scheme>://<host>[:<port>]/…` — scheme, host,
port, and a fixed `/…` when there is any path or query. **Never the path. Never the query string** (§6.6).
The renderer for this lives in Core with the dispatcher (task 07); use it rather than writing a second one.

### 3 — Construct the sink at the exact point §3.3 pins, with `await using`

Grep for `diagramSeed`. On the line after it is read, and **before** the
`OnTheFlyDiagramObserver? diagramObserver = null;` bracket opens:

```csharp
await using var eventSink = WebhookEventSink.TryStart(   // null when no --on-event URL
    onEventUrl, onEventAuth, userAgent, io.Out.WriteLine, cancellationToken);
```

- `userAgent` is derived from **`GuardrailsVersion`**, which lives in `src/Guardrails.Cli/GuardrailsVersion.cs`.
  §4.3 names the trap: `Guardrails.Cli` references `Guardrails.Core` and not the reverse, so reading the
  executing assembly from Core would silently report `Guardrails.Core`'s own `1.0.0` instead of the tool's
  version. The CLI passes the finished `User-Agent` value in.
- Pass the **run's `CancellationToken`**. §3.3 step 4 is explicit that it is passed **only to select the
  teardown budget** — the drain itself never observes it, because a token that is already cancelled would
  otherwise skip the drain entirely, losing the terminal row on exactly the path that most needs it.
- `io.Out.WriteLine` is the `onNotice` sink. Everything it emits is **buffered by the dispatcher and
  flushed at teardown**, never printed mid-run: grep for `liveObserver` and read the comment three lines
  above its construction — *"any console write into an active Live region corrupts the table (#145 Bug 1)"*
  — and that region covers the entire DAG, which is exactly when a circuit would open.

**Why `await using` at that point and nowhere else.** It compiles to an implicit try/finally whose scope
encloses the `RunFinished` bracket, so the unwind order becomes: the `RunFinished` `finally` → the sink's
dispose → `logServer.DisposeAsync()`. Verify that ordering against the real brace structure in the file
rather than trusting this paragraph — it is the claim whose failure would repeat plan 35 §9.3, where
`LogServer` drained in-flight requests **three lines too late**, after the transport had already been torn
down, and the "best-effort" final delivery of `run-finished` failed **every single time** across ~10
measured variants. The recorded finding: *"A 'best-effort' mechanism that is 0% effective is not
best-effort; it is dead code."* The rule that came out of it is **signal wind-down first, drain second,
tear the transport down last** — and nothing between the construction point and the bracket may return
past the construction.

### 4 — Thread `onRow` / `includeDetail` through `BuildObserverChain` to every call site

Task 08 added both parameters to `BuildObserverChain` and ignored them. Now pass them into the
`RunEventStream` construction inside it, and give each call site the real values from the sink.

> Grep `RunCommand.cs` for `BuildObserverChain(` and update EVERY call site it returns. At authoring time
> that was **2**, in the `live` and non-`live` branches. If your grep returns a different number, **trust
> the grep**, cover what it found, and say so in your summary.

That grep also returns the method's own **declaration**, which is not a call site.

**`BuildObserverChain`'s two parameters take NO default values, and you must not add any.** A defaulted
parameter lets a production call site silently deliver nothing — a new branch, or a merge that drops an
argument, compiles clean and posts nothing, with no test anywhere going red. That is the plan-34 §3 swallow
hazard, and the whole point of leaving them undefaulted is that the compiler forces every call site to
state its answer.

**Contrast `RunEventStream`'s constructor parameters, which ARE defaulted on purpose** (`Action<EventDelivery>? onRow = null,
bool includeDetail = false`, task 03): "no webhook" is the correct answer for a run without `--on-event`
and for the ~20 existing test constructions, which then compile unchanged. Both halves are deliberate. Do
not harmonize them in either direction — and if you find yourself adding a default to
`BuildObserverChain` to make a call site compile, that compile error is the mechanism working.

### Constraints

- A failed delivery must **NEVER** affect the run's verdict, on any path — not a timeout, not a 401, not a
  full queue, not a drained-with-pending shutdown. The exit code is computed exactly as it is today; the
  dispatcher has no input to it.
- Webhooks are independent of the log server: `--no-log-server` must not touch `--on-event`. A headless CI
  run — the run that most needs to be observed — gets the full stream with no listener bound at all.
- `--dry-run` emits no events and therefore no deliveries. It exits before the DAG; that is correct and
  needs no special case.
- Do not follow redirects, do not add a TLS-validation escape hatch, and do not add an `--insecure` flag
  (§6.5). Those decisions belong to the dispatcher and are already made.

### Done when

`dotnet test tests/Guardrails.Integration.Tests --filter "Category=RunEvents&FullyQualifiedName~WebhookDeliveryTests"`
is green — all ten methods — and `RunCommand.cs` actually calls `WebhookEventSink.TryStart`.
