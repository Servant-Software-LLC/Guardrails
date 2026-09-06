## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g. `06-correct-scan-order-doctrine`), NOT the
  stableId. The harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "06-correct-scan-order-doctrine": { "someKey": "someValue" } }`.
- EXCEPTION — the CONTROL KEYS `needsHarnessWrite` and `needsHuman` are TOP-LEVEL
  SIBLINGS of your folder-name key, never nested inside it. They are instructions to
  the harness, not state, so the rule above does not cover them:
  `{ "06-correct-scan-order-doctrine": { "someKey": "someValue" },
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

## Writing under `.claude/` — go straight to `needsHarnessWrite`

Your primary deliverable is a file under `.claude/`, which a Claude Code subprocess CANNOT write —
the tool-permission layer refuses every `.claude/` write unconditionally. Do NOT attempt a direct
`Write`/`Edit` to the `.claude/` path: a direct-write probe wastes a turn and populates the
harness permission-wall tracker. Instead, FIRST write a `needsHarnessWrite` request to the
state-out path. The harness (which is NOT subject to that layer) performs the write directly, then
your guardrails still run normally against the result.

**`needsHarnessWrite` is a TOP-LEVEL key — a SIBLING of your task's folder-name key, NEVER nested
inside it.** The harness reads it at the fragment root only.

Every file this task touches already exists, so use the **`edits`** form:

```
{"needsHarnessWrite": {"path": "<workspace-relative path>", "reason": "<why>",
 "edits": [{"old": "<verbatim anchor text>", "new": "<replacement text>"}]}}
```

Each `old` must occur **exactly once** in the file — zero matches and two-or-more matches are both
rejected, so include enough surrounding context to make each anchor unique. `old` is matched
VERBATIM (exact indentation, punctuation and blank lines), so copy the passage out of the file rather
than retyping it. Edits apply in order and ATOMICALLY: if any one fails, none are written. Use `edits`
however large the file is — its cost scales with your change, not the file.

**If your deliverable spans SEVERAL files, send an ARRAY of those entries in ONE request** — one entry
per file:

```
{"needsHarnessWrite": [{"path": "<file A>", "reason": "<why>", "edits": [ ... ]},
                       {"path": "<file B>", "reason": "<why>", "edits": [ ... ]}]}
```

Do NOT deliver them one per attempt: a failed attempt rolls the workspace back to a clean base, so an
earlier attempt's write is DISCARDED and progress cannot accumulate. The array is applied ATOMICALLY —
fix the entry the message names and re-emit the WHOLE array. Two entries naming the same file are
rejected as ambiguous.

## Task

The doctrine tells a guardrail author to preprocess source before matching, and is wrong about the
order. Fix it in the plan-breakdown skill. Read `docs/plans/38-guardrail-scan-soundness.md` sections 4,
4.1a, 4.2 and 6 first — they carry the corrected idiom, the measurement behind it, and the exact sites.

**Three deliverables, all in this one skill directory.**

### 1. The ordering rule, at every site that currently states it backwards

At least six places say *"strip comments first"* — grep for `strip comments` and `comment-strip` across
your three files; `guardrail-catalogue.md` and `stacks/dotnet.md` section 11 are the dense ones. Every
one of them must gain the order and the reason. Each edited site must carry this exact sentence, so the
rule is greppable and so a reader who lands on any one of them gets the whole rule:

> Neutralize string literals BEFORE stripping comments.

Then state why, in the site's own voice: a `/*` spelled inside a string literal (a glob such as
`src/*`) opens a phantom block comment that runs to the next `*/` spelled in a later literal, blanking
everything between. It fails **both closed and open** — a required-present clause over the blanked
region false-REDs, and a forbidden-present clause over it false-PASSES. Measured on this repository: 29
of the 31 committed guardrails that do both operations do them in the defective order.

The corrected idiom is design 38 section 4.1. Note the detail that is easy to drop: the neutralizer must
map `/` and `*` inside a literal as well as braces — neutralizing braces alone leaves the delimiters
intact, which is exactly how the shipped form fails.

Add a worked trap to the catalogue: a subject whose literals contain an **unclosed** `/*` and, later, a
`*/`, with the construct under test between them. A trap where both delimiters sit inside the *same*
literal does not reproduce the defect — the lazy match closes immediately — and a sample built that way
passes under both orders while appearing to test the fix.

### 2. The rendered-vs-stored anti-pattern (issue #428)

Add a catalogue anti-pattern beside *grep-scope contamination* and *structural-vs-keyword*: a guardrail
that matches a phrase as it **reads** rather than as it is **stored**. When the target is emitted across
two or more source-line literals — wrapped `AppendLine` calls composing prompt or feedback prose — a
single-line regex for the whole sentence never matches and the guardrail **silently passes**. It is a
false-PASS, which is the expensive direction. Live instance in this repository: `RetryPolicy.cs` composes
its retry-feedback prose across `AppendLine` calls split by a six-line comment and an `if` boundary,
about 400 characters, bisected mid-phrase — so neither a single-line regex nor a bounded dotall window
can match it. Recorded instances: four, most recently commit `62c59db3`.

The entry must carry this exact sentence, so it is greppable and so the guardrail can bind to it:

> the rendered form is not the stored form

State the three-line rule with it: prefer a distinctive fragment that sits on **ONE source line**;
when the whole phrase matters, normalize whitespace or use `[\s\S]`; verify the fragment against the
REAL file, never against the rendered text. Keep the words `ONE source line` verbatim — the guardrail
binds to them too.

**Do not add a GR2037 registry entry for this one.** Design 38 section 6 rules it out deliberately: the
expressible pattern is heuristic in both directions, and a gate that certifies less than it appears to is
the defect this plan exists to close. Say so where you add the anti-pattern, so the next reader does not
"finish the job".

### 3. The generator emits `Stop` and a terminal `exit`

Where the skill and the stack file show a guardrail script's opening lines, make the emitted form carry
both of these, together:

```
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
```

The first makes an engine-raised error terminate instead of failing open. The second is load-bearing and
must not be dropped: without it, on a box where that preference is `$true`, a non-zero `dotnet test` —
the **success** condition of every inverse TDD-red check — becomes a terminating error and aborts the
check. Measured on pwsh 7.6.5 it is `False` by default, so today it is belt-and-braces; it is a
preference, not a guarantee, which is why the doctrine pins it (design 38 section 4.1a).

Show the two lines **together**, as one opening block — the guardrail asserts they appear within 200
characters of each other, because shown apart an author copies one and not the other. Do not simply add
the second line somewhere else in the file: `$ErrorActionPreference = 'Stop'` already appears in both of
these files today, so a check for it alone would have been green before you started and would have
certified nothing.

Also state that a guardrail **ends on an explicit `exit`**. The harness shim relies on it: a script that
falls off its end after a failing native command would otherwise take that command's exit code. All 900
committed guardrails already do this, so the rule is documenting an invariant, not asking for a change.

## Constraints

**Scope boundary (harness-enforced):** Write only to the three files listed in this task's `writeScope`,
all under `.claude/skills/plan-breakdown/`. `guardrails-review/SKILL.md` belongs to a **different** task
and an edit to it fails this one.

Match each document's own conventions — heading depth, the `(#NNN)` issue-tagging style, the table
shapes. These files are read far more often than they are written; a section that reads like an
insertion is worse than one that reads like it was always there.
