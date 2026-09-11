## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g.
  `23-update-domain-knowledge-skill`), NOT the stableId. The harness REJECTS a fragment
  keyed by anything else (every attempt), so:
  `{ "23-update-domain-knowledge-skill": { "someKey": "someValue" } }`.
- EXCEPTION — the CONTROL KEYS `needsHarnessWrite` and `needsHuman` are TOP-LEVEL
  SIBLINGS of your folder-name key, never nested inside it.
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

## Task

Document `guardrails supply` in `.claude/skills/guardrails-domain-knowledge/SKILL.md`.

**This is the surface that matters most and the one nothing tests.** The README is enforced by
`ReadmeCommandCoverageTests`; this is not. Design 40 §5a is explicit that if `supply` is
agent-callable at all, an agent has to know it exists — and the place an agent looks is this
skill, not the design document.

Cover:
- the verb, and that the staged path is the workspace path the file must have;
- **the two boundaries** — task boundary on a live run, run start on a halted one;
- **the caller scoping**: a task agent may supply only paths inside its OWN `writeScope`; an
  operator invocation is unrestricted. This is what makes the JIT case usable — an agent
  authoring a script it then needs on the base almost certainly owns that path already.

## Your deliverable is under `.claude/` — use needsHarnessWrite, do NOT write directly

Your primary deliverable is a file under `.claude/`, which a Claude Code subprocess CANNOT
write — the tool-permission layer refuses every `.claude/` write unconditionally. Do NOT attempt
a direct `Write`/`Edit` to the `.claude/` path: a direct-write probe wastes a turn and populates
the harness's permission-wall tracker. Instead, FIRST write a `needsHarnessWrite` request to the
state-out path. The harness performs the write, then your guardrails still run normally against
the result.

**`needsHarnessWrite` is a TOP-LEVEL key — a SIBLING of your task's folder-name key, NEVER
nested inside it.** Nested one level down the harness REJECTS the attempt and nothing is written.

Use **`edits`** (not `content`) — `SKILL.md` is a large existing file, and `edits` costs what
your change costs rather than what the file weighs:

`{"needsHarnessWrite": {"path": ".claude/skills/guardrails-domain-knowledge/SKILL.md",
"reason": "document the supply verb (#373)", "edits": [{"old": "<verbatim anchor>",
"new": "<replacement>"}]}}`

Each `old` must occur EXACTLY ONCE — include enough surrounding context to make it unique, and
copy it out of the file rather than retyping it.

**Scope boundary (harness-enforced):** Write only to the path(s) listed above. After this
task completes, the harness runs a `git diff` membership check and rejects any edit outside
them. An out-of-scope edit fails the task immediately and consumes a retry.

