# catches: the #382 defect in its purest form - a WebhookEventSink that is fully built, fully unit-tested
#          and completely GREEN (tasks 06/07) while the ENTRY POINT never constructs it, so the feature is
#          reachable only from xUnit and inert from `guardrails run`. Design section 10 calls this pair
#          "the row that matters most (#382)"; this is the one-second backstop under the two-minute proof.
#
# WHY IT RUNS FIRST, and why it is not redundant with guardrail 02. Guardrail 02 (the delivery census) is
#          the real proof and this cannot pass while that one fails. But it costs ~1 second against ONE
#          file, and it answers the single most likely cause of a total delivery failure - "is the sink
#          even constructed?" - in one line. Under guardrailMode failFast a ~2-minute integration run
#          sitting in front of it would report the SYMPTOM ("nothing arrived at the receiver") and never
#          reach the CAUSE, so cheapest-first here is about diagnosis quality, not just wall clock. It
#          also survives a scenario guardrail 02 does not: an integration suite that goes green for a
#          reason nobody predicted still leaves this clause red if the sink is never constructed.
#
# WHAT THIS DOES NOT COVER, stated rather than implied, and WHY THE GAP IS CLOSED WITH A TEST. The env
#          fallbacks GUARDRAILS_ON_EVENT / GUARDRAILS_ON_EVENT_AUTH are a section 6.4 deliverable, and a
#          required-present clause for them is DELIBERATELY absent from this file. Its subject is one
#          file, but this task's writeScope is the whole of src/Guardrails.Cli/, so a correct
#          implementation is free to put the env read in a helper alongside RunCommand.cs - a clause
#          pinned to RunCommand.cs would then false-red correct work (#193), which is the shape that
#          dead-ends a task it cannot fix. They are covered instead by task 08's
#          EnvVarSuppliesTheEndpointWhenTheFlagIsAbsent, censused RED there and Passed in guardrail 02
#          here: it runs with NO --on-event flag, both variables set, and asserts both that rows arrived
#          and that the Authorization header is the verbatim env value. That is the only fixture in the
#          plan that can reach GUARDRAILS_ON_EVENT_AUTH at all, since it is env-only and no flag exists
#          for it. Do NOT "strengthen" this file by adding a grep for either name.
#
# Source-shape check over CODE, and it is NOT demotable to a test (#468): the property is "the production
#          entry point file references this constructor", which is structural about RunCommand.cs itself.
#          The RUNTIME half - that the sink is constructed early enough and disposed late enough that the
#          terminal row still arrives - is carried by guardrail 02's RunFinishedArrives and
#          RunFinishedArrivesWhenTheReceiverIsSlow, so nothing is grepped here that a test already proves.
#          In particular there is NO clause on `await using`, on the construction's POSITION in the file,
#          or on the unwind order: those are HOW, they have more than one correct spelling, and the tests
#          above already fail if any of them is wrong.
#
# THE SAMPLE SUBJECT ARRIVES AS THE POSITIONAL ARGUMENT, NEVER FROM THE ENVIRONMENT - and that is a
#          correctness requirement, not a style choice. `SampleVerifier.RunSampleAsync` binds the sample
#          BOTH ways (`src/Guardrails.Core/Samples/SampleVerifier.cs`: the absolute path as the
#          guardrail's first positional argument AND in `GR_SUBJECT`), so either spelling satisfies
#          `guardrails samples verify` and the pre-DAG sample preflight. Only ONE of them is safe here.
#          `GR_SUBJECT` is OUTSIDE the `GUARDRAILS_` prefix, so `ProcessRunner.ApplyEnvironment` does not
#          strip it (it filters on `HarnessEnvPrefix`, ProcessRunner.cs:152 and :203) - and a PowerShell
#          `$env:` assignment persists for the whole session. An operator who ran the documented smoke
#          test and then `guardrails run` in the same shell got THIS guardrail certifying the VALID
#          SAMPLE while RunCommand.cs was never opened: measured exit 0. Section 6.4 argues the
#          `GUARDRAILS_` namespace is load-bearing precisely because it is hermetic, and this file sat
#          outside it. The positional argument cannot leak that way: a real run launches a guardrail as
#          `pwsh -File <script>` with the definition's own args, and this guardrail declares none, so
#          $args is EMPTY at run time by construction and the default below is the only reachable
#          subject. Do NOT reintroduce an env-var override, and do NOT rename the env var either - the
#          name is the HARNESS's constant (SampleVerifier.SubjectEnvironmentVariable), so renaming it
#          here would leave the committed pair unverifiable and fail the pre-DAG preflight.
#
# Author-time smoke test (#302), re-runnable (#468) - no environment variable to leave set behind you:
#   ./01-composition-root-constructs-the-sink.ps1 'docs/plans/36-onevent-webhooks/tasks/09-implement-cli-wiring/samples/01-composition-root-constructs-the-sink.valid.cs'    # expect 0
#   ./01-composition-root-constructs-the-sink.ps1 'docs/plans/36-onevent-webhooks/tasks/09-implement-cli-wiring/samples/01-composition-root-constructs-the-sink.invalid.cs'  # expect 1
#
# Measured baseline (#478) on the untouched tree, 2026-09-04 - MEASURED, not assumed:
#   WebhookEventSink                              -> 0 occurrences in src/Guardrails.Cli/Commands/RunCommand.cs
#   WebhookEventSink\s*\.\s*TryStart\s*\(         -> 0 occurrences in the same file
#   (and 0 occurrences of "WebhookEventSink" anywhere across src/ and tests/ - the type is created by
#    this plan's task 04/07. Expected 0; no exemption needed.)
$f = if ($args.Count -ge 1 -and $args[0]) { $args[0] } else { "src/Guardrails.Cli/Commands/RunCommand.cs" }

# PRECONDITION - the only early exit: the clause below would crash on a missing subject.
if (-not (Test-Path $f)) {
    Write-Output "$f does not exist - the CLI entry point this task wires is not where this check looks. Do NOT create it; this is a guardrail/tree mismatch, not work for this task."
    exit 1
}

$raw  = Get-Content $f -Raw                                  # NEVER matched against
$code = [regex]::Replace($raw,  '/\*[\s\S]*?\*/', '')        # /* */ block comments
$code = [regex]::Replace($code, '(?m)//.*$', '')             # // line comments
$scan = [regex]::Replace($code, '"""[\s\S]*?"""', '""')      # C# 11 raw strings
$scan = [regex]::Replace($scan, '@"(?:[^"]|"")*"', '""')     # verbatim strings
$scan = [regex]::Replace($scan, '"(\\.|[^"\\])*"', '""')     # ordinary strings

# ACCUMULATE (#478): one distinguishable message per clause, dumped once at the end.
$failures = @()

# ANCHORED ON THE CALL, NOT THE NAME (#521, measured 2026-08-28). A clause ending at the dotted NAME is
# satisfied by `nameof(WebhookEventSink.TryStart)` - valid C# containing that exact text, which survives
# the $scan strip because nameof is not a string literal - and a hollow wiring whose only reference was a
# dead nameof expression, with ZERO invocations, was MEASURED to exit 0 against exactly that shape. The
# trailing `\s*\(` is the whole rule. TryStart is a static factory METHOD, so every correct call site
# writes it in the `Type.Member(` shape and requiring the paren cannot false-red correct work. Do NOT
# relax this back to a bare dotted name, and do NOT add a nameof BAN: requiring the CALL already kills the
# operator, and a ban would false-red a legitimate nameof() inside a diagnostic message.
if ($scan -cnotmatch 'WebhookEventSink\s*\.\s*TryStart\s*\(') {
    $failures += "$f never CALLS WebhookEventSink.TryStart - the CLI composition root does not construct the webhook dispatcher, so --on-event delivers nothing however complete and green WebhookEventSink's own unit tests are (#382, design section 10 row 7). A mention is not a call: a comment, a string literal, or nameof(WebhookEventSink.TryStart) does NOT satisfy this - invoke it, at the construction point section 3.3 pins (grep for the `diagramSeed` local; the construction goes on the line after it is read, above the `OnTheFlyDiagramObserver? diagramObserver = null;` bracket)."
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Output $_ }
    exit 1
}
exit 0
