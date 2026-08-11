## Harness contract (do not remove)
- Read input state from the JSON file at the GUARDRAILS_STATE_IN path provided in
  the appended sections; write ONLY new/changed keys as a JSON object to
  GUARDRAILS_STATE_OUT.
- Write everything you publish under your task's WAVE-QUALIFIED id as the single
  top-level key (this plan is waved), e.g.
  `{ "wave-04-fix-the-advice/01-author-tests-salvage-advice": { "k": "v" } }`.
  The harness REJECTS a fragment keyed by anything else (every attempt).
- If a previous-attempt feedback section is appended, this is a RETRY: fix those
  specific failures; do not start over.
- If you cannot proceed without a human decision, write
  {"needsHuman": "<question>"} to the state-out path and stop.

## Task
Create `tests/Guardrails.Core.Tests/RetryPolicySalvageAdviceTests.cs` (xUnit v3) pinning the corrected
salvage advice. These tests must COMPILE and FAIL against the current `AppendSalvageSection`.

Assert:
1. **Patch route leads.** The advice presents reading `prior-attempt.patch` plus targeted edits as the
   FIRST/cheapest option, not framed as all-or-nothing "Pull in EVERYTHING". Measured data: agents use
   targeted Edit (largest real emission was 13% of the output cap), and one real hand-recovery cost
   ~4,200 output tokens.
2. **Size routing.** When `SalvageRef.DiffStat` shows few changed lines, the advice points at reading the
   hunk and editing; when a file is essentially new, at pulling the whole blob.
3. **The acceptance invariant (load-bearing):** every command the emitted advice names is `git show`, the
   patch path, or a file-editing tool - NOTHING else. In particular `git diff <taskBase> <ref>` must be
   GONE: it is outside the injected grant, so leaving it reproduces this exact defect in miniature.
4. No copy-pasteable `git <write-verb>` invocation appears (checkout / restore / reset / stash), while
   the prose MAY still name those verbs to warn they are ungranted.

Do NOT implement the change. Do NOT weaken the existing #374 regression test in `RetryPolicyTests`.

**Scope boundary (harness-enforced):** Write only to
`tests/Guardrails.Core.Tests/RetryPolicySalvageAdviceTests.cs`. The harness runs a `git diff` check and
rejects any edit outside that path - including `RetryPolicy.cs` (task 02 owns it) and the existing
`RetryPolicyTests.cs`. An out-of-scope edit fails the task immediately and consumes a retry. If a missing
symbol elsewhere blocks compilation, do NOT edit that file - write {"needsHuman": "<what is missing>"}.
