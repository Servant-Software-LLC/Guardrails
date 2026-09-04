# catches: an AI-merge that silently damages the union - a conflict-marker block left in a merged file,
#          a contribution that arrives as a COMMENT with the real construct dropped, or the #175
#          duplicate-definition case where two branches each append the SAME new type to different
#          regions of one file and the 3-way merge keeps BOTH with no textual conflict (CS0101, which
#          only the build catches and only at the very end).
# scope:"integration" (see the .json sidecar): this is the per-union re-verify, so it runs at EVERY
#          fan-in, on partial merges where downstream tasks have NOT run yet. Every content check is
#          therefore CONDITIONAL - "IF contribution X is present, verify it is real" - and passes
#          trivially before the contributing task has landed (#125/#165). A REQUIRE-present clause here
#          would red-halt a correct run at its first union.
# GR2028 credit rests on the conflict-marker scan below, not on the contribution checks: the
#          union-safe conditional form can never FAIL when a merge DROPPED a contribution entirely (the
#          gate goes false -> pass), so a content grep certifies nothing about union soundness on its
#          own (#343). The contribution checks are the additive tightening layered on top.
# Marker regex is LINE-ANCHORED (^<<<<<<< / ^>>>>>>>) and carries NO bare '=======' clause: half the
#          files scanned here are markdown, where a '====' banner or a setext underline would make an
#          unanchored check red-halt a correct run (#187).
# Required-present baseline (#478): every clause below is inside a presence gate, so all are
#          vacuously green at plan start - EXPECTED and correct for a union-safe conditional (the named
#          exemption: "the 'if X is present' half of a union-safe conditional").
$ErrorActionPreference = 'Continue'

$files = @(
    'src/Guardrails.Core/Execution/RunEventStream.cs',
    'src/Guardrails.Core/Execution/GuardrailFailureReason.cs',
    'src/Guardrails.Core/Execution/WebhookEventSink.cs',
    'src/Guardrails.Cli/Commands/RunCommand.cs',
    'docs/plans/02-schemas-and-contracts.md',
    'docs/plans/585-layer3-webhooks-contract.md',
    '.claude/skills/guardrails-domain-knowledge/SKILL.md',
    'tests/Guardrails.Core.Tests/RunEvents/RunEventBracketTests.cs',
    'tests/Guardrails.Core.Tests/Webhooks/WebhookPolicyTests.cs',
    'tests/Guardrails.Core.Tests/Webhooks/WebhookEventSinkTests.cs',
    'tests/Guardrails.Integration.Tests/RunEvents/WebhookDeliveryTests.cs'
)

$failures = New-Object System.Collections.Generic.List[string]

# ---- 1. Union soundness: every file PRESENT in the union is intact (this is the GR2028 content) ----
foreach ($rel in $files) {
    if (-not (Test-Path -LiteralPath $rel -PathType Leaf)) { continue }   # not contributed yet - fine
    $raw = Get-Content -LiteralPath $rel -Raw
    if ($null -eq $raw -or $raw.Trim().Length -eq 0) {
        $failures.Add("[$rel] is present but EMPTY - the union kept the path and lost the content.")
        continue
    }
    if ($raw -match '(?m)^<<<<<<<' -or $raw -match '(?m)^>>>>>>>') {
        $failures.Add("[$rel] contains git conflict markers - the union did not cleanly integrate.")
    }
}

# ---- 2. Contribution-present tightening (additive; each gated on its own arrival) ----
function Get-CodeOnly([string]$text) {
    # Strip block and line comments so a contribution that arrived only as a COMMENT cannot satisfy a
    # structural clause (#97/#98). String literals are left alone: no clause below matches inside one.
    $t = [regex]::Replace($text, '(?s)/\*.*?\*/', '')
    return [regex]::Replace($t, '(?m)//.*$', '')
}

$res = 'src/Guardrails.Core/Execution/RunEventStream.cs'
if (Test-Path -LiteralPath $res -PathType Leaf) {
    # Gate on the RAW text, assert on the STRIPPED text. Gating on stripped source would make a
    # contribution that arrived only as a COMMENT turn its own gate false and pass - the exact case this
    # header claims to catch, and how the first draft of this script failed its own smoke test.
    $raw  = Get-Content -LiteralPath $res -Raw
    $code = Get-CodeOnly $raw
    if ($raw -match 'EventDelivery') {
        $decl = [regex]::Matches($code, 'record\s+struct\s+EventDelivery\b')
        if ($decl.Count -lt 1) {
            $failures.Add("[$res] mentions EventDelivery but declares no 'record struct EventDelivery' - the type arrived as prose and the real declaration was dropped.")
        } elseif ($decl.Count -gt 1) {
            $failures.Add("[$res] declares 'record struct EventDelivery' $($decl.Count) times - an AI-merge kept two copies of the same new type in different regions (CS0101, #175).")
        }
    }
}

$sink = 'src/Guardrails.Core/Execution/WebhookEventSink.cs'
if (Test-Path -LiteralPath $sink -PathType Leaf) {
    $code = Get-CodeOnly (Get-Content -LiteralPath $sink -Raw)
    # No raw/stripped split needed here: the GATE is the file EXISTING, not a token appearing in it.
    $decl = [regex]::Matches($code, '(?:class|record)\s+WebhookEventSink\b')
    if ($decl.Count -lt 1) {
        $failures.Add("[$sink] exists but declares no WebhookEventSink type - the union kept the file and lost its declaration.")
    } elseif ($decl.Count -gt 1) {
        $failures.Add("[$sink] declares WebhookEventSink $($decl.Count) times - the #175 AI-merge duplicate-definition case (CS0101).")
    }
}

$ssot = 'docs/plans/02-schemas-and-contracts.md'
if (Test-Path -LiteralPath $ssot -PathType Leaf) {
    # A doc clause strips HTML comments before matching: an appended '<!-- TODO -->' renders as NOTHING,
    # so without this a commented-out stub would satisfy a required-present check (a false GREEN).
    $doc = Get-Content -LiteralPath $ssot -Raw
    if ($doc -match '(?s)<!--(?!.*?-->)') {
        $failures.Add("[$ssot] has an unterminated '<!--' - refusing to strip to EOF, which would delete the rest of the document over one stray token.")
    } else {
        $doc = [regex]::Replace($doc, '(?s)<!--.*?-->', '')
        if ($doc -match '(?m)^###\s+8\.3\b' -and $doc -notmatch '\(runId, bracket, seq\)') {
            $failures.Add("[$ssot] has a section 8.3 heading but no '(runId, bracket, seq)' delivery key - the section arrived without the contract it exists to state.")
        }
    }
}

if ($failures.Count -gt 0) {
    Write-Output "=== Union integrity ($($failures.Count) problem(s)) ==="
    $failures | ForEach-Object { Write-Output $_ }
    exit 1
}
Write-Output "Union intact: every contributed file present is non-empty, conflict-marker-free, and carries a real declaration."
exit 0
