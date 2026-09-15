## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g. `20-update-ssot-wave-delivery`), NOT the
  stableId. The harness REJECTS a fragment keyed by anything else (every attempt), so:
  `{ "20-update-ssot-wave-delivery": { "someKey": "someValue" } }`.
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

Record design 39's contracts in `docs/plans/02-schemas-and-contracts.md`. Where design 39 disagrees with
itself, §4 ("The record, pinned at review") and the review-round-4 answers win over §5's older one-line
schema:

- a wave's **`brief.md` front matter** gains **`delivers: true`** (bool, **default false**). NOT a
  wave manifest — there is no such thing, and §14.1 says so; the design's first draft was corrected
  at review. Record the front-matter surface in §14.1's layout notes and in §14.10 (`brief.md`),
  and say that `WaveDefinitionHash` already folds it, so flipping the flag on a completed wave
  trips drift;
- `run.json`'s `waves.<dir>` gains **`delivered`**, the record design 39 §4 pinned:
  `"delivered": { "status": "delivered", "startedAt": "…", "at": "…", "commit": "…", "outcome": "fast-forwarded", "covers": ["<wave>", …] }`.
  `status` is `"running"` | `"delivered"` | `"refused"` | `"suppressed"`. A barrier delivery that begins
  always writes `"running"`, with `startedAt` and `covers`, even over an existing `"delivered"` record:
  it is written when the delivery begins — after the delivery switches and the interlock pass, and
  before the trial merge runs the user's hooks (#625) — and is then replaced by `"delivered"` or
  `"refused"`. A `"suppressed"` record is written already settled, and its `detail` names the decision
  and its subject. A `"refused"` record carries `outcome` (`conflict` | `dirty-working-tree` |
  `hook-rejected` | `branch-moved` | `trial-gate-failed`, the last when the wave's exit gate fails on the
  trial merge) and `detail`. A `hook-rejected` record does not halt the run (the hold rule below), and every
  later barrier delivery in that run writes `"suppressed"` with a `detail` naming that rejection. A
  `trial-gate-failed` detail names each failing check, the user's tip the
  trial was built from, and the range `git log <plan-tip-sha>..<user-tip-sha>`, which lists exactly the
  user's commits the trial merged. Key the range on the two shas, never on branch names, so it still
  names the same commits after either branch moves, and record it as a range: nothing lists those commits
  one by one. When a resume's trial finds the plan tip already on the user's branch (equal tips included,
  as right after a quiet-case promotion), it
  skips the promotion and restores the prior `"delivered"` record, or writes one if the crash came before
  it, so a resume never records a refusal for a delivery that already landed. A rewind — drift resolution
  or `guardrails reset <plan> <wave>` — keeps a delivered wave's record, because its commits are already
  on the user's branch (review round 5, `d39-rewind-delivered-wave`); a re-run that delivers again
  replaces it. A delivery the operator forced past a suppressing decision
  records `"delivered"` with a `detail` naming the overridden decision. Null fields are omitted, never
  written as null. `covers` lists every wave the delivery carries, in order, ending with this one,
  computed from the journal so a resume computes the same set. A wave has NO `delivered` key — absent,
  not null — when it is not a delivery point, never reached its barrier, failed its exit gate, had
  delivery resolved off (`--no-merge-on-success`, or a serial run), had its delivery withheld by #556
  (a task definition edited mid-run), or is the plan's final wave, which delivers at run end and so never
  writes a barrier record. The missing key does not say where that wave's work is. The work
  reached the user's branch only if a later barrier delivery carried it (the wave is in that record's
  `covers`) or the run-end delivery landed (the top-level `delivery` record). Otherwise the report says
  the wave is held or not reached, reading the wave's own status. A serial run is different again: it has
  no plan branch at all, and its work is already in the checkout;
- **`branch-moved` has two causes, and the `detail` says which** (design 39 §1, review round 4). Record
  both, each with its remedy: the checkout was switched to another branch (the #588 text — check the
  branch out again, then resume), or the user's branch advanced after the trial was built
  (`'<branch>' moved from <sha10> to <sha10> after the trial was built` — resume, and the next trial
  includes the new commits);
- **barrier delivery obeys the same switches as run-end delivery**: `--no-merge-on-success` (or
  `"mergeOnSuccess": false`) turns it off, `--merge-on-success` lifts a delivery the §1a interlock held (a suppressing
  decision) but never a hold a rejecting hook caused, a task definition
  edited mid-run blocks it (#556) as it blocks run-end delivery, a serial run never delivers at a
  barrier, and a wave with no exit gate is never a delivery point. The interlock is
  consulted before the trial merge is built, so a held delivery never runs the user's hooks;
- **the plan's final wave never delivers at its barrier** (review round 5, `d39-barrier-terminal-gate`): it
  delivers through the run-end delivery, which waits for the plan-level terminal gate when the plan
  declares one (#457). Earlier waves deliver at their own barriers, before that gate can run. When the
  terminal gate fails after earlier waves delivered, the top-level `delivery` record is still written —
  before the CLI's terminal-gate halt returns — and reads `partially-delivered`;
- **a rejecting hook holds delivery instead of halting** (review round 5, `d39-hooks-untracked-tooling`):
  when the user's hook rejects a trial merge commit, that delivery and every later barrier delivery are
  held to run end, where the run-end merge runs the user's hooks in the user's own checkout. The
  rejecting wave's record reads `"refused"` with outcome `hook-rejected`, each later barrier's record reads
  `"suppressed"` with a `detail` naming that rejection, and no status is added. Say why: a hook that needs
  untracked tooling, such as `node_modules`, fails in a harness-owned worktree and passes in the user's
  checkout. The hold is visible in the end-of-run delivery report, which names the rejecting wave, the
  hook's detail and the waves held with it; the top-level `delivery` record does not change for it, and no
  `decisions[]` entry or observer event is added;
- the **`WaveDelivered`** observer event, `WaveDelivered(WaveNode, WaveDeliveredRecord)`, raised only for
  a `delivered` record and only after that record is persisted; and `RunReport.WaveDeliveries`, stamped
  from the journal in `BuildReport` on every report, halted ones included. In §8, `observer.jsonl` gains
  a `WaveDelivered` line carrying `member`, `waveDir`, `commit` and `covers`. `events.jsonl` (§8.1) gains
  NO delivery kind — the durable record is `waves.<dir>.delivered` — and `guardrails attach` does not
  replay deliveries in v1: its replay skips the line it does not know;
- the **refused-delivery halt**: a `conflict`, `branch-moved` or `dirty-working-tree` refusal halts the run
  at that wave with `WaveHaltKind.DeliveryRefused` (a `hook-rejected` trial holds instead, above). **A
  failed trial-tree gate halts as an exit-gate failure** (review round 5, `d39-trial-gate-failure`): it goes
  through the existing gate halt — `run.json`'s `halt` section, the gate logs and the log-site halt banner —
  with a headline saying the gate failed on the merge with the user's branch and naming the user's tip and
  `git log <plan-tip-sha>..<user-tip-sha>`, while the wave's record still reads `"refused"` with outcome
  `trial-gate-failed`. The wave's marker commit and `completed` status
  are written only after its delivery settles, so a resume re-attempts a refused delivery at that wave's
  barrier. No `RunHaltKind` is added, and the top-level `halt` section stays scoped to gates (#432).
  A `DeliveryRefused` halt also appends a `decisions[]` entry — `"boundary": "wave"`, `"decision": "halted"`, gate
  `delivery-refused`, and the wave directory as both `subject` and `wave` — so the refusal shows on the
  console and in `observer.jsonl`. It changes no outcome, exit code or answer-file behavior:
  `RunOutcomePolicy` acts only on `proceeded-best-guess` and `proceeded-unreviewed`. The log site does
  not show it (its only reader of a decision's gate keeps breakdown gates), and there is no log-site
  panel for a refused delivery in v1; it shows the wave as
  needs-human;
- the top-level **`delivery`** record (#542) on a run where some waves delivered and verified work is
  still held: outcome **`partially-delivered`** with `delivered: false`. `delivered` stays true only when
  ALL verified work reached the user's branch. `partially-delivered` wins over a held or refused run-end
  outcome: when earlier waves delivered at their barriers, a run-end delivery that the interlock held or
  the merge refused still records `partially-delivered`, and `reason` names the holding decision or the
  refusal's token. When a rejecting hook held barrier deliveries and the run-end merge then landed, the run
  is delivered, not partially delivered: the run-end merge carried every held wave;
- **`GR2078`** (a post-delivery wave with no entry preflight) and **`GR2079`** (a `delivers: true`
  wave with no exit gate), BOTH warnings, in the diagnostics registry section. Each entry says what its
  code warns about in the same sentence as the code: correcting the stale sentence below removes
  `GR2078`'s only current mention, and a bare code in a list records no contract. While you are
  there, **correct the stale sentence** that currently reads *"an unrelated new code should take
  `GR2078`"*: this plan takes GR2078 and GR2079, so the next free code is **GR2080**. GR2077 stays
  reserved by name;
- the **trial-merge ref** `refs/guardrails/trial/<waveDir>` (§1): a delivering wave gates against
  the trial merge and only fast-forwards the user's branch on green, which changes §14.3's
  exit-gate contract;
- the **trial-merge provider members** (§1, review round 4): the trial merge commit is created WITH the
  user's git hooks — never `--no-verify` — so the commit that lands on the user's branch was
  **hook-checked** (#149). The hooks come from the user's resolved hooks directory, including a relative
  `core.hooksPath` such as husky's, which a harness-owned worktree would otherwise skip. The moved-branch
  (#588) and dirty-tree (#448) checks re-run against the trial ref before the fast-forward;
- **`DecisionEntry` gains a `Wave` member** (§1a) — the wave-scoped interlock reads a recorded
  attribution rather than parsing `Subject`. That is a change to the shared `decisions[]` surface: show
  it in §7's `decisions[]` JSON as `"wave": "<waveDir>"`, omitted when null. Only the two decisions that
  suppress delivery (`proceeded-best-guess` and `proceeded-unreviewed`) and the refused-delivery entry
  above set it; every other entry leaves it null. And
  the **ride-along rule** (review round 4): a delivery is held when **any wave it carries** recorded a
  suppressing decision — the interlock reads the delivery's `covers`, not only the delivering wave's own
  decisions;
- in **§7**, beside `supplied[]`, the **`refreshed[]`** provenance section (design 39 §1c, "How a refresh
  is recorded"): `"refreshed": [ { "at", "commit", "from", "upstream", "deliveredWave", "paths" } ]` —
  absent (never null, never empty) when there was no refresh, and written only after its commit exists;
- in **§5.3**, the **refresh commit's shape**: a `--no-ff` merge of the `upstream` sha (never the branch
  name) in the integration worktree, with the plan-branch tip as FIRST parent and the trailer
  `Refreshed-From: <from>` / `Guardrails-Run: <runId>` — never `Supplied-By:`, because nothing was
  supplied — and its **trigger**: refresh iff the user's branch tip was NOT an ancestor of the
  plan-branch tip at delivery, read from the trial merge's own ancestry result. Say why the trigger is not
  `FastForwarded`: the trial merge makes every promotion a fast-forward, so that check would never fire;
- in **§14.3**, the **gate-halt disclosure**: a wave entry-preflight or exit-gate halt appends every
  `supplied[]` and `refreshed[]` record, oldest first (a refresh reads `refresh from '<from>' at
  <upstream, 10 chars>`), AFTER the failing check names, and a run with neither section renders
  byte-identically to today; and the **single-reader rule** — `UnauthoredContentNote` is the ONLY code
  that reads the two sections to answer "what is in this tree that no task authored?".

Write it in the document's own conventions — match the surrounding sections rather than importing
design 39's. Do NOT reword existing prose to satisfy a checking pattern; if a required token reads
wrong in context, say so via `needsHuman`.

**Scope boundary (harness-enforced):** Write only to `docs/plans/02-schemas-and-contracts.md`. After this
task completes, the harness runs a `git diff` membership check and rejects any edit outside these paths. An
out-of-scope edit fails the task immediately and consumes a retry. If you hit a compile error caused by a
missing symbol in another file, do NOT edit that file — write `{"needsHuman": "<what is missing>"}` to the
state-out path and stop.

**The harness runs this task's guardrails itself when you finish.** Do not try to run the guardrail scripts yourself: the shell they need is not granted to you, and a call refused on two attempts can halt the task even after the work is done.
