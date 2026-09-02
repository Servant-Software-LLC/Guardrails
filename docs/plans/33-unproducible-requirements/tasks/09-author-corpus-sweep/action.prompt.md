## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's FOLDER NAME as the single top-level
  key — the name of the directory this task.json lives in (e.g.
  `09-author-corpus-sweep`), NOT the stableId. The harness REJECTS a fragment
  keyed by anything else (every attempt), so:
  `{ "09-author-corpus-sweep": { "someKey": "someValue" } }`.
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

Write `tests/Guardrails.Core.Tests/ProducerCoverageCorpusTests.cs` — class
**`ProducerCoverageCorpusTests`** — the sweep that runs GR2060 over the whole committed corpus. It is
wired as a **terminal-gate** guardrail, so it withholds delivery rather than merging when it disagrees.

**The population is ALL 850 COMMITTED `.ps1` under `docs/plans/`, waved folders included — and you
enumerate them with `git ls-tree`, never by walking the working tree.** A working-tree walk finds
1,271, but **364 of those are gitignored generated `containment-hook.ps1` copies**, and a gitignored
file cannot be read at a historical commit with `git show <commit>:<path>` — so a disk walk would hand
you a population your own per-commit method cannot evaluate, and 364 copies of one generated hook. This is the part the
design got wrong the first time, and it is worth stating why. The hand-run sweep enumerated plan folders
carrying a top-level `tasks/` directory and walked **533 of 850** scripts. Four plan folders are
**waved** — they nest their tasks under `wave-NN-*/tasks/` — and were silently excluded:

| folder | scripts | in the old sweep? |
|---|---|---|
| `autonomous-mode-impl` | 100 | no — waved |
| `model-tiering-stage-2` | **89** | no — waved, **and it carries the positive control** |
| `model-tiering-stage-3` | 78 | no — waved |
| `salvage-advice-provisioning` | 39 | no — waved |
| `09-preflight-first-class` | 11 | no — neither layout |

So the headline *"0 findings over 14 plan folders"* was computed over a population that structurally
excluded **the one plan known to fire**. Your enumeration must find scripts under both layouts.

**The expectation is a TABLE, per plan and per commit — NOT a blanket zero.** Put it in the test file
where a reader meets it:

| plan | commit | expected GR2060 findings |
|---|---|---|
| `model-tiering-stage-2` — `guardrails/03-dor-section-6-contract-landed.ps1` | `544f7d5` | **exactly 1**, naming `tierSource` and `docs/plans/02-schemas-and-contracts.md` |
| the same script | `5bd29da` | **0** — witness still absent, but `14-land-ssot-schema-deltas` now declares that path in its `writeScope`. This row and the one above differ ONLY in whether a task owns the file, so together they are the only rows that can catch the check firing wrongly AND going mute |
| the same script | today's HEAD | **0** — the requirement is satisfied now |
| every other plan folder | its own pre-run commit where one exists | **0** |

**Evaluate each plan at its own pre-run commit, not only at HEAD.** Today's tree is post-merge, so the
witnesses these plans required are present *because the plans ran*. A HEAD-only sweep proves the check
is silent on **satisfied** requirements and nothing more — it is structurally incapable of failing,
which makes it a gate with no teeth.

**The required non-zero is the point.** A sweep that expects zero everywhere cannot distinguish a
working check from a mute one. The `model-tiering-stage-2` row proves the sweep can fail in the
*firing* direction; the HEAD row proves it can fail in the *silence* direction. Section 11 prohibition 5
forbids re-baselining this to a tolerance or flattening it back to a blanket zero.

**Scope boundary (harness-enforced):** Write only to
`tests/Guardrails.Core.Tests/ProducerCoverageCorpusTests.cs`. After this task completes, the harness
runs a `git diff` check and rejects any edit outside that path. If a plan folder in the corpus produces
an unexpected finding, that is a **result**, not a licence to edit that plan — let the test fail, or
escalate with needsHuman naming the plan and the finding, and stop.

## Done when

- The sweep enumerates scripts under BOTH the flat `tasks/` and the nested `wave-NN-*/tasks/` layouts,
  and its own assertion proves the walked count covers the waved folders rather than 533 of 850.
- The per-plan, per-commit expectation table is in the file, including the required **non-zero**.
- The suite passes.
