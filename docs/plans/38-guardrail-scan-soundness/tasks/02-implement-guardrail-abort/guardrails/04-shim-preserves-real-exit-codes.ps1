# catches: a shim that detects the abort but silently breaks a REAL guardrail finding - `exit 1` arriving
#          at the harness as exit 0. Design 38 SS3.3 measured two plausible shim shapes that do exactly
#          that, and an independent review confirmed one of them passes FIVE of task 01's six pinned
#          tests. The sixth (ExitOneGuardrail_StillFails) is the only thing standing in its way, and a
#          test file is the one artifact a retrying agent may edit - a hollow `Assert.True(true)` body
#          satisfies the census in BOTH directions, because that row is declared-exempt and expected to
#          PASS, so no tree distinguishes a real body from an empty one.
#          This guardrail needs no test file at all: it drives the shim directly and asserts the three
#          exit codes. Two-sided by construction - a shim that gets any of them wrong fails here.
# Measured baseline (#478): the shim does not exist on the starting tree, so the PRECONDITION below
#          fires and this guardrail is RED before task 02 runs.
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false   # it runs `& pwsh` and EXPECTS non-zero; a non-zero native exit is DATA here

$shim = 'src/Guardrails.Core/Execution/guardrail-shim.ps1'
if (-not (Test-Path -LiteralPath $shim -PathType Leaf)) {
    Write-Output "PRECONDITION: $shim does not exist. Task 02's prompt PINS that path because this guardrail drives it directly; if the shim lives elsewhere, reconcile the two rather than moving this check."
    exit 1
}

$problems = New-Object System.Collections.Generic.List[string]
$tmp = Join-Path ([System.IO.Path]::GetTempPath()) ("gr38-shim-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force $tmp | Out-Null
try {
    # Each case is a REAL guardrail shape, run through the REAL shim.
    $cases = @(
        @{ Name = 'a real finding';        Expect = 1;  Body = "Write-Output 'a real finding'`nexit 1" },
        @{ Name = 'a clean pass';          Expect = 0;  Body = "Write-Output 'all good'`nexit 0" },
        @{ Name = 'stderr noise, exit 0';  Expect = 0;  Body = "[Console]::Error.WriteLine('Switched to branch main')`nWrite-Output 'checked'`nexit 0" },
        @{ Name = 'exit 1 inside try';     Expect = 1;  Body = "try { Write-Output 'found it'`nexit 1 } finally { }" },
        @{ Name = 'an aborted guardrail';  Expect = 97; Body = "`$ErrorActionPreference = 'Continue'`ntry { Write-Output 'starting'`n`$problems.Add('boom')`nWrite-Output 'CENSUS RAN'`nexit 0 } finally { }" }
    )

    foreach ($c in $cases) {
        $script = Join-Path $tmp ((New-Guid).ToString('N') + '.ps1')
        Set-Content -LiteralPath $script -Value $c.Body -Encoding UTF8
        & pwsh -NoProfile -ExecutionPolicy Bypass -File $shim $script *> $null
        $actual = $LASTEXITCODE
        if ($actual -ne $c.Expect) {
            $problems.Add("[$($c.Name)] the shim returned exit $actual, expected $($c.Expect). " + $(
                if ($c.Expect -eq 1) { "A guardrail's real FINDING is reaching the harness as something else - if it became 0 this is the exact regression design 38 SS3.3 measured, and it is worse than the defect being fixed." }
                elseif ($c.Expect -eq 0) { "A correct guardrail is being reported as a failure - a false RED on every run." }
                else { "An ABORTED guardrail is not being distinguished, so it still reaches the harness as a verdict it never reached (#608)." }))
        }
    }

    # The abort exit code is a DATUM with a downstream consumer: task 08's guardrail requires the literal
    # `exit 97` in the SSOT. Nothing gated its CARRIER, so task 02 could ship `exit 90` and make task 08's
    # clause honestly unsatisfiable - a #474 severed hop. It is pinned HERE, not in guardrail 03, because
    # 03 carries a sample pair and must stay a pure function of its subject; this guardrail has no pair,
    # and it has just PROVEN behaviourally that the shim emits 97, so the two facts sit together.
    $runner = 'src/Guardrails.Core/Execution/GuardrailRunner.cs'
    if (-not (Test-Path -LiteralPath $runner -PathType Leaf)) {
        $problems.Add("[$runner] does not exist - the abort verdict has no home.")
    }
    elseif ((Get-Content -Raw -LiteralPath $runner) -notmatch '(?<![0-9#])97(?![0-9])') {
        $problems.Add("[$runner] never mentions the abort exit code 97. The shim emits it and the SSOT (task 08) is required to document `exit 97`; if this task chose a different number, all three must change together, not two of them.")
    }

    if ($problems.Count -gt 0) {
        Write-Output "=== The shim does not preserve guardrail exit codes ($($problems.Count) of $($cases.Count) case(s)) ==="
        $problems | ForEach-Object { Write-Output $_ }
        exit 1
    }
    Write-Output "The shim preserves all $($cases.Count) exit codes: a real finding stays 1, a clean pass stays 0, stderr noise is ignored, and an abort becomes 97."
    exit 0
}
finally {
    Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
}
