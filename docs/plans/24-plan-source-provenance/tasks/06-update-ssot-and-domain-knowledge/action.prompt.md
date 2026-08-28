## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in, i.e.
  `{ "06-update-ssot-and-domain-knowledge": { "someKey": "someValue" } }`. The harness
  REJECTS a fragment keyed by anything else (every attempt).
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

## Harness-write escape hatch (one of your two files lives under `.claude/`)

Your primary deliverable is a file under `.claude/`, which a Claude Code subprocess CANNOT write —
the tool-permission layer refuses every `.claude/` write unconditionally. Do NOT attempt a direct
`Write`/`Edit` to the `.claude/` path: a direct-write probe wastes a turn and populates the
harness's permission-wall tracker. Instead, FIRST write a `needsHarnessWrite` request to the
state-out path. The harness (which is NOT subject to that layer) performs the write directly, then
your guardrails still run normally against the result. There are two forms, and they are mutually
exclusive — send exactly one:

- **MODIFYING an existing file — use `edits` (prefer this):**
  `{"needsHarnessWrite": {"path": "<workspace-relative path>", "reason": "<why>", "edits":
  [{"old": "<verbatim anchor text>", "new": "<replacement text>"}]}}`.
  Each `old` must occur **exactly once** in the file — zero matches and two-or-more matches are both
  rejected, so include enough surrounding context to make each anchor unique. `old` is matched
  VERBATIM (exact indentation, punctuation and blank lines; only line endings are tolerated), so
  copy the passage out of the file rather than retyping it. Edits apply in order and ATOMICALLY: if
  any one fails, none are written and the file is unchanged. An empty `new` deletes the anchored
  text. Use `edits` **however large the file is** — its cost scales with your change, not the file.
- **CREATING a file — use `content`:**
  `{"needsHarnessWrite": {"path": "<workspace-relative path>", "content": "<full file content>",
  "reason": "<why>"}}`.
  Do NOT use `content` to modify a large existing file: the harness refuses full-content mode for an
  existing target over 64 KB, and re-emitting thousands of lines you did not mean to change risks
  silently corrupting them.

**If your deliverable spans SEVERAL files, send an ARRAY of those entries in ONE request** — one
entry per file, mixing `edits` and `content` freely:
`{"needsHarnessWrite": [{"path": "<file A>", "reason": "<why>", "edits": [...]}, {"path": "<file B>",
"reason": "<why>", "content": "..."}]}`.
Do NOT deliver them one per attempt: a failed attempt rolls the workspace back to a clean base, so
an earlier attempt's write is DISCARDED and progress cannot accumulate. The array is applied
ATOMICALLY — if any entry fails, nothing is written anywhere and every file is unchanged, so fix the
entry the message names and re-emit the WHOLE array. One entry per file: two entries naming the same
file are rejected as ambiguous (merge their changes into a single `edits` array).

If you already attempted a direct write and it was refused, do NOT retry it or try workarounds
(PowerShell, `dangerouslyDisableSandbox`) — just emit `needsHarnessWrite` as above.

**`.claude/skills/guardrails-domain-knowledge/SKILL.md` is ~112 KB, well over the 64 KB full-content
ceiling — so `edits` is the only form that will be accepted for it.** The SSOT file is NOT under
`.claude/`: edit `docs/plans/02-schemas-and-contracts.md` directly with your normal `Edit` tool.

## Task

Record the new artifact and the new gate in the two documents that carry the contract. This is a
documentation delta, not a design decision: the design is settled in
`docs/plans/24-plan-source-provenance.md` — read it first and write down what it says.

**Write exactly two files:**

1. `docs/plans/02-schemas-and-contracts.md` (the SSOT) — direct `Edit`.
2. `.claude/skills/guardrails-domain-knowledge/SKILL.md` — via `needsHarnessWrite` `edits`.

**Scope boundary (harness-enforced):** Write only to those two paths. After this task completes, the
harness runs a `git diff` check and rejects any edit outside them — including the plan of record, any
source file, or another skill. An out-of-scope edit fails the task immediately and consumes a retry.

### 1. The SSOT — `docs/plans/02-schemas-and-contracts.md`

Two edits, both small:

**(a) The section 1 plan-folder layout tree.** Add one line inside the `state/` block, in the exact
style of the sibling lines already there. The precedent to copy is the line beside it:

```
│   ├── guardrails-review.json   # OPTIONAL review marker — COMMITTED, PlanDefinitionHash-keyed (§7.3, §13)
```

Your new line names `plan-source.json` in the same column-aligned comment form, saying it is the
breakdown-time provenance record and pointing at the section you add in (b).

**(b) A short subsection under section 6 (State)** describing the artifact. It must carry, verbatim as
literal tokens:

| Literal token | Why it is pinned |
|---|---|
| `state/plan-source.json` | The path. Sibling precedent already in this file: `state/guardrails-review.json`. |
| `sourceSha256Lf` | The LF-normalised hash field. Sibling precedent for a camelCase JSON field named inline in this document: `mergeOnSuccess`, `expectedDurationSeconds`. |
| `declaredDelegatedDecisions` | The declared-count field the gate consumes. Same precedent. |

Content, all of it drawn from `docs/plans/24-plan-source-provenance.md`:

- The JSON shape (section 3 of the plan of record) in a fenced block, matching how this document
  already presents `guardrails.json` and sidecar schemas.
- **Both** hashes and why both exist: `sourceSha256` is byte-exact over the bytes as read (so it joins
  to Charter's hash of the same file); `sourceSha256Lf` normalises line endings, because a raw
  mismatch is usually `core.autocrlf` rather than tampering, and a check whose first alarm is a false
  one trains everyone to ignore it.
- `stamps` is an **open map** keyed by whatever `<!-- charter: key=value -->` comments are found — not
  two named fields — so Charter can add stamp lines without a schema change here. First wins on a
  duplicate key, and the duplicate is reported.
- `declaredDelegatedDecisions` is the integer from the `DECISIONS DELEGATED TO YOU: (\d+)**` line, or
  `0` when the line is absent.
- **Why it lives under `state/`:** a field on `guardrails.json` would fold into `PlanDefinitionHash`,
  which keys the review attestation — recording provenance there would de-attest the plan's review and
  re-fire GR2025. `state/` is excluded from all four hashes and from `BreakdownManifest.ShouldInclude`
  (only the committed `state/seed.json` is authored content), and `RunReset` deletes named files
  rather than the folder, so the artifact survives `--fresh`.
- **The declared-count gate:** the harness compares the declared count **N** it read against the
  count **M** the produced folder records; when `N >= 1` and `M != N` the breakdown fails. State the
  two limits the failure message also carries: it proves the count, never that a decision was made
  well; and it depends on Charter's count-line guarantee, so markers present with no count line is a
  Charter bug to file there, not a plan defect.
- The interactive `/plan-breakdown` door runs no harness code, so neither the record nor the gate
  happens on that path — note it as a known, deliberately-deferred gap (plan of record section 5).

### 2. The domain-knowledge skill — `.claude/skills/guardrails-domain-knowledge/SKILL.md`

A **short** entry, placed where the skill already describes harness-written `state/` artifacts. The
sibling precedent to match in form and length is the existing `breakdown-intent.json` entry:

```
`<wave>/state/breakdown-intent.json` -- `{ version, declaredAt, tasks: [{ folder, purpose }] }`, the
```

Your entry must carry, verbatim as literal tokens, `plan-source.json` and
`declaredDelegatedDecisions`, and must say in one or two sentences: written by the harness at
breakdown time from the single read chokepoint; carries both hashes, the open stamps map and the
declared count; hash-excluded `state/` placement so it cannot de-attest the plan's review; survives
`--fresh`; and it is what the declared-count gate reads.

Follow the skill's own SELF-UPDATING instruction: **update the affected section(s) only.** Do not
restructure the file, do not touch the YAML frontmatter, and do not rewrite neighbouring entries to
match your phrasing.

### The bar

Both documents have strong existing conventions. Write in the voice of the section you are adding to,
reuse its heading depth and its table/fenced-block style, and keep the addition proportionate — this
is one artifact and one gate, not a new chapter.
