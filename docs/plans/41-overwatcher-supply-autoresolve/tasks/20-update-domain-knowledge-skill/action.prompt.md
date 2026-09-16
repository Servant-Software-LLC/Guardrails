## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g. `20-update-domain-knowledge-skill`), NOT the
  stableId. The harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "20-update-domain-knowledge-skill": { "someKey": "someValue" } }`.
- EXCEPTION — the CONTROL KEYS `needsHarnessWrite` and `needsHuman` are TOP-LEVEL
  SIBLINGS of your folder-name key, never nested inside it.
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- Guardrails constrain the OUTCOME, never HOW you implement it. Never reshape working
  code — or reword a document away from its own conventions — to match a check's pattern.
- If you cannot proceed without a human decision, write
  {"needsHuman": {"question": "<question>", "kind": "blocked-work"}} to the
  state-out path and stop. If instead a guardrail reports something ABSENT that you can
  see is PRESENT, that guardrail is defective: use "kind": "defective-guardrail" and
  quote (a) the guardrail's exact claim and (b) the file:line that refutes it. If you
  cannot produce BOTH quotes it is not a defective guardrail — retry the work, or
  escalate as "blocked-work". Difficulty is never "defective-guardrail".

## Your deliverable is under `.claude/` — use needsHarnessWrite, do NOT write directly

Your deliverable is `.claude/skills/guardrails-domain-knowledge/SKILL.md`, and a Claude Code subprocess
**CANNOT** write under `.claude/` — the tool-permission layer refuses every such write unconditionally.
Do NOT attempt a direct `Write`/`Edit` to that path: a direct-write probe wastes a turn and populates the
harness's permission-wall tracker. Instead, FIRST write a `needsHarnessWrite` request to the state-out
path. The harness performs the write, and your guardrails then run normally against the result.

**`needsHarnessWrite` is a TOP-LEVEL key — a SIBLING of your task's folder-name key, NEVER nested inside
it.** Nested one level down the harness REJECTS the attempt and nothing is written:

```json
{
  "20-update-domain-knowledge-skill": { "someKey": "someValue" },
  "needsHarnessWrite": {
    "path": ".claude/skills/guardrails-domain-knowledge/SKILL.md",
    "reason": "record the dial:critical missing-resource auto-resolve (#712)",
    "edits": [ { "old": "<verbatim anchor>", "new": "<replacement>" } ]
  }
}
```

Use **`edits`**, not `content` — `SKILL.md` is a large existing file, and `edits` costs what your change
costs rather than what the file weighs. Each `old` must occur **EXACTLY ONCE**: include enough surrounding
context to make it unique, and **copy the anchor out of the file** rather than retyping it.

## Task

Record design 41's missing-resource auto-resolve in the domain-knowledge skill — **the surface an AGENT
reads**, and the one that decides whether this capability ever fires. The auto-resolve only engages when a
halting agent names the missing file by its exact workspace-relative path; an agent that does not know
that writes a vague question, and the feature silently never fires.

Add the bullet **design 41 section 12 gives verbatim** (see its `guardrails-domain-knowledge` hunk, the
last one in that section), under the existing `## Supplying a resource to an in-flight or halted run`
section, beside the `guardrails supply` bullets it belongs with. It must carry:

- **At `dial:critical` the overwatcher may resolve a missing file itself** (design 41, issue #712).
- **The trigger shape** — the task halts with
  `{"needsHuman": {"question": "...", "kind": "blocked-work"}}` whose question names the missing file by
  its **exact workspace-relative path**; a root-level file is written `./name`. Name the trigger the way
  the SSOT names it — `missing-resource` — so an agent reading a run's overwatch records can connect them
  to the halt it wrote, and say in that same sentence that the `blocked-work` classification is what lets
  the overwatcher supply the file (a `defective-guardrail` kind is refused outright).
- **The conditions** — the file is committed on the branch the run started from, missing from the run's
  base, and not produced by any other task.
- **The outcome** — the harness may commit it onto the plan branch as `Supplied-By: overwatcher` and
  re-run the task.
- **What the agent should therefore DO** — name the path verbatim and classify the halt `blocked-work`;
  never stub it, fetch it, or hand-copy it into your worktree.

Write it in the skill's own style and put each fact where it belongs — do not append a detached list at
the end of the file. The skill already documents the two drain boundaries, the plan-folder/code asymmetry
and the caller scoping in that section; this is the case where the harness supplies the file without a
human.

**Do not reword existing prose to match a checking pattern.** Every token the guardrail requires was
checked against a sibling precedent already in this file before it was pinned. If one reads wrong in
context, say so via `needsHuman` rather than bending the document around it.

**Scope boundary (harness-enforced):** Write only to
`.claude/skills/guardrails-domain-knowledge/SKILL.md`, and only through `needsHarnessWrite`. After this
task completes, the harness runs a `git diff` membership check and rejects any edit outside that path. An
out-of-scope edit fails the task immediately and consumes a retry. If you need a change in another file to
make this one correct, do NOT edit that file — write `{"needsHuman": "<what is missing>"}` to the
state-out path and stop.

**The harness runs this task's guardrails itself when you finish.** Do not try to run the guardrail scripts yourself: the shell they need is not granted to you, and a call refused on two attempts can halt the task even after the work is done.
