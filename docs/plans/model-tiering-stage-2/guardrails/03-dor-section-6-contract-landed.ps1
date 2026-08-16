# catches: THE Stage 1 failure - a 9/9 GREEN run that shipped materially different code than the
#          design of record, because every guardrail verified what the TASKS specified and nothing
#          ever compared shipped code against the DoR.
#
# ==> READ THIS BEFORE EDITING. This file has been through two adversarial review passes and its
#     SHAPE is the result. Two earlier designs failed, and the reasons ARE the design rationale:
#
#     v1 could only fail for something PRESENT AND WRONG. Every check was nested inside "if the
#     resolver file exists", so an ABSENT deliverable passed silently - which is the entire Stage 1
#     failure mode. It also deferred its own extension to a prose "maintenance contract", i.e. to the
#     discipline of the agent whose work it grades.
#
#     v2 fixed absence with a 14-clause grep manifest - and was MEASURED to certify DECLARATIONS
#     rather than BEHAVIOUR: against a tree holding only wave 1's TierResolution record and NO
#     attempt-launch wiring at all, 10 of 14 clauses went green. A `bool NoRoute` property satisfied
#     "the no-route outcome exists"; a `string TierSource` property satisfied "tierSource provenance
#     is recorded". A grep cannot distinguish "a property with this name exists" from "this value is
#     written to per-attempt provenance", and no amount of tightening changes that - it is a category
#     limit, not a bug. That is the Stage 1 shape reproduced INSIDE the gate built to prevent it.
#
#     v3 (this file) splits the question by what each tool can actually prove:
#       PART 1 - STRUCTURAL facts, by grep. Shape claims about source text: the shared predicate is
#                not re-implemented; no boolean bypass parameter exists. Greps prove these soundly.
#       PART 2 - BEHAVIOURAL facts, by a REAL-SEAM CONTRACT TEST (#382). Everything of the form "X
#                actually happens at runtime" - resolution runs per attempt, tierSource reaches the
#                journal, no-route settles needs-human, the D28 ceiling warning fires, the judge
#                resolves through the same resolver, Invariant 7 holds. A test drives the real path;
#                a grep only sees that a name exists.
#     The contract test is a DELIVERABLE of the waves that make those behaviours real (see the wave
#     briefs). Its ABSENCE fails this gate - that is what keeps "fails for something absent" true.
#
# LOCAL - no scope key (#165): terminal postconditions, false at any partial union by construction.
$ErrorActionPreference = 'Stop'
$failures = @()

function Read-Code([string]$path) {
    if (-not (Test-Path $path)) { return $null }
    $raw    = Get-Content -Raw -Path $path
    $noRaw  = [regex]::Replace($raw, '"""[\s\S]*?"""', '""')      # C# 11 raw string literals
    $noVerb = [regex]::Replace($noRaw, '@"(?:[^"]|"")*"', '""')
    $noStr  = [regex]::Replace($noVerb, '"(\\.|[^"\\])*"', '""')
    $code   = [regex]::Replace($noStr, '/\*[\s\S]*?\*/', '')
    return [regex]::Replace($code, '(?m)//.*$', '')
}

# =================================================================================================
# PART 1 - STRUCTURAL. Only claims about the SHAPE of source text. Scoped to the resolver's own
# files, and case-SENSITIVE (-cmatch): these are C# identifiers, and C# is case-sensitive while
# PowerShell's -match is not - a mismatch that once let an unrelated `judgeTier` local in Stage 1.5's
# PlanValidator certify an entire wave as landed.
# =================================================================================================
$resolverFiles = @(
    "src/Guardrails.Core/Prompts/TierResolver.cs",
    "src/Guardrails.Core/Prompts/TierResolution.cs"
)
foreach ($f in $resolverFiles) {
    $code = Read-Code $f
    if ($null -eq $code) {
        $failures += "[6.1/6.2 structure] $f does not exist - the static resolver (#226) has not landed"
        continue
    }

    # D22a: the candidacy predicate must not be RE-IMPLEMENTED. Keyed on the membership TEST, which
    # is the unambiguous half of the predicate's body. Deliberately NOT keyed on `Costly` or on
    # `DeclaresTier`: the D28 binding-ceiling datum is DEFINITIONALLY `DeclaresTier AND NOT
    # ServesTier` - the harness computes exactly that in GR2048 as
    # `r.Costly is true && atOrAbove.Any(r.DeclaresTier)` - so banning those two made a REQUIRED
    # deliverable uncomputable and pushed implementers toward obfuscated spellings that shipped the
    # very duplication the ban existed to prevent. Whether the floor actually HOLDS is behavioural
    # and is proven in PART 2.
    if ($code -cmatch '\.Tiers\s*\.\s*(?:Contains|Any|Exists|IndexOf)\s*\(') {
        $failures += "[6.2/D22a] $f tests routing.Tiers membership directly - that IS the candidacy predicate. Call ServesTier(tier) so validate's GR2048 check and the runtime resolver can never disagree about which blocks serve a rung"
    }

    # D22: the costly floor admits NO override. A boolean parameter on a method in the resolver's own
    # files is an opt-in switch by construction, whatever it is named - this catches `permitReserved`
    # and every other invented identifier a denylist would miss. NOTE: no `= true|false` default is
    # required; an earlier version demanded one and a plain `bool allowCostly` walked through.
    if ($code -cmatch '(?m)^\s*(?:public|internal|private|protected|static|\s)*[\w<>,\[\]\?]+\s+\w+\s*\([^)]*\bbool\s+\w+\s*[,)]') {
        $failures += "[6.2/D22] $f declares a method with a boolean parameter - the costly floor admits no override, no flag and no dial. A human reaches a costly model by PINNING it per task (6.1 item 1), which never enters this resolver"
    }
}

# Invariant 4: the schema delta lands in the SSOT in the SAME change as its code. `tierSource` is the
# probe token because it is net-new to this stage (the SSOT already carried the string `no-route` in
# 9.6 prose, so a no-route token would have been pre-satisfied).
$ssot = if (Test-Path "docs/plans/02-schemas-and-contracts.md") { Get-Content -Raw "docs/plans/02-schemas-and-contracts.md" } else { "" }
if ($ssot -cnotmatch 'tierSource') {
    $failures += "[12.4/Invariant 4] docs/plans/02-schemas-and-contracts.md does not mention tierSource - every 12.x schema delta must land in the SSOT in the same change as its code, or a claim about the schema lives outside the schema and decays without anything noticing"
}

# =================================================================================================
# PART 2 - BEHAVIOURAL. A real-seam contract test (#382) drives the ACTUAL path. This is the half a
# grep cannot do, and its ABSENCE is a failure - which is what makes this gate fail for a deliverable
# that was never built (a wave that never ran, or ran and wired nothing).
# =================================================================================================
$env:DOTNET_CLI_UI_LANGUAGE = 'en'    # the run summary the guard below reads is LOCALIZED
$filter = 'FullyQualifiedName~Stage2ConformanceTests'
$out = dotnet test tests/Guardrails.Integration.Tests --filter $filter --nologo 2>&1
$testExit = $LASTEXITCODE                                   # capture BEFORE any other statement
$out | ForEach-Object { Write-Output $_ }

# EXIT CODE FIRST, guard second: a test host that never ran exits NON-zero with no summary, so
# checking the exit code first reports its real error instead of blaming the filter.
if ($testExit -ne 0) {
    $detail = $out |
        Select-String -Pattern '\[FAIL\]|Error Message:|Assert\.|Exception|Stack Trace:|Expected:|Actual:' |
        ForEach-Object { $_.Line } |
        Select-Object -First 40
    Write-Output ""
    Write-Output "=== Failure details (re-emitted so they land in the harness feedback tail) ==="
    if ($detail) { $detail | ForEach-Object { Write-Output $_ } }
    else { Write-Output "(no assertion/exception lines matched - inspect the full log above)" }
    $failures += "[6 behavioural] the Stage 2 real-seam conformance tests FAIL - shipped behaviour does not match the DoR (see the re-emitted failure details above)"
}
else {
    # ZERO-MATCH GUARD, and here it carries the whole absence property: if no wave ever authored the
    # conformance tests, this filter matches nothing and `dotnet test` exits 0. Keyed on the EXECUTED
    # count (Passed+Failed; `Total:` would also count [Skip]ped tests), never on the verbosity-
    # dependent "No test matches" string.
    $ran = ([regex]::Matches(($out | Out-String), '(?:Passed|Failed):\s*(\d+)') |
            ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum
    if ($ran -lt 6) {
        $failures += "[6 behavioural] only $ran Stage2ConformanceTests executed (need at least 6). The real-seam contract test is the ONLY thing here that can prove BEHAVIOUR rather than the presence of a name, so an absent or thin suite means this gate certified nothing. It must drive the real attempt-launch path and cover, at minimum: resolution runs per ATTEMPT (not once per task); the resolved route reaches per-attempt provenance INCLUDING tierSource; a rung with no non-costly candidate settles no-route/needs-human and never selects the costly block; a binding costly ceiling emits the D28 warning on re-attempt; the judge resolves through the SAME resolver with the strength bump (6.5); and Invariant 7 - a routing-ENABLED config with a zero-tag plan resolves via the legacy path with zero tier-resolution activity"
    }
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== DoR section 6 conformance: $($failures.Count) finding(s) ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    Write-Output ""
    Write-Output "shipped code does not match the design of record (docs/plans/17-model-tiering.md section 6). The tasks may all be green - that is exactly the Stage 1 failure this gate exists to catch: a green run proves the TASKS were satisfied, not that the DESIGN was implemented."
    exit 1
}
Write-Output "DoR section 6 conformance: structural checks clean, and the real-seam contract tests pass."
exit 0
