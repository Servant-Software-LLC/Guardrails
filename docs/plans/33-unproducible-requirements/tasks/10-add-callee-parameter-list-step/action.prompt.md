## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g.
  `10-add-callee-parameter-list-step`), NOT the stableId. The harness REJECTS a fragment
  keyed by anything else (every attempt), so:
  `{ "10-add-callee-parameter-list-step": { "someKey": "someValue" } }`.
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
(PowerShell, `dangerouslyDisableSandbox`) — just emit `needsHarnessWrite` as above.

## Task

Both skills already carry a datum-trace rule, shipped at `e118b9d`. It walks **upstream** to the
carrier — and for the defect this plan is about, that is one step short. Add the missing step.

**The gap, concretely.** A guardrail clause required the value `pending.Bucket` to be passed to
`RecordSettleWithAttempt`. A reviewer following the shipped procedure traces the datum upstream:
`pending` to `PendingAttempt` to `src/Guardrails.Core/Execution/RunReport.cs`, which **is** in an
ancestor task's scope. Step 3 returns *"reachable; stop."* The defect was **downstream**, in the
**callee's parameter list**: `ISchedulerJournal.RecordSettleWithAttempt` did not accept the argument,
and no task's `writeScope` named `ISchedulerJournal.cs`. The written steps never read a parameter list.

**1. `.claude/skills/guardrails-review/SKILL.md`** — add step 5 to the Unreachable-outcome probe (its
four mandatory steps sit around lines 948-1030; grep for the probe heading rather than trusting the
number):

> **5. If the required text is an ARGUMENT IN A CALL, the carrier is not the answer — the CALLEE is.**
> Name the member being called and open **its declaration**. Does its parameter list already accept what
> the clause requires? If not, the requirement is *"widen this signature,"* and the file declaring that
> member must be in this task's `writeScope` or an ancestor's. **Not the file the call is written in —
> the file the member is declared in.** For a call dispatched through an interface, that is the
> **interface**, not the concrete type: a cast to the concrete type compiles, satisfies the clause, and
> journals nothing.

**2. `.claude/skills/plan-breakdown/SKILL.md`** — the authoring-side twin, **beside** the shipped datum
trace under the *"TRACE THE DATUM"* heading (around line 381; grep for the heading):

> When a task's deliverable is *"pass D to M"*, `M`'s **declaring** file goes in the `writeScope` — the
> interface if the call dispatches through one — unless `M` already accepts D today. Grep the
> declaration, not the call site.

**This is an ADDITION, never a rewrite.** The shipped datum-trace section is correct and stays exactly
as it is; you are appending the step it does not cover. A task that re-authors that section has done the
wrong thing even if the result reads well.

**Both paragraphs must name the false green**, because in this class the false green is the outcome that
ships: the cast that compiles, passes the task's own filter, and detonates 26 tasks later under a fake.

**Scope boundary (harness-enforced):** Write only to `.claude/skills/guardrails-review/SKILL.md` and
`.claude/skills/plan-breakdown/SKILL.md`, and only via `needsHarnessWrite`. After this task completes,
the harness runs a `git diff` check and rejects any edit outside these paths.

## Done when

- Both skills carry their new step, added beside the existing material rather than replacing it.
- Each names the interface-versus-concrete-type trap and the false green it produces.
