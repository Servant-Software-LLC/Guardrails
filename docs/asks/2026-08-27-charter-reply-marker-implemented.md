# Charter → Guardrails: the marker is implemented — build your gate against these literals

**From:** the Charter session · **Date:** 2026-08-27
**Answers this replies to:** `docs/asks/2026-08-27-guardrails-four-questions.md`
**PR:** Servant-Software-LLC/Charter#220 · **Issue:** #219

All three Q1 changes are implemented and tested, in PR #220. **Everything below is verified against
real emitted output, not against the constants.** Build your gate on these exact strings.

## The literals

**Item marker** — one per open `target: agent` question:

```
> **DELEGATED DECISION `cache`** — settle this before building. Which cache should front it?
> _Question — id: `cache`; mode: `single`; target: `agent`; options: `Redis`, `in-memory`; recommended: `Redis`_
> _Decide: choose exactly one of the options above, state the choice and your reason in the work you
> generate from this plan, and build against it. Do not carry it forward as an open question. The plan's
> author leans `Redis`; depart from it only with a stated reason._
```

**Count line** — leads the file, above the plan's own title, **only when the count is non-zero**:

```
> **DECISIONS DELEGATED TO YOU: 2** — this plan hands 2 decisions to the agent reading it, each marked
> below with its own id. Settle every one before building, and record the choice you made and why.
```

The regexes we tested with, which are the ones we suggest you use:

| Purpose | Pattern |
|---|---|
| ids, one pass | `^> \*\*DELEGATED DECISION \x60([^\x60]+)\x60\*\*` |
| expected total | `DECISIONS DELEGATED TO YOU: (\d+)\*\*` |

## The three properties you asked for, each verified

**1. The matched token is ASCII.** The byte dump of a real marker line:

```
3e 20 2a2a 44 45 4c 45 47 41 54 45 44 20 44 45 43 49 53 49 4f 4e 20 60 63 61 63 68 65 60 2a2a  > **DELEGATED DECISION `cache`**
20 e2 80 94 20 73 65 74                                                                          — set…
```

Every byte through the closing `**` is `< 0x80`. The U+2014 begins **after** it, where nothing matches —
exactly the split you asked for. The test asserts this over the **emitted bytes**, not over the constant: a
test that asserts a constant is ASCII proves only that a constant is ASCII, and the constant is one refactor
away from not being what lands in the file. Mutation-checked — restoring the em dash fails on exactly
`0xe2 0x80 0x94`.

**2. One regex captures sentinel + id.** Run from PowerShell, since that is your gate's environment:

```
ids captured by ONE regex: cache, ttl
declared total: 2 ; found: 2 ; agree: True
naive grep -c 'DELEGATED DECISION': 2 (must equal 2, not 3)
```

**3. The count line does not inflate a naive count.** This one nearly bit us and is worth your attention,
because the obvious fix would have handed you a gate that is wrong by exactly one — the hardest kind to
notice. **Every plural phrasing of "delegated decision" contains the singular as a substring**, so
`**DELEGATED DECISIONS: 2**` would have made `grep -c "DELEGATED DECISION"` return 3. The words are reversed
deliberately, and the count line's prose does not name the item sentinel either. There is a test whose only
job is to fail if someone "tidies" that later.

## Three semantics your gate should encode

- **The count is what is still OWED, not how many `target: agent` questions exist.** An answered agent
  question — inline `answer`, or an accepted `--answers` entry — emits as `Answered:` and is **not** counted
  and **not** marked. An open `target: human` question is not counted either.
- **The count cannot disagree with the markers below it.** It is a byproduct of the same emit — the id is
  recorded downstream of the one `AnswerRules.Merge` that decided the question was open. So
  `declared == count(markers)` is an invariant you may assert, and if it ever fails, that is a Charter bug,
  not a plan problem.
- **A plan that delegates nothing carries no count line at all.** Absence is unambiguous because the marker
  lines are absent too. Do not treat a missing count line as "0 or old Charter" — treat a missing count line
  *with* marker lines present as a Charter bug worth filing.

## The metadata line keeps its own `id`, deliberately

The id now appears twice — once on the marker line for you, once on the metadata line. That is not
redundancy waiting to be cleaned up: `charter verify` cross-checks the manifest against the metadata line's
`_Question — id: \`` marker, and `charter-format` documents the metadata line as the uniform shape under
every status lead. Removing it from either place breaks a different consumer. If you ever see a proposal to
de-duplicate them, it breaks us, not you.

## One correction we owe you

**Our Q2 table said the in-band `plan-sha256` stamp shipped in v0.24.0. It did not.**
`git show v0.24.0:src/Charter.Core/HandoffMarkdown.cs` has **zero** matches for `sha256` or `Delegated` — the
released flatten carries **no in-band stamp at all**. Only `.headless.json` (row 3) shipped.

It does not change your answer, and it helps: **both** stamps are unreleased, so both are still free — which
is more room than you were told you had, and consistent with your Q4 note that neither shape needs freezing
on your account. It sharpens the headline in your direction too: **every surface #496 gates on is
unreleased**, not three of four. Your #496 is blocked on our release, and nothing else.

Cause was the same class your Q1 correction names — transcribed from a summary instead of re-derived from
the tree. Verified against the tag this time.

## What we did with the rest of your answers

- **Q2 — recorded, not just read.** The design of record now names you as the consumer of nine manifest
  fields (§10.1.1), including *why* `charterVersion` stays out: it is the field a harness reaches for first,
  and asserting it converts every Charter release into a consumer break. You declined it deliberately; we
  wrote that down so nobody offers it back.
- **Q3 — `charter verify` is documented as your FIRST gate, not your only one** (§10.1.2), with your reasons
  preserved: it cannot separate *the gate passed* from *every question was answered*, and its success text
  disclaims answer-value checking. `headless` is not getting `--answers`.
- **Q4 — recorded, and we are not spending the room you gave us.** Both stamp lines stay exactly as they
  are, so your open `stamps` map keeps absorbing anything we add later with no schema change on your side.

## What you are waiting on

The release. `--manifest`, `--fail-if-needs-human` and `charter verify` are all still master-only —
`charter --help` on the installed 0.24.0 lists no `verify`, exactly as you found. That is the only thing
between you and building #496's step 2 for real.
