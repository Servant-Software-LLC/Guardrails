# catches: shipping the post-delivery refresh WITHOUT the control that makes it defensible.
#          Design §1c calls this "the only place in this design where the harness must grow a new
#          distinction": a positive baseline re-evaluates, a negative one keeps skip-once. Today
#          Scheduler.RunWaveEntryGateAsync documents "Skip-once: a passed entry marker for this
#          wave is not re-evaluated on resume" for BOTH kinds, so after a refresh admits the
#          user's commits into the tree, the next wave would pass over a tree it never checked —
#          "silently", in the design's own word.
#
#          There is no stub file, and the reason is the one tasks 14 and 16 give: the production
#          type already exists. RunWaveEntryGateAsync is there; what is missing is the
#          DISTINCTION, not a type. Grep Scheduler.cs for RunWaveEntryGateAsync rather than
#          trusting a line number.
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

$env:DOTNET_CLI_UI_LANGUAGE = 'en'

$pinned = @(
    'APositiveBaselineEntryCheck_ReEvaluatesAfterADelivery',
    'ANegativeBaselineEntryCheck_KeepsSkipOnce',
    'AnEntryGateAfterARefresh_RunsAgainstTheRefreshedTree'
)

$results = Join-Path $env:TEMP ("gr39-census-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $results -Force | Out-Null

try {
    # Class-scoped, never the bare plan-wide trait (#455).
    & dotnet test "tests/Guardrails.Core.Tests/Guardrails.Core.Tests.csproj" -c Debug --nologo `
        --filter "FullyQualifiedName~WaveEntryBaselineKindTests" `
        --logger "trx;LogFileName=census.trx" --results-directory $results 2>&1 | Out-String | Write-Output

    $trx = Get-ChildItem -Path $results -Filter '*.trx' -File | Select-Object -First 1
    if (-not $trx) {
        # PRECONDITION, not an unbound-behaviour report: "the run did not happen" is a different
        # fact from "the tests did not behave as required".
        Write-Output "PRECONDITION: no TRX was produced — the test run did not happen (a build break, or a bad filter). Fix that first; this is NOT a statement about the pinned behaviours."
        exit 1
    }

    [xml]$doc = Get-Content -Raw -LiteralPath $trx.FullName
    $results_nodes = @($doc.TestRun.Results.UnitTestResult | Where-Object { $_ })

    if ($results_nodes.Count -lt 1) {
        # The zero-match hole (#455/#248): with nothing executed the TRX carries no <Results>
        # element, so the dotted navigation yields $null and @($null).Count is 1 — an unfiltered
        # .Count check would evaluate 1 -lt 1 and never fire. Hence the Where-Object above.
        Write-Output "PRECONDITION: the filter 'FullyQualifiedName~WaveEntryBaselineKindTests' matched NO tests. A zero-match filter exits 0 and certifies nothing — fix the filter or the class name."
        exit 1
    }

    $failures = @()
    foreach ($name in $pinned) {
        $node = $results_nodes | Where-Object { $_.testName -like ("*" + $name + "*") } | Select-Object -First 1
        if (-not $node) {
            $failures += "[$name] NOT FOUND in the TRX — the prompt pins this behaviour to a test of that name; it was never executed."
        }
        elseif ($node.outcome -ne 'Failed') {
            $failures += "[$name] outcome was '$($node.outcome)', expected 'Failed'."
        }
    }

    if ($failures.Count -gt 0) {
        Write-Output ""
        Write-Output "=== Census FAILED ($($failures.Count) unbound behaviour(s)) ==="
        $failures | ForEach-Object { Write-Output $_ }
        exit 1
    }

    Write-Output "Census: all $($pinned.Count) pinned behaviour(s) observed Failed."
    exit 0
}
finally {
    Remove-Item -Recurse -Force -LiteralPath $results -ErrorAction SilentlyContinue
}