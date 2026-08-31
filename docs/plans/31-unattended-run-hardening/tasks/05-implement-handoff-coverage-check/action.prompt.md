## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key - the name of the directory this task.json lives in, NOT the stableId. The
  harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "05-implement-handoff-coverage-check": { "someKey": "someValue" } }`.
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

This task implements stage 5 of `docs/plans/31-unattended-run-hardening.md`. **READ section 4.1 THROUGH section 4.9
IN FULL** - section 4 is one argument, and an implementation written from a summary of it will get the
argument direction, the anchor test, or the per-row verdict wrong. Section 12 item 7 carries the two SSOT
rows, which are the normative statement of the rule. Where this prompt and the plan disagree, the plan
is authoritative and you should say so in your summary.

Read: **plan sections 4.1-4.9 and section 12 item 7** (the two `section 9.6` table rows), and **section 8's `#553` bullets**.

## Task

Make `tests/Guardrails.Core.Tests/Loading/HandoffScopeCoverageTests.cs` pass, without editing it.

### The three files

1. **`src/Guardrails.Core/Loading/HandoffScopeCoverage.cs`** (new) - the table locator, the extractor
   and the coverage check.
2. **`src/Guardrails.Core/Loading/PlanValidator.cs`** - add one line,
   `ValidateHandoffScopeCoverage(plan, diagnostics);`, to `Validate` (declared at `:51`), beside its two
   `writeScope` siblings at `:76-77` (`ValidateWriteScopes`, `ValidateStructuralOverScope`).
3. **`src/Guardrails.Core/Loading/DiagnosticCodes.cs`** - the two constants and the marker.

### Two gates that produce SILENCE, not noise (section 4.2)

1. **The plan document.** `BreakdownCommand.cs:112-115` builds `<dir>/foo.md` -> `<dir>/foo/`, so the
   sibling `.md` is the layout the CLI itself creates. It is **not universal** -
   `examples/parallel-hello/parallel-hello/` has no sibling `.md` at all. **v1 is sibling-only.** No
   sibling `.md` ⇒ **silent**. Do NOT add the fallback that mines task prompts for the plan path: it is
   declined, because a wrong plan document produces a wrong diagnostic, which is the worst outcome a
   path-coverage check can have.
2. **The table.** Anchored on **content, not section number**: a markdown table one of whose column
   headers normalises to `filestouched` (case- and space-insensitive). No such table ⇒ **silent**.

The second gate is what keeps this from being muted within a week. Most plans predate the convention;
a check that fires on every legacy plan gets turned off. Adopting the convention is opt-in **by writing
the column**.

### Extraction (section 4.3)

A `filesTouched` cell is prose with paths in it. Two narrowings, both load-bearing:

1. **Only backtick-delimited code spans are candidates.** "all seven section 3.4 producers" is deliberately
   not a path and must never be guessed at.
2. **Prose spans are silent.** A candidate with **no `/` and no file extension** is not a path -
   `required` and `writeScope` drop out; `RawManifests.cs` and `Journal/` survive. **No extension
   allow-list, no case heuristics, no C#-member-access special case.**

A trailing `:<line>` is stripped; a trailing `/` normalises to `/**`.

### Resolution - the whole-segment anchor (section 4.4)

> A candidate is **resolvable** when its **first path segment** equals a **whole path segment** of some
> `writeScope` entry in the plan. An unresolvable candidate is **dropped - SILENTLY**.

`tests` is a whole segment of `tests/Guardrails.Core.Tests/…`, so a stale `tests/…` path stays
checkable and fires. `Cli` is a whole segment of nothing (the real segment is `Guardrails.Cli`), so
`Cli/Commands/` is dropped and its row goes silent on its remaining resolvable candidates. `Loading` is
a whole segment of `src/Guardrails.Core/Loading/PlanLoader.cs`, so row 7 - the row #553 was written
about - stays checkable.

**This is NOT the "root-vocabulary gate" a previous revision removed.** That gate required the first
segment to be the FIRST segment of an entry, which muted `Loading/PlanLoader.cs` - it silenced the
motivating case. Whole-segment **anywhere** is the correct relaxation, and it is exactly the premise
the suffix arm needs to resolve a relative cell at all.

### Coverage - per ROW, by a SINGLE task (section 4.5)

`WriteScope.IsInScope(path, scope)` **globs the `scope` side and splits `path` literally**
(`WriteScope.cs:74-98`). That is ONE direction, and the two candidate shapes need it pointed opposite
ways. **Getting this backwards is the easiest way to ship a check that can never fire**, and pin 5a
exists to fail under the un-swapped form:

| candidate `C` | covered by scope entry `e` when |
|---|---|
| **concrete** (no `*`) | `WriteScope.IsInScope(C, [e])` ∨ `e == C` ∨ `e` ends with `/C` (a **segment-aligned suffix**, never a substring) |
| **glob** (has `*`) | `WriteScope.IsInScope(e, [C])` ∨ `WriteScope.IsInScope(e, ["**/" + C])` - **arguments swapped** |

The suffix arm and its `**/` analogue resolve a relative cell **without touching the repo tree**, which
is required because a handoff table names files the plan is about to **CREATE**.

**Build NO new primitive.** In particular there is no `WriteScope.Covers` (strict containment): it is
too strict and fires on plan 28 row 6 (`tests/**/OpenAiCompat*Tests.cs`), where seven tasks legitimately
write concrete files under one open glob - a correct row a containment rule calls broken. A guardrail
on this task greps `HandoffScopeCoverage.cs` for `WriteScope.IsInScope(` and **fails on local
segment-glob logic** (a `Split('/')` paired with `'*'` handling), because a private inline matcher that
happens to agree with every fixture passes all of them and silently owns a second copy of the glob
grammar.

**The verdict.** Let `A` be the row's resolvable candidates. The row is **clean** when some SINGLE task
`T` arm-matches **every** candidate in `A`. Otherwise:

| Code | Name | Condition |
|---|---|---|
| `GR2068` | `HandoffPathUnreachable` | some `C ∈ A` is matched by **no task at all** - provably broken |
| `GR2069` | `HandoffRowSplitAcrossTasks` | every `C ∈ A` is reachable, but **no single task** reaches them all - a CONFIRM |

**Mutually exclusive per row** (pin 3a). Both ship as **WARNING** in v1: `RunCommand.cs:198-207`
refuses to run a plan whose validation emits any error, so an ERROR would be a retroactive,
run-blocking gate on every plan carrying the convention - and row 3 proves a correct, shipped, fully
green plan can carry a stale cell.

### The two messages (section 4.7)

`GR2069` names **which task** covers each path, because that is the fact the author needs and the check
has already computed it; and it says **in its own words** that a deliberate split is expected to
trigger it - it is a CONFIRM, not a finding of fault. `GR2068` is blunt and deliberately does **NOT**
guess at a near-miss path: a suggested correction that is wrong is worse than none. Section 4.7 carries both
message forms; follow them.

### `DiagnosticCodes.cs`

Take **GR2068 and GR2069**, and advance the `CURRENT next-free code:` marker (currently at `:991`) to
**GR2070**. The three reserved-by-name gaps - **GR2060** (doc 19), **GR2061** (doc 18) and **GR2054**
(doc 17 section 13.2) - are **untouched**, and the `GR10xx` ladder is restated unchanged: the block's own note
says a doc stating only one ladder is half a fact. Spell the constants like their neighbours:
`public const string HandoffPathUnreachable = "GR2068";`.

### One thing section 7 asks for that your scope cannot reach

Section 7 says *"a pin in the implementation stage asserts each constant equals its literal"*. Your `writeScope`
carries **no `tests/**` path**, so you cannot write that pin, and task 04's tests are forbidden from
naming the constants at all (they would not have compiled). It is realized instead as this task's
guardrail `04-codes-and-marker.ps1`, a structural check over `DiagnosticCodes.cs` that also covers the
marker advance and the three reserved gaps - which no unit test would have. **Do not widen your scope to
add a test.** If you believe a unit test is genuinely required, that is a `needsHuman`.

### Note what your own change does to THIS plan folder

Once this task merges, `guardrails validate` on `docs/plans/31-unattended-run-hardening/` runs your new
check against plan 31's own section 13 handoff table - the first artifact it will ever see. That table is
written one-row-per-task with concrete paths precisely so it comes out clean. If it does not, read the
diagnostic carefully before assuming the table is wrong: on this plan, a fire is far more likely to be
your extractor or your anchor test than a bad row.

**Scope boundary (harness-enforced):** Write only to
`src/Guardrails.Core/Loading/HandoffScopeCoverage.cs`, `src/Guardrails.Core/Loading/PlanValidator.cs`
and `src/Guardrails.Core/Loading/DiagnosticCodes.cs`. After this task completes, the harness runs a
`git diff` check and rejects any edit outside these paths - including `WriteScope.cs`, any test file,
and the `.csproj`. An out-of-scope edit fails the task immediately and consumes a retry. Do NOT edit the
authored tests: make them pass by fixing the implementation, and if a pin is genuinely wrong or
incompatible, write `{"needsHuman": "<why>"}` to the state-out path and stop.
