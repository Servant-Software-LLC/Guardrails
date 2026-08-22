## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key. This plan is WAVED, so the key is the WAVE-QUALIFIED id:
  `{ "wave-01-config-net/06-update-ssot-and-domain-knowledge": { "someKey": "someValue" } }`.
  The harness REJECTS a fragment keyed by anything else (every attempt).
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

## Escape hatch for the `.claude/` deliverable (do not remove)

One of your two deliverables is `.claude/skills/guardrails-domain-knowledge/SKILL.md`, which a Claude
Code subprocess CANNOT write — the tool-permission layer refuses every `.claude/` write
unconditionally. Do NOT attempt a direct `Write`/`Edit` to that path: a direct-write probe wastes a
turn and populates the harness's permission-wall tracker. Instead, FIRST write a `needsHarnessWrite`
request to the state-out path. The harness (which is NOT subject to that layer) performs the write
directly, then your guardrails still run normally against the result.

**Use the `edits` form — this file is large and you are changing a few passages of it:**

`{"needsHarnessWrite": {"path": ".claude/skills/guardrails-domain-knowledge/SKILL.md", "reason": "<why>", "edits": [{"old": "<verbatim anchor text>", "new": "<replacement text>"}]}}`

Each `old` must occur **exactly once** in the file — zero matches and two-or-more matches are both
rejected, so include enough surrounding context to make each anchor unique. `old` is matched
VERBATIM (exact indentation, punctuation and blank lines; only line endings are tolerated), so copy
the passage out of the file rather than retyping it. Edits apply in order and ATOMICALLY: if any one
fails, none are written and the file is unchanged. Do NOT use the full-`content` form here — the
harness refuses it for an existing target over 64 KB, and re-emitting thousands of lines you did not
mean to change risks silently corrupting them.

**`docs/plans/02-schemas-and-contracts.md` is an ORDINARY write** — it is not under `.claude/`. Edit
it directly with your normal tools. Only the skill file goes through `needsHarnessWrite`.

## Task

Land the documentation half of wave 1: record GR2051, GR2052 and GR2053 as **allocated** in both the
SSOT and the domain-knowledge skill, and retire them from the reservation statements in both.

**Scope boundary (harness-enforced):** Write only to `docs/plans/02-schemas-and-contracts.md` and
`.claude/skills/guardrails-domain-knowledge/SKILL.md` (the latter via `needsHarnessWrite`). An
out-of-scope edit fails the task immediately and consumes a retry.

### In each of the two documents

1. **Document the three codes where that document already documents its GR codes** — with the code,
   its constant name, its **warning** severity, and one line of meaning. Find how GR2047–GR2050 are
   recorded there and follow that form exactly; do not invent a new table or section.

   | Code | Name | Severity | Meaning |
   |---|---|---|---|
   | GR2051 | `NonRoutableBlockIsDefault` | warning | a `costly: true` or `routing`-less block is the registry `default` pointer in a tiering-configured file |
   | GR2052 | `CostlyBlockRoutingInert` | warning | a `costly: true` block also declares `routing`, which can never apply |
   | GR2053 | `PinAndTierCoexist` | warning | a pin (`action.runner` **or** `action.model`) and `action.tier` coexist on one action |

2. **Retire them from the reservation statements.** Both documents currently say GR2051–GR2054 are
   reserved by name and must not be re-used. After this task only **GR2054** is reserved — it is the
   v2 `#227` probes code and nothing in this plan takes it. A past-tense historical note is fine; a
   live claim that GR2051–GR2053 are still reserved or free is the defect, because the next allocator
   reads it and collides.

### Two hard constraints

**Do NOT touch the SSOT's `promptRunners` schema block.** `tests/Guardrails.Core.Tests/SchemaDriftTests.cs`
byte-compares that block against the copy in the `plan-breakdown` skill's `references/schemas.md`, and
this task's `writeScope` does not include that skill — so editing the block here would break a test you
cannot fix. You do not need to: these three codes add **no new configuration field**. They validate
fields that already exist (`default`, `costly`, `routing`, `action.tier`, `action.runner`,
`action.model`). Your change belongs in the **validation summary / diagnostic-code** material only.

**The SSOT is `eol=lf`-pinned in `.gitattributes` and byte-compared.** Preserve LF line endings; do
not let an editor rewrite the file's endings.
