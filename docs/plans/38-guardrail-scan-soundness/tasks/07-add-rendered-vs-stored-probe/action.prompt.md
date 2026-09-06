## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g. `07-add-rendered-vs-stored-probe`), NOT the
  stableId. The harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "07-add-rendered-vs-stored-probe": { "someKey": "someValue" } }`.
- EXCEPTION — the CONTROL KEYS `needsHarnessWrite` and `needsHuman` are TOP-LEVEL
  SIBLINGS of your folder-name key, never nested inside it. They are instructions to
  the harness, not state, so the rule above does not cover them:
  `{ "07-add-rendered-vs-stored-probe": { "someKey": "someValue" },
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

`#428` has **no mechanical gate by design** (design 38 section 6: the expressible GR2037 pattern is
heuristic in both directions, and a gate that certifies less than it appears to is the defect this plan
exists to close). Its only enforcement is an adversarial-review probe. Add it.

Read `docs/plans/38-guardrail-scan-soundness.md` sections 4.2, 6 and 11 first. Section 11 says plainly
that this is the weakest item in the plan and why — do not oversell it in the text you write.

**Two deliverables, both in `.claude/skills/guardrails-review/SKILL.md`.**

### 1. The rendered-vs-stored probe

A new probe in the same voice as the existing ones. What it looks for: a guardrail clause that matches a
phrase **as it reads** rather than **as it is stored**. When the target is emitted across two or more
source-line literals — wrapped `AppendLine` calls composing prompt or feedback prose, a concatenated
message — a single-line regex for the whole sentence can never match and the guardrail **silently
passes**. That is a false-PASS, the expensive direction: a false-RED halts a correct run and is fixed in
minutes; a false-PASS certifies work that was never done.

The probe must carry this exact sentence, so it is greppable and so the guardrail can bind to it:

> the rendered form is not the stored form

The tell a reviewer can act on: a `-match` / `Select-String` literal containing a long, space-separated
phrase, over a source file that composes text. The remedy: prefer a distinctive fragment that sits on
**ONE source line**; normalize whitespace or use `[\s\S]` when the whole phrase matters; and verify the
fragment against the **real file**, never against the rendered text. Keep `ONE source line` verbatim.

Live instance worth citing: `RetryPolicy.cs` composes its retry-feedback prose across `AppendLine` calls
split by a six-line comment and an `if` boundary — about 400 characters, bisected mid-phrase — so
neither a single-line regex nor a bounded dotall window can match it. Recorded instances: four, the most
recent commit `62c59db3`.

### 2. The preprocessing-order correction

This file states the comment-strip rule in at least two places (grep for `comment-strip` and
`comment-blind`). Both currently imply that stripping comments is the whole of the preparation. Correct
them with the sentence the plan-breakdown skill also carries, verbatim:

> Neutralize string literals BEFORE stripping comments.

and the one-line reason: a `/*` spelled inside a string literal opens a phantom block comment running to
the next `*/` in a later literal, blanking everything between — which fails both closed (a
required-present clause over the blanked region false-REDs) and open (a forbidden-present clause over it
false-PASSES). Measured: 29 of the 31 committed guardrails that do both operations do them in the
defective order.

## Constraints

**Scope boundary (harness-enforced):** Write only to `.claude/skills/guardrails-review/SKILL.md`. The
plan-breakdown skill's three files belong to a **different** task and an edit to any of them fails this
one — including `references/guardrail-catalogue.md`, which is the natural place to want to reach.

Match the document's own conventions — probe numbering, the `(#NNN)` tagging style, the checklist format
near the end of the file. A probe that reads like an insertion gets skipped.
