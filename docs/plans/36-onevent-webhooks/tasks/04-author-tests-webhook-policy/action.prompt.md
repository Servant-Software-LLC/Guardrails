## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g.
  `04-author-tests-webhook-policy`), NOT the stableId. The harness REJECTS a fragment
  keyed by anything else (every attempt), so:
  `{ "04-author-tests-webhook-policy": { "someKey": "someValue" } }`.
- EXCEPTION — the CONTROL KEYS `needsHarnessWrite` and `needsHuman` are TOP-LEVEL
  SIBLINGS of your folder-name key, never nested inside it. They are instructions to
  the harness, not state, so the rule above does not cover them:
  `{ "04-author-tests-webhook-policy": { "someKey": "someValue" },
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

Author the tests that pin the webhook dispatcher's two PURE functions — the retry classifier
(design §5.1) and the redacted-URL renderer (design §6.6) — and land
`src/Guardrails.Core/Execution/WebhookEventSink.cs` as **throwing stubs** so those tests compile and
fail.

**Scope boundary (harness-enforced):** Write only to `tests/Guardrails.Core.Tests/Webhooks/WebhookPolicyTests.cs`, `src/Guardrails.Core/Execution/WebhookEventSink.cs`. After this task completes the harness runs a
`git diff` check and rejects any edit outside that surface - production files, other test files, the
`.csproj`. An out-of-scope edit fails the task immediately and consumes a retry. If you hit a compile
error caused by a missing symbol in another file, do NOT edit that file - write
`{"needsHuman": "<what is missing>"}` to the state-out path and stop.

`WebhookEventSink.cs` is a PRODUCTION file and it IS inside your scope — but only for the stub surface
described below. Writing real logic into it is task 05's job, not yours, and doing it here makes your
own tests green and fails this task's red census.

The file does not exist today: `WebhookEventSink`, `IsRetryable` and `EventDelivery` each appear
**0 times** in `src/` and `tests/`. You are creating it.

### The stub surface — and the one way to get it wrong

Create `src/Guardrails.Core/Execution/WebhookEventSink.cs` in namespace `Guardrails.Core.Execution`
containing ONLY:

1. **The two pure functions, as stubs:**

   ```csharp
   internal static bool IsRetryable(HttpStatusCode? status, Exception? error)
       => throw new NotImplementedException("task 05");

   public static string RedactUrl(Uri url)
       => throw new NotImplementedException("task 05");
   ```

   `IsRetryable` is `internal` — `Guardrails.Core.csproj` carries
   `<InternalsVisibleTo Include="Guardrails.Core.Tests" />` (measured: line 27), so your test class
   calls it directly. `RedactUrl` is **`public`** and that is deliberate: §6.6 requires ONE owner of
   the renderer because both Core's runtime notices and the CLI's startup plain-`http` warning need
   it, and `Guardrails.Cli` is a separate assembly that `InternalsVisibleTo` does **not** cover (it
   lists only the two test projects). Splitting the renderer across the two assemblies is the
   `GR2069 HandoffRowSplitAcrossTasks` shape §6.6 names.

2. **The design §5.2 bounds as named constants** (`internal`, so a later task's tests can read them
   rather than re-typing the numbers). Land all of them now, unasserted — task 06's tests are what
   pin them:

   | Constant | Value |
   |---|---|
   | max attempts per row | `4` (initial + 3 retries) |
   | backoff steps | `1 s`, `2 s`, `4 s` |
   | jitter band | `[0.5, 1.5)` |
   | per-attempt timeout | `10 s` |
   | hard per-row ceiling | `45 s` |
   | queue capacity | `1024` |
   | circuit threshold | `5` consecutive terminally-failed rows |
   | backlog drain budget | `10 s` normally, `0 s` when the run was cancelled |
   | terminal delivery timeout | `10 s` normally, `500 ms` when the run was cancelled |
   | response body read cap | `8 KB` |

   Name them for what they bound, not for their numbers. Give each a one-line comment citing the
   design section it comes from.

3. Nothing else. **No channel, no pump, no `HttpClient`, no `Emit`, no `DisposeAsync`.** Those are
   task 06/07 and adding them here puts you outside the deliverable.

**The one way to get the stub wrong, stated because it is the likely mistake:** do NOT write
`=> false` (or any other default) as the stub body. Two of your nine tests assert `IsRetryable`
returns **false**, so a `return false` stub makes them pass against a tree where the classifier does
not exist — the census below reads that as "not coupled to the code path" and fails this task.
`throw new NotImplementedException(...)` is what makes every one of the nine genuinely red.

### The test class

File `tests/Guardrails.Core.Tests/Webhooks/WebhookPolicyTests.cs`, namespace
`Guardrails.Core.Tests.Webhooks` (the folder-mirrors-namespace convention this project already
follows — see `tests/Guardrails.Core.Tests/RunEvents/RunEventVocabularyTests.cs`).

Class name is pinned: **`WebhookPolicyTests`**. **Every test method carries BOTH traits:**

```csharp
[Trait("Category", "RunEvents")]
[Trait("Plan", "36-onevent")]
```

Both are load-bearing. `Category=RunEvents` is what this task's guardrails and task 05's filter select
on; `Plan=36-onevent` exists ONLY so the plan's baseline preflights can exclude this plan's
intentional red. A test missing `Category` is invisible to the census and reads as "never authored".

### The behaviours — these exact method names

The nine names below are pinned. The census reads them out of the runner's TRX, so a renamed or
merged test reads as an unbound behaviour.

**Retry classification (design §5.1). Between them these six must cover EVERY row of that table.**

**1. `IsRetryableIsTrueFor408And429`**
`408 Request Timeout` and `429 Too Many Requests` are retryable — the server explicitly said "later".

**2. `IsRetryableIsTrueForEvery5xx`**
Every status in **500–599** is retryable: server-side, transient by definition. Assert the **band**,
not a handful of favourites — walk the `HttpStatusCode` members in that range (or the raw integers)
so a `== 500 || == 503` implementation cannot pass. **This test also carries table row 1 (`2xx` —
delivered):** assert `IsRetryable(HttpStatusCode.OK, null)` is **false**, as the band's lower
control. A success that classified as retryable would re-POST a row the receiver already accepted.

**3. `IsRetryableIsFalseFor3xx`**
`3xx` is a **hard** failure. Redirects are never followed (`AllowAutoRedirect = false`, §6.5), so a
retry reproduces the redirect forever and the payload plus its `Authorization` header never reaches
anywhere the operator named. Assert across the 3xx band, not one member.

**4. `IsRetryableIsFalseForOtherFourXx`**
Every other `4xx` — 400, 401, 403, 404, 405, 413 — is **not** retryable: a byte-identical retry of a
malformed, unauthorized or misaimed request fails identically and hides the real problem. Assert
that 408 and 429 are still `true` **inside this test** as the discriminating control, so an
implementation that blankets all of `4xx` as false cannot pass it.

**5. `IsRetryableIsTrueForTransportExceptions`**
Connection refused, DNS failure, TLS handshake failure, socket error — all retryable; an endpoint may
still be starting up, which is the sidecar case. Pass `status: null` with an exception (an
`HttpRequestException`, a `SocketException`, an `IOException`). **This test also carries the table's
last row** — "any other exception from the client → yes, treated as transient": include an exception
type the classifier cannot have a rule for (e.g. `InvalidOperationException`) and assert `true`. The
policy is deliberately conservative; §5.2's bounds are what cap the cost of being wrong.

**6. `IsRetryableIsTrueForPerAttemptTimeout`**
The 10 s per-attempt timeout is retryable — a slow receiver is not a wrong request. In .NET that
surfaces as a `TaskCanceledException` (`OperationCanceledException`), so assert
`IsRetryable(null, new TaskCanceledException())` is **true**. Add a comment in the test saying why
this is not a bug: telling an attempt timeout from a RUN cancellation is the **caller's** job (the
pump owns both tokens and checks its own), and §3.3 is explicit that the drain never observes the
run's token. A pure classifier that special-cased cancellation would make the per-attempt timeout
non-retryable, which is the opposite of the table.

**Redacted-URL rendering (design §6.6).** The renderer produces `<scheme>://<host>[:<port>]/…` —
scheme, host, port, and a fixed `/…` standing in for everything else. Pin these three, and pin the
port rule concretely: the port is shown when it is **not** the scheme's default (`Uri.IsDefaultPort`
is false), so `https://h/x` renders without `:443`.

**7. `RedactedUrlKeepsSchemeHostAndPort`**
The positive half — what survives redaction:
- `https://hooks.example.com/services/T00/B11/XyZ?token=abc` → `https://hooks.example.com/…`
- `http://127.0.0.1:9000/hook` → `http://127.0.0.1:9000/…` (loopback and private addresses are
  explicitly allowed, §6.5 — the whole point of the feature is an agent monitor on `127.0.0.1`)
- `https://example.com` (no path, no query) → `https://example.com` — there is nothing to elide, so
  no `/…` is appended.
- **And what is dropped along with the path:** `https://user:s3cr3t@example.com/hook` must render
  `https://example.com/…`. The userinfo is a credential and it never appears. This is the assertion
  that forces the renderer to be BUILT from `Uri.Scheme` / `Uri.Host` / `Uri.Port` rather than
  produced by trimming `url.ToString()`.

**8. `RedactedUrlNeverContainsThePath`**
The negative half. Build a URL whose path segments are recognisable, credential-shaped strings, and
assert none of them appears anywhere in the rendered output. **Include a POSITIVE CONTROL in the same
test** — assert the host DOES appear — so the test cannot pass by rendering an empty string. A
negative assertion with nothing proving the subject was produced at all is the failure shape #176
exists to stop.

**9. `RedactedUrlNeverContainsTheQuery`**
Same shape for the query string: assert neither the query key nor its value appears, with the host as
the positive control. Cover a URL that has a query and no path, and one that has both.

Why these two are security tests rather than cosmetics: for Slack incoming webhooks and
webhook.site, **the URL path IS the credential**. A full URL printed into a redirected `run.log` that
an operator later pastes into a GitHub issue is a live leak caused by our own success message.

### Done when

`Guardrails.Core.Tests` **compiles** and all nine tests **fail** against the stubs. Failing is the
deliverable; not compiling is a mistake to fix. Do NOT implement `IsRetryable` or `RedactUrl` — that
is task 05.
