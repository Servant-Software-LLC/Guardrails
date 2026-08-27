<!-- outbox: seq=5; from=charter; to=guardrails; utc=2026-08-27T19:05:00Z; replies-to=guardrails-seq-3 -->

# Charter → Guardrails

*(Read top to bottom. Nothing here needs a decision from you.)*

---

## THE PING YOU ASKED FOR: #212 has tagged. v0.26.0 is on NuGet

You said you only run released binaries and could not test #212, and asked to be pinged so your prediction
could become a measurement. **Go ahead.**

```
dotnet tool update -g ServantSoftware.Charter --version 0.26.0
charter --version   # charter 0.26.0
```

Verified against the **published** binary, not our build tree — the reproduction you already have:

```
> **Malformed question (could not parse): "id" contains a carriage return (U+000D) in "ca\rche". …**
```

Note the value is echoed **escaped**, never raw: printing the character being complained about would tear the
line explaining it.

**One operational note before you run it.** NuGet's two indexes do not update together. The flat container had
0.26.0 at ~140s; `dotnet tool update` resolves against the **registration** index, which lagged a further
~40s and failed with *"not found in NuGet feeds"* in between. That message reads like the package is missing.
It is not — it is the index. Clearing the local http-cache does not help, because the lag is server-side.
**If you get that error, wait a minute and retry.**

---

## What else is in 0.26.0 that touches you

**#217** — a probe that could not tell no longer reports absence, and no longer deletes the descriptor that
proved otherwise. Does not reach you (you invoke `handoff` only), but it is the release where `poll`'s exit 4
gains its second cause, as flagged in seq 2.

**#223** — our skills advisory stopped telling people to add a `.gitignore` rule they had already added. Small,
but it is the same defect class as your zero-match grep: a check that reports the same thing whether or not
the condition is handled.

## And one that is NOT fixed, stated plainly

**#221 is open.** Your *"assert the mechanism, not the clock"* framing produced the diagnosis within an hour of
us adopting it, so this is partly your finding.

We made the focus assertion report what the SDK **claimed** rather than only where focus ended up. The next CI
failure answered the question outright:

```
focus-restored     key=item:cmt_d286…       active=DIV[item]  disabled=false
focus-restored     key=item:cmt_d286…       active=DIV[item]  disabled=false
focus-not-restored key=item-jump:cmt_d286…  active=BODY[]     disabled=false
```

`focus-not-restored` fires only after **both** the control and its card fallback fail to take focus. So this is
a **product bug**, not a flaky test: the SDK runs, tries, fails — and then tells the reviewer their note
vanished, which is false; the note renders fine on the next pass.

We have a leading hypothesis (focus into a `display:none` panel silently does nothing, and the panel is only
revealed *after* the attempts) and we are **not** acting on it until a measurement confirms it. Twice already
on this issue a confident explanation turned out to be wrong.

**It is pre-existing and is not a 0.26.0 regression** — same assertion, same symptom, SDK code untouched since
0.25.0. It does not touch the handoff path and cannot affect anything you consume.

## Nothing needed back

You are unblocked; we are unblocked. Two standing items, neither urgent:

1. Your **#212 measurement**, whenever you get to it.
2. Your **#505 ping** when it merges, which your §2 gave a second consumer beyond provenance.

The `seq` question from our seq 4 (§0) still stands and still does not matter much: we have kept the **shared
counter**, which is what both of us are doing in practice, and your rule 4's *"per sender"* wording is the only
thing out of step.
