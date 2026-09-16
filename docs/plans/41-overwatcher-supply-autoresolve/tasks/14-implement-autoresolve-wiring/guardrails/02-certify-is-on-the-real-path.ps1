# catches: a wiring that reaches the deterministic gate's RESULT without ever calling the gate —
#          the #712 shape, one level up. Design §3.1 warns of it by name: "A wiring that checked
#          'any resource-supply op' and never called Certify is exactly the #712 shape again." Such
#          a wiring supplies the file, so P1/P2 and most controls in the wiring proof go green; only
#          C3/C5/C6 catch it there, and each of those is one fixture edit away from being satisfied
#          by a hand-rolled copy of the gate's refusal list inlined into Scheduler.cs. This check
#          says the gate itself must be on the path: the pure function OWNS the rule (§3.1), so a
#          future caller cannot skip it.
#
# Source-shape check over CODE, and it is NOT demotable to a test: no test can assert which function
# another function called, only what came out. The runtime half — that the refusals actually fire —
# is carried by guardrail 01's C3/C5/C6. Nothing is greped here that a test could prove.
#
# Author-time smoke test (#302), re-runnable (#468):
#   $env:GR_SUBJECT='docs/plans/41-overwatcher-supply-autoresolve/tasks/14-implement-autoresolve-wiring/samples/02-certify-is-on-the-real-path.valid.cs';   ./02-certify-is-on-the-real-path.ps1  # expect 0
#   $env:GR_SUBJECT='docs/plans/41-overwatcher-supply-autoresolve/tasks/14-implement-autoresolve-wiring/samples/02-certify-is-on-the-real-path.invalid.cs'; ./02-certify-is-on-the-real-path.ps1  # expect 1
#
# baseline counts on the untouched tree (#478) — MEASURED with Select-String, not assumed. Both are
# censused against the text the clauses actually READ ($code: string literals neutralized, then
# comments stripped), over this clause's own subject src/Guardrails.Core/Execution/Scheduler.cs:
#   \.\s*Certify\s*\(                0   (Select-String -CaseSensitive '\.Certify\s*\(' -> 0 raw. The
#                                        only two case-INSENSITIVE 'Certify' hits in the file are the
#                                        English word "certify" inside // comments at :995 and :5000,
#                                        which the comment strip removes — so the $code count is 0 too.)
#   \bOverwatchSupplyAutoResolve\b   0   (Select-String -CaseSensitive -> 0 raw; the type has ZERO
#                                        production callers today, which IS issue #712.)
#   ancestor check: the ONLY ancestor of task 14 whose writeScope includes Scheduler.cs is
#   13-implement-observer-by-field, and its change is the IRunObserver.SuppliedResourcesCommitted
#   `by` parameter plus that one Scheduler call site. Neither token is part of it, so today's 0 is
#   still 0 on the tree this task will see. Asked and recorded, per #478.
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

$f = if ($env:GR_SUBJECT) { $env:GR_SUBJECT } else { "src/Guardrails.Core/Execution/Scheduler.cs" }

# PRECONDITION — the only early exit: every clause below would crash on a missing subject.
if (-not (Test-Path $f)) {
    Write-Output "$f does not exist — there is no Scheduler to inspect for the auto-resolve wiring."
    exit 1
}

# Variable discipline (catalogue -> the two-variable rule): $raw is NEVER matched against.
# Neutralize string literals BEFORE stripping comments (#561): doing it the other way runs the
# comment-strip regex over text where a literal spelling '/*' or '*/' can forge a delimiter and blank
# real code before it is ever scanned.
$raw  = Get-Content $f -Raw
$lit  = [regex]::Replace($raw, '"""[\s\S]*?"""', '""')          # C# 11 raw strings
$lit  = [regex]::Replace($lit, '@"(?:[^"]|"")*"', '""')         # verbatim strings
$lit  = [regex]::Replace($lit, '"(\\.|[^"\\])*"', '""')         # ordinary + interpolated strings
$code = [regex]::Replace($lit,  '/\*[\s\S]*?\*/', '')           # /* */ block comments
$code = [regex]::Replace($code, '(?m)//.*$', '')                # // line comments

# BOTH required clauses read $code — the literal-neutralized, comment-stripped copy — not the
# comments-only strip that §11a's required clauses usually take. A dotted CALL construct and a C#
# TYPE reference can never legitimately live inside a string literal in this subject, so reading the
# neutralized copy cannot false-red a correct implementation; and it closes the loophole where a log
# message spelling "OverwatchSupplyAutoResolve.Certify(...) was not consulted" satisfies both clauses
# while nothing calls anything. The measured baselines above are stated against this same $code.

# ACCUMULATE (#478): one distinguishable message per clause, dumped once at the end, so ONE attempt
# learns every gap instead of discovering them one retry at a time.
$failures = @()

# ANCHORED ON THE CALL, NOT THE NAME (#76, #521). A clause ending at the dotted NAME is satisfied by
# `nameof(OverwatchSupplyAutoResolve.Certify)` — valid C# containing that exact text, which is not a
# string literal and therefore survives the strip above. The trailing \s*\( is the whole rule.
#
# WHY (?<![\w]) AND NOT A REQUIRED DOT (#479, review fix). The first spelling of this clause was
# '\.\s*Certify\s*\(' — a REQUIRED dotted call. That constrains the SHAPE of correct code rather than
# the outcome: `using static Guardrails.Core.Execution.OverwatchSupplyAutoResolve;` followed by a bare
# `Certify(...)` is a correct wiring that the dotted form REJECTS, and #479 is the most expensive
# authoring defect measured in this catalogue precisely because such a clause reads perfectly and
# false-REDS work that is already right. The lookbehind accepts both spellings (a `.` is not \w, so
# `.Certify(` still matches) while still rejecting `XCertify(`; and it stays safe because the subject
# is Scheduler.cs, where no `Certify` is DECLARED — the declaration lives in OverwatchDecision.cs, so
# a bare match here can only be a call. `nameof(...Certify)` has no `(` after the name and is still
# rejected.
#
# The cost of this widening, stated rather than hidden: the two clauses are no longer PAIRED, so a
# `.Certify(` on some other type plus a bare OverwatchSupplyAutoResolve reference elsewhere would
# satisfy both. That is contrived (the gate is a static class with no other production caller, which
# IS #712), and the runtime half is covered by guardrail 01's C3/C5/C6 — whereas false-redding a
# correct `using static` implementation would dead-end the task. WEAK-pass over BLOCKER-fail is the
# right trade here.
if ($code -cnotmatch '(?<![\w])Certify\s*\(') {
    $failures += "$f never CALLS a dotted .Certify( — the deterministic gate is not on the real path. Design §3.1: the Scheduler may not reach the gate's RESULT by re-deciding it inline ('a wiring that checked any resource-supply op and never called Certify is exactly the #712 shape again'). A mention is not a call: nameof(...), a comment, or the name inside a log message does NOT satisfy this — invoke it."
}

# The TYPE half of the #76 pairing: the dotted call must belong to the certification gate, not to some
# other Certify the file happens to reach.
if ($code -cnotmatch '\bOverwatchSupplyAutoResolve\b') {
    $failures += "$f contains no reference to OverwatchSupplyAutoResolve — the certification gate (design §3.1) is not reached from the Scheduler at all. This is issue #712 verbatim: the gate shipped in plan 40 with ZERO production callers, and this task exists to give it one."
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Output $_ }
    exit 1
}
exit 0
