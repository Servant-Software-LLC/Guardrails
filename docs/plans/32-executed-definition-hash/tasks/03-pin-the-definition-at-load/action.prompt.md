## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key - the name of the directory this task.json lives in, NOT the stableId. The
  harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "03-pin-the-definition-at-load": { "someKey": "someValue" } }`.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- Guardrails constrain the OUTCOME, never HOW you implement it. Never reshape working
  code - or reword a document away from its own conventions - to match a check's
  pattern.
- If you cannot proceed without a human decision, write
  {"needsHuman": {"question": "<question>", "kind": "blocked-work"}} to the
  state-out path and stop. If instead a guardrail reports something ABSENT that you
  can see is PRESENT, that guardrail is defective: use "kind": "defective-guardrail"
  and quote (a) the guardrail's exact claim and (b) the file:line that refutes it.
  If you cannot produce BOTH quotes it is not a defective guardrail - retry the work,
  or escalate as "blocked-work". Difficulty is never "defective-guardrail".

## Plan of record

This task implements stage 3 of `docs/plans/32-executed-definition-hash.md`. **Read sections 4.1, 5.1,
5.2, 5.5 and 6.3 in full**, plus section 11's "what an unattended run of this plan must not be allowed to
do". Where this prompt and the plan disagree, the plan is authoritative and you should say so in your
summary.

You are the first implementation stage. Everything downstream reads what you capture.

## Task

Add **two** load-time captures to `TaskNode`, and populate both **eagerly** at the loader's single
construction site.

```csharp
// src/Guardrails.Core/Model/TaskNode.cs - both captured by the loader from the bytes it just read.
public string? DefinitionHashAtLoad { get; init; }                                // FULL surface, aggregate. The journal records THIS.
public IReadOnlyDictionary<string, string>? DefinitionFilesAtLoad { get; init; }  // per file. The GATE diffs THIS.
```

### Why two, and why the map is not deferrable

Section 5.2 is explicit: a single aggregate string **cannot serve milestone C at all**. The gate (stage
13) has to name **which** definition files moved, and a per-file diff needs per-file load-time state that
one hash carries none of. **Stage 13's `writeScope` cannot reach `TaskNode.cs`** - so if the map is not
here, it is nowhere, and the implementer three stages downstream has only bad options.

### DEVIATION FROM THE PLAN'S WORDING, and it is deliberate: the map is captured UNFILTERED

Section 5.2 describes `DefinitionFilesAtLoad` as *"the FILTERED per-file map the gate diffs"*, filtered by
the editor-artifact ignore list (`.DS_Store`, `Thumbs.db`, `*.swp`, `*.orig`, `*.rej`).

**Capture it UNFILTERED.** The ignore predicate is `LivePlanEditWatch.IsEditorArtifact`, which is
`private static` until **stage 5** promotes it, and stage 5 is *downstream* of you (it needs your pin
before it can stamp it). So filtering here would force you to inline a **second copy** of the ignore
list - which is precisely what section 6.2 forbids, and section 15.2 names that exact pressure as the one
that "silently un-decides section 6.2, the sharpest call in this document."

The result is identical: the filter is a pure function of the file name, so filtering the map at capture
time and filtering both sides at compare time produce the same diff. Stage 13 applies the one shared
predicate to **both** sides before diffing. One predicate, one home, and no ordering problem.

### The construction mechanic, named here so it is not discovered at implementation time

`TaskDefinitionHash.Compute(task)` needs a **fully-built node** - it reads `task.Directory` and the
*resolved* `task.Action.Path` - so the pin cannot be set inside the object initializer. `LoadTask` builds
the node and immediately returns the pinned copy:

```csharp
var node = new TaskNode { /* ... as today ... */ };
return node with
{
    DefinitionHashAtLoad  = TaskDefinitionHash.Compute(node),
    DefinitionFilesAtLoad = /* per-file map over the SAME enumeration */,
};
```

There is exactly **one** `new TaskNode` in `src/`, at `PlanLoader.cs:1061` inside `LoadTask` (declared at
`:1011`). Find it by symbol; do not rely on the line number.

### The per-file map

Fold **`TaskDefinitionFiles.Enumerate(task)`** - the same enumeration `TaskDefinitionHash.Compute` uses,
so the two surfaces can never disagree about *what defines a task* (section 5.3's closing rule). Key each
entry by the enumeration's own **label** (`task.json`, `action:<relative path>`, the `guardrails/**` and
`preflights/**` entries); value is that one file's hash, computed with the **same `HashText` primitive**
`TaskDefinitionHash` and `LivePlanEditWatch` already use.

`TaskDefinitionFiles` is **`internal`**, namespace **`Guardrails.Core.Journal`** (not `Loading`) - same
assembly as `PlanLoader`, so a `using` is all it needs.

**`HashText` has NO per-file helper, and must not gain one.** It lives at
`src/Guardrails.Core/Hashing/HashText.cs` (namespace `Guardrails.Core.Hashing`, `internal`), and its
per-file surface is `AppendFile(builder, label, absolutePath)`. `LivePlanEditWatch.TryHashFile` builds a
single-file hash from it in two lines - append into a fresh `StringBuilder`, then SHA-256 the result -
and that is the construction to mirror, so the two per-file surfaces produce the **same value for the
same file**. Copying those two lines is fine; **adding a helper to `HashText.cs` is not** (section 11 -
it is outside your `writeScope`, and its file set and framing feed every recorded definition hash in
every plan).

**Never throw on an unreadable file.** The loader must stay total: an unreadable entry is skipped, exactly
as `HashText` and the watch already handle it.

### Nullable, NOT `required` - and the null case is already decided

`src/` contains exactly one `new TaskNode`; `tests/` contains **27, across 21 files**. `required` would
turn a two-file change into a repo-wide test edit and pull `tests/**` into a stage that section 11 forbids
from holding it.

> **A null pin records a null hash. There is NO fallback to disk, at any write site, ever.**

That is not a hole - it is the state SSOT section 7.2 already defines and already handles (*"recorded hash
absent ⇒ treated as 'unknown - assume unchanged' → match"*), the same path a pre-#274 journal entry takes.
In production it is unreachable, because the loader is the only constructor.

## The three things this stage must never do

1. **Never make either capture LAZY.** No `Lazy<>`, no `??=`, no expression-bodied property, no
   compute-on-first-access. A lazy capture reads disk *later* and silently restores the entire defect,
   and it passes every test that does not edit inside the exact window (section 11). Guardrail 02 is a
   grep for exactly this reason, and it is the reason the properties must be **bodiless auto-properties
   that cannot name a hash function at all**.
2. **Never write `?? TaskDefinitionHash.Compute(task)` anywhere.** Section 5.2 calls it *"the cheapest
   wrong implementation of this entire plan"*: it reads like defensive coding, passes every behavioural
   pin, and restores the defect for any node the loader did not build.
3. **Never touch `HashText` or `TaskDefinitionFiles`.** Changing the file set or the framing moves **every
   recorded definition hash in every plan** and turns the next resume of each into a drift halt (section
   11). Both files are outside your `writeScope` for exactly that reason. **Call them; do not change
   them.**

## What must still be true when you are done

`PlanLoader.QualifyWaveDependencies` clones both records (`PlanLoader.cs:949`, `task with { DependsOn =
qualified }`, and `:952`). A record `with`-expression copies every property it does not name, so both
captures ride through - and that clone rebinds only `DependsOn`, which lives *inside* `task.json` and is
therefore already inside the hash. **A clone that rebound `Directory` or `Action` would carry a pin
describing a different folder; do not introduce one** (section 5.2).

Guardrail 04 runs the four shipped definition-hash suites - `TaskDefinitionHashTests`,
`WaveDefinitionHashTests`, `RunJournalDefinitionHashTests`, `PlanDefinitionHashWaveTests`. They must pass
**untouched**: this plan changes *when* the hash is computed, never *what* it is computed over, and those
suites are where that claim is read.

**Scope boundary (harness-enforced):** Write only to `src/Guardrails.Core/Model/TaskNode.cs` and
`src/Guardrails.Core/Loading/PlanLoader.cs`. After this task completes, the harness runs a `git diff`
check and rejects any edit outside these paths - including `HashText.cs`, `TaskDefinitionFiles.cs`,
`LivePlanEditWatch.cs`, any test file, and the `.csproj`. An out-of-scope edit fails the task immediately
and consumes a retry. If you hit a compile error caused by a missing symbol in another file, do NOT edit
that file - write `{"needsHuman": "<what is missing>"}` to the state-out path and stop.
