## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g.
  `11-record-gr2060-in-knowledge-skill`), NOT the stableId. The harness REJECTS a fragment
  keyed by anything else (every attempt), so:
  `{ "11-record-gr2060-in-knowledge-skill": { "someKey": "someValue" } }`.
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
  text. Use `edits` however large the file is — its cost scales with your change, not the file.
- **CREATING a file — use `content`:**
  `{"needsHarnessWrite": {"path": "<workspace-relative path>", "content": "<full file content>",
  "reason": "<why>"}}`.
  Do NOT use `content` to modify a large existing file: the harness refuses full-content mode for an
  existing target over 64 KB, and re-emitting thousands of lines you did not mean to change risks
  silently corrupting them.

**Both files you edit already exist and both are large, so use `edits` for both — in ONE request.**
Send an ARRAY of two entries, one per file. Do NOT deliver them one per attempt: a failed attempt rolls
the workspace back to a clean base, so an earlier attempt's write is DISCARDED and progress cannot
accumulate. The array is applied ATOMICALLY — if any entry fails, nothing is written anywhere, so fix
the entry the message names and re-emit the WHOLE array. One entry per file; two entries naming the same
file are rejected as ambiguous.

If you already attempted a direct write and it was refused, do NOT retry it or try workarounds
(PowerShell, `dangerouslyDisableSandbox`) — just emit `needsHarnessWrite` as above. You edit ONE file
here, so send ONE entry with an `edits` array.

## Task

Record two facts in `.claude/skills/guardrails-domain-knowledge/SKILL.md` so the next design starts from
the evidence rather than from the idea.

**1. GR2060 is shipped, and what it means.** Name `GR2060 UnproducibleGateRequirement` and state the
producer-coverage invariant in one line: *a guardrail may only require content some task in the plan can
actually produce* — the gate-level companion to the task-level artifact-ancestry rule. Note that it is
an **ERROR**, and that its severity is conditional on the JIT-gate excuse being present (a plain
`validate` on a partial prefix still errors; the breakdown gate excuses it and still reports it).

**2. GR2070 is HELD, not free.** This is the fact most likely to be lost. Record that GR2070
`UnproducibleCallArgument` was **designed and declined** — it has never fired on a real defect at any
commit in this repository — and that the reservation is in `DiagnosticCodes.cs` with a pointer to
`docs/plans/33-unproducible-requirements.md` §6.3. State the bar for revisiting it: **a defect, at a
commit**, where the named-argument requirement and the unowned declaring file coexist — not a clause
that merely happens to be written in the right shape.

Keep both entries short and in the skill's own voice. This is a knowledge skill: it records what is
true, not how to do something.

**The next-free code is GR2071.** If the skill states a next-free code anywhere, update it — and
remember the GR10xx and GR20xx ladders advance independently, so a line that states only one of them is
half a fact.

**Scope boundary (harness-enforced):** Write only to
`.claude/skills/guardrails-domain-knowledge/SKILL.md`, and only via `needsHarnessWrite`. After this task
completes, the harness runs a `git diff` check and rejects any edit outside that path.

## Done when

- The skill names GR2060 and the producer-coverage invariant.
- The skill records GR2070 as held-not-allocated, with the revisit bar.
- Any next-free statement in the skill reads GR2071 for the GR20xx ladder.
