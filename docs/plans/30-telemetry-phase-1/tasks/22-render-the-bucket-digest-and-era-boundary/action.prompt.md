## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g.
  `22-render-the-bucket-digest-and-era-boundary`), NOT the stableId. The harness REJECTS a fragment
  keyed by anything else (every attempt), so:
  `{ "22-render-the-bucket-digest-and-era-boundary": { "someKey": "someValue" } }`.
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

## Plan of record

This task implements the report half of `docs/plans/30-telemetry-phase-1.md`. **Read sections 2, 3.2 and
3.3 in full.** Section 2 is the survivorship finding the era boundary exists to contain: over 171 tasks
every routed stratum read 100% first-pass, because the fourteen failed attempts of twenty-three carried
no provenance and fell into `(no route recorded)`. Where this prompt and the plan disagree, the plan is
authoritative and you should say so in your summary.

**Section 3.1 of that plan is marked `STATUS: DONE` and shipped as commit `3129919`. It is not work.**

## Section 5 puts "any change to the report's honesty rules" OUT of scope. Read this before you edit.

The three things you are about to do — sourcing `BUCKET` from the row, folding the model digest into the
model fingerprint, and printing the era boundary — are **not** changes to those rules. They are **the
existing rules finally receiving their data.** The report has been saying, correctly and out loud, that
it has no bucket and no digest; you are supplying both, and the sentences that said so become sentences
that say what is now recorded.

So, concretely:

- **No legend sentence may be weakened or deleted.** `RenderLegend` (lines 579-599) prints five: the `N`
  rule, the FINGERPRINT gap, the `BUCKET` rule, the MED/P90-with-ABANDONED pairing, and the COST
  null-versus-zero note. **All five must still be there when you are done.** Re-word the FINGERPRINT and
  `BUCKET` sentences — that is required, they are now stale — but re-word them to explain the mixed
  corpus, never to drop the caveat.
- **The `(unbucketed)` sentinel must SURVIVE.** The corpus is append-only and never rewritten, so rows
  written before §3.2's bucket landed keep rendering `(unbucketed)` forever. That is honest, not a
  regression, and one of the authored tests
  (`AnUnbucketedLegacyRow_StillRendersUnbucketed`) is green today and must stay green.
- **Do not annotate the numbers.** "Mark the 100% as survivorship" was considered and rejected in section
  5 as treating the symptom. Filter, and state the date. Nothing else.
- **Do not rewrite history.** No backfill, no re-baseline, no row mutation, no purge. The pre-boundary
  rows stay exactly where they are; they are simply not counted into the stratified table.

## Task

One file, and only this one: `src/Guardrails.Cli/Commands/TelemetryCommand.cs`.

### 1. Source `BUCKET` from the row

`ToSamples` currently sets `FingerprintBucket = UnbucketedBucket` unconditionally (line 435). Source it
the way `Tier` is already sourced one line above (line 434): **prefer the task-grain row, fall back to
the first attempt row, fall back to `UnbucketedBucket`.** That fallback chain is not a style choice —
its comment at lines 431-433 records the reason: the task row is the task-grain fact, and the attempt
row is a fallback for a corpus whose task row is missing, *never a second opinion when it is present*.
The bucket has the same grain, so it takes the same chain.

`UnbucketedBucket` stays exactly where it is, keeps its value `(unbucketed)`, and keeps being what a
bucketless row renders.

### 2. Fold the digest into the model fingerprint

`Fingerprint(TelemetryRow)` (line 455) composes `kind/runner/model`. Extend it so **a row carrying a
`ModelDigest` fingerprints distinctly from one that does not**, in exactly this form:

- with a digest: `kind/runner/model@digest`
- without: `kind/runner/model` — **byte-identical to today**, so no existing corpus row's stratum moves
- with no route at all: `NoRouteRecorded`, unchanged

§15.5's rule is that two model fingerprints never pool, and §3.4 says why it is not hypothetical: the
64GB Mac Studio is a **tighter** box than the 128GB MacBook, so the same model name runs at a different
quantization on each and **must not be pooled as one sample**. The grouping key in
`TelemetryReport.Build` (line 77) is `(ModelFingerprint, Tier, FingerprintBucket)`, so making the
fingerprint carry the digest is the whole mechanism — there is no second place to change.

### 3. Filter and state the era boundary

**The boundary is `2026-08-31T00:00:00Z`, rendered as `2026-08-31`.** That is the first UTC midnight
after §3.1's provenance fix (#532, commit `3129919`, 2026-08-30 17:58 UTC) and the corpus-isolation fix
(#547, commit `6229643`, 2026-08-30 18:06 UTC) were both on master. Put it in a named constant in this
file, with a doc comment recording those two commits — a bare magic date in a filter is unreadable in
six months, and this one has a derivation worth keeping.

- **Exclude rows whose `StartedAt` is BEFORE that instant** from the stratified table. The cleanest place
  is before `ToSamples` in `RunReport`, so both the sample count and the row count the report prints are
  the post-boundary ones.
- **Print the boundary in the legend**, as a row labelled **`BOUNDARY`** carrying the literal
  `2026-08-31`, beside — not instead of — the five sentences already there. Say what it means: rows
  before it are excluded because a failed attempt then recorded no provenance, so every routed stratum
  read 100% first-pass by survivorship; the rows remain in the corpus.
- **Report how many rows were excluded**, the way `RenderLegend` already reports unreadable lines. An
  excluded row is a measured fact and hiding it would be the same species of silence this plan exists
  to remove.
- **Do not let the empty-after-filtering case lie.** `RunReport`'s zero-sample branch currently prints
  *"The corpus holds no attempt yet, so there is nothing to report."* If rows exist but every one of them
  is pre-boundary, that sentence is false. Distinguish the two cases: an empty corpus, versus a corpus
  whose every row predates the boundary — the second should name the boundary and the excluded count.

### 4. Update the class doc

Lines 39-49 declare *"Two honest gaps in the row→sample mapping"* — no digest, no bucket. Both are now
closed, so both sentences are stale. **Update them; do not delete the paragraph.** What replaces them is
narrower and still honest:

- the corpus now records a bucket, and a row written before §3.2 landed carries none and renders
  `(unbucketed)`;
- the corpus now records a digest **where the provider volunteers one**. A Claude row's digest is
  permanently null — the Claude CLI stream carries a model TAG and no fingerprint — and an
  `openai-compat` row carries one only where the engine emits `system_fingerprint`. **That is a provider
  fact to state, not a gap to apologize for.** A reader who does not know it will read null as a bug.

## Out of scope, stated so you do not drift into it

- **`Guardrails.Core` is not yours.** `TelemetryRow` declares every column you read here, landed by
  `04a-extend-the-corpus-row-shape`, which IS an ancestor of this task - you can rely on it being there.
  `TelemetryIngest` is what POPULATES those columns from the journal
  (`20-carry-phase1-facts-into-the-corpus-row`), and that task is **NOT** an ancestor of yours: the report
  chain runs parallel to the ETL chain on purpose, because every test you make pass writes its corpus rows
  directly rather than through the ETL. So do not assume a real ingested row carries a bucket yet, and do
  NOT `fix` the ETL if you notice it does not - that is task 20's deliverable, and editing it would fail
  this task's write-scope check. `TelemetryReport.Build`'s grouping needs no change — the
  fingerprint string is the key, and you are changing the string.
- **Do not edit the authored tests.** `tests/Guardrails.Integration.Tests/Commands/TelemetryReportPhase1Tests.cs`
  is outside this task's writeScope. Make them pass by fixing the rendering. If a test is genuinely wrong
  or incompatible with the plan, write `{"needsHuman": {"question": "<why>", "kind": "blocked-work"}}` to
  the state-out path rather than changing it.
- **`telemetry census` is not yours.** `24-implement-the-attribution-census` registers that verb in this
  same file, and it depends on this task precisely so the two edits are serialized.

**Scope boundary (harness-enforced):** Write only to
`src/Guardrails.Cli/Commands/TelemetryCommand.cs`. After this task completes, the harness runs a
`git diff` check and rejects any edit outside that path — including the authored test file, other
production files, or the `.csproj`. An out-of-scope edit fails the task immediately and consumes a retry.
