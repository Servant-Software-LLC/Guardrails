# catches: the NUMBERING half of this task shipping unverified. The prompt asks for three things —
#          the tests, the GR2078/GR2079 CONSTANTS, and advancing the next-free marker — and before
#          this file existed only the first was checked. The cheapest passing implementation wrote
#          the tests against the string literals "GR2078"/"GR2079" and never opened
#          DiagnosticCodes.cs: the marker would still read GR2078, and the NEXT plan would re-take
#          it. That is the #320 collision, silently, in a later plan — and the prompt itself says
#          this "has gone wrong three times recently".
#
#          Ported from plan 31's 04-codes-and-marker.ps1, which shipped exactly this check.
#
#          Rung 3 (source shape) DELIBERATELY, and the #468 demotion question answered: this is a
#          structural fact about a hand-maintained registry comment, not a runtime behaviour. No
#          test can assert "the marker names the next FREE code" without re-implementing the
#          registry's own bookkeeping, which is what the marker exists to record.
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

# The pre-DAG sample gate hands the subject as argv[0] and as GR_SUBJECT (#559); honour it, or the
# valid and invalid halves both scan the untouched repo and the gate reports ValidHalfFailed.
$subject = if ($args.Count -ge 1 -and $args[0]) { $args[0] }
           elseif ($env:GR_SUBJECT) { $env:GR_SUBJECT }
           else { 'src/Guardrails.Core/Loading/DiagnosticCodes.cs' }

if (-not (Test-Path -LiteralPath $subject)) {
    Write-Output "PRECONDITION: $subject does not exist."
    exit 1
}

$raw = Get-Content -Raw -LiteralPath $subject
$failures = @()

# 1. Both new codes declared as CONSTANTS, not merely mentioned. Anchored on the assignment, so a
#    doc-comment mention of GR2078 does not satisfy it.
foreach ($code in 'GR2078', 'GR2079') {
    if ($raw -notmatch "=\s*`"$code`"") {
        $failures += "MISSING constant for $code in $subject — the code is not DECLARED (looked for = `"$code`"). A test written against the string literal does not reserve the number."
    }
}

# 2. GR2077 stays RESERVED BY NAME (issue #587 check B) and must NOT be allocated here. The
#    catalogue tests cannot catch this: GR2077 is reserved in a COMMENT, not a constant, so
#    NoTwoConstantsShareACode never sees it, and taking 2077 AND 2078 still leaves the high-water
#    mark at 2078 so TheNextFreeMarkerNamesACodeThatIsActuallyFree still passes.
if ($raw -match '=\s*"GR2077"') {
    $failures += "GR2077 has been ALLOCATED in $subject. It is RESERVED BY NAME for issue #587 check B (UnownedRequiredChange) and must not be taken."
}

# 3. Exactly ONE live next-free marker, and it names GR2080.
#    The regex requires the comment to START with the phrase: this file also contains a PROSE
#    mention, `// "CURRENT next-free code: GR2047". The design-of-record reserved …`, which is
#    quoted and must not count. Anchoring on `//\s*CURRENT` excludes it (measured).
$markers = [regex]::Matches($raw, '(?m)^\s*//\s*CURRENT next-free code:\s*(GR\d+)')
if ($markers.Count -ne 1) {
    $failures += "Expected EXACTLY ONE live next-free marker in $subject; found $($markers.Count). The registry's bookkeeping depends on there being one."
}
elseif ($markers[0].Groups[1].Value -ne 'GR2080') {
    $failures += "The next-free marker reads '$($markers[0].Groups[1].Value)' but this plan takes GR2078 and GR2079, so it must now read GR2080. Leaving it behind is how the next plan re-takes a live code."
}

if ($failures.Count -gt 0) {
    Write-Output "=== $($failures.Count) diagnostic-registry problem(s) in $subject ==="
    $failures | ForEach-Object { Write-Output $_ }
    exit 1
}
exit 0
