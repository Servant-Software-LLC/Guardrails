## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's WAVE-QUALIFIED id as the single
  top-level key (this plan is waved), e.g.
  `{ "wave-01-correct-the-record/05-correct-guardrails-domain-knowledge-allowlist-wording": { "someKey": "someValue" } }`. The harness REJECTS a fragment keyed by
  anything else (every attempt).
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Harness-write escape hatch (do not remove)
Your primary deliverable is a file under `.claude/`, which a Claude Code subprocess CANNOT write —
the tool-permission layer refuses every `.claude/` write unconditionally. Do NOT attempt a direct
`Write`/`Edit` to the `.claude/` path: a direct-write probe wastes a turn and populates the
harness's permission-wall tracker. Instead, FIRST write a `needsHarnessWrite` fragment to the
state-out path; the harness (which is NOT subject to that layer) performs the write, and your
guardrails then run normally against the result.

**Use the ANCHORED EDIT form — you are MODIFYING a large existing file, not creating one:**

`{"needsHarnessWrite": {"path": "<workspace-relative path>", "reason": "<why>", "edits":
[{"old": "<verbatim anchor text>", "new": "<replacement text>"}]}}`

- Each `old` must occur **exactly once** in the file — zero matches and two-or-more matches are both
  rejected, so include enough surrounding context to make each anchor unique.
- `old` is matched **VERBATIM** (exact indentation, punctuation and blank lines; only line endings are
  tolerated). **Copy the passage out of the file rather than retyping it** — a retyped anchor that
  differs by one character is a zero-match rejection.
- Edits apply in order and **ATOMICALLY**: if any one fails, none are written and the file is unchanged.
- An empty `new` deletes the anchored text.

**Do NOT use the full-content (`"content"`) form here.** It is for CREATING a file. The files this
task targets are large (one is over 200 KB), and a full-content write to an existing file over 64 KiB
is now REFUSED outright (issue #437) — re-emitting thousands of lines of normative text to change a few
also risks silently corrupting the parts you did not mean to touch, which nothing here would catch.
`content` and `edits` are mutually exclusive; a fragment carrying both is rejected.

If a direct write was already refused, do NOT retry it or try workarounds (PowerShell,
`dangerouslyDisableSandbox`) — just emit the anchored `needsHarnessWrite` above.

## Task
The #252 guidance in **SKILL.md** under `.claude/skills/guardrails-domain-knowledge/` states that state-mutating git commands
(`restore`/`reset`/`checkout`/`push`/`commit`/`stash`) "stay outside `allowedTools`" — wording that
claims something the mechanism **cannot deliver**.

Claude Code **merges** the harness's `--allowedTools` with the operator's `~/.claude/settings.json`. A
plan's list can therefore only **GRANT**; it can never **WITHHOLD**. Evidence: on a real run
(`docs/plans/diagram-live-status-and-search/logs/.../07-author-tests-diagram-search-box/attempt-2/transcript.md`)
a bare `git checkout <ref> -- <paths>` RAN and completed a salvage under a plan whose `allowedTools`
contained no git at all, because the operator's settings file granted `Bash(git checkout:*)`.

Rewrite the affected passage so it is TRUE:
- `allowedTools` is a **floor**: it adds capabilities; it does not remove any.
- The read-only default remains the right thing to AUTHOR (it is what a clean box / CI gets, where the
  plan's list IS the whole grant) — keep recommending it.
- Drop any phrasing implying the omission WITHHOLDS a verb, or that a reader can rely on a
  state-mutating verb being unavailable.

Keep the change tight: correct the claim, do not restructure the section or alter unrelated guidance.

**Scope boundary (harness-enforced):** Write only under `.claude/skills/guardrails-domain-knowledge/`. After this task completes the
harness runs a `git diff` check and rejects any edit outside that path — including other skills'
directories, which sibling tasks own. An out-of-scope edit fails the task immediately and consumes a
retry.
