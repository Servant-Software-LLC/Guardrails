## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g.
  `05-implement-webhook-policy`), NOT the stableId. The harness REJECTS a fragment
  keyed by anything else (every attempt), so:
  `{ "05-implement-webhook-policy": { "someKey": "someValue" } }`.
- EXCEPTION — the CONTROL KEYS `needsHarnessWrite` and `needsHuman` are TOP-LEVEL
  SIBLINGS of your folder-name key, never nested inside it. They are instructions to
  the harness, not state, so the rule above does not cover them:
  `{ "05-implement-webhook-policy": { "someKey": "someValue" },
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

Fill real logic over the two stubs task 04 left in
`src/Guardrails.Core/Execution/WebhookEventSink.cs`: the retry classifier and the redacted-URL
renderer. `WebhookPolicyTests` is the specification — read it first.

**Scope boundary (harness-enforced):** Write only to `src/Guardrails.Core/Execution/WebhookEventSink.cs`. After this task completes the harness runs a
`git diff` check and rejects any edit outside that surface - production files, other test files, the
`.csproj`. An out-of-scope edit fails the task immediately and consumes a retry. If you hit a compile
error caused by a missing symbol in another file, do NOT edit that file - write
`{"needsHuman": "<what is missing>"}` to the state-out path and stop.

**Do NOT edit the tests authored upstream.** They are the specification. If one is genuinely wrong,
write `{"needsHuman": "<why>"}` to the state-out path and stop rather than changing it — an
out-of-scope edit to a test file fails the task immediately and consumes a retry.

Leave the §5.2 bounds constants task 04 landed exactly as they are, and do **not** start on the
channel, the pump, the `HttpClient` or `DisposeAsync` — those are tasks 06 and 07, and building them
here would collide with the tests task 06 is about to author against them.

### 1. `internal static bool IsRetryable(HttpStatusCode? status, Exception? error)`

A **pure function**: no clock, no field, no counter, no logging, no IO. It is unit-tested directly
precisely because nothing about the classification needs an HTTP server, and keeping it pure is what
makes that true. The design's truth table (§5.1) in full:

| Condition | Result |
|---|---|
| `2xx` | not retryable — the row was delivered; a success that re-entered the retry path would re-POST a row the receiver already accepted |
| `408 Request Timeout`, `429 Too Many Requests` | **retry** — the server explicitly said "later" |
| `5xx` (500–599, the whole band) | **retry** — server-side, transient by definition |
| Connection refused, DNS failure, TLS handshake failure, socket error | **retry** — an endpoint may still be starting up; that is the sidecar case |
| Per-attempt timeout (10 s) | **retry** — a slow receiver is not a wrong request |
| `3xx` | **no** — redirects are not followed (§6.5), so a retry reproduces the redirect |
| Any other `4xx` (400, 401, 403, 404, 405, 413, …) | **no** — the request is malformed, unauthorized or aimed at nothing; a byte-identical retry fails identically, and retrying a 401 against a misconfigured token is pure waste that hides the real problem |
| Any other exception from the client | **retry**, treated as transient — deliberately conservative; §5.2's bounds cap the cost of being wrong |

Two things the table decides that are easy to get backwards:

- **Classify by BAND, not by member.** `5xx` is 500–599 and `3xx` is 300–399. A `switch` over the
  half-dozen statuses you can name today silently mis-classifies the rest.
- **Do not special-case cancellation.** A per-attempt timeout arrives as `TaskCanceledException` /
  `OperationCanceledException`, and the table says retry. Telling that apart from a RUN cancellation
  is the **caller's** job — the pump owns both tokens and checks its own — and §3.3 is explicit that
  the drain never observes the run's token. A classifier that returned false for cancellation would
  make the per-attempt timeout non-retryable, which is the opposite of the table.

### 2. `public static string RedactUrl(Uri url)`

Renders `<scheme>://<host>[:<port>]/…` — scheme, host, port, and a fixed `/…` standing in for
everything else. **Never the path. Never the query. Never the userinfo.**

- Show the port only when it is not the scheme's default (`Uri.IsDefaultPort` is false), so
  `https://h/x` renders without `:443`.
- Append `/…` only when there is something to elide — a URL with no path and no query renders as
  `<scheme>://<host>` with no trailing marker, because a `/…` there would claim a path that does not
  exist.
- **Build the string from `Uri.Scheme`, `Uri.Host` and `Uri.Port`.** Do not produce it by trimming
  `url.ToString()`: that carries `user:password@` through, and it is one careless edit away from
  carrying the path as well. Composition cannot leak what it never reads.

**Why this is `public` and why it lives here rather than in the CLI** (§6.6, and the reason is
recorded so nobody "tidies" it later): both Core's runtime notices and the CLI's startup plain-`http`
warning need this renderer, `Guardrails.Cli` is a separate assembly, and Core's `InternalsVisibleTo`
covers only the two test projects. One row of the design's handoff table implemented across two
assemblies with the negative test stranded between them is `GR2069 HandoffRowSplitAcrossTasks` —
caught on the design's own adversarial pass and fixed by giving the renderer one owner.

**Why it exists at all:** for Slack incoming webhooks and webhook.site the URL **path is the
credential**. A full URL printed into a redirected `run.log` that an operator later pastes into a
GitHub issue is a live leak, caused by our own success message.

### Done when

Every test in `WebhookPolicyTests` passes, and nothing outside
`src/Guardrails.Core/Execution/WebhookEventSink.cs` changed.
