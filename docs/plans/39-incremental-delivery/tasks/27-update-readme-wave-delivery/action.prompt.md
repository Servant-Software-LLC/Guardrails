## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g. `27-update-readme-wave-delivery`), NOT the
  stableId. The harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "27-update-readme-wave-delivery": { "someKey": "someValue" } }`.
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

## Task

Update `README.md` for what plan 39 changes about delivery. **Extend the existing
`### Delivery on success` section** (under the command-line reference; grep for the heading rather
than trusting a line number). Do NOT add a parallel section: an operator reads one delivery story, and
two sections that each tell half of it is how one of them goes stale.

Describe what SHIPPED, not the design's rejected alternatives. The sources are design 39
(`docs/plans/39-incremental-delivery.md` §1b, §1a, §1c and §4) and the SSOT
(`docs/plans/02-schemas-and-contracts.md`) as task 20 has just updated it. Where they disagree, the
SSOT and the code win.

Cover these, in the document's existing operator-facing style:

- **Per-wave delivery.** A wave opts in with `delivers: true` in its `brief.md` YAML front matter
  (default false). A delivering wave is a **delivery point**: when its exit gate passes, the plan branch
  as it stands merges into your branch at that wave's barrier, carrying the non-delivering waves before
  it. A plan that marks no wave behaves exactly as today — one merge at run end. Say where the flag
  lives plainly: there is no per-wave config file, and a `guardrails.json` inside a wave directory
  un-waves the plan.
- **The interlock is wave-scoped.** A wave delivers only if no machine decision that suppresses delivery
  (a proceeded-best-guess or proceeded-unreviewed) was recorded during that wave. Say this in the
  sentence that already explains when delivery is held back, so a reader does not meet two rules.
- **The partial-delivery report.** A run where an earlier wave delivered and a later wave failed prints
  which waves landed on your branch and which are held on the plan branch, BEFORE the verdict. The exit
  code does not change: a run with a failed wave is still a failed run. `git branch --no-merged` stays
  the confirmation, and the README already points at it — connect the two rather than repeating it.
- **The post-delivery refresh.** When your branch moved independently between deliveries, the harness
  merges it back into the plan branch after delivering, so later waves build on your new commits. That
  admits content no task authored, so `run.json` records it in `refreshed[]`, and a gate failure over a
  refreshed tree names the refresh instead of blaming the wave.
- **The two new `validate` warnings.** `GR2078`: a wave that follows a delivery point carries no entry
  preflight. `GR2079`: a wave sets `delivers: true` but carries no exit gate, so it cannot deliver. Both
  are warnings and neither moves the exit code.

**REWORD one existing sentence — it becomes FALSE.** The section currently says *"Nothing is merged on
a run that does **not** reach green: a needs-human halt, a failed gate, or a cancellation leaves your
branch untouched either way."* Once a wave can deliver at its barrier, a later wave's halt no longer
leaves your branch untouched — the earlier delivery has already landed. Rewrite that sentence so it is
true for both flat and waved plans. Do NOT leave it standing and add a qualifier elsewhere: a guardrail
asserts the unqualified claim is GONE, precisely because an append leaves both claims on the page.

**Scope boundary (harness-enforced):** Write only to `README.md`. After this
task completes, the harness runs a `git diff` membership check and rejects any edit outside these paths. An
out-of-scope edit fails the task immediately and consumes a retry. If you hit a compile error caused by a
missing symbol in another file, do NOT edit that file — write `{"needsHuman": "<what is missing>"}` to the
state-out path and stop.

**The harness runs this task's guardrails itself when you finish.** Do not try to run the guardrail scripts yourself: the shell they need is not granted to you, and a call refused on two attempts can halt the task even after the work is done.
