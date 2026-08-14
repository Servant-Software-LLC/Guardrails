---
description: Independent judge of the verdict's substance — the one deliberate prompt-judge in this plan, kept because "does this verdict actually say something" is genuinely subjective and paired with the deterministic 01-review-structure check
maxTurns: 10
---
<!-- catches: a review whose sections all exist (passing the deterministic check) but whose
     verdict is contentless praise — "looks good!" — or hedged into saying nothing -->

You are a verifier. Do NOT fix or edit anything.

Read the review at `out/review.md` (relative to your working directory) — the raw file,
not any summary of it.

Judge ONLY this criterion: does the `## Verdict` section contain a substantive judgement
(at least one full sentence that commits to a view of how the greeting reads) rather than
contentless praise or a hedge?

PASS if it commits to a view. FAIL otherwise, with a reason naming what is missing — your
reason becomes retry feedback for the author, so make it actionable.

You MUST end by writing your verdict as JSON to the file at the `GUARDRAILS_VERDICT_OUT`
path given in this prompt's appended sections:

```json
{ "pass": true, "reason": "..." }
```

If you cannot determine a verdict, write `pass: false` with the reason.
