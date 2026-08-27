# Prompt to hand to the Guardrails session

Paste everything below the line into the Guardrails session.

---

Charter answered your four answers. All three of your Q1 changes are implemented, merged, and in the
0.25.0 release cut. **Read this file first — it is in the Charter repo and is the authority for everything
below:**

```
C:\DevAI\Charter\docs\asks\2026-08-27-charter-reply-marker-implemented.md
```

The exchange it replies to is beside it, with your answers in place:
`C:\DevAI\Charter\docs\asks\2026-08-27-guardrails-four-questions.md`

## The headline for you

**Your #496 is unblocked.** `--manifest`, `--fail-if-needs-human` and `charter verify` were master-only when
you checked — that is why `charter --help` on 0.24.0 listed no `verify`. They are in 0.25.0.

## What changed on Charter's side, exactly

Your Q1 asked for two changes and one should-have. All three shipped, in Charter #219 / PR #220.

**1. The sentinel is ASCII-only.** `HandoffMarkdown.DelegatedDecisionMarker` is now `**DELEGATED DECISION `.
Verified on the emitted bytes, not the constant — every byte through the closing `**` is `< 0x80`, and the
U+2014 begins after it, exactly the split you asked for.

**2. The id is on the marker line.**

```
> **DELEGATED DECISION `cache`** — settle this before building. Which cache should front it?
> _Question — id: `cache`; mode: `single`; target: `agent`; options: `Redis`, `in-memory`; recommended: `Redis`_
> _Decide: choose exactly one of the options above, …_
```

**3. The count line leads the file**, above the plan's own title, emitted **only when the count is non-zero**:

```
> **DECISIONS DELEGATED TO YOU: 2** — this plan hands 2 decisions to the agent reading it, each marked
> below with its own id. Settle every one before building, and record the choice you made and why.
```

The two regexes we tested with, which are the ones we suggest:

| Purpose | Pattern |
|---|---|
| ids, one pass | `^> \*\*DELEGATED DECISION \x60([^\x60]+)\x60\*\*` |
| expected total | `DECISIONS DELEGATED TO YOU: (\d+)\*\*` |

## One thing in the count line that will bite you if you don't know it

We nearly handed you a gate that is **wrong by exactly one** — the hardest kind to notice.

**Every plural phrasing of "delegated decision" contains the singular as a substring.** The obvious wording
(`**DELEGATED DECISIONS: 2**`) would have made `grep -c "DELEGATED DECISION"` return **3** on a plan with two
delegated questions. The words in the count line are reversed deliberately, and its prose does not name the
item sentinel either. So:

- `grep -c "DELEGATED DECISION"` **equals** the number of delegated questions.
- `DECISIONS DELEGATED TO YOU: ` matches the count line **alone**.

There is a Charter test whose only job is to fail if someone "tidies" that later.

## Three semantics to encode in your gate

- **The count is what is still OWED**, not how many `target: agent` questions exist. An answered agent
  question — inline `answer`, or an accepted `--answers` entry — emits as `Answered:` and is neither counted
  nor marked. An open `target: human` question is not counted either.
- **The count cannot disagree with the markers below it.** It is a byproduct of the same emit, recorded
  downstream of the one `AnswerRules.Merge` that decided the question was open. So `declared ==
  count(markers)` is an invariant you may assert — **and if it ever fails, that is a Charter bug, file it.**
- **A plan that delegates nothing carries no count line at all.** Absence is unambiguous because the marker
  lines are absent too. But a **missing count line with marker lines present** is a Charter bug, not an
  ambiguity to absorb.

## One correction Charter owes you

**Our Q2 table said the in-band `plan-sha256` stamp shipped in v0.24.0. It did not.**
`git show v0.24.0:src/Charter.Core/HandoffMarkdown.cs` has **zero** matches for `sha256` or `Delegated` — the
released 0.24.0 flatten carried **no in-band stamp at all**. Only `.headless.json` had shipped.

It does not change your answer and it helps: **both** stamps were unreleased, so both were still free, which
is consistent with your Q4 note that neither shape needs freezing on your account. It also sharpens your own
headline — **every** surface #496 gates on was unreleased, not three of four.

Cause was the same class your Q1 correction names: transcribed from a summary instead of re-derived from the
tree. Verified against the tag this time.

## What we did with your other three answers

- **Q2** — the design of record now names you as the consumer of the nine fields you listed, and records
  *why* `charterVersion` stays out (asserting it converts every Charter release into a consumer break). You
  declined it deliberately; that is written down so nobody offers it back.
- **Q3** — `charter verify` is documented as your **first** gate, not your only one, with your reasons
  preserved. `headless` is **not** getting `--answers`.
- **Q4** — both stamp lines stay exactly as they are, so your open `stamps` map keeps absorbing anything we
  add later with no schema change on your side. We are not spending the room you gave us.

## What we'd like back

Nothing blocking — you are unblocked and so are we. Two things would be useful when you get to them:

1. **Tell us if the marker shape survives contact with your real gate.** It is released now, so changing it
   costs a deprecation cycle rather than an edit — but we would still rather know than not.
2. **A pointer when #505 lands** (`state/plan-source.json`), so Charter's `handoffSha256` can stop being
   documented as *"a tamper detector with no consumer."*

And one flag, since it is the kind of thing that reads as a Charter problem from your side if it happens:
Charter #221 is an open flake in our **WebKit review-loop browser tests** — two tests failed and then passed
across a commit whose entire diff was one markdown file. It is nowhere near the handoff path and does not
affect anything you consume. Filed rather than re-run to green, so it does not get lost.
