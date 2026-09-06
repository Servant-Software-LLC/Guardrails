# Design 38: Guardrail scan soundness — the substrate that certifies, and the scan copy it certifies from

> Status: DESIGN OF RECORD — approved by the lead 2026-09-06 (the §3 fork was put to the lead and
> answered), not yet implemented.
>
> **Closes:** #608 (a PowerShell guardrail that aborts exits 0 and certifies GREEN), #561 (comments
> stripped before literals, so a `/*` in a literal blanks the file), #449 (GR2037 has no entry for a
> comment-blind forbidden-string grep), #428 (a content grep spanning multiple source-line literals
> silently false-passes).
>
> **Root cause, one sentence:** the doctrine tells a guardrail author to preprocess source before
> matching, is wrong or silent about *how*, and the substrate underneath cannot tell a scan that ran
> from a scan that aborted.
>
> **Allocates no new GR code.** All four land as data in the existing GR2037 registry plus one runtime
> change. GR2073 stays next-free.

Everything below is measured against `master` today (`a3f3e977`). Every count in §0 was re-measured for
this document; three of them contradict the issues they come from, including one of my own.

---

## 0. Corrections to the four filings

Read these first. Two change the plan's shape and one invalidates the fix I proposed when the fork was
put to the lead.

**0.1 — #608's "391 committed guardrails exposed" was the wrong population. The real number is 864 of
900, and the corpus has drifted from doctrine completely.**

| | count |
|---|---|
| committed `.ps1` guardrails under `docs/plans/**/guardrails/` | **900** |
| explicitly set `$ErrorActionPreference = 'Continue'` | 358 |
| set nothing → pwsh defaults to `Continue` | 506 |
| set `'Stop'` — the form the SSOT's own worked examples use | **37** |
| `.sh` guardrails | 0 |
| `.py` guardrails | 0 |

`examples/` is 12/12 `'Stop'`. **The golden examples follow the doctrine and the emitted corpus never
did** — which is the more useful finding than any single file's bug, because it says the drift is in the
generator, not in one author's hands.

**0.2 — The signal proposed at the fork does not exist on pwsh 7. This is the correction that matters.**

The fork was answered *"harness reads stderr on exit 0"*, and the option described keying on a PowerShell
error-record trailer, `+ FullyQualifiedErrorId :`. **Measured: pwsh 7 defaults to `ConciseView`, which
emits no such trailer** — that is `NormalView`, i.e. Windows PowerShell 5.1. Real captured stderr from
the reproducer:

```
InvalidOperation: <path>\abort.ps1:4
Line |
   4 |      $problems.Add("this throws: the list does not exist yet")
     |      ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~
     | You cannot call a method on a null-valued expression.
```

A detector keyed on the trailer would have matched **nothing**, on the primary interpreter, while reading
correctly in review — the exact defect class this plan exists to close. §3 keeps the decision the fork
settled (*exit 0 alone is not a verdict; the harness closes this, not the guardrail author*) and replaces
the signal with a structural one that is measured rather than assumed.

**0.3 — #561's "28 of 30" holds; today's count is 29 of 31.** Of the 31 committed guardrails that do both
a comment-strip and a literal-neutralization, **29 do them in the defective order**. The two correct ones
are the fixes already applied by hand.

**0.4 — #561's premise that the idiom comes from doctrine is half right, and the half that is wrong
changes the fix.** The `blankKeepingNewlines`/`neutralizeBraces` spelling appears **nowhere** in
`.claude/skills/**`. There is no SSOT for the scan copy to correct. What the doctrine *does* say, in at
least six places, is *"strip comments first"* — for example `guardrail-catalogue.md:2390` and
`dotnet.md:1509`. **That instruction is the proximate cause of #561**, because it is the wrong order for
any file whose literals can forge a comment delimiter. `guardrail-catalogue.md:2587` gets within one
sentence of the hazard — *"a **string literal**, which no comment-stripper touches"* — and stops.

So #561 is not "fix a drifted copy of the SSOT." It is "the SSOT states the rule backwards, and the
idiom that implements it propagates by copying neighbouring plan folders."

**0.5 — The keystone, and the reason these four are one plan.** `plan-breakdown/SKILL.md:2078` already
states the entire failure, in one row of a table, written for a human doing an author-time smoke test:

> **stderr non-empty** | it THREW. With `$ErrorActionPreference = 'Continue'` that is non-fatal, so a
> broken regex silently skips a comment/string strip and changes the guardrail's meaning | **fix before
> reporting**

That sentence contains #608's mechanism *and* #561's consequence. `guardrails-review/SKILL.md:1568`
repeats the instruction — *"Read stderr, not just the exit code."* **Nothing in the harness does either.**
The doctrine has known this the whole time; the product never enforced it.

---

## 1. The thesis

A script guardrail is a claim about source text. Between the file on disk and the claim sit two things:

1. a **preprocessor** that builds a scan copy (strip comments, neutralize literals), and
2. a **substrate** that decides whether the script's verdict counts.

Every one of the four issues is a defect in one of those two, and every one of them fails in the
direction that looks fine:

- the preprocessor **corrupts** the scan copy (#561 — a `/*` inside a literal blanks ~190 lines),
- the preprocessor is **absent** and nothing notices (#449 — stated four times, gated zero times),
- the preprocessor is **asked the wrong question** (#428 — the rendered sentence is not the stored one),
- the substrate cannot tell a scan that **ran** from one that **aborted** (#608).

#608 sits underneath the other three: a guardrail that aborts inside its own preprocessing exits 0, and
the harness reports PASS. So a correct fix to #561 can be silently not running, and nothing says so.
**That is the dependency edge that sets the sequencing in §10.**

---

## 2. Three-state test

For each defect: what does a *correct* implementation do, what does a *wrong* one do, and **what wrong
implementation passes the gate we are about to build?** The last column is the one that has to be
answered before any of this is worth building, and §11 answers it again adversarially.

| # | correct | wrong (today) | wrong-but-passes, if we are careless |
|---|---|---|---|
| 608 | an aborted guardrail is not a pass | exit 0 ⇒ `Passed = true`, stdout and stderr unread | a detector keyed on a string pwsh 7 never emits — **§0.2, already happened once** |
| 561 | literals neutralized, *then* comments stripped | comments first; a literal's `/*` blanks to the next `*/` | a lint that matches the two known spellings and not the shape |
| 449 | a whole-file read that feeds a banned-literal match strips comments in between | no strip at all; `validate` says "OK: plan is valid" | a lint keyed on `Get-Content -Raw`, dodged by `Select-String`/`[IO.File]::ReadAllText` |
| 428 | match a fragment that lives on one source line | match the rendered sentence; wrapped `AppendLine`s never match | a catalogue entry nobody reads, with no probe |

---

## 3. #608 — the substrate

### 3.1 The measured fail-open

Reproducer, the shape from the filing (`$ErrorActionPreference = 'Continue'`, `exit 0` inside a `try`):

```powershell
$ErrorActionPreference = 'Continue'
try {
    Write-Output 'guardrail starting'
    $problems.Add("this throws: the list does not exist yet")
    Write-Output 'RED CENSUS RAN'
    exit 0
} finally {
    Write-Output 'finally ran'
}
```

Run exactly as the harness runs it (`InterpreterMap.cs:131` — `pwsh -NoProfile -ExecutionPolicy Bypass
-File <script>`):

- **exit code 0**
- stdout: `guardrail starting`, `finally ran` — **`RED CENSUS RAN` never printed**
- stderr: the error record above

The method-on-null is *statement*-terminating: it unwinds the `try`, skipping `exit 0` **and everything
else the guardrail was going to check**, runs `finally`, escapes the script uncaught — and pwsh `-File`
still exits 0. `ProcessResult.Succeeded => !TimedOut && ExitCode == 0` (`ProcessResult.cs:22`), and
`GuardrailRunner.ToGuardrailResult` (`GuardrailRunner.cs:384-388`) returns `Passed = true` **without
reading stdout or stderr at all**. The abort is written to the attempt log by
`AttemptArtifacts.WriteGuardrailLogs` and never consulted for the verdict.

### 3.2 The fix: a harness-owned shim, not a text detector

`InterpreterMap`'s `.ps1` template invokes a shim the harness owns, which invokes the real guardrail
inside a `catch`. An error that escapes the guardrail can then never reach the harness as exit 0.

```powershell
$script = $args[0]
$rest   = if ($args.Count -gt 1) { $args[1..($args.Count - 1)] } else { @() }
try {
    & $script @rest
} catch {
    [Console]::Error.WriteLine("GUARDRAILS-ABORT: the guardrail did not run to completion -- " + $_.Exception.Message)
    exit 97
}
if ($null -eq $LASTEXITCODE) { exit 0 }
exit $LASTEXITCODE
```

This is **structural, not heuristic**. It does not read message text, so it cannot rot when an error view,
a locale, or a PowerShell version changes — which is precisely how the signal in §0.2 was already wrong
before it was written.

### 3.3 Measured behaviour — every case, against the unshimmed baseline

| case | `-File` today | shimmed | |
|---|---|---|---|
| aborts mid-script (the defect) | **0** | **97** | fixed |
| `exit 0` after real work | 0 | 0 | unchanged |
| `exit 1` — a real finding | 1 | 1 | unchanged |
| writes to stderr, exits 0 (git/dotnet noise) | 0 | 0 | unchanged — **no false-RED** |
| completes with no explicit `exit` | 0 | 0 | unchanged |
| failing native command, then no `exit` | 0 | 3 | **the one divergence** — §3.4 |

`$PSScriptRoot` and `$MyInvocation.MyCommand.Path` resolve to the **guardrail's** own file, and arguments
round-trip. Both were verified, and both were broken in earlier drafts of this shim: a `param()` form with
`ValueFromRemainingArguments` silently ate `-a one` into a single value, and two forms that looked
obviously correct (`& $script` then `exit 0`, and dot-sourcing) turned **`exit 1` into exit 0** — a real
guardrail failure reported as a pass, which is the worst outcome in this document, introduced by the fix
for it. `exit N` inside an invoked script returns control to the caller in *both* `&` and `.` forms; only
`exit $LASTEXITCODE` propagates it. That is why §9's test list pins `exit 1` first.

### 3.4 The one divergence, and why it is acceptable — measured, not argued

A guardrail that runs a failing native command and then falls off the end without an explicit `exit`
would go from 0 to that command's code. Exposure in the committed corpus:

> **900 of 900** committed `.ps1` guardrails contain an `exit` statement and end on one. The divergent
> shape occurs **zero** times.

It is also directionally right — a failing command with no verdict after it is not evidence of anything —
and §7 adds a lint entry requiring a guardrail to end on an explicit `exit`, which keeps the shim's
assumption **enforced rather than assumed**. That is the difference between this and an assumption that
silently rots.

### 3.5 Exit 97 must not surface as a mystery

`ToGuardrailResult` builds its one-line `reason` from the first non-empty line of stdout, falling back to
stderr. For an aborted guardrail stdout is non-empty (`guardrail starting`), so the reason would read
`guardrail starting` — technically a failure, uselessly reported. **Exit 97 gets an explicit branch**:

- `Reason`: `the guardrail aborted before reaching a verdict — exit 0 would have been a false PASS`
- `Output`: stderr in full, so the `GUARDRAILS-ABORT:` line and the pwsh error record both reach the
  retry-feedback tail (#179) where the next attempt reads them.

97 is chosen to sit outside the range guardrails use for findings (0/1) and outside pwsh's own
`$LASTEXITCODE` conventions. If a guardrail ever legitimately exits 97 the shim's own code is
indistinguishable from it — an accepted, documented residual, and a lint could ban `exit 97` if it ever
matters.

### 3.6 Interpreter scope, stated honestly

**One more residual, found by the independent review:** `InterpreterMap.Resolve` short-circuits to the
operator's `_overrides` before it reaches `BuiltInTemplates`, so a plan that pins `.ps1` through
`guardrails.json`'s `"interpreters"` block **bypasses the shim entirely and silently**. That is an
escape hatch the operator chose, but nothing says so at the time — worth a line in the CLI's own docs,
and named here rather than discovered later.

Measured: **bash has the same fail-open** (a failing command mid-script, then `exit 0`, exits 0);
**python does not** (an uncaught exception exits 1). The corpus is **100% pwsh** — 0 `.sh`, 0 `.py` — so
the shim ships for `.ps1` only. The bash equivalent is `set -euo pipefail` and belongs in doctrine plus a
lint entry (§7), not in a second shim built for zero current consumers. **Named as a residual, not
silently omitted.**

---

## 4. #561 — the preprocessing order

### 4.1 The rule, corrected

The doctrine's *"strip comments first"* is wrong wherever the subject's string literals can spell a
comment delimiter — which for this repository is routine, because guardrails inspect files full of glob
patterns (`**/*.cs` supplies a `/*`, and a later one supplies a `*/`).

**The correct order is: neutralize string literals first, then strip comments.** Every substitution stays
1:1 so offsets remain valid and the length-preserving precondition still holds:

```powershell
# 1. literals first -- map the characters that can forge a delimiter, INCLUDING / and *
$scan = [regex]::Replace($raw,  '"""[\s\S]*?"""',   $neutralize)   # C# 11 raw strings
$scan = [regex]::Replace($scan, '@"(?:[^"]|"")*"',  $neutralize)   # verbatim strings
$scan = [regex]::Replace($scan, '"(\\.|[^"\\])*"',  $neutralize)   # ordinary strings
# 2. only then comments
$scan = [regex]::Replace($scan, '/\*[\s\S]*?\*/',   $blankKeepingNewlines)
$scan = [regex]::Replace($scan, '(?m)//[^\r\n]*',   $blankKeepingNewlines)
```

`$neutralize` must map `{`, `}`, **`/` and `*`** inside a literal to a filler character. The committed
form neutralizes braces only, which is why the delimiters survive to be paired.

### 4.1a Setting `'Stop'` is safe for the red checks -- but only if the doctrine pins one more preference

An inverse TDD-red check depends on `dotnet test` exiting NON-zero, so the obvious objection to
mandating `'Stop'` is that a failing native command would become a terminating error and abort the very
check whose success condition it is.

**Measured on pwsh 7.6.5: it does not.** `$PSNativeCommandUseErrorActionPreference` is **`False`**, so a
native command's non-zero exit -- and its stderr -- pass through untouched under `'Stop'`:

| script | result |
|---|---|
| `'Stop'` + `& cmd /c "... & exit 1"` | survives, `$LASTEXITCODE = 1`, script exits 0 |
| `'Stop'` + native command writing to **stderr** and exiting 1 | survives, `$LASTEXITCODE = 1` |

But that is a **preference, not a guarantee** -- it is a promoted experimental feature and a box that
flips it to `$true` would turn every red check into an abort. So the generator rule is **two lines, not
one**, and the second is load-bearing:

```powershell
$ErrorActionPreference = 'Stop'                    # an engine error must terminate, not fail open
$PSNativeCommandUseErrorActionPreference = $false  # ... but a non-zero `dotnet test` is DATA, not an error
```

The GR2037 entry `#608a` bans `Continue`/`SilentlyContinue`; it must **not** also demand the second
line, because a guardrail that runs no native command does not need it. Requiring it everywhere would
make a correct script fail a lint -- the false-RED half of the same family this plan is closing.

### 4.2 Where the correction lands

Not in a scan-copy SSOT — §0.4 established there isn't one. It lands in the **six-plus doctrine sites
that state the rule backwards**, each of which gains the ordering and the reason:

- `guardrail-catalogue.md:2390` — *"Strip comments before the scan"*
- `guardrail-catalogue.md:2424`, `:2587` (which already names the literal residual and stops short)
- `dotnet.md:1506-1512`
- `guardrails-review/SKILL.md:422`, `:2212`
- `plan-breakdown/SKILL.md:547`

Plus a worked trap in the catalogue: **a file whose literals contain both a `/*`-forming and a later
`*/`-forming glob**, which is the sample #561 notes was missing (`samples/03-pins-key-the-right-codes.valid.cs:47`
contains the opener and nothing closes it, so the valid half passed by luck).

---

## 5. #449 — the gate that was never built

The doctrine here is **not** missing. It is written four times, prominently, and rated BLOCKER
(`guardrail-catalogue.md:460`, `:3208`; `guardrails-review/SKILL.md:419-430`, `:2212`). It was ignored
anyway, and three `/guardrails-review` passes plus a fourth wave-scoped pass all missed the violation.

**The decisive negative control, from triage and still true:** `guardrails validate
docs/plans/salvage-advice-provisioning` reports **"OK: plan is valid"** across 77 diagnostic codes while
the offending guardrail sits committed and unmodified since `d9c006d3`.

That is the whole argument. More prose is the one remedy already proven not to work.

**But the lint is not expressible, and an independent review measured why. #449 gets no entry in this
plan.** The shape a `#449` entry must fire on — *a whole-file read reaching a banned-literal match with
no strip in between* — is **also the shape of the doctrine's own canonical union guardrail**:

```powershell
# examples/parallel-hello/parallel-hello/guardrails/01-whole-repo-greeting.ps1
$content = Get-Content -Raw -Path $file.FullName
if ($content -match '(?m)^<<<<<<<' -or $content -match '(?m)^>>>>>>>') { ... }
```

That form is **correct without a strip** — a conflict marker inside a comment is still a conflict marker,
and you want to find it. `BannedPatternRegistryTests.AnchoredConflictMarker_IsClean_NoGr2037` asserts
exactly that, and would go red. So an entry keyed on the shape rejects the right answer, and an entry
narrow enough to spare it (excluding line-anchored patterns, say) is heuristic in both directions.

**This is the same verdict §6 reaches for #428, on the same reasoning, and consistency is the point:**
a gate that certifies less than it appears to is the defect this plan exists to close, so it does not get
shipped here merely because the issue asks for one. #449 keeps its doctrine, loses its lint, and gains a
measured reason — which it did not have before.

The entry must key on the **shape** — a whole-file read reaching a banned-literal match with no strip in
between — not on a token. An entry keyed on `Get-Content -Raw` is dodged by `Select-String -Path`,
`[IO.File]::ReadAllText`, or `grep -q`, and shipping that weaker version would reproduce this issue's own
grievance one level down.

---

## 6. #428 — rendered is not stored

A guardrail asserting a sentence is present or absent matches how the text **reads**, not how it is
**stored**. `RetryPolicy.cs:138-146` composes its retry-feedback prose across wrapped `AppendLine` calls
split by a 6-line comment *and* an `if` boundary — roughly 400 characters, bisected mid-phrase. A check
written from the rendered feedback can never fire, and neither can a bounded dotall window.

Recorded instances: **four**, the most recent `62c59db3` (2026-09-02).

**Deliberately no lint entry.** A GR2037 entry is expressible — a `-match` literal containing a long
space-separated phrase — but it is heuristic in both directions, and this document is not the place to
ship a gate that certifies less than it appears to. The honest scope is the catalogue entry the issue
asks for, sibling to *grep-scope contamination* and *structural-vs-keyword*, plus the matching
`/guardrails-review` probe, plus the three-line rule:

1. prefer a distinctive fragment that sits on **one** source line;
2. when the whole phrase matters, normalize whitespace or use `[\s\S]`;
3. verify the fragment against the **real file**, never the rendered text.

**This is the one of the four with no mechanical gate, and it should stay that way until someone can show
a `mustMatch`/`mustNotMatch` pair that earns it.**

---

## 7. The registry entries

`banned-guardrail-patterns.json` is built for exactly this — *"grow it by adding a JSON object with two
fixtures, not harness C#"* — and it carries three entries today (`#73`, `#187a`, `#462`). **Three** are
added; #449's was designed and dropped (§5).
Each needs `badPattern`, `reason`, `goodPatternHint`, and both fixture arrays, because
`BannedPatternRegistryTests.EverySeedEntry_BadPatternMatchesAllMustMatch_AndNoMustNotMatch` will not let a
malformed entry ship.

| id | catches | notes |
|---|---|---|
| `#608a` | a `.ps1` guardrail setting `ErrorActionPreference` to `Continue`/`SilentlyContinue` | 358 corpus instances; doctrine and `examples/` both say `'Stop'` |
| `#608b` | a guardrail that does not end on an explicit `exit` | keeps §3.4's divergence unreachable — the shim's assumption, enforced |
| `#561` | a block-comment strip that precedes a literal-neutralization | shape, not the two known spellings |

Two constraints on authoring them, both load-bearing:

- **The validator strips whole-line comments before scanning** (`PlanValidator.cs:2325`, itself the #97
  lesson). Fixtures must not rely on comment text, and a `badPattern` cannot be defeated by a header
  comment describing the construct.
- **`Registry_IsExactlyTheCuratedSet_NotWhateverAccumulated`** pins the entry set, so adding entries is a
  deliberate, reviewed act. That test must be updated in the same change — it is a feature, not an
  obstacle.

**No new GR code.** All four are GR2037 data.

---

## 8. The corpus — what is migrated, and what is not

**Migrated: one file.** `docs/plans/35-event-vocabulary/tasks/04-author-tests-event-vocabulary/guardrails/02-tests-fail-on-stubs.ps1`
creates its `List[string]` at `:60` after using it at `:54` and `:57`. This is #608's live instance and
the one guardrail known to be armed.

**Not migrated: the other 863, and the 29 wrong-order scans.** They are artifacts of merged, green,
shipped runs. Rewriting them would be a 900-file diff that changes no outcome, and the argument for it —
*"they are the copy sources"* — is answered better by §7's lint, which stops the next copy at `validate`
rather than hoping the next author reads a warning.

Consequence, stated so nobody rediscovers it as a bug: **after this ships, `guardrails validate` on an
older plan folder will report GR2037 errors.** That is true and useful.

**Correction — the "nothing goes red on merge" claim below was wrong, and an independent review caught
it.** The original text reasoned that `BannedPatternRegistryTests` uses synthetic fixtures and therefore
nothing breaks. It missed that *those synthetic fixtures are themselves guardrail bodies the validator
scans*. Measured on master today:

| entry | test files whose fixtures it reds |
|---|---|
| `#608a` | `GuardrailRequiresForbiddenTokenTests.cs`, `JitPrefixVetoTests.cs`, `ProducerCoverageTests.cs` |
| `#608b` | **none** — a first scan said two integration-test files; re-checked, those bodies are stub *agent runners* (they read stdin and emit `{"type":"result"}`), which the validator never scans |
| `#561` | **none** |

**Three** files, one line each. The `#608b` row was wrong in the first measurement and is recorded here corrected rather than quietly fixed — an over-broad scan that counts non-guardrails is the same error as an over-broad lint, one level up. The three that remain are real: a task guardrail (`HistoricalTask06Guardrail`), a plan-root terminal gate (`GateBody`), and a plan gate (`PlanGate`). **They belong to no task**, which is the #587 tripwire shape — they would
surface at the terminal gate where nothing can fix them. So the re-baseline is a named deliverable of
`04-author-tests-registry-entries`, which owns the test surface and runs in tier 0, *before* the entries
land. Immunizing a fixture (adding `'Stop'`, or a terminal `exit`) does not change what it tests, so each
file stays green both before and after.

---

## 9. Implementation handoff

### 9.1 Files

| file | change |
|---|---|
| `src/Guardrails.Core/Execution/InterpreterMap.cs` | `.ps1` templates invoke the shim; `powershell.exe` fallback likewise |
| *(new)* the shim script, embedded and materialized beside the run | §3.2 verbatim |
| `src/Guardrails.Core/Execution/GuardrailRunner.cs:384` | exit 97 branch (§3.5) |
| `.claude/skills/plan-breakdown/references/banned-guardrail-patterns.json` | +4 entries (§7) |
| `.claude/skills/plan-breakdown/references/guardrail-catalogue.md` | ordering rule (§4.2), #428 anti-pattern (§6) |
| `.claude/skills/plan-breakdown/references/stacks/dotnet.md` | `:1506-1512` ordering; emit `'Stop'` |
| `.claude/skills/plan-breakdown/SKILL.md` | `:547`, and the generator emits `'Stop'` + a terminal `exit` |
| `.claude/skills/guardrails-review/SKILL.md` | `:422`, `:2212`; +#428 probe |
| `docs/plans/35-…/02-tests-fail-on-stubs.ps1` | move the list creation above its first use |
| `tests/Guardrails.Core.Tests/BannedPatternRegistryTests.cs` | curated-set assertion + per-entry firing controls |
| `docs/plans/02-schemas-and-contracts.md` | the shim is a contract change: record it |

### 9.2 The test that matters most

Every other test here is ordinary. This one is the plan:

> **A guardrail that aborts mid-script must fail, and a guardrail that calls `exit 1` must still fail.**

Both halves, in one fixture, because two independently plausible shims broke the second while fixing the
first (§3.3). A test that only pins the abort passes a shim that reports every real finding as a pass.

Firing controls required, per the #302 two-sided rule: each of the four registry entries must be shown to
**fire** on its `mustMatch` fixtures and stay **silent** on its `mustNotMatch` fixtures — the registry's
meta-test already enforces this, which is why entries are cheap and safe to add.

---

## 10. Sequencing

**Corrected during breakdown: this is a FLAT plan, not two waves.** The draft above proposed waves, and
the plan-breakdown skill's own rule rejects that — a wave exists for a stage whose downstream tasks
*cannot be authored* until the upstream is materialized, and every task here was fully authorable up
front. Fine-grained ordering is a task DAG inside one wave; a wave barrier would only destroy the
parallelism. The dependency argued in §1 is real but it is a *trust* relationship, not a materialization
one, and it is carried by two `dependsOn` edges rather than a barrier.

Eight tasks, three tiers:

| tier | tasks |
|---|---|
| 0 | `01-author-tests-guardrail-abort`, `03-fix-plan35-census-list-ordering`, `04-author-tests-registry-entries`, `06-correct-scan-order-doctrine`, `07-add-rendered-vs-stored-probe` |
| 1 | `02-implement-guardrail-abort` (after 01), `05-add-banned-pattern-entries` (after 04) |
| 2 | `08-record-shim-contract` (after 02) |

Two shape decisions worth recording, because both were forced by issues in this repository's own backlog:

- **The registry work is split into an author-tests task and a JSON task (04 → 05) rather than one.** A
  single task would carry a `writeScope` mixing `.claude/**` with a normal test path — **#540**, measured
  at 12 attempts and never green, because an atomic attempt cannot bank the half it got right. Split, each
  half is one mechanism.
- **The doctrine work is split by SKILL DIRECTORY (06, 07), not by deliverable.** Splitting 06 by its
  three deliverables would produce three tasks with overlapping `writeScope` on the same three files —
  the #132/#175 AI-merge duplicate-definition shape — which is a worse outcome than one task whose blast
  radius is three files in one directory. The Step 2 trigger (a) was considered and dispositioned, not
  waved through.

No task's `writeScope` overlaps another's, so the terminal union guardrail needs no duplicate-definition
sub-check.

## 11. Self-critique — what wrong implementation passes this?

**A shim that catches the abort and breaks `exit 1`.** Two forms that read as obviously correct do exactly
that (§3.3). §9.2 pins both halves. *This one already happened, twice, while measuring.*

**A detector keyed on text pwsh never emits.** §0.2 — already happened once, and it is why §3.2 is
structural. Any future reviewer who proposes going back to a text signal should be shown the ConciseView
transcript.

**Registry entries that match the two known spellings.** The #449 entry keyed on `Get-Content -Raw`, or
the #561 entry keyed on `blankKeepingNewlines`, would each pass their fixtures, ship, and catch the next
instance only if it were copy-pasted. §5 and §7 say *shape*; the `mustNotMatch` arrays are where that gets
proven, and they should contain a **respelling of the bad shape**, not merely good code.

**Fixing the doctrine and calling #561 done.** 29 committed guardrails keep the defect. That is accepted
in §8 and is only defensible *because* the lint stops the next copy. If the lint is descoped, the
acceptance in §8 must be revisited in the same breath — the two decisions are load-bearing on each other.

**Shipping #428 as a catalogue entry nobody reads.** It has no gate by design (§6). Its only enforcement
is the `/guardrails-review` probe, which is the same enforcement #449 proves can be ignored four times.
**#428 is therefore the weakest item in this plan, and that is a known, stated limit rather than an
oversight** — the alternative is a heuristic lint, which is worse.

**The whole plan certifying itself — and the reassurance that turned out to be false.** The doctrine
tasks' guardrails are written using the doctrine those tasks are changing. This section previously said
the shim keeps that honest. **It does not, for this plan's own run:** the harness executing the run is the
*installed* CLI, so the shim task 02 builds never protects tasks 03–08. What actually keeps this plan
honest is narrower and worth stating accurately — every guardrail in the folder sets
`$ErrorActionPreference = 'Stop'` and creates its accumulator before first use, and an independent pass
could not construct a `'Stop'` fail-open at all (engine error, `[xml]` cast failure and null-method all
exit 1 under `pwsh -File`, inside `try{}finally{}` and at top level). That is a real second line of
defence, and it is the argument for `#608a` rather than a footnote to it.

**Three of this plan's own guardrails certified vocabulary rather than work, and an independent pass
measured it.** Tasks 06, 07 and 08 could each be satisfied by appending the required literals — in one
case a single line — leaving 18 backwards doctrine statements in place. That is this plan's own defect
class, authored into this plan, and it is the strongest argument in the document for #467: the pass that
found it was run by an agent that did not write it. Per #467, that is not optional here.
