# catches: this task landing its TESTS and silently dropping its STUBS. Task 02 owes three
#          deliverables - the RunEventBracketTests file, the EventDelivery record with the widened
#          RunEventStream constructor, and the MaxChars visibility promotion - and until this file
#          existed the last two had NO independent clause anywhere in the plan folder.
#
# WHY THE TRANSITIVE ARGUMENT DOES NOT HOLD, and it was MEASURED rather than reasoned about. The
#          sibling 02-tests-build.ps1 used to claim it proved the stubs were real, on the grounds that
#          "the tests name EventDelivery, the two new ctor parameters and GuardrailFailureReason.MaxChars,
#          and none of them compiles unless this task actually added them". That claim is transitive
#          through content NOTHING ENFORCES: a RunEventBracketTests referencing none of the three was
#          written and BOTH of this task's guardrails exited 0 over it. The prompt asks for the
#          references; no check requires them; the red census below grades OUTCOMES, not source text.
#          The earliest thing that would have caught the missing stubs is task 06's build failure -
#          four tasks downstream, reported against a task that did not cause it.
#
# CHEAPEST FIRST (#478 rule 4), and that is why this file is 01. It is a ~0.1s regex over two files;
#          02-tests-build.ps1 is a ~15s build and 03-tests-fail-on-stubs.ps1 a ~25s test run. Under
#          guardrailMode failFast a missing stub is now reported in a tenth of a second, by name,
#          instead of after both of them have run green.
#
# SOURCE-SHAPE CHECKS OVER CODE, and the report owes a line for each (#468) - so here it is. Both
#          clauses are OUTCOME-shaped, not HOW-shaped: "a delivery record type named EventDelivery
#          exists in this file" and "the cap constant is visible outside its own class". Neither has a
#          runtime proxy, and neither is demotable to a test:
#          * EventDelivery. Its EXISTENCE is what the whole plan's wire copy is typed on, but this
#            task deliberately lands it INERT (accepted and ignored - see "The stubs" in the prompt),
#            so there is no behaviour to assert and no test can observe it. Task 03 is where it gets
#            behaviour, and a task-03 test cannot certify a task-02 deliverable.
#          * MaxChars. The INTENDED proxy was "a test that would not compile if it were still private
#            carries it better than a regex" - and that proxy is exactly what the measurement above
#            falsified, because nothing pins the test's content. A visibility keyword has no runtime
#            behaviour to test at all: `internal` and `private` produce identical IL for the reader
#            inside the class, and the only observer is another assembly's COMPILER.
#          Nothing about HOW is pinned. There is no clause on `public`, on `readonly`, on the record's
#          parameter list, on the constructor's defaulted parameters, or on the `_ = onRow;` discard
#          spelling - those have more than one correct form and the sibling guardrails already fail if
#          any of them is wrong (a CS0414 from a stored-but-unread field fails 02-tests-build.ps1; a
#          non-defaulted parameter breaks the ~20 existing call sites and fails it too).
#
# Measured baseline (#478) on the untouched tree, 2026-09-04 - MEASURED, not assumed:
#   record\s+struct\s+EventDelivery\b    -> 0 in src/Guardrails.Core/Execution/RunEventStream.cs
#                                          (and 0 occurrences of the bare word "EventDelivery" in that
#                                           file, and 0 anywhere across src/ and tests/)
#   internal\s+const\s+int\s+MaxChars\b  -> 0 in src/Guardrails.Core/Execution/GuardrailFailureReason.cs
#                                          (the line reads `private const int MaxChars = 2000;` today -
#                                           1 occurrence of the private form, which is the promotion
#                                           this task performs)
#   Both clauses are therefore ARMED, not pre-satisfied.
#
# NO COMMITTED SAMPLE PAIR, deliberately, and this is the reason rather than an omission. This
#          guardrail has TWO subjects, and `SampleVerifier` binds exactly ONE path per half (as
#          $args[0] and as GR_SUBJECT). A committed pair would leave the second subject pointing at
#          the real, still-unstubbed tree, so BOTH halves would exit 1 and the pre-DAG sample
#          preflight would halt the run. The override below therefore honours the two paths ONLY when
#          BOTH are supplied - a single-argument invocation is ignored outright rather than
#          half-applied. Do not add a samples/ folder to this task.
#
# Author-time smoke test (#302), re-runnable (#468) - no environment variable involved, so nothing
# persists in the operator's shell (the #442 hermeticity trap):
#   ./01-stubs-are-real.ps1 <stubbed-RunEventStream.cs> <stubbed-GuardrailFailureReason.cs>   # expect 0
#   ./01-stubs-are-real.ps1                                                                   # expect 1 on the untouched tree
$ErrorActionPreference = 'Continue'

# BOTH-OR-NEITHER (see the sample-pair note above): a lone argument never redirects one subject while
# the other silently keeps its default.
if ($args.Count -ge 2 -and $args[0] -and $args[1]) {
    $streamFile = $args[0]
    $reasonFile = $args[1]
}
else {
    $streamFile = "src/Guardrails.Core/Execution/RunEventStream.cs"
    $reasonFile = "src/Guardrails.Core/Execution/GuardrailFailureReason.cs"
}

# ACCUMULATE (#478): one distinguishable message per clause, dumped once at the end, so a single
# attempt learns about BOTH missing stubs rather than rediscovering the second one on the next retry.
$failures = @()

# Strips comments and string literals before matching, for the #521 reason: a MENTION is not a
# declaration. Without this, the prompt's own code fence pasted into a `//` comment - or the type name
# inside a diagnostic string - satisfies a clause over source that declares nothing.
function Get-CodeOnly {
    param([string]$Path)
    $raw  = Get-Content -LiteralPath $Path -Raw
    $code = [regex]::Replace($raw,  '/\*[\s\S]*?\*/', '')        # /* */ block comments
    $code = [regex]::Replace($code, '(?m)//.*$', '')             # // line comments (XML /// doc too)
    $code = [regex]::Replace($code, '"""[\s\S]*?"""', '""')      # C# 11 raw strings
    $code = [regex]::Replace($code, '@"(?:[^"]|"")*"', '""')     # verbatim strings
    return [regex]::Replace($code, '"(\\.|[^"\\])*"', '""')      # ordinary strings
}

# PRECONDITIONS - the only early exits: the clauses below would crash on a missing subject, and
# "the file is not there" is a different report from "the stub is not in it".
foreach ($f in @($streamFile, $reasonFile)) {
    if (-not (Test-Path -LiteralPath $f)) {
        Write-Output "PRECONDITION: $f does not exist - a file this task is scoped to edit is not where this check looks. Do NOT create it; this is a guardrail/tree mismatch, not work for this task."
        exit 1
    }
}

$streamCode = Get-CodeOnly -Path $streamFile
$reasonCode = Get-CodeOnly -Path $reasonFile

# Clause 1 - the delivery record EXISTS. Anchored on the `record struct` declaration keywords rather
# than on the bare type name, so a `using`, a doc reference or a parameter of some other type named in
# passing cannot satisfy it. The leading accessibility and `readonly` are deliberately NOT pinned:
# the prompt dictates `public readonly record struct`, but the outcome under test is that the type is
# declared here, and a check that false-reds a correct-but-differently-spelled declaration is the
# shape that dead-ends a task it cannot fix (#193).
if ($streamCode -cnotmatch 'record\s+struct\s+EventDelivery\b') {
    $failures += "$streamFile does not DECLARE the EventDelivery record. This is one of task 02's three deliverables, and nothing else in this plan asserts it: the test project can compile without ever naming the type, so 02-tests-build.ps1 going green proves nothing about it. Add it at file level in namespace Guardrails.Core.Execution beside the RunEventStream class, exactly as 'The stubs' item 1 in the prompt spells it, and carry its <summary> from design section 3.1. A comment or a string mentioning the name does NOT satisfy this - both are stripped before matching."
}

# Clause 2 - the cap constant is VISIBLE to the test assembly. The private->internal promotion is the
# whole of item 3 in the prompt; `Guardrails.Core.csproj` already carries the matching
# InternalsVisibleTo, so the keyword is the only thing standing between the test and the constant.
if ($reasonCode -cnotmatch 'internal\s+const\s+int\s+MaxChars\b') {
    $failures += "$reasonFile does not declare MaxChars as 'internal const int'. Task 02 promotes it from 'private const int MaxChars = 2000;' to 'internal const int MaxChars = 2000;' and changes nothing else in that file - not MaxTailLines, not Tail, not the class doc. Guardrails.Core.csproj already carries <InternalsVisibleTo Include=`"Guardrails.Core.Tests`" />, so this keyword is the only thing keeping the constant out of the test assembly. Do NOT duplicate the value in the test; the promotion is the deliverable."
}

if ($failures.Count -gt 0) {
    Write-Output "=== $($failures.Count) of task 02's stub deliverable(s) are missing ==="
    $failures | ForEach-Object { Write-Output $_ }
    Write-Output ""
    Write-Output "These are SOURCE-SHAPE clauses over two files this task's writeScope already covers. They are checked here, cheaply and by name, because the alternative is a compile failure four tasks downstream in task 06 - reported against a task that did not cause it."
    exit 1
}
exit 0
