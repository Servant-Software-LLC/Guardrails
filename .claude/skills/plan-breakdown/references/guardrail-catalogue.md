# Guardrail catalogue — archetypes, decision tree, anti-patterns

The quality of a plan breakdown IS the quality of its guardrails. This catalogue is
the selection doctrine: **deterministic over prompts, always.** A unit test beats a
regex; a regex beats an LLM judge. Every guardrail must answer one question:

> **"What wrong implementation does this catch?"**

Write that answer as a comment at the top of every guardrail file (`# catches: …` in
scripts; an HTML comment or frontmatter note in prompt guardrails). If you cannot
write the sentence, the guardrail is decorative — delete it.

**Two layers.** This catalogue holds the **universal** doctrine — archetypes, the
decision tree, the demotion gate, and stack-agnostic anti-patterns. Stack-specific
*idioms* (how .NET registers a project in a solution, how Java declares an interface,
the canonical build command, the layout-specific grep-scope traps) live in a **stack
file** — `references/stacks/<stack>.md` — which SKILL.md Step 0 loads for the detected
stack. When this catalogue says "the exact regex/command lives in the stack file,"
follow that pointer; never bake a `.NET`-only pattern into a guardrail on a JVM/Go/
Python project.

## Archetypes (the number is a stable ID, not a strength ranking)

**Run the source-shape demotion gate (next section) BEFORE you pick from this table.** The numbers are
identifiers the rest of this catalogue cites (`archetype #4`, `the #8 form`) and they order the table
roughly by cost. They do **not** rank strength, and #1 being first is not permission to reach for it
first: reaching for a `file-contains` regex over implementation source because it is the top row is the
measured authoring defect of #468.

| # | Archetype | Form | Use when | Catches |
|---|-----------|------|----------|---------|
| 1 | **file-exists / file-contains** (regex) | script | Any artifact-producing task — `file-exists` is almost always guardrail #1. **`file-contains` over IMPLEMENTATION SOURCE is the LAST resort for a claim about runtime behaviour** — pass the demotion gate below first, and say in the breakdown report why no test could carry it (#468) | Agent claimed success without producing the artifact, or produced the wrong shape |
| 2 | **command-exit-code** | script | Task output is itself runnable; CLI behavior checks | Artifact exists but is broken when actually executed |
| 3 | **build-passes** | script (`dotnet build`) | Any code-producing task | Code that doesn't compile |
| 4 | **specific-tests-pass** | script (`dotnet test --filter`) | Behavior implementation — the filter selects **THIS task pair's OWN test class**, never a plan-wide trait, and carries a **zero-match guard** ("Its SCOPE decides whether it proves anything", #455); whole-suite green belongs to a terminal integration task only. **Re-emit failure DETAIL at the end of stdout so it reaches the retry tail (#179 — "Failure detail must reach the retry tail" below)** | Wrong behavior, regressions in the targeted area |
| 5 | **lint/format clean** | script | The repo already has a configured linter (never introduce one ad hoc) | Style/usage violations the repo's standards forbid |
| 6 | **schema-validates** | script | Task emits structured data and a schema exists (or you inserted a schema-author task) | Structurally invalid output |
| 7 | **port/endpoint-answers** | script (probe + curl, owns process start/stop, with timeout) | Task delivers a running service behavior | Service that builds but doesn't actually serve |
| 8 | **build-passes + tests-fail-on-stubs** (behavioral) · **tests-fail-on-current-code** (data-model) | script | THE distinctive one — the TDD "red" signal for inserted test-author tasks. The **form depends on the type under test** — see the stub-based TDD section below (it is the SSOT for choosing); its **filter must select this pair's OWN tests + guard the zero match**, or the red proof degrades into merge-order luck (#455). Where the prompt **enumerates behaviours**, emit the red as the **per-test census** (every manifested behaviour observed `Failed`), not a suite exit — a suite red hides a hollow test behind its failing siblings (#375) | Tautological tests that pass against a stub and verify nothing |

> **The TDD "red" must COMPILE and FAIL, never merely exit non-zero.** A non-compiling
> test file exits `dotnet test` non-zero *identically* to a compiling-but-failing one, so a
> guardrail that accepts **any** non-zero exit as "red" passes on **garbage that does not
> compile** — and the implementation task (whose `writeScope` excludes the test file) cannot
> fix it, so the run dead-ends at `needsHuman`. True TDD red is "tests compile AND fail."
> Achieving it splits on the type under test, and the **stub-based TDD decision (next
> section) is the SSOT**: a **behavioral type** (a class with methods/logic) gets the
> **two-guardrail** `build-passes` + `tests-fail-on-stubs` pattern (the test-author task also
> writes minimal stubs so the tests compile); a **data model** (enum/record/value type, no
> behavioral stub possible) defaults to **collapsing the split** and asserting `tests-pass` in
> one task, or keeps `tests-fail-on-current-code` as a compile-coupled red with a strengthened
> structural `covers-key-behaviors`. Read that section before selecting either form.
| 9 | **verify-recorded-action-result (don't replay)** | script | The action ALREADY ran an expensive command (a build+test) and the postcondition is expressible from what it recorded — verify the recorded output/artifact instead of re-running the command | A wasteful replay of the action's own work — see the dedicated section for the GOOD-vs-BAD-target rules (this is a speed/flake trade-off, NOT a free correctness win) |
| 10 | **prompt-judge** | `.prompt.md` (writes `{pass, reason}` verdict) | **LAST RESORT** — see the demotion gate | Genuinely subjective properties: tone, clarity, design taste |
| 11 | **negative assertion** (`if -match … exit 1`) | script | The action prompt EXCLUDES a scenario/keyword the file must NOT contain ("do NOT include `X`", "must NOT call `Y` directly") — the mirror of `covers-key-behaviors`; pair it with the positive check | A removed/forbidden scenario the agent included anyway — undetected by a presence-only coverage check (#176) |

## The source-shape demotion gate — prove BEHAVIOUR with a test, shape with a regex (#468)

**A test IS the property. A source-shape regex is a PROXY for it, and proxy and property are only ever
accidentally aligned.** That is why one guardrail class failed repeatedly while every other class held.
Measured over three adversarial review rounds and five independent agents on one breakdown:

| layer | outcome across 3 rounds / 5 agents |
|---|---|
| Test-based checks — scoped filters (#455), verified tool output (#248), the #179 re-emit, zero-match guards, TDD-red pairs, the DAG | **never broken by any agent in any round** |
| Regex checks asserting a property of **implementation source** | **every blocker lived here**, including **5 regressions introduced while fixing earlier rounds** |

Three patch rounds did not converge — round 3 found *more* blockers than round 2. The layer was
ultimately deleted and what it guarded moved into behaviour.

### The ordering — apply it in this order, stop at the first rung that carries the property

1. **Behavioural proof.** The invariant is a claim about what the code DOES at runtime → a test,
   an exit code, an endpoint response (archetypes #2/#4/#7/#8). The test is the property; there is
   nothing to drift. **The drive-the-real-seam contract test (#382) is a named rung-1 instance with NO
   rung-3 form** — *"the component works through the production seam"* is behaviour, and a regex over a
   test file grepping `new ClaudePromptRunner(` certifies **vocabulary**, which is this section's headline
   failure verbatim. See the drive-the-real-seam section for its one permitted degradation (which is a
   *different assertion*, not a weaker spelling of the same one).
2. **An AGREEMENT property test**, when the invariant is *"X must USE Y"* (next section). No regex can
   express it; a test can. **Do not reach for it when the question is "does X work THROUGH the real Y?"** —
   that is rung 1's real-seam case, and the two are not interchangeable (drive-the-real-seam → "AGREEMENT
   vs real-seam").
3. **A source-shape regex — LAST.** Only when the property is genuinely **unobservable at runtime**.
   Then: pass the anti-pattern battery below, ship the two-sided sample pair, and **state in the
   breakdown report why no test could carry it.** An unexplained source-shape check on a behavioural
   claim is a self-review finding, not a style preference.

**The one question that decides rung 1 vs rung 3:** *is this a claim about what the code DOES, or a
structural fact about the build/wiring graph?* Behaviour → test. Structure → regex. And before writing
any token probe, the #479 test still applies: *can a correct implementation be written that this
rejects?*

### Scope note — source-shape checks are NOT always wrong

The rule is about reaching for a regex to prove something **a test could prove better**, not a blanket
ban. These are genuine structural facts with **no runtime proxy**, and they held up fine across the same
rounds: **build-descriptor registration** (a `.csproj` in a `.slnx`, `stacks/dotnet.md §1`),
**cross-module reference chains** (§2), **entry-point wiring** (#64), the **grep fallback** in
composition-root wiring (#120, explicitly the weakest of its three forms), **negative assertions** over
an excluded scenario (#176), and the **`writeScope`-adjacent** facts the harness cannot see. Keep those.

### Declaration is not behaviour — the measured headline

Against a tree carrying the plan's **type declarations and no wiring at all**, a **14-clause grep
manifest went 10/14 green**. A `bool NoRoute` property satisfied *"the no-route outcome exists"*. Every
clause read as a reasonable statement about the feature; ten of them were satisfied by declarations
alone. **A grep manifest measures vocabulary, not capability** — which is the whole of #468 in one
sentence, and the reason rung 1 exists.

### The compounding rule — RE-RUN THE WHOLE BATTERY AFTER EVERY EDIT

These guardrails are maintained by the agent whose work they grade, so **a fix to one clause and a
regression in its neighbour arrive in the same commit** — five times in the motivating rounds, including
a raw-vs-stripped inconsistency fixed in round 1 and re-broken by the round-3 rewrite of the same file.
Every defect below was **one execution away from discovery**; what was missing was that the execution had
to be re-run *after every edit*, and it never was. So: **after ANY edit to a source-shape guardrail,
re-run its entire two-sided sample pair — not just the case you just fixed.** Fixing the case in front of
you and re-running only that case is how round 3 found more blockers than round 2.

### The measured failure taxonomy — named shapes `/guardrails-review` probes by name

Each was found by **executing** a guardrail, not by reading it. Every one reads plausibly on the page.
Entries marked *(covered)* are already doctrine elsewhere — cited here so the battery is complete, not
restated.

| # | named shape | what it did | anchor on instead |
|---|---|---|---|
| 1 | **declaration-satisfies-call** | a bare `Name\s*\(` matched the method's OWN signature, so *"does Resolve delegate?"* passed on a `Resolve` that delegated to nothing | *(covered)* the dotted call — method-call anchoring (#76); Probe B op 6 |
| 2 | **truncating body extraction** | a `(?ms)` body extractor ending at `^\s{0,8}\}` stopped at the **first nested** block (file-scoped namespaces put methods at 4 spaces, nested closes at 8) — **false-green** on the exact inversion it existed to catch, **false-red** on a correct implementation whose `if`s were braced. Brace style decided the verdict on correct code | never extract a body by brace-matching in a regex. Key on a token whose presence the OUTCOME implies, scoped to the one file |
| 3 | **case-mismatch with the language** | PowerShell `-match` is case-**IN**sensitive; C#/Java/Go identifiers are case-**sensitive**. A clause keyed on `JudgeTier` was satisfied by `judgeTier`, an unrelated local in a different class — certifying an entire unbuilt wave as landed | `-cmatch` (or an inline `(?-i)`) for every **required-present** identifier clause in a case-sensitive language. **Polarity decides how bad the mistake is:** on a REQUIRED clause, case-insensitivity false-**GREENS** (an unrelated `judgeTier` satisfies it) — always `-cmatch`. On a FORBIDDEN clause it can only over-ban, so `-match` is the safe default there |
| 4 | **inconsistent stripping across siblings** | one check stripped comments, its sibling in the same file did not; a token was satisfied by a `// checklist:` comment | *(extends #97/#98)* the **two-variable rule** below — strip ONCE at the top, and let **no** clause match raw content |
| 5 | **raw-vs-stripped inconsistency inside one script** | the negative checks read stripped code, the positive check re-read `$content` raw — so a resolver naming the required symbol **only in a comment** passed. Fixed in round 1, **re-broken by the round-3 rewrite of the same file** | *(extends #97/#98)* the two-variable rule; no second `Get-Content`; and the compounding rule above |
| 6 | **modifier-order / modifier-presence fragility** | a positional `record Foo(…, bool X)` failed while `sealed record Foo(…, bool X)` passed — one irrelevant keyword decided the verdict | *(generalises #112)* anchor on the part the language FIXES (the declaration keyword through the name), never on a modifier list the author may freely reorder or omit |
| 7 | **under-inclusive negation** | a ban keyed on a **defaulted `bool` parameter** missed the non-defaulted form, the nullable form, an `async` signature, and an options-object parameter | ban the **construct** (the enum member, the type, the destination), not one spelling of it — and when you must enumerate forms, say in `# catches:` that the ban is a **lower bound** |
| 8 | **name-locking a free choice** | a required datum's token alternation rejected a correct implementation that used **the plan's own phrasing** for the concept | *(covered)* the PRECEDENT anti-pattern — accept the artifact's form, or both. A free naming choice is not an invariant |
| 9 | **vacuous token** | a `[Mm]odel` coverage token was satisfied by the test file's own `namespace …ModelTiering;`; a `[Rr]unner` token by the ambient type `PromptRunnerConfig` | the **ambient-vocabulary test**: before pinning a coverage token, grep the target file's namespace, usings, and surrounding type names for it. A token already present before the task runs discriminates nothing |
| 10 | **one-line omnibus evasion** | `var unused = new { r.Costly, r.Climbed, r.NoRoute };` — ONE line of **real code** satisfies every token of a multi-token coverage check, so comment-stripping is irrelevant | *(extends Probe B ops 1/5)* require the tokens in **distinct constructs** the outcome implies (a `[Fact]` per behaviour, a dotted call per collaborator), never N tokens anywhere in one file |
| 11 | **count floor over an executed test run** | `dotnet test` counts **theory data rows**, not methods — one `[Theory]` with six `[InlineData]` rows cleared an *"at least 6 executed"* floor while proving one behaviour | never a count. Use the behaviour manifest (below) — read with the **per-test red census** predicate (observed `Failed`, then `Passed`), not with name discovery, which entry 14 defeats |
| 12 | **declaration-is-not-behaviour** | the 14-clause manifest, 10/14 green against declarations with zero wiring (above) | rung 1 — a test |
| 13 | **control characters from the authoring pipeline** | a clause containing literal `0x08` bytes (a `\b` collapsed by the authoring transport) could never match, and a **negative-only** smoke test could not reveal it — everything was failing anyway | never author a regex-bearing guardrail through a shell heredoc; and run the **VALID** half of the sample pair, which is the only half that exposes a clause that can never match |
| 14 | **vacuous test body** | a test NAMED for the behaviour whose body asserts a **tautology** (`Assert.True(true)`, `Assert.NotNull` on a value the test itself constructed, an assertion that never invokes the subject). It cleared the `covers-*` token floor **and** sat green on the stub tree behind its genuinely-failing siblings, so the suite-level red certified the file honest — five security invariants pinned by nothing (#375) | the **per-test red census** — every manifested behaviour's test observed **`Failed`** in the runner's own result file, never merely discovered by name. **Never** a rejection-shaped source regex (`Assert\.Throws`/`Assert\.False`): it false-**reds** a correct `Assert.Equal(RejectedStale, r.Outcome)` and is satisfied in one tautological line — taxonomy 1/9/10 all apply to it. Probe B op 21 |
| 15 | **a guard that cannot fire** | a per-test census's zero-match precondition read `@($xml.TestRun.Results.UnitTestResult).Count -lt 1` — but with **zero tests executed the TRX carries no `<Results>` element**, so the navigation yields `$null` and `@($null).Count` is **1**. In the only situation the guard existed for it evaluated `1 -lt 1`, fell through, and emitted **11 misdirected findings** naming every pinned behaviour as unbound — at the one artifact the retry agent may edit, which is what its own header comment said it prevented | **execute the guard against its zero case** before shipping it — #302's sample pair applied to the precondition (a guard is *proven* to fire, never merely authored). Measured siblings in the same family: `Total:` counting `[Skip]`ped tests, a **localized** summary line, `-v q` suppressing the very string the guard matched on. Universal rule: §"Its SCOPE decides whether it proves anything" companion rule 2; the `@($null)` / TRX specifics are `stacks/dotnet.md §4.4` |

### The two-variable rule — one strip, two levels, no raw matching

Entries 4 and 5 say *"strip consistently"*; #470 says *"strip string literals before a forbidden scan"*.
Applied naively as ONE variable those collide, and the collision is itself a dead-end: a guardrail can
legitimately **require** a token that lives inside a string literal — the measured case is a required
`[Trait("Category", "…")]` attribute — so stripping literals before the **required** clause makes it
unsatisfiable, which is exactly the failure #470 is about. Derive **two** variables, once, at the top:

```powershell
$raw  = Get-Content $f -Raw                                  # NEVER matched against
$code = [regex]::Replace($raw,  '/\*[\s\S]*?\*/', '')        # comments gone ...
$code = [regex]::Replace($code, '(?m)//.*$', '')             #   ... -> what POSITIVE clauses read
$scan = <also strip string literals from $code>              #        -> what FORBIDDEN clauses read
```

- **`$raw` is never matched against.** Any clause reading it is entry 4/5.
- **Every required-present clause reads `$code`** — comments stripped, string literals intact, so an
  attribute value or a required message string can still satisfy it.
- **Every forbidden-present clause reads `$scan`** — literals stripped too, so a banned token cannot
  false-RED from a comment, a string, or a test name.

Consistency means *no clause matches raw text* and *every clause of the same polarity reads the same
variable* — **not** that both polarities read the same one. (The literal-stripping expressions are in the
forbidden-token collision section, #470.)

## The AGREEMENT property test — the answer to "X must USE Y" (#468)

**The invariant that no regex can express.** When a plan says *"the resolver must consume the one shared
candidacy predicate"* / *"the writer must go through the injected formatter"* / *"both call sites must
share one policy"*, the property is **agreement**, not text. Three successive regexes failed on the
motivating case (a resolver required to consume a shared predicate); the replacement was **one test**:

> for every `(block, rung)` pair, the resolver's candidate set **agrees with** `ServesTier`.

An **inlined copy that is equivalent today passes** — which is correct, because today it *is* equivalent
— **and fails the moment it drifts**, which is the only moment the rule matters. That is the whole
property, and a source regex cannot state it: a regex asks *"is the symbol mentioned?"*, which is
satisfied by a comment, a `using`, a local stub, or a dead reference, and is silent about the thing the
plan actually cares about.

**The shape.** Enumerate the input domain (or a representative bounded sample of it), evaluate both
sides, assert equality — and make the failure message name the disagreeing input.

**Fires when** a task's prompt says *must use / must consume / must go through / must share / must not
diverge from* a named collaborator, predicate, table, or policy. **Prefer this over rung 3 every time
the two sides are both callable from a test.** When one side is NOT callable (a build descriptor, a
wiring fact), you are in the scope note above — a structural fact with no runtime proxy — and a regex
is correct.

> **Not the same thing as a drive-the-real-seam contract test (#382), and NOT a substitute for one.**
> AGREEMENT asks *does X **agree with** Y* — it evaluates **both** sides and asserts equality, catching
> **drift** between two implementations of one policy. Real-seam asks *does X **work through** the real Y*
> — it constructs **one** side for real and asserts an effect only that side emits, catching **a contract
> the fake silently satisfies**. The full comparison, and the reason **an AGREEMENT test between a fake and
> a real implementation is worse than nothing**, is in the drive-the-real-seam section → "AGREEMENT vs
> real-seam".

## A source-shape guardrail ships with its two-sided sample pair COMMITTED (#468 / #302)

#302 already requires a two-sided smoke test **at author time**. This makes it **durable** rather than a
per-round manual pass that nobody repeats: the samples become artifacts that live beside the script, so
the next edit — by a later wave, a regeneration, or the agent the guardrail grades — can re-run them.

**The rule.** A guardrail asserting the shape of implementation source ships with two committed sample
files, in a `samples/` sibling of the guardrail folder:

```
tasks/<id>/guardrails/NN-check.ps1
tasks/<id>/samples/NN-check.valid.<ext>      # a representative CORRECT artifact — the guardrail must exit 0
tasks/<id>/samples/NN-check.invalid.<ext>    # the ONE defect the guardrail exists to catch — must exit non-zero
```

> **Never put a sample INSIDE a guardrail folder.** The loader enumerates **every** non-`.json` file in
> `guardrails/`/`preflights/` and treats it as a guardrail — there is no extension allowlist. A
> `NN-check.valid.cs` dropped in `tasks/<id>/guardrails/` loads as a **script guardrail** with no
> validation error, **counts toward GR2003** ("task has ≥1 guardrail" — a fixture would satisfy the
> task-is-verifiable check), and is **executed** at run time. In the catches-enforced folders it is a
> GR2027 load error instead. `tasks/<id>/samples/` is not enumerated by the loader and is excluded from
> the task definition hash, so a sample edit cannot silently invalidate a review marker.

Both are re-run by `/guardrails-review` and by **any later edit to the script** (the compounding rule).
The valid half is the one authors skip and the one that pays: it exposes a clause that can never match
(taxonomy 13), a false-red on legitimate brace style (2), and a case mismatch (3) — none of which the
invalid half can reveal, because under it everything is failing anyway.

**Keep the samples honest.** The valid sample must be **complete** — a representative correct artifact,
not a minimal fragment. An incomplete valid sample produces a *different* failure and masks the real
one (measured: it nearly hid a #470 collision).

### The DOCUMENTATION escape hatch — do NOT mandate an impossible artifact

**For a documentation deliverable there is no meaningful invalid sample, and no behavioural rung to
demote into.** You cannot run prose; and "a wrong version of this design doc" is not a thing you can
synthesize with a straight face. That is the artifact class where this issue's own remedy is *least*
applicable — and it is one of the most common terminal tasks (every plan with a contract document ends
with an SSOT-landing task).

So the rule for a **prose/document target** is:

- The two-sided sample pair is **NOT required.** State in the breakdown report that the deliverable is a
  document and the pair was skipped for that reason — an honest, named exemption, never a silent one.
- **The PRECEDENT check is the substitute, and it is mandatory.** For every literal token the guardrail
  demands of the document, point at a **sibling precedent in that same document**. Two greps settle it.
  (The full rule is the *"Demands a token with no PRECEDENT in the target artifact"* anti-pattern —
  don't restate it, run it.)
- **Accept both forms when both are legitimate.** Where the house style and the code identifier are each
  defensible, write the alternation — `'(?:"judge"\s*:|AttemptJudge)'` — rather than dictating one.

A **code** deliverable does not get this hatch: if you cannot write an invalid sample for a code
guardrail, you do not yet know what the guardrail catches, which is the `# catches:` rule failing.

## Every required-present clause records its MEASURED baseline count (#478)

**The sample pair above cannot see this one.** Both halves of the pair are *synthetic files you wrote*;
neither says anything about **the real tree the task will run against**. A clause can fail its `.invalid`
sample, pass its `.valid` sample, and read perfectly on the page — while being **already satisfied by
the target file as it stands today**, for a reason that has nothing to do with the capability. The task
then only has to satisfy that clause's siblings, and the clause certifies nothing for the life of the
plan.

**Three shipped in one authored wave, and every one survived a full `/guardrails-review` pass:**

| the clause | why it was already green before its task started |
|---|---|
| the harness must write a `.prompt.md` | `action.prompt.md` **already appeared twice in that exact file**. Measured: appending `internal static class Marker { public const string k = "prompt"; }` — one unused constant, zero capability — took the whole task to **exit 0** |
| `Scheduler.cs` must contain `Judge` | it already contained `CriticalityJudge` **5×** |
| the replacement for row 1 — a dotall `guardrails` … `prompt.md` proximity window | it matched `CreateDirectory(…"guardrails"))` against the pre-existing `"action.prompt.md"` two lines below |

Row 1's guardrail carried the comment **"appears nowhere else."** That was not a measurement, it was an
impression formed while reading that region of the file — and **the comment was the defect**, because it
put a claim exactly where a reviewer would look for evidence.

**A red exit code does not clear you.** Run as authored against the real tree, *every* pure-script
guardrail of that wave exited **1** — including the two carrying a pre-satisfied clause. **A guardrail
has many clauses and one exit code**, so a clause that is green on arrival hides behind its siblings'
failures and **no amount of executing the script can see it**. The measurement has to be per clause.

### The rule

**For every required-present clause you author, run that clause's own pattern against that clause's own
subject on the tree as it stands, and record the count in the script.**

```powershell
# baseline counts on the untouched tree - MEASURED, not assumed:
#   \bStage2GuardrailSpec\b  0     \bJudgeGuardrail\b  0     (?<!action\.)prompt\.md  0
if ($code -cnotmatch '\bStage2GuardrailSpec\b') { $failures += '...' }
```

- One `Select-String` per clause, over the **exact subject the clause scans** — not the repo, not
  "roughly that area" — with the **same case sensitivity as the operator** (`-cmatch`/`-cnotmatch` are
  case-sensitive, `-match`/`-notmatch` are not; taxonomy 3 is what confusing the two costs).
- **Count the text the clause actually reads.** Under the two-variable rule your required-present clause
  matches `$code` (comments stripped), while `Select-String` reads the file raw. Hits that are entirely
  inside comments do not pre-satisfy a `$code` clause — a raw count of 3 that is 3 comment mentions is
  **0**, and recording 3 invents a defect. Look at where each hit lives before you write the number.
- **Zero is the expected answer. A nonzero count means you change the CLAUSE, not the comment.** Pin
  something the outcome implies that the tree does not already carry — row 1's fix was the negative
  lookbehind `'(?<!action\.)prompt\.md'`, measured at 0 on the untouched harness.
- **A nonzero count is permitted only with a named reason on the same line** (see the exceptions below).
  An undeclared nonzero is the finding; a declared one is a fact the reviewer can re-measure.
- **Measure against the tree the TASK will see, not the tree you are standing in.** A non-root task runs
  after its ancestors have written, so today's `0` can be tomorrow's pre-satisfaction. You cannot measure a
  tree that does not exist — so do the cheap textual half: check whether an **ancestor task's prompt or
  `writeScope`** puts that same token in that same subject. If one does, the clause is measuring the
  ancestor's work, not this task's. Note the check in the comment (`no ancestor writes this token`) so the
  reviewer knows it was asked.
- **Subject does not exist yet?** Write `n/a — file created by this task`. The clause is red on arrival by
  construction and there is nothing to measure. Say that rather than writing a `0` you did not measure —
  the honest note and the fake zero look identical to a reviewer, which is how this defect got here. But
  run taxonomy 9's **ambient-vocabulary** test anyway: a token that will be ambient in the file the task
  creates (its namespace, its usings, a base type) discriminates nothing the moment the file exists.
- This **generalises taxonomy 9's ambient-vocabulary test** — from *coverage tokens* to *every
  required-present clause*, and from *"the namespace, usings and surrounding type names"* to the whole
  subject the clause actually reads.

**Polarity — only two clause kinds have a defect here. Measuring the other two manufactures false ones.**

| clause kind | expected on the baseline | green on arrival means |
|---|---|---|
| **required-present** (`-cnotmatch 'X'` → fail) | **0** matches | **pre-satisfied — the defect this section exists for** |
| **numeric floor** (`-lt N` → fail) | count **< N** | the floor is already cleared, so it certifies nothing |
| **forbidden-present** (`-cmatch 'X'` → fail) | 0 matches, and that is **CORRECT** | nothing at all — a ban is *supposed* to be green before its task. A ban **RED** on arrival is the #470 collision, which is a different section |
| **behavioural** (a suite must PASS) | RED | `tests-fail-on-stubs` failing to be red — the ordinary TDD-red story, not this one |

Roughly **a third** of the `if`-position clauses in the committed corpus are bans. Applying the
measured-zero rule to them "finds" a defect in every correctly-authored prohibition.

### The exceptions — declared, never inferred

A required-present clause is legitimately nonzero on the baseline when it is:

- a **positive baseline / wave-entry preflight** — assert-PRESENT *by design*, and already documented as
  such (the preflight and wave-gate sections);
- a **`tests-untouched` / regression** clause — "this existing thing is still here" is green on arrival
  by definition;
- the *"if X is present"* half of a **union-safe conditional** integration guardrail (#125/#165);
- a **ratcheting behaviour-manifest** clause on a plan regenerated against a partially-landed tree —
  some named tests already exist, and that is the ratchet working as intended.

Each of these stays. Each carries its measured count **and its one-line reason**. A rule with no hatch
would reject correct guardrails, which is the anti-pattern at the head of this file wearing a different
hat.

### ACCUMULATE the clause results; early-exit ONLY for a precondition or a cost stage

A **multi-clause** guardrail appends each clause's finding to an accumulator and dumps the whole list at
the end. It does **not** `exit 1` on the first clause that fires.

```powershell
$failures = @()
if ($code -cnotmatch '...') { $failures += 'clause 1: <what is missing, and why it matters>' }
if ($code -cnotmatch '...') { $failures += 'clause 2: <...>' }
if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Output "  - $_" }
    exit 1
}
```

Give each clause its **own distinguishable message** — two clauses sharing wording make the list
unreadable as a per-clause verdict, which is the whole value of the shape.

Three reasons, in cost order:

1. **#179, priced per attempt.** An early-exit chain feeds back **one** gap per attempt, so an N-clause
   guardrail can cost N attempts to converge — N full model invocations to learn what a single run
   already knew. A committed example carries the author's own comment *"One `if` per token so the failure
   names the gap"* directly above four chained `exit 1`s: the intent was to name the gaps, and the shape
   delivers the first one.
2. **The compounding rule.** Fix clause 1, discover clause 2, fix clause 2 and re-break clause 1. That is
   how a patch round finds *more* blockers than the round before it.
3. **It is the only shape a reviewer can read a per-clause verdict out of.** `/guardrails-review` Probe A₂
   uses the baseline failure LIST as its fast path. With an early-exit chain there is no list, and every
   clause falls through to a hand-run census.

**Two early exits stay legitimate, and both are visible in the script:**

- a **precondition** — the subject is missing or unparseable, so every clause below would crash or compare
  against nothing: `if (-not (Test-Path $file))`, a failed `ConvertFrom-Json`, an empty state key. It must
  short-circuit. (**87 of the 97** committed multi-clause accumulator guardrails carry one.)
- a **cost stage** — the expensive behavioural clause (`dotnet test`) placed *after* the accumulator dump,
  so a structurally-wrong tree never pays for a suite run.

Both are blind spots for the fast path: on a greenfield baseline the precondition fires and the clause
list is never printed, and a staged clause never executes at all. That is a reviewer's problem to work
around, not a reason to avoid either shape — but note which one you used in the header comment so the
reviewer is not guessing.

## Never use an executed-test COUNT as a suite's adequacy floor (#468)

A floor like *"at least 6 tests executed"* over a `dotnet test` run is **gameable and measures the wrong
thing**: the runner counts **theory data rows**, not behaviours, so **one** `[Theory]` with six
`[InlineData]` rows clears a 6-test floor while proving a single behaviour. Raising the number does not
fix it — six rows become twelve for free.

**Use a behaviour MANIFEST — one clause per required behaviour, never a number.** The manifest is the
right data structure: it names *behaviours*, so no amount of data-row multiplication clears it, and it
**ratchets** — a later wave lands the behaviour and its clause goes green with nobody editing a script,
which is mechanism instead of discipline.

**The PREDICATE over the manifest is what decides whether it proves anything, and the obvious one is too
weak (#375).** Matching each clause against the runner's **test-name listing**
(`dotnet test <proj> --filter "<the filter>" --list-tests`, which enumerates without running anything)
asks *"does a test with this name exist?"* — and a **hollow body satisfies that exactly as a comment
satisfies a token floor**. Name-discovery relocates the naming problem one abstraction up; it does not
close it. Measured: a test file naming every wire token of a security matrix, with `Assert.NotNull` /
`Assert.True(true)` bodies, cleared its floor. **#468 proposal 4 chose the right data structure and the
wrong predicate over it.**

> **The predicate is `observed FAILED on the stub tree, then observed PASSED after implementation`** —
> read from the **runner's own per-test result file**, never from a name listing and never from stdout
> (#248). That is the **per-test red census**; its rule, its two-sided pair, and its honest boundary are
> §"The per-test red census — every manifested behaviour's test observed FAILED, not merely discovered".
> The manifest is the shared artifact; the census is what reads it. Adopting the census is not a
> competing proposal — it is proposal 4 finished.

The clause-authoring rules are the same under either predicate:

- Each clause names **one** behaviour and pins a **discriminating** test name — a name a correct suite
  would carry, not a substring an unrelated test satisfies (taxonomy 9).
- **One message per unbound behaviour** (#179 / the accumulator rule), so one attempt learns every gap
  rather than the first one.
- **Name-discovery ALONE stays a lower bound.** Where there is genuinely no stub tree to be red against
  (see the census's "where it does not apply"), the `--list-tests` form is what you have — then say so in
  `# catches:` and do **not** word it as proof the behaviour is tested. A discovered test may assert
  nothing.

The **zero-match guard** (#455) is the one legitimate use of a count, and it is not an adequacy floor:
it asserts `>= 1` test **executed**, which proves the filter selected something rather than proving the
suite is adequate. Keep it; the ban is on counts standing in for coverage.

### file-contains: structural vs. keyword matching (universal)

A `file-contains` regex must match the **construct**, not a bare keyword that can also
appear in a comment, an import/`using`, a string literal, or a locally-defined copy of
the thing you meant to require. A check for "implements interface IFoo" that greps for
the token `IFoo` passes on `// IFoo`, on `using …IFoo`, and on a class that declares its
*own* local `IFoo` — none of which prove the real type was implemented. Match the
language's declaration syntax instead: `class Foo : IBar` (C#), `implements Foo` /
`extends Bar` (Java/TS), `func (r Recv) Method` (Go). This principle is stack-agnostic;
the **exact regex per language lives in the stack file** (`references/stacks/<stack>.md`,
e.g. the C# class-declaration pattern in `stacks/dotnet.md`).

**Member-order insensitivity (#112).** A structural check must also be insensitive to the
**free ordering of members/accessors** inside the construct. A property's accessors have no
fixed order in C# — `{ get; init; }` ≡ `{ init; get; }` ≡ `{ get; set; }` ≡ `{ set; get; }` —
so a regex keyed on a fixed leading accessor (`…NAME\s*\{\s*get`) **false-passes a "property
removed" check** when the property survives as `{ init; get; }` (init first), shipping an
incomplete refactor green; it **false-fails a "declared" check** symmetrically. Key the match
on the order-free part of the declaration — **up to the opening brace** (`public\s+TYPE\s+NAME\s*\{`)
— and, only if accessor presence matters, test for `(get|set|init)` **anywhere inside** the
accessor block, never a fixed leading accessor. The exact C# property-declaration regex is
`stacks/dotnet.md §3.1`. The same rule applies to any `class/record/interface … { … }` check:
anchor on the part of the syntax whose order the language fixes, never on whichever
member/accessor happens to be written first.

### Comment-blind keyword scan — strip comments before forbidden-keyword matching (universal) (#97, #98)

The structural-vs-keyword rule (above) is about a *required* construct a comment/`using`/local
copy can fake. This is its **forbidden**-keyword mirror: a guardrail that scans source text for
**banned** constructs — read-only checks (`MERGE`/`EXEC`/`INSERT`/`xp_cmdshell`), no-shell,
no-eval, `no-console.log`, no-`TODO` — and matches the **raw file including comments** will
**false-POSITIVE on a comment** (and on string literals and disabled code). Same root cause as
the structural rule — *matching raw text, not code* — same fix family — *strip/parse, don't
raw-grep*. But where structural-vs-keyword causes a false **green** (a comment satisfies a
required token), comment-blind scanning causes a false **red**: a comment that merely *names*
the banned thing trips the check.

**Why this is a BLOCKER pattern, not a nuisance.** It is not a wrong implementation passing — it
is a **correct implementation failing permanently**, with no path to recovery. The classic trap is
a *coupled pair*: (1) the action prompt asks the agent to write a self-describing **safety-header
comment** naming the banned constructs (good engineering practice — "READ-ONLY survey; performs no
MERGE/INSERT/EXEC; makes no external calls (no xp_cmdshell…)"); (2) the guardrail keyword-matches
the **raw file** and so flags those keywords *in the header the prompt asked for*. The agent cannot
tell the match came from its own comment, so each retry it strips one mention and exposes the next —
**whack-a-mole to `needs-human`** on a strictly-read-only artifact. Real run (plan 0007 task 01):
attempt 1 flagged `MERGE`/`EXEC`, attempt 2 `EXEC`, attempt 3 `xp_cmdshell` — three *different*
banned keywords across three attempts, all from one safety comment. The harness behaved correctly
(accurate feedback, retries, honest halt) — the **guardrail** was mis-scoped.

**Rule (catalogue doctrine).** Any guardrail that scans a source artifact for **banned keywords**
MUST strip the source language's comments — ideally string literals too — **before** matching. Use
the target language's comment syntax. For SQL (the motivating case), strip `/* */` block comments
and `-- …` line comments first; the same applies to any language — a `//`-comment or docstring that
documents "this code uses no `eval`" must not trip an `eval` ban.

```powershell
# catches: a forbidden-keyword (read-only / no-shell) check that false-POSITIVES on a comment -
#          e.g. a SAFETY-HEADER comment the action prompt asked for ("performs no MERGE/EXEC,
#          no xp_cmdshell") - sending a CORRECT read-only script to needs-human via whack-a-mole.
#          Strip comments BEFORE the keyword scan so only real code is matched.
$raw = Get-Content $f -Raw
$c = [regex]::Replace($raw, '/\*[\s\S]*?\*/', ' ')   # /* */ block comments
$c = [regex]::Replace($c,   '--[^\r\n]*', ' ')        # -- line comments
# ...now run the banned-keyword checks against $c (the comment-free code), NOT $raw.
if ($c -match '(?i)\bxp_cmdshell\b') {
    Write-Output "$f calls xp_cmdshell in CODE (not just a comment) - external/unsafe surface"
    exit 1
}
exit 0
```

For a **line-number-reporting** guardrail (e.g. no-forbidden-egress that names the offending line),
do not collapse the file — **blank the comment spans in place, preserving newlines**, so line
numbers in the failure message stay accurate:

```powershell
# strip block comments but KEEP newlines so reported line numbers stay correct
$raw = [regex]::Replace($raw, '/\*[\s\S]*?\*/', { $args[0].Value -replace '[^\r\n]', ' ' })
$lines = $raw -split '\r?\n'
# an existing per-line '--' line-comment skip handles line-comment-only lines
```

```bash
# catches: same - a banned-keyword scan that false-positives on a comment/safety-header.
#          Strip /* */ then -- before matching; scan the code, not the comment.
set -euo pipefail
c=$(perl -0pe 's{/\*.*?\*/}{ }gs; s{--[^\n]*}{ }g' "$f")
if printf '%s' "$c" | grep -Eiq '\bxp_cmdshell\b'; then
    echo "$f calls xp_cmdshell in CODE (not just a comment) - external/unsafe surface"
    exit 1
fi
exit 0
```

**Action-prompt discipline — the breakdown must not grep for what it tells the action to document
(#98).** A self-describing safety header is good practice, but it requires a **comment-safe**
guardrail. During guardrail selection, flag the **direct conflict** when the *same* task both
(a) tells the action to write a header comment naming the banned constructs AND (b) greps for those
constructs without comment-stripping — that pairing is a guaranteed false positive that burns the
full retry budget and escalates a correct artifact. Resolve it by stripping comments in the guardrail
(above); the `# catches:` line already documents intent, so the **action prompt should NOT enumerate
banned keywords** unless its guardrail is comment-safe. The per-language comment syntax lives in the
stack file (`stacks/dotnet.md §11` for SQL/C#).

### The DOCUMENTATION target has the same hole, at the opposite polarity — strip `<!-- -->` before a required-present clause (universal)

Everything above is written about **source files**, and #97/#98's failure mode is a false **RED**: a
comment *names* a banned construct and reds a correct artifact. Over a **`.md` target** the same
blindness runs the other way and produces a false **GREEN** — because the clause you write over a
document is almost always **required-present** ("the contract doc must mention `X`"), and the
two-variable rule's *"every required-present clause reads `$code`, comments stripped"* was never
extended past source syntax. Markdown's comment is `<!-- … -->`, and nothing in the family stripped it.

**Measured.** A guardrail requiring two tokens in `docs/plans/02-schemas-and-contracts.md` and in a
`SKILL.md` went from exit **1** to exit **0** when this one line was appended:

```
<!-- TODO: document `samples verify` and SampleVerifier here -->
```

The guardrail's stated purpose — *the contract moves in the same change-set as the code* — was
discharged by a commented-out TODO. This is not the "thin prose" residual the `covers-*` floor already
admits to. **An HTML comment renders as nothing**: it is *invisible* text, and a reader of the published
document cannot tell the difference between it and the token's total absence. A one-word mention at
least appears on the page for a human to judge; this does not.

**Rule.** A required-present clause over a `.md` target strips HTML comments before matching:

```powershell
# catches: a required-present clause over a MARKDOWN target satisfied by an HTML COMMENT - the doc
#          "documents" the contract in text that RENDERS AS NOTHING. Strip <!-- --> before matching.
#          Fenced code is deliberately NOT stripped (see below): a fence renders.
$f   = "docs/plans/02-schemas-and-contracts.md"
$raw = Get-Content $f -Raw                              # never matched against (the two-variable rule)
$doc = [regex]::Replace($raw, '(?s)<!--.*?-->', '')     # invisible text gone -> what the clauses read

# The residual, NAMED rather than silently absorbed: an UNTERMINATED '<!--' has no closing '-->' for the
# lazy quantifier to reach, so its text survives the strip. Do NOT "fix" that by stripping to EOF - that
# deletes the rest of the document over one stray token and turns a typo into a green no-op. Fail on it:
# it is malformed markdown, and this check cannot be trusted over it. (Measured on four real repo docs -
# the SSOT, this catalogue, plan-breakdown's SKILL.md, the README - the residual count is 0 in all four,
# so this clause fires only on a genuinely unterminated comment.)
if ($doc -match '<!--') {
    Write-Output "$f has an UNTERMINATED '<!--' - the comment strip cannot bound it, so this required-present check cannot be trusted. Close the comment."
    exit 1
}

# -cnotmatch stays the default for a required-present clause (taxonomy 3 - case-insensitivity
# false-GREENS a required clause). But note the judgement over PROSE: a token that legitimately appears
# sentence-capitalized needs an explicit alternation, NOT a downgrade to -notmatch - dropping to
# case-insensitive to dodge one capitalization re-opens the false-green for every other spelling.
$failures = @()
foreach ($token in 'samples verify', 'SampleVerifier') {
    if ($doc -cnotmatch [regex]::Escape($token)) {
        $failures += "$f does not document '$token' outside an HTML comment - the contract must move in the same change-set as the code"
    }
}
if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Output $_ }
    exit 1
}
exit 0
```

**Fenced code blocks are NOT stripped — and this is the interesting half of the judgement.** The
tempting symmetry ("a token in a fence isn't prose either") is wrong, and the discriminator is
**rendering**: a fence *renders*, so an entry documenting a verb in a usage fence is legitimate house
style, not evasion. Measured on the real SSOT (`docs/plans/02-schemas-and-contracts.md`): **43,387 bytes
of fenced content across 26 blocks**, and **2 of its 36 `PlanDefinition` occurrences live inside one**.
A clause that stripped fences would reject a correct document written in its own style — the
name-locking-a-free-choice failure (taxonomy 8) wearing a different hat. Strip what renders as nothing;
keep what renders.

> **If you do count fences — for your own analysis, never as a strip — anchor the regex to line starts.**
> An unanchored fence pattern (`(?s)` + triple-backtick + `.*?` + triple-backtick) also matches inline
> triple-backtick spans sitting inside prose lines, and silently mis-attributes them. Measured on the same
> SSOT: the unanchored form reports **29 blocks / 128,214 bytes / 15 of 36 occurrences fenced**; the
> line-anchored form (`(?ms)^` + triple-backtick + `.*?^` + triple-backtick) reports **26 / 43,387 / 2**.
> Get this wrong and your judgement about what is "legitimately fenced" is wrong before you write a clause.

**Where this bites hardest:** a DOCUMENTATION deliverable is **exempt from the `.valid`/`.invalid` sample
pair** (§"the two-sided sample pair" — you cannot synthesize a meaningful invalid design doc), so the one
mechanism that would have caught a doc clause satisfiable by an invisible line is the one that does not
run over doc targets. The strip above is the cheap compensating control for that exemption, and it is
**not optional** on a `.md` required-present clause. The PRECEDENT check remains the other half.

### Positive-effect / non-hollow assertion (universal) (#73)

The structural-vs-keyword rule has a sibling on the *value* side: a guardrail must
assert a **positive observable OUTCOME**, never merely the **absence of an error** — a
zero exit, a `NotNull`, or the bare *presence* of an assertion keyword. An assertion that
green-lights a zero/null/empty result is structurally a **no-op for a "did anything get
produced?" question**: a terminal e2e that runs a full migration and asserts
`Assert.Equal(0, writer.Count)` certifies a no-op while reporting success. This is the
terminal-task analogue of `tests-fail-on-current-code` (archetype #8) — asserting a
zero/null quantity is equivalent to asserting nothing at all about an output quantity.

The trap has two shapes:

1. **Hollow keyword-presence on a count.** A regex that requires only that an assertion
   *mentions* a quantity token — `Assert.*\([^)]*(Moved|Written|Count|Entities)` — matches
   `Assert.Equal(0, writer.Count)`. The keyword is present; the value is zero; the migration
   moved nothing and the run is green. (Note: anchor on `Assert.*\(` / `Assert\.\w+\(`, not
   `Assert\w*\(` — the latter's `\w*` cannot span the `.` in the dotted xUnit form
   `Assert.Equal(`, so it silently never matches it. The point stands either way: matching the
   assertion's *text* is the wrong tool; require a positive *value*, below.)
2. **Absence-of-error standing in for presence-of-effect.** A terminal guardrail whose
   whole assertion is `exit 0` / no exception / `Assert.NotNull(result)`. "It didn't throw"
   and "the handle isn't null" are *necessary* but never *sufficient* for "it produced the
   thing" — an empty result is non-null and throws nothing.

**Rule.** When a task's deliverable is a **non-empty quantity of output** — a migration
moved-count, items written, rows produced, entities created — the guardrail MUST require a
**strictly positive** value, not the presence of an assertion keyword and not merely a
non-error exit. Apply this to the terminal/integration e2e task whose action prompt claims
a "how many items were processed" result (see SKILL.md Step 4's decision tree).

- **Pattern to AVOID** (hollow — matches `Assert.Equal(0, x.Count)`):
  `Assert.*\([^)]*(Moved|Written|Count|Entities)`
- **Pattern to USE** (requires a positive value):
  `(>\s*0|>=\s*1|NotEmpty\s*\(|True\s*\([^)]*Count\s*>\s*0)`

> **Also enforced deterministically (GR2037, #346).** The hollow AVOID construction above is banned by
> the banned-pattern registry (`references/banned-guardrail-patterns.json`, entry `#73`; SSOT §4.6) —
> `guardrails validate` rejects any guardrail whose comment-stripped source keys on the
> `Assert.*(Moved|Written|Count|Entities)` keyword-presence shape. It **complements**, not replaces, the
> #302 smoke-test + `/guardrails-review`; use the strictly-positive value check above and it never fires.

Even stronger than matching the *source text* of an assertion is reading the **runner's
recorded outcome** (a TRX, a structured result file, or a state key the action published)
and asserting the moved-count `> 0` directly — the source-text regex proves a positive
assertion was *written*, not that the run actually *produced* a positive count. Prefer the
recorded-outcome read when the action emits one (verify-recorded-action-result, #9; the
state-output leaf below); fall back to the positivity regex when it does not.

```powershell
# catches: a terminal e2e that runs the migration but asserts a HOLLOW result -
#          Assert.Equal(0, writer.Count) / Assert.NotNull(...) / a bare exit 0 - so a
#          run that moved ZERO entities still goes green. Require a POSITIVE moved-count.
$test = "tests/Migration.E2E/EndToEndTests.cs"
$src  = Get-Content $test -Raw
# AVOID: -match 'Assert.*\([^)]*(Moved|Written|Count|Entities)'  # passes on Assert.Equal(0, x.Count)
if ($src -notmatch '(>\s*0|>=\s*1|NotEmpty\s*\(|True\s*\([^)]*Count\s*>\s*0)') {
    Write-Output "$test never asserts a POSITIVE moved-count (>0) - a zero-entity migration would pass"
    exit 1
}
exit 0
```

```bash
# catches: a terminal e2e that runs the migration but asserts a HOLLOW result -
#          Assert.Equal(0, writer.Count) / Assert.NotNull(...) / a bare exit 0 - so a
#          run that moved ZERO entities still goes green. Require a POSITIVE moved-count.
set -euo pipefail
test="tests/Migration.E2E/EndToEndTests.cs"
# AVOID: grep -E 'Assert.*\([^)]*(Moved|Written|Count|Entities)'  # passes on Assert.Equal(0, x.Count)
if ! grep -Eq '(>\s*0|>=\s*1|NotEmpty\s*\(|True\s*\([^)]*Count\s*>\s*0)' "$test"; then
    echo "$test never asserts a POSITIVE moved-count (>0) - a zero-entity migration would pass"
    exit 1
fi
exit 0
```

### verify-recorded-action-result: don't replay, but don't trust a log either (#62)

The harness hands every guardrail the action's **already-captured** outcome
(SSOT §5.1): `$env:GUARDRAILS_ACTION_RESULT` → `action-result.json` =
`{ kind, exitCode, summary }`, plus `$env:GUARDRAILS_ACTION_STDOUT` /
`$env:GUARDRAILS_ACTION_STDERR` (the captured streams). So when an action ran an
**expensive** command — the motivating case is a `dotnet build; dotnet test` action — a
guardrail can verify the postcondition by *reading what the action recorded* instead of
**re-running** the whole build+test suite. The replay is what makes the run slow.

**This is a speed/flake trade-off, not a free correctness win.** Replaying re-executes
reality; reading recorded output trusts a log. Verify-don't-replay is sound **only** when
the postcondition is expressible from recorded output the action **could not fabricate**.
Choose the target deliberately:

**GOOD targets** (recorded output the action could not fabricate):
- An **artifact the action produced** — a built DLL, a generated file. Verify it with the
  ordinary archetypes: file-exists (#1) / file-contains (#1) / command-exit-code (#2).
- A **runner-written structured result file** — a TRX / JUnit / coverage file the *test
  runner* wrote (not the action's prose). Assert it exists and parse it for the pass/fail
  totals the runner recorded.
- An **upstream task's state value** read from `GUARDRAILS_STATE_IN` (or the producer's
  fragment) — already covered by the state-output leaf below.
- `GUARDRAILS_ACTION_RESULT.kind` — confirms *which kind* of action ran (e.g. `script`).
  Useful as a cheap sanity assert; it is **not** a substitute for checking the artifact.

**BAD targets — name these as traps, never generate them:**
- **The action's `exitCode`.** At guardrail time it is **ALWAYS 0** — a non-zero action
  fails the attempt *before* any guardrail runs (SSOT §5.1, §6.1). `if ($result.exitCode
  -ne 0)` is a pure **tautology**: it can never fire. (This is also why there is no
  `GUARDRAILS_ACTION_EXIT_CODE` env var — it would be tautological by construction.)
- **The action's own self-reported success line in `_STDOUT`.** Grepping
  `GUARDRAILS_ACTION_STDOUT` for `"Passed!"` / `"Build succeeded"` / `"0 Error(s)"` is an
  **echo-judge**: the action narrates its own success, so the guardrail trusts the thing
  it is supposed to check. It is also **format-brittle** — that exact wording rots across
  SDK / runner versions, so the guardrail silently passes (or spuriously fails) on an
  upgrade. The runner's *structured* result file (TRX) is the honest read; its *prose
  stdout* is not.

**When the strong postcondition isn't expressible from recorded output, re-executing
reality IS the honest gate.** Don't replace a strong replay (e.g. `dotnet test --filter`
that actually runs the targeted tests) with a weak grep just to save time — a slow honest
check beats a fast tautology. Reach for verify-recorded-result only when a GOOD target
above carries the postcondition.

GOOD snippet — verify the runner-written TRX the action's `dotnet test` produced (and a
produced artifact), NOT the exit code and NOT a stdout success word:

```powershell
# catches: the build+test action ran but did not produce its built artifact, OR the test
#          runner recorded failing tests — verified from the recorded TRX, without
#          re-running the build+test suite (a wasteful replay of the action's own work)
$result = Get-Content $env:GUARDRAILS_ACTION_RESULT -Raw | ConvertFrom-Json
if ($result.kind -ne 'script') {
    Write-Output "expected a script action; recorded kind = '$($result.kind)'"
    exit 1
}
# GOOD target 1: an artifact the action PRODUCED (could not fabricate by narrating success)
$dll = 'src/MyProj/bin/Release/net8.0/MyProj.dll'
if (-not (Test-Path $dll)) {
    Write-Output "build artifact missing: $dll (the action claimed success but produced no DLL)"
    exit 1
}
# GOOD target 2: the TRX the TEST RUNNER wrote — parse its recorded totals, do not re-run tests
$trx = Get-ChildItem 'TestResults' -Filter *.trx -Recurse -ErrorAction SilentlyContinue |
       Sort-Object LastWriteTime | Select-Object -Last 1
if (-not $trx) {
    Write-Output "no .trx result file under TestResults/ — the action did not record a test run"
    exit 1
}
$counters = ([xml](Get-Content $trx.FullName -Raw)).TestRun.ResultSummary.Counters
if ([int]$counters.failed -gt 0) {
    Write-Output "TRX records $($counters.failed) failing test(s) — see $($trx.Name)"
    exit 1
}
# Do NOT add `if ($result.exitCode -ne 0)` (always 0 here — tautology) and do NOT grep
# $env:GUARDRAILS_ACTION_STDOUT for "Passed!" (echo-judge, SDK-version-brittle).
exit 0
```

```bash
# catches: the build+test action ran but did not produce its built artifact, OR the test
#          runner recorded failing tests — verified from the recorded TRX, without
#          re-running the build+test suite (a wasteful replay of the action's own work)
set -euo pipefail
kind=$(jq -r '.kind' "$GUARDRAILS_ACTION_RESULT")
if [ "$kind" != "script" ]; then
    echo "expected a script action; recorded kind = '$kind'"
    exit 1
fi
# GOOD target 1: an artifact the action PRODUCED
dll='src/MyProj/bin/Release/net8.0/MyProj.dll'
if [ ! -f "$dll" ]; then
    echo "build artifact missing: $dll (the action claimed success but produced no DLL)"
    exit 1
fi
# GOOD target 2: the TRX the TEST RUNNER wrote — read its recorded totals, do not re-run
trx=$(find TestResults -name '*.trx' -print0 2>/dev/null | xargs -0 ls -t 2>/dev/null | head -n1)
if [ -z "$trx" ]; then
    echo "no .trx result file under TestResults/ — the action did not record a test run"
    exit 1
fi
failed=$(grep -oP '(?<=failed=")[0-9]+' "$trx" | head -n1)
if [ "${failed:-0}" -gt 0 ]; then
    echo "TRX records $failed failing test(s) — see $(basename "$trx")"
    exit 1
fi
# Do NOT test `jq -r .exitCode "$GUARDRAILS_ACTION_RESULT"` (always 0 here — tautology)
# and do NOT grep "$GUARDRAILS_ACTION_STDOUT" for "Passed!" (echo-judge, runner-version-brittle).
exit 0
```

The action must actually emit a TRX for this to work — `dotnet test --logger "trx"`
writes one. If the action does not produce a runner-written result file and the only
"evidence" of success is its prose stdout, you have **no** GOOD target: keep the honest
replay (`specific-tests-pass`, #4) rather than demoting to an echo-judge.

## Stub-based TDD — the "red" must COMPILE and FAIL, not just exit non-zero (#155)

This is the SSOT for **how to author the TDD "red" guardrail** on an inserted test-author task.
The decision-tree's Code branch and archetype #8 both point here.

**The failure this fixes.** A test-author task whose anti-tautology guardrail accepts a **compile
failure** as the "red" signal is gameable. `dotnet test --filter …` exits non-zero whether the test
file **compiles and fails** (true TDD red) or **does not compile at all** (garbage):

```csharp
// ImportMode TcApiLocal XtcFileOnly CommanderRest ImportResult   ← satisfies a bare-keyword covers-check
class Garbage { DOES_NOT_COMPILE }                                ← exits dotnet test non-zero
```

That garbage passes `tests-fail-on-current-code` (non-zero exit) *and* a bare-keyword
`covers-key-behaviors` (the tokens appear in a comment) — **all three test-author guardrails go green
on a file that compiles to nothing**. The downstream implementation task's `writeScope` **excludes**
the test file, so it cannot fix the compile error — the run dead-ends at `needsHuman` with no
actionable error on the task that caused it. True TDD red = **the tests compile and fail**, not
"something exits non-zero."

The fix splits on the **type under test**.

### Behavioral type (a class with methods/logic) → the test-author task ALSO writes the stubs

When the type under test has behavior (methods, an algorithm, a service), the test-author task
produces **two** artifacts: the **test file** AND the **minimal stubs** the tests need to COMPILE —
interface declarations / skeleton classes whose members throw `NotImplementedException` (or return
`default`). The test-author task's `writeScope` covers **both** the test file and the stub file(s).

Replace the single compile-coupled guardrail with **TWO** guardrails (cheapest-first):

1. **`build-passes`** (archetype #3) — `dotnet build` (stack equivalent) succeeds. With the stubs in
   place this is a hard proof the **test file is syntactically valid and type-correct**. Garbage
   fails here unambiguously — there is no longer any confusion between "missing types" and "syntax
   error," because the stubs supply the types.
2. **`tests-fail-on-stubs`** (the #8 form) — `dotnet test --filter …` exits non-zero. Because the
   build now **succeeds** (guardrail 1 proved it), a non-zero exit unambiguously means **the tests
   ran and FAILED** — the stubs throw `NotImplementedException`, so the behavior is genuinely absent.
   That is TDD red.
   **When the action prompt ENUMERATES behaviours, the suite exit is not enough — emit the per-test
   red census instead** (§"The per-test red census"): non-zero fires if *any* test fails, so a hollow
   `Assert.True(true)` passes on the stub tree and hides behind its genuinely-failing siblings (#375).
   Same guardrail, same filter, stronger predicate: every manifested behaviour observed `Failed` in the
   runner's own result file.

The **implementation task's `writeScope` still EXCLUDES the test file** (the deterministic
test-protection, SSOT §3.4 — unchanged) but now **TARGETS the stub file(s)**: it fills real logic
over the skeletons the test-author created. If the stub lives under the same implementation surface
the impl scope already names (e.g. `src/Foo/`), the scope already covers it; if the stub lives
elsewhere, list it in the impl scope explicitly. Either way the test file is out of the impl's scope.

This pattern is **language-agnostic** — every compiled language has a build step, and the
two-guardrail split is identical in all of them (the .NET commands are `stacks/dotnet.md §4`).

### Data model (enum, record, value type — no behavioral stub possible) → collapse, or strengthen

A pure data model has **no behavioral stub**: the type declaration IS the full implementation. A test
that checks "can I set `Mode = TcApiLocal`" passes the instant the type exists — there is no stub-vs-real
distinction, so the two-guardrail behavioral pattern does not apply. **Default: collapse the TDD split
into a single task** — define the type and assert `tests-pass` in one go — and **state the reason
explicitly** in the task description / breakdown report: *"data model — no behavioral stub possible."*

If you keep the split anyway (the type is large enough that authoring its tests first adds value):
- The anti-tautology check is **weaker** here — say so in the report.
- Keep `tests-fail-on-current-code` as the compile-coupled red (the test references the not-yet-existing
  type, so the file won't compile against current code — non-zero exit IS the red, and a separate
  `build-passes` would fail at the same moment, so omit it).
- **Strengthen `covers-key-behaviors` with a STRUCTURAL check** rather than a bare keyword grep:
  assert the test file actually carries `[Fact]`/`[Theory]` **attributes** (not just that the domain
  tokens appear in text) — `(?m)^\s*\[(Fact|Theory)\]` — so a comment naming the enum values cannot
  satisfy the coverage check. (This is the structural-vs-keyword rule, §"file-contains", applied to
  the test-attribute construct; `stacks/dotnet.md §17`.)

### Mixed task (data + behavioral) → lean behavioral

When a task is **mixed** — it adds both a data model and behavior (the commander-import example: an
`ImportMode` enum AND an importer that acts on it) — **lean behavioral**: write the minimal stubs for
the behavioral parts so the whole test file COMPILES, and use the two-guardrail `build-passes` +
`tests-fail-on-stubs` pattern. The data-model members come along for free inside the same compiling
file; the behavioral stubs are what make the build pass and the tests fail honestly.

### Why this is the strongest anti-tautology check the skill has

A compile failure is a *weak* red (garbage produces it). `build-passes` + `tests-fail-on-stubs` is a
*strong* red: the file must be type-correct (build) **and** the behavior must be genuinely absent
(tests fail against throwing stubs). It is strictly stronger than the old single-guardrail form, and
it removes the dead-end where the implementation task could never repair a non-compiling test file.

## Its SCOPE decides whether it proves anything — a test filter selects the pair's OWN tests, never a plan-wide trait (#455)

Governs **both** test archetypes — the forward `specific-tests-pass` (#4) and the inverse
`tests-fail-on-stubs` / `tests-fail-on-current-code` (#8) — so it sits outside the stub-based-TDD
section above rather than inside it. Read in the same breath, though: the red proof just described is
only as strong as **which tests its filter selects**. Get the scope wrong and the strongest
anti-tautology check the skill has silently becomes the weakest.

**The rule.** A **task-level** test filter (`tests-pass` and `tests-fail-on-stubs` alike) selects **the
tests that task pair owns** — the pair's own test class — and nothing else. A **plan-wide** selector (a
category/trait/tag/marker every new test class in the plan carries, a whole test project, a whole suite)
is never a task-level filter.

**The attractive wrong turn — name it, because doctrine that only says "do X" does not survive contact
with the next author.** A plan whose tasks all add tests to one existing test project introduces a
plan-wide trait so the **baseline preflight** can EXCLUDE the not-yet-written tests (`!=` the trait) from
its start-from-green check. That is correct — and it means that at the exact moment you sit down to write
the task guardrails, the plan-wide trait is the most visible, most authoritative-looking selector you
have. It reads like the natural filter for everything. **The plan-wide trait belongs in exactly two
places: the baseline preflight's `!=` exclusion, and nowhere else.**

**Both directions break, and only one of them is loud:**

- **Forward (`tests-pass`) — a cycle no DAG check models.** A trait-scoped `tests-pass` on task 02 selects
  tests task 03 authors red and only task 04 makes green — and task 04 `dependsOn` task 02. Task 02 cannot
  go green until a task that depends on it has run. `validate` and `graph --check` both PASS: the cycle is
  between a task and a **sibling's test corpus**, not between tasks. It costs a full retry budget and ends
  at `needs-human` with the task's own deliverable complete.
- **Inverse (`tests-fail-on-stubs`) — a tautology that certifies nothing.** The check wants *some*
  matching test red. Once any sibling's intended-red tests are on the base it passes regardless of this
  pair's tests — the red proof degrades into **merge-order luck**. This is the worse half: the forward
  deadlock at least fails loudly.

Which one bites is decided by **merge timing, not correctness** — a third task with the identical filter
can pass purely because it branched before a sibling's red tests reached its base.

**Two companion rules, both non-optional:**

1. **The narrowed selector must actually be narrow.** Substring/prefix matching re-widens a filter
   silently (`~Dispatch` selects `DispatchRouterTests` too). Check the chosen selector against every other
   test class the plan authors and every existing class in the target project; qualify it (namespace, full
   class name) when it is not discriminating. Same lesson as the orphaned-golden broad-filter trap.
2. **Narrowing reintroduces the zero-match hole — guard it.** A filter that matches **nothing** (or is
   malformed) typically reports SUCCESS (exit 0), so a typo'd class name turns both halves of the pair
   into green no-ops. Emit a guard asserting the run actually executed ≥1 test. Four things decide
   whether such a guard can fire at all, and each has bitten:
   - key it on the runner's **executed-test COUNT**, not on an error string — the "no tests matched"
     diagnostic is frequently verbosity-suppressed, which is how a string-keyed guard gets written,
     executed, and observed never to fire (the #248 failure);
   - key it on **executed**, not **total** — runners that report a total INCLUDING skipped tests let a
     fully-skipped class satisfy a guard whose entire job is proving tests ran;
   - **pin the runner's output language.** Summary lines are LOCALIZED; a guard matching English tokens
     inverts into an unconditional failure on a non-English machine. This is the axis #248 most often
     misses: verbosity gets varied during authoring, culture almost never does, so the pattern ships
     "verified" with its most fragile dimension untested.
   - **count what the runtime hands you when the answer IS zero.** The count expression itself can be the
     dead part. PowerShell's `@(…)` wraps a `$null` into a **one**-element array, so a precondition keyed
     on `@(<a navigation that yields nothing>).Count -lt 1` evaluates `1 -lt 1` and never fires — in the
     one situation it exists for. Whatever the host language, write the zero case down and evaluate it;
     a count that cannot reach zero is not a guard. (The .NET/TRX shape of this — a TRX with **no
     `<Results>` element** when nothing ran — is `stacks/dotnet.md §4.4`.)

   **A zero-match guard is not authored, it is PROVEN to fire.** All four traps share one property: the
   guard reads correctly on the page and is dead in execution, so no amount of re-reading finds it — only
   running it against an artifact that *should* trip it does. Before shipping one, execute it against its
   own zero case (an empty result file, a deliberately typo'd filter) and watch the precondition line
   come out. This is the #302 sample-pair discipline applied to the guard's own precondition, and it is
   taxonomy 13's VALID-half lesson restated: the half where everything is failing anyway cannot reveal a
   clause that can never match. **Measured cost of skipping it:** a census whose dead precondition let it
   fall through and emit **11 misdirected findings** naming every pinned behaviour as unbound — a
   confident, actionable, wrong message aimed at the one artifact a retry agent may edit, which is the
   exact outcome its own header comment said the guard prevented.

   And **order the guard by polarity**: where exit 0 is the pass, check the exit code FIRST (a runner that
   never started exits non-zero with no summary, and a guard-first script misreports that as "your filter
   matched nothing" — a confident wrong diagnosis pointing at the test file, which is exactly the artifact
   the retry agent is allowed to edit). Where non-zero is the pass, the guard must come FIRST, or a
   crashed runner is certified as TDD red.

The exact filter syntax, the measured runner-output table, the count-based guard expression, and the two
canonical emitted scripts are the stack file's job: `stacks/dotnet.md §4.3`.

## The per-test red census — every manifested behaviour's test observed FAILED, not merely discovered (#375)

**This is the form of `tests-fail-on-stubs` you emit when the test-author task's prompt enumerates
behaviours.** It is not a new guardrail beside the stub-based TDD pair (above) and not new vocabulary: it
is that pair's second guardrail with its **predicate strengthened from suite-exit to per-test outcome**.

**The defect it closes, measured.** A suite-level red is `dotnet test --filter … exits non-zero`, and
non-zero fires if **ANY** test in the filter fails. So a hollow test — `Assert.True(true)`,
`Assert.NotNull` on a value the test itself constructed — **passes on the stub tree** and hides behind its
genuinely-failing siblings, while the red gate reports the file honest. On a security wave a
`covers-*` floor exited **0** against a file that named every wire token with exactly those bodies; the
five load-bearing invariants were pinned by nothing. This is #479's pathology one level down — *a
pre-satisfied item hides behind its siblings' failures* — and #479's fix was **per-item, not aggregate**.
The mechanism needed is therefore **not new machinery**: it is the existing red gate evaluated at the
granularity the claim was always made at.

### The rule

> **Suite form (weaker — for a task with no enumerated behaviours):** `dotnet test --filter <Class>`
> exits non-zero.
>
> **Per-test red census (this section):** for **every** behaviour in the task's manifest, the test bound
> to it is observed with outcome **`Failed`** in the **runner's own per-test result file**. A manifested
> behaviour whose test is passing, skipped, or **absent** is a finding, **named individually**. A test
> outside the manifest is not the census's business.

**Assert the outcome IS `Failed`; never assert it is not-passing.** Result files spell the non-red
outcomes in ways you will not guess — TRX writes a skipped test as **`NotExecuted`**, not `Skipped`, and
also carries `Timeout`, `Aborted`, `Error`, `Inconclusive`. A clause enumerating the bad outcomes lets
every spelling it forgot through, and the one it forgets first is the one an agent reaches for
(`[Fact(Skip="…")]`). The positive form has no such list to get wrong.

The **second side is already shipped**: the implementation task's `specific-tests-pass` (#4) requires the
same names observed **`Passed`** after implementation. Two trees, per test, both sides — the same
`$filter` and the same name list copied verbatim between the pair's halves, exactly as #455 requires.

**Read the runner's own result file, never stdout** (#248) and never a name listing. Per-test outcomes
are a structured artifact the runner writes (TRX for .NET); scraping `[FAIL]` lines out of console output
re-introduces every verbosity and localization trap #455 measured. The .NET realization —
which result file, how to select it, and the emitted script — is `stacks/dotnet.md §4.4`.

### What it kills, exactly

| shape on the stub tree | runner records | census |
|---|---|---|
| `Assert.True(true)` | `Passed` | **RED — caught** |
| `Assert.NotNull(sut)` where `sut` is merely constructed | `Passed` | **RED — caught** |
| a comment / string literal naming the behaviour, no test | absent | **RED — caught** |
| `[Fact(Skip="…")]` named for the behaviour | skipped (**`NotExecuted`** in TRX) | **RED — caught** |
| one `[Theory]` with N rows standing in for N behaviours | one name for N entries | **RED — caught** (N−1 unbound) |
| a genuine test that drives the stub | throws → `Failed` | green — correct |

The measured dogfood file sits **entirely inside the caught column, and not by luck**: those bodies are
hollow *because they never invoke the subject*, and never invoking the subject is precisely what makes
them pass against a throwing stub. The census's power comes from that coincidence being structural.

### The honest boundary — say it here, or the next author will believe it proves more than it does

A test that **invokes** the subject and then asserts something hollow:

```csharp
var result = sut.Consume(staleAnswer);   // the stub throws -> the test FAILS on the stub tree
Assert.NotNull(result);                   // ...and asserts nothing after implementation
```

is red on stubs, green after, and **PASSES the census**. State this loudly rather than footnoting it:
**the census proves the test is coupled to the code path; it does not prove the assertion is correct.**
That is strictly weaker than *"the assertions are right"* and strictly stronger than *"the test exists
and is named right"* — and the second gap is the one that was measured. Closing the invoking-hollow gap
needs a second mutant beyond the null implementation, i.e. **real mutation testing**, which is a deferred
v2 bet, not something to approximate with a regex over test source.

What makes this floor different from the ones it replaces is the **polarity of the residual gaming**: to
defeat a token floor an agent writes a comment (zero coupling, zero cost); to defeat the census it must
write a test that actually invokes the subject, compiles against the real API, fails against the null
implementation and passes against the real one. Still a bad test — but a bad test *wired to the thing*,
and visible to a reader.

### Its aiming surface, and why mis-aiming is safe

The census pins **test names**, so the same #455 prerequisite is non-optional and one step sharper: **the
action prompt must PIN the test method name for each enumerated behaviour**, and the census's manifest
uses those exact names. A prompt that says "author tests for these five behaviours" and leaves the method
names to the agent makes a correct census unwritable.

Mis-aiming is nonetheless in a different safety class from a mis-aimed source regex: **a census entry
that matches no test goes RED**, costs one attempt, and its message names the missing binding. A
mis-aimed source regex goes GREEN on a comment *and* red on correct code. A gate whose mis-aiming can
only cost time is not the same as a gate whose mis-aiming can certify a lie.

### The declared exemption — a row a CORRECT implementation leaves GREEN

The census demands `Failed`. Some enumerated behaviours a **correct** implementation leaves green even
before it is implemented, and demanding red for those demands that a correct implementation fail. The
measured case is the **discriminator**: *"a sound input does NOT halt the run"*. The production path
returns early for that fixture's shape, so it never reaches the not-yet-implemented member; a correct
test of it passes on the pre-implementation tree. It is not hollow — it is the row that gives the
red ones their meaning, because a gate that only ever fires proves nothing about a gate that discriminates.

**The wrong repair is to drop the row**, and it is nearly the one that gets authored: the census goes
green, the behaviour disappears from the manifest, and **a silently omitted row is indistinguishable
from an oversight** — the reviewer cannot tell a considered exemption from a behaviour nobody thought
about. Same principle as every other named exemption in this catalogue (the documentation sample-pair
hatch, the TDD-exempt seam): honest and named, never silent.

**The rule.** Such a row stays in the manifest and carries an **`Expect='Executed'`** marker — the test
is **present in the runner's result file and was not skipped** — with the **structural reason stated in
the guardrail's header comment**, in terms of the production path (*"the consumer returns before it
reaches the stubbed member, so a correct test of this is green on the stub tree"*). Never a bare
`Expect='Executed'` with no reason: the reason is the thing a reviewer checks.

`Executed` is deliberately weaker than `Failed` and deliberately stronger than name discovery. It proves
the test compiled, was selected by the filter, and actually ran — which is exactly what `--list-tests`
name discovery cannot prove and what a `[Fact(Skip="…")]` defeats. It does **not** prove the assertion
bites; nothing in this section does (§"The honest boundary").

> **The abuse to watch for, and its tell.** An exemption is a claim about the **production path**, never
> about the test being awkward to write. If most rows are exempt you no longer have a red census — you
> have a forward one, wearing the census's name. Read that as the structural signal it is: it almost
> always means **there was no upstream test-author task to be red against**, and the repair is the split
> (SKILL.md Step 2 rule 5 — a task that authors both the tests and the implementation they exercise must
> split), not a manifest full of exemptions.

The .NET manifest shape (a bare string means `Expect='Failed'`; a hashtable declares the exemption) and
the loop that reads it are `stacks/dotnet.md §4.4`.

### Shape rules the census inherits (all non-optional)

- **Accumulate, do not early-exit** — one message per unbound behaviour, each distinguishable, so a
  single attempt learns every gap (#179 priced per attempt; the accumulator rule).
- **One legitimate precondition early-exit: no result file.** If the runner wrote no result file the run
  did not happen — fail with *that* diagnosis. Do **not** let it fall through into "every behaviour is
  unbound", which is a confident wrong message pointing at the test file, the one artifact the retry
  agent is allowed to edit (the #455 misdiagnosis rule, applied here).
- **Zero-match is subsumed but not free** (#455): a manifest entry with no matching test is already a
  named finding, which is the guard — but keep a "did anything run at all" precondition, keyed on the
  **count of result records in the file**, never on a verbosity-dependent error string (#248).
  **Reading the result file makes both summary-line traps inapplicable**, which is a reason to prefer it
  beyond per-test granularity: the file's outcome values are schema tokens, so there is no localized
  `gesamt:` to invert the guard and no verbosity flag that can suppress them. Do not port the
  summary-line `Passed:` + `Failed:` regex into a census that already reads the file.
- **No `-v q`, no re-emit.** The census's failure output *is* the per-behaviour list; there is no
  assertion detail to surface, because on the stub tree the expected exception is the point (#179's
  re-emit applies to checks where exit 0 is the pass — `stacks/dotnet.md §4.2`).
- **The census script's own exit code is FORWARD** — 0 when every manifested behaviour is bound to an
  observed `Failed` test. Do **not** key it on the test run's exit code: a suite that exits non-zero is
  exactly the condition that hid the defect.

### Where it does NOT apply, and what to use instead

- **Data-model waves.** A pure data model has no behavioural stub (stub-based TDD, above), so there is no
  red side and the census is inert. The right tool for a data-model invariant is the **negative
  assertion** (archetype #11) — *"the answer-kind enum contains no `review-attested` member"* is a source
  fact with no runtime proxy, which is the legitimate carve-out for a source-shape check (#468).
- **Tests authored BEFORE the run** and reviewed with the plan. The defect is *run-authored* tests; a
  pre-existing, human-read test file is out of scope, and its suite-form red is fine.
- **No enumerated behaviours.** Nothing to manifest — emit the suite form and say so.

### Relationship to the drive-the-real-seam assertion requirement (#382) — reuse, not a parallel vocabulary

The real-seam archetype already carries the **assertion requirement**: *the test must assert an effect
only the production implementation emits; a recording double / call count / `Verify` IS the
passing-but-blind shape.* The census is **the same claim made mechanically** — that requirement's
weakest mechanically-decidable half, *the test can fail when the implementation is absent*, carried by an
exit code instead of by prose plus review. A reader who knows the real-seam rule learns this in one
sentence: **the assertion requirement, checked per test against the null implementation.** `/guardrails-review`
Probe B operator 21 is the reading half, exactly as operator 20 is for the real-seam half.

## Baseline-green / start-from-green (preflight) — verify a CURRENTLY-GREEN positive precondition holds BEFORE any work runs (#181)

**"Never build on red."** This is the general **positive-baseline / preflight** archetype: a plan-root
`<plan>/preflights/` CHECK (a "Full Flight Check", §3.3) that asserts a *positive precondition that
ALREADY holds* on the starting state, so every work task builds on a known-green base. The
**unit-test baseline** (the EXISTING area tests pass on the current code) is the canonical worked
instance — and the ONLY instance the skill emits today — but the SHAPE generalizes to any cheap,
deterministic, currently-true positive baseline:

| Positive baseline (preflight) | The currently-green precondition it pins before the DAG |
|---|---|
| **Existing-tests-green** *(emitted today)* | the EXISTING tests in the touched area pass on the current code |
| Build-green *(same shape; not emitted yet)* | the touched project(s) already compile on the starting code |
| Endpoint-up *(same shape; not emitted yet)* | a dependency the plan extends already answers on the starting state |

All three share the one shape below (a guardrail-shaped FILE in `<plan>/preflights/`, evaluated once
before the DAG). The rest of this section uses the **existing-tests-green** instance because it is the
only one emitted today; read "the area's existing tests pass" as the worked case of "the positive
precondition holds."

Stub-based TDD (above) gives the *new* behavior its red→green signal; this archetype guarantees the
**starting** state was green so that signal is sound. For a plan that builds onto **existing** code,
nothing in the rest of the catalogue verifies that the existing unit tests **in the area future tasks
will modify** pass *before* work begins. The failures that causes:

- **Misattribution.** A work task's `tests-pass` guardrail can fail from PRE-EXISTING breakage, not the
  task's own change → the failure is blamed on the task, retries are wasted, and the run reaches
  `needsHuman` late, on the wrong task.
- **Ambiguous red.** A new test's "red" is only meaningful if the area was green to start — otherwise
  "red because the behavior is missing" is indistinguishable from "red because the area was already
  broken." The green baseline is the prerequisite that makes red→green attribution sound (it is
  TDD-*adjacent*, the precondition, not a step of the cycle).
- **Wasted budget.** A full DAG run against an already-broken base burns time/$$ before surfacing the
  real problem.

### When it fires — BROWNFIELD only

- **Brownfield** = the plan modifies project(s)/module(s) that ALREADY have existing tests in the
  touched area → **emit the baseline-green preflight** (below).
- **Greenfield** = a new project, or no existing tests in the touched area → **SKIP it** (nothing to
  baseline) and state the reason in the breakdown report. Do **NOT** emit a vacuous baseline: a
  `dotnet test` over a project with zero tests trivially "passes" and certifies nothing while looking
  like a gate — strictly worse than no baseline. (SKILL.md Step 0 sets `$baselineArea`; Step 5 inserts.)

### The shape — an existing-tests-PASS check FILE in `<plan>/preflights/`

The emitted artifact is `<plan>/preflights/01-baseline-<area>-tests-green.ps1` — a guardrail-shaped
FILE (same parser as `tasks/<id>/guardrails/`), NOT a task. There is **no `task.json`, no action, and
no `dependsOn`**: the plan-root `preflights/` folder is evaluated by the pre-DAG phase directly, so the
file IS the verification — it exists to gate the run on the precondition holding before any task is
scheduled. (This REPLACES the retired no-op ROOT task model — do NOT emit a `00-baseline-*` task with a
no-op `exit 0` action; the retired no-op-action scaffolding and its #174/#182 short-circuit dependence
are GONE from the baseline story — a RED preflight simply halts the run before scheduling any task.)

**The check: the EXISTING area tests PASS on the CURRENT code, scoped via `--filter`.** Run the
existing test project(s) covering exactly the projects the plan modifies (`$baselineArea` from Step 0)
and assert they ALL pass (exit 0). **Scope to the CURRENTLY-GREEN existing tests of the touched area
via `--filter` — NEVER the whole suite/project.** This is load-bearing, not a nicety: a whole-project
`dotnet test` in the preflight hits the **#165/#176 compile-coupling trap** — a mid-TDD project does not
compile (its test project references types implementation tasks have not produced yet), so a
whole-project test manufactures a FALSE RED that no work task can fix, dead-ending the run. So the rule
is: **the baseline targets the existing, currently-passing tests of the touched area only** — the
`--filter`/category that selects the pre-existing tests, bounded to that subtree. Too wide a scope also
re-imports unrelated flakiness into the pre-DAG phase.

**One baseline per AREA, deduped.** Emit **one** preflight file per *distinct touched test project*,
each scoped (via `--filter`) to gate only **that** area's subtree — NOT a single global whole-repo
preflight. A plan that modifies two independent test projects gets two area preflight files; a plan
touching one area gets one. Never collapse N areas into one whole-suite preflight (that re-creates the
compile-coupling trap and the serialization cost at once), and never emit two baselines for the same
area.

**It runs BEFORE the DAG — no `dependsOn`, no edges.** The `<plan>/preflights/` folder is evaluated once
against the starting repo before the Scheduler builds any wave, so every task is implicitly gated on it;
you do NOT wire work tasks to it (the retired model made every area work task `dependsOn` a no-op root —
that scaffolding is gone). A preflight file is not a DAG node, so acyclicity (Step 3) is unaffected.

### Three load-bearing edges

1. **Existing tests ONLY, evaluated BEFORE the TDD-red tasks.** The baseline asserts the PRE-PLAN tests
   pass; it runs against the STARTING workspace state, BEFORE any inserted `author-tests` task adds its
   intentionally-FAILING new tests. So its check must target the **existing** test project(s)/area and
   must NOT accidentally run (and fail on) the about-to-be-authored red tests. The pre-DAG phase
   evaluates it against the starting bytes (no new tests yet), which makes this natural; if
   `$baselineArea` is a whole project a later `author-tests` task will ALSO add failing tests into,
   prefer a `--filter`/category selecting only the pre-existing tests, so the baseline can never go red
   on tests that don't exist yet.
2. **Distinct from the terminal full-suite gate.** Baseline = green START on EXISTING tests **before the
   DAG** (`<plan>/preflights/`); the terminal `<plan>/guardrails/` gate = green END on EVERYTHING on the
   **merged HEAD**. They are complementary — emit both on a brownfield plan, and state both in the report.
3. **The PASS check is a tests-pass archetype → it MUST use the #179 failure-detail-re-emit
   pattern** (capture → emit full log → re-emit failure-signal lines at the END), so a RED baseline's
   WHY (the failing assertion/exception) reaches the halt feedback, not just `[FAIL] <name>`. (The .NET
   realization is `stacks/dotnet.md §21`, which reuses §4.2's re-emit form.)

### The "worth-it" gate — a check with teeth, not a default-on insertion

Emit a positive baseline ONLY when **ALL** of these hold. If any fails, do NOT emit it:

- **Pre-exists.** The target already exists on the starting state (the area's tests are already there).
- **Modifies, not creates.** The plan MODIFIES the target, it does not CREATE it (a brand-new project
  has no pre-plan green to pin → greenfield → skip).
- **Deterministic + cheap.** The check is deterministic and bounded — **a bounded, filtered
  command** (a filtered `dotnet test`, not "boot the app and poll"): no live-service boot or network
  poll (that flakes), and not the whole unfiltered suite. A baseline that stands up a server or
  runs the whole suite is neither cheap nor union-safe and re-creates the compile-coupling trap.
- **Strictly narrower than the terminal gate.** The baseline is area-scoped and runs before the DAG; the
  terminal `<plan>/guardrails/` gate is whole-repo on the merged HEAD. If your "baseline" is the same
  scope as the terminal gate, it is not a baseline — delete it.
- **≥2 work tasks build on the area.** A baseline pays for itself only when multiple downstream tasks
  share the area it protects (it disambiguates which task broke it). One lone task touching the area can
  attribute its own failure without a baseline.
- **Deduped per area.** Exactly one baseline per distinct touched test project (the dedup rule above).

**Under-fire when unsure.** A MISSED baseline is just the status quo (work tasks attribute their own
failures the slow way); a FALSE baseline halts a correct plan at the root. The asymmetry says: when you
cannot tell whether the area is genuinely green at the start, or whether ≥2 tasks really build on it,
**lean toward NOT emitting** — and let `guardrails-review`'s probe flag the gap if it is real.

### Negative baseline = the existing TDD-red checks, not a new archetype

The mirror question — "is a precondition that should be ABSENT genuinely absent at the start?" — is the
**negative baseline**, and it already has a home: do NOT fork a parallel "negative preflight" archetype.
The "not yet present" negative baseline IS `tests-fail-on-current-code` / `tests-fail-on-stubs` (Stub-based
TDD, above) — a new test's RED proves the behavior is not yet present — and the #120 composition-root
*wired/not-wired contrast* case is the same idea at the wiring layer (a guardrail that passes only because
the feature is NOT yet inert). Reach for those; this section stays purely positive.

### A RED preflight halts the run BEFORE the DAG

A failing `<plan>/preflights/` check stops the run before any task is scheduled (the general
Full-Flight-Check semantics) — **no retry budget is burned on a no-op, because there is no task**. (This
is why the retired no-op ROOT task's dependence on the #174/#182 no-op-deadlock short-circuit is gone
from the baseline story: the short-circuit remains a general §7 rule for any REAL task that no-ops
elsewhere, untouched — it simply no longer participates in the baseline/preflight story.) Make the
check's final actionable line say so plainly: *"the area's existing tests are already failing on the
starting code — fix the pre-existing breakage before this plan builds on it."* That message is the #181 +
#179 composition in one line: a clear WHY (the re-emitted failure detail above it), an actionable
instruction, and a fast halt. **Greenfield → skip** the baseline and state why (nothing pre-exists to
pin).

**Decision-tree leaf:** *the plan is BROWNFIELD (modifies project(s) with existing tests in the touched
area) AND the worth-it gate passes* → EMIT one `<plan>/preflights/01-baseline-<area>-tests-green.ps1`
per touched area (a guardrail-shaped FILE — no task, no action — that runs the EXISTING area tests via
`--filter` and asserts they pass, #179-re-emit form, area-scoped, deduped), evaluated before the DAG so
every task is implicitly gated on it. GREENFIELD → skip and state why; never a vacuous or whole-suite
baseline.

## Wave entry/exit gates — the two boundaries of a wave in a waved plan (#254)

Not a NEW archetype — two EXISTING archetypes relocated to the wave boundary in a waved plan (SKILL.md
Step 9; SSOT §14.3). A wave is a mini-plan, so its `preflights/`/`guardrails/` are the four-folder model
one level up. `terminal-gate-of-wave-N == preflight-of-wave-(N+1)`: one boundary, two authored folders.

- **Wave ENTRY gate (`<plan>/<wave>/preflights/`) = the #181 positive-baseline archetype at the wave
  boundary.** A POSITIVE, assert-**present** check that the concrete artifacts this wave builds on — the
  real files/symbols/binary the prior wave produced — are materialized and non-empty on the branch
  before this wave's DAG spends a turn. **Positive-monotone-safe** (never "not yet present" — a segment
  only grows). Shape (union-safe presence, one actionable line):

  ```powershell
  # catches: wave-N starting before wave-(N-1)'s outputs are materialized on the branch — the "prior
  #          wave materialized" entry gate (the #181 positive baseline at the wave boundary, #254).
  foreach ($rel in @('out/greet.ps1', 'out/config.json')) {      # the real paths the prior wave produced
      if (-not (Test-Path $rel)) {
          Write-Output "$rel not materialized on the branch — the prior wave's output is missing; this wave cannot build on it"
          exit 1
      }
      if ([string]::IsNullOrWhiteSpace((Get-Content -Raw -Path $rel))) {
          Write-Output "$rel is present but empty — the prior wave did not materialize real content"
          exit 1
      }
  }
  exit 0
  ```

  For **wave 1** the entry gate is the ordinary plan-start baseline: a brownfield green-start (#181) or a
  NEGATIVE fresh-start ("the not-yet-produced artifact is absent" = `tests-fail-on-current-code` /
  `tests-fail-on-stubs`, above — not a new archetype).

- **Wave EXIT gate (`<plan>/<wave>/guardrails/`) = the Terminal-Gate archetype per wave.** **GR2028
  applies PER WAVE**: a multi-leaf/fan-in wave's exit gate needs ≥1 real integration re-run (a
  build/suite invocation or a git-conflict-marker union invariant, NOT `exit 0`).

  **Every wave-root gate is LOCAL. Never tag one `scope:"integration"` — the tag is INERT there.**
  `validate` says so: **GR2059** (#459). The per-union re-verify set is built from the task
  `tasks/<id>/guardrails/` folders plus the **plan-root** `<plan>/guardrails/` folder — and *nothing
  else* (SSOT §4.3). A wave-root entry is therefore never in it: it runs **exactly once, on the merged
  HEAD at the end of its own wave** (SSOT §14.3), which is precisely what the folder is for. Dropping
  the `scope` key is **behaviour-identical** — the file already runs at wave exit, tagged or not.
  Whether a wave-root tag *should* become meaningful is an open contract question (#459); do not
  pre-empt it by tagging one today, and do not move a wave-exit gate to the plan root to "fix" the
  warning — that changes WHEN it runs.

  **A union invariant belongs at the PLAN ROOT.** If you need a check re-verified on the merged bytes
  at *every* union — including the fan-ins *inside* a wave — author it in `<plan>/guardrails/` and keep
  `scope:"integration"` **there**. It must then be UNION-SAFE: a **CONDITIONAL** invariant ("if
  contribution X is present, verify it", conflict-marker-free) able to PASS on a partial merge where
  downstream tasks have not run yet (#125 / #165) — a terminal postcondition tagged integration
  red-halts a correct partial merge.

  A whole-build/whole-suite check stays LOCAL in whichever gate folder holds it. The **LAST** wave's
  exit gate runs on the fully-merged HEAD, so a whole-suite `tests-pass` LOCAL check belongs there —
  the same role the flat plan's terminal `<plan>/guardrails/` folder plays. A single-leaf linear wave
  forms no union and needs no integration re-run at all; a plain LOCAL terminal postcondition is fine.
  (Union-safe form: "A `scope:"integration"` guardrail MUST be UNION-SAFE", below.)

`catches:`/GR2027 and the author-time smoke-test (#302) apply to these gate scripts like any other — a
wave entry gate that renders/executes the not-yet-materialized upstream is exactly the #302 high-value
render/execute target (hand-synthesize a materialized sample + a missing-artifact sample). The
`examples/waved-hello` demo carries a worked entry gate (`01-scaffold-materialized`, assert-present)
and exit gates (`01-scaffold-union-clean` conflict-marker union invariant, `01-greeting-complete`
whole-artifact terminal) — **both exit gates LOCAL, neither tagged `scope:"integration"`**, which is
the correct wave-root form.

## Composition-root wiring — the component is CONSTRUCTED/INJECTED in production (#120)

**The recurring lesson, the highest-impact false-green the skill emits.** A plan adds a
new collaborator behind a seam — an `IFoo` interface + a `FooImpl`, injected into some
*assembler* (a factory, a DI container, a `Program.cs`, a `RunCommand`). Every component
task author-tests + implements `FooImpl` against an injected constructor seam, each goes
green, the terminal whole-suite build + test passes — and the feature is **inert**: nothing
ever constructs `FooImpl` and hands it to the assembler in the production path, so the real
entry point never takes the new branch. The tests pass *because they inject the seam
themselves* (`new Scheduler(plan, executor, …, provider)`), which is exactly why they say
nothing about whether production wires it. **Green proves the components in isolation, not
the assembled feature.** This recurred **3×** in one plan (plan-08: the worktree engine, the
AI-merge worker, and the needs-human triage — all built, all unit-tested, all dead from the
CLI because `SchedulerFactory.Create` never constructed/injected them).

This is a sibling of #64 (entry-point wiring) but more general: #64 is the *executable serves
over a port* case (grep `Program.cs` + smoke-test a route); #120 is the *internal collaborator
injected at a composition root* case (a factory/container/wiring method must construct it and
pass it on). The fix is the same shape — **a deliverable plus a guardrail** — applied at the
assembly layer.

**Decision rule — when does this fire?** When a plan introduces a component that must be
**constructed and injected at a production composition root or entry point** to do anything.
Concretely, fire on any of:
- The plan introduces an **`IFoo` + `FooImpl` pair** (or any new collaborator a production
  assembler must construct and pass on). The heuristic: *every `IFoo`/`FooImpl` pair the plan
  adds needs a "wire `FooImpl` into the composition root" deliverable.*
- The new component is reachable only via a **constructor/DI seam** the unit tests inject
  themselves — so the tests pass regardless of whether the production assembler wires it.
- The plan names a **factory / `Program.cs` / `Startup` / DI registration / dispatch site /
  `RunCommand`** that must branch on, construct, or inject the new component.
- The feature activates only under a **mode/flag** (e.g. `maxParallelism > 1`) the production
  dispatch must honour — built machinery reachable "only from xUnit" is the tell.

**Two artifacts close it** (generated in SKILL.md Step 5):
1. **An explicit integration/wiring TASK** — a *named deliverable* distinct from the
   per-component implement tasks: "construct `FooImpl` and inject it into `<the assembler>` so
   the production path uses it." Make a DAG sink depend on it (the wiring is the thing that
   makes the feature real; downstream gates must not be reachable without it). Depends on the
   component-implementation task(s) — the collaborator must exist before it can be wired.
2. **A composition-root guardrail asserting the component is ACTUALLY wired in production** —
   not merely unit-tested in isolation. Two deterministic shapes, strongest first:

   - **(a) Observable-behaviour through the real entry point (strongest).** Drive the real
     composition root / entry point end-to-end (run the CLI on a fixture plan, hit the binary)
     with the new mode active, and assert an **observable output only the wired feature
     produces**. cf. plan-08's `Factory_RunsWorktreeMode_OnCommittedFixturePlan`: it calls the
     **real `SchedulerFactory.Create`** (no manual injection — *a test that injects the provider
     would pass even with an unwired factory and is FORBIDDEN*) at `maxParallelism = 2` and
     asserts the worktree-mode outputs (a `guardrails/<plan>` branch exists; ≥2 commits carry
     `Guardrails-Task:` trailers). This is a `specific-tests-pass` (#4) or
     `port/endpoint-answers` (#7) guardrail pointed at the assembled feature.
   - **(b) Structural/reflection assertion that the assembler injects the collaborator (the
     canonical `Factory_Wires*` shape).** When observable behaviour is too expensive or
     environment-bound to drive in a guardrail, assert structurally that the production
     assembler constructs and passes the collaborator. cf. plan-08's
     `Factory_WiresAiMergeWorker_InWorktreeMode` / `Factory_WiresNeedsHumanTriage_WhenRunnerAvailable`:
     each drives the **real factory** and asserts via reflection that the constructed object holds
     a non-null collaborator field — **with a contrast case** (`maxParallelism = 1`, or a
     script-only plan) asserting it is *null* when it should not be wired, proving the wiring is
     **conditional and real**, not a constant. A pure source grep that `SchedulerFactory.cs`
     contains `new FooImpl(` is the weakest acceptable form (it proves the text exists, not that
     the wired object is reached) — prefer (a), then the reflection form of (b), then the grep.

   The guardrail belongs on the **wiring task** (artifact 1), since that task owns producing the
   wired composition root. It is `scope: "integration"` **only when it ALSO passes the #125
   union-safe decision test** — *"would this pass on a partial merge with a downstream task
   unsettled?"* — evaluated against every union point anywhere in the plan, not just ones upstream
   of the wiring task (a completely unrelated parallel sibling merging back onto the plan branch is a
   union too, and `scope:"integration"` re-verifies there just the same, SSOT §4.3).

   This is an easy case to get backwards: "it drives the whole assembled feature, so mark it
   `scope: "integration"`" and "check it's union-safe" can read as being in tension — apply them
   together instead. A composition-root guardrail almost always asserts "the collaborator IS wired,"
   which typically **cannot** pass until the wiring task's own action has actually run (nothing is
   wired before then) — that fails the #125 test outright, and answers the "when does this fire"
   question for scope, not just for the guardrail's existence. In that (common) case the guardrail
   should be `scope: "local"` instead (the default — omit the `scope` key), verified only in the
   wiring task's own attempt, never re-verified at some other task's merge. Getting this backwards is
   not hypothetical: a `scope:"integration"` composition-root guardrail was re-verified — and
   correctly failed — by two completely unrelated parallel siblings that each merged back onto the
   plan branch before the wiring task had even started, costing both a wasted rollback-and-retry
   cycle for a plan whose guardrails had already passed review (#250). Reserve
   `scope: "integration"` here for the rarer case where the guardrail asserts a union-safe invariant
   that holds even on a partial merge — not "is this fully wired yet."

**Why the existing gates miss it (state this in the report when it fires).** The TDD pair
(`tests-fail-on-current-code` → `specific-tests-pass`) proves the component; the terminal
whole-suite build + test proves nothing *exercises the composition root* with the new mode —
both go green over an unwired feature. A full-suite-green gate over seam-injected unit tests is
**necessary but not sufficient**. The composition-root guardrail is the missing sufficiency check.

**FORBIDDEN shapes** (the review skill hunts these):
- A guardrail that **constructs `FooImpl` itself and injects it**, then asserts it works — it
  proves the component, never the wiring. The guardrail MUST go through the production assembler.
- Trusting the **terminal whole-suite green** to cover wiring — it cannot; that is exactly the
  structural false-green this archetype exists to catch.
- A **prompt-judge** "is this wired correctly?" — wiring is a deterministic structural fact
  (the object is constructed and passed, or it isn't); demote to (a)/(b).

The .NET realization (the reflection-on-factory pattern + the drive-the-real-factory integration
test) is `stacks/dotnet.md §10`.

## Drive-the-real-seam — the component is proven through the ACTUAL seam, not a fake of it (#382)

**Passing but blind.** A per-task TDD guardrail proves a component **in isolation, against a FAKE for
the very seam the real run drives** — a fake `IPromptRunner` injected into a `CriticalityJudge`, a fake
executor, a fake scheduler, a fake factory. The guardrail goes **GREEN over a component that is broken
through the real composition root**: the fake never touches the `StreamLogPath` the real
`ClaudePromptRunner` throws on; the test never drives the executor's real `TransientBackoff`, so a
class-(b) transient that should record `blocker-retried` is silently swallowed. Both were **certified
green by their own guardrails while broken through the real run path** (the `autonomous-mode-impl` wave-3
dogfood). The whole value proposition is "a deterministic gate certifies" — but *a unit test that fakes a
seam the real run exercises, with no paired real-seam test, certifies nothing about the run.*

This is the **per-component** root cause whose **per-wave** symptom is #120's terminal wiring sink: when
every component is proven only against fakes, the ONLY thing that exercises the real wiring is one
end-of-wave "drive the real factory" task — which then surfaces **every** masked integration bug at once,
late, in a task that is over-scoped by construction (#378) and **cannot fix** them (each bug lives in a
different upstream task's file, outside its `writeScope`). "Big-bang integration at the end" is exactly
the anti-pattern TDD/CI methodology warns against; the wave decomposition must not bake it in.

**Decision rule — when does this fire?** When an `author-tests-*` task **injects a fake of an in-process
seam the production run drives** (a prompt runner, the executor, the scheduler, a factory / DI-resolved
collaborator) AND no task provides a **paired real-seam / contract test** for that component. **WHICH**
faked seams owe a proof is not a judgement call: it is decided by the **closed four-bucket classification**
in SKILL.md Step 4 — **N** exempt (a four-item enumeration of non-determinism primitives, carrying the
**N4 trap**: *fake the wait, never the waiter*), **E** and **C** owe proof, **U** relocates it. That
classification, the **T\*** placement test and the **seam ledger** are the authoring procedure; this
section is the guardrail's **shape**.

**Rung 1 under the demotion ordering, and there is NO rung-3 form (#468).** The real-seam proof is a
**behavioural** claim — *the component works through the production seam* — so it is always a **test**. A
regex over a test file grepping `new ClaudePromptRunner(` certifies **vocabulary, not capability**: it is
satisfied by a commented-out line, a `using`, or a construction whose object is then discarded — the
demotion gate's headline failure verbatim. **The source-grep fallback that #120 permits as its weakest
form is NOT available here.** The only permitted degradation is the **#120(b) reflection-plus-contrast**
form, and that is a *different assertion* ("the assembler holds this collaborator"), not a weaker spelling
of this one.

**The archetype: a real-seam contract test, authored as a TDD PAIR across the component's OWN two tasks.**

- **On the `author-tests-*` task** — the real-seam test is written **alongside** the fake-based unit tests,
  listed in the task's `covers-key-behaviors` manifest (#75), and **included in the
  `tests-fail-on-current-code` / `tests-fail-on-stubs` filter**, so it is proven **RED** and cannot be a
  tautology. #155 applies unchanged — the red must **COMPILE** and fail, so this task also writes whatever
  stub the real-seam test needs to compile. (A real-seam test that is red because it does not compile is
  not a proof of anything.)
- **On the implement task — usually T\*** — a `specific-tests-pass` (#4) guardrail whose `--filter` selects
  **that pair's own test class** (#455 scoping, with the zero-match guard and the #179 failure-detail
  re-emit). The test drives the **ACTUAL** seam the run uses — the real `IPromptRunner` / executor /
  scheduler / factory — and asserts the behaviour the real seam exposes (the `StreamLogPath` is honoured;
  the transient path records `blocker-retried`).

Its `# catches:` sentence follows this template:

```
# catches: a component that passes its unit tests against a faked <seam> but is broken
#          through the real <seam> (passing-but-blind) - e.g. CriticalityJudge green against a
#          fake IPromptRunner but throwing on the real ClaudePromptRunner's StreamLogPath.
```

**The assertion requirement — an effect ONLY the production implementation emits.** The test must assert
something the fake could not produce without reimplementing the real behaviour: the stream log **file**
appears on disk; the journal contains a `blocker-retried` **decision**; the verdict's `Source` is **not**
the catch-and-safe-default. ***"The seam was called" is NOT an assertion*** — the fake satisfies it, which
is exactly how the motivating bugs shipped green. This clause is #120(a)'s "observable output only the
wired feature produces" imported wholesale, and it is what makes the archetype survive the review skill's
Probe B (an author can otherwise satisfy a `…RealSeam…` filter with a test that constructs the fake under
a real-sounding name).

**`scope`: `"local"` — omit the key.** A real-seam proof asserts *"this component works through the real
seam"*, which **cannot pass before its implement task's action has run** — so it **fails the #125
union-safe decision test** and must NOT be tagged `scope: "integration"`. This section sits beside the
composition-root section above, which discusses `scope` at length; a reader carrying that discussion
across without carrying its **conclusion** is the live failure mode. The conclusion, restated here so it
cannot be lost in transit: getting this backwards on a composition-root guardrail cost two completely
unrelated parallel siblings a rollback-and-retry on a plan whose guardrails had already passed review
(#250).

**The boundary rule, in its final form — ONE REAL LEVEL, AND NO FURTHER.** (This REPLACES the older rule
of thumb *"fake the process, never the in-process seam"*: same rule, made precise about how far down
"real" goes.)

> The component under test is constructed with the **REAL implementation of its declared dependency**.
> That implementation's **own** declared dependencies MAY be substituted — because each of those
> substitutions is its own ledger row, owed at its own task.

Faking the **CLI / process / subprocess** boundary *beneath* the real seam stays legitimate and expected:
the #120 wiring test runs the real factory while the underlying agent CLI is stubbed, and a real-seam test
may still shell out to a fake external binary. What is FORBIDDEN is faking **the in-process seam the
component under test collaborates with** — that is the exact substitution that blinds the guardrail.

> **This does NOT contradict #120's forbidden shape "a guardrail that constructs `FooImpl` itself and
> injects it".** Same verb, **different slot**, different question. #120 forbids a test injecting the
> collaborator **into the ASSEMBLER's slot** — doing so bypasses the production assembler, so the
> *wiring* is never proven. #382 **requires** the test to construct the real seam and pass it into the
> **COMPONENT-under-test's own constructor** — which proves the *component through its collaborator* and
> claims nothing about the assembler. Concretely: a real-seam test never calls `SchedulerFactory.Create`,
> and a composition-root test never hand-injects. If one test is doing both, it is two tests.

**Why one level is enough — the induction, which is the point of the whole rule.** If every task proves its
component one real level down, then **by induction over the dependency graph** every level of the
composition has been exercised for real *somewhere*, in a scope that could fix it. What remains unproven is
only the **assembly** — that the production assembler constructs these particular objects, in this order,
and hands them on. That residue is small, genuinely composition-level, and is exactly what the terminal
**join-check** should assert. Big-bang integration stops being structurally necessary — which is the whole
return on the extra `[Fact]` per component.

**The construction bound — the honest limit.** If constructing the production seam forces you to build a
**second** real level (the real `Scheduler` needs a real journal needs a real repository needs…), you have
**left the rule**, and the proof degrades along #120's existing ladder rather than a new one:

1. drive the real seam and assert an observable effect — **the default**;
2. **#120(b)** — construct the real collaborator, assert by reflection that the component holds it, **with
   a contrast case** proving the wiring is conditional and real;
3. a source grep — **NOT available here** (see the no-rung-3 floor above).

**A degradation to (2) is NAMED in the breakdown report with the constructor chain that forced it; an
unnamed degradation is a review finding.** A high construction cost is a signal in its own right: a
production type you cannot build without three more of them is badly factored, and surfacing that beats
hiding it behind a fake.

> **The bound applies to bucket C ONLY. An E row can never invoke it.** This is the escape hatch an
> author reaches for first, so it is closed by definition rather than by judgement: what sits beneath an
> **E** seam is a process / network / disk boundary, and faking *that* is already permitted — it is the
> one substitution the rule has always allowed. So constructing a real E adapter never forces a second
> real level; you construct the real adapter and stub the boundary underneath it. "I could not construct
> it" is therefore not available for an E row, and an E row claiming the bound is a review finding, not a
> degradation. Only **C** — an in-repo collaborator whose own dependencies are themselves in-repo — can
> genuinely hit the bound.

**Distribute, don't concentrate.** Prove each component through the real seam **at T\*, the task that
builds it** (so a bug surfaces in-scope and early, where the retry budget can spend itself on a fix)
rather than deferring all real-path proof to a terminal sink. Keep the final full-path check, but as a
**thin JOIN-CHECK over already-proven parts** — never the first place the real path is exercised. Its
`# catches:` must name a defect that **survives every upstream real-seam proof passing** ("the factory
never hands the judge to the scheduler"); if it cannot name one it is redundant, and if the only defect it
can name is *"this seam is exercised for the first time here"* then a ledger row is mis-placed and the fix
is upstream, **not** a wider `writeScope` here. Concentrating proof in that sink is the #378 over-scope
fingerprint; the two issues share one root.

**FORBIDDEN shapes** (the review skill hunts these):
- A test named `…_RealSeam` / `…_Integration` that **constructs the FAKE** — same class, same
  real-sounding name, a substituted seam. This is the cheapest way to satisfy the guardrail's filter
  without delivering anything, and the assertion requirement above is the only thing that kills it.
- An assertion that **the collaborator was called** (a recording double, a call count, a `Verify`). The
  fake satisfies it by construction — that IS the passing-but-blind shape.
- A **source grep** that the test file mentions the production type (the no-rung-3 floor).
- Tagging the guardrail `scope: "integration"` because "it drives the real thing" (#250).
- Substituting a **policy object** and calling it bucket N4 because it happens to sleep — N4 is the wait,
  never the waiter.

This archetype is a candidate for the **#350 vetted-guardrail-library** — the "drive-the-real-seam"
contract-test shape is reusable across plans rather than hand-authored once per wave; parameterizing it
here instead would be the second half-overlapping mechanism this doctrine exists to avoid. The .NET
realization is `stacks/dotnet.md §10e`.

### AGREEMENT vs real-seam — two different questions, and NEITHER substitutes for the other

The AGREEMENT property test (#468) and this archetype (#382) landed independently and are both *"the
answer when a regex won't do"*, which makes them easy to confuse — and a reviewer can substitute one for
the other and believe they have complied. They answer different questions:

| | AGREEMENT (#468) | Drive-the-real-seam (#382) |
|---|---|---|
| the question | *does X **agree with** Y?* | *does X **work through** the real Y?* |
| the defect | **drift** between two implementations of one policy | a **contract the fake silently satisfies** and the real one does not |
| the shape | enumerate the domain, evaluate **both** sides, assert equality | construct **one** side for real, assert an effect only it emits |
| passes when | an inlined copy is equivalent **today** | never, if the real seam rejects the input the fake accepted |
| the motivating case | a resolver required to consume a shared predicate | `CriticalityJudge` over the real `ClaudePromptRunner` |

**An AGREEMENT test between a FAKE and a REAL implementation is worse than nothing** — it certifies that a
fake you wrote matches a real thing you never ran, and it reads on the page like an integration proof.
When the two sides are two implementations of one policy, you want AGREEMENT; when one side is the
production collaborator the run resolves, you want a real-seam test. Neither is a cheaper spelling of the
other, and neither discharges the other's row.

## Dispatch / factory wiring — the CORRECT concrete type is paired with the CORRECT mode (#158)

The **next failure past #120.** #120 asks "is the component wired at all?"; this asks "is it wired to
the **right** mode?". When a dispatch wires N enum/discriminated values to N concrete implementations
(`ImportMode.TcApiLocal → new TcApiLocalImporter()`, `ImportMode.CommanderRest → new
CommanderRestImporter()`), an agent can **swap the pairings** — route Mode B to the wrong importer and
Mode C to the other — and ship the feature **inverted**. The usual gates all stay green:

- the **build** passes (both concrete types exist and satisfy the interface, so either compiles in
  either branch);
- the **dispatch tests** pass — they inject a **substituted fake** (`RecordingImporter` / `FakeHandler`)
  via DI and assert only that *the registered `ICommanderImporter` was called*, never **which concrete
  type** was registered (seam-injection proves routing, not type identity);
- a **bare keyword check** (`if ($content -notmatch "ImportMode|TcApiLocal") …`) passes in the inverted
  case too — every enum value and every type name is present *somewhere* in the dispatch file regardless
  of how they're paired.

So the swap survives every check that doesn't bind a **specific enum value to a specific concrete type**.

### The archetype: one proximity check per enum→concrete pairing

For each of the N pairings, add **one** guardrail asserting `<EnumValue>` appears within a **bounded
window** (~300 characters — a single short `if` block) of `<ConcreteType>` in the dispatch file. Use a
**multiline-dotall** window `[\s\S]{0,300}` (matches across newlines), checked in **both orders** so the
declaration order inside the block doesn't matter — NOT single-line `.{0,300}` (which stops at the first
newline and false-fails a multi-line block):

```powershell
# catches: the wrong concrete importer wired to the TcApiLocal mode (e.g. swapped with
#          CommanderRestImporter) - build + seam-injected dispatch tests + a bare keyword
#          check all pass on the inverted wiring; only the per-pairing proximity check fails.
$file = "src/Commander/ImporterDispatch.cs"
$content = Get-Content $file -Raw
if ($content -notmatch "TcApiLocal[\s\S]{0,300}TcApiLocalImporter|TcApiLocalImporter[\s\S]{0,300}TcApiLocal") {
    Write-Output "TcApiLocal mode is not paired with TcApiLocalImporter in $file - verify the correct importer is wired to the correct ImportMode branch (a swap passes the build and the seam-injected dispatch tests)"
    exit 1
}
exit 0
```

Emit **one such check per pairing** (one for each `<EnumValue, ConcreteType>` couple). The 300-char
window covers a single short `if`/`switch`-arm block; **widen it** for a longer block, but keep it tight
enough that an *unrelated* later branch naming the other type can't accidentally fall inside the window.
Scope the grep to the **one dispatch file** the wiring task owns (grep-scope rule).

### WHEN to use it — both conditions must hold

Emit the pairing checks only when **both** are true; otherwise they are noise:

1. The task **selects among ≥2 concrete implementations by an enum / discriminated value** (a real
   dispatch with a real chance of a swap — a single implementation cannot be mis-paired); AND
2. the dispatch tests use a **substituted fake / seam-injection** (`RecordingImporter`, `FakeHandler`,
   an `InMemoryX` registered via DI) that proves *routing* but **not type identity** — so the swap is
   invisible to the test suite.

### DECISION GATE — omit when the tests already assert the concrete type

**If the dispatch tests assert the concrete TYPE NAME** (e.g. `Assert.IsType<TcApiLocalImporter>(...)`
on the object the dispatch resolved for Mode C, not merely that *an* importer was called), the test
**already catches the swap** and this guardrail is **redundant** — **omit it** and state why in the
`# catches:` comment of whatever guardrail covers the dispatch (e.g. `# pairing not separately checked:
DispatchTests assert IsType<TcApiLocalImporter> for Mode C, so a swap fails the tests`). Adding the
proximity check on top of a type-asserting test is duplicate coverage, not extra safety.

Relation to #120: composition-root wiring asks whether `FooImpl` is constructed/injected *at all*; this
asks whether — given it IS wired — each mode got the **right** impl. A plan can need both (wire the
dispatch into the production path AND prove each mode's pairing). A `.NET` realization is
`stacks/dotnet.md §10d`; this catalogue section is the universal archetype.

## Production testability seam — insert it upstream of the test-author task (#84)

A test-author task can correctly refuse to author a behavior it knows is **unsatisfiable as
architected**: the production code has no injection point, so the behavior can never be expressed
as a test that eventually goes green. The real run then halts `needsHuman` mid-run and forces a
human to hand-edit production code — defeating the "approve the guardrails once, then let it run"
model. The agent did the right thing (wrote the tests, confirmed them red, refused the unsatisfiable
one); the **breakdown** is what should have caught it, by inserting the seam as its own task.

**This is a sibling of #120 (composition-root wiring) at a different layer.** #120 is about
*production* injecting the *real* impl so the feature is live from the CLI. This (#84) is about
*tests* being able to inject a *fake/double* so a behavior is **expressible as a passing test at
all**. A plan can need both: a seam task (opens the injection point) AND, later, a wiring task
(production constructs and injects the real collaborator). Do not conflate them.

**Why the existing patterns do not cover this.** The compile-coupled-tests pattern (catalogue →
archetype #8 note: the test references a not-yet-existing **DTO/type** the implementation adds) works
when the missing symbol is something the **test constructs** — forcing the whole test file into a
compile failure is the correct red. It does NOT work when only **one behavior of several** needs the
seam: forcing the entire file red to satisfy behavior 3 would stop behaviors 1/2/4 — which are
runtime-testable against the existing surface — from compiling and failing as their own clean red. So
the seam belongs in its **own small upstream task**, not folded into either the test-author or the
implementation task (neither cleanly OWNs it as a verifiable deliverable otherwise).

**Decision rule — when does this fire?** While parsing a test-author behavior: **does expressing this
behavior as a test that can eventually PASS require a production-code seam that does not exist yet** —
a DI constructor overload, a factory delegate, an injectable interface, a fixture source? The
detection tell: the behavior injects a fake/double (`RecordingX`, `FakeX`, `InMemoryX`, a fixture
source) into a type currently constructed **only** via a production constructor with **no injection
point**. The action prompt's "if no seam exists, write `needsHuman` and stop" escape hatch is the
**last resort**, not the default — by run start the seam task should already exist.

**Two artifacts close it** (generated in SKILL.md Step 5):
1. **A production-seam TASK** — `NN-add-<component>-<seam>-seam`, a **pure structural production
   change**: add the constructor overload / factory delegate / injectable interface + its DI
   registration. **No behavior, no endpoint** — the seam only opens an injection point. Edge
   direction: the **test-author task `dependsOn` this seam task** (the seam is upstream; the tests
   compile against it), never the reverse. **TDD-exempt:** a seam is too simple for meaningful unit
   tests — state the exemption reason in the task description.
2. **A structural seam-exists guardrail** on the seam task — pairing `build-passes` (#3) with a
   **structural check that the seam exists** using the stack file's **declaration regex** (the new
   constructor signature / factory delegate / interface), **never a bare name grep** (this is the
   universal structural-vs-keyword rule, §"file-contains: structural vs. keyword matching"). Scope it
   to the one production file the seam task owns (grep-scope rule). The .NET realizations (constructor
   overload, factory delegate, injectable interface) are `stacks/dotnet.md §11`.

With the seam present, the test-author task authors **all** behaviors against the real injection
point: every behavior fails at runtime (the endpoint/feature is still absent) as a clean red, with no
`needsHuman`. The run stays autonomous.

**FORBIDDEN shapes** (the review skill hunts these):
- A test-author task expected to **invent the seam itself** (or to gesture vaguely at "add an
  injection mechanism") — neither it nor the implementation task cleanly owns the seam as a verifiable
  deliverable, and the `needsHuman` escape fires at run time.
- A **bare name grep** for the seam (`Select-String "Launcher"`) instead of the declaration regex — it
  passes on a comment, a `using`, or an unrelated mention (the structural-vs-keyword trap).

## Bulk / unbounded fan-out → scripted ETL, not an agent-per-item loop (#100)

When a task's deliverable is **"process N items where N is unknown and potentially large"** — a web
crawl/scrape, a bulk transform over an unknown-size glob, a mass API fetch, a dataset ETL — the wrong
model is an **agent-iterated loop**: one agent turn-budget covering N fetch+convert+write cycles. Agent
turns are the wrong unit for bulk work. A real run modeled a `portal-crawl` as "use Playwright to
enumerate the in-scope pages and produce a note per page"; the in-scope set turned out to be ~409
pages, the crawl sub-agent hit max-turns (50) and was killed, and the retry hit the same wall
identically — a hard dead-end (`action-failed` → retries fail → `needs-human`) that wasted a large
turn/$$ budget *discovering the wall*. This is a **task-structuring** failure, not a `maxTurns` one:
raising the turn budget (#94) only moves the wall, because bulk fan-out does not scale with turns at
all.

**Decision rule — when does this fire?** When a task fans out over an **external or unknown-size set**:
a website / section / sitemap, a recursive glob, an API listing — "every page under…", "all files
matching…", "each record in…". The tell is **cardinality the plan cannot bound at breakdown time**
("8 expected" → 409 actual). A retry-cheapness / one-session check on **"could this be hundreds of
items?"** trips the rule during sizing (SKILL.md Step 2).

**Structure it as a scripted bulk operation — three moves:**
1. **Scripted-ETL action (the volume goes off the turn budget).** The agent authors and runs **one
   `script`** that does the N-item work in a single execution (Playwright + HTML→markdown; a glob walk
   + transform). The agent's turns go to *writing, verifying, and running* the script — NOT to
   iterating items. This is a `script` action, not a `.prompt.md` that loops. Guard it with the
   ordinary script archetypes — `file-exists` (#1) on the output directory + `command-exit-code` (#2)
   or a count check — and verify the **recorded output** (verify-don't-replay, #9), not a re-run.
2. **Discover-size-first.** Where the set size is unknown, **enumerate/count before** committing to an
   approach, so sizing and any curation are calibrated to reality. This is a cheap upstream probe
   (enumerate the in-scope set, write the count to state or a manifest) that may be its own task
   feeding the ETL task.
3. **Split bulk-capture from per-item derivation.** Make the cheap, complete, **scripted capture** one
   task (deterministic, fits a session — dump all N items locally), and any **agent
   derivation/curation** a separate, **bounded** task over a *selected subset* — never "derive all N."
   "Scripted crawl dumps all 409 pages to local markdown" then "a curate task derives a high-value
   committed subset" is the shape, not one agent told to "crawl and curate 409 pages."

**Relation to siblings.** Complementary to corpus-completeness/substance guardrails (#99 — those
*verify* the captured output is complete and substantive; this *structures the task* so it can be
produced at all) and to `maxTurns` budgeting (#94 — necessary but insufficient; bulk fan-out is the
case where more turns never help). The decision-tree leaf is below; the .NET scripted-crawl shape is
`stacks/dotnet.md §12`.

## Entry-point wiring + the live smoke-test (server/executable plans) (#64)

A plan whose outcome is a **server or CLI executable** — "a CLI entrypoint that starts a
loopback HTTP server and serves a wizard", "prints a URL", "listens on a port", a `.csproj`
with `Microsoft.NET.Sdk.Web` or `<OutputType>Exe</OutputType>` — decomposes cleanly into
component tasks (scaffold the exe project, implement the launcher, implement the routes),
and **each component compiles and unit-tests green**. The terminal whole-solution build
passes too. Yet a real failure slips through every one of those checks: the `Program.cs`
that never instantiates the `Launcher`. The build is green, the unit tests pass, and the
server 404s everything — because **no task ever wired the entry point to the handler**, and
no guardrail ever ran the binary.

A library/test deliverable is fully covered by the TDD cycle (author tests → implement →
`specific-tests-pass`). An **executable** needs a third kind of check the unit tests
structurally cannot provide: *does running the binary produce the expected observable
behaviour?* `new Launcher().StartAsync()` being absent from `Program.cs` is invisible to any
unit test of `Launcher` — the type works; it's just never called. Two guardrails, on two
inserted tasks (SKILL.md Step 5), close the gap:

1. **Entry-point-wiring (structural grep).** A `file-contains` guardrail on the
   ENTRY-POINT file asserting it references the launcher type — `Program.cs` must mention
   (and start) `Launcher`. This is the universal "structural vs keyword" rule applied to the
   wiring point; the exact .NET regex is `stacks/dotnet.md §7`. It catches the green-build
   `Program.cs` that ignores the launcher. (It is necessary but not sufficient — a grep can't
   prove the wired call actually serves; that's the smoke-test's job.)
2. **Live smoke-test (archetype #7, port/endpoint-answers).** The only guardrail that
   verifies *the exe does what the plan says* rather than *the code compiles*. It STARTS the
   built binary as a background process, POLLS a known route (`/health`, `/current-step`,
   whatever the plan names) until it answers or a bounded timeout elapses, ASSERTS HTTP 200,
   and ALWAYS stops the process in a `finally` (so a failed poll still tears the process
   down). It owns its own start/stop — no separate launch-script ancestor is needed — but the
   route it polls must be produced by an ancestor (artifact-ancestry). The full
   cross-platform script (port handling, bounded poll, `finally` teardown, one actionable
   failure line) is `stacks/dotnet.md §8`.

**Determinism rules for the smoke-test** (it is a live process check, the flakiest archetype
— hold it to these or it poisons the run with false reds):
- **Bounded poll, not a fixed sleep.** Retry the route on a short interval up to a hard
  timeout; a server's warm-up time varies, so a single `sleep 2` is both slower and flakier
  than "poll every 250 ms for up to 15 s".
- **Teardown in `finally`.** The process MUST be killed on every exit path — pass, route
  failure, or exception — or a leaked server holds the port and every subsequent run fails.
- **Deterministic port.** Tell the binary which port to use (CLI arg / env var) and poll
  that exact port, OR parse the port from the URL the binary prints to its captured stdout.
  Never guess. A fixed well-known port risks a collision with a leaked prior run; prefer a
  port the plan fixes for the exe, or an ephemeral one the binary prints.
- **One actionable failure line.** "smoke-test: GET http://127.0.0.1:5005/health did not
  return 200 within 15s (last: connection refused)" converges; "smoke-test failed" loops.

This is **starts-and-serves verification ONLY.** Whether the served page is the *described
UI* — built at all, and returned as real markup — is the **UI-presence** archetype below.
The two compose: this smoke-test proves the exe serves *something*; the served-markup half
of UI-presence proves that *something* is the UI the plan described. Don't duplicate the
process management — the served-markup check *extends* this lifecycle with one body
assertion (see below).

## UI-presence — the described UI was built and is actually served (#66)

A plan whose outcome is **user-facing UI** — "serves a multi-step wizard to the browser",
"a page the user completes", "master/detail view", "tri-state tree", a screen the user
*sees and operates* — has a failure mode distinct from #64's. With #64 in place the binary
starts and a route answers 200; the unit tests pass; the build is green. And still **no UI
exists**: every task decomposed to a JSON HTTP endpoint or a unit test, not one produced an
HTML page, stylesheet, client JS, or a `wwwroot`. The shipped artifact is a working JSON API
with no human-facing frontend, and the run is 100% green because nothing ever asserted a UI
artifact. This is the **most expensive false-green the skill can emit** — a plan promising a
frontend that decomposes to zero frontend tasks.

#64 would only have *caught* that no real UI is served (its smoke-test asserts a 200, which a
JSON root satisfies); it never *builds* the screens. #66 ensures the work to build the UI is
generated in the first place, AND that a guardrail asserts the UI is present and served. The
fix is a **UI-implementation task** per described screen (SKILL.md Step 5) plus a **pair of
deterministic guardrails** — never a prompt-judge:

1. **Asset-exists (archetype #1, file-exists).** A static check that the page/asset the
   screen needs is present on disk (or as a declared embedded resource) — `wwwroot/wizard.html`,
   its stylesheet, its client JS. Scoped to the one file the UI task owns (grep-scope rule). It
   catches the green-build run where no frontend file was ever written. The exact .NET realization
   (`wwwroot/<page>.html` existence, or the embedded-resource manifest check) is `stacks/dotnet.md §9`.
2. **Served-markup-contains (archetype #7, EXTENDING the §64 smoke-test).** The deterministic
   proof that the served root returns the **real UI markup**, not a placeholder, a 404 body, or
   JSON. It reuses the smoke-test's exact lifecycle — start the binary, poll the UI route, tear
   down in `finally` — and adds **one assertion**: the response body **contains a known UI
   element/string** from the page (a heading, a known `id`/`data-` attribute, a wizard step
   label). Asserting HTTP 200 alone is not enough — a JSON API returns 200 from `/`. This is
   **not a second process manager**: fold the body assertion into the existing smoke-test
   guardrail so the process starts once; only stand up a separate one if no executable
   smoke-test exists. The known string MUST come from the markup the UI task produces
   (artifact-ancestry). The .NET realization (the §8 lifecycle with the body-contains assertion)
   is `stacks/dotnet.md §9`.

**Determinism is mandatory here.** UI-presence is *presence and wiring*, never *visual
quality*. The asset-exists grep and the served-markup string are both deterministic; a
prompt-judge "does this look like a good UI" is OUT OF SCOPE and forbidden — it is exactly
the subjective vibes the demotion gate rejects, and worse, it cannot catch the failure
(a frontend can "look good" and still bind to no backend; a present, wired page that
contains the asserted element is the deliverable). The cross-check that a *described* UI
mapped to *some* build-ui task lives in SKILL.md Step 7.0 (exit-criteria self-review).

## Corpus / aggregation completeness & substance (#99)

A task whose deliverable is **derived artifacts from a set of inputs** — doc mining, codegen
from a spec, API→docs, dataset import, schema→fixtures, a crawl/enumeration that produces one
output per page — has a failure mode the existing archetypes miss. `file-exists` and
`file-contains` (and `tests-fail-on-current-code`) cover **shape** and **anti-tautology**;
nothing covers the **completeness and substance of a derived corpus**. The result is the worst
kind of false-green: a run that is 100% green and **ships an empty or partial corpus** — worse
than a hard failure, because it *looks done*. Three concrete misses (all the same gap):

- **F1 — hollow artifacts.** `file-exists` + a required marker line (e.g. a `Source:` citation)
  passes a **one-line stub**. The deliverable is empty-but-shaped.
- **F2 — incomplete aggregate/index.** An index that references *one* output "resolves" and
  passes while omitting most of the corpus — silently blinding any consumer that navigates via
  the index.
- **F3 — shallow ingestion.** A crawl that captures 2 of N pages passes, because the guardrails
  verify "everything I *listed* exists," never "I listed *enough*."

Distinct from the comment-stripping family (#97/#98): that is about false **positives** on banned
constructs; this is about false **negatives** on hollow/incomplete derived corpora. It complements
`tests-fail-on-current-code` (anti-tautology for tests) with an anti-tautology for
*extraction/aggregation outputs*.

**The four guardrails.** For a derived-corpus task, add deterministic checks that assert:

1. **Input→output coverage (no silent drops).** Every input — a manifest entry, a source file, an
   enumerated URL — maps to an **existing** output artifact. Iterate the *input* set and fail on the
   first input with no corresponding output; never iterate the outputs (that only proves "what I
   produced exists," F3's blind spot).
2. **Per-output substance floor (anti-stub).** Each derived artifact exceeds a **minimal content
   floor** — e.g. ≥ N non-empty lines or ≥ N characters *beyond* the required boilerplate/marker —
   so a one-line stub (F1) cannot pass. Subtract the boilerplate before measuring, or the marker line
   itself satisfies the floor.
3. **Aggregate/index completeness (`produced ⊆ indexed`).** The index/rollup references **every**
   produced artifact, not just ≥ 1. Compute the produced set and the indexed set and fail if any
   produced artifact is absent from the index (F2).
4. **Ingestion lower bound (where knowable).** A sanity floor on **how many** inputs were processed
   — `count(outputs) ≥ N` — to catch a trivially shallow run (F3). Set N from the manifest/known
   corpus size when one exists.

```powershell
# catches: a derived-corpus run that ships HOLLOW or INCOMPLETE outputs while green - a one-line
#          stub per source (F1), an index naming only 1 of N outputs (F2), or 2-of-N pages
#          ingested (F3). Asserts input->output coverage, a per-output substance floor, index
#          completeness (produced subset of indexed), and an ingestion lower bound.
$inputs   = Get-Content "manifest.txt" | Where-Object { $_.Trim() }   # the input set (1 line per input)
$outDir   = "docs/derived"
$indexFile = "docs/derived/INDEX.md"
$minLines = 5                                                          # substance floor (tune per corpus)
$minInputs = 10                                                       # ingestion lower bound (tune)

# 4. ingestion lower bound
if ($inputs.Count -lt $minInputs) {
    Write-Output "only $($inputs.Count) inputs in manifest (< $minInputs) - corpus looks trivially shallow"
    exit 1
}
$produced = @()
foreach ($in in $inputs) {
    $slug = [IO.Path]::GetFileNameWithoutExtension($in)
    $out  = Join-Path $outDir "$slug.md"
    # 1. input -> output coverage (no silent drops)
    if (-not (Test-Path $out)) {
        Write-Output "input '$in' has no derived output at $out - a source was silently dropped"
        exit 1
    }
    # 2. per-output substance floor (anti-stub): non-empty lines beyond the Source: marker
    $body = Get-Content $out | Where-Object { $_.Trim() -and $_ -notmatch '^\s*Source:' }
    if ($body.Count -lt $minLines) {
        Write-Output "$out has only $($body.Count) substantive lines (< $minLines) - hollow stub"
        exit 1
    }
    $produced += $out
}
# 3. aggregate/index completeness: produced subset of indexed
$index = Get-Content $indexFile -Raw
foreach ($p in $produced) {
    $name = [IO.Path]::GetFileName($p)
    if ($index -notmatch [regex]::Escape($name)) {
        Write-Output "$indexFile does not reference produced artifact '$name' - index is incomplete"
        exit 1
    }
}
exit 0
```

**The honest limit — state it in the doctrine so authors don't over-trust the floors.** These are
**lower bounds, not faithfulness checks.** A deterministic guardrail can enforce "≥ N,
every-input-mapped, non-trivial size, fully indexed" — it **cannot** verify the extraction is
*content-faithful* or *semantically complete* (that the derived doc actually captures its source).
That residual needs a human pass or a demotion-gated prompt-judge (one paired with these
deterministic floors, never alone — the demotion gate below). The floors are **tunable per corpus**:
set N, the substance floor, and the ingestion lower bound from the known corpus size; when none is
knowable, drop guardrail 4 and say so in the breakdown report rather than inventing a number.

**Decision-tree leaf:** *deliverable = derived corpus / aggregate over a set of inputs* → add the
coverage + substance-floor + index-completeness guardrails (lower bounds; note the faithfulness
residual is human/judge work).

## The prompt-judge demotion gate

For EVERY candidate prompt-judge, ask all four. Any "no" → demote to a deterministic
archetype:

1. **Is the property genuinely subjective** (tone, clarity, taste)? If a regex,
   schema, or test could check it, it must.
2. **Is it paired with ≥ 1 deterministic guardrail** on the same task? A judge is
   never alone.
3. **Is the judge criterion-specific**, not vibes? "PASS iff the report names every
   failed task" — never "is this good?".
4. **Is it pointed at the raw artifact**, not at anything the action wrote about its
   own work? If the action can game it by writing a flattering summary, point the
   judge at the artifact itself.

The judge prompt must instruct: *you are a verifier; do NOT fix anything; write
`{"pass": bool, "reason": string}` to `GUARDRAILS_VERDICT_OUT`; the reason becomes
retry feedback, so make it actionable.* (The harness appends the full verdict
contract automatically — the prompt only needs the criterion. See
`examples/hello-guardrails/hello-guardrails/tasks/03-quality-check/guardrails/02-tone-is-friendly.prompt.md`
for the golden reference.)

## Failure detail must reach the retry tail (#179)

A guardrail's failure feedback is only as good as **what survives the tail.** When a guardrail
exits non-zero, the harness feeds the next attempt the **tail** of that guardrail's stdout — the
last ~60 lines, then the last 4000 chars (a fixed harness contract, `RetryPolicy.AppendTail`; a
guardrail cannot change the tail size). For most guardrails this is fine: a `file-contains` or
`build-passes` check prints **one** actionable line and that line IS the tail. The trap is a
**test-runner** guardrail.

**The trap.** Default / minimal-verbosity `dotnet test` (and many other runners) prints each
failure's **assertion message and exception/stack trace INLINE, mid-run**, then ends with only
`[FAIL] <name>` lines and a `Failed: N, Passed: M` count. A guardrail that does
`dotnet test … ; if ($LASTEXITCODE -ne 0) { Write-Output "tests failing"; exit 1 }` therefore puts
only the test **names** in the tail. The agent sees **WHAT** failed but not **WHY** — no
`Assert.Equal() Failure / Expected / Actual`, no `JsonException` path — and generates ineffective
retries. The motivating case (#179, plan-0009 task 10) consumed **12 attempts** and escalated to
`needsHuman` before a human ran the tests manually to read a two-line JSON error the tail had cut.

**The rule.** A guardrail that asserts a test suite **PASSES** must make the failure DETAIL
(assertion/exception text) the **LAST thing on stdout**, so the harness tail captures it — not just
the `[FAIL] <name>` summary. The robust, runner-order-independent form is **capture → emit the full
log → re-emit the failure-signal lines at the very end**, bounded so the re-emitted block fits the
~60-line tail in the common few-failures case, with one final actionable reason line beneath it:

1. Capture the runner's combined output (`$out = dotnet test … 2>&1`).
2. Emit the full log first (so the attempt's saved output is complete).
3. On failure, `Select-String` the failure-signal lines (`[FAIL]`, `Error Message:`, `Assert.`,
   `Exception`, `Stack Trace:`, `Expected:`, `Actual:`), bound them (~40 lines), and re-emit them
   under a clear header at the END.
4. Print the single actionable reason line last.

This is **deterministic** — it does not depend on logger ordering. You MAY *also* raise verbosity
(`--logger "console;verbosity=detailed"`, which moves failure messages into the end-of-run summary),
but the re-emit is the load-bearing part: it puts the detail in the tail even with several failures.

**But you may NOT LOWER it — a quiet flag defeats this rule on its own.** Measured on `dotnet test`:
under `-v q` the runner prints the `[FAIL] <name>` line and **nothing else** — no `Error Message:`,
no `Expected:`/`Actual:`, no `Stack Trace:`. There is then no detail in the output for step 3 to
re-emit, so a perfectly-written capture-and-re-emit guardrail still tails out test NAMES only. Quiet
flags belong on the **build** command, never on the test command of a check that asserts tests pass
(`stacks/dotnet.md §4.3`). Treat "is a verbosity flag suppressing the thing I am about to grep for?"
as part of the #248 verify-against-real-output habit.
The exact regex and the full PowerShell pattern live in the **stack file**
(`references/stacks/dotnet.md §4.2`); other stacks instantiate the same capture-and-re-emit shape
for their runner.

**Polarity — re-emit only where exit 0 is the pass.** This applies to every `tests-pass` /
`all-tests-pass` / `specific-tests-pass` realization (archetype #4) and to any test driving a
production seam (composition-root wiring). It does **NOT** apply to the **inverse** TDD-red checks —
`tests-fail-on-stubs` / `tests-fail-on-current-code` (archetype #8), where a **non-zero** exit is
the SUCCESS: there is no failure to feed back, and re-emitting would surface the EXPECTED red as if
it were a problem. Match the construct's polarity: re-emit failure detail only on the checks whose
pass is exit 0.

**Toolchain gotcha — `--nologo` is NOT a `dotnet run` flag (#194).** A dogfood/self-hosting plan
often validates a task folder against the freshly-built loader with
`dotnet run --project src/Guardrails.Cli -- validate <folder>`. `--nologo` is valid for `dotnet
build` and `dotnet test` but **not** for `dotnet run`: placed before the `--` it falls through to
`dotnet run`'s parser (or the app's), failing the guardrail before `validate` ever runs; placed after
the `--` it becomes a bogus CLI arg the app rejects. To quiet build chatter use **`-v quiet` before
the `--`**: `dotnet run --project <proj> -v quiet -- validate <folder>`. (Stack specifics live in
`references/stacks/dotnet.md`.)

## A `scope:"integration"` guardrail MUST be UNION-SAFE (#125)

This is an authoring constraint on **which assertion** a `scope: "integration"` guardrail may make.
Per SSOT **§4.3**, the run's integration-guardrail set re-runs at **EVERY union point** — every
fan-in and every **non-FF** plan-branch integration (SSOT §5.3 case B), on the merged bytes, *before*
the merge commit and *before any downstream action* — **not only in the terminal `<plan>/guardrails/`
folder**. The terminal gate and the per-union re-verify are **one mechanism at two scopes** (§4.3). So
an integration guardrail runs at moments when **downstream tasks have not run yet**.

**Where the tag is LIVE — exactly two folders.** The per-union set is built from the task
`tasks/<id>/guardrails/` folders **plus the plan-root `<plan>/guardrails/` folder**, and nowhere else. A
**WAVE root** (`<plan>/<wave>/guardrails/`) is NOT in that set: the tag there is **INERT** and `validate`
warns **GR2059** (#459). Everything in this section applies to the two live positions; at a wave root
the correct form is LOCAL, no `scope` key (see the wave entry/exit gate section).

**The full build and whole test suite on the terminal gate are NOT integration-scoped (#165).** It is
tempting to think "the whole-repo build + full suite ARE the integration set, so mark them
`scope: "integration"`." That is the **#125 anti-pattern**, not the rule. A full build and a full test
suite are **terminal postconditions**, not union-safe invariants: at an intermediate union in a TDD
plan, the merged bytes contain test files referencing types whose implementation task has not run yet,
so `dotnet build` / `dotnet test` FAIL on those merged bytes and the harness rolls the whole wave back
— even though every per-task guardrail passed. Apply the decision test (*"would this pass on a partial
merge with a downstream task unsettled?"*): a full build/suite answers **no**. So `01-solution-builds`
and `02-all-tests-pass` in the terminal `<plan>/guardrails/` folder are **LOCAL** (no `scope` key) —
they run only once, at the terminal gate on the merged HEAD, after every upstream task has merged, which
is the correct and ONLY moment for a full build + full suite. The real integration-set re-run that
**GR2028** requires in the folder is instead a **union-safe conditional invariant** (below) — the
**conflict-marker-freedom check**, never the build or the suite.

**What actually satisfies GR2028 — the two ungameable forms (#343).** GR2028 is credited **only** by a
**git-conflict-marker-freedom check** (the line-anchored `<<<<<<<` / `>>>>>>>` scan) **or** a recognized
whole-repo build/test/suite invocation. These two are ungameable by construction. A
content/**contribution-present** grep — "if token `X` is present, verify it's real" — alone does **NOT**
satisfy GR2028: it is **ADDITIVE**, layered on top of one of the two forms, never the sole content of the
terminal/exit gate. The reason is structural: the union-safe CONDITIONAL shape (#165, below) can never
*fail* when a merge **dropped** a contribution entirely — the gate goes false → pass — so a content-only
union check certifies **nothing** about whether the union integrated soundly. It is a per-contribution
tightening, not a union-soundness proof. (This is why `guardrails validate` rejects a content-topic-only
union guardrail with GR2028 even though it is a textbook union-SAFE conditional: union-safety and
GR2028-satisfaction are two different bars — a GR2028-satisfying guardrail must be BOTH union-safe AND
carry one of the two ungameable forms.)

**A CAPTURED build/test invocation counts as form 2 (#429).** The failure-detail-in-tail doctrine
(`stacks/dotnet.md` §4.2, #179) *requires* a tests-pass guardrail to capture the run — `$log = dotnet test
<sln> … 2>&1` — so the assertion/exception lines can be re-emitted LAST and reach the retry-feedback tail.
`validate` used to reject that exact shape as "no integration re-run", which left a terminal/exit gate
unable to satisfy both rules at once: an author had to drop the re-emit at the very gate where failure
detail matters most, or add a second file purely to be recognized. **The recognizer should not reject the
form another rule requires**, so a captured invocation is now credited. What is still rejected is a
*mention* rather than a run: `$msg = "dotnet test …"` (a quoted string) and `$out = echo dotnet test …`
(the output of an echo) credit nothing, as does a comment that merely names a build command.

**The rule: assert a union-safe INVARIANT, never a terminal POSTCONDITION.** A `scope:"integration"`
guardrail must assert something true of **any valid intermediate union** — an invariant like "every
produced file present is non-empty and conflict-marker-free", "the solution still builds", "the
already-merged tests still pass". It must **NOT** assert a **terminal postcondition** that only holds
once the *whole* plan has merged — "the final combined output exists", "the sink wrote its
aggregate", "all N contributors are present". A terminal postcondition **fails at an intermediate
union** where the producing task hasn't settled yet, turning a healthy partial merge into a spurious
`needs-human`.

**Surfaced live by `parallel-hello`:** a sink-postcondition gate ("the final combined output exists")
was marked `scope:"integration"` and **failed when the 2nd leaf settled as a union before the sink
ran** — the combined output legitimately did not exist yet. The fix was twofold, and it is the
template:

1. **Keep the integration gate union-safe** — re-scope it to an invariant ("any produced file present
   is non-empty and conflict-marker-free"), true at every union including the terminal one.
2. **Move the terminal assertion to a `local` guardrail on the sink** — "the final combined output
   exists" runs **in-attempt on the sink's own segment** (default `"local"` scope), where the sink's
   action has just produced it. A `local` guardrail runs only in the sink's attempt lifecycle, never
   at an upstream union, so it never fires early.

**Decision test (apply to every `scope:"integration"` guardrail):** *"If this ran on a partial merge
where a downstream task has not settled yet, would it pass?"* Apply it against **every union point
that can occur anywhere in the plan before the guardrail's own task has run** — not only unions
structurally upstream of that task in the DAG. A `scope:"integration"` guardrail re-verifies at
**every** fan-in plan-wide (§4.3 above: "no per-task or per-colliding-sibling guardrail selection at
a union"), so a merge by a **completely unrelated parallel sibling** counts just as much as a union
that feeds the guardrail's own ancestor chain. Checking only "does a union feed into MY task's
ancestors?" is the too-narrow version of this test, and it will miss exactly that case — this is
what actually happened live in review: two siblings with zero dependency on a composition-root
wiring task each merged back onto the plan branch (and re-verified, and failed, that task's
guardrail) before the wiring task had even started (#250). If **no**, it is a terminal postcondition
wearing an integration scope — demote it to a `local` guardrail on the task that owns the
postcondition, and (if needed) replace it with a union-safe invariant at integration scope. An
integration guardrail asserting a terminal-only postcondition is an **anti-pattern** (see the
anti-patterns list).

Do not fork the contract here — §4.3 (re-verify at every union) and §5.3 (FF vs union) are the SSOT;
this section is doctrine *about how to author within* that contract.

### The union-safe form is CONDITIONAL: "if X is present, verify it" — never "require X" (#165)

Because the guardrail re-runs at intermediate unions where only a SUBSET of contributing tasks has
integrated, every content check inside a `scope: "integration"` guardrail must be written as a
**conditional** — gate on the contribution being present, then verify it — so it passes trivially
before the contributing task has run:

```powershell
# Union-safe: gate-then-verify. Absent contribution → pass (the producing task hasn't run yet).
$path = Join-Path $ws 'src/Importers/CommanderLauncher.cs'
if (-not (Test-Path $path)) { exit 0 }          # nothing to verify at this union yet
$content = Get-Content -Raw -Path $path
$failures = @()
if ($content -match 'test-commander-rest') {     # the REST contribution has landed — now require it real
    if ($content -notmatch '"test-commander-rest"') {
        $failures += "test-commander-rest present only as comment — route string literal missing"
    }
}
if ($content -match 'ImportMode') {              # the dispatch contribution has landed — require the construct
    if ($content -notmatch 'ImportMode\.\w+|switch.*ImportMode|case ImportMode') {
        $failures += "ImportMode present only as comment — dispatch construct missing"
    }
}
if ($content -match '(?m)^<<<<<<<' -or $content -match '(?m)^>>>>>>>') {
    $failures += "conflict markers remain — the union did not cleanly integrate"
}
if ($failures.Count -gt 0) { Write-Output ($failures -join "; "); exit 1 }
exit 0
```

**Anchor the conflict-marker regex — and drop the bare `=======` (#187).** A real git conflict
writes its markers at **column 0** and always writes BOTH `<<<<<<<` (ours) and `>>>>>>>` (theirs).
Match them **line-anchored** (`(?m)^<<<<<<<` / `(?m)^>>>>>>>`) so only a real conflict trips the
check. The unanchored form (`-match '<<<<<<<'`) matches the char run **anywhere** on a line, and the
bare `=======` middle marker collides with legitimate content — a `======` banner, a Markdown setext
`===` header underline, an ASCII-art table rule — so an unanchored `=======` check red-halts a
**correct** parallel run over innocent content. Line-anchored `<<<<<<<`/`>>>>>>>` (git's labelled
ours/theirs markers) are false-positive-free; the `=======` separator is redundant given both are
required, so drop it entirely (even line-anchored it still collides with a column-0 setext underline).
Gold standard when the file is under git: `git diff --check` reports conflict markers directly.

> **Also enforced deterministically (GR2037, #346).** Because correct doctrine text here does not
> guarantee an LLM applies it every generation (a fresh breakdown regressed this exact fix to the old
> unanchored spelling), `guardrails validate` mechanically **rejects** the unanchored `<<<<<<<`/`>>>>>>>`
> forms (a 7-char ours/theirs run not line-anchored) via the banned-pattern registry
> (`references/banned-guardrail-patterns.json`, entry `#187a`; SSOT §4.6). It **complements** — does not
> replace — the #302 author-time smoke-test and the `/guardrails-review` adversarial pass; author the
> line-anchored form and it never fires. (The bare `=======` is retired from the good form by doctrine
> but is deliberately NOT banned by GR2037 — a `={7}` ban would false-fire on a setext underline / banner.)

The **unconditional** form (`if ($content -notmatch "test-commander-rest") { exit 1 }`) is the bug: it
fails at the intermediate union BEFORE the REST task has run, red-halting a healthy partial merge. The
conditional form is correct at every union — it tightens as each contribution lands and is fully
satisfied only at the terminal HEAD. This is the same shape as the conflict-marker check (which gates
on the file existing) and the overlapping-writeScope union-guardrail below.

### Overlapping writeScopes need a `scope:"integration"` union-guardrail on the shared file (#132)

The corollary of "the union re-verify is integration-set-only" (SSOT §4.3): the union re-verify runs
**ONLY** the `scope:"integration"` set — it does **NOT** re-run a colliding sibling's per-attempt
`local` guardrails (running `local` guardrails on arbitrary union bytes false-fails — fragment-readers
checking `GUARDRAILS_STATE_FRAGMENT`, anti-tautology `tests-fail-on-current-code`, not-yet-run
downstream tasks). So when an AI-merge resolves a union of **two tasks that both write a shared
file** (overlapping `writeScope`s — colliding siblings), a hunk the merge silently DROPS on that file
is re-verified at the union **only** if a `scope:"integration"` guardrail asserts the shared file's
**union invariant**. A drop catchable solely by a sibling's `local` guardrail is **not** caught at
the union (it surfaces at the terminal gate, or not at all — the accepted v1 residual, SSOT §4.3
"Accepted residual").

**The authoring rule (proactive — emit it when you generate colliding writeScopes).** Whenever the
breakdown produces **≥2 tasks with overlapping `writeScope`s on a shared file/path** (rare by
design — the disjoint-scope CHECK flags most such collisions as a plan-shape smell, prefer disjoint
scopes), author **one** `scope:"integration"` guardrail on the integration / fan-in task that asserts
the **union invariant** on that shared file: the merged file still holds **every** colliding sibling's
contribution (each sibling's distinctive marker / declaration is present, conflict-marker-free,
non-empty). This is exactly the texttools showcase's `components-union-verified` guardrail
(`05-integration-gate/.../03-components-union-verified.json`, `scope:"integration"`). Keep it
**union-safe** (#125 — assert "every contribution PRESENT in the union is intact", an invariant true
of any valid intermediate union, never a terminal "all N present" postcondition that false-fails on a
partial merge). When this union-guardrail also serves as a terminal/exit gate, it satisfies **GR2028
via its conflict-marker-freedom check** (above) — the contribution-present checks are the *additive*
tightening layered on top, never the GR2028-crediting content on their own (#343). The well-authored
plan covers the residual this way; `guardrails-review` emits a WEAK finding when colliding writeScopes
carry no such union-guardrail.

**Duplicate-definition sub-check on a shared CODE file (#175).** When the shared overlapping-`writeScope`
file is a **code file** and **both** colliding tasks could ADD a type/member DEFINITION to it (each
appends a `class`/`record`/`interface`/`enum`/method the other does not), the conflict-marker +
contribution-present checks are **not enough**. A 3-way / AI-merge of two branches that each appended the
**same** new definition to **different** regions of the file produces **no textual conflict marker** —
git keeps **both** copies — so the union-guardrail's conflict-marker check passes while the merged file
holds a **duplicate definition**. For C# that is a CS0101 ("already contains a definition for …") the
build catches only at the terminal gate — the exact #175 failure that red-halted plan-0009 (task 07 and
task 09 both defined `CommanderRestImporter` in `Launcher.cs`). Add a **duplicate-definition count check**
to the same `scope:"integration"` union-guardrail: for each definition both siblings could add, count
occurrences and fail when **>1**, naming the AI-merge duplicate. Keep it **union-safe/conditional** — run
it only inside the file-present gate, so it passes trivially at a union where the file hasn't landed. The
.NET realization counts the declaration with `[regex]::Matches($content, 'class\s+CommanderRestImporter').Count`
and fails on `-gt 1` (`stacks/dotnet.md §19`):

```powershell
# Inside the file-present gate of the union-guardrail (so it's union-safe):
$classMatches = ([regex]::Matches($content, 'class\s+CommanderRestImporter')).Count
if ($classMatches -gt 1) {
    Write-Output "Launcher.cs contains $classMatches definitions of CommanderRestImporter - the AI-merge produced a duplicate class (overlapping writeScopes); remove all but one"
    exit 1
}
```

The harness cannot generically detect a semantic duplicate (that is the build guardrail's job); at the
terminal gate it can only surface a **HEDGED** structural hint (#272) — it names the colliding `writeScope`
task pairs + the shared path as a *possibility to verify IF the reported failure detail (the PRIMARY
signal) looks merge-related*, NOT an assertion a collision occurred, since overlap alone is a **WEAK**
signal a stub+impl pair produces by design (SSOT §3.3/§3.4, #175/#272). The duplicate-definition check is
the authoring-side **prevention**: it catches the duplicate at the union, before the terminal gate. The deeper fix for
the plan-0009 case is the missing DAG edge that trapped the agent into redefining the class at all — see
the transitive-compilation-dependency rule (plan-breakdown Step 3 / `guardrails-review` §2, #176); the
duplicate-definition check is the union-side safety net when an overlap is genuinely needed.

<!-- BEGIN ADDED SECTION #76 — method-call anchoring (auto-merge friendly; do not merge into prose above) -->
## Method-call anchoring — match the call construct, not a bare method name (#76)

The **call-site sibling** of the structural-vs-keyword rule (§"file-contains: structural vs.
keyword matching", which covers *type/member declarations*). That rule says a check for "type
`IFoo` is implemented" must match the declaration, never the bare token `IFoo`. The same trap
exists for **method calls**: a guardrail verifying "file calls method `X` on type `Y`" that greps
a **bare method name** — `RunAsync\s*\(` — false-passes on three things that do NOT prove the real
library method is wired up:

- a **comment** that merely names it — `// then we call RunAsync(scope)`;
- a **local stub/wrapper** method that happens to share the name — `private void RunAsync(...)`;
- any unrelated method called `RunAsync` on a different type.

This is the call-site shape of the green-build false-pass: the guardrail goes green while the
specific library method it was meant to verify is never actually invoked. It surfaced on a
"CLI must call `MigrationRunner.RunAsync`" wiring guardrail written as
`(Get-Content $prog -Raw) -notmatch 'RunAsync\s*\('` — satisfied by a local `RunAsync` wrapper
or a `// RunAsync(scope)` comment, neither of which wires the real runner.

**Rule.** When a guardrail verifies "file calls method `X` on type `Y`," require **both** of two
sequential checks (each a separate `if` so the failure line names the missing half):
1. **A reference to the TYPE** — `TypeName` (or the stricter `TypeName\s*\.`) — rules out a local
   stub that shares only the method name.
2. **The call with a DOT prefix** — `\.MethodName\s*\(` — rules out substring matches in comments
   and standalone method *definitions* (a definition reads `void MethodName(` with no leading dot;
   a call reads `something.MethodName(`).

- **Pattern to AVOID** (matches comments, local stubs, any same-named method):
  `RunAsync\s*\(`
- **Pattern to USE** (two sequential checks — type reference, then dotted call):
  ```powershell
  # catches: a "CLI calls MigrationRunner.RunAsync(...)" wiring claim satisfied by a comment
  #          (// RunAsync(scope)) or a LOCAL method also named RunAsync - neither invokes the
  #          real library method. Require BOTH the type reference and the dotted call construct.
  $prog = "src/Migration.Cli/Program.cs"
  $content = Get-Content $prog -Raw
  if ($content -notmatch 'MigrationRunner') {
      Write-Output "$prog does not reference MigrationRunner - the runner type is never named (a local RunAsync stub would not wire it)"
      exit 1
  }
  if ($content -notmatch '\.RunAsync\s*\(') {
      Write-Output "$prog does not call .RunAsync(...) on an instance - only a bare/commented/locally-defined RunAsync would match without the dot"
      exit 1
  }
  exit 0
  ```

Apply whenever the plan says "task A must call `B.Method()`" where `B` is a specific type in
another project (the entry-point-wiring grep in §"Entry-point wiring" is the executable-specific
instance of the same idea — it already requires `new\s+Launcher\b|Launcher\s*\.\s*\w` rather than
a bare `Launcher`). For the strict-string-literal residual (a banned/expected method name sitting
inside a string), the same caveat as the comment-strip family applies: a regex is a lower bound, a
parser is out of scope — note it in the report if it matters. The .NET realization is
`stacks/dotnet.md §15`.
<!-- END ADDED SECTION #76 -->

<!-- BEGIN ADDED SECTION #74 — no-direct-bypass archetype (auto-merge friendly; do not merge into prose above) -->
## No-direct-bypass — an extracted library must write THROUGH its injected interface (#74)

The **inverse** of the two registration/reference seams (build-descriptor registration,
cross-module reference): those prove a library is *wired in* (registered in the solution, referenced
by the consumer). This proves a library does **not bypass its own abstraction** from the inside. A
library can be correctly registered, building, and passing its tests while still calling the
**concrete** dependency directly in its internals — bypassing the very `IInterface` it was extracted
to enforce. Registration, build, and tests-pass guardrails all go green; the bypass slips through.

It surfaced on an "extract migration-engine library" task: the library was registered, built, and
tested, but nothing prevented the extracted engine from calling `ToscaCloudClient.UploadEntitiesAsync`
directly — bypassing the injected `IDestinationWriter` entirely. The library's whole purpose was to
enforce the writer abstraction; without this guardrail the bypass is invisible to every other check.

**Rule.** When a task extracts a library that **must call through an injected interface rather than a
concrete dependency**, add a guardrail that scans the extracted project's `.cs` files for a **direct
call to the concrete method** and fails if it finds one. Two anchoring requirements (both from the
method-call-anchoring rule, #76 above — a bare-name grep here would *false-RED* on a comment, escalating
a correct library):
1. **Strip comments before the scan** (#97/#98 comment-blind family) — a `// we used to call
   UploadEntitiesAsync directly` comment must not trip the ban (false positive → whack-a-mole to
   `needs-human` on a correct library).
2. **Anchor on the dotted call construct** — `\.UploadEntitiesAsync\s*\(` (optionally `ConcreteType`
   nearby), not a bare `UploadEntitiesAsync`, so a same-named method on a *different* allowed type or a
   string literal does not false-RED.

Scope the scan to the **new library's project folder only** (grep-scope rule), excluding `bin`/`obj`:

```powershell
# catches: <LibraryProject> bypassing <IInterface> by calling <ConcreteClass>.<ConcreteMethod>
#          directly in its internals - registered, building, and tested all stay green while the
#          injected abstraction is bypassed. Strip comments first (so a comment naming the method
#          is not a false RED), then anchor on the DOTTED call construct, scoped to the library only.
$libDir = "PoC/ConformedSources/Migration.Engine"
$hits = Get-ChildItem $libDir -Recurse -Filter *.cs |
    Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' } |
    Where-Object {
        $raw  = Get-Content $_.FullName -Raw
        $code = [regex]::Replace($raw, '/\*[\s\S]*?\*/', ' ')   # /* */ block comments
        $code = [regex]::Replace($code, '//[^\r\n]*', ' ')       # // line comments
        $code -match '\.UploadEntitiesAsync\s*\('
    }
if ($hits) {
    $names = ($hits | ForEach-Object { $_.Name }) -join ', '
    Write-Output "$libDir calls .UploadEntitiesAsync(...) directly in [$names] - must write THROUGH the injected IDestinationWriter, not the concrete client"
    exit 1
}
exit 0
```

**Trigger:** the plan or action prompt contains language like "must NOT call `X` directly", "must
write **through** interface `Y`", "the current Exe bypasses the abstraction", or "the engine must
depend only on `IInterface`." This is a **forbidden-call** check (a ban), so it inherits both the
comment-blind caveat (#97/#98 — strip first) and the string-literal residual (a parser is out of
scope; note it if the concrete method name plausibly appears in a string). The .NET realization is
`stacks/dotnet.md §16`.
<!-- END ADDED SECTION #74 -->

<!-- BEGIN ADDED SECTION #75 — covers-key-behaviors guardrail (auto-merge friendly; do not merge into prose above) -->
## Covers-key-behaviors — a test-author task with an enumerated behavior list (#75)

A concrete instance of the **coverage-gap** anti-pattern (the action's stated completion criteria
exceed what the guardrails verify). When a test-author task's action prompt **enumerates specific
named behaviors** to encode — a numbered list under "encode these behaviors: 1. sub-processes are not
filtered 2. ProcessID keying 3. rollup counts" — the standard TDD pair (`tests-exist` +
`tests-fail-on-current-code`) verifies the file *exists* and *fails against current code*, but neither
verifies the **enumerated behaviors are actually present**. An agent can satisfy both with **one**
trivially-failing stub test and never encode behaviors 2–5.

**Rule.** When a test-author task's action prompt enumerates **3 or more** specific named behaviors to
encode, add a `03-covers-key-behaviors.ps1` guardrail that checks the test file for **2–3 of the most
distinctive terms** from the behavior list (one `if` per term, so the failure line names the missing
behavior). Scope the grep to the **one test file the task authors** (grep-scope rule).

**A `covers-*` token floor is a NAMING lower bound — never the gate that carries the enumerated-behaviour
claim.** Read that as part of the rule above, not as a caveat after it: the whole correction below exists
because this section used to stop at the paragraph you just read. One question decides what to emit:

| the test-author task | what carries the enumerated-behaviour claim |
|---|---|
| enumerates behaviours **and** authors a stub tree (a behavioural type — the usual case) | the **per-test red census** — every enumerated behaviour observed `Failed` in the runner's own result file. `covers-key-behaviors` ships **as well**, never instead: the census is what makes its lower bound worth having |
| enumerates behaviours with **no** stub tree (a pure data model, a documentation deliverable — §"Where it does NOT apply") | the token floor is genuinely what you have. Then **say so in `# catches:`** — word it as a lower bound, never as proof the behaviour is tested |

Do **not** reach for a rejection-shaped source regex (`Assert\.Throws` / `Assert\.False`) as the
strengthening move — it is simultaneously too strict and too weak, and taxonomy 14 says why.

```powershell
# catches: a test file that lacks coverage of <Behavior1> or <Behavior2> - both named in the action
#          prompt's "encode these behaviors" list - while tests-exist + tests-fail-on-current-code
#          both pass on a single trivially-failing stub test.
$f = "tests/Migration.Engine.Tests/SubProcessRollupTests.cs"
$content = Get-Content $f -Raw
if ($content -notmatch 'ProcessId') {
    Write-Output "$f does not test ProcessID keying - add a test asserting entities are keyed by ProcessId (behavior 2)"
    exit 1
}
if ($content -notmatch 'RollupCount') {
    Write-Output "$f does not test rollup counts - add a test asserting the parent's RollupCount aggregates its sub-processes (behavior 3)"
    exit 1
}
exit 0
```

**Term-selection rules:**
- Choose terms **distinctive** to the behavior — a domain type name, an enum value, a method name —
  **never** generic words like `test`, `assert`, `Fact`, or `should`, which any stub satisfies.
- Pick the **headline** behaviors most likely to be accidentally omitted (the ones the plan's risk
  section flags), not all of them — **2 checks per guardrail is usually enough**.
- Scope to the **one test file** the task authors (grep-scope rule, §"Grep-scope contamination").

**The honest limit — state it so authors don't over-trust the check.** This is a **lower bound**, the
same class as the corpus substance floors (#99): a distinctive term *present in the file* proves a
test *names* the behavior, not that it *asserts* it correctly — a term in a comment or an unused
variable still matches. It is a cheap guard against the "one stub for five behaviors" failure, not a
faithfulness check.

**The residual, CORRECTED — the old mitigation sentence was false (#375).** This section used to name
the mitigation for that residual as *"the `tests-fail-on-current-code` red plus human review."*
**That sentence was false as written, and #375 is the measurement of its falsity.** The red gate is
`dotnet test --filter … exits non-zero`, and non-zero fires if **ANY** test in the filter fails — so a
hollow `Assert.True(true)` **passes** on the pre-implementation tree and hides behind its
genuinely-failing siblings. A suite-level red proves *the suite as a whole is not yet satisfied*; it
proves **nothing about any individual test in it**. Measured on a security wave: `covers-security-matrix.ps1`
exited **0** against a test file that merely *named* every wire token (`stale`, `replayed`, `runId`,
`review-attested`, `proceed-unreviewed`) with `Assert.NotNull` / `Assert.True(true)` bodies, sitting
green beside a suite red. That is #479's own headline pathology one level down — *a pre-satisfied item
hides behind its siblings' failures* — and #479's fix was **per-item, not aggregate**. So is this one.

**What replaces it: the per-test red census** (§"The per-test red census — every manifested behaviour's
test observed FAILED, not merely discovered"). Every behaviour in the task's manifest must be observed
with outcome **`Failed`** in the **runner's own per-test result file** on the stub tree; the shipped
`specific-tests-pass` is the second side. Emit the census on the same test-author task whenever this
archetype's behaviour list is the thing being trusted downstream — it is not a replacement for
`covers-key-behaviors` but the gate that makes its lower bound worth having. **Human review remains the
residual only for the WRONG-assertion case** — a test that invokes the subject and asserts the *wrong*
invariant — never for the vacuous case, which is now deterministic.

The breakdown report (Step 7) should **list which
enumerated behaviors were NOT covered** by the key-behaviors guardrail, so the human reviewer can
decide whether to add checks. The .NET realization is `stacks/dotnet.md §17`.
<!-- END ADDED SECTION #75 -->

<!-- BEGIN ADDED SECTION #176 — negative assertion (auto-merge friendly; do not merge into prose above) -->
## Negative assertion — verify an EXCLUDED scenario is ABSENT (#176)

The **mirror** of `covers-key-behaviors`. That archetype checks a kept scenario is **present**
(`if ($content -notmatch "X") { … exit 1 }` — "X must be present"). The **negative assertion** checks
an **excluded** scenario is **absent** (`if ($content -match "X") { … exit 1 }` — "X must be absent").
Both are first-class deterministic archetypes; the polarity of the `if` distinguishes them.

**When to emit one.** Whenever a task's action prompt **explicitly excludes** a scenario/keyword the
deliverable must NOT contain — "Mode C / `CommanderRest` is wizard-blocked, do NOT include it in the
dispatch tests"; "the importer must NOT call the concrete writer directly"; "the read-only artifact
must NOT contain `MERGE`/`EXEC`". A presence-only coverage check says nothing about the excluded
scenario, so the agent can include the removed thing **undetected**. In plan-0009 the dispatch
test-author task's prompt removed `CommanderRest`, but no guardrail forbade it — the agent re-added it,
and that reference compiled-coupled the downstream wiring task to a type produced by a non-ancestor
(the #176 transitive-compilation trap). A single fail-on-present line would have caught it.

```powershell
# catches: a dispatch test file that references CommanderRest - Mode C is wizard-blocked and the action
#          prompt explicitly EXCLUDED it, but the positive covers-key-behaviors check only verifies the
#          KEPT scenarios are present, so a re-added CommanderRest would slip through undetected (#176).
$f = "tests/Importer.Tests/MigrateDispatchTests.cs"
$content = Get-Content $f -Raw
if ($content -match "CommanderRest") {
    Write-Output "$f references CommanderRest - Mode C is wizard-blocked and must not appear in the dispatch tests"
    exit 1
}
exit 0
```

**Pair it with the positive `covers-key-behaviors`**, do not replace it — the two are complementary
lower bounds: the positive check verifies the kept scenarios are named, the negative check verifies the
excluded one stays out. Scope both to the **one file** the task owns (grep-scope rule).

**GR2026 is (correctly) SILENT on a negative assertion (#177).** `guardrails validate`'s GR2026
stale-coverage lint flags only **POSITIVE require-present** coverage tokens — a token a `-notmatch …
exit` (fail-on-absent) or `-match … $hits++` (presence-counting) block requires to be **present** in
the authored file, which the action prompt is therefore expected to mention (SSOT §4.4). A negative
assertion's keyword is **intentionally absent** from the prompt (that is the whole point — it was
excluded), so GR2026 must NOT warn about it; a warning there is the #177 false positive that was fixed
by classifying match-line polarity. Do **not** weaken or delete a legitimate negative assertion to
silence a GR2026 warning — post-#177 there is none to silence. The .NET realization is
`stacks/dotnet.md §20`.
<!-- END ADDED SECTION #176 -->

<!-- BEGIN ADDED SECTION #470 — required ∧ forbidden token collision (auto-merge friendly; do not merge into prose above) -->
## A forbidden token must not collide with what the task REQUIRES (#470)

The **safety rule for every negative assertion above.** A guardrail can carry a required-present clause
and a forbidden-present clause whose tokens **collide**, making it satisfiable by **no file at all**.
Every attempt then fails identically, the retry feedback is coherent, actionable and **wrong**, and the
task dead-ends at `needs-human` having never been achievable. Reading did not reveal it: the two clauses
were 40 lines apart and **each is individually correct**.

### The measured instance — the required attribute's own string literal carries the banned token

```powershell
# line 25 — REQUIRED
if ($content -notmatch '\[Trait\s*\(\s*"Category"\s*,\s*"TierResolution"\s*\)\s*\]') { $failures += '…no trait…' }

# line 66 — FORBIDDEN (a correctly-motivated #176 negative assertion)
if ($content -match 'TierResolver|TierResolution') { $failures += '…references TierResolution - FORBIDDEN…' }
```

Both clauses fire on the **same character sequence**. Keeping the trait fails clause 2; removing it fails
clause 1. Task 06 authored the wave's conformance suite and tasks 07 → 08 → 09 depended on it — one
unsatisfiable regex would have dead-ended the whole chain after paying task 06's full retry budget.

### The rule — two clauses, both non-optional

**When a task carries BOTH a required-present and a forbidden-present clause:**

1. **The forbidden scan runs over STRIPPED source — comments *and* string literals.** #97/#98 is written
   about **comments**; this is the same fix family one step wider. The banned token hid in an **attribute's
   string literal**, which no comment-stripper touches.
2. **Anchor the ban on a USE, not a mention (#76).** Ban the construct the prompt actually forbids — a
   dotted call, a type position, an enum member, a declaration — never the bare word.

The applied fix, re-measured over **8 cases** (correct suite GREEN; the token in a **comment** GREEN; the
token in a **string** GREEN; a real `TierResolver.Resolve(…)` call RED; `TierResolution` used as a **type**
RED; eight-of-nine names RED; missing trait RED; file absent RED) — **teeth intact, false-RED gone**:

```powershell
# catches: a conformance suite that USES the forbidden resolver - while leaving the word free in prose,
#          comments, string literals and test NAMES, so the REQUIRED [Trait("Category","TierResolution")]
#          attribute (whose own string literal carries the token) can still satisfy its clause (#470).
$raw  = Get-Content $f -Raw                                 # never matched against
$code = [regex]::Replace($raw,  '/\*[\s\S]*?\*/', '')       # comments gone -> POSITIVE clauses read $code
$code = [regex]::Replace($code, '(?m)//.*$', '')
$scan = [regex]::Replace($code, '"""[\s\S]*?"""', '""')     # raw strings   -> FORBIDDEN clauses read $scan
$scan = [regex]::Replace($scan, '@"(?:[^"]|"")*"', '""')    # verbatim
$scan = [regex]::Replace($scan, '"(\\.|[^"\\])*"', '""')    # ordinary — kills the Trait's own value

# REQUIRED reads $code, so the trait's string literal is still there to satisfy it.
# -cnotmatch (taxonomy 3): a REQUIRED identifier clause must be CASE-SENSITIVE, or `[trait("category",…)]`
# false-GREENs a file C# would not even compile.
if ($code -cnotmatch '\[Trait\s*\(\s*"Category"\s*,\s*"TierResolution"\s*\)\s*\]') {
    Write-Output "$f carries no [Trait(""Category"", ""TierResolution"")] attribute on the suite class"
    exit 1
}
# FORBIDDEN reads $scan, anchored on a USE — a dotted call or a type position, never the bare word
if ($scan -match 'TierResolver\s*\.|(?<![\w.])TierResolution(?![\w"])') {
    Write-Output "$f USES TierResolver/TierResolution - the DoR forbids the resolver in this suite"
    exit 1
}
```

That is the **two-variable rule** (demotion-gate section) applied: one strip, two levels, and the two
polarities reading different levels — which is what makes the required and forbidden clauses coexist
instead of cancelling.

### The second axis — a forbidden token the task's OWN PROMPT uses

The collision above is **guardrail ↔ itself**. The near-miss variant sits on the **prompt ↔ guardrail**
axis and cost a full attempt in a live run: a guardrail banned `(?i)\bUnavailable\b` over **raw** content
while the task's own action prompt used that exact word **three times** — twice as the concept under
discussion, once as the thing not to invent. The agent echoed the prompt's vocabulary and attempt 1 died.

It **is** satisfiable (attempt 2 wrote the suite without the word), so this is **trap-shaped, not
unsatisfiable** — but the prohibition as written banned an ordinary English word rather than the thing
actually forbidden, which was the **enum member**. Same fix family, narrowed to the construct over
stripped source:

```powershell
if ($scan -match 'PromptFailureKind\s*\.\s*Unavailable|(?m)^\s*Unavailable\s*[,}]?\s*$') { … }
```

**The authoring check:** for every fail-on-present guardrail, **grep the paired `action.prompt.md` for
the banned token.** A hit means either narrow the guardrail to the construct, or change the prompt's
wording — the agent must never be invited to write the very thing that reds it.

### Do not confuse this with GR2026 — the two are opposite polarities

Stated side by side so neither is read as a revert of the other (#177):

> **GR2026 (positive):** the guardrail REQUIRES a token the prompt **never mentions** → the task can never pass.
> **This rule (negative):** the guardrail FORBIDS a token the prompt **does use** → the agent is invited to write the very thing that reds it.

Both are *"prompt and guardrail disagree about a token"*; they differ only in polarity, and **each is
silent in the other's healthy case**. #177 correctly made GR2026 silent on negative-assertion keywords —
"the prompt doesn't mention this banned token" is the normal, healthy case. Do not weaken a legitimate
negative assertion to satisfy either rule.

**Why the existing probes miss the guardrail-internal collision.** Each is a *single-clause* judgement
and both clauses are correct in isolation: #97/#98 is written about comments (the collision was a
**string literal in an attribute**); #176 *asks for* the forbidden clause and says nothing about checking
it against the file's required content; #302 finds it only if someone executes the script with a
**complete** valid sample. The mechanical same-file lint (the GR2026 analogue — a required literal that
trips the same file's forbidden pattern is a **proof** of unsatisfiability, not a heuristic) **SHIPPED as
`GR2057`**: `validate` fires it when ONE subject variable carries both a required-present literal and a
forbidden-present pattern that the literal trips.

**GR2057 does not retire this section.** It is deliberately silent wherever it cannot PROVE the collision,
and every one of those gaps is doctrine's: clauses over **DIFFERENT subjects** (the two-variable
`$code`/`$scan` fix this section mandates — which GR2057 must NOT flag, or the prescribed fix would be
punished), compound `-and`/`-or` conditions, interpolated or composed patterns, anchored forbidden
patterns, `.sh` guardrails, and both the **cross-file** and **prompt↔guardrail** axes. A green `validate`
means the provable case is clear, not that the pair agrees — run the two-direction check regardless.
<!-- END ADDED SECTION #470 -->

<!-- BEGIN ADDED SECTION #96 — producer<->consumer name-convention seam (auto-merge friendly; do not merge into prose above) -->
## Name-convention seam — producer files ⟷ consumer lookup by a derived name (#96)

The **third sibling** of the two "independent build passes, but the link is unverified" seams
(build-descriptor registration §1, cross-module reference §2). Here task A **produces artifacts** whose
names a consumer (task B, or a runtime component) **resolves by a derived or mapped name** — not a
literal path B already hard-codes: a URL → embedded resource, a step id → filename, a key → file, a
route → handler, a message-type → schema file. `file-exists`/`file-contains` on A and content checks on
B both pass while the **naming contract between them is never exercised**: B can derive a name A never
produced (case / separator / special-case drift) and fail **only at runtime**.

It surfaced as a real runtime bug a clean guardrail suite passed end-to-end. A browser wizard's step
fragments were produced kebab-case (`wwwroot/steps/source-connection.html`, and the `DestinationSelection`
step served by `destination.html` — an outlier, not `destination-selection.html`); the shell requested
fragments by the **PascalCase step id** — `GET /wizard/pages/SourceConnection.html` → embedded resource
`…wwwroot.steps.SourceConnection.html` → **404 → silent fallback**. Every guardrail passed because each
side was verified **independently**: file-exists + file-contains on each fragment (✅ the kebab files
existed), the shell's stepper/order were correct (✅), and the smoke test asserted step *order*, not
fragment *fetchability* (✅). Nothing verified **the consumer can resolve the producer's artifacts by the
name it derives at runtime** — the single most expensive class of false-green for UI / transport /
convention-heavy plans, because the failure is invisible to the whole suite and surfaces only on the
first real run.

**Rule.** When a task's artifacts are consumed by a name the consumer **derives** (a fetch-by-name, a
reflection / embedded-resource lookup, a convention-based file map, a route→handler resolution), add an
**integration guardrail that DRIVES the real lookup** end-to-end and asserts resolution succeeds for
**every** item in the set. Three properties — each load-bearing:

1. **Consumer-driven names.** Derive the lookup names from the **consumer's own mapping** — parse or run
   the shell's real `STEPS`/`FRAGMENTS` map, the route table, the message-type registry — **never a
   hard-coded copy** of the contract in the test. A test-side copy of the naming convention hides a
   *consumer-side* drift: it would test the test's idea of the names, not the consumer's.
2. **Cover EVERY item.** Iterate the *whole* set (all steps / all message types / all routes), not a
   sample — the drift is typically a **single special case** (the `destination.html` outlier). One
   un-checked item is exactly where it hides.
3. **Drive the real lookup, assert a per-item success marker.** Exercise B against A's *actual*
   artifacts (start the server and `GET` each fragment **through the shell's own map**; or invoke the
   real resolver) and assert **200 + a per-item content marker** — never a 404 / silent-fallback body.
   Resolution succeeding ≠ a 200 from a fallback page; assert a marker that only the *correctly resolved*
   artifact contains.

**Placement — both sides must be present.** This belongs on a task where **producer AND consumer
coexist** — a terminal / integration task (the whole-suite gate or a dedicated end-to-end task), never
on the producer task or the consumer task alone (on either, the other side isn't there to drive). Mark
it `scope: "integration"` — and keep it **union-safe** (#125): "every producer artifact present resolves
through the consumer's derived name" is an **invariant** true of any valid intermediate union where both
sides are present, so it is a legitimate integration-scope assertion, **not** a terminal postcondition.
(If a plan can reach a union where the producer set is only *partially* present, scope the assertion to
"every artifact **that is present** resolves" so a partial merge does not false-RED — the drift you are
hunting is a *wrong-name* failure, not a *missing-file* one, so a present-set invariant still catches it.)

This is **starts-and-resolves** verification — distinct from #64 (the exe *serves something*) and the
#66 served-markup check (the served root is *the described UI*). Compose them: #64 proves the server
answers, #66 proves the root is the UI, and this proves **every derived-name lookup across the set
resolves to the right artifact**. The .NET realization (parse the consumer's embedded-resource / route
map, drive each through the live server, assert 200 + per-item marker) is `stacks/dotnet.md §18`.

**Decision-tree leaf:** *task A's artifacts are resolved by task B (or a runtime component) via a
DERIVED/MAPPED name (not a literal path B hard-codes)* → add a **consumer-driven integration guardrail**
on a both-sides-present task that drives the real lookup for **every** item and asserts 200 + a per-item
marker (union-safe; the per-side independent file-exists / content checks do NOT cover the seam).
<!-- END ADDED SECTION #96 -->

<!-- BEGIN ADDED SECTION #221 — prose-only prohibition, no structural backing (auto-merge friendly; do not merge into prose above) -->
## Prose-only prohibition — an explicit "do NOT …" needs a matching structural guardrail (#221)

Generalizes the **negative assertion** archetype above (#176) from "an excluded SCENARIO/keyword" to
any explicit forbidden APPROACH or SHAPE the action prompt states, and states the authoring doctrine as
its own rule rather than a per-instance archetype pick. When a generated action prompt writes an
explicit prohibition — "do NOT wrap this in a retry loop," "do NOT weaken this assertion," "do NOT use
approach X" — the prohibition itself is not a guardrail. It is prose one implementer (an adversarial
one, or a merely lazy or wrong one) is free to ignore, while the guardrails that actually gate task
completion say nothing about it.

**The rule.** For every explicit "do NOT …" statement written into a generated action prompt, ask: *is
the forbidden behavior structurally checkable* — a regex, a count, or a shape/AST test on the file the
task modifies? If **yes**, emit a guardrail enforcing the prohibition alongside the prose — never rely
on the prose alone. If **no** (a genuine judgment call with no mechanical proxy — "do NOT make the fix
uglier than it needs to be"), say so explicitly in the breakdown report rather than silently leaving it
unguarded; an unguarded, unacknowledged prohibition is a coverage gap the human reviewer has no way to
notice.

**Why this is sharper than an ordinary coverage gap — the perverse-incentive angle.** An ordinary
coverage gap means a completion criterion goes unverified — the guardrails are silent where they should
speak. A prose-only prohibition can be WORSE than silent: when the surrounding guardrail is
EMPIRICAL/statistical rather than structural, the guardrail can actively **REWARD** the forbidden
shortcut instead of merely failing to catch it. That is not hypothetical — it is exactly how this
pattern was found (a real adversarial review pass, not a synthesized example):

**Worked case — hardening a flaky concurrency test (`WorktreeProviderSeamTests`).** The plan's action
prompt stated two explicit prohibitions with no backing guardrail:

1. *"do NOT weaken any assertion to tolerate fewer than 3 arrivals (e.g. changing `Assert.Equal(3, …)`
   to `Assert.True(… >= 2)`)."* The cheapest wrong implementation is exactly the forbidden weakened
   assertion — and it is **perverse**: the plan's own empirical guardrail ("run the test N times, assert
   it always passes") becomes **EASIER** to satisfy with the weakened assertion, because it now
   tolerates the very race the plan exists to fix. The statistical guardrail doesn't just fail to catch
   the shortcut — it pays the shortcut a higher pass rate than the honest fix.
2. *"do NOT wrap the test body in a retry-until-pass loop."* The cheapest wrong implementation is
   exactly the forbidden retry-until-pass wrapper — it brute-forces a ~50%-per-try race into a high
   per-outer-iteration pass rate with zero real fix, and nothing in the guardrail suite structurally
   distinguishes it from a genuine fix (both converge to "usually passes").

Both were closed with small, cheap, purely structural guardrails once named:

```powershell
# catches: an implementation that "fixes" the flaky test by WEAKENING one of the four load-bearing
#          assertions (e.g. Assert.Equal(3, ...) -> Assert.True(... >= 2)) so the test tolerates the
#          very race it exists to catch. Lock the assertions to survive VERBATIM.
$f = "tests/Worktree.Tests/WorktreeProviderSeamTests.cs"
$content = Get-Content $f -Raw
$required = @(
    'Assert\.Equal\(3,\s*arrivals\.Count\)'
    # ... the other three load-bearing assertions, one regex each
)
foreach ($pattern in $required) {
    if ($content -notmatch $pattern) {
        Write-Output "$f no longer contains the load-bearing assertion matching '$pattern' - do not weaken an assertion to tolerate fewer arrivals than the race actually produces"
        exit 1
    }
}
exit 0
```

```powershell
# catches: an implementation that "fixes" the flaky test by wrapping the test body in a
#          retry-until-pass loop instead of fixing the race - it brute-forces a high pass rate with
#          zero real fix, and an empirical N-run guardrail cannot tell the difference. Assert the
#          method calls the driving async method EXACTLY ONCE and contains no for/while/catch.
$f = "tests/Worktree.Tests/WorktreeProviderSeamTests.cs"
$content = Get-Content $f -Raw
$method = [regex]::Match($content, '(?s)public async Task ConcurrentArrivals_.*?\n    \}').Value
$calls = [regex]::Matches($method, '\bAcquireWorktreeAsync\s*\(').Count
if ($calls -ne 1) {
    Write-Output "$f drives AcquireWorktreeAsync $calls times in the test body - expected exactly 1 (a retry-until-pass loop brute-forces the race instead of fixing it)"
    exit 1
}
if ($method -match '\b(for|while|catch)\s*\(') {
    Write-Output "$f contains a for/while/catch construct in the test body - do not wrap the test in a retry-until-pass loop"
    exit 1
}
exit 0
```

**This generalizes past concurrency tests.** Any generated prompt with "do NOT do X" where X is
checkable has the same shape — the intent is real, stated in the one place (prose) an agent is free to
ignore, while completion is gated by guardrails that never mention it. Treat every such prohibition as
a candidate guardrail, not decoration on the prompt.

**Relation to the negative assertion archetype (#176).** Negative assertion is the KEYWORD/SCENARIO
special case of this rule (a `-match "<token>" → exit 1` fail-on-present check). This rule is the
general authoring doctrine: it also covers forbidden *shapes* (a retry loop, a weakened assertion, a
banned control-flow construct, a call-count invariant) that a single keyword match cannot express, and
it names the check-for-a-backing-guardrail step as mandatory authoring practice rather than one
archetype among many. Emit a negative assertion when the prohibition is keyword-shaped; reach for a
count/shape check (regex-lock, call-count, forbidden-construct scan) when it is not — either way, the
prohibition gets a guardrail or an explicit "not structurally checkable" note in the report.

**Decision-tree leaf:** *the action prompt contains an explicit "do NOT …" statement* → ask whether the
forbidden behavior is structurally checkable (regex/count/shape test on the modified file). YES → emit
a guardrail enforcing it (a negative assertion for a keyword/scenario, a regex-lock or shape/count check
for a forbidden approach), placed alongside the prohibition. NO → state so explicitly in the breakdown
report — never leave it silently unguarded.
<!-- END ADDED SECTION #221 -->

## The decision tree (apply per task)

**Gate the whole tree on the demotion order (#468).** Before reading a leaf, ask: *is this invariant a
claim about what the code DOES at runtime, or a structural fact about the build/wiring graph?* Behaviour
→ a test (rung 1) or an AGREEMENT property test (rung 2). Structure → a regex (rung 3), and the report
says why no test could carry it. The tree's leaves name the archetype; the gate decides whether a
source-shape leaf is admissible at all.

```
What is the task's primary deliverable?
├── A file/artifact            → file-exists (always) + the strongest content check available:
│                                schema-validates > file-contains-regex > prompt-judge
├── Code (library/feature)     → build-passes + specific-tests-pass (--filter THIS task's tests)
│                                + writeScope on the IMPLEMENTATION task that EXCLUDES the test
│                                │  files (SSOT §3.4): the harness's deterministic read-only
│                                │  write-scope check then catches an implementation that edits the
│                                │  tests instead of fixing the code — see SKILL.md Step 5 and the
│                                │  writeScope test-exclusion rule below
│                                └─ INSERT a test-author task upstream BY DEFAULT (SKILL Step 2
│                                   TDD rule); skip only if tests already exist or behavior is
│                                   too simple for unit tests — state why in task description.
│                                   The test-author "red" guardrail SPLITS on the type under test
│                                   (stub-based TDD section above — the SSOT):
│                                   • BEHAVIORAL type (class/method/logic) → the test-author task
│                                     ALSO writes the minimal STUBS so the tests COMPILE, its
│                                     writeScope covers the test file AND the stub file(s), and its
│                                     guardrails are build-passes (3) + tests-fail-on-stubs (8). The
│                                     IMPLEMENTATION task's writeScope EXCLUDES the test file but
│                                     TARGETS the stub file(s) (fills logic over the skeletons).
│                                     If the prompt ENUMERATES behaviours, emit the red as the
│                                     PER-TEST CENSUS (#375) — every manifested behaviour observed
│                                     Failed in the runner's result file, not a suite exit code.
│                                   • DATA MODEL (enum/record/value type — no behavioral stub) →
│                                     COLLAPSE the split into one define-type-and-assert-tests-pass
│                                     task; state "data model — no behavioral stub possible". If you
│                                     keep the split, note the anti-tautology is weaker, keep
│                                     tests-fail-on-current-code (8), and strengthen
│                                     covers-key-behaviors with a STRUCTURAL [Fact]/[Theory] check.
│                                   • MIXED (data + behavioral) → lean BEHAVIORAL (stub the
│                                     behavioral parts so the whole file compiles).
├── A runnable script/tool     → file-exists + command-exit-code on a representative invocation
├── A running service /        → entry-point-wiring (grep: the entry point references the launcher)
│    server / CLI executable      + port/endpoint-answers (#7: START the binary, POLL a route,
│                                 ASSERT 200, STOP in a finally) — the ONLY check that the exe
│                                 starts and serves vs merely compiles; see the entry-point-wiring
│                                 section below + stacks/dotnet.md §7–§8
├── A user-facing UI            → asset-exists (#1: the page/asset file is on disk, e.g.
│    (screen/page served to       wwwroot/<page>.html, scoped to the UI file) + served-markup-contains
│    the browser)                 (#7 EXTENDING the smoke-test: same start/poll/teardown, assert the
│                                 body contains a known UI string — NOT just 200, which JSON satisfies).
│                                 INSERT a build-ui-<screen> task per screen, ALONGSIDE the backend
│                                 that serves it. Deterministic only — NO prompt-judge on visual
│                                 quality; see the UI-presence section below + stacks/dotnet.md §9
├── A component injected at a  → composition-root wiring (#120): INSERT a wiring task that
│    production composition       constructs FooImpl and injects it into the production assembler
│    root (IFoo + FooImpl, a      (factory / Program.cs / DI / RunCommand), + a guardrail that
│    factory/DI/Program.cs must   asserts it is ACTUALLY wired — drive the real assembler and
│    construct + inject)          assert observable output (strongest), or reflect on the
│                                 constructed object for the non-null collaborator with a
│                                 contrast case (the Factory_Wires* shape). NEVER inject the seam
│                                 INTO THE ASSEMBLER'S OWN SLOT here — a guardrail that hands the
│                                 assembler a FooImpl it was supposed to construct proves nothing
│                                 about production wiring. Read that prohibition as SLOT-SPECIFIC:
│                                 it is about the ASSEMBLER's slot only, and the #382 leaf below
│                                 REQUIRES injecting into the COMPONENT-UNDER-TEST's own constructor
│                                 (same verb, different slot — D12; see "drive-the-real-seam").
│                                 NEVER trust terminal whole-suite green to cover wiring.
│                                 See the composition-root section + stacks/dotnet.md §10
├── A component whose TESTS    → drive-the-real-seam contract test (#382): the component's OWN
│    substitute an IN-PROCESS     implement task carries a test that drives the ACTUAL in-process
│    seam the production run       seam the production run drives, faking ONLY the process/CLI
│    drives (an IPromptRunner,     boundary underneath — NEVER the in-process seam itself. A TDD
│    the executor, the             guardrail that injects a FAKE of that seam goes GREEN over a
│    scheduler, a factory)         component that is broken through the real composition root: a
│                                  green light over a broken wire (passing-but-blind). Real-path
│                                  proof is DISTRIBUTED to each component's own task, never
│                                  concentrated in one terminal wiring sink — that concentration is
│                                  what the #378 over-scope WARN detects wearing a size costume.
│                                  Record a SEAM-LEDGER row per substituted in-process seam and put
│                                  each proof at its recomputed T*. Injecting into the COMPONENT's
│                                  own constructor here is REQUIRED and is NOT the #120 violation
│                                  above (D12 — different slot). Process seams (child process, CLI,
│                                  socket, HTTP, DB, filesystem) need no row.
│                                  See the drive-the-real-seam section + stacks/dotnet.md §10e
├── A derived corpus /         → coverage (every input maps to an output) + per-output substance
│    aggregate over a set of      floor (anti-stub) + index completeness (produced ⊆ indexed) +
│    inputs (doc-mine, codegen    ingestion lower bound (#99). LOWER BOUNDS, not faithfulness —
│    -from-spec, crawl, import)   the semantic residual is human/judge work; see the corpus /
│                                 aggregation completeness section above
├── Config/data                → schema-validates; else file-contains on load-bearing keys
├── State output (a key a      → fragment-key-present (read $env:GUARDRAILS_STATE_FRAGMENT,
│    downstream task reads)      parse JSON, assert the key non-null + non-empty; allowed-set
│                                check if a downstream task branches on the value)
├── Docs / prose               → file-exists + file-contains (required headings/terms). There is NO
│                                behavioural rung to demote into (prose cannot be run), so instead:
│                                every demanded token must have a PRECEDENT in the target artifact
│                                (#468 — two greps settle it), accept BOTH forms where both are
│                                legitimate, and the two-sided sample pair is EXEMPT (no meaningful
│                                invalid sample of a design doc exists) — NAME the exemption in the
│                                report. prompt-judge ONLY for genuine subjective quality, never alone
├── Bulk / unbounded fan-out  → scripted-ETL archetype (#100), NOT an agent-per-item loop: ONE
│    (crawl/scrape, recursive    `script` action does the N-item work in a single run (volume off
│    glob, API listing, ETL —    the turn budget) + file-exists/command-exit-code/count on its
│    "process N items, N         output; INSERT a discover-size-first probe where N is unknown; SPLIT
│    unknown & maybe large")     scripted bulk-capture from a BOUNDED per-item curation task. Raising
│                                maxTurns does NOT help. See the bulk-fan-out section + stacks/dotnet.md §12
├── Test needs an injection   → INSERT an upstream production-seam task (#84): add the DI ctor
│    seam to express a behavior  overload / factory delegate / injectable interface + DI registration
│    (a fake/double injected      (pure structural change, no behavior), guarded by build-passes +
│    into a type with no          a STRUCTURAL seam-exists check (declaration regex, never a bare
│    injection point)             name grep). The test-author task dependsOn it. Distinct from #120
│                                 (which injects the REAL impl in production). See the
│                                 production-testability-seam section + stacks/dotnet.md §11
├── Extracted library that must → no-direct-bypass (#74): scan the LIBRARY project's .cs (strip
│    write THROUGH an injected     comments first, scope to the lib folder, exclude bin/obj) for a
│    interface (not the concrete   DOTTED call to the concrete method (\.ConcreteMethod\s*\() and
│    dependency directly)          FAIL if present — registration/build/tests all pass over a bypass.
│                                 Trigger: "must NOT call X directly" / "write through interface Y".
│                                 See the no-direct-bypass section + stacks/dotnet.md §16
├── Test-author task whose      → covers-key-behaviors (#75): in ADDITION to tests-exist +
│    action prompt enumerates     tests-fail-on-current-code, add a check for 2–3 DISTINCTIVE terms
│    ≥3 named behaviors to        from the behavior list (domain type / enum / method name, never
│    encode                        generic words), scoped to the one test file. LOWER BOUND, not a
│                                 faithfulness check; report which behaviors went unchecked. See the
│                                 covers-key-behaviors section + stacks/dotnet.md §17
│                               AND (behavioral type, stub tree present) emit the red as the PER-TEST
│                                 CENSUS (#375): every enumerated behaviour bound to a PINNED test
│                                 name and observed Failed in the runner's own result file. The
│                                 covers-* floor alone is naming-only — a hollow Assert.True(true)
│                                 clears it and hides behind its failing siblings in a suite red.
│                                 See the per-test-red-census section + stacks/dotnet.md §4.4
├── "Task A must call          → method-call anchoring (#76): TWO sequential checks — reference the
│    B.Method()" on a specific    TYPE (rules out a local same-named stub) AND the dotted call
│    type in another project      (\.Method\s*\(, rules out comments + standalone definitions). NOT a
│                                 bare Method\s*\( grep. See the method-call-anchoring section + §15
├── "X must USE Y" — must      → AGREEMENT property test (#468), NOT a regex: enumerate the input
│    consume / go through /      domain and assert the two sides AGREE for every input, naming the
│    share / not diverge from    disagreeing one on failure. An inlined copy that is equivalent today
│    a shared predicate,         PASSES (correctly) and FAILS the moment it drifts — the only moment
│    policy, formatter, table    the rule matters. No regex can express that; three successive ones
│                                 failed on the measured case. Distinct from #76 above: that asserts a
│                                 CALL SITE exists, this asserts BEHAVIOURAL agreement. Falls back to a
│                                 regex only when one side is not callable from a test (a build
│                                 descriptor, a wiring fact). See the AGREEMENT property-test section
├── Producer files ⟷ consumer  → name-convention seam (#96): a CONSUMER-DRIVEN integration guardrail
│    lookup by a DERIVED/mapped    on a both-sides-present task — parse the consumer's real map, drive
│    name (url→resource, step     the lookup for EVERY item, assert 200 + a per-item marker (not a 404/
│    id→file, key→file,           fallback). Union-safe (#125). Per-side file-exists/content checks do
│    route→handler)               NOT cover the seam. See the name-convention-seam section + §18
└── Refactor (no new behavior) → build-passes + existing-tests-still-pass (the suite IS the guardrail)
```

**Plan-level (not a per-task deliverable): BROWNFIELD → a positive-baseline preflight CHECK in
`<plan>/preflights/` per area (#181).** Orthogonal to the per-task tree above, ask once **per touched
area**: does the plan modify a project that already has tests in that area, and does the worth-it gate
pass (target pre-exists, MODIFIES-not-creates, deterministic + cheap, strictly narrower than the
terminal gate, ≥2 work tasks build on the area)? If yes, EMIT one
`<plan>/preflights/01-baseline-<area>-tests-green.ps1` per area — a guardrail-shaped FILE (no task, no
action) running the EXISTING area tests **via `--filter`** (NEVER the whole suite — a whole-project test
hits the #165/#176 compile-coupling trap and false-reds) and asserting they pass (#179-re-emit form),
deduped one-per-area. The plan-root `preflights/` folder runs ONCE, BEFORE the DAG, against the starting
repo, so it gates every task with no edges to author ("never build on red"). If greenfield (new project
/ no existing tests in the area) or the worth-it gate fails, skip it and state why; never emit a vacuous
or whole-suite baseline. See the baseline-green / start-from-green (preflight) section above;
`stacks/dotnet.md §21`.

**Verify-recorded-result vs. replay (the Code/Refactor branches).** When the *action
itself* already ran the expensive build+test (e.g. a `dotnet build; dotnet test` action)
and recorded a GOOD target — a produced artifact or a runner-written TRX — prefer
**verify-recorded-action-result (#9)** over a guardrail that re-runs the same command.
This is a speed/flake trade-off, sound only against output the action could not fabricate;
when no such recorded target exists, keep the honest replay (`specific-tests-pass`, #4).
See the verify-recorded-action-result section above for the GOOD-vs-BAD-target rules.

**`writeScope` test-exclusion — doctrine (replaces the removed `tests-untouched` triad).**
Test-file integrity is now a **deterministic, read-only harness check**, not a hash-compare
guardrail. The TDD pair declares two scopes: the **test-author task** owns its test files in
`writeScope`; the **implementation task's** `writeScope` EXCLUDES the test files. After the
implementation action runs and before its own guardrails, the harness diffs the task's segment
worktree and asserts every changed path is in scope (SSOT §3.4) — an edit to a test file falls
outside the implementation's scope, fails the check, and retries with feedback naming the
out-of-scope paths. There is no `captureHashes`, no `Get-FileHash` recompute, no `restoreOnRetry`,
and no downstream `tests-untouched` guardrail. The check belongs implicitly to the task whose scope
is declared — the implementation task that must not write the tests — because it runs against that
task's own diff at the moment that task is verified, catching the edit on the exact task that could
have made it. Worktree isolation (physical) + this write-scope check (deterministic) together
replace the `captureHashes`/`tests-untouched`/`restoreOnRetry` triad, with no shared-state hashes to
forge and so no cross-task poisoning surface to defend.

**State-output leaf — the fragment-key contract.** When a task's action publishes a key
to the state fragment (written to `GUARDRAILS_STATE_OUT`) that a downstream task later
reads from its merged snapshot (`GUARDRAILS_STATE_IN`), the *file/build* guardrails do
NOT cover the state hand-off: the action can produce its on-disk artifact yet never write
the key, and the downstream task then runs with a null value. Add a guardrail on the
producing task that reads the not-yet-merged fragment from `GUARDRAILS_STATE_FRAGMENT`
(the env var guardrails get — see schemas.md §5.1), parses it as JSON, and asserts the
key is present, non-null, and non-empty. If a downstream task *branches* on the value,
also assert it is in the allowed set.

```powershell
# catches: action produced its artifact but never wrote the state key a downstream task reads
$fragmentPath = $env:GUARDRAILS_STATE_FRAGMENT
if (-not $fragmentPath -or -not (Test-Path $fragmentPath)) {
    Write-Output "no state fragment written - 'tsw_mechanism_recommended' key is missing"
    exit 1
}
$fragment = Get-Content $fragmentPath -Raw | ConvertFrom-Json
$value = $fragment.'01-research-tsw-write-mechanism'.tsw_mechanism_recommended
if ([string]::IsNullOrWhiteSpace($value)) {
    Write-Output "state key 'tsw_mechanism_recommended' is missing, null, or empty"
    exit 1
}
$allowed = @('rest-api', 'file-drop', 'sdk')
if ($allowed -notcontains $value) {
    Write-Output "state key 'tsw_mechanism_recommended' = '$value' is not in the allowed set ($($allowed -join ', '))"
    exit 1
}
exit 0
```

Drop the allowed-set block when no downstream task branches on the value. The fragment the
producing action writes namespaces the value under the producing task's **FOLDER NAME** as the
single top-level key (`{ "01-research-tsw-write-mechanism": { "tsw_mechanism_recommended": … } }`),
matching the fragment convention (schemas.md §6.2) — **the folder name, NOT the `stableId`** (an
internal regeneration token the harness rejects as a foreign key). This guardrail indexes the
fragment under that **same folder name** (`$fragment.'01-research-tsw-write-mechanism'` above), so
the producing prompt and this guardrail must agree on the folder name as the key.

Per task: **minimum 1, typical 2–3, soft max 4** guardrails. Order them
**cheapest-first** by filename (`01-exists`, `02-builds`, `03-tests`, `04-review`) —
the default `failFast` mode stops at the first failure, so a cheap existence check
should fail before an expensive test run or a paid judge ever starts.

## Anti-patterns (the review skill hunts for these — don't generate them)

- **Constrains the SHAPE of correct code, not the OUTCOME** (#479/#481): the guardrail is satisfiable
  only by writing the implementation a particular way, so a *correct* implementation written any other
  way goes RED. This is the single most expensive authoring defect measured to date — **three fired in
  one live run**, each costing retries, and one came within a single attempt of dead-ending a task
  whose work was already right. Three measured spellings, all of which read perfectly well on the page:
  - **Statement-bounded proximity.** `'"guardrails"[^;]{0,200}?prompt\.md'` — required two tokens in
    ONE statement. The natural implementation hoists the directory into a local
    (`string dir = Path.Combine(taskDir, "guardrails"); … Path.Combine(dir, name + ".prompt.md")`),
    putting a `;` between them. The agent wrote exactly that and was told it had never written the
    file at all. **Fix: key on a token the outcome implies** — here `'(?<!action\.)prompt\.md'`, which
    distinguishes the new guardrail file from the pre-existing `action.prompt.md` however the path is
    assembled.
  - **A literal call in a body.** Requiring `MarkupLine|WriteLine` inside an observer method rejects a
    leaf that delegates to a private render helper. **Fix: accept any call** (`\w+\s*\(`) — an empty
    body still fails, which is the actual requirement.
  - **Vocabulary the target artifact never uses.** Requiring the C# type name `AttemptJudge` in a
    schema document that names **zero** journal record types — its two sibling objects are documented
    as `"provenance": {` and `"usage": {`. A correct delta in the document's own house style could not
    pass. **Fix: accept the artifact's form, or both** — `'(?:"judge"\s*:|AttemptJudge)'`.

  **The test, before you write any token-presence probe:** *can a correct implementation be written
  that this rejects?* If yes, you are constraining shape. Ask what the token proves, then key on that.

- **Demands a token with no PRECEDENT in the target artifact** (#468/#479): the vocabulary case above,
  generalised into a check you can run in seconds and without executing anything. **For every literal
  token a guardrail demands of an EXISTING artifact, point at a sibling precedent in that same
  artifact.** If the analogous prior thing is written `"usage": {`, a probe demanding `AttemptUsage`
  contradicts the artifact's settled conventions and is wrong however reasonable it reads. This matters
  most for **documentation deliverables** (an SSOT-landing task, a contract doc), where there is no
  behavioural proof to demote the check into — prose cannot be executed, so a token-presence regex is
  simultaneously the *only* available form and the most defect-prone one. Two greps of the target file
  settle it. Where both forms are legitimate, accept both rather than dictating one.

- **Source-shape regex standing in for behavioural proof** (#468): the guardrail asserts a property of
  **implementation source** when the invariant is a claim about what the code **DOES at runtime** — a
  test could have carried it and did not. Measured over three review rounds and five agents on one
  breakdown: the test layer was **never broken by any agent in any round**, and **every blocker lived in
  the source-shape layer**, including **5 regressions introduced while fixing earlier rounds**. The
  headline evidence: against a tree carrying the type declarations and **no wiring at all**, a 14-clause
  grep manifest went **10/14 green** — a `bool NoRoute` property satisfied *"the no-route outcome
  exists"*. **A grep manifest measures vocabulary, not capability.** Fix: run the source-shape demotion
  gate above — behavioural proof, then an AGREEMENT property test for *"X must use Y"*, then a regex only
  when the property is genuinely unobservable at runtime, with the reason stated in the breakdown report.
  Not a blanket ban: a build-descriptor registration, an entry-point-wiring grep, or a negative assertion
  is a structural fact with no runtime proxy and stays. BLOCKER when the check false-reds a correct
  implementation; WEAK when it merely certifies vocabulary a test should have certified.
- **A source-shape guardrail with no committed two-sided sample pair** (#468/#302): a `file-contains`
  check over implementation source shipped without `tasks/<id>/samples/NN-check.valid.<ext>` /
  `.invalid.<ext>`, so nothing re-runs when the script is next edited. (The samples live in a `samples/`
  sibling, **never inside `guardrails/`** — the loader would treat them as guardrails and execute them.)
  Every defect in the taxonomy was **one
  execution away from discovery**; what was missing was that the execution had to be re-run **after every
  edit**, and it never was — which is how a raw-vs-stripped inconsistency fixed in round 1 was re-broken
  by the round-3 rewrite of the same file. Fix: commit the pair and re-run **the whole battery** after any
  edit, not just the case just fixed. **Exemption — documentation deliverables**: no meaningful invalid
  sample of a design doc exists, so the pair is NOT required; the **PRECEDENT check** above is the
  mandatory substitute, and the report names the exemption. A CODE guardrail gets no such hatch: if you
  cannot write its invalid sample, you do not yet know what it catches (the `# catches:` rule failing).
- **A required-present clause that is PRE-SATISFIED on the baseline tree** (#478): the clause's token
  already appears in its own subject before the task runs, so the task can satisfy it by doing nothing
  and it certifies nothing for the life of the plan. **The sample pair cannot reveal this** — both halves
  are synthetic files — and **neither can the exit code**: a guardrail has many clauses and one exit code,
  so a green clause hides behind its siblings' failures and the script still exits 1. Three shipped in one
  wave (`.prompt.md` already twice in the target file; `Judge` already 5× as `CriticalityJudge`; a
  proximity window matching a pre-existing line), each surviving a full review pass. Fix: **measure the
  baseline count per required-present clause and record it in the script** — zero, or a named reason it is
  not. A `# … appears nowhere else` comment with no number behind it is the defect, not the evidence.
  (Section → "Every required-present clause records its MEASURED baseline count".)
- **Early-exit clause chain in a MULTI-clause guardrail** (#478/#179): `if (…) { Write-Output …; exit 1 }`
  repeated per clause instead of an accumulator dumped at the end. It reports **one gap per attempt**, so
  an N-clause guardrail can burn N model invocations learning what one run already knew; it makes the
  compounding rule bite (fix clause 1, discover clause 2, re-break clause 1); and it leaves
  `/guardrails-review` Probe A₂ no per-clause verdict to read, forcing every clause into a hand-run
  census. Fix: accumulate, one distinguishable message per clause, dump the list once. **Legitimate
  early exits: a PRECONDITION (the subject is missing or unparseable, so every clause below would crash),
  and an expensive behavioural stage placed after the dump** — both stay, both named in the header comment.
- **Executed-test COUNT as an adequacy floor** (#468): a guardrail asserting *"at least N tests
  executed"* as a proxy for coverage. The runner counts **theory data rows, not behaviours** — one
  `[Theory]` with six `[InlineData]` rows clears a 6-test floor while proving one behaviour, and raising
  N does not fix it. Fix: a **behaviour manifest**, one clause per required behaviour — and read the
  manifest with the **per-test red census** predicate (*observed `Failed` on the stub tree, then observed
  `Passed` after*), not with name discovery: `--list-tests` asks only *"does a test with this name
  exist?"*, which a hollow body satisfies exactly as a comment satisfies a token floor (#375). The
  manifest **ratchets** either way. Not to be confused with the #455 **zero-match
  guard**, which asserts `>= 1` test executed to prove the filter selected something; that count is
  legitimate and stays. See the count-floor section above.
- **Vacuous test body — a suite-level red certifying a hollow test** (#375): a test named for the
  behaviour whose body asserts a tautology **passes** on the stub tree and hides behind its
  genuinely-failing siblings, so `tests-fail-on-stubs` (a *suite* exit code) reports the file honest and
  the `covers-*` token floor reports it covered. Measured on a security wave: five load-bearing
  invariants pinned by `Assert.NotNull` / `Assert.True(true)`. Fix: the **per-test red census** — every
  manifested behaviour observed `Failed` in the runner's own result file. **Not** a rejection-shaped
  source regex, which false-reds a correct `Assert.Equal(RejectedStale, r.Outcome)` and is satisfied by
  one tautological `Assert.Throws<NotImplementedException>` line. See the per-test-red-census section.
- **The 13 measured source-shape failure shapes** (#468): a named battery — declaration-satisfies-call,
  truncating body extraction, case-mismatch with the language, inconsistent stripping across siblings,
  raw-vs-stripped inconsistency inside one script, modifier-order fragility, under-inclusive negation,
  name-locking a free choice, vacuous token, one-line omnibus evasion, count floor, declaration-is-not-
  behaviour, control characters from the authoring pipeline. Each was found by **executing** a guardrail
  and each reads plausibly on the page. The table (what each did, what to anchor on instead, and which
  are already covered by #76/#97/#98/#112/the PRECEDENT rule) is in the demotion-gate section above —
  `/guardrails-review` probes them by name rather than re-deriving them.
- **Required token that the same guardrail also FORBIDS** (#470): a required-present clause and a
  forbidden-present clause whose tokens **collide**, so the guardrail is satisfiable by **no file at
  all** — every attempt fails identically with coherent, actionable, wrong feedback, and the task
  dead-ends at `needs-human` having never been achievable. Measured: a required
  `[Trait("Category", "TierResolution")]` whose own **string literal** carries the token a later clause
  forbids; the two clauses sat 40 lines apart and each was individually correct, so reading did not
  reveal it. Its near-miss sibling is **trap-shaped rather than unsatisfiable**: a fail-on-present clause
  banning a word the task's **own action prompt** uses (measured: `(?i)\bUnavailable\b` banned raw while
  the prompt used the word three times — cost one full attempt). Fix: run the forbidden scan over
  **STRIPPED** source (comments **and** string literals) and anchor it on a **USE, not a mention** — ban
  the construct, never the English word; and grep the paired `action.prompt.md` for every banned token.
  BLOCKER for the collision (unsatisfiable), WEAK for the prompt-vocabulary trap. See the forbidden-token
  collision section above.

- **Tautological**: the guardrail checks something the action writes specifically to
  satisfy it ("status.txt contains DONE"). The action controls the evidence.
- **Hollow output assertion** (#73): a terminal/e2e guardrail that asserts only the
  **absence of an error** — `Assert.Equal(0, x.Count)`, `Assert.NotNull(...)`, a bare
  `exit 0`, or the mere *presence* of an assertion keyword
  (`Assert.*\([^)]*(Moved|Written|Count|Entities)` matches `Assert.Equal(0, …)`) — for a
  task whose deliverable is a **non-empty quantity of output** (migration moved-count, items
  written, entities produced). It certifies a no-op: a run that moved zero entities goes
  green. Fix: require a **strictly positive** value — `(>\s*0|>=\s*1|NotEmpty\s*\(|True\s*\([^)]*Count\s*>\s*0)` —
  or better, read the runner-recorded count / state key and assert `> 0`. See the
  positive-effect / non-hollow assertion section above.
- **Accessor-order-sensitive structural regex** (#112): a property "declared/removed"
  check keyed on a fixed leading accessor — `…NAME\s*\{\s*get` or `…\{\s*set` — is itself a
  finding. C# accessor order is free (`{ get; init; }` ≡ `{ init; get; }`), so the regex
  **false-passes a removal check** when the field survives as `{ init; get; }` (an incomplete
  refactor ships green) and **false-fails a declared check** symmetrically. Fix: match the
  declaration up to the brace (`public\s+TYPE\s+NAME\s*\{`), order-insensitive by construction;
  if accessor presence matters, test `(get|set|init)` anywhere inside the block. See the
  member-order-insensitivity note above; exact regex `stacks/dotnet.md §3.1`.
- **Comment-blind keyword scan** (#97, #98): a forbidden-keyword guardrail (read-only / no-shell /
  no-eval) that calls `Get-Content $f -Raw` and matches banned keywords against the **raw file
  including comments** — it false-POSITIVES on a comment, a string literal, or disabled code that
  merely *names* the banned construct. The poison case: the action prompt asked the agent to write a
  **safety-header comment** listing the banned keywords ("performs no MERGE/EXEC, no xp_cmdshell"),
  and the guardrail flags those keywords in the header — sending a **correct** read-only artifact to
  `needs-human` via whack-a-mole (each retry strips one mention and exposes the next). A BLOCKER
  pattern: not a wrong implementation passing, but a correct one failing permanently. Fix: **strip the
  source language's comments before matching** (SQL: `/* */` then `-- …`; blank-in-place preserving
  newlines for line-number-reporting checks). And don't pair a header-documenting prompt with a
  comment-blind grep — that is a guaranteed false positive. See the comment-blind keyword-scan section
  above; per-language syntax `stacks/dotnet.md §11`.
- **Hollow / incomplete derived corpus** (#99): a derived-corpus task (doc mining, codegen-from-spec,
  crawl→one-output-per-page, dataset import) whose guardrails verify only **shape** — `file-exists` +
  a marker line — so it ships a green run over an **empty or partial** corpus. Three tells: a
  one-line **stub** passes a marker check (F1); an **index** naming only 1 of N outputs "resolves"
  (F2); a crawl that captured **2 of N** pages passes because the checks verify "what I listed
  exists," never "I listed enough" (F3). Worse than a hard failure — it *looks done*. Fix: add the
  four completeness/substance guardrails — input→output coverage, per-output substance floor
  (anti-stub), index completeness (`produced ⊆ indexed`), and an ingestion lower bound — noting they
  are **lower bounds**, not faithfulness checks (the semantic residual is human/judge work). See the
  corpus / aggregation completeness section above.
- **Terminal-postcondition at integration scope** (#125): a `scope:"integration"` guardrail that
  asserts a **terminal postcondition** ("the final combined output exists", "the sink wrote its
  aggregate", "all N contributors present") instead of a **union-safe invariant**. Per SSOT §4.3 the
  integration set re-runs at **every** union point (every fan-in / non-FF integration, §5.3 case B),
  on partial merges where downstream tasks have **not run yet** — so a terminal postcondition
  spuriously fails at an intermediate union and escalates a healthy partial merge to `needs-human`
  (surfaced live by `parallel-hello`). Fix: keep the integration guardrail to an invariant true of
  any valid intermediate union ("any produced file present is non-empty and conflict-marker-free");
  move the terminal assertion to a `local` guardrail on the sink (runs in-attempt on the sink's own
  segment, where the output exists). Decision test: *"would this pass on a partial merge with a
  downstream task unsettled?"* — if no, demote to `local`. See the union-safe section above.
- **Overlapping writeScopes with no integration union-guardrail** (#132): ≥2 tasks with **overlapping
  `writeScope`s on a shared file** (colliding siblings — AI-merge territory at the union) and **no**
  `scope:"integration"` guardrail asserting that shared file's **union invariant**. The union re-verify
  is **integration-set-only** (SSOT §4.3) — it does NOT re-run a sibling's per-attempt `local`
  guardrails (they false-fail on union bytes), so a hunk an AI-merge silently DROPS on the shared file
  is caught at the union ONLY by an integration-scoped guardrail; a drop catchable solely by a sibling's
  `local` guardrail is the **accepted v1 residual** (not caught at the union). Fix: author one
  `scope:"integration"` union-guardrail on the integration / fan-in task that asserts the merged shared
  file holds every sibling's contribution (each distinctive marker present, conflict-marker-free) —
  union-safe (#125), like the texttools showcase's `components-union-verified`. Prefer **disjoint**
  writeScopes (the disjoint-scope CHECK flags the collision); emit the union-guardrail when the overlap
  is genuine. See the overlapping-writeScope union-guardrail section above. WEAK — an authoring nudge,
  not a harness bug. **When the shared file is CODE both siblings define into**, that union-guardrail
  must ALSO carry a **duplicate-definition count check** (`[regex]::Matches($content,'class\s+<Name>').Count
  -gt 1`, union-safe) — a 3-way merge keeps both copies of an appended definition with no conflict marker
  (CS0101), the #175 residual the harness can only attribute at the gate (SSOT §3.3). See the
  duplicate-definition sub-check in the overlapping-writeScope section; `stacks/dotnet.md §19`.
- **Excluded scenario left unverified** (#176): a task whose action prompt **excludes** a
  scenario/keyword ("do NOT include `CommanderRest`", "must NOT call `X` directly") but whose guardrails
  carry only PRESENCE checks (`covers-key-behaviors`) and no **negative assertion** for the excluded
  keyword — so the agent can re-add the removed scenario undetected (the plan-0009 #176 trap). Fix: emit
  a fail-on-present negative assertion (`if ($content -match "<keyword>") { … exit 1 }`), paired with the
  positive coverage check. See the negative-assertion section above; `stacks/dotnet.md §20`. WEAK
  (BLOCKER when the excluded scenario traps a downstream compile).
- **Stale line-number pointer / unhedged architecture claim about a not-yet-run sibling** (#203): a
  later-wave task's action prompt cites a **line number** ("Scheduler ~231-253") for code an
  **earlier-wave task in the same plan** will create or modify before the later task executes, and/or
  states **as settled fact** how that sibling's deliverable works ("this extends the same `Scheduler`
  path"). Both claims were true only at plan-authoring time — the earlier task lands its own edits
  before the later task runs by construction, so the line number is stale on arrival (not by bad luck)
  and the architecture claim may simply be wrong (the earlier task may have built something structurally
  different than what the plan predicted). The motivating incident (issue #202): a task's prompt
  pointed at `Scheduler.cs ~231-253` and asserted the sibling task "extends the same Scheduler path" —
  by execution time the lines had shifted AND the sibling had actually built a brand-new standalone
  class (`PlanPreflightPhase.cs`), not a `Scheduler.cs` extension. The agent burned 60-170+ turns of
  pure re-discovery across two attempts, one fully exhausting its turn budget touching zero of its own
  deliverables. Fix: cite a **durable, structure-stable marker** instead of a line number (a distinctive
  comment string, a method/class/type name, a grep-able symbol), and phrase any "how it currently works"
  claim as a checkable hypothesis ("this reflects the plan-authoring-time state, before deliverable N
  had actually run — verify it's still accurate"), never as a given. See SKILL.md Step 6's
  durable-marker / architecture-caveat rule. **Pairs with the #204 `maxTurns: 75` trigger** (this
  catalogue's "maxTurns budgeting (#94)" section) — a task that needs one usually needs the other; treat
  them as one fix, not two independent bullets. WEAK (BLOCKER when the re-discovery cost is severe
  enough to exhaust the task's turn budget, as in the motivating incident).
- **Echo-judge**: a prompt-judge evaluating the action's own claim of success (its
  summary, its commit message) rather than the artifact.
- **Replay-the-action**: a guardrail that **re-runs the action's own command** (e.g. a
  full `dotnet build; dotnet test`) when the postcondition is **cheaply verifiable from
  recorded output** — a produced artifact or a runner-written TRX (SSOT §5.1, the
  verify-recorded-action-result section above). Pure wasted time/flake. Fix: verify the
  recorded artifact/result instead of replaying. (Counter-caution: replaying is the
  HONEST gate when no recorded GOOD target carries the postcondition — don't demote a real
  replay to a weak grep just for speed.)
- **Echo-judge on action stdout / action-exit-code tautology**: a guardrail that greps
  `GUARDRAILS_ACTION_STDOUT` for the action's own success string (`"Passed!"`, `"Build
  succeeded"`) — the action narrates its own success, and the wording rots across runner
  versions — or that tests `GUARDRAILS_ACTION_RESULT.exitCode -ne 0`, which is a pure
  tautology because a non-zero action already failed the attempt before any guardrail ran
  (the recorded exit code is ALWAYS 0 at guardrail time). Fix: read a runner-written
  structured result (TRX) or a produced artifact, never the action's self-report.
- **Failure detail lost to the tail** (#179): a `tests-pass` guardrail that runs `dotnet test`
  (or any runner whose default output prints assertion/exception text **mid-run** and ends with
  only `[FAIL] <name>` + a count) and exits 1 on failure — but never re-emits the detail. The
  harness feeds back only the **tail** of stdout (last ~60 lines / 4000 chars), so the agent sees
  WHAT failed, not WHY, and retries blind (plan-0009 burned 12 attempts to `needsHuman`). Fix:
  capture → emit full log → **re-emit the failure-signal lines at the END** so they land in the
  tail (see "Failure detail must reach the retry tail" below; the .NET regex is `stacks/dotnet.md
  §4.2`). The INVERSE checks — a `tests-fail-on-stubs` red, where a non-zero exit is success — do
  NOT re-emit (there is no failure to feed back).
- **Over-broad**: "all tests pass" on an early task — it fails for unrelated reasons,
  poisons retries with noise, and serializes the DAG. Whole-suite green belongs to one
  terminal integration task.
- **No baseline on a brownfield plan / a vacuous or whole-suite baseline** (#181): a plan
  that modifies project(s) with **existing tests in the touched area** but has **no baseline-green
  preflight** — so a work task's `tests-pass` guardrail can fail from PRE-EXISTING breakage
  (misattributed → wasted retries → late `needsHuman`), and a new test's "red" is ambiguous
  (missing-behavior vs already-broken). Fix: emit one
  `<plan>/preflights/01-baseline-<area>-tests-green.ps1` per touched area (a guardrail-shaped FILE — no
  task, no action — running the EXISTING area tests **via `--filter`** and asserting they pass,
  area-scoped, deduped, #179-re-emit form); the plan-root `preflights/` folder runs before the DAG and
  gates every task with no edges to author ("never build on red"). It is **distinct** from the terminal
  full-suite gate (green START before the DAG vs green END on the merged HEAD — emit both). A RED
  preflight halts the run before any task is scheduled — a fast, actionable halt, no retry budget burned
  (there is no task). Two **sibling errors** are just as wrong: a **vacuous baseline** on a GREENFIELD
  plan (a `dotnet test` over a project with zero tests, which trivially passes — certifies nothing while
  looking like a gate; greenfield must SKIP and state why); and a **whole-suite/whole-project** baseline
  (hits the #165/#176 compile-coupling trap — a mid-TDD project does not compile, so it false-reds with
  an error no work task can fix). See the baseline-green / start-from-green (preflight) section above;
  `stacks/dotnet.md §21`. WEAK (BLOCKER when the area's existing tests are in fact red at the start —
  every work task then mis-fails).
- **Compiles-but-never-runs** (server/executable plans, #64): the breakdown emits component
  tasks (scaffold exe, implement launcher, implement routes) each guarded by build +
  unit-tests, plus a terminal whole-solution build — but **no task wires the entry point to
  the launcher and no guardrail ever starts the binary**. Every check is green while the
  server 404s everything (the `Program.cs` that never calls `new Launcher().StartAsync()`).
  Fix: insert the entry-point-wiring task (structural grep, `stacks/dotnet.md §7`) and the
  live smoke-test task (start → poll route → assert 200 → stop in `finally`, `stacks/dotnet.md
  §8`) — see the entry-point-wiring section above. Unit tests structurally cannot catch a
  launcher that is implemented but never called.
- **Built-but-unwired component** (#120) — the recurring lesson, and a structural false-green
  at the assembly layer. The breakdown emits per-component tasks (author tests → implement
  `FooImpl`) each guarded by build + unit-tests through a constructor seam, plus a terminal
  whole-suite gate — but **no task constructs `FooImpl` and injects it at the production
  composition root** (the factory / `Program.cs` / DI / `RunCommand`), and **no guardrail ever
  drives the real assembler with the new mode active**. Every check is green while the feature is
  inert: the production path never branches into it, the machinery is reachable only from xUnit
  (which injects the seam itself). This recurred 3× in one plan (worktree engine, AI-merge worker,
  triage — all built, all dead from the CLI). Distinct from compiles-but-never-runs (#64): there
  the *exe* served nothing over a port; here an *internal collaborator* exists but is never wired
  into the assembler, so the unit-tested seam is dead code in production. Fix: insert the wiring
  task (construct + inject `FooImpl` into the assembler) and a composition-root guardrail that
  drives the REAL assembler and asserts the new mode activates — observable output through the
  entry point (strongest) or a reflection assertion on the constructed object that the collaborator
  is non-null, **with a contrast case** (the `Factory_Wires*` shape). See the composition-root
  section above + `stacks/dotnet.md §10`. The tells `/guardrails-review` hunts: an `IFoo`/`FooImpl`
  pair with no "wire it into the composition root" task; a guardrail that **constructs and injects
  `FooImpl` itself** (proves the component, not the wiring); reliance on terminal whole-suite green
  to cover wiring. **Forbidden "fix":** a prompt-judge "is this wired?" — wiring is a deterministic
  structural fact, asserted by driving the real assembler, never by vibes.
- **Backend-only-greenness for a UI plan** (#66) — the single most expensive false-green.
  The plan describes a **user-facing screen** ("serves a wizard to the browser", "the user
  completes the form", "master/detail view") and the breakdown emits ONLY JSON HTTP
  endpoints, DTOs, and their unit tests — **not one task produces an HTML page, stylesheet,
  client JS, or a `wwwroot`**. Build is green, unit tests pass, and (even with #64's
  smoke-test) the root returns 200 — because a JSON API answers 200. The run is 100% green
  and ships **no human-facing UI whatsoever**. This is distinct from compiles-but-never-runs:
  there the exe served nothing; here the exe serves the *wrong thing* (an API where the plan
  promised a UI), and the UI was never even built. Fix: insert a `build-ui-<screen>` task per
  described screen and the UI-presence guardrails — asset-exists (`stacks/dotnet.md §9`) and a
  served-markup-contains assertion EXTENDING the smoke-test (body contains a known UI string,
  not just HTTP 200) — see the UI-presence section above. The tell `/guardrails-review` hunts:
  a plan whose prose promises a frontend whose task folder contains zero frontend artifacts and
  zero served-markup assertion. **Forbidden "fix":** a prompt-judge "does the UI look good" —
  it is subjective vibes the demotion gate rejects AND cannot catch the failure; the deliverable
  is *presence and wiring*, asserted deterministically.
- **Test-author left to invent the seam** (#84): a plan needs a production injection seam — a DI
  constructor overload, a factory delegate, an injectable interface — for **one** behavior to be
  expressible as a test that can pass, but the breakdown emits no upstream seam task. Neither the
  test-author task nor the implementation task cleanly OWNS the seam as a verifiable deliverable, so
  the test-author task's `needsHuman` escape ("if no injection mechanism exists, write a needsHuman
  note and stop") fires at run time and a human must hand-edit production code mid-run. Distinct from
  compile-coupled-tests (where the missing symbol is a **type the test constructs** and forcing the
  whole file red is correct): here only one behavior of several needs the seam, so the file must keep
  compiling and failing as its own clean red. Fix: insert `NN-add-<component>-<seam>-seam` (pure
  structural production change, build + a STRUCTURAL seam-exists check via the declaration regex,
  TDD-exempt) the test-author task `dependsOn` — see the production-testability-seam section above +
  `stacks/dotnet.md §11`. **Forbidden "fix":** a bare name grep for the seam (passes on a comment / a
  `using`); use the declaration regex.
- **Agent-per-item loop over a large/unknown fan-out** (#100): a task whose deliverable is "process N
  items where N is unknown and potentially large" — a web crawl, a recursive-glob transform, a mass
  API fetch — modeled as an **agent-iterated loop** (one `.prompt.md` turn-budget covering N
  fetch+convert+write cycles). Agent turns are the wrong unit for bulk work: a few hundred items blow
  the budget, the action hits max-turns and is killed, and the retry hits the same wall identically —
  a hard dead-end on a task that is perfectly doable as a script. Raising `maxTurns` (#94) only moves
  the wall. Fix: model it as a **scripted-ETL `script` action** (the N-item volume happens in one
  script execution, off the turn budget), add a **discover-size-first** probe where N is unknown, and
  **split** the scripted bulk-capture from a **bounded** per-item curation task — see the
  bulk/unbounded-fan-out section above + `stacks/dotnet.md §12`. The tell `/guardrails-review` hunts: a
  crawl/scrape/bulk-transform task written as a prompt that "enumerates … and produces a note per
  item" with no size bound and no script.
- **Hidden-state**: the guardrail depends on machine state (network, globally
  installed tools, a developer's home dir) rather than ancestor outputs or the repo.
  Declare required interpreters via `guardrails.json` instead.
- **Unactionable failure**: a guardrail that fails with "FAIL" and nothing else. The
  failure line on stdout becomes the retry feedback — "greeting.txt missing 'Hello'"
  converges; "FAIL" loops.
- **Grep-scope contamination**: a guardrail that checks a property of a file THIS task
  produces but greps the whole project directory for the pattern. A sibling task in the
  same wave can satisfy a broad grep with terminology it happens to share — so the check
  passes even when this task's file is wrong. Scope `Select-String`/`Get-Content` to the
  specific file this task produces, never the project tree.
  - Weak (gameable): `Get-ChildItem src/Desktop -Recurse -Filter *.cs | Select-String -Pattern "LocalAppData"` — a sibling `SettingsService.cs` mentioning `LocalApplicationData` in the same wave satisfies it.
  - Strong: `Select-String -Path "src/Desktop/WorkspaceRecentsList.cs" -Pattern "LocalAppData"` — scoped to the one file this task owns.

<!-- BEGIN ADDED ANTI-PATTERNS #74/#75/#76/#96 (auto-merge friendly; do not merge into the list above) -->
- **Keyword-not-structural for a METHOD CALL** (#76): a "file calls `B.Method()`" guardrail that greps
  a **bare method name** — `RunAsync\s*\(` — instead of the call construct. It false-passes on a comment
  (`// RunAsync(scope)`), a **local stub/wrapper** method of the same name (`private void RunAsync(...)`),
  or any unrelated same-named method — none of which invoke the real library method. The call-site
  sibling of the type/member keyword-not-structural trap. Fix: **two sequential checks** — reference the
  **type** (`MigrationRunner`, rules out a local stub) AND the **dotted call** (`\.RunAsync\s*\(`, rules
  out comments and standalone definitions). Apply whenever "task A must call `B.Method()`" on a specific
  type in another project. See the method-call-anchoring section; `stacks/dotnet.md §15`.
- **Library bypasses its injected interface** (#74): a task extracts a library that **must write through
  an injected `IInterface`**, the library is registered + builds + tests pass — but **no guardrail checks
  the library's internals don't call the CONCRETE method directly**, bypassing the abstraction it exists
  to enforce. Registration/build/tests all stay green over the bypass. Fix: a forbidden-call scan of the
  **library project's `.cs` only** (scope to the lib folder, exclude `bin`/`obj`), **comment-stripped**
  (#97/#98 — else a comment naming the method false-REDs a correct library) and **dot-anchored**
  (`\.ConcreteMethod\s*\(`, #76 — else a same-named method or string literal false-REDs). Trigger: "must
  NOT call `X` directly" / "write through interface `Y`" / "the Exe bypasses the abstraction". See the
  no-direct-bypass section; `stacks/dotnet.md §16`.
- **Enumerated behaviors unverified** (#75): a test-author task whose action prompt lists **≥3 named
  behaviors** to encode but whose guardrails are only `tests-exist` + `tests-fail-on-current-code` —
  **neither checks the named behaviors are present**, so **one** trivially-failing stub test satisfies
  both while behaviors 2–N are never encoded (the coverage-gap anti-pattern, made concrete). Fix: add a
  `covers-key-behaviors` check for **2–3 distinctive terms** (domain type / enum / method name — never
  generic words like `test`/`assert`) from the behavior list, **scoped to the one test file**. Name it a
  **lower bound** (a term present ≠ the behavior asserted; the residual is human review) and report which
  enumerated behaviors went unchecked. See the covers-key-behaviors section; `stacks/dotnet.md §17`.
- **Name-convention seam unverified** (#96): task A produces artifacts a consumer (task B / a runtime
  component) resolves by a **derived or mapped name** (url→embedded resource, step id→filename, key→file,
  route→handler, message-type→schema) — and `file-exists`/`file-contains` on A plus content checks on B
  **both pass while the naming contract between them is never exercised**. B derives a name A never
  produced (case / separator / single special-case drift) and **404s/silently-falls-back at runtime** on
  a 100%-green suite — invisible until the first real run (a kebab `destination.html` outlier vs a
  PascalCase `DestinationSelection.html` lookup). Fix: a **consumer-driven integration guardrail** on a
  **both-sides-present** task that **parses the consumer's real map** (never a hard-coded contract copy),
  drives the lookup for **every** item, and asserts **200 + a per-item marker** (not a fallback body).
  Mark it `scope:"integration"` and keep it **union-safe** (#125 — assert "every present artifact
  resolves", an invariant, not a terminal postcondition). The tell `/guardrails-review` hunts: a
  derived-name consumer (fetch-by-name, embedded-resource/reflection lookup, convention file-map) with
  only per-side file-exists/content checks and no end-to-end lookup over the whole set. See the
  name-convention-seam section; `stacks/dotnet.md §18`.
<!-- END ADDED ANTI-PATTERNS #74/#75/#76/#96 -->

<!-- BEGIN ADDED ANTI-PATTERNS #221 (auto-merge friendly; do not merge into prose above) -->
- **Prose-only prohibition, no structural backing** (#221): the action prompt states an explicit "do
  NOT …" ("do NOT wrap this in a retry loop," "do NOT weaken this assertion to tolerate fewer than N
  arrivals") and the forbidden behavior is **structurally checkable** — but no guardrail enforces it, so
  the prohibition survives only as prose an implementer (adversarial, or merely lazy or wrong) is free
  to ignore. Sharper than an ordinary coverage gap: when the task's OTHER guardrail is
  EMPIRICAL/statistical (a "run it N times, assert it always passes" flake check), the guardrail can
  actively **REWARD** the forbidden shortcut rather than merely miss it — a weakened assertion that
  tolerates the race makes the N-run check EASIER to pass, and a retry-until-pass wrapper brute-forces a
  high pass rate with zero real fix, indistinguishable from a genuine fix to a statistical check alone.
  (Motivating case: a flaky concurrency test's action prompt forbade weakening `Assert.Equal(3, …)` to
  `Assert.True(… >= 2)` and forbade a retry-until-pass wrapper — neither prohibition had a guardrail, and
  both are exactly the cheapest wrong implementation for their task.) Fix: for every "do NOT …" in a
  generated prompt, ask whether the forbidden behavior is structurally checkable (a regex/count/shape
  test on the file the task modifies). If yes, emit a guardrail alongside the prohibition — a regex-lock
  on load-bearing assertions surviving verbatim, a call-count + forbidden-construct scan (no
  `for`/`while`/`catch`) for a banned control-flow shape, or a negative assertion (#176) for a
  keyword/scenario. If no, state so explicitly in the breakdown report rather than leaving it silently
  unguarded. See the prose-only-prohibition section above; SKILL.md Step 6 adds the matching authoring
  rule. BLOCKER when the empirical guardrail actively rewards the forbidden shortcut (the
  perverse-incentive case); WEAK when the prohibition is merely uncovered by an otherwise-deterministic
  suite.
<!-- END ADDED ANTI-PATTERNS #221 -->

<!-- BEGIN ADDED ANTI-PATTERNS #251 (auto-merge friendly; do not merge into the list above) -->
- **Relative path handed to a self-canonicalizing tool** (#251): a guardrail script passes a bare
  **relative** directory/file argument to a tool that does its own internal path canonicalization
  (e.g. `bats --tap tests/scripts/`) instead of resolving it to an absolute path first. On
  Windows/Git-Bash the tool's internal canonicalization can produce the Windows drive-letter hybrid
  form (`C:/Users/...`) rather than POSIX-absolute (`/c/Users/...`), which the tool's own downstream
  logic (bats' `load` library loader, motivating case) then rejects as "not absolute" — a **BLOCKER**:
  the guardrail can never pass, regardless of how correct the implementation is. Fix: resolve the
  argument to an absolute path yourself, first — `"$(cd <dir> && pwd)"` — before the tool ever sees it.
  See the absolute-path-resolution section above.
- **Bare `grep '^not ok'` as bats TDD-red proof** (#251): a bats-based test-author task's anti-tautology
  "red" check is a bare `grep -q '^not ok'` against `bats --tap` output. bats emits its own synthetic
  `not ok N bats-gather-tests` / `not ok N bats-source-scripts` lines on a **suite LOAD failure** — a
  different condition from "a real test ran and failed" — and those lines match the identical `^not ok`
  pattern, so the check accidentally passes when the suite never loaded a single test. Fix: exclude the
  internal marker names before counting a match as proof of red (`grep '^not ok' | grep -v
  'bats-gather-tests\|bats-source-scripts'`, or equivalent). See the bash/bats TDD-red-specificity
  section above.
<!-- END ADDED ANTI-PATTERNS #251 -->

<!-- BEGIN ADDED SECTION #94 — maxTurns budgeting doctrine (auto-merge friendly; do not merge into prose above) -->
## maxTurns budgeting — a turn-budget exhaustion is NOT a sizing failure (#94)

A guardrail catches a *wrong implementation*; a turn-budget exhaustion is a different failure class
— a **legitimately-progressing agent killed at the turn cap mid-task** — and the breakdown prevents
it not with a guardrail but by **budgeting `maxTurns` per task** (SKILL.md Step 4a; schemas.md
"Per-task `maxTurns`"). It belongs in this catalogue because the *diagnosis* is the doctrine: when a
prompt task fails on `max_turns` (`"terminal_reason":"max_turns"`, `"Reached maximum number of turns
(50)"`), the wrong fix is "split it further."

**Why "split it further" is wrong here.** The sizing heuristics (Step 2: one-session rule, guardrail-
boundary rule) model *deliverable count*, not *research/discovery overhead*. An integration task whose
assertions share ONE expensive setup (an in-process stdio/MCP harness) is correctly sized — its
assertions cannot be split without **duplicating** that setup, which makes the budget problem worse.
The cost driver is the agent reverse-engineering an unfamiliar SDK before it can write code (grepping
package XML docs for `McpClientOptions`/`CallToolResult.Content`/…), which is real progress, not a
loop. Splitting punishes a well-sized task for a budget problem.

**The doctrine.** Keep the flat **50** default; bump only the predictably turn-expensive archetypes
to a fixed **75** (a first-attempt cushion — actuals in the motivating run were 54 and 32, unguessable
in advance):
- **integration / smoke / e2e** tests, especially an in-process harness, transport-client wiring, or
  spawning a server;
- **work against an unfamiliar third-party SDK** (discover the API before writing code);
- **terminal aggregation / wiring** tasks that connect several unfamiliar seams at once;
- **integrates with, extends, or describes a sibling task's not-yet-landed implementation (#203/#204)**
  — the task's action prompt must integrate with, extend, or describe an **earlier-wave deliverable in
  the same multi-wave plan** that did not exist yet when the prompt was authored. The root cause
  differs from the other three (temporal ordering within the plan, not external unfamiliarity or
  aggregation complexity), but the re-discovery cost is the same shape: the agent must locate and
  understand code that may not match what the prompt described, since the prompt was necessarily
  written before that code existed. **Pair this bump with the durable-marker / architecture-caveat
  authoring rule (SKILL.md Step 6, #203)** — they are companion fixes for the same situation (hedge the
  prompt text AND budget the turns), not two bullets to apply independently.

A guessed *exact* budget is impossible; the fixed bump only needs to clear the common boundary case
(54 > 50). The real safety net is a **harness-side auto-escalate-on-`max_turns` retry policy**
(×1.5 next attempt) + distinct `max_turns` retry feedback — a SEPARATE harness concern, owned by the
harness developer, NOT emitted by the breakdown. The breakdown's contribution is the deliberate
first-attempt bump and a shared-harness insertion (below) so the heuristic is applied at generation
time, not discovered by a failed run.

**Amortize unfamiliar-SDK discovery (a generative insertion).** When ≥2 downstream tasks need the
same setup against an API no ancestor established, insert ONE upstream harness task that learns the
API and writes the reusable helper (a `<X>TestHost`); the downstream tasks `dependsOn` it instead of
re-discovering the API. This is the test-harness sibling of the production-seam (#84) and
composition-root (#120) insertions — driven by a shared *discovery cost*, not a missing artifact. The
harness task itself gets the `maxTurns: 75` bump (it pays the discovery cost). See SKILL.md Step 4a.
<!-- END ADDED SECTION #94 -->

<!-- BEGIN ADDED SECTION #116 — Windows-safe git test fixture (auto-merge friendly; do not merge into prose above) -->
## Windows-safe git test fixture — author-tests that build a real git repo (#116)

When an author-tests task's tests create a **real git repository**, a hand-rolled temp-repo helper
that assumes POSIX git semantics fails on Git-for-Windows in ways a POSIX-only author never sees —
and because the breakdown generates each author-tests task in isolation, **every** test-author agent
re-discovers (or misses) the same quirks independently, each a fresh `needs-human` halt. The fix is
the same posture as the test-framework decision: **resolve it once at generation time** by emitting
ONE shared, Windows-safe fixture (or injecting a portability directive), not per-task rediscovery
(SKILL.md Step 5a).

**The logged Windows-git lessons the fixture MUST encode** (each is a real halt):
- **Read-only loose objects (#109).** `Directory.Delete(repoRoot, recursive: true)` throws
  `UnauthorizedAccessException` (NOT `IOException`) because Git marks `.git/objects` loose objects
  **read-only** on Windows. → Strip read-only attributes before deleting.
- **Empty-directory prune (task-14).** After `git rm`/`git mv` empties a directory (`src/`),
  Git-for-Windows **prunes it**, so the next `File.WriteAllText(src/New.cs)` throws
  `DirectoryNotFoundException`. → Recreate the directory before writing into it.
- **`merge --abort` rollback failure (W3).** `git merge --abort` fails rc=128 on a dirtied tracked
  path. → Roll back with `git reset --hard <preHead>`, never `git merge --abort`.
- **Non-deterministic hashes.** Platform line-ending translation changes fixture content hashes. →
  Set `core.autocrlf=false` for deterministic hashes across platforms.

**Two ways to satisfy it** (pick per task; prefer the fixture when ≥2 tasks build real repos — the
same amortize-the-discovery logic as #94's shared-harness insertion):
1. Emit a shared `TempGitRepo` fixture (one reviewed file the git-touching tests reuse). The .NET
   realization is `stacks/dotnet.md §11`.
2. Inject a "Windows-Git test portability" directive into the git-touching author-tests action
   prompt, pointing at the fixture and naming the four behaviors above.

This is **authored-test portability**, distinct from runner-level failures (#114/#115). It is applied
at generation time so a Windows-git quirk surfaces in a reviewed fixture, not as a mid-run halt.
<!-- END ADDED SECTION #116 -->

<!-- BEGIN ADDED SECTION #101 — new-.claude/-subdirectory deliverable seeding (auto-merge friendly; do not merge into prose above) -->
## New-`.claude/`-subdirectory deliverable — seed the directory before the run (#101)

This is the **directory analogue of the artifact-ancestry rule** (below): a guardrail referencing a
file no ancestor produces is a missing inserted task; here the missing prerequisite is a *directory*.
Claude Code's `acceptEdits` mode (the default runner profile) auto-approves writes to **existing**
paths but **blocks creating a new subdirectory under `.claude/`** (skills/commands/hooks/agents/
contexts) without interactive confirmation. Headless, there is no human to confirm, so an agent
writing `.claude/skills/<new>/SKILL.md` into a not-yet-existing directory correctly self-blocks to
`{"needsHuman": "..."}` and the run halts (#101).

**Detection (at breakdown time).** A task whose primary deliverable is a file under `.claude/` AND
whose target subdirectory does not already exist (`Test-Path .claude/skills/<name>/`). An existing
subdir needs nothing; only a NEW one trips the barrier.

**Fix — seed it, or warn** (SKILL.md Step 5b):
1. Insert a **directory-seed task** (`NN-seed-<name>-dir`, a **script** action — `New-Item -ItemType
   Directory` + a `.gitkeep` write) immediately before the writing task, which `dependsOn` it —
   making the directory "existing" so `acceptEdits` approves the write. It MUST be a script, not a
   prompt: a script the harness runs directly bypasses the `acceptEdits` tool-permission barrier,
   so it creates the new `.claude/` subdir headlessly; a prompt seed task would hit the same barrier
   it is meant to remove. Prefer this for an unattended run.
2. Or add a `## Pre-conditions` note to the writing task's action prompt requiring the caller to
   pre-create the directory (a committed `.gitkeep`) before the run.

**Guardrail — make the barrier visible.** A `01-dir-seeded.ps1` (`file-exists`, #1) on the writing
(or seed) task asserting the target subdir exists before the write, scoped to the one subdir the task
owns — so the barrier reads as a guardrail failure, not a cryptic `needsHuman`:

```powershell
# catches: a task writing into a NEW .claude/ subdir that acceptEdits cannot create headlessly -
#          the dir was never seeded, so the write self-blocks to needsHuman mid-run. Assert the
#          target subdir EXISTS before the write is attempted.
$dir = ".claude/skills/survey-eval"
if (-not (Test-Path $dir -PathType Container)) {
    Write-Output "$dir does not exist - seed it (a committed .gitkeep) before the run; acceptEdits cannot create a new .claude/ subdir headlessly"
    exit 1
}
exit 0
```

Issue #104 is the harness-side counterpart (granting the write up front); the breakdown owns only the
detection + seeding doctrine here.
<!-- END ADDED SECTION #101 -->

<!-- BEGIN ADDED SECTION #251a — absolute-path resolution for tools with their own path canonicalization (auto-merge friendly; do not merge into prose above) -->
## Absolute-path resolution — pre-resolve paths for tools that canonicalize internally (Windows/Git-Bash) (#251)

**The rule.** A guardrail script that hands a directory/file argument to a tool which does its **own**
internal path canonicalization MUST resolve that argument to an absolute path itself, FIRST —
`"$(cd <dir> && pwd)"` — and never pass a bare **relative** path. On Windows/Git-Bash a relative path
handed to such a tool can get internally resolved to the **Windows drive-letter hybrid form**
(`C:/Users/...`) instead of proper POSIX-absolute (`/c/Users/...`), and if any downstream logic inside
that tool (or a library it loads) requires a leading `/` before it will accept a path as "absolute," the
hybrid form is rejected outright — producing a guardrail that **can never pass**, regardless of how
correct the implementation under test is.

**Worked example — `bats`.** `bats`' own `load` helper (used inside a `.bats` file to source a shared
library) rejects any library path that isn't POSIX-absolute. Handing `bats` a **relative** suite
directory triggers its internal canonicalization, which on Windows/Git-Bash can produce the hybrid
form — and `load` then fails before a single real test runs:

```bash
# BAD - relative path; bats canonicalizes it internally. On Windows/Git-Bash this can produce
# the hybrid C:/Users/... form, which bats' own library loader then rejects as "not absolute" -
# the whole suite fails to LOAD, before any real test runs:
npx bats --tap tests/scripts/
#   1..1
#   not ok 1 bats-gather-tests
#   # Passed library load path is not an absolute path: C:/Users/.../tests/scripts/_helpers.bash

# GOOD - resolve to an absolute path FIRST, so bats never has to canonicalize it itself:
suite_dir="$(cd tests/scripts && pwd)"
npx bats --tap "$suite_dir"
```

**This is stack-agnostic doctrine, not a `bats` quirk.** Any cross-platform CLI tool that does its own
path resolution — not just `bats` — can hit the same trap on Windows/Git-Bash: resolve the path
yourself before the tool sees it, and the tool's internal canonicalization step is never reached. Apply
this whenever a guardrail script (or the action prompt it's paired with) invokes an external tool with a
directory/file argument on a bash-based stack. No `stacks/bash.md` ships yet (only `stacks/dotnet.md`
does, per SKILL.md Step 0) — this rule lives here, in the universal catalogue, until one does; migrate it
verbatim into `stacks/bash.md` §"absolute paths" if that file is ever created, rather than duplicating it.
<!-- END ADDED SECTION #251a -->

<!-- BEGIN ADDED SECTION #251b — bats TDD-red specificity, excluding synthetic suite-load markers (auto-merge friendly; do not merge into prose above) -->
## Bash/bats TDD-red specificity — exclude bats' own synthetic suite-load markers (#251)

This extends the Stub-based TDD SSOT above (#155) for a **bats-based** test-author task specifically:
the same "the red must COMPILE and FAIL, never merely exit non-zero" doctrine applies, but bats' TAP
output has its own failure mode that a naive check conflates with genuine red.

**The trap.** The obvious anti-tautology check for a bats suite is "did at least one test fail?", tested
with a bare `grep -q '^not ok'` against `bats --tap` output. That check **accidentally passes** for the
wrong reason: bats emits its **own synthetic** `not ok N bats-gather-tests` / `not ok N
bats-source-scripts` lines when the suite **fails to LOAD at all** (a missing/broken `load` target, a
syntax error in a `.bats` file, or — the motivating case — the absolute-path trap above) — and those
synthetic lines match `^not ok` identically to a genuine failing test. A bare `grep '^not ok'` cannot
tell "the suite loaded and a real test failed" (true TDD red) apart from "the suite never loaded a
single test" (a load failure that proves nothing about the behavior under test).

**Fix — exclude the internal marker names before treating a match as proof of red:**

```bash
# catches: a bats SUITE-LOAD failure (bats' own synthetic "not ok N bats-gather-tests" /
#          "not ok N bats-source-scripts" markers) being mistaken for a genuine failing test -
#          both match a bare `^not ok` scan identically, so a suite that never loaded a single
#          real test would accidentally satisfy this "red" check for the wrong reason.
set -euo pipefail
tap_out="$(npx bats --tap "$(cd tests/scripts && pwd)" || true)"
genuine_failures="$(printf '%s\n' "$tap_out" | grep '^not ok' | grep -v 'bats-gather-tests\|bats-source-scripts' || true)"
if [ -z "$genuine_failures" ]; then
    echo "no genuine failing test found - either the suite passed (not TDD-red) or it never loaded a real test (see TAP output above for why)"
    echo "$tap_out"
    exit 1
fi
exit 0
```

**Pairs with the absolute-path section above.** The two bugs compound: the relative-path trap makes the
suite fail to LOAD, and the bare `grep '^not ok'` check then mistakes that load failure for the "red"
the test-author task was supposed to prove — going green for a completely different reason than the one
the guardrail author intended. Fixing only one of the two still leaves a gap: fixing the path without
fixing the grep leaves the suite genuinely loading, but a *different* future load failure (a typo'd
`load` target, a syntax error) would still slip through as accidental "red." Fix both.
<!-- END ADDED SECTION #251b -->

## The artifact-ancestry rule

A guardrail may only reference artifacts that are (a) produced by an ANCESTOR task in
the DAG, or (b) pre-existing in the repo. A guardrail that checks something no
upstream task produces will fail forever — that is a missing inserted task (see the
skill's Step 5), not a guardrail problem. Sweep every guardrail against this rule
before writing the folder.
