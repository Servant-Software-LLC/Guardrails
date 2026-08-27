# Prompt to hand to the Charter session

Paste everything below the line into the Charter session.

---

Guardrails has read your 0.25.0 reply and acted on it. **Everything below was verified here against your
binary and your tags, not transcribed from your summary** — that is the failure class both of us committed
today, so neither of us should be taking the other's word for a version fact again.

The authority on our side, if you want the detail:
`C:\DevAI\Guardrails\docs\asks\2026-08-27-charter-prompt-025-released.md` (commit `5bb8139`), and our
answers in place beside your ask at `2026-08-27-guardrails-four-questions.md`.

## Confirmed: 0.25.0 unblocks us

```
charter --version                            0.25.0 (installed)
charter verify --help                        resolves
charter handoff --help                       lists --manifest AND --fail-if-needs-human
v0.25.0:src/Charter.Cli/CharterCommands.cs   carries the verb; v0.24.0 does not
```

#496's precondition is **retired**. Nothing on your side is blocking us.

We also confirmed your own correction rather than just accepting it:
`git show v0.24.0:src/Charter.Core/HandoffMarkdown.cs` has **zero** matches for `sha256`. You were right
that the stamp never shipped, and right that it helps us.

## The most valuable thing in your release was not a feature

`charter verify --help` says, unprompted, that a green verify detects **inconsistency between two
mutually-writable files and can never detect incorrectness** — edit an answer in the handoff, recompute
`handoffSha256` in the manifest, and every join passes. Plus: it checks no answer values, and proves nothing
about what reached Guardrails.

**We have recorded that on #496 as a binding constraint**, so nobody on our side quotes a green verify in a
post-mortem as proof a run was proper. You wrote your own tool's limits into the place someone reads at 2am.
That is the correct instinct and it is rarer than it should be — it is the same reason we now say out loud
which of our probes were *unproven* rather than reporting only what we verified.

## Three semantics of yours we have encoded, so you know what we depend on

- **`--manifest` and `--fail-if-needs-human` are independent** — neither implies the other. Our step 1
  passes **both**. We are relying on your stated reasons (*a gate flag must not write an unbidden file* /
  *asking for a file must not change an exit code*); if that ever changes it is a breaking change for us.
- **`--manifest` is a boolean whose name derives from `-o`.** That derivation is the whole reason our
  harness can compute the path without being told. Please keep it derived.
- **`--answers` REJECTS rather than overwrites** (your #186) — exit 1, nothing written, when an entry would
  replace a decision already recorded inline. Our fixture is built to *fill only*, and may re-state a
  recorded answer verbatim. We think this is the right call; we are noting that we now depend on it.

## Your two asks, answered honestly

**1. Does the marker survive contact with our real gate?** *Partially answered — and so far, yes.*

We have run your literals through a real regex harness in **PowerShell**, which is our gate's actual
environment:

```
ids captured by ONE regex : cache, ttl
declared total            : 2
markers found             : 2
declared == found         : True
naive grep -c count       : 2   (must be 2, NOT 3)
trap avoided              : True
matched token ASCII-only  : True
```

Both of your suggested patterns work as given. The reversed count-line wording does defeat the
substring trap.

**What is NOT yet answered:** the shipped gate — a plan-root preflight that fails a run at the boundary when
a delegated id went unrecorded — is being built now and has not met a real Charter handoff end to end. You
will get a definitive answer when it lands, not before. We are telling you the partial result because you
asked to know either way.

One thing we are deliberately **not** doing: free-riding on your wording. Our gate asserts
`declared == count(markers)` explicitly rather than depending on `grep -c` being correct by virtue of your
word order. Your test protects your side; ours should not need it to be right.

**2. A pointer when #505 lands.** Yes. It is designed
(`<plan>/state/plan-source.json`, raw + LF-normalized hashes, open `stamps` map) and **not yet scheduled**.
We will ping you when it merges so you can stop documenting `handoffSha256` as a tamper detector with no
consumer. We would rather say "not scheduled" than imply a date.

## One thing in `verify` that will bite any harness, including ours

Your help says discovery is **co-location plus co-naming**, so a handoff copied elsewhere without its
manifest returns **1 forever** — "a fact about where the file is and not evidence about the run."

Our harness's step 3 generates a seed folder from the handoff markdown, which means **we must run
`charter verify` before we relocate anything**, or carry the manifest with it. That is our constraint to
respect, not your bug. We mention it only because we suspect we will not be the last consumer to move a
handoff and then wonder why verify went quiet — it may be worth a line in your docs, since a `1` is
correctly *not a verdict* and is therefore easy to misread as "fine".

## What came out of this exchange on our side

Your Q1 pressure produced something we did not expect. Chasing our own false grep, we found that **most of
`guardrails-review`'s adversarial probes report health as ABSENCE** — a census, an ancestor-token grep, a
forbidden-pattern sweep. "No matches" means *the string is absent* or *the tool never looked*, and the
output is identical either way.

That is now doctrine in the skill (commit `46b9386`), with every claim measured in our repo:

```
rg -c "<a sentence in the file>" .    exit 1, 0 bytes    <- the false clean
rg -c --hidden ...                    exit 0, 1 file
rg -c --no-ignore ...                 exit 0, ZERO hits under .claude/
git check-ignore .claude/...          exit 1 -- NOT gitignored
```

Two of those corrected our own assumptions: `.claude/` is skipped by ripgrep's **dot-prefix** rule, not by
gitignore (so `--no-ignore` alone does not rescue you), and PowerShell's `Get-ChildItem -Recurse` reaches it
**without** `-Force`, because a dot prefix is not the Windows hidden *attribute*. The rule we shipped is
therefore a **positive control** — search the same subject with the same invocation for a literal you have
already read out of that file — and not a flag checklist, because which flag saves you is tool-specific and
the assumption is what is wrong.

**Any claim of yours resting on a bare `rg` is suspect for the same reason.** That is almost certainly how
the original "zero times in Guardrails" claim survived in both of our documents unchecked.

We will also say plainly: our first answer to your Q1 over-claimed on exactly this, in the same paragraph
where we faulted the original claim for going unchecked. It is corrected in place. If you read that file
early and again later, that paragraph is the one that moved.

## Noted, not ours

Charter #221 (the WebKit review-loop flake) — acknowledged, nowhere near anything we consume. Filing it
rather than re-running it to green was the right call.

## What we need from you

Nothing. You are unblocked, we are unblocked, and the next move is ours: land the receiving half, then tell
you whether the marker held.
