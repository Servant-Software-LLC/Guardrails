## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g. `24-author-tests-refresh-provenance`), NOT the
  stableId. The harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "24-author-tests-refresh-provenance": { "someKey": "someValue" } }`.
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

Author failing tests AND the stubs for the refresh provenance record and its one reader — design 39
§1c ("How a refresh is recorded") and §5. Read that subsection first: it records the decision these
tests pin.

**Test file:** `tests/Guardrails.Core.Tests/WaveDelivery/RefreshProvenanceTests.cs`
**Test class:** `RefreshProvenanceTests`
**Stubs:** `src/Guardrails.Core/Journal/RefreshedRecord.cs` and
`src/Guardrails.Core/Journal/UnauthoredContentNote.cs`, plus a working property in `JournalModel.cs`
and a throwing method in `RunJournal.cs`.

Every test carries `[Trait("Category", "WaveDelivery")]`.

**The decision, so the tests encode it and not the rejected alternative.** A post-delivery refresh
admits content no task authored, exactly as a plan 40 supply does — but it is NOT a supply. It has no
caller to put in `by`, a `Supplied-By:` trailer on it would be a false statement, and a merge has no
honest `bytes`. So it gets its OWN record in its OWN top-level section, `refreshed[]`, beside
`supplied[]`: never a `kind` field on `SuppliedRecord`, and never an entry in `supplied[]`. What the
two share is ONE reader, `UnauthoredContentNote`, which reads BOTH sections to answer "what is in this
tree that no task authored?". Do not touch `SuppliedRecord.cs`.

**The contract** (camelCase on the wire, via `JournalJson.Options`):

| Member | Type | Meaning |
|---|---|---|
| `At` | `required DateTimeOffset` | when the refresh commit was made |
| `Commit` | `required string` | the refresh merge commit on the plan branch |
| `From` | `required string` | the user's branch that was merged in |
| `Upstream` | `required string` | the sha that was merged — the refresh commit's second parent |
| `DeliveredWave` | `required string` | the wave whose delivery triggered the refresh |
| `Paths` | `required IReadOnlyList<string>` | the paths the refresh changed, ordinal-sorted |

`run.json` carries `"refreshed": [ { "at", "commit", "from", "upstream", "deliveredWave", "paths" } ]`,
and the section is ABSENT — not `null`, not `[]` — on a run that never refreshed.

**Write these stubs, and make them COMPILE:**
- `RefreshedRecord` — a `sealed record` with the six `required` members above, each getter throwing
  `NotImplementedException` (an `init` accessor is fine).
- `UnauthoredContentNote` — a `public static class` with
  `string? HeadlineSuffix(JournalDocument document)` and
  `IReadOnlyList<string> DetailLines(JournalDocument document)`, both throwing.
- `JournalDocument.Refreshed` in `JournalModel.cs` — a WORKING nullable property,
  `[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public IReadOnlyList<RefreshedRecord>? Refreshed { get; init; }`,
  the exact shape of the shipped `Supplied` property beside it.
- `RunJournal.RecordRefreshed(RefreshedRecord record)` in `RunJournal.cs` — throwing.

**The note's text contract** (the SSOT and the wave-gate halt quote it): a refresh renders as
`refresh from '<from>' at <first 10 characters of upstream>`, a supply as
`supplied by <by> at <first 10 characters of commit>`. `HeadlineSuffix` returns `null` when neither
section has an entry, so a run with no unauthored content keeps a byte-identical halt headline.

**Pin these behaviours to these EXACT method names:**

- `RefreshedRecord_RoundTripsThroughTheJournalJson` — serialize AND deserialize through
  `JournalJson.Options`; all six fields are equal after the round trip; the JSON contains
  `"refreshed"`, `"from"`, `"upstream"` and `"deliveredWave"`. Rejects a write-side-only converter,
  and wire names that drift from the SSOT.
- `Journal_WithNoRefreshedSection_RoundTripsUnchanged` — a document that never set `Refreshed`
  serializes with NO `"refreshed"` key and reads back `null`. Rejects a `= []` initializer that adds
  `"refreshed": []` to every run.json.
- `RecordRefreshed_AppendsAndPersists` — a real `RunJournal` in a temp directory; record two entries;
  re-read `run.json` from disk: both present, in order, and `Supplied` still `null`. Rejects replacing
  instead of appending, never persisting, and writing into `supplied[]`.
- `Note_WithNoUnauthoredContent_AddsNothing` — with both sections absent, and again with both present
  but empty: the suffix is `null` and the detail is empty. Rejects an always-on prefix that changes
  every existing halt.
- `Note_NamesARefreshByBranchAndUpstream` — the suffix contains `refresh from '<from>'` and the first
  10 characters of `upstream`; the detail contains the full `upstream`, the full `commit`, and
  `deliveredWave`. Rejects naming `commit` where the merged branch's sha belongs, and omitting the
  branch.
- `Note_NamesASupplyByWhoAndCommit` — the suffix contains `supplied by <by>` and the first 10
  characters of `commit`. Rejects a reader that reads only `refreshed[]`.
- `Note_ListsEveryRecordOldestFirst_AcrossBothSections` — a supply at T1, a refresh at T2 and a supply
  at T3 appear in that order in BOTH outputs. Rejects concatenating the sections, newest-first order,
  and latest-only.

`Journal_WithNoRefreshedSection_RoundTripsUnchanged` is declared exempt from the red census: the
`Refreshed` property is a working container in the stub, so a correct test of it is green on arrival.
It must still exist.

The house precedent for this shape is plan 40's shipped
`tests/Guardrails.Core.Tests/Supply/SuppliedProvenanceTests.cs` — the record, the round trip, and
`RunJournal.RecordSupplied` against a temp directory.

**No process-wide state (#520).** Do not set environment variables, change the current directory, or
touch the console or the culture — pass values in. xUnit runs classes in parallel, and a mutation here
breaks a class that did nothing wrong.

The other six tests MUST COMPILE and FAIL. Do NOT implement the record, the reader or the journal write.

**Scope boundary (harness-enforced):** Write only to `tests/Guardrails.Core.Tests/WaveDelivery/RefreshProvenanceTests.cs`, `src/Guardrails.Core/Journal/RefreshedRecord.cs`, `src/Guardrails.Core/Journal/UnauthoredContentNote.cs`, `src/Guardrails.Core/Journal/JournalModel.cs`, and `src/Guardrails.Core/Journal/RunJournal.cs`. After this
task completes, the harness runs a `git diff` membership check and rejects any edit outside these paths. An
out-of-scope edit fails the task immediately and consumes a retry. If you hit a compile error caused by a
missing symbol in another file, do NOT edit that file — write `{"needsHuman": "<what is missing>"}` to the
state-out path and stop.

**The harness runs this task's guardrails itself when you finish.** Do not try to run the guardrail scripts yourself: the shell they need is not granted to you, and a call refused on two attempts can halt the task even after the work is done.
