## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g. `05-add-banned-pattern-entries`), NOT the
  stableId. The harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "05-add-banned-pattern-entries": { "someKey": "someValue" } }`.
- EXCEPTION — the CONTROL KEYS `needsHarnessWrite` and `needsHuman` are TOP-LEVEL
  SIBLINGS of your folder-name key, never nested inside it. They are instructions to
  the harness, not state, so the rule above does not cover them:
  `{ "05-add-banned-pattern-entries": { "someKey": "someValue" },
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

Add three entries to `.claude/skills/plan-breakdown/references/banned-guardrail-patterns.json` so the
firing controls authored in `tests/Guardrails.Core.Tests/BannedPatternRegistryTests.cs` pass **without
editing that test file** — it is outside your `writeScope`, and an edit to it fails this task.

Read the registry header comment first. It says how to grow it — *"add a JSON object with two fixtures,
not harness C#"* — and that is exactly the shape required here. No C# changes at all.

| id | catches | design 38 |
|---|---|---|
| `#608a` | a `.ps1` guardrail setting `ErrorActionPreference` to `Continue` or `SilentlyContinue` | 7, 0.1 |
| `#608b` | a guardrail that does not end on an explicit `exit` | 7, 3.4 |
| `#561` | a block-comment strip that runs BEFORE a string-literal neutralization | 4, 4.2 |

Each entry needs `id`, a rationale comment in the file house style, `badPattern`, `reason`,
`goodPatternHint`, `mustMatch` and `mustNotMatch`. The meta-test
`EverySeedEntry_BadPatternMatchesAllMustMatch_AndNoMustNotMatch` compiles every `badPattern` and holds
you to both fixture arrays, so a malformed entry cannot ship.

**Four constraints that decide whether these entries are worth having:**

1. **Key on the SHAPE, not on a token.** `#561` keyed on the two spellings `blankKeepingNewlines` /
   `neutralizeBraces` catches only a copy-paste; key on the ORDER of the two replaces, whatever the
   helpers are called. Design 38 section 11 names shipping the weaker version as the way this plan fails
   while looking finished.

   **There is deliberately no `#449` entry, and no test for one.** An earlier draft had both; an
   independent review measured that the shape a `#449` entry must fire on is *also* the shape of the
   doctrine's own canonical union guardrail (`examples/parallel-hello/.../01-whole-repo-greeting.ps1`, a
   raw read feeding `-match '(?m)^<<<<<<<'` with no strip — which is CORRECT without one, because a
   conflict marker inside a comment is still a conflict marker). Adding one reds
   `AnchoredConflictMarker_IsClean_NoGr2037`, which task 04 may not edit. Design 38 section 5 records it.

2. **Put a respelling of the BAD shape in `mustMatch`, not only good code in `mustNotMatch`.** A
   `mustNotMatch` array full of obviously-correct scripts proves nothing about whether the pattern
   generalizes.
3. **`#608b` must NOT reject the `try { ... exit N } finally { ... }` cleanup idiom.** A guardrail that
   creates a temp directory has to clean it up on the failure path too, so its last line is `}`, not an
   `exit`. **Three of this plan's own guardrails are written that way**, including
   `tasks/02/guardrails/04-shim-preserves-real-exit-codes.ps1`. The authored test
   `Entry608b_TryFinallyCleanupIdiom_IsClean_NoGr2037` pins it: a `#608b` that reds that shape is the
   false-RED half of exactly the family design 38 section 4.1a says this plan is closing, and it would
   falsify section 3.4's "0 of 900" claim using this plan's own artifacts as the counterexample.
4. **`#608a` must NOT also demand `$PSNativeCommandUseErrorActionPreference = $false`.** A guardrail
   that runs no native command does not need that line, and requiring it everywhere would make a
   correct script fail a lint — the false-RED half of the family this plan is closing (design 38
   section 4.1a).

**The validator strips whole-line comments before scanning** (`PlanValidator.cs`, itself the #97
lesson), so no `badPattern` may depend on comment text and no entry may be defeated by a header comment
describing the construct. Verify that against the existing three entries rather than assuming it.

Adding entries makes `guardrails validate` report GR2037 errors on some older, already-merged plan
folders. That is expected and correct (design 38 section 8) — do not weaken an entry to keep an old
folder quiet.
